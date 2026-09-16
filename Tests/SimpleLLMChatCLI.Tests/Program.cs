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

        static void WriteCliIni(string dir, string llmServer)
        {
            string path = Path.Combine(dir, "LLMSettings.ini");
            File.WriteAllText(path,
                "[System]\r\n" +
                "apikey=test-key\r\n" +
                "llmserver=" + llmServer + "\r\n" +
                "model=test-model\r\n" +
                "sysprompt=\"You are a test assistant.\"\r\n" +
                "contextWindowSize=0\r\n" +
                "[Tools]\r\n" +
                "tools=\r\n" +
                "toolsrequiringapproval=\r\n" +
                "[Appearance]\r\n" +
                "assistantname=LLM\r\n" +
                "markdownparsing=0\r\n" +
                "[RAG]\r\n" +
                "ragenabled=0\r\n",
                Encoding.UTF8);
        }
    }
}
