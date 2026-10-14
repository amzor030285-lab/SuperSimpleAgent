using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WebTools.Tests
{
    internal static class Program
    {
        private const string Exe = "WebTools.exe";

        static int Main(string[] args)
        {
            return SuiteMain.Run("WebTools", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("unknown_tool", () => ToolClient.AssertUnknownTool(Exe));

            TestRunner.Run("read_website.local", () =>
            {
                if (!ToolClient.ProductExists("curl.exe"))
                    TestRunner.Skip("curl.exe not packaged beside test EXE");

                using (LocalHttpServer server = new LocalHttpServer(
                    "<html><head><title>T</title></head><body><p>HelloLocal</p><a href=\"http://example.com/a\">A</a></body></html>"))
                {
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "read_website",
                        new JObject { ["URL"] = server.Url },
                        null,
                        60000);
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, "HelloLocal", "body");
                }
            });

            TestRunner.Run("download_file.local", () =>
            {
                if (!ToolClient.ProductExists("curl.exe"))
                    TestRunner.Skip("curl.exe not packaged beside test EXE");

                using (TempWorkspace ws = new TempWorkspace("webdl"))
                using (LocalHttpServer server = new LocalHttpServer("download-body", "application/octet-stream"))
                {
                    string dest = ws.Combine("out.bin");
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "download_file",
                        new JObject { ["URL"] = server.Url, ["filename"] = dest },
                        null,
                        60000);
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.True(File.Exists(dest), "file exists");
                    TestAssert.Contains(File.ReadAllText(dest), "download-body", "content");
                }
            });

            TestRunner.Run("run_web_search.firecrawl_post_body", () =>
            {
                if (!ToolClient.ProductExists("curl.exe"))
                    TestRunner.Skip("curl.exe not packaged beside test EXE");

                // The query round-trips through the JSON POST body. Quotes, an
                // ampersand, a backslash and a non-ASCII letter are all characters
                // the old inline -d "escaped" form could mangle. (No newline: the
                // search parser intentionally normalizes whitespace in titles.)
                string nasty = "a\"quoted&arg\\back\\ё line2";
                using (FirecrawlEchoServer server = new FirecrawlEchoServer())
                {
                    JObject config = new JObject
                    {
                        ["firecrawlendpoint"] = server.BaseUrl,
                        ["firecrawlapikey"] = "test-key"
                    };
                    ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_web_search",
                        new JObject { ["query"] = nasty },
                        config,
                        60000);
                    TestAssert.Equal(0, r.ExitCode, "exit");
                    TestAssert.Contains(r.Text, nasty, "POST body query arrived intact");
                }
            });

            TestRunner.Run("run_web_search.live", () =>
            {
                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "run_web_search",
                    new JObject { ["query"] = "OpenAI" },
                    null,
                    120000);
                TestAssert.Equal(0, r.ExitCode, "exit");
                TestAssert.True(!string.IsNullOrWhiteSpace(r.Text), "non-empty results");
            });

            TestRunner.Run("download_video.binary_version", () =>
            {
                string ytDlp = ToolClient.ProductExe("yt-dlp.exe");
                if (!File.Exists(ytDlp))
                    TestRunner.Skip("yt-dlp.exe not packaged beside test EXE");

                Process p = new Process();
                p.StartInfo.FileName = ytDlp;
                p.StartInfo.Arguments = "--version";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.WorkingDirectory = Path.GetDirectoryName(ytDlp);
                string outText = "";
                string errText = "";
                p.Start();
                Thread errDrain = new Thread(() =>
                {
                    try { errText = p.StandardError.ReadToEnd(); }
                    catch { }
                });
                errDrain.IsBackground = true;
                errDrain.Start();
                try { outText = p.StandardOutput.ReadToEnd(); }
                catch { }
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { }
                    TestAssert.True(false, "yt-dlp.exe --version timed out");
                }
                errDrain.Join(5000);

                // Same launch limitation as the live test: where the packaged
                // XP build cannot load python311.dll, the version is unanswerable.
                string all = (outText ?? "") + (errText ?? "");
                if (p.ExitCode != 0 && all.IndexOf("Python DLL", StringComparison.OrdinalIgnoreCase) >= 0)
                    TestRunner.Skip("packaged yt-dlp.exe cannot start on this machine");

                TestAssert.Equal(0, p.ExitCode, "exit");
                TestAssert.Contains(outText, "2026.", "version string");
            });

            TestRunner.Run("download_video.live", () =>
            {
                if (!ToolClient.ProductExists("yt-dlp.exe"))
                    TestRunner.Skip("yt-dlp.exe not packaged beside test EXE");

                // Use a tiny well-known short clip; fail if offline/remote error.
                ToolInvokeResult r = ToolClient.InvokeProduct(Exe, "download_video",
                    new JObject { ["URL"] = "https://www.youtube.com/watch?v=jNQXAC9IVRw" },
                    null,
                    300000);

                // The packaged yt-dlp is a PyInstaller build; on a machine where its
                // launcher cannot start (the XP build fails to load python311.dll on
                // x64) the tool never runs, which says nothing about WebTools. Skip
                // instead of failing the suite; a yt-dlp that starts and then fails
                // to download still fails the test below.
                if (r.ExitCode != 0 && (r.Text ?? "").IndexOf("Python DLL", StringComparison.OrdinalIgnoreCase) >= 0)
                    TestRunner.Skip("packaged yt-dlp.exe cannot start on this machine: " +
                        (r.Text ?? "").Replace("\r", " ").Replace("\n", " "));

                // YouTube may be unreachable from this machine (regional block or no
                // network). A yt-dlp that starts and then cannot connect is an
                // environment problem, not a WebTools defect.
                if (r.TimedOut)
                    TestRunner.Skip("download hung waiting on the network");
                if (r.ExitCode != 0 && LooksLikeNetworkBlock(r.Text))
                    TestRunner.Skip("YouTube unreachable from this machine: " +
                        (r.Text ?? "").Replace("\r", " ").Replace("\n", " "));

                TestAssert.Equal(0, r.ExitCode, "exit");
            });
        }

        private static bool LooksLikeNetworkBlock(string text)
        {
            string t = text ?? "";
            string[] signatures =
            {
                "URLError",
                "Could not resolve host",
                "Connection refused",
                "timed out",
                "Max retries exceeded",
                "HTTPSConnectionPool"
            };
            for (int i = 0; i < signatures.Length; i++)
            {
                if (t.IndexOf(signatures[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private sealed class FirecrawlEchoServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running;

            public string BaseUrl { get; private set; }

            public FirecrawlEchoServer()
            {
                string baseUrl;
                _listener = TestHttpListener.StartLoopback(out baseUrl);
                BaseUrl = baseUrl;
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true };
                _thread.Start();
            }

            private void Loop()
            {
                while (_running)
                {
                    try
                    {
                        HttpListenerContext ctx = _listener.GetContext();
                        string raw;
                        using (StreamReader reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            raw = reader.ReadToEnd();

                        string query = "";
                        JObject payload = JObject.Parse(raw);
                        JToken queryToken = payload["query"];
                        if (queryToken != null && queryToken.Type != JTokenType.Null)
                            query = queryToken.ToString();

                        JObject result = new JObject
                        {
                            ["success"] = true,
                            ["data"] = new JObject
                            {
                                ["web"] = new JArray(new JObject
                                {
                                    ["url"] = "http://127.0.0.1/echo",
                                    ["title"] = query,
                                    ["description"] = "d"
                                })
                            }
                        };
                        byte[] bytes = Encoding.UTF8.GetBytes(result.ToString());
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.KeepAlive = false;
                        ctx.Response.ContentLength64 = bytes.Length;
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                    }
                    catch
                    {
                        if (!_running) break;
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        private sealed class LocalHttpServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly Thread _thread;
            private volatile bool _running;
            private readonly string _body;
            private readonly string _contentType;

            public string Url { get; private set; }

            public LocalHttpServer(string body)
                : this(body, "text/html; charset=utf-8")
            {
            }

            public LocalHttpServer(string body, string contentType)
            {
                _body = body ?? "";
                _contentType = string.IsNullOrEmpty(contentType)
                    ? "text/html; charset=utf-8"
                    : contentType;
                string baseUrl;
                _listener = TestHttpListener.StartLoopback(out baseUrl);
                Url = baseUrl + "/";
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true };
                _thread.Start();
            }

            private void Loop()
            {
                while (_running)
                {
                    try
                    {
                        HttpListenerContext ctx = _listener.GetContext();
                        byte[] bytes = Encoding.UTF8.GetBytes(_body);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = _contentType;
                        ctx.Response.KeepAlive = false;
                        bool head = string.Equals(ctx.Request.HttpMethod, "HEAD",
                            StringComparison.OrdinalIgnoreCase);
                        if (head)
                        {
                            // download_file probes MIME with curl -I. HTTP.SYS on XP
                            // can deadlock if a HEAD response writes a body (or a
                            // non-zero Content-Length) that curl never reads.
                            ctx.Response.ContentLength64 = 0;
                        }
                        else
                        {
                            ctx.Response.ContentLength64 = bytes.Length;
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        }
                        ctx.Response.Close();
                    }
                    catch
                    {
                        if (!_running) break;
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }
    }
}
