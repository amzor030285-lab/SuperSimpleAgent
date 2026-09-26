using Newtonsoft.Json.Linq;
using SimpleLLMChatCLI;
using SimpleLLMChatCLI.RAG;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleLLMChatCLI.Tests
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            // The suite EXE doubles as a fake curl.exe for the curl-fallback tests
            // (it is copied to curl.exe next to the CLI under test). The mode env
            // var selects it: the CLI launches curl with its own fixed arguments.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAKE_CURL_MODE")))
                return FakeCurlMain(args);
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

            TestRunner.Run("Ini.approval_default", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("cli-approval"))
                {
                    // Key absent: the tools that can run commands or write files
                    // fall back to requiring approval.
                    string absent = ws.Combine("absent.ini");
                    File.WriteAllText(absent, "[System]\r\napikey=k\r\n", Encoding.UTF8);
                    List<string> defaults = new ConfigHandler(absent).GetConfigList(
                        "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval);
                    TestAssert.Equal(2, defaults.Count, "default count");
                    TestAssert.Equal("run_shell_command", defaults[0], "run_shell_command default");
                    TestAssert.Equal("download_file", defaults[1], "download_file default");

                    // Key present but empty: the user opted out, so no default applies.
                    string optedOut = ws.Combine("opted-out.ini");
                    File.WriteAllText(optedOut, "[System]\r\ntoolsrequiringapproval=\r\n", Encoding.UTF8);
                    TestAssert.Equal(0, new ConfigHandler(optedOut).GetConfigList(
                        "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval).Count,
                        "explicit empty stays empty");
                }
            });

            TestRunner.Run("Ini.secret_protection", () =>
            {
                string protectedKey = SecretProtector.Protect("sk-secret-123");
                TestAssert.True(SecretProtector.IsProtected(protectedKey), "prefixed");
                TestAssert.Equal("sk-secret-123", SecretProtector.Unprotect(protectedKey), "round trip");
                TestAssert.Equal(protectedKey, SecretProtector.Protect(protectedKey), "protect is idempotent");
                TestAssert.Equal("plain-key", SecretProtector.Unprotect("plain-key"), "plaintext passes through");
                TestAssert.Equal("dpapi:not-base64!", SecretProtector.Unprotect("dpapi:not-base64!"),
                    "undecryptable value is kept so the key can be re-entered");
                TestAssert.True(SecretProtector.IsSecretKey("apiKey"), "apikey is a secret");
                TestAssert.True(!SecretProtector.IsSecretKey("sysprompt"), "sysprompt is not a secret");

                using (TempWorkspace ws = new TempWorkspace("cli-secret"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(path, "[System]\r\napikey=" + protectedKey + "\r\n", Encoding.UTF8);
                    TestAssert.Equal("sk-secret-123", new ConfigHandler(path).GetConfigValue("apikey"),
                        "protected config value decrypts");

                    string legacy = ws.Combine("legacy.ini");
                    File.WriteAllText(legacy, "[System]\r\napikey=legacy-plain\r\n", Encoding.UTF8);
                    TestAssert.Equal("legacy-plain", new ConfigHandler(legacy).GetConfigValue("apikey"),
                        "legacy plaintext config still works");
                }
            });

            TestRunner.Run("ToolRegistry.stdin_payload_scoped", () =>
            {
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools"));
                string webToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "WebTools"));
                if (!File.Exists(Path.Combine(fileToolsDir, "FileTools.json"))
                    || !File.Exists(Path.Combine(webToolsDir, "WebTools.json")))
                {
                    TestRunner.Skip("tool manifests not found next to the test output");
                    return;
                }

                using (TempWorkspace ws = new TempWorkspace("cli-toolcfg"))
                {
                    // A config with secrets that must never reach a tool's stdin,
                    // plus declared options that must.
                    string ini = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(ini,
                        "[System]\r\n" +
                        "apikey=super-secret-key\r\n" +
                        "embeddingsapikey=another-secret\r\n" +
                        "firecrawlapikey=fire-secret\r\n" +
                        "llmserver=http://127.0.0.1:9\r\n" +
                        "model=m\r\n" +
                        "sysprompt=\"secret prompt\"\r\n" +
                        "maxFileContentLength=1234\r\n",
                        Encoding.UTF8);

                    ConfigHandler cfg = new ConfigHandler(ini);
                    ToolRegistry registry = new ToolRegistry(cfg);
                    registry.LoadToolsFromDirectory(fileToolsDir);

                    // FileTools declares maxFileContentLength and outputEncoding, and
                    // nothing else: its stdin must carry exactly those (current
                    // value for the first, manifest default for the second).
                    JObject root = JObject.Parse(registry.BuildStdinPayload(
                        Path.Combine(fileToolsDir, "FileTools.exe"), "{}"));
                    JObject cfgObj = root["config"] as JObject;
                    TestAssert.True(cfgObj != null, "config object present");
                    TestAssert.True(!HasConfigKey(cfgObj, "apikey"), "apikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "embeddingsapikey"), "embeddingsapikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "firecrawlapikey"), "undeclared firecrawlapikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "llmserver"), "llmserver not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "sysprompt"), "sysprompt not forwarded");
                    TestAssert.True(HasConfigKey(cfgObj, "maxFileContentLength"), "declared option forwarded");
                    TestAssert.Equal("1234", cfgObj["maxFileContentLength"].ToString(), "current value wins");
                    TestAssert.Equal("auto", cfgObj["outputEncoding"].ToString(), "manifest default when unset");

                    // WebTools declares FirecrawlApiKey: a secret it declares must
                    // still reach its own package, while apikey must not.
                    registry.LoadToolsFromDirectory(webToolsDir);
                    JObject webRoot = JObject.Parse(registry.BuildStdinPayload(
                        Path.Combine(webToolsDir, "WebTools.exe"), "{}"));
                    JObject webCfg = webRoot["config"] as JObject;
                    TestAssert.True(cfgObj != null && webCfg != null, "web config object present");
                    TestAssert.True(HasConfigKey(webCfg, "firecrawlapikey"), "declared secret reaches its own package");
                    TestAssert.Equal("fire-secret", webCfg["FirecrawlApiKey"].ToString(), "declared secret value");
                    TestAssert.True(!HasConfigKey(webCfg, "apikey"), "llm apikey not forwarded to web tools");
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

            TestRunner.Run("cli.sse_data_no_space", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The space after "data:" is optional per the SSE spec; some
                    // servers/proxies omit it and the parser must accept both.
                    server.FixedReply = "no-space SSE works";
                    server.DataPrefixNoSpace = true;
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, "no-space SSE works", 15000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_ok", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // llmserver is an https:// URL pointed at a plain HTTP server: the
                    // .NET 4.0 client fails the TLS handshake, so the CLI must take
                    // the curl fallback. The bundled "curl.exe" is this test EXE in
                    // --fake-curl mode answering with a short SSE stream.
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SimpleLLMChatCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "ok");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "curl works", 20000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_stall", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The fake curl never produces stream data; the 2 s stall watchdog
                    // must kill it instead of hanging the CLI forever (curl has no
                    // idle timeout of its own for streaming).
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SimpleLLMChatCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl, streamStallTimeout: 2);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "hang");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                            TestAssert.Contains(cli.Stdout, "model stalled", "stall reason printed");
                            CliRunner.WaitForText(cli, "You:", 30000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_connect_timeout", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The fake curl simulates curl(1) honoring --connect-timeout (exit 28)
                    // only when the flag is present, proving the CLI passes it.
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SimpleLLMChatCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl, requestTimeout: 2);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "hang-connect");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                            TestAssert.Contains(cli.Stdout, "cURL failed (exit 28)", "connect timeout surfaced");
                            CliRunner.WaitForText(cli, "You:", 30000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
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

            TestRunner.Run("cli.approval_mode_auto", () =>
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
                    // --approval auto must run a tool call with no prompt at all:
                    // the CLI would otherwise block on stdin waiting for Y/N.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var autoArgs = new JObject();
                    autoArgs["command"] = "echo approval-auto-ok";
                    server.ToolCallArguments = autoArgs.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval auto"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("run it");
                        CliRunner.WaitForText(cli, "approval-auto-ok", 60000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            cli.Stdout.IndexOf(ToolApproval.ApprovalPrompt, StringComparison.Ordinal) < 0,
                            "auto mode shows no approval prompt");
                    }
                }
            });

            TestRunner.Run("cli.approval_mode_ask", () =>
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
                    // --approval ask prompts for every tool call, including a tool
                    // that is not in the (empty) toolsRequiringApproval list, and N
                    // must cancel the call without running the command.
                    string sideEffect = Path.Combine(ws.Path, "ask-mode-must-not-exist.txt");
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var askArgs = new JObject();
                    askArgs["command"] = "echo hi > \"" + sideEffect + "\"";
                    server.ToolCallArguments = askArgs.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval ask"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("run it");
                        CliRunner.WaitForText(cli, ToolApproval.ApprovalPrompt, 60000);
                        cli.WriteLine("N");
                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);

                        TestAssert.True(!File.Exists(sideEffect),
                            "declined tool call never ran the command");
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

            TestRunner.Run("cli.tool_tree_killed_on_timeout", () =>
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
                    // The tool runs `start /b /wait ping ...`, so its child ping
                    // outlives the 5s tool timeout; the job must kill both.
                    // /b keeps the extra process level the test needs but stops
                    // `start` from flashing a console window on the desktop.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "start /b /wait ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command",
                        toolTimeout: "run_shell_command=5");

                    int baselinePings = CountProcesses("ping");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c > baselinePings, 3000),
                            "child ping never appeared while the tool ran; kill assertion is vacuous");

                        CliRunner.WaitForText(cli, "timed out after 5 second(s)", 60000);

                        TestAssert.True(PollProcessCount("ping", c => c == baselinePings, 5000),
                            "child ping survived the tool timeout: the process tree was not killed");
                    }
                }
            });

            TestRunner.Run("cli.tool_tree_killed_on_abort", () =>
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
                    // No per-tool timeout here: the STOPTOOL control-pipe
                    // command must kill the tool and its child ping.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "start /b /wait ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    int baselinePings = CountProcesses("ping");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c > baselinePings, 3000),
                            "child ping never appeared while the tool ran; kill assertion is vacuous");

                        TestAssert.True(cli.SendStopTool(5000), "STOPTOOL delivered through the control pipe");
                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c == baselinePings, 5000),
                            "child ping survived the tool abort: the process tree was not killed");

                        CliRunner.WaitForTextAfter(cli, "cancelled by the user", "You: ", 30000);
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

            TestRunner.Run("cli.lastsession_save_capped", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A pre-cap-era lastsession: 100 messages of ~19000 chars each
                    // (~2 MB), far over the 1 MB cap.
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"Version\":1,\"SavedAtUtc\":\"2026-01-01T00:00:00Z\",\"Messages\":[");
                    for (int i = 0; i < 100; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        string content = i == 0 ? "OLD_MARKER_FIRST_MESSAGE" : new string('a', 19000);
                        sb.Append("{\"Role\":\"user\",\"Content\":\"" + content
                            + "\",\"ToolCallId\":\"\",\"ToolCalls\":[]}");
                    }
                    sb.Append("]}");
                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    Directory.CreateDirectory(sessionsDir);
                    File.WriteAllText(Path.Combine(sessionsDir, "lastsession.json"), sb.ToString(), Encoding.UTF8);

                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    string file = Path.Combine(sessionsDir, "lastsession.json");
                    long size = 0;
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "-- restored lastsession: 100 messages --", 20000);

                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        // The re-prompt after the reply proves the loop finished the
                        // iteration (the lastsession mirror runs at its end).
                        CliRunner.WaitForTextAfter(cli, server.FixedReply, "You: ", 15000);

                        // The auto-save after the turn must have capped the mirror.
                        TestAssert.True(File.Exists(file), "lastsession.json exists after the turn");
                        size = new FileInfo(file).Length;
                        TestAssert.True(size <= SessionStore.MaxLastSessionBytes,
                            "lastsession.json is within the cap: " + size + " <= "
                            + SessionStore.MaxLastSessionBytes + "\nCLI stderr:\n" + cli.Stderr);
                    }

                    List<JObject> messages = null;
                    TestAssert.True(SessionStore.Load(sessionsDir, "lastsession", out messages),
                        "capped lastsession.json still parses");
                    TestAssert.True(messages.Count < 102,
                        "oldest messages were dropped: " + messages.Count + " < 102");
                    TestAssert.True(messages.Count >= 2, "newest messages kept: " + messages.Count);
                    TestAssert.Equal(server.FixedReply, (string)messages[messages.Count - 1]["Content"],
                        "last message is the new reply");
                    TestAssert.Equal("hello", (string)messages[messages.Count - 2]["Content"],
                        "second-to-last is the new user turn");
                    bool oldGone = true;
                    for (int i = 0; i < messages.Count; i++)
                    {
                        string content = (string)messages[i]["Content"];
                        if (content != null && content.IndexOf("OLD_MARKER_FIRST_MESSAGE") >= 0)
                            oldGone = false;
                    }
                    TestAssert.True(oldGone, "the oldest (marker) message was dropped");
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

            TestRunner.Run("cli.truncation_keeps_user_message", () =>
            {
                string fileToolsDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release");
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // LM Studio-style server: its Jinja chat template raises
                    // "No user query found in messages" (400) when the payload
                    // carries no "user" role. Twelve tool rounds reading the
                    // first 8000 chars of the file each (read_file's limit) keep
                    // the two newest groups at ~16k chars (~5.4k tokens) - past
                    // the 95% band of the 5000 window even after micro-compact,
                    // so the bounded 30% oldest-first truncation must chew toward
                    // the front, where the single user message sits. Dropping
                    // it used to leave a user-less payload and the 400.
                    server.RejectPayloadsWithoutUserMessage = true;
                    server.ToolCallRoundsRemaining = 12;
                    server.ToolCallName = "read_file";
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;

                    string bigFile = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 280; i++)
                        big.AppendLine("padding line " + i + " to make the tool output ~17k chars");
                    File.WriteAllText(bigFile, big.ToString(), Encoding.UTF8);

                    var args = new JObject();
                    args["filename"] = bigFile;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 5000, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("read the big file");
                        CliRunner.WaitForText(cli, "[tool output]", 30000);
                        // The usage ladder fires mid-turn: micro-compact drops old
                        // groups, then the bounded 30% truncation eats toward the front.
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        // The turn must survive: the outgoing payload kept its
                        // "user" role so the template accepted it.
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            !cli.Stdout.Contains("No user query found in messages"),
                            "payload must always contain a user message");
                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the truncation loop");
                    }
                }
            });

            TestRunner.Run("cli.spike_summarized_before_truncate", () =>
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
                    // Thirteen turns of ~2KB markdown (~27k chars) then one big tool
                    // output (the 20k shell cap) puts the mid-turn spike far past
                    // 95% of the 12000 window. The old ladder chewed the oldest
                    // messages away in geometric 30% passes with no summary; the
                    // summary pass must now absorb the spike (recap + verbatim
                    // tail) and no destructive truncation must run at all.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallSkipFirstRequests = 13;
                    server.ToolCallName = "run_shell_command";
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 3000; i++)
                        big.Append("0123456789");
                    File.WriteAllText(bigPath, big.ToString(), Encoding.UTF8);
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 12000, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 13; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);

                        // The spike must be absorbed by the summarize pass.
                        CliRunner.WaitForText(cli, "[Context usage high - summarizing conversation...", 30000);
                        CliRunner.WaitForText(cli, "[Conversation summarized - continuing...]", 30000);
                        // Requests so far: 13 turns + tool call + summary = 15, so the
                        // continued reply is turn 16.
                        CliRunner.WaitForText(cli, "# Reply turn 16", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        TestAssert.True(
                            !cli.Stdout.Contains("[Context usage high - truncated"),
                            "no destructive truncation: the spike was absorbed by the summary");
                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the spike");
                    }
                }
            });

            TestRunner.Run("cli.truncate_after_summary_still_over", () =>
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
                    // The 6000 window is smaller than the 8k-token recent tail
                    // (~24k chars at the default char ratio), so even a successful
                    // summary leaves recap + tail far above 95%. The ladder must run
                    // the bounded 30% truncation AFTER the summary, and - because one
                    // 20k tool output dominates the tail - finish with the last-resort
                    // shrink of the single biggest message.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallSkipFirstRequests = 6;
                    server.ToolCallName = "run_shell_command";
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 3000; i++)
                        big.Append("0123456789");
                    File.WriteAllText(bigPath, big.ToString(), Encoding.UTF8);
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                    File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                    File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 6000, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 6; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);

                        // Pass 2: the spike is summarized first...
                        CliRunner.WaitForText(cli, "[Context usage high - summarizing conversation...", 30000);
                        CliRunner.WaitForText(cli, "[Conversation summarized - continuing...]", 30000);
                        // ...pass 3: the recap + tail is still above 95%, so the bounded
                        // truncation runs AFTER the successful summary (oldest 30%
                        // prefix, user messages protected).
                        CliRunner.WaitForTextAfter(
                            cli, "[Conversation summarized - continuing...]",
                            "[Context usage high - truncated", 30000);
                        // ...pass 4: the single 20k tool output still dominates the tail,
                        // so the biggest message is shrunk to fit the window.
                        CliRunner.WaitForTextAfter(
                            cli, "[Context usage high - truncated",
                            "[Context usage high - message content truncated to fit the context window]", 30000);
                        // Requests so far: 6 turns + tool call + summary = 8, so the
                        // continued reply is turn 9.
                        CliRunner.WaitForText(cli, "# Reply turn 9", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the full ladder");
                    }
                }
            });

            TestRunner.Run("cli.resummarization_carries_prior_summary", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Two manual /compactfull passes. Fifteen turns (~26k chars) push
                    // the conversation past the ~24k keep window so the first
                    // compaction actually summarizes the older head. The second one
                    // then summarizes a conversation that already holds a [Previous
                    // conversation summary] marker: ExtractPreviousSummary must lift
                    // the first recap out and BuildSummaryPrompt must embed it in a
                    // <prior-summary> block of the outgoing request.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SimpleLLMChatCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 15; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        // First compaction: summary request #16 ("# Reply turn 16")
                        // becomes the stored recap.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(cli, "[Conversation compacted]", 30000);
                        CliRunner.WaitForTextAfter(cli, "[Conversation compacted]", "# Reply turn 16", 30000);
                        CliRunner.WaitForTextAfter(cli, "End of turn 16.", "You:", 15000);

                        // Two more turns (requests #17 and #18).
                        for (int turn = 16; turn <= 17; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + (turn + 1), 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        // Second compaction: summary request #19 must embed the
                        // first recap in a <prior-summary> block.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForTextAfter(cli, "# Reply turn 18", "[Conversation compacted]", 30000);
                        CliRunner.WaitForTextAfter(cli, "[Conversation compacted]", "# Reply turn 19", 30000);
                        CliRunner.WaitForTextAfter(cli, "End of turn 19.", "You:", 15000);

                        TestAssert.Contains(
                            server.LastBody, "<prior-summary>",
                            "second summary request must carry the prior-summary block");
                        TestAssert.Contains(
                            server.LastBody, "# Reply turn 16",
                            "second summary request must embed the first recap text");

                        cli.WriteLine("continue");
                        CliRunner.WaitForText(cli, "# Reply turn 20", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "chat must continue after the re-summarization");
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
                        // and the CLI falls back to truncation. The failure reason
                        // is now printed in the notice.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - summary failed (", 30000);
                        CliRunner.WaitForText(cli, "truncated", 15000);

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

        /// <summary>
        /// Fake curl.exe for the curl-fallback tests. Behavior is selected by the
        /// FAKE_CURL_MODE env var:
        ///   "ok"           - read the request from the named pipe, emit a short SSE reply
        ///   "hang"         - never connect/respond (simulates a frozen server)
        ///   "hang-connect" - behave like curl hitting --connect-timeout (exit 28) only
        ///                    when the flag is actually present in the arguments
        /// </summary>
        static int FakeCurlMain(string[] args)
        {
            string mode = Environment.GetEnvironmentVariable("FAKE_CURL_MODE");
            if (mode == null) mode = "";
            bool hasConnectTimeout = false;
            string pipeName = null;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--connect-timeout")
                    hasConnectTimeout = true;
                int pipeIdx = a.IndexOf("\\pipe\\", StringComparison.OrdinalIgnoreCase);
                if (pipeIdx >= 0)
                {
                    pipeName = a.Substring(pipeIdx + 6).Trim('"');
                }
            }

            if (mode == "hang-connect" && hasConnectTimeout)
                return 28; // curl(1)'s timeout exit code

            if (mode == "hang")
            {
                Thread.Sleep(60000);
                return 0;
            }

            if (pipeName != null)
            {
                // curl(1) opens the \\.\pipe\<name> body pipe directly. .NET's
                // FileStream rejects device paths, so open it via CreateFile.
                IntPtr handle = CreateFile("\\\\.\\pipe\\" + pipeName,
                    0x80000000 /*GENERIC_READ*/, 3 /*share r/w*/, IntPtr.Zero,
                    3 /*OPEN_EXISTING*/, 0x80 /*NORMAL*/, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while (ReadFile(handle, buf, (uint)buf.Length, out n, IntPtr.Zero) && n > 0) { }
                    CloseHandle(handle);
                }
            }

            string evt = "data: {\"choices\":[{\"delta\":{\"content\":\"curl works\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: [DONE]\n\n";
            Console.Write(evt);
            Console.Out.Flush();
            return 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr secAttrs,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        static extern bool ReadFile(IntPtr handle, byte[] buffer, uint count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static bool HasConfigKey(JObject cfgObj, string name)
        {
            foreach (JProperty prop in cfgObj.Properties())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

        static int CountProcesses(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            int count = processes.Length;
            foreach (Process p in processes)
                p.Dispose();
            return count;
        }

        static bool PollProcessCount(string processName, Func<int, bool> predicate, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int count = CountProcesses(processName);
                if (predicate(count))
                    return true;
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(150);
            }
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
