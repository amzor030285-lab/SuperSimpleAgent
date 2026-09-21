using SimpleLLMChatGUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SimpleLLMChatGUI.Tests
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            return SuiteMain.Run("SimpleLLMChatGUI", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("SettingsIniWriter.escape", () =>
            {
                string escaped = SettingsIniWriter.EscapePromptForStorage("a\\b\nc\td");
                TestAssert.Contains(escaped, "\\\\", "backslash");
                TestAssert.Contains(escaped, "\\n", "newline");
                TestAssert.Contains(escaped, "\\t", "tab");
            });

            TestRunner.Run("SettingsIniWriter.write_roundtrip", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-ini"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    SettingsIniWriter.WriteInitialConfig(
                        path, "key", "http://127.0.0.1:9", "test-model", "Hello", 4096);
                    TestAssert.True(File.Exists(path), "exists");
                    string text = File.ReadAllText(path);
                    TestAssert.Contains(text, "llmserver=http://127.0.0.1:9", "server");
                    TestAssert.Contains(text, "model=test-model", "model");
                }
            });

            TestRunner.Run("ToolApproval.parse", () =>
            {
                string msg = ToolApproval.FormatApprovalMessage("shell", "echo hi");
                string tool, arguments;
                TestAssert.True(ToolApproval.TryParseApprovalPrompt(
                    msg + "\n" + ToolApproval.ApprovalPrompt, out tool, out arguments), "parse");
                TestAssert.Equal("shell", tool, "tool");
                TestAssert.Contains(arguments, "echo hi", "args");
            });

            TestRunner.Run("ModelsClient.list_models", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                {
                    IList<string> models = ModelsClient.ListModels(server.BaseUrl, "test-key");
                    TestAssert.True(models.Count >= 1, "count");
                    TestAssert.Equal("test-model", models[0], "id");
                }
            });

            TestRunner.Run("ModelsClient.empty_url", () =>
            {
                bool threw = false;
                try { ModelsClient.ListModels("", ""); }
                catch (Exception) { threw = true; }
                TestAssert.True(threw, "should throw");
            });

            TestRunner.Run("gui.stop_button_interrupts", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    // Long slow reply: a natural completion (~15s) cannot beat the stop.
                    server.FixedReply = new string('z', 3000);
                    server.StreamDelayMs = 300;
                    server.StreamChunkChars = 60;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        Process cli = null;
                        Stopwatch ready = Stopwatch.StartNew();
                        while (ready.ElapsedMilliseconds < 15000 && cli == null)
                        {
                            cli = gui.FindCliProcess();
                            if (cli == null)
                                Thread.Sleep(100);
                        }
                        TestAssert.True(cli != null, "CLI process started");

                        gui.StartTurn("please stream", 10000);

                        // Wait out the double-click guard before clicking Stop.
                        Thread.Sleep(800);
                        TestAssert.Equal("Stop", gui.SendButtonName(), "button shows Stop while generating");
                        gui.ClickSend();

                        gui.WaitUntilIdle(20000);
                        TestAssert.Equal("Send", gui.SendButtonName(), "button back to Send after stop");
                        TestAssert.True(gui.FindCliProcess() != null, "CLI still running after stop");
                    }
                }
            });

            TestRunner.Run("gui.reply_contains_you_prompt", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    // The reply quotes the user mid-line: only the CLI's own
                    // blank-line-anchored "You: " prompt may end the generation.
                    server.FixedReply = "To answer as You: phrased it, the answer is 42.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("answer the question", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "as You: phrased it", "mid-line You: text stays in the reply");
                        TestAssert.Contains(text, "the answer is 42.", "reply not truncated at the quote");
                        TestAssert.Equal("Send", gui.SendButtonName(), "idle after the turn");
                    }
                }
            });

            TestRunner.Run("gui.reply_you_colon_no_space", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    // "You:" not followed by a space can never be the CLI's
                    // prompt (it always emits "You: "); the old code held such
                    // text back forever and never finished the turn.
                    server.FixedReply = "The macro You:assign expands to 1.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("expand the macro", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "You:assign", "non-prompt You: text shown");
                        TestAssert.Contains(text, "expands to 1.", "reply completes");
                        TestAssert.Equal("Send", gui.SendButtonName(), "idle after the turn");
                    }
                }
            });

            TestRunner.Run("gui.save_load_dialogs", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply = "Saved nicely.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        gui.SendTurn("hello dialogs", 30000);

                        gui.ClickMainButton("saveButton");
                        gui.SetTextBoxInWindow("Save Session", "saveNameInput", "gui_sess", 10000);
                        gui.ClickButtonInWindow("Save Session", "dialogOkButton", 10000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "gui_sess.json");
                        Stopwatch sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 10000 && !File.Exists(sessionFile))
                            Thread.Sleep(100);
                        TestAssert.True(File.Exists(sessionFile), "session file saved");
                        string json = File.ReadAllText(sessionFile, Encoding.UTF8);
                        TestAssert.Contains(json, "hello dialogs", "user content preserved");

                        gui.WaitUntilIdle(15000);

                        gui.ClickMainButton("loadButton");
                        gui.SelectListItemInWindow("Load Session", "sessionListBox", "gui_sess", 10000);
                        gui.ClickButtonInWindow("Load Session", "dialogOkButton", 10000);

                        gui.WaitUntilIdle(15000);
                    }
                }
            });

            TestRunner.Run("gui.markdown_table", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply =
                        "Here is the table.\n\n" +
                        "| Name | Value |\n" +
                        "|:-----|------:|\n" +
                        "| alpha | 1 |\n" +
                        "| beta  | 22 |";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("show the table", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "alpha", "table cell rendered");
                        TestAssert.True(!text.Contains(":-----"), "separator row consumed into WPF table");
                    }
                }
            });

            TestRunner.Run("gui.markdown_crash_cases", () =>
            {
                // Ragged table row (Markdig pads it with an empty childless cell)
                // and a link whose label holds an autolink (WPF forbids a
                // Hyperlink inside a Hyperlink) — both used to crash Render.
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply =
                        "Table with a short row:\n\n" +
                        "| Name | Value |\n" +
                        "|:-----|------:|\n" +
                        "| alpha |\n" +
                        "\n" +
                        "Nested link: [<http://inner.example>](http://outer.example)";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("show the crash cases", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "alpha", "ragged-row table rendered");
                        TestAssert.True(!text.Contains(":-----"), "separator row consumed");
                        TestAssert.Contains(text, "http://inner.example", "autolink text preserved");
                        TestAssert.Contains(text, "http://outer.example", "outer url preserved");
                    }
                }
            });

            TestRunner.Run("gui.markdown_lists_blockquote", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply =
                        "Here are the items:\n\n" +
                        "- alpha\n" +
                        "- beta\n\n" +
                        "1. first\n" +
                        "2. second\n\n" +
                        "> quoted line";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("show the list", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "alpha", "unordered item rendered");
                        TestAssert.Contains(text, "\u2022", "bullet marker rendered");
                        TestAssert.Contains(text, "first", "ordered item rendered");
                        TestAssert.Contains(text, "2.", "ordered marker rendered");
                        TestAssert.Contains(text, "quoted line", "blockquote text rendered");
                        TestAssert.True(!text.Contains("- alpha"), "list marker consumed");
                        TestAssert.True(!text.Contains("> quoted"), "blockquote marker consumed");
                    }
                }
            });

            TestRunner.Run("gui.code_block_copy", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply =
                        "Here is the code:\n\n" +
                        "```python\n" +
                        "print('hello')\n" +
                        "print('world')\n" +
                        "```\n\n" +
                        "Done.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("show the code", 30000);

                        string text = gui.GetAllText();
                        TestAssert.Contains(text, "print('hello')", "code text rendered");
                        TestAssert.Contains(text, "python", "language label rendered");
                        TestAssert.Contains(text, "Done.", "text after code block preserved");
                        TestAssert.True(!text.Contains("```"), "fence markers consumed");

                        gui.ClearClipboard();
                        gui.ClickMainButton("codeCopyButton");
                        string copied = gui.ReadClipboardContaining("print('hello')", 5000);
                        TestAssert.Contains(copied, "print('hello')", "code copied");
                        TestAssert.True(!copied.Contains("```"), "fence markers excluded from copied code");
                        TestAssert.True(!copied.Contains("Done."), "surrounding text excluded from copied code");

                        // Index 1 = the assistant turn's message copy button (index 0 is the user turn).
                        gui.ClickMainButton("copyButton", 1);
                        string message = gui.ReadClipboardContaining("```python", 5000);
                        TestAssert.Contains(message, "Done.", "message copy includes full reply");
                        TestAssert.Contains(message, "```python", "message copy keeps raw markdown");
                    }
                }
            });

            TestRunner.Run("gui.markdown_edge_cases", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply =
                        "Mixed **bold**, *italic*, ~~strike~~ and `code` in one line.\n" +
                        "\n" +
                        "- top one\n" +
                        "  - nested one\n" +
                        "- top two\n" +
                        "\n" +
                        "1. step one\n" +
                        "2. step two\n" +
                        "\n" +
                        "- [x] done item\n" +
                        "- [ ] open item\n" +
                        "\n" +
                        "> note **inside** quote\n" +
                        "\n" +
                        "| Left | Right |\n" +
                        "|:-----|------:|\n" +
                        "| `a1` | **b2** |\n" +
                        "\n" +
                        "---\n" +
                        "\n" +
                        "See [docs](https://example.com/docs) and [mail](mailto:x@y.z) for more.\n" +
                        "\n" +
                        "2.4.1. dotted paragraph line.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.SendTurn("show the edge cases", 30000);

                        string text = gui.GetAllText();

                        // Inline styles: rendered without raw markers.
                        TestAssert.True(!text.Contains("**bold**"), "bold markers consumed");
                        TestAssert.True(!text.Contains("*italic*"), "italic markers consumed");
                        TestAssert.True(!text.Contains("~~strike~~"), "strikethrough markers consumed");
                        TestAssert.True(!text.Contains("`code`"), "code span markers consumed");

                        // Lists: nested and ordered, markers consumed.
                        TestAssert.Contains(text, "nested one", "nested list item rendered");
                        TestAssert.Contains(text, "step two", "ordered item rendered");
                        TestAssert.True(!text.Contains("- top one"), "unordered marker consumed");

                        // Task list.
                        TestAssert.Contains(text, "[x]", "checked task rendered");
                        TestAssert.Contains(text, "[ ]", "unchecked task rendered");

                        // Blockquote with inline bold.
                        TestAssert.Contains(text, "inside", "blockquote inline text rendered");
                        TestAssert.True(!text.Contains("> note"), "blockquote marker consumed");

                        // Table with inline code and bold cells.
                        TestAssert.Contains(text, "a1", "code cell rendered");
                        TestAssert.Contains(text, "b2", "bold cell rendered");
                        TestAssert.True(!text.Contains(":-----"), "table separator consumed");

                        // Links: http as hyperlink label, mailto as plain label.
                        TestAssert.Contains(text, "docs", "link label rendered");
                        TestAssert.Contains(text, "mail", "mailto label rendered");
                        TestAssert.True(!text.Contains("mailto:"), "mailto url not displayed");
                        TestAssert.True(!text.Contains("(https://"), "raw link syntax not displayed");

                        // A dotted version line must stay a paragraph, not a list.
                        TestAssert.Contains(text, "2.4.1. dotted paragraph line.", "dotted line stays plain text");
                    }
                }
            });

            TestRunner.Run("gui.window_and_long_chat", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.LongMarkdownReplies = true;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    string sessionPerf = Path.Combine(
                        Path.GetDirectoryName(TestLog.LogPath) ?? ".",
                        "gui-session.perf.tsv");
                    using (StreamWriter perf = new StreamWriter(sessionPerf, false, Encoding.UTF8))
                    {
                        perf.WriteLine("turn\twait_ms\tgui_workingset_mb\tcli_workingset_mb");

                        using (GuiDriver gui = new GuiDriver(ws.Path))
                        {
                            gui.WaitUntilReady(30000);

                            Process cli = null;
                            Stopwatch ready = Stopwatch.StartNew();
                            while (ready.ElapsedMilliseconds < 15000 && cli == null)
                            {
                                cli = gui.FindCliProcess();
                                if (cli == null)
                                    System.Threading.Thread.Sleep(100);
                            }
                            TestAssert.True(cli != null, "CLI process started under work dir");

                            const int turns = 50;
                            for (int i = 1; i <= turns; i++)
                            {
                                Stopwatch turnSw = Stopwatch.StartNew();
                                gui.SendTurn("turn " + i, 60000);
                                turnSw.Stop();

                                long guiMb = WorkingSetMb(gui.GuiProcess);
                                Process cliProc = gui.FindCliProcess() ?? cli;
                                long cliMb = WorkingSetMb(cliProc);
                                perf.WriteLine(string.Format("{0}\t{1}\t{2}\t{3}",
                                    i, turnSw.ElapsedMilliseconds, guiMb, cliMb));
                                perf.Flush();
                                TestLog.Detail(string.Format(
                                    "turn {0} wait_ms={1} gui_mb={2} cli_mb={3}",
                                    i, turnSw.ElapsedMilliseconds, guiMb, cliMb));
                            }
                        }
                    }
                    TestLog.Info("gui-session.perf.tsv=" + sessionPerf);
                }
            });
        }

        static long WorkingSetMb(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return 0;
                p.Refresh();
                return p.WorkingSet64 / (1024 * 1024);
            }
            catch
            {
                return 0;
            }
        }

        static TempWorkspace PackageGuiWorkspace()
        {
            TempWorkspace ws = new TempWorkspace("gui-run");
                string[] files = { "SimpleLLMChatGUI.exe", "SimpleLLMChatCLI.exe", "Newtonsoft.Json.dll", "Markdig.dll" };
            foreach (string f in files)
            {
                string src = ToolClient.ProductExe(f);
                if (!File.Exists(src) && f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new TestFailureException("Missing packaged file: " + src);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(ws.Path, f), true);
            }
            return ws;
        }

        static void WriteGuiIni(string dir, string llmServer)
        {
            string path = Path.Combine(dir, "LLMSettings.ini");
            SettingsIniWriter.WriteInitialConfig(
                path,
                "test-key",
                llmServer,
                "test-model",
                "You are a test assistant.",
                8192);
        }
    }
}
