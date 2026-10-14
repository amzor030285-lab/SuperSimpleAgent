using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace WebTools
{
    internal static class CurlHelper
    {
        /// <summary>
        /// The bundled curl.exe carries a stale compiled-in CA store; when a fresh
        /// bundle sits beside it, pin it so certificate validation stays current.
        /// Returns an empty string when no bundle is present.
        /// </summary>
        private static string CacertFlag()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "curl-ca-bundle.crt");
            return File.Exists(path) ? " --cacert \"" + path + "\" " : "";
        }

        /// <summary>
        /// Runs curl with -s -L, the configured user-agent, and the given URL.
        /// Extra flags (e.g. "-I", "-o file") and extra headers are optional.
        /// </summary>
        public static string Execute(string url, out int exitCode,
            string extraFlags = "", bool combineErrorOutput = true, params string[] extraHeaders)
        {
            string ua = " -H \"User-Agent: " + ToolHelper.GetConfigValue("useragent") + "\"";
            string hdrs = string.Concat(System.Array.ConvertAll(extraHeaders, h => " -H \"" + h + "\""));
            string flags = string.IsNullOrEmpty(extraFlags) ? "" : extraFlags + " ";
            string arguments = "-s -L " + CacertFlag() + flags + ua + hdrs + " \"" + url + "\"";
            // Raw HTML/JSON is an intermediate artifact: the parsed result is
            // capped by WebBrowser, not the raw bytes.
            return ToolHelper.ExecuteProcess("curl.exe", arguments, out exitCode, combineErrorOutput, null, false);
        }

        /// <summary>
        /// POSTs a JSON body via curl. The body is written to a named pipe so it
        /// is not quoted on the command line. Optional extra headers (e.g. Authorization).
        /// </summary>
        public static string PostJson(string url, string jsonBody, out int exitCode, params string[] extraHeaders)
        {
            byte[] body = Encoding.UTF8.GetBytes(jsonBody ?? "");
            string pipeName = "webcurl_" + Guid.NewGuid().ToString("N");
            string hdrs = " -H \"Content-Type: application/json\""
                + string.Concat(System.Array.ConvertAll(extraHeaders, h => " -H \"" + h + "\""));
            string arguments = "-s -L -X POST" + CacertFlag() + hdrs
                + " --data-binary \"@\\\\.\\pipe\\" + pipeName + "\" \"" + url + "\"";

            NamedPipeServerStream pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte);
            Thread writer = new Thread(() =>
            {
                try
                {
                    pipe.WaitForConnection();
                    if (body.Length > 0)
                        pipe.Write(body, 0, body.Length);
                    pipe.Flush();
                    pipe.Close();
                }
                catch
                {
                }
            });
            writer.IsBackground = true;
            writer.Start();

            try
            {
                return ToolHelper.ExecuteProcess(
                    "curl.exe", arguments, out exitCode, false, null, false);
            }
            finally
            {
                pipe.Dispose();
                writer.Join(5000);
            }
        }

        public static string[] FirecrawlAuthHeaders(string apiKey)
        {
            return string.IsNullOrWhiteSpace(apiKey)
                ? new string[0]
                : new[] { "Authorization: Bearer " + apiKey.Trim() };
        }
    }
}
