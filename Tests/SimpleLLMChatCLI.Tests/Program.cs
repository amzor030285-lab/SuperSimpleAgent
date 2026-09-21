using Newtonsoft.Json.Linq;
using SimpleLLMChatCLI.RAG;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimpleLLMChatCLI.Tests
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            return SuiteMain.Run("SimpleLLMChatCLI", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("TokenEstimator.basic", () =>
            {
                TestAssert.Equal(0, TokenEstimator.ApproximateTokens(0), "zero");
                TestAssert.Equal(3, TokenEstimator.ApproximateTokens(10), "10/3");
                TestAssert.True(TokenEstimator.ShouldSummarize(3000, 1000), "summarize");
                TestAssert.True(!TokenEstimator.ShouldSummarize(100, 1000), "no summarize");
            });

            TestRunner.Run("TokenEstimator.calibration", () =>
            {
                double original = TokenEstimator.TokensPerCharacter;
                try
                {
                    TokenEstimator.Recalibrate(0, 100);
                    TokenEstimator.Recalibrate(-1, 100);
                    TestAssert.True(
                        Math.Abs(TokenEstimator.TokensPerCharacter - (1.0 / 3.0)) < 0.0001,
                        "invalid measurements ignored");

                    TokenEstimator.Recalibrate(300, 600); // measured 0.50 -> blend (1/3+0.5)/2
                    double expected = (1.0 / 3.0 + 0.5) / 2.0;
                    TestAssert.True(
                        Math.Abs(TokenEstimator.TokensPerCharacter - expected) < 0.0001,
                        "measured value blended in");
                    TestAssert.Equal((int)(1000 * expected), TokenEstimator.ApproximateTokens(1000), "approx uses ratio");

                    TokenEstimator.TokensPerCharacter = 1.0;
                    TokenEstimator.Recalibrate(1, 10000000); // measured ~0 -> blend clamps at floor 0.05
                    TestAssert.True(TokenEstimator.TokensPerCharacter >= 0.05, "lower clamp");

                    TokenEstimator.TokensPerCharacter = 0.05;
                    TokenEstimator.Recalibrate(int.MaxValue, 1); // measured huge -> clamps at ceiling 1.0
                    TestAssert.True(TokenEstimator.TokensPerCharacter <= 1.0, "upper clamp");
                }
                finally
                {
                    TokenEstimator.TokensPerCharacter = original;
                }
            });

            TestRunner.Run("ToolApproval.roundtrip", () =>
            {
                string msg = ToolApproval.FormatApprovalMessage("read_file", "{\\\"filename\\\":\\\"a.txt\\\"}");
                string tool, arguments;
                TestAssert.True(ToolApproval.TryParseApprovalPrompt(
                    msg + "\n" + ToolApproval.ApprovalPrompt, out tool, out arguments), "parse");
                TestAssert.Equal("read_file", tool, "tool");
            });

            TestRunner.Run("ToolResultParser.json", () =>
            {
                string text, image, mime;
                ToolResultParser.Parse("{\"text\":\"hi\",\"image\":{\"data\":\"QQ==\",\"mime\":\"image/png\"}}",
                    out text, out image, out mime);
                TestAssert.Equal("hi", text, "text");
                TestAssert.Equal("QQ==", image, "image");
                TestAssert.Equal("image/png", mime, "mime");
            });

            TestRunner.Run("VectorStore.search", () =>
            {
                VectorStore store = new VectorStore();
                store.Add(new ChunkVector
                {
                    File = "a.txt",
                    StartLine = 1,
                    EndLine = 2,
                    Embedding = new float[] { 1f, 0f, 0f }
                });
                store.Add(new ChunkVector
                {
                    File = "b.txt",
                    StartLine = 1,
                    EndLine = 2,
                    Embedding = new float[] { 0f, 1f, 0f }
                });
                List<ChunkHit> hits = store.Search(new float[] { 1f, 0f, 0f }, 1);
                TestAssert.Equal(1, hits.Count, "count");
                TestAssert.Equal("a.txt", hits[0].Chunk.File, "top file");
            });

            TestRunner.Run("Ini.ConfigHandler", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("cli-ini"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(path,
                        "[System]\r\napikey=k\r\nllmserver=http://127.0.0.1\r\nmodel=m\r\n",
                        Encoding.UTF8);
                    ConfigHandler cfg = new ConfigHandler(path);
                    TestAssert.Equal("k", cfg.GetConfigValue("apikey"), "apikey");
                    TestAssert.Equal("m", cfg.GetConfigValue("model"), "model");
                }
            });

            TestRunner.Run("cli.bad_reasoning_effort", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    ProcessResult r = ProcessRunner.Run(
                        Path.Combine(ws.Path, "SimpleLLMChatCLI.exe"),
                        "--reasoning-effort nope",
                        ws.Path, null, 15000);
                    TestAssert.ContainsIgnoreCase(r.Stderr + r.Stdout, "Invalid reasoning effort", "error");
                }
            });

            TestRunner.Run("cli.missing_image_path", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    ProcessResult r = ProcessRunner.Run(
                        Path.Combine(ws.Path, "SimpleLLMChatCLI.exe"),
                        "--image",
                        ws.Path, null, 15000);
                    TestAssert.ContainsIgnoreCase(r.Stderr + r.Stdout, "requires a file path", "error");
                }
            });

            TestRunner.Run("cli.stop_aborts_generation", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Long reply, slow cadence: plenty of streaming left to cut short.
                    server.FixedReply = new string('x', 2400);
                    server.StreamDelayMs = 120;
                    server.StreamChunkChars = 48;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SimpleLLMChatCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("please stream for a while");

                        // Wait until the reply actually starts streaming.
                        CliRunner.WaitForText(cli, "x", 15000);

                        TestAssert.True(cli.SendStop(3000), "STOP delivered through control pipe");

                        CliRunner.WaitForText(cli, "[Generation stopped by user]", 15000);

                        // The CLI mirrors the conversation to "lastsession" between the
                        // notice and the re-prompt, so wait for a fresh prompt after the
                        // notice instead of reading stdout immediately.
                        CliRunner.WaitForTextAfter(cli, "[Generation stopped by user]", "You: ", 15000);

                        // The CLI must have returned to its prompt after the notice.
                        string outText = cli.Stdout;
                        TestAssert.Contains(outText, "[Generation stopped by user]", "notice present");
                        int noticeIndex = outText.IndexOf("[Generation stopped by user]", StringComparison.Ordinal);
                        TestAssert.True(
                            outText.LastIndexOf("You: ", StringComparison.Ordinal) > noticeIndex,
                            "re-prompted after stop");
                        // The stream was cut short — never the full 2400-char reply.
                        TestAssert.True(
                            outText.IndexOf(new string('x', 600), StringComparison.Ordinal) < 0,
                            "reply truncated");
                    }
                }
            });

            TestRunner.Run("cli.image_not_supported_recovery", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    server.RejectImageRequests = true;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    // Minimal 1x1 PNG so /image has a real file to encode.
                    byte[] png = Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcS3AAAADUlEQVR4" +
                        "nGNgYGD4DwABHQAf" +
                        "aKT/3QAAAABJRU5ErkJggg==");
                    File.WriteAllBytes(Path.Combine(ws.Path, "tiny.png"), png);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/image \"tiny.png\" describe this");

                        // The first attempt carries the image and is rejected.
                        CliRunner.WaitForText(
                            cli, "[Model does not support images - image removed and retrying]", 30000);

                        // The retry goes out text-only and succeeds.
                        CliRunner.WaitForTextAfter(
                            cli, "[Model does not support images - image removed and retrying]",
                            "Hello there.", 30000);
                        CliRunner.WaitForTextAfter(cli, "Hello there.", "You: ", 15000);

                        // The conversation is no longer poisoned: a plain text turn works.
                        cli.WriteLine("next question");
                        CliRunner.WaitForTextAfter(cli, "You: ", "Hello there.", 30000);
                    }
                }
            });

            TestRunner.Run("cli.session_save_load", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, "Hello there.", 15000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/save my_session");
                        CliRunner.WaitForText(cli, "Session saved: my_session", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "my_session.json");
                        TestAssert.True(File.Exists(sessionFile), "session file created");
                        string json = File.ReadAllText(sessionFile, Encoding.UTF8);
                        TestAssert.Contains(json, "\"Role\": \"assistant\"", "assistant message");
                        TestAssert.Contains(json, "Hello there.", "content preserved");
                        TestAssert.Contains(json, "\"Version\": 1", "envelope version");

                        cli.WriteLine("/sessions");
                        CliRunner.WaitForText(cli, "  my_session", 15000);
                        TestAssert.Contains(cli.Stdout, "messages", "count printed");

                        cli.WriteLine("/load my_session");
                        CliRunner.WaitForText(cli, "Session loaded: my_session", 15000);
                    }
                }
            });

            TestRunner.Run("cli.session_auto_name_and_list", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    WriteCliIni(ws.Path, "http://127.0.0.1:1");
                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/save");
                        CliRunner.WaitForText(cli, "Session saved: session_", 15000);

                        cli.WriteLine("/sessions");
                        CliRunner.WaitForText(cli, "  session_", 15000);
                        TestAssert.Contains(cli.Stdout, "messages", "count printed");

                        // No-keyword load picks the most recent session.
                        cli.WriteLine("/load");
                        CliRunner.WaitForText(cli, "Session loaded: session_", 15000);
                    }
                }
            });

            TestRunner.Run("cli.context_high_auto_truncate", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // ~2 KB markdown per turn keeps the test offline and deterministic.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 850);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("first message");
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("second message");
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // The third turn starts far beyond 95%: the CLI must
                        // auto-truncate (no prompt, no stdin) and continue.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        CliRunner.WaitForText(cli, "# Reply turn 3", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);
                    }
                }
            });

            TestRunner.Run("cli.shell_output_capped", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A file much larger than the 20k process-output ceiling: the
                    // shell tool must cap the captured output (head + tail + marker)
                    // and offload the full output to a file instead of dumping
                    // ~300KB into the conversation.
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 30000; i++)
                        big.Append("0123456789");
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    File.WriteAllText(bigPath, big.ToString());

                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);
                        CliRunner.WaitForText(cli, "Full output saved to", 60000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.tool_cancelled_by_user", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A ~30 second sleep: long enough for the test to send
                    // STOPTOOL while it runs, with no configured timeout, so
                    // only the user can stop it.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(cli.SendStopTool(5000), "STOPTOOL delivered through the control pipe");

                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);
                        CliRunner.WaitForTextAfter(cli, "cancelled by the user", "You: ", 30000);
                    }
                }
            });

            TestRunner.Run("cli.tool_timeout", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // tooltimeout.run_shell_command=2 must kill the ~30s sleep
                    // after two seconds and report the timeout to the model.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command",
                        toolTimeout: "run_shell_command=2");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "timed out after 2 second(s)", 60000);
                        CliRunner.WaitForTextAfter(cli, "timed out after 2 second(s)", "You: ", 30000);
                    }
                }
            });

            TestRunner.Run("cli.clear_wipes_lastsession", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string lastsessionFile = Path.Combine(ws.Path, "sessions", "lastsession.json");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SimpleLLMChatCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // After a completed turn the CLI mirrors the
                        // conversation into lastsession.json; the re-prompt
                        // only appears once that save is done.
                        cli.WriteLine("hello");
                        CliRunner.WaitForTextAfter(cli, server.FixedReply, "You: ", 30000);
                        TestAssert.True(File.Exists(lastsessionFile), "lastsession saved after a turn");

                        // /clear must wipe the in-memory context AND the
                        // on-disk auto-saved session, otherwise the next
                        // startup would restore the conversation the user
                        // just cleared.
                        cli.WriteLine("/clear");
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(15000);
                        while (File.Exists(lastsessionFile) && DateTime.UtcNow < deadline)
                            System.Threading.Thread.Sleep(50);
                        TestAssert.True(!File.Exists(lastsessionFile), "lastsession wiped by /clear");
                        TestAssert.True(!cli.HasExited, "CLI still alive after /clear");
                    }
                }
            });

            TestRunner.Run("cli.restored_oversized_session_capped", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Hand-crafted session (pre-cap era) whose tool message is ~160KB.
                    // With contextWindowSize=0 the percentage ladder never fires, so
                    // the absolute per-message guard must cap it before the request.
                    StringBuilder huge = new StringBuilder();
                    for (int i = 0; i < 20000; i++)
                        huge.Append("abcdefgh");
                    string sessionJson =
                        "{\"Version\":1,\"SavedAtUtc\":\"2026-01-01T00:00:00Z\",\"Messages\":[" +
                        "{\"Role\":\"user\",\"Content\":\"do the thing\",\"ToolCallId\":\"\",\"ToolCalls\":[]}," +
                        "{\"Role\":\"tool\",\"Content\":\"" + huge.ToString() + "\",\"ToolCallId\":\"call_1\",\"ToolCalls\":[]}" +
                        "]}";
                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    Directory.CreateDirectory(sessionsDir);
                    File.WriteAllText(Path.Combine(sessionsDir, "huge.json"), sessionJson, Encoding.UTF8);

                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("/load huge");
                        CliRunner.WaitForText(cli, "Session loaded: huge", 15000);

                        cli.WriteLine("continue");
                        CliRunner.WaitForText(cli, "[Context safety - oversized message(s) capped to the per-message limit]", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.compaction_micro_command", () =>
            {
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release"));
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.ToolCallRoundsRemaining = 3;
                    server.ToolCallName = "read_file";
                    server.StreamDelayMs = 5;
                    var args = new JObject();
                    args["filename"] = Path.Combine(ws.Path, "LLMSettings.ini");
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("read the settings file");
                        // Three tool rounds then a plain reply => three tool groups in context.
                        CliRunner.WaitForText(cli, "[tool output]", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/compact");
                        CliRunner.WaitForText(cli, "Compaction: removed 2 old tool messages.", 20000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.compaction_full_command", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // 16 turns of ~2KB markdown replies (~29k chars) so the
                    // conversation grows well past the recent-tail window
                    // (~24k chars at the default char ratio) even with the
                    // tail's backward-walk overshoot; the older head must then
                    // be summarized (the fake server answers the summary
                    // request too).
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 16; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(cli, "[Conversation compacted]", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.compaction_full_short_noop", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("hi");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // The whole conversation fits in the recent-tail window:
                        // no LLM summary call, no marker, nothing is dropped.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(
                            cli, "[Compaction: conversation fits in the keep window - nothing to summarize]", 20000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.True(
                            !cli.Stdout.Contains("[Conversation compacted]"),
                            "no summary for a conversation that fits in the keep window");
                    }
                }
            });

            TestRunner.Run("cli.token_calibration_usage", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The server reports real-ish usage (0.48 tok/char). With a window of
                    // 1720, before turn 3 the legacy chars/3 heuristic would sit at ~0.70
                    // (below every compaction threshold), while the recalibrated counter
                    // crosses the ladder thresholds - so turn 3 MUST trigger an automatic
                    // compaction pass, and turn 2 must not.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.IncludeUsagePromptTokens = true;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 1720);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("first message");
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // Turn 2 is still below every threshold => plain reply, no compaction.
                        cli.WriteLine("second message");
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.True(
                            !cli.Stdout.Contains("[Context usage high")
                            && !cli.Stdout.Contains("[Conversation summarized"),
                            "no compaction before turn 3");

                        // Calibration accrued over turns 1-2 => turn 3 (before processing) crosses the
                        // ladder and truncates the oldest messages (the chars/3 heuristic
                        // would be at ~0.70 here and do nothing). The reply follows as #3.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        CliRunner.WaitForText(cli, "# Reply turn 3", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.Contains(cli.Stdout, "[Context usage high", "calibrated usage triggered compaction at turn 3");
                    }
                }
            });

            TestRunner.Run("cli.stream_stall_abort_and_save", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The first chat request sends one chunk then freezes. The stall
                    // watchdog must abort the request and return the CLI to its prompt
                    // so the user can still /save.
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    server.StallFirstChatRequestMs = 20000;
                    WriteCliIni(ws.Path, server.BaseUrl, streamStallTimeout: 3, requestTimeout: 60);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("hello stall");
                        CliRunner.WaitForText(cli, "partial ", 15000);

                        // The stream goes silent: the watchdog aborts after ~3 s.
                        CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                        TestAssert.Contains(cli.Stdout, "model stalled", "stall reason printed");
                        CliRunner.WaitForText(cli, "You:", 30000);

                        // The prompt is back: saving must work right after a stall.
                        cli.WriteLine("/save after_stall");
                        CliRunner.WaitForText(cli, "Session saved: after_stall", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "after_stall.json");
                        TestAssert.True(File.Exists(sessionFile), "session file created");
                    }
                }
            });

            TestRunner.Run("cli.summary_fail_truncate_fallback", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A short first turn plus one long message puts the start of
                    // turn 3 at ~92% (29591 chars / 3 / 10670): inside the
                    // silent-summarize band (90-95%) so the free truncate pass
                    // (>=95%) stays quiet and the summarize pass fires with a
                    // substantial head (the first turn). The summarize pass
                    // (request #3) is injected as a 500 so the CLI must fall
                    // back to truncation.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.FailChatRequestAtTurn = 3;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 10670);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        StringBuilder shortFirst = new StringBuilder();
                        for (int i = 0; i < 2000; i++)
                            shortFirst.Append('a');
                        cli.WriteLine(shortFirst.ToString());
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        StringBuilder longSecond = new StringBuilder();
                        for (int i = 0; i < 24000; i++)
                            longSecond.Append('b');
                        cli.WriteLine(longSecond.ToString());
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // Turn 3 starts at ~92%: silent summarize fires, fails (500),
                        // and the CLI falls back to truncation.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - summary failed, truncated", 30000);

                        // The main request still succeeds. It is the server's 4th chat
                        // request (#3 was the failed summary), so it is labeled turn 4.
                        CliRunner.WaitForText(cli, "# Reply turn 4", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        cli.WriteLine("/save after_summary");
                        CliRunner.WaitForText(cli, "Session saved: after_summary", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "after_summary.json");
                        string json = File.ReadAllText(sessionFile, Encoding.UTF8);
                        TestAssert.Contains(json, "Reply turn 4", "latest reply preserved");
                    }
                }
            });
        }

        static TempWorkspace PackageCliWorkspace()
        {
            TempWorkspace ws = new TempWorkspace("cli-run");
            string srcExe = ToolClient.ProductExe("SimpleLLMChatCLI.exe");
            string srcJson = ToolClient.ProductExe("Newtonsoft.Json.dll");
            if (!File.Exists(srcExe))
                throw new TestFailureException("SimpleLLMChatCLI.exe not packaged: " + srcExe);
            File.Copy(srcExe, Path.Combine(ws.Path, "SimpleLLMChatCLI.exe"), true);
            if (File.Exists(srcJson))
                File.Copy(srcJson, Path.Combine(ws.Path, "Newtonsoft.Json.dll"), true);
            return ws;
        }

        static void WriteCliIni(string dir, string llmServer, int contextWindowSize = 0, string tools = "",
            int streamStallTimeout = 120, int requestTimeout = 300, string toolTimeout = "")
        {
            string path = Path.Combine(dir, "LLMSettings.ini");
            File.WriteAllText(path,
                "[System]\r\n" +
                "apikey=test-key\r\n" +
                "llmserver=" + llmServer + "\r\n" +
                "model=test-model\r\n" +
                "sysprompt=\"You are a test assistant.\"\r\n" +
                "contextWindowSize=" + contextWindowSize + "\r\n" +
                "streamstalltimeout=" + streamStallTimeout + "\r\n" +
                "requesttimeout=" + requestTimeout + "\r\n" +
                "[Tools]\r\n" +
                "tools=" + (tools ?? "") + "\r\n" +
                "toolsrequiringapproval=\r\n" +
                (string.IsNullOrEmpty(toolTimeout) ? "" : "tooltimeout." + toolTimeout + "\r\n") +
                "[Appearance]\r\n" +
                "assistantname=LLM\r\n" +
                "markdownparsing=0\r\n" +
                "[RAG]\r\n" +
                "ragenabled=0\r\n",
                Encoding.UTF8);
        }
    }
}
