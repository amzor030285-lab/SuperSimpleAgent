using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ShellTools.Tests
{
    internal static class Program
    {
        private const string Exe = "ShellTools.exe";

        static int Main(string[] args)
        {
            return SuiteMain.Run("ShellTools", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("unknown_tool", () => ToolClient.AssertUnknownTool(Exe));

            using (TempWorkspace ws = new TempWorkspace("shelltools"))
            {
                TestRunner.Run("echo.hello", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "echo hello" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "hello", "stdout");
                });

                TestRunner.Run("failing.exit_code", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "exit /b 7" });
                    TestAssert.Equal(7, r.ExitCode, "exit code 7");
                    TestAssert.True(r.Stdout.Trim().StartsWith("{"), "json envelope");
                });

                TestRunner.Run("paths.with_spaces", () =>
                {
                    string folder = ws.Combine("my folder");
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, "hello world.txt");
                    File.WriteAllText(file, "spaced-content", Encoding.UTF8);

                    ToolInvokeResult type = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "type \"" + file + "\"" });
                    TestAssert.Equal(0, type.ExitCode, "type exit");
                    TestAssert.Contains(type.Text, "spaced-content", "type content");

                    ToolInvokeResult dir = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "dir /b \"" + folder + "\"" });
                    TestAssert.Equal(0, dir.ExitCode, "dir exit");
                    TestAssert.Contains(dir.Text, "hello world.txt", "dir name");
                });

                TestRunner.Run("cd.spaced_directory", () =>
                {
                    string folder = ws.Combine("path with spaces");
                    Directory.CreateDirectory(folder);
                    // Use `cd` (prints cwd) — `%CD%` is expanded at parse time before `cd /d` runs.
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject
                        {
                            ["command"] = "cd /d \"" + folder + "\" && cd"
                        });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.ContainsIgnoreCase(r.Text.Replace('/', '\\'),
                        folder.Replace('/', '\\'), "cwd");
                });

                TestRunner.Run("stderr.captured", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "echo oops 1>&2" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "oops", "stderr combined into text");
                });

                TestRunner.Run("output.capped_and_offloaded", () =>
                {
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 5000; i++)
                        big.Append("0123456789");
                    string bigPath = ws.Combine("capped-50k.txt");
                    File.WriteAllText(bigPath, big.ToString(), new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "type \"" + bigPath + "\"" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "characters omitted: output exceeded the 20000 character limit", "cap marker");
                    TestAssert.Contains(r.Text, "Full output saved to", "offload hint");

                    const string savedMarker = "Full output saved to ";
                    int start = r.Text.IndexOf(savedMarker, StringComparison.Ordinal);
                    int end = r.Text.IndexOf(" - read it in parts", start, StringComparison.Ordinal);
                    TestAssert.True(start >= 0 && end > start, "offload path present");

                    string offloadPath = r.Text.Substring(start + savedMarker.Length, end - start - savedMarker.Length);
                    try
                    {
                        string offloaded = File.ReadAllText(offloadPath);
                        TestAssert.Equal(big.Length, offloaded.Length, "offloaded char count");
                        TestAssert.Equal(
                            big.ToString().Substring(big.Length - 20),
                            offloaded.Substring(offloaded.Length - 20),
                            "offloaded tail");
                    }
                    finally
                    {
                        try { File.Delete(offloadPath); } catch { }
                    }
                });

                TestRunner.Run("output.read_ceiling", () =>
                {
                    StringBuilder huge = new StringBuilder();
                    for (int i = 0; i < 300000; i++)
                        huge.Append("0123456789");
                    string hugePath = ws.Combine("capped-3m.txt");
                    File.WriteAllText(hugePath, huge.ToString(), new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject { ["command"] = "type \"" + hugePath + "\"" }, null, 30000);
                    TestAssert.True(!r.TimedOut, "not timed out");
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "character read limit", "read ceiling marker");
                    TestAssert.True(r.Text.Length < 30000, "result bounded, chars: " + r.Text.Length);
                    TestAssert.True(!r.Text.Contains("Full output saved to"), "no offload after a truncated read");
                });

                TestRunner.Run("missing.command", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject());
                    TestAssert.True(r.ExitCode != 0, "non-zero");
                    TestAssert.ContainsIgnoreCase(r.Text ?? r.Stdout, "missing", "error");
                });

                TestRunner.Run("gui.launch_notepad", () =>
                {
                    // Snapshot existing notepad PIDs so we only kill ones we start.
                    System.Collections.Generic.HashSet<int> before = SnapshotPids("notepad");

                    // Open a real file rather than a blank document: the window flashes on
                    // the test author's desktop either way, and a marker makes it obvious
                    // that it is a test artifact instead of a stray editor session.
                    string marker = ws.Combine("launch-notepad-marker.txt");
                    File.WriteAllText(marker,
                        "This window was opened by the ShellTools test suite and is closed again automatically.\r\n",
                        new UTF8Encoding(false));

                    // Use cmd's built-in `start` so this remains compatible with XP, where
                    // PowerShell is not installed by default. Redirect its standard streams
                    // to NUL; ToolHelper bounds draining of any other inherited pipe handles.
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                        new JObject
                        {
                            ["command"] = "start \"\" notepad.exe \"" + marker + "\" >nul 2>&1"
                        },
                        null,
                        15000);

                    try
                    {
                        if (r.TimedOut)
                            throw new TestFailureException(
                                "ShellTools.exe still running after launching notepad; kill issued.");

                        TestAssert.Equal(0, r.ExitCode, "start exit");

                        int newPid = WaitForNewProcess("notepad", before, 10000);
                        TestAssert.True(newPid > 0, "notepad appeared");
                    }
                    finally
                    {
                        KillNewProcesses("notepad", before);
                    }
                });

                RunEncodingTests(ws);
                RunCyrillicTests(ws);
            }

            RunDecoderUnitTests();
        }

        // The decoder decides what the agent reads, so it is checked directly on the
        // exact byte sequences a console, a UTF-8 program and a Windows-1251 file
        // produce. No cmd.exe involved, so these hold on any Windows, in any locale.
        static void RunDecoderUnitTests()
        {
            const string cyrillic = "привет ёжик";

            TestRunner.Run("decoder.utf8", () =>
            {
                byte[] utf8 = new UTF8Encoding(false).GetBytes(cyrillic);
                TestAssert.Equal(cyrillic, ToolHelper.DecodeProcessOutput(utf8, "auto"),
                    "valid UTF-8 is detected as UTF-8");
            });

            TestRunner.Run("decoder.utf8_bom", () =>
            {
                byte[] body = new UTF8Encoding(false).GetBytes(cyrillic);
                byte[] withBom = new byte[body.Length + 3];
                withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
                Buffer.BlockCopy(body, 0, withBom, 3, body.Length);

                string decoded = ToolHelper.DecodeProcessOutput(withBom, "auto");
                TestAssert.True(decoded.IndexOf('\uFEFF') < 0,
                    "the BOM does not survive as U+FEFF\n      got: " + Describe(decoded));
                TestAssert.Equal(cyrillic, decoded, "text after the BOM");
            });

            TestRunner.Run("decoder.explicit_overrides_detection", () =>
            {
                byte[] win1251 = Encoding.GetEncoding(1251).GetBytes(cyrillic);
                TestAssert.Equal(cyrillic, ToolHelper.DecodeProcessOutput(win1251, "1251"),
                    "a named code page wins over detection");
                TestAssert.Equal(cyrillic, ToolHelper.DecodeProcessOutput(win1251, "1251"),
                    "and is stable when repeated");
            });

            TestRunner.Run("decoder.ascii", () =>
            {
                byte[] ascii = Encoding.ASCII.GetBytes("plain ascii output");
                TestAssert.Equal("plain ascii output", ToolHelper.DecodeProcessOutput(ascii, "auto"),
                    "ASCII is never mangled");
            });

            TestRunner.Run("decoder.empty", () =>
            {
                TestAssert.Equal("", ToolHelper.DecodeProcessOutput(new byte[0], "auto"), "empty input");
                TestAssert.Equal("", ToolHelper.DecodeProcessOutput(null, "auto"), "null input");
            });

            TestRunner.Run("decoder.console_page", () =>
            {
                Encoding oem;
                try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { TestRunner.Skip("no console OEM code page"); return; }

                if (oem.CodePage == 65001)
                {
                    TestRunner.Skip("console is already UTF-8");
                    return;
                }

                byte[] oemBytes = oem.GetBytes(cyrillic);
                TestAssert.Equal(cyrillic, ToolHelper.DecodeProcessOutput(oemBytes, "auto"),
                    "console code page " + oem.CodePage + " output is detected, not mangled");
            });

            TestRunner.Run("decoder.ansi_page", () =>
            {
                // On a Russian Windows the system ANSI page is CP1251 while the
                // console page is CP866: C-stdio utilities (reg, ipconfig) print
                // ANSI bytes into a pipe. Auto detection must pick 1251 by content,
                // not fall back to the console page.
                int ansiCodePage;
                try { ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
                catch { TestRunner.Skip("no system ANSI code page"); return; }

                if (ansiCodePage == 65001)
                {
                    TestRunner.Skip("ANSI is already UTF-8");
                    return;
                }

                Encoding ansi;
                try { ansi = Encoding.GetEncoding(ansiCodePage); }
                catch { TestRunner.Skip("ANSI code page " + ansiCodePage + " unavailable"); return; }

                if (ansi.GetString(ansi.GetBytes(cyrillic)) != cyrillic)
                {
                    TestRunner.Skip("ANSI code page " + ansiCodePage + " cannot represent Cyrillic");
                    return;
                }

                try
                {
                    if (ansiCodePage == CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                        TestRunner.Skip("ANSI and OEM are the same page here, covered by decoder.console_page");
                }
                catch { }

                byte[] ansiBytes = ansi.GetBytes(cyrillic);
                TestAssert.Equal(cyrillic, ToolHelper.DecodeProcessOutput(ansiBytes, "auto"),
                    "ANSI code page " + ansiCodePage + " output is detected, not mangled");
            });

            TestRunner.Run("decoder.oem_tasklist_header", () =>
            {
                // Regression for T06 (tasklist on a Russian XP): console (CP866)
                // bytes whose CP1251 decode is also full of Cyrillic letters. The
                // old letter-count scorer let the shifted CP1251 decode out-score
                // the real one; the detector must keep CP866. First 160 bytes of
                // the output: the header line, the separator and the first row.
                byte[] oemTasklist = new byte[]
                {
                    0x0D, 0x0A, 0x88, 0xAC, 0xEF, 0x20, 0xAE, 0xA1, 0xE0, 0xA0, 0xA7, 0xA0, 0x20, 0x20, 0x20, 0x20,
                    0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x50,
                    0x49, 0x44, 0x20, 0x88, 0xAC, 0xEF, 0x20, 0xE1, 0xA5, 0xE1, 0xE1, 0xA8, 0xA8, 0x20, 0x20, 0x20,
                    0x20, 0x20, 0x20, 0x20, 0xFC, 0x20, 0xE1, 0xA5, 0xA0, 0xAD, 0xE1, 0xA0, 0x20, 0x20, 0x20, 0x20,
                    0x20, 0x20, 0x20, 0x8F, 0xA0, 0xAC, 0xEF, 0xE2, 0xEC, 0x0D, 0x0A, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
                    0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
                    0x3D, 0x3D, 0x3D, 0x3D, 0x20, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x20, 0x3D, 0x3D, 0x3D, 0x3D,
                    0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x20, 0x3D, 0x3D, 0x3D,
                    0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x20, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D, 0x3D,
                    0x3D, 0x3D, 0x0D, 0x0A, 0x53, 0x79, 0x73, 0x74, 0x65, 0x6D, 0x20, 0x49, 0x64, 0x6C, 0x65, 0x20
                };
                string expected = Encoding.GetEncoding(866).GetString(oemTasklist);
                TestAssert.Equal(expected, ToolHelper.DecodeProcessOutput(oemTasklist, "auto"),
                    "CP866 console output is not misdetected as CP1251");
            });
        }

        // Renders text with the code points that actually matter for a decoding
        // failure: a BOM, a replacement char, or a non-ASCII letter.
        static string Describe(string text)
        {
            if (text == null)
                return "<null>";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (c == '\r' || c == '\n' || c == '\t')
                    continue;
                sb.Append(c < 32 || c > 126 ? "\\u" + ((int)c).ToString("X4") : c.ToString());
            }
            return sb.ToString();
        }

        // "type" copies a file's bytes to stdout unchanged, so these tests decide the
        // decoding question without depending on what the console code page is: the
        // file holds the exact bytes a program would have printed. The UTF-8 case used
        // to come back as mojibake on any machine whose console page was not UTF-8,
        // and on an English XP the Cyrillic tests above skip entirely - which is the
        // whole reason Russian output went wrong there.
        static void RunEncodingTests(TempWorkspace ws)
        {
            const string cyrillic = "привет ёжик";

            TestRunner.Run("encoding.utf8_detected", () =>
            {
                string file = ws.Combine("utf8.txt");
                File.WriteAllText(file, cyrillic + "\n", new UTF8Encoding(false));

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "type \"" + file + "\"" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, cyrillic,
                    "UTF-8 bytes are detected and decoded, not read as the console code page"
                    + "\n      got: " + Describe(r.Text));
                TestAssert.True((r.Text ?? "").IndexOf('\uFFFD') < 0, "no U+FFFD replacement chars");
            });

            TestRunner.Run("encoding.utf8_bom_stripped", () =>
            {
                // With a BOM the bytes are still valid UTF-8, but the BOM must not
                // survive as U+FEFF ahead of the text.
                string file = ws.Combine("utf8bom.txt");
                File.WriteAllText(file, cyrillic + "\n", new UTF8Encoding(true));

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "type \"" + file + "\"" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.True((r.Text ?? "").IndexOf('\uFEFF') < 0,
                    "no U+FEFF left in the text\n      got: " + Describe(r.Text));
                TestAssert.Contains(r.Text, cyrillic, "text after the BOM\n      got: " + Describe(r.Text));
            });

            TestRunner.Run("encoding.explicit_1251", () =>
            {
                // Windows-1251 bytes are not valid UTF-8, so detection cannot tell them
                // apart from the console page on every machine; the argument has to win.
                string file = ws.Combine("win1251.txt");
                Encoding win1251 = Encoding.GetEncoding(1251);
                File.WriteAllBytes(file, win1251.GetBytes(cyrillic + "\n"));

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "type \"" + file + "\"", ["encoding"] = "1251" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, cyrillic, "explicit encoding argument is honored");
            });

            TestRunner.Run("encoding.ansi_detected", () =>
            {
                // The exact failure found on a Russian XP: reg.exe and ipconfig
                // print ANSI (CP1251) bytes when redirected, and the console page
                // is CP866. The file holds those ANSI bytes; no encoding argument
                // is given, so detection must choose the ANSI page by content.
                int ansiCodePage;
                try { ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
                catch { TestRunner.Skip("no system ANSI code page"); return; }

                if (ansiCodePage == 65001)
                {
                    TestRunner.Skip("ANSI is already UTF-8");
                    return;
                }

                Encoding ansi;
                try { ansi = Encoding.GetEncoding(ansiCodePage); }
                catch { TestRunner.Skip("ANSI code page " + ansiCodePage + " unavailable"); return; }

                if (ansi.GetString(ansi.GetBytes(cyrillic)) != cyrillic)
                {
                    TestRunner.Skip("ANSI code page " + ansiCodePage + " cannot represent Cyrillic");
                    return;
                }

                try
                {
                    if (ansiCodePage == CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                        TestRunner.Skip("ANSI and OEM are the same page here, covered by encoding.console_page_preserved");
                }
                catch { }

                string file = ws.Combine("ansi.txt");
                File.WriteAllBytes(file, ansi.GetBytes(cyrillic + "\n"));

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "type \"" + file + "\"" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, cyrillic,
                    "ANSI code page " + ansiCodePage + " output decodes without an explicit encoding");
            });

            TestRunner.Run("encoding.console_page_preserved", () =>
            {
                // The existing behaviour must survive: a Russian console prints 866 and a
                // command that emits those bytes still has to read back correctly.
                Encoding oem;
                try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { oem = Encoding.UTF8; }

                if (oem.GetBytes(cyrillic).Length == 0 || oem.CodePage == 65001)
                {
                    TestRunner.Skip("console code page " + oem.CodePage + " is not a single-byte Cyrillic page");
                    return;
                }

                string file = ws.Combine("oem.txt");
                File.WriteAllBytes(file, oem.GetBytes(cyrillic + "\n"));

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "type \"" + file + "\"" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, cyrillic,
                    "console code page " + oem.CodePage + " output still decodes correctly");
            });

            TestRunner.Run("encoding.ascii_untouched", () =>
            {
                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "echo plain ascii output" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, "plain ascii output", "ascii path unaffected");
            });
        }

        static void RunCyrillicTests(TempWorkspace ws)
        {
            Encoding oem;
            try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { oem = Encoding.UTF8; }

            const string probe = "привет";
            bool oemHasCyrillic;
            try { oemHasCyrillic = oem.GetString(oem.GetBytes(probe)) == probe; }
            catch { oemHasCyrillic = false; }

            if (!oemHasCyrillic)
            {
                TestRunner.Skip("OEM code page " + oem.CodePage + " cannot represent Cyrillic");
                return;
            }

            TestRunner.Run("cyrillic.echo", () =>
            {
                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "echo привет ёжик" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, "привет", "cyrillic echo");
            });

            TestRunner.Run("cyrillic.dir_name", () =>
            {
                string file = ws.Combine("тест_список.txt");
                File.WriteAllText(file, "x", Encoding.UTF8);

                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "dir /b \"" + ws.Path + "\"" });
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.Contains(r.Text, "тест_список.txt", "cyrillic dir entry");
            });

            TestRunner.Run("cyrillic.stderr_clean", () =>
            {
                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_shell_command",
                    new JObject { ["command"] = "definitely_not_a_real_command_zzz" });
                TestAssert.True(r.ExitCode != 0, "non-zero");
                TestAssert.True((r.Text ?? "").IndexOf('\uFFFD') < 0, "no U+FFFD replacement chars");
            });
        }

        static System.Collections.Generic.HashSet<int> SnapshotPids(string name)
        {
            var set = new System.Collections.Generic.HashSet<int>();
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try { set.Add(p.Id); } catch { }
                try { p.Dispose(); } catch { }
            }
            return set;
        }

        static int WaitForNewProcess(string name, System.Collections.Generic.HashSet<int> before, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (!before.Contains(p.Id))
                            return p.Id;
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                Thread.Sleep(100);
            }
            return -1;
        }

        static void KillNewProcesses(string name, System.Collections.Generic.HashSet<int> before)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!before.Contains(p.Id))
                        p.Kill();
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }
}
