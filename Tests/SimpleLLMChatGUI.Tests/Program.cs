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

      // The reply keeps streaming for several seconds after the marker, so the
      // check has a real window in which the text must already be on screen. A
      // flush that only happened at the end of the turn would miss it.
      const string SlowPrefix = "progressive-render-check-part-one-";
      const string SlowSuffix =
          "-part-two-continues-streaming-for-a-while-so-the-marker-must-appear-early-" +
          "not-at-the-end-third-section-of-the-same-reply-keeps-coming-and-coming-" +
          "and-still-coming-so-the-turn-is-unambiguously-still-running-when-the-" +
          "marker-is-found-on-screen-fourth-and-final-section-of-the-long-reply.";

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
                    TestAssert.Contains(text, "toolsrequiringapproval=run_shell_command,download_file", "default approval tools");
                    TestAssert.Contains(text, "apikey=dpapi:", "api key encrypted on disk");
                    TestAssert.True(!text.Contains("apikey=key"), "api key not in plaintext");
                    TestAssert.Equal("key", new ConfigHandler(path).GetConfigValue("apikey"), "api key decrypts on read");
                }
            });

            TestRunner.Run("SettingsIniWriter.chatpath_roundtrip", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-ini-chatpath"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    // A fresh install must leave the key present but empty: the CLI then
                    // detects the path from llmserver. Dropping the line entirely would
                    // lose the user's setting the next time Options rewrites the file.
                    SettingsIniWriter.WriteInitialConfig(
                        path, "key", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "Hello", 4096);
                    string text = File.ReadAllText(path);
                    TestAssert.Contains(text, "chatpath=\r\n", "chatpath written empty on a fresh install");
                    TestAssert.Equal("", new ConfigHandler(path).GetConfigValue("chatpath"), "reads back empty");

                    // The Options save path carries an explicit value through unchanged.
                    List<string> lines = SettingsIniWriter.GetSystemSettings(
                        "key", "https://open.bigmodel.cn/api/paas/v4", " /chat/completions ",
                        "glm-4-flash", "Hello", 4096);
                    string chatLine = null;
                    foreach (string line in lines)
                        if (line.StartsWith("chatpath=", StringComparison.Ordinal))
                            chatLine = line;
                    TestAssert.Equal("chatpath=/chat/completions", chatLine, "explicit chatpath trimmed and kept");
                }
            });

            TestRunner.Run("SettingsIniWriter.repetition_guard_off_on_a_fresh_install", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-ini-guard"))
                {
                    // A new install must not have the guard running: whether a model
                    // loops is a property of the model, and the user is the one who
                    // decides. The key is written explicitly so the value is visible
                    // and editable instead of appearing only when something goes wrong.
                    string path = ws.Combine("LLMSettings.ini");
                    SettingsIniWriter.WriteInitialConfig(
                        path, "key", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", "Hello", 4096);

                    string text = File.ReadAllText(path);
                    TestAssert.Contains(text, "repetitionguard=0\r\n", "written, off");
                    TestAssert.Equal("0", new ConfigHandler(path).GetConfigValue("repetitionguard"), "reads back off");
                }
            });

            TestRunner.Run("readme_documents_every_ini_key", () =>
            {
                // Nothing in the build compares the README to the keys the app
                // actually writes, so a key can ship undocumented and nothing
                // notices - which is how chatpath, the two timeouts and the
                // presets all went missing. The key list is taken from a real
                // config rather than a hand-kept one, so the test cannot drift
                // along with the code.
                using (TempWorkspace ws = new TempWorkspace("gui-readme"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    SettingsIniWriter.WriteInitialConfig(
                        path, "key", "https://example.invalid/api/v1", "a-model", "Hello", 4096);

                    string readme = ReadRepoFile("README.md");
                    List<string> missing = new List<string>();
                    foreach (string line in File.ReadAllLines(path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }

                        string key = line.Substring(0, eq).Trim();
                        if (key.Length == 0 || key.StartsWith("[", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // Dynamic keys carry a per-tool suffix and are documented
                        // by pattern, so match on the part before the dot.
                        int dot = key.IndexOf('.');
                        if (dot > 0)
                        {
                            key = key.Substring(0, dot);
                        }

                        if (readme.IndexOf("`" + key + "`", StringComparison.Ordinal) < 0)
                        {
                            missing.Add(key);
                        }
                    }

                    TestAssert.Equal(0, missing.Count,
                        missing.Count == 0
                            ? "every written key is in the README Configuration list"
                            : "undocumented INI keys: " + string.Join(", ", missing.ToArray()));
                }
            });

            TestRunner.Run("PresetStore.roundtrip", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-preset"))
                {
                    string path = ws.Combine(PresetStore.FileName);
                    var first = new LlmPreset
                    {
                        Name = "GLM-4.7-Flash - bigmodel",
                        ServerUrl = "https://open.bigmodel.cn/api/paas/v4",
                        ApiKey = "glm-key",
                        ChatPath = string.Empty,
                        Model = "glm-4.7-flash",
                        ContextWindowSize = 131072,
                    };
                    var second = new LlmPreset
                    {
                        Name = "Local ollama",
                        ServerUrl = "http://127.0.0.1:11434",
                        Model = "qwen2.5:14b",
                        EmbeddingsEndpoint = "https://api.openai.com/v1",
                        EmbeddingsApiKey = "emb-key",
                        EmbeddingsModel = "text-embedding-3-small",
                    };

                    PresetStore.Save(path, new List<LlmPreset> { first, second });
                    List<LlmPreset> loaded = PresetStore.Load(path);

                    TestAssert.Equal(2, loaded.Count, "both presets read back");
                    TestAssert.Equal("GLM-4.7-Flash - bigmodel", loaded[0].Name, "file order preserved");
                    TestAssert.Equal("https://open.bigmodel.cn/api/paas/v4", loaded[0].ServerUrl, "server url");
                    TestAssert.Equal("glm-key", loaded[0].ApiKey, "api key round-trips through DPAPI");
                    TestAssert.Equal("glm-4.7-flash", loaded[0].Model, "model");
                    TestAssert.Equal(131072, loaded[0].ContextWindowSize, "context window");
                    // An empty chatpath means "detect from llmserver" and must stay empty:
                    // baking the resolved path in would freeze today's detection rule.
                    TestAssert.Equal("", loaded[0].ChatPath, "empty chatpath stays empty");

                    TestAssert.Equal("Local ollama", loaded[1].Name, "second preset name");
                    // No api key on this one, which is legitimate for a local server.
                    TestAssert.Equal("", loaded[1].ApiKey, "absent api key stays empty");
                    // 0 keeps the CLI sizing its budget from real usage instead of a
                    // percentage of a window.
                    TestAssert.Equal(0, loaded[1].ContextWindowSize, "unset context window stays 0");
                    TestAssert.Equal("https://api.openai.com/v1", loaded[1].EmbeddingsEndpoint, "embeddings endpoint");
                    TestAssert.Equal("emb-key", loaded[1].EmbeddingsApiKey, "embeddings key");
                    TestAssert.Equal("text-embedding-3-small", loaded[1].EmbeddingsModel, "embeddings model");
                }
            });

            TestRunner.Run("PresetStore.secrets_are_encrypted", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-preset-secret"))
                {
                    string path = ws.Combine(PresetStore.FileName);
                    PresetStore.Save(path, new List<LlmPreset>
                    {
                        new LlmPreset { Name = "p", ApiKey = "sk-live-PLAINTEXT-abc123", EmbeddingsApiKey = "sk-emb-PLAINTEXT-def456" },
                    });

                    string text = File.ReadAllText(path);
                    TestAssert.True(!text.Contains("sk-live-PLAINTEXT-abc123"), "api key is not on disk in the clear");
                    TestAssert.True(!text.Contains("sk-emb-PLAINTEXT-def456"), "embeddings key is not on disk in the clear");
                    TestAssert.Contains(text, "apikey=" + SecretProtector.Prefix, "api key stored with the dpapi prefix");
                    TestAssert.Contains(text, "embeddingsapikey=" + SecretProtector.Prefix, "embeddings key stored with the dpapi prefix");

                    LlmPreset loaded = PresetStore.Load(path)[0];
                    TestAssert.Equal("sk-live-PLAINTEXT-abc123", loaded.ApiKey, "still decrypts for the app");
                    TestAssert.Equal("sk-emb-PLAINTEXT-def456", loaded.EmbeddingsApiKey, "embeddings key still decrypts");
                }
            });

            TestRunner.Run("PresetStore.value_with_equals_survives", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-preset-equals"))
                {
                    string path = ws.Combine(PresetStore.FileName);
                    // Both an INI value and a query string can contain '='; the parser
                    // must split on the first one only.
                    PresetStore.Save(path, new List<LlmPreset>
                    {
                        new LlmPreset
                        {
                            Name = "p",
                            ServerUrl = "https://host/v1?a=b&c=d",
                            Model = "m=1",
                            ApiKey = "key=with=equals",
                        },
                    });

                    LlmPreset loaded = PresetStore.Load(path)[0];
                    TestAssert.Equal("https://host/v1?a=b&c=d", loaded.ServerUrl, "url with query string survives");
                    TestAssert.Equal("m=1", loaded.Model, "model with '=' survives");
                    TestAssert.Equal("key=with=equals", loaded.ApiKey, "key with '=' survives");
                }
            });

            TestRunner.Run("PresetStore.upsert_and_delete", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-preset-upsert"))
                {
                    string path = ws.Combine(PresetStore.FileName);
                    var presets = new List<LlmPreset>
                    {
                        new LlmPreset { Name = "a", Model = "first" },
                        new LlmPreset { Name = "b", Model = "second" },
                    };
                    PresetStore.Save(path, presets);

                    // Re-saving the same name overwrites in place instead of duplicating.
                    // Located by name, because the list is re-read and holds new instances.
                    List<LlmPreset> current = PresetStore.Load(path);
                    int index = PresetStore.FindIndex(current, "a");
                    TestAssert.Equal(0, index, "FindIndex locates the preset by name");

                    LlmPreset updated = current[index];
                    updated.Model = "updated";
                    current[index] = updated;
                    PresetStore.Save(path, current);

                    List<LlmPreset> afterUpdate = PresetStore.Load(path);
                    TestAssert.Equal(2, afterUpdate.Count, "overwrite did not add a third section");
                    TestAssert.Equal("updated", afterUpdate[0].Model, "same-name preset was overwritten");
                    TestAssert.Equal("second", afterUpdate[1].Model, "the other preset is untouched");

                    TestAssert.True(PresetStore.Delete(path, "a"), "delete reports success");
                    TestAssert.Equal(1, PresetStore.Load(path).Count, "one preset left");
                    TestAssert.Equal("b", PresetStore.Load(path)[0].Name, "the right one is left");
                    TestAssert.Equal("second", PresetStore.Load(path)[0].Model, "with its fields intact");
                    TestAssert.True(!PresetStore.Delete(path, "a"), "deleting a missing preset reports failure");
                    TestAssert.True(PresetStore.FindIndex(PresetStore.Load(path), "a") < 0, "FindIndex reports a missing preset as -1");
                }
            });

            TestRunner.Run("PresetStore.skips_unusable_section", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("gui-preset-badname"))
                {
                    string path = ws.Combine(PresetStore.FileName);
                    // A name becomes a section header, so brackets cannot be part of it.
                    // A hand-edited file must not throw on the next read either.
                    File.WriteAllLines(path, new List<string>
                    {
                        "[good]",
                        "model=m1",
                        "[bad]name]",
                        "model=m2",
                    });

                    List<LlmPreset> loaded = PresetStore.Load(path);
                    TestAssert.Equal(1, loaded.Count, "only the usable section is read");
                    TestAssert.Equal("good", loaded[0].Name, "usable section kept");
                    // Still m1, so the unreadable section's model=m2 did not bleed into it.
                    TestAssert.Equal("m1", loaded[0].Model, "unread section does not leak keys into it");

                    PresetStore.Save(path, new List<LlmPreset> { new LlmPreset { Name = "also bad]name", Model = "m3" } });
                    TestAssert.Equal(0, PresetStore.Load(path).Count, "an unusable name is not written back");
                }
            });

            TestRunner.Run("ToolApproval.parse", () =>
            {
                string msg = ToolApproval.FormatApprovalMessage("shell", "echo hi");
                string tool, arguments;
                TestAssert.True(ToolApproval.TryParseApprovalPrompt(
                    msg + ToolApproval.ApprovalPrompt, out tool, out arguments), "parse");
                TestAssert.Equal("shell", tool, "tool");
                TestAssert.Contains(arguments, "echo hi", "args");
            });

            TestRunner.Run("ColorHelper.palettes_cover_configured_colors", () =>
            {
                var expected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var config in ColorHelper.ColorConfigs.Values)
                    expected.Add(config.ResourceKey);

                TestAssert.Equal(14, expected.Count, "configured brush resources");
                TestAssert.Equal(ColorHelper.ColorConfigs.Count, expected.Count, "no two settings share a resource");

                foreach (var palette in ColorHelper.ThemePalettes)
                {
                    foreach (string key in expected)
                        TestAssert.True(palette.Value.ContainsKey(key),
                            palette.Key + " defines " + key);
                    TestAssert.Equal(expected.Count, palette.Value.Count,
                        palette.Key + " has no extra/duplicate entries");
                }

                foreach (string theme in ColorHelper.ThemeOrder)
                    TestAssert.Equal(ColorHelper.ColorConfigs.Count,
                        ColorHelper.GetThemedResourceKeys(theme).Count,
                        theme + " themes every configured color");
            });

            TestRunner.Run("ColorHelper.button_border_is_a_configured_color", () =>
            {
                // Regression: ButtonBorderBrush was in every palette but had no
                // ColorConfig, so ApplyTheme never painted it.
                var config = ColorHelper.GetConfig("ButtonBorderBrush");
                TestAssert.True(config != null, "ButtonBorderBrush has a config");
                TestAssert.Equal("buttonbordercolor", config.Key, "config key");
                TestAssert.Equal("Button Border", config.DisplayName, "dialog label");

                foreach (var palette in ColorHelper.ThemePalettes)
                    TestAssert.True(palette.Value.ContainsKey("ButtonBorderBrush"),
                        palette.Key + " border color is applied");
            });

            TestRunner.Run("ColorHelper.theme_order_matches_palettes", () =>
            {
                foreach (string theme in ColorHelper.ThemeOrder)
                {
                    if (theme != ColorHelper.ThemeSystem)
                        TestAssert.True(ColorHelper.ThemePalettes.ContainsKey(theme),
                            theme + " has a palette");
                }
                TestAssert.Equal(ColorHelper.ThemeOrder.Length,
                    ColorHelper.ThemePalettes.Count + 1,
                    "order covers every palette plus system");
            });

            TestRunner.Run("ColorHelper.custom_color_overrides_theme", () =>
            {
                var custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                custom["chattextcolor"] = "#FFFF00FF";

                TestAssert.Equal("#FFFF00FF",
                    HexColor(ColorHelper.ResolveColor("ChatTextColorBrush", ColorHelper.ThemeDark, custom)),
                    "custom color wins over the palette");

                var cleared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                cleared["chattextcolor"] = "   ";
                TestAssert.Equal(
                    HexColor(ColorHelper.ThemePalettes[ColorHelper.ThemeDark]["ChatTextColorBrush"]),
                    HexColor(ColorHelper.ResolveColor("ChatTextColorBrush", ColorHelper.ThemeDark, cleared)),
                    "blank value falls back to the palette");

                TestAssert.Equal(
                    HexColor(ColorHelper.GetConfig("ChatTextColorBrush").DefaultSystemColor),
                    HexColor(ColorHelper.ResolveColor("ChatTextColorBrush", ColorHelper.ThemeSystem,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
                    "system theme uses system colors");
            });

            TestRunner.Run("ColorHelper.solarized_controls_differ_from_chat_background", () =>
            {
                // Regression: buttons and the user bubble were the same color as
                // the chat background, so they were invisible.
                var palette = ColorHelper.ThemePalettes[ColorHelper.ThemeSolarizedDark];
                foreach (string key in new[]
                    { "ButtonBackgroundBrush", "UserBubbleBackgroundBrush", "CodeBlockBackgroundColorBrush" })
                {
                    TestAssert.True(palette[key] != palette["ChatBackgroundColorBrush"],
                        key + " differs from the chat background");
                }
            });

            TestRunner.Run("ColorHelper.every_theme_is_visible_against_system", () =>
            {
                // Regression: a theme that resolves to the system colors looks
                // like "nothing happened" when the user picks it.
                var noCustom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (string theme in ColorHelper.ThemeOrder)
                {
                    if (theme == ColorHelper.ThemeSystem)
                        continue;

                    foreach (string key in new[] { "WindowBackgroundColorBrush", "ChatTextColorBrush" })
                    {
                        var themed = ColorHelper.ResolveColor(key, theme, noCustom);
                        var system = ColorHelper.ResolveColor(key, ColorHelper.ThemeSystem, noCustom);
                        TestAssert.True(themed != system,
                            theme + " changes " + key + " (both were " + themed + ")");
                    }
                }
            });

            TestRunner.Run("ColorHelper.theme_is_read_from_colors_ini", () =>
            {
                // Regression: theme=claudecode in colors.ini must survive parsing.
                var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                settings["theme"] = "claudecode";
                TestAssert.Equal("claudecode", ColorHelper.GetThemeName(settings), "theme from ini");

                var file = Path.Combine(Path.GetTempPath(), "colors-" + Guid.NewGuid().ToString("N") + ".ini");
                File.WriteAllText(file, "theme=claudecode\r\n");
                try
                {
                    TestAssert.Equal("claudecode", ColorHelper.GetTheme(file), "theme from file on disk");
                    TestAssert.Equal("claudecode",
                        ColorHelper.GetThemeName(IniFileHandler.LoadIni(file)), "theme via ini loader");
                }
                finally
                {
                    File.Delete(file);
                }
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

            TestRunner.Run("gui.approval_mode_button", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    // The bottom-left button shows the current tool-approval mode and
                    // cycles config -> ask -> auto; each click sends /approval <mode>
                    // to the CLI. With no approvalmode in the INI the default is config.
                    server.FixedReply = "ok";
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        TestAssert.Equal("config", gui.MainButtonName("approvalModeButton"), "default mode");
                        gui.ClickMainButton("approvalModeButton");
                        TestAssert.Equal("ask", gui.MainButtonName("approvalModeButton"), "after first click");
                        gui.ClickMainButton("approvalModeButton");
                        TestAssert.Equal("auto", gui.MainButtonName("approvalModeButton"), "after second click");
                        gui.ClickMainButton("approvalModeButton");
                        TestAssert.Equal("config", gui.MainButtonName("approvalModeButton"), "cycles back");

                        // The CLI must have received the mode (a shell tool call in
                        // config mode would otherwise open the approval dialog).
                        gui.SendTurn("what is your approval mode", 30000);
                        TestAssert.Equal("Send", gui.SendButtonName(), "turn completed");
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

              TestRunner.Run("gui.streamed_text_is_not_lost_in_batching", () =>
              {
                  using (FakeOpenAiServer server = new FakeOpenAiServer())
                  using (TempWorkspace ws = PackageGuiWorkspace())
                  {
                      // Streamed text is now buffered and flushed on a timer instead of
                      // rendered per chunk. Two things can go wrong that way: the tail
                      // of a turn can be lost if the buffer is not flushed at the
                      // boundary, and characters can be dropped when the reader thread
                      // and the flush timer touch the buffer at the same time. The reply
                      // ends in a marker and has no internal newlines, so a lost
                      // character shows up as a mismatch against the exact string.
                      StringBuilder reply = new StringBuilder();
                      for (int i = 0; i < 120; i++)
                          reply.Append("chunk" + i + " ");
                      reply.Append("END-OF-REPLY");
                      server.FixedReply = reply.ToString();
                      server.StreamDelayMs = 1;
                      server.StreamChunkChars = 7;
                      WriteGuiIni(ws.Path, server.BaseUrl);

                      using (GuiDriver gui = new GuiDriver(ws.Path))
                      {
                          gui.WaitUntilReady(30000);
                          gui.SendTurn("stream a long reply", 30000);

                          string text = gui.GetAllText();
                          TestAssert.Contains(text, "END-OF-REPLY",
                              "last streamed chunk flushed at the turn boundary");
                          TestAssert.Contains(text, "chunk0 ", "first chunk rendered");
                          TestAssert.Contains(text, "chunk119 ", "last numbered chunk rendered");

                          // Every chunk must survive intact and in order; the reader
                          // thread and the UI flush share the buffer under a lock.
                          int expected = 0;
                          for (int i = 0; i < 120; i++)
                          {
                              int at = text.IndexOf("chunk" + i + " ", StringComparison.Ordinal);
                              TestAssert.True(at >= 0, "chunk" + i + " present");
                              if (at >= 0 && expected >= 0)
                                  TestAssert.True(at > expected, "chunk" + i + " in order");
                              expected = at;
                          }

                          TestAssert.Equal("Send", gui.SendButtonName(), "idle after the turn");
                      }
                  }
              });

              TestRunner.Run("gui.second_turn_still_streams", () =>
              {
                  using (FakeOpenAiServer server = new FakeOpenAiServer())
                  using (TempWorkspace ws = PackageGuiWorkspace())
                  {
                      // The flush timer is parked at every turn boundary, so the
                      // second turn is the one that proves it gets started again.
                      // The symptom to catch is the reply arriving in one lump at the
                      // end instead of progressively: the marker is only reachable
                      // while the turn is still streaming, so it can never be found by
                      // a final flush.
                      server.StreamDelayMs = 60;
                      server.StreamChunkChars = 8;
                      WriteGuiIni(ws.Path, server.BaseUrl);

                      using (GuiDriver gui = new GuiDriver(ws.Path))
                      {
                          gui.WaitUntilReady(30000);

                          server.FixedReply = "FIRST-TURN-MARKER-END";
                          gui.SendTurn("first question", 30000);
                          TestAssert.Contains(gui.GetAllText(), "FIRST-TURN-MARKER-END", "first turn rendered");

                          server.FixedReply = SlowPrefix + "SECOND-STREAMING-MARKER" + SlowSuffix;
                          gui.StartTurn("second question", 30000);

                          // Must already be on screen while the turn is still running:
                          // finding it only after the turn ended would just mean the
                          // end-of-turn flush worked, which it always does.
                          Stopwatch sw = Stopwatch.StartNew();
                          bool seenWhileStreaming = false;
                          while (sw.ElapsedMilliseconds < 30000)
                          {
                              if (gui.GetAllText().Contains("SECOND-STREAMING-MARKER"))
                              {
                                  seenWhileStreaming = gui.SendButtonName() == "Stop";
                                  break;
                              }
                              Thread.Sleep(100);
                          }

                          TestAssert.True(seenWhileStreaming,
                              "second turn rendered progressively, not only at the end");

                          gui.WaitUntilIdle(30000);
                          TestAssert.Contains(gui.GetAllText(), "SECOND-STREAMING-MARKER", "marker persisted");
                          TestAssert.Equal("Send", gui.SendButtonName(), "idle after the second turn");
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

            // A session file stores what was sent to the model, and a user turn is the
            // user's message plus the context the app baked into it (the memory and
            // skills snapshots). Showing that verbatim makes it look like the user
            // typed a wall of memory dumps into their own message.
            TestRunner.Run("gui.loaded_session_hides_baked_context", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    server.FixedReply = "Done.";
                    server.StreamDelayMs = 5;
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    const string typed = "напиши ок";
                    string baked = typed + "\n\nCurrent Memories:\ncurrent_date: 29 сентября 2026\n"
                        + "\n\nSkills:\nSkills are located in C:\\skills.\n(no skills installed)";

                    // New files keep the typed text in UserText; old ones only have the
                    // combined Content and have to be split on the snapshot headers.
                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    Directory.CreateDirectory(sessionsDir);
                    File.WriteAllText(Path.Combine(sessionsDir, "baked_new.json"),
                        SessionJson(baked, "\"UserText\":\"" + JsonText(typed) + "\","), Encoding.UTF8);
                    File.WriteAllText(Path.Combine(sessionsDir, "baked_old.json"),
                        SessionJson(baked, ""), Encoding.UTF8);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        foreach (string name in new string[] { "baked_new", "baked_old" })
                        {
                            gui.ClickMainButton("loadButton");
                            gui.SelectListItemInWindow("Load Session", "sessionListBox", name, 10000);
                            gui.ClickButtonInWindow("Load Session", "dialogOkButton", 10000);
                            gui.WaitUntilIdle(15000);

                            string text = gui.GetAllText();
                            TestAssert.Contains(text, typed, name + ": the user's own message is shown");
                            TestAssert.True(!text.Contains("Current Memories:"),
                                name + ": the memory snapshot is not shown as user text");
                            TestAssert.True(!text.Contains("Skills:"),
                                name + ": the skills snapshot is not shown as user text");
                        }
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
                        TestAssert.True(!text.Contains("Copy"), "copy buttons are icons, not text labels");

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

            TestRunner.Run("gui.repetition_guard_box_survives_a_restart", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    WriteGuiIni(ws.Path, server.BaseUrl);
                    string iniPath = Path.Combine(ws.Path, "LLMSettings.ini");

                    // How prone a model is to loop is the user's call, so the key must
                    // reach the INI the CLI reads. A box that only worked for the
                    // session it was ticked in would silently leave the guard off.
                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        // Off on a config that never mentions the key.
                        gui.ClickMainButton("optionsButton");
                        gui.SelectListItemInWindow("Options", "CategoryListBox", "System", 10000);
                        TestAssert.True(
                            !gui.GetCheckBoxStateInWindow("Options", "RepetitionGuardCheckBox", 10000),
                            "unchecked on a fresh config");
                        gui.ClickButtonInWindow("Options", "optionsCancelButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);

                        // Reopening Options starts on the first category, so the box
                        // only exists to the driver once the System page is shown.
                        gui.ClickMainButton("optionsButton");
                        gui.SelectListItemInWindow("Options", "CategoryListBox", "System", 10000);
                        TestAssert.True(
                            gui.SetCheckBoxInWindow("Options", "RepetitionGuardCheckBox", true, 10000),
                            "ticked");
                        gui.ClickButtonInWindow("Options", "optionsOkButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                        TestAssert.Equal("1", new ConfigHandler(iniPath).GetConfigValue("repetitionguard"),
                            "saved as enabled");
                    }

                    // And it is still ticked when the app is started again.
                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.ClickMainButton("optionsButton");
                        gui.SelectListItemInWindow("Options", "CategoryListBox", "System", 10000);
                        TestAssert.True(
                            gui.GetCheckBoxStateInWindow("Options", "RepetitionGuardCheckBox", 10000),
                            "still ticked after a restart");
                        gui.ClickButtonInWindow("Options", "optionsCancelButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                    }

                    // Ticking it back off has to be saved too, otherwise the setting
                    // could only ever be turned on.
                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.ClickMainButton("optionsButton");
                        gui.SelectListItemInWindow("Options", "CategoryListBox", "System", 10000);
                        gui.SetCheckBoxInWindow("Options", "RepetitionGuardCheckBox", false, 10000);
                        gui.ClickButtonInWindow("Options", "optionsOkButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                    }

                    TestAssert.Equal("0", new ConfigHandler(iniPath).GetConfigValue("repetitionguard"),
                        "saved as disabled again");
                }
            });

            TestRunner.Run("gui.colors_dialog_exposes_every_setting", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    WriteGuiIni(ws.Path, server.BaseUrl);

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);
                        gui.ClickMainButton("optionsButton");
                        gui.ClickButtonInWindow("Options", "chooseColorsButton", 15000);

                        // Every configurable color must be reachable in the dialog;
                        // keys used to be dropped silently when a row had no preview.
                        List<string> chooseIds = gui.AutomationIdsInWindow("Colors", "chooseButton_", 10000);
                        TestAssert.Equal(ColorHelper.ColorConfigs.Count, chooseIds.Count,
                            "one Choose button per configurable color");
                        foreach (string key in ColorHelper.ColorConfigs.Keys)
                            TestAssert.True(chooseIds.Contains("chooseButton_" + key),
                                "Colors dialog has a row for " + key);

                        gui.ClickButtonInWindow("Colors", "colorsCancelButton", 10000);
                        gui.WaitForDialogClosed("Colors", 10000);
                        gui.ClickButtonInWindow("Options", "optionsCancelButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                    }
                }
            });

            TestRunner.Run("gui.theme_selection_saves_and_cancel_keeps_it", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageGuiWorkspace())
                {
                    WriteGuiIni(ws.Path, server.BaseUrl);
                    string colorsPath = Path.Combine(ws.Path, "colors.ini");

                    using (GuiDriver gui = new GuiDriver(ws.Path))
                    {
                        gui.WaitUntilReady(30000);

                        gui.ClickMainButton("optionsButton");
                        gui.SelectComboItemInWindow("Options", "themeComboBox", "Solarized Dark", 10000);
                        gui.ClickButtonInWindow("Options", "optionsOkButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                        TestAssert.Equal("solarizeddark", ThemeSetting(colorsPath), "theme saved on OK");

                        // Previewing another theme and cancelling must not persist it.
                        gui.ClickMainButton("optionsButton");
                        gui.SelectComboItemInWindow("Options", "themeComboBox", "Dracula", 10000);
                        gui.ClickButtonInWindow("Options", "optionsCancelButton", 10000);
                        gui.WaitForDialogClosed("Options", 10000);
                        TestAssert.Equal("solarizeddark", ThemeSetting(colorsPath), "theme unchanged after cancel");
                    }
                }
            });
        }

        static string ThemeSetting(string colorsPath)
        {
            var settings = IniFileHandler.LoadIni(colorsPath);
            string theme;
            return settings.TryGetValue("theme", out theme) ? (theme ?? "").Trim() : "";
        }

        static string HexColor(System.Windows.Media.Color color)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
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

        /// <summary>
        /// A session file shaped like the CLI's: one user turn carrying
        /// <paramref name="content"/> plus whatever extra fields the caller needs.
        /// </summary>
        static string SessionJson(string content, string extraFields)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"Version\":1,\"SavedAtUtc\":\"2026-01-01T00:00:00Z\",\"Messages\":[");
            sb.Append("{\"Role\":\"user\",\"Content\":\"" + JsonText(content) + "\"");
            if (extraFields.Length > 0)
                sb.Append(',').Append(extraFields);
            sb.Append('}');
            sb.Append("]}");
            return sb.ToString();
        }

        // The suite runs from Tests-Build\<Suite>\, so the README is a couple of
        // levels up. Walking up beats hardcoding a depth: a run from a different
        // output folder should not turn a documentation gap into a crash.
        static string ReadRepoFile(string name)
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                dir = dir.Parent;
            }

            throw new FileNotFoundException("not found above " + name, name);
        }

        static string JsonText(string text)
        {
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r\n", "\\n")
                .Replace("\r", "\\n")
                .Replace("\n", "\\n");
        }
    }
}
