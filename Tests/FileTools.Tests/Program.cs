using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace FileTools.Tests
{
    internal static class Program
    {
        private const string Exe = "FileTools.exe";

        static int Main(string[] args)
        {
            return SuiteMain.Run("FileTools", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("unknown_tool", () => ToolClient.AssertUnknownTool(Exe));

            using (TempWorkspace ws = new TempWorkspace("filetools"))
            {
                string hello = ws.Combine("hello.txt");
                File.WriteAllText(hello, "hello world", Encoding.UTF8);

                TestRunner.Run("read_file.basic", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "read_file",
                        new JObject { ["filename"] = hello });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "hello world", "content");
                });

                TestRunner.Run("write_file.basic", () =>
                {
                    string path = ws.Combine("written.txt");
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "write_file",
                        new JObject { ["filename"] = path, ["content"] = "abc" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Equal("abc", File.ReadAllText(path, Encoding.UTF8), "disk");
                });

                TestRunner.Run("edit_file.replace", () =>
                {
                    string path = ws.Combine("edit.txt");
                    File.WriteAllText(path, "one two three", Encoding.UTF8);
                    JArray edits = new JArray
                    {
                        new JObject { ["old_string"] = "two", ["new_string"] = "2" }
                    };
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "edit_file",
                        new JObject { ["filename"] = path, ["edits"] = edits });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Equal("one 2 three", File.ReadAllText(path, Encoding.UTF8), "disk");
                });

                TestRunner.Run("edit_file.preserves_crlf_bom", () =>
                {
                    string path = ws.Combine("crlf_bom.txt");
                    File.WriteAllText(path, "line1\r\nline2\r\nline3", new UTF8Encoding(true));
                    JArray edits = new JArray
                    {
                        new JObject { ["old_string"] = "line2", ["new_string"] = "L2" }
                    };
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "edit_file",
                        new JObject { ["filename"] = path, ["edits"] = edits });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    byte[] disk = File.ReadAllBytes(path);
                    TestAssert.True(disk.Length >= 3
                        && disk[0] == 0xEF && disk[1] == 0xBB && disk[2] == 0xBF, "BOM preserved");
                    TestAssert.Equal("line1\r\nL2\r\nline3", File.ReadAllText(path, Encoding.UTF8), "CRLF preserved");
                });

                TestRunner.Run("edit_file.lf_pattern_matches_crlf_file", () =>
                {
                    string path = ws.Combine("lf_match_crlf.txt");
                    File.WriteAllText(path, "alpha\r\nbeta\r\ngamma", Encoding.UTF8);
                    JArray edits = new JArray
                    {
                        new JObject { ["old_string"] = "alpha\nbeta", ["new_string"] = "A\nB" }
                    };
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "edit_file",
                        new JObject { ["filename"] = path, ["edits"] = edits });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Equal("A\r\nB\r\ngamma", File.ReadAllText(path, Encoding.UTF8),
                        "LF pattern applied to CRLF file, CRLF kept");
                });

                TestRunner.Run("edit_file.preserves_lf_no_bom", () =>
                {
                    string path = ws.Combine("lf_no_bom.txt");
                    // .NET Framework Encoding.UTF8 emits a BOM, so opt out explicitly.
                    File.WriteAllText(path, "x1\nx2\nx3", new UTF8Encoding(false));
                    JArray edits = new JArray
                    {
                        new JObject { ["old_string"] = "x2", ["new_string"] = "X2" }
                    };
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "edit_file",
                        new JObject { ["filename"] = path, ["edits"] = edits });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    byte[] disk = File.ReadAllBytes(path);
                    TestAssert.True(disk.Length < 3
                        || !(disk[0] == 0xEF && disk[1] == 0xBB && disk[2] == 0xBF), "no BOM added");
                    TestAssert.Equal("x1\nX2\nx3", File.ReadAllText(path, Encoding.UTF8), "LF preserved");
                });

                TestRunner.Run("copy_move_delete_list", () =>
                {
                    string src = ws.Combine("src.txt");
                    string copy = ws.Combine("copy.txt");
                    string moved = ws.Combine("moved.txt");
                    File.WriteAllText(src, "payload", Encoding.UTF8);

                    ToolInvokeResult c = ToolClient.InvokeProduct(Exe, "copy_file",
                        new JObject { ["source_path"] = src, ["destination_path"] = copy });
                    TestAssert.Equal(0, c.ExitCode, "copy exit");
                    TestAssert.True(File.Exists(copy), "copy exists");

                    ToolInvokeResult m = ToolClient.InvokeProduct(Exe, "move_file",
                        new JObject { ["source_path"] = copy, ["destination_path"] = moved });
                    TestAssert.Equal(0, m.ExitCode, "move exit");
                    TestAssert.True(File.Exists(moved), "moved exists");
                    TestAssert.True(!File.Exists(copy), "copy gone");

                    ToolInvokeResult list = ToolClient.InvokeProduct(Exe, "list_directory",
                        new JObject { ["directory_path"] = ws.Path });
                    TestAssert.Equal(0, list.ExitCode, "list exit");
                    TestAssert.Contains(list.Text, "moved.txt", "list contains moved");

                    ToolInvokeResult d = ToolClient.InvokeProduct(Exe, "delete_file",
                        new JObject { ["file_path"] = moved });
                    TestAssert.Equal(0, d.ExitCode, "delete exit");
                    TestAssert.True(!File.Exists(moved), "deleted");
                });

                TestRunner.Run("read_file.bounded_excerpt", () =>
                {
                    string path = ws.Combine("big.txt");
                    StringBuilder body = new StringBuilder();
                    for (int i = 0; i < 10000; i++)
                        body.Append((char)('a' + (i % 26)));
                    File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "read_file",
                        new JObject { ["filename"] = path, ["offset"] = "100" },
                        new JObject { ["maxFileContentLength"] = "5000" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "File length = 10000 bytes", "full size reported");
                    TestAssert.Contains(r.Text, "reading chars 100-5099", "window header");
                    TestAssert.Contains(r.Text, body.ToString().Substring(100, 5000), "excerpt content");
                    TestAssert.Contains(r.Text, "...[truncated]", "truncation marker");
                    TestAssert.True(r.Text.Length < 6000, "output bounded, got " + r.Text.Length);
                });

                TestRunner.Run("read_file.offset_beyond_eof", () =>
                {
                    string path = ws.Combine("short.txt");
                    File.WriteAllText(path, "short", new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "read_file",
                        new JObject { ["filename"] = path, ["offset"] = "50" });
                    TestAssert.Equal(1, r.ExitCode, "non-zero exit");
                    TestAssert.Contains(r.Text, "exceeds file length", "message");
                });

                TestRunner.Run("read_file.missing", () =>
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "read_file",
                        new JObject { ["filename"] = ws.Combine("nope.txt") });
                    TestAssert.True(r.ExitCode != 0, "non-zero exit");
                });

                TestRunner.Run("extract_file", () =>
                {
                    if (!ToolClient.ProductExists("7za.exe"))
                        TestRunner.Skip("7za.exe not packaged beside test EXE");

                    // Create a tiny zip via PowerShell is heavy; skip creating archive if 7za missing already handled.
                    // Use 7za to create then extract.
                    string archive = ws.Combine("a.zip");
                    string member = ws.Combine("member.txt");
                    File.WriteAllText(member, "zip-body", Encoding.UTF8);
                    ProcessResult pack = ProcessRunner.Run(
                        ToolClient.ProductExe("7za.exe"),
                        "a \"" + archive + "\" \"" + member + "\"",
                        ws.Path, null, 30000);
                    TestAssert.Equal(0, pack.ExitCode, "7za pack");

                    string dest = ws.Combine("extracted");
                    Directory.CreateDirectory(dest);
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "extract_file",
                        new JObject { ["archive_path"] = archive, ["destination_path"] = dest });
                    TestAssert.Equal(0, r.ExitCode, "extract exit");
                });

                TestRunner.Run("grep_search.matches", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    string root = ws.Combine("greproot");
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "a.txt"), "alpha\nneedle here\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "b.md"), "no match in here\n", new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "needle", ["directory_path"] = root });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "a.txt", "matched file reported");
                    TestAssert.Contains(r.Text, "needle here", "matched line reported");
                    TestAssert.True(r.Text.IndexOf("b.md", StringComparison.Ordinal) < 0,
                        "unmatched file must not appear, got: " + r.Text);
                });

                TestRunner.Run("grep_search.file_pattern", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    string root = ws.Combine("grepfilt");
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "keep.cs"), "token-cs\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(root, "skip.txt"), "token-txt\n", new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "token", ["directory_path"] = root, ["file_pattern"] = "*.cs" });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "keep.cs", "included file reported");
                    TestAssert.True(r.Text.IndexOf("skip.txt", StringComparison.Ordinal) < 0,
                        "filtered file must not appear, got: " + r.Text);
                });

                TestRunner.Run("grep_search.case_insensitive", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    string root = ws.Combine("grepcase");
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "c.txt"), "MixedCase Token\n", new UTF8Encoding(false));

                    ToolInvokeResult sensitive = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "mixedcase", ["directory_path"] = root });
                    TestAssert.Contains(sensitive.Text, "No matches found", "case-sensitive default");

                    ToolInvokeResult insensitive = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "mixedcase", ["directory_path"] = root, ["case_insensitive"] = "true" });
                    TestAssert.Equal(0, insensitive.ExitCode, "exit");
                    TestAssert.Contains(insensitive.Text, "MixedCase Token", "case-insensitive match");
                });

                TestRunner.Run("grep_search.regex", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    string root = ws.Combine("grepregex");
                    Directory.CreateDirectory(root);
                    File.WriteAllText(Path.Combine(root, "r.txt"), "value=42\nvalue=7\n", new UTF8Encoding(false));

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "value=[0-9]+", ["directory_path"] = root });
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "value=42", "first match");
                    TestAssert.Contains(r.Text, "value=7", "second match");
                });

                TestRunner.Run("grep_search.no_match_is_success", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "zzz-no-such-token-zzz", ["directory_path"] = ws.Path });
                    TestAssert.Equal(0, r.ExitCode, "no match is not an error");
                    TestAssert.Contains(r.Text, "No matches found", "message");
                });

                TestRunner.Run("grep_search.missing_directory", () =>
                {
                    if (!ToolClient.ProductExists("grep.exe"))
                        TestRunner.Skip("grep.exe not packaged beside test EXE");

                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "grep_search",
                        new JObject { ["pattern"] = "x", ["directory_path"] = ws.Combine("no-such-dir") });
                    TestAssert.Equal(1, r.ExitCode, "non-zero exit");
                    TestAssert.ContainsIgnoreCase(r.Text, "not found", "message");
                });
            }
        }
    }
}
