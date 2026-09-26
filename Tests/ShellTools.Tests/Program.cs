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

                RunCyrillicTests(ws);
            }
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
