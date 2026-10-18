using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SearchTools
{
    /// <summary>
    /// File search through Everything's HTTP server (http://127.0.0.1:8888/).
    /// Everything (https://www.voidtools.com/) keeps an NTFS index, so a query
    /// that would take a recursive walk minutes answers in about a millisecond.
    /// The server is optional: if it is off, or something else already owns the
    /// port, the model gets an explanation instead of a wrong "not found".
    /// </summary>
    internal static class EverythingSearchTool
    {
        public const int MaxResultsDefault = 50;
        public const int MaxResultsHardLimit = 100;
        public const int MaxQueryLength = 100;
        public const int DefaultPort = 8888;
        public const int DefaultTimeoutSeconds = 5;

        // The host is a constant on purpose. A configurable host would turn a
        // settings value into a request proxy for whatever address a user (or a
        // stray ini edit) put there; only the port is meant to be changed.
        public const string Host = "127.0.0.1";

        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB" };

        // Windows FILETIME counts 100ns intervals since 1601-01-01.
        private static readonly DateTime FileTimeEpoch =
            new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string Search(
            string query,
            int maxResults,
            int offset,
            int port,
            int timeoutSeconds,
            out int exitCode)
        {
            string validationError;
            if (!ValidateQuery(query, out validationError))
            {
                exitCode = 1;
                return "error: " + validationError;
            }

            string normalized = query.Trim();
            if (maxResults < 1)
            {
                exitCode = 1;
                return "error: max_results must be at least 1.";
            }
            if (maxResults > MaxResultsHardLimit)
            {
                maxResults = MaxResultsHardLimit;
            }
            if (offset < 0)
            {
                exitCode = 1;
                return "error: offset must be >= 0.";
            }

            int effectivePort = port;
            if (effectivePort < 1 || effectivePort > 65535)
            {
                effectivePort = DefaultPort;
            }
            int timeoutMs = (timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds) * 1000;
            string url = BuildUrl(effectivePort, normalized, maxResults, offset);

            string body;
            string transportError;
            if (!TryDownload(url, timeoutMs, out body, out transportError))
            {
                exitCode = 1;
                return transportError;
            }

            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch (JsonException ex)
            {
                exitCode = 1;
                return PortOccupiedMessage(effectivePort, body, ex.Message);
            }

            JArray results = root["results"] as JArray;
            if (results == null || results.Count == 0)
            {
                exitCode = 0;
                return EmptyResultHint(normalized);
            }

            List<string> lines = new List<string>();
            for (int i = 0; i < results.Count && lines.Count < maxResults; i++)
            {
                JObject item = results[i] as JObject;
                if (item == null)
                {
                    continue;
                }

                string name = StringField(item, "name") ?? "Unknown";
                string path = StringField(item, "path") ?? "Unknown";
                string modified = FormatFileTime(StringField(item, "date_modified"));
                // Folders carry no size in Everything's response, so "unknown
                // size" would just be noise: name the type instead.
                string meta = StringField(item, "type") == "folder"
                    ? "folder"
                    : FormatSize(StringField(item, "size"));
                lines.Add(name + " -> " + path + " (" + meta + ", " + modified + ")");
            }

            int total = TotalResults(root);
            string header = total >= 0
                ? "[showing " + lines.Count + " of " + total + " total]"
                : "[showing " + lines.Count + "]";

            StringBuilder output = new StringBuilder();
            output.Append(header).Append('\n');
            output.Append(string.Join("\n", lines.ToArray()));

            if (total >= 0 && offset + lines.Count < total)
            {
                output.Append("\n\nMore results available (")
                    .Append(total - offset - lines.Count)
                    .Append(" remaining). Call again with offset=")
                    .Append(offset + lines.Count)
                    .Append('.');
                output.Append("\nTIP: Narrow the query instead (ext:, dm:, size:) if you don't need everything.");
            }

            exitCode = 0;
            return output.ToString();
        }

        /// <summary>
        /// Rejects empty and over-broad queries. A one-character or bare-'*'
        /// search matches most of the disk and the model only pays for the
        /// context, so it is refused before the request goes out.
        /// </summary>
        private static bool ValidateQuery(string query, out string error)
        {
            if (query == null || query.Trim().Length == 0)
            {
                error = "query cannot be empty. Provide a filename or pattern (e.g. 'report', 'ext:py').";
                return false;
            }

            string trimmed = query.Trim();
            if (trimmed.Length > MaxQueryLength)
            {
                error = "query is too long (" + trimmed.Length + " chars). Max allowed is " + MaxQueryLength + ".";
                return false;
            }

            StringBuilder meaningful = new StringBuilder(trimmed.Length);
            bool hasLetterOrDigit = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '*' || char.IsWhiteSpace(c))
                {
                    continue;
                }
                meaningful.Append(c);
                if (char.IsLetterOrDigit(c))
                {
                    hasLetterOrDigit = true;
                }
            }

            if (!hasLetterOrDigit || meaningful.Length < 2)
            {
                error = "query is too broad. Specify a filename, part of a name "
                    + "or a filter (e.g. 'ext:md', 'dm:today', 'size:gigantic').";
                return false;
            }

            error = null;
            return true;
        }

        private static string BuildUrl(int port, string query, int maxResults, int offset)
        {
            return "http://" + Host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/"
                + "?search=" + Uri.EscapeDataString(query)
                + "&json=1"
                + "&path_column=1"
                + "&size_column=1"
                + "&date_modified_column=1"
                + "&count=" + maxResults.ToString(CultureInfo.InvariantCulture)
                + "&offset=" + offset.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns false with a ready-to-show explanation for every failure mode
        /// that is not "Everything answered". The four cases are kept apart on
        /// purpose: a refused connection, a timeout, a foreign HTTP server on the
        /// port and a protocol error each need a different fix, and telling the
        /// model to switch Everything on when the port is simply taken sends the
        /// user chasing the wrong setting.
        /// </summary>
        private static bool TryDownload(string url, int timeoutMs, out string body, out string error)
        {
            body = null;
            error = null;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            // A machine-wide proxy must not swallow a loopback request.
            request.Proxy = null;

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                return true;
            }
            catch (WebException ex)
            {
                error = DescribeWebException(url, ex);
                return false;
            }
            catch (Exception ex)
            {
                error = "error: the request to " + url + " failed unexpectedly: "
                    + ex.Message;
                return false;
            }
        }

        private static string DescribeWebException(string url, WebException ex)
        {
            if (ex.Status == WebExceptionStatus.Timeout)
            {
                return "error: the Everything HTTP server at " + url + " did not answer in time. "
                    + "Everything is probably busy indexing. Retry once; if it keeps timing out, "
                    + "check the Everything window for a long-running scan.";
            }

            if (ex.Status == WebExceptionStatus.ConnectFailure)
            {
                return NotRunningMessage(url, NativeError(ex));
            }

            using (HttpWebResponse response = ex.Response as HttpWebResponse)
            {
                if (response != null)
                    return "error: the Everything HTTP server at " + url + " answered with HTTP "
                        + (int)response.StatusCode + " " + response.StatusDescription + ".";
            }

            return "error: the request to the Everything HTTP server at " + url + " failed ("
                + ex.Status + "): " + ex.Message;
        }

        private static string NotRunningMessage(string url, string nativeError)
        {
            StringBuilder message = new StringBuilder();
            message.Append("error: cannot reach the Everything HTTP server at ").Append(url).Append('.');
            if (!string.IsNullOrEmpty(nativeError))
            {
                message.Append(" (system error ").Append(nativeError).Append(')');
            }
            message.AppendLine();
            message.AppendLine("Everything is not running, or its HTTP server is disabled.");
            message.AppendLine("To fix it:");
            message.AppendLine("  1. Install and start Everything (https://www.voidtools.com/).");
            message.AppendLine("  2. In Everything: Tools -> Options -> HTTP Server.");
            message.AppendLine("  3. Tick \"Enable HTTP server\" and set \"Server port\" to the port in this tool's options.");
            message.AppendLine("  4. Run the search again.");
            message.Append("Until the server answers, an empty result means nothing: do not report a file "
                + "as missing on the strength of this tool.");
            return message.ToString();
        }

        private static string PortOccupiedMessage(int port, string body, string parseError)
        {
            StringBuilder message = new StringBuilder();
            message.Append("error: something answered on port ").Append(port)
                .Append(" but it is not Everything's JSON (").Append(parseError).AppendLine(").");
            message.Append("Most likely another program already uses that port. Set this tool's port option "
                + "to a free one and give Everything the same value in "
                + "Tools -> Options -> HTTP Server -> Server port.");
            message.Append("First bytes received: ").Append(PreviewBody(body));
            return message.ToString();
        }

        private static string PreviewBody(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return "(empty response)";
            }

            string flat = body.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= 200 ? flat : flat.Substring(0, 200) + "...";
        }

        private static string NativeError(WebException ex)
        {
            SocketException socketError = ex.InnerException as SocketException;
            if (socketError != null)
            {
                return socketError.SocketErrorCode.ToString();
            }
            return null;
        }

        /// <summary>
        /// No match is a normal answer, not a failure: it is reported with exit
        /// code 0 and no hint about the server, otherwise a working Everything and
        /// a missing file would look identical to a dead Everything.
        /// </summary>
        private static string EmptyResultHint(string query)
        {
            StringBuilder hint = new StringBuilder();
            hint.Append("No files found for query: ").Append(query).Append('\n');
            if (LooksLikePath(query))
            {
                hint.AppendLine();
                hint.AppendLine("HINT: your query looks like a file path. Use only the filename, not the full path.");
                hint.Append("Example: instead of 'C:\\Users\\file.txt', use 'file.txt'.");
            }
            else
            {
                hint.AppendLine();
                hint.Append("HINT: try adding an extension filter (e.g. 'ext:md') or a folder name. "
                    + "Remember Everything only indexes NTFS volumes, so a FAT32 stick or an "
                    + "excluded folder will never match.");
            }
            return hint.ToString();
        }

        private static bool LooksLikePath(string query)
        {
            return query.IndexOf('\\') >= 0 || query.IndexOf('/') >= 0;
        }

        private static string StringField(JObject item, string key)
        {
            JToken token = item[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            string value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static int TotalResults(JObject root)
        {
            JToken token = root["totalResults"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return -1;
            }

            int total;
            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            string raw = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out total) && total >= 0
                ? total
                : -1;
        }

        private static string FormatSize(string raw)
        {
            long bytes;
            if (string.IsNullOrEmpty(raw) ||
                !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes) ||
                bytes < 0)
            {
                return "unknown size";
            }

            if (bytes == 0)
            {
                return "0 B";
            }

            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < SizeUnits.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + SizeUnits[unit];
        }

        /// <summary>
        /// Everything reports date_modified as a FILETIME in 100ns ticks since
        /// 1601-01-01 UTC, which is exactly the TimeSpan tick unit, so the
        /// conversion is an exact addition with no rounding. The value is
        /// rendered in UTC, which is what the field itself stores.
        /// </summary>
        private static string FormatFileTime(string raw)
        {
            long ticks;
            if (string.IsNullOrEmpty(raw) ||
                !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                ticks <= 0)
            {
                return "unknown date";
            }

            try
            {
                DateTime stamp = FileTimeEpoch.Add(TimeSpan.FromTicks(ticks));
                return stamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                return "unknown date";
            }
        }
    }
}
