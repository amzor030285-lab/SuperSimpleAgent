using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SearchTools.Tests
{
    /// <summary>
    /// Stands in for Everything's HTTP server. Everything itself is never required
    /// by the suite: this listener answers on a loopback port the tool is then
    /// pointed at, so every branch of the tool (results, empty page, foreign port,
    /// stall, garbage body) is reachable on a machine with no Everything installed.
    /// </summary>
    internal sealed class FakeEverythingServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _thread;
        private volatile bool _running;

        public int Port { get; private set; }

        /// <summary>Body returned for the next request. Set before each invoke.</summary>
        public string NextBody { get; set; }

        public int NextStatus { get; set; }

        /// <summary>When true, accept the connection and never answer.</summary>
        public bool Stall { get; set; }

        /// <summary>Everything query strings seen so far, in order.</summary>
        public List<string> ReceivedSearches { get; private set; }

        /// <summary>Value of the count= parameter seen so far, in order.</summary>
        public List<string> ReceivedCounts { get; private set; }

        public FakeEverythingServer()
        {
            ReceivedSearches = new List<string>();
            ReceivedCounts = new List<string>();
            NextBody = "{\"totalResults\":0,\"results\":[]}";
            NextStatus = 200;

            string baseUrl;
            _listener = TestHttpListener.StartLoopback(out baseUrl);
            Port = new Uri(baseUrl).Port;
            _running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true };
            _thread.Start();
            TestLog.Detail("FakeEverythingServer listening at " + baseUrl);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    if (!_running) break;
                    continue;
                }

                try
                {
                    Handle(ctx);
                }
                catch (Exception ex)
                {
                    TestLog.Detail("FakeEverythingServer error: " + ex.Message);
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string search = QueryParam(ctx, "search");
            string count = QueryParam(ctx, "count");
            string offset = QueryParam(ctx, "offset");

            lock (ReceivedSearches)
            {
                ReceivedSearches.Add(search);
                ReceivedCounts.Add(count);
            }

            string path = ctx.Request.Url.AbsolutePath ?? "";
            TestLog.Detail("FakeEverything GET " + path + " search=" +
                search + " count=" + count + " offset=" + offset);

            if (Stall)
            {
                Thread.Sleep(10000);
                try { ctx.Response.Abort(); } catch { }
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(NextBody ?? "");
            ctx.Response.StatusCode = NextStatus;
            ctx.Response.ContentType = NextStatus == 200 ? "application/json" : "text/html";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        /// <summary>
        /// Reads a query parameter from the raw URL, percent-decoding it as
        /// UTF-8. ctx.Request.QueryString decodes with the system ANSI code
        /// page (cp1251 on this box), which mangles the UTF-8 percent-encoding
        /// that a real HTTP server (and Everything itself) would decode
        /// correctly.
        /// </summary>
        static string QueryParam(HttpListenerContext ctx, string name)
        {
            string rawUrl = ctx.Request.RawUrl ?? "";
            int queryStart = rawUrl.IndexOf('?');
            if (queryStart < 0 || queryStart + 1 >= rawUrl.Length)
            {
                return "";
            }

            foreach (string pair in rawUrl.Substring(queryStart + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }
                if (Uri.UnescapeDataString(pair.Substring(0, eq)) == name)
                {
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            return "";
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _thread.Join(2000); } catch { }
        }
    }

    internal static class Program
    {
        const string ToolExe = "SearchTools.exe";

        static int Main(string[] args)
        {
            return SuiteMain.Run("SearchTools", args, RunTests);
        }

        static void RunTests()
        {
            TestRunner.Run("search.returns_indexed_results", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    // date_modified is a FILETIME: 133000000000000000 == 2022-06-18 04:26:40 UTC.
                    server.NextBody =
                        "{\"totalResults\":2,\"results\":[" +
                        "{\"type\":\"file\",\"name\":\"notes.md\",\"path\":\"C:\\\\docs\",\"size\":\"1536\",\"date_modified\":\"133000000000000000\"}," +
                        "{\"type\":\"folder\",\"name\":\"projects\",\"path\":\"C:\\\\work\",\"size\":\"\",\"date_modified\":\"0\"}]}";

                    ToolInvokeResult r = Invoke(server, new JObject { { "query", "notes" } });

                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "[showing 2 of 2 total]", "header carries the total");
                    TestAssert.Contains(r.Text, "notes.md -> C:\\docs (1.50 KB, 2022-06-18 04:26)", "file line");
                    TestAssert.Contains(r.Text, "projects -> C:\\work (folder, unknown date)", "folder line names the type, not a missing size");
                    TestAssert.True(!r.Text.Contains("More results available"),
                        "no pagination hint when everything fits");
                    TestAssert.Equal("notes", LastSearch(server), "query is forwarded verbatim");
                }
            });

            TestRunner.Run("search.pagination_hint", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody =
                        "{\"totalResults\":500,\"results\":[" +
                        "{\"type\":\"file\",\"name\":\"a.log\",\"path\":\"C:\\\\a\",\"size\":\"10\",\"date_modified\":\"133000000000000000\"}," +
                        "{\"type\":\"file\",\"name\":\"b.log\",\"path\":\"C:\\\\b\",\"size\":\"10\",\"date_modified\":\"133000000000000000\"}]}";

                    ToolInvokeResult r = Invoke(server, new JObject
                    {
                        { "query", "log" },
                        { "max_results", 2 },
                        { "offset", 4 }
                    });

                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "[showing 2 of 500 total]", "header");
                    TestAssert.Contains(r.Text, "More results available (494 remaining)", "remaining count");
                    TestAssert.Contains(r.Text, "offset=6", "next offset accounts for the one requested");
                }
            });

            TestRunner.Run("search.query_is_url_escaped", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody = "{\"totalResults\":0,\"results\":[]}";

                    // A space and a non-ASCII letter must survive the round trip
                    // instead of being sent raw and breaking the request line.
                    ToolInvokeResult r = Invoke(server, new JObject { { "query", "ext:md size:large" } });
                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    TestAssert.Equal("ext:md size:large", LastSearch(server), "spaces survive escaping");

                    r = Invoke(server, new JObject { { "query", "Отчет" } });
                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    TestAssert.Equal("Отчет", LastSearch(server), "non-ASCII survives escaping");
                }
            });

            TestRunner.Run("search.no_match_is_not_an_error", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody = "{\"totalResults\":0,\"results\":[]}";

                    ToolInvokeResult r = Invoke(server, new JObject { { "query", "nothinghere" } });

                    TestAssert.Equal(0, r.ExitCode, "an empty page is a successful search");
                    TestAssert.Contains(r.Text, "No files found for query: nothinghere", "plain message");
                    TestAssert.True(!r.Text.Contains("Everything is not running"),
                        "a working server must not be reported as down");
                    TestAssert.True(!r.Text.Contains("HTTP Server"),
                        "no server setup advice when the server answered");
                }
            });

            TestRunner.Run("search.path_like_query_gets_a_hint", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody = "{\"totalResults\":0,\"results\":[]}";

                    ToolInvokeResult r = Invoke(server, new JObject { { "query", @"C:\Users\file.txt" } });

                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "looks like a file path", "path hint");
                    TestAssert.Contains(r.Text, "use 'file.txt'", "path hint example");
                }
            });

            TestRunner.Run("search.server_down_explains_the_fix", () =>
            {
                int deadPort = ClosedPort();

                ToolInvokeResult r = Invoke(new JObject { { "query", "anything" } }, deadPort, 5);

                TestAssert.Equal(1, r.ExitCode, "failure exit code");
                TestAssert.Contains(r.Text, "Everything is not running", "says what is wrong");
                TestAssert.Contains(r.Text, "Enable HTTP server", "names the setting to tick");
                TestAssert.Contains(r.Text, "do not report a file as missing", "warns against a wrong conclusion");
                TestAssert.Contains(r.Text, deadPort.ToString(CultureInfo.InvariantCulture), "names the port it tried");
            });

            TestRunner.Run("search.timeout_is_reported", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.Stall = true;

                    ToolInvokeResult r = Invoke(new JObject { { "query", "anything" } }, server.Port, 1);

                    TestAssert.Equal(1, r.ExitCode, "failure exit code");
                    TestAssert.Contains(r.Text, "did not answer in time", "timeout message");
                    TestAssert.True(!r.Text.Contains("Enable HTTP server"),
                        "a timeout must not be reported as a missing server");
                }
            });

            TestRunner.Run("search.foreign_server_on_port", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextStatus = 200;
                    server.NextBody = "<html><body>It works! This is the default IIS page.</body></html>";

                    ToolInvokeResult r = Invoke(new JObject { { "query", "anything" } }, server.Port, 5);

                    TestAssert.Equal(1, r.ExitCode, "failure exit code");
                    TestAssert.Contains(r.Text, "not Everything's JSON", "names the real problem");
                    TestAssert.Contains(r.Text, "another program already uses that port", "says why");
                    TestAssert.Contains(r.Text, "It works!", "quotes what came back");
                    TestAssert.True(!r.Text.Contains("Everything is not running"),
                        "a busy port is not a stopped Everything");
                }
            });

            TestRunner.Run("search.rejects_overly_broad_query", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    ToolInvokeResult r = Invoke(server, new JObject { { "query", "*" } });

                    TestAssert.Equal(1, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "too broad", "bare star refused");
                    TestAssert.Equal(0, server.ReceivedSearches.Count, "nothing is sent for a refused query");

                    r = Invoke(server, new JObject { { "query", " a " } });
                    TestAssert.Equal(1, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "too broad", "single letter refused");

                    // A blank query is trimmed away by the shared argument
                    // helper before it reaches the query validator.
                    r = Invoke(server, new JObject { { "query", "  " } });
                    TestAssert.Equal(1, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "missing 'query' argument", "blank refused");
                }
            });

            TestRunner.Run("search.rejects_overlong_query", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    StringBuilder query = new StringBuilder();
                    for (int i = 0; i < 101; i++) query.Append('a');

                    ToolInvokeResult r = Invoke(server, new JObject { { "query", query.ToString() } });

                    TestAssert.Equal(1, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "too long (101 chars)", "length is reported");
                    TestAssert.Contains(r.Text, "Max allowed is 100", "limit is stated");
                    TestAssert.Equal(0, server.ReceivedSearches.Count, "nothing is sent for a refused query");
                }
            });

            TestRunner.Run("search.caps_page_size", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody =
                        "{\"totalResults\":1000,\"results\":[" +
                        "{\"type\":\"file\",\"name\":\"x.log\",\"path\":\"C:\\\\x\",\"size\":\"1\",\"date_modified\":\"133000000000000000\"}]}";

                    ToolInvokeResult r = Invoke(server, new JObject
                    {
                        { "query", "log" },
                        { "max_results", 5000 }
                    });

                    TestAssert.Equal(0, r.ExitCode, "exit code");
                    // count is capped at 100 before it reaches the server.
                    TestAssert.Equal("100", LastCount(server), "page size is capped");
                }
            });

            TestRunner.Run("search.negative_offset_refused", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    ToolInvokeResult r = Invoke(server, new JObject
                    {
                        { "query", "log" },
                        { "offset", -1 }
                    });

                    TestAssert.Equal(1, r.ExitCode, "exit code");
                    TestAssert.Contains(r.Text, "offset must be >= 0", "message");
                    TestAssert.Equal(0, server.ReceivedSearches.Count, "nothing is sent");
                }
            });

            TestRunner.Run("search.port_option_overrides_default", () =>
            {
                using (FakeEverythingServer server = new FakeEverythingServer())
                {
                    server.NextBody = "{\"totalResults\":0,\"results\":[]}";

                    // The default port is not reachable in the test environment, so
                    // a hit on the configured port proves the option is honoured.
                    ToolInvokeResult r = Invoke(new JObject { { "query", "anything" } }, server.Port, 5);

                    TestAssert.Equal(0, r.ExitCode, "the configured port is used");
                    TestAssert.Contains(r.Text, "No files found", "server answered");
                }
            });

            TestRunner.Run("unknown_tool", () => ToolClient.AssertUnknownTool(ToolExe));
        }

        static ToolInvokeResult Invoke(FakeEverythingServer server, JObject arguments)
        {
            return Invoke(arguments, server.Port, 5);
        }

        static ToolInvokeResult Invoke(JObject arguments, int port, int timeoutSeconds)
        {
            JObject config = new JObject
            {
                { "port", port },
                { "timeout", timeoutSeconds }
            };
            return ToolClient.InvokeProduct(ToolExe, "everything_search", arguments, config, 30000);
        }

        static string LastSearch(FakeEverythingServer server)
        {
            lock (server.ReceivedSearches)
            {
                return server.ReceivedSearches.Count == 0
                    ? null
                    : server.ReceivedSearches[server.ReceivedSearches.Count - 1];
            }
        }

        static string LastCount(FakeEverythingServer server)
        {
            lock (server.ReceivedCounts)
            {
                return server.ReceivedCounts.Count == 0
                    ? null
                    : server.ReceivedCounts[server.ReceivedCounts.Count - 1];
            }
        }

        /// <summary>
        /// A port with nothing behind it. Bound and released so the number is
        /// very likely to still be free when the tool connects.
        /// </summary>
        static int ClosedPort()
        {
            System.Net.Sockets.TcpListener probe =
                new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
