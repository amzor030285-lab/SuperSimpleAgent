using System;
using System.IO;

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
        /// POSTs a JSON body via curl (-d inline). Optional extra headers (e.g. Authorization).
        /// </summary>
        public static string PostJson(string url, string jsonBody, out int exitCode,
            bool combineErrorOutput = true, params string[] extraHeaders)
        {
            string hdrs = " -H \"Content-Type: application/json\""
                + string.Concat(System.Array.ConvertAll(extraHeaders, h => " -H \"" + h + "\""));
            string escapedJson = (jsonBody ?? "").Replace("\"", "\\\"");
            string arguments = "-s -L -X POST" + CacertFlag() + hdrs + " -d \"" + escapedJson + "\" \"" + url + "\"";
            return ToolHelper.ExecuteProcess("curl.exe", arguments, out exitCode, combineErrorOutput, null, false);
        }

        public static string[] FirecrawlAuthHeaders(string apiKey)
        {
            return string.IsNullOrWhiteSpace(apiKey)
                ? new string[0]
                : new[] { "Authorization: Bearer " + apiKey.Trim() };
        }
    }
}
