using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

public struct ToolResult
{
    public string Output;
    public int ExitCode;
    public string ImageBase64;
    public string ImageMime;

    public ToolResult(string output, int exitCode)
    {
        Output = output;
        ExitCode = exitCode;
        ImageBase64 = null;
        ImageMime = null;
    }

    public ToolResult(string output, int exitCode, string imageBase64, string imageMime)
    {
        Output = output;
        ExitCode = exitCode;
        ImageBase64 = imageBase64;
        ImageMime = imageMime;
    }
}

/// <summary>
/// Shared helper utilities for tool executable projects (FileTools, WebTools, etc.).
/// </summary>
public static class ToolHelper
{
    /// <summary>
    /// Common entry point for all tool executables. Handles encoding setup,
    /// manifest defaults, stdin JSON parsing, error handling, and output.
    /// </summary>
    public static int RunToolMain(string[] args, Func<string, string, ToolResult> dispatch)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        LoadManifestDefaults();

        if (args.Length < 1)
        {
            string exeName = Path.GetFileName(Assembly.GetEntryAssembly().Location);
            Console.Write("Usage: " + exeName + " <tool_name>\nArguments JSON is read from stdin.");
            return 1;
        }

        string toolName = args[0];
        string stdinData = Console.In.ReadToEnd();
        string argumentsJson = "";

        if (!string.IsNullOrWhiteSpace(stdinData))
        {
            try
            {
                JObject root = JObject.Parse(stdinData);

                JObject configObj = root["config"] as JObject;
                if (configObj != null)
                {
                    foreach (JProperty prop in configObj.Properties())
                        Config[prop.Name] = prop.Value.ToString();
                }

                JToken argsToken = root["arguments"];
                if (argsToken != null)
                    argumentsJson = argsToken.ToString();
            }
            catch
            {
                argumentsJson = stdinData;
            }
        }

        int exitCode = 0;
        ToolResult result = new ToolResult("", 0);

        try
        {
            result = dispatch(toolName, argumentsJson);
            exitCode = result.ExitCode;
        }
        catch (Exception e)
        {
            result = new ToolResult("error: " + e.Message, 1);
            exitCode = 1;
        }

        Console.Write(FormatResultJson(result));
        return exitCode;
    }

    /// <summary>Serialize a tool result as the JSON-first stdout protocol.</summary>
    public static string FormatResultJson(ToolResult result)
    {
        var obj = new JObject();
        obj["text"] = result.Output ?? "";

        if (!string.IsNullOrEmpty(result.ImageBase64))
        {
            string mime = string.IsNullOrEmpty(result.ImageMime) ? "image/png" : result.ImageMime;
            obj["image"] = new JObject
            {
                ["mime"] = mime,
                ["data"] = result.ImageBase64
            };
        }

        return obj.ToString(Newtonsoft.Json.Formatting.None);
    }

    // Loads option defaults from the manifest file sitting next to the executable.
    // Must be called before stdin config is parsed so that stdin values take precedence.
    private static void LoadManifestDefaults()
    {
        try
        {
            string manifestPath = Path.ChangeExtension(
                Assembly.GetEntryAssembly().Location, ".json");
            if (!File.Exists(manifestPath)) return;
            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
            foreach (JToken opt in manifest["options"] ?? new JArray())
            {
                string name = opt["name"]?.ToString();
                string def  = opt["default"]?.ToString();
                if (!string.IsNullOrEmpty(name) && def != null)
                    Config[name] = def;
            }
        }
        catch { }
    }

    // Config values passed via the "config" key in stdin JSON
    public static Dictionary<string, string> Config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static string GetConfigValue(string key)
    {
        string value;
        if (Config.TryGetValue(key, out value))
            return value;
        return "";
    }

    public static int GetConfigInt(string key, int defaultValue)
    {
        string value;
        if (Config.TryGetValue(key, out value))
        {
            int result;
            if (int.TryParse(value, out result))
                return result;
        }
        return defaultValue;
    }

    public static Encoding ResolveOutputEncoding()
    {
        string key = GetConfigValue("outputEncoding");
        if (string.IsNullOrEmpty(key) || key.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
        if (key.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || key.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8;
        try { return Encoding.GetEncoding(key); }
        catch { return Encoding.UTF8; }
    }

    public static string GetConfiguredOutputEncodingName()
    {
        string key = GetConfigValue("outputEncoding");
        return string.IsNullOrEmpty(key) ? "auto" : key.Trim();
    }

    public static string GetOptionalArg(string arguments, string argName)
    {
        string value = JsonExtractString(arguments, argName);
        return string.IsNullOrEmpty(value) ? null : value.Trim();
    }

    /// <summary>
    /// Reads a child process' output as bytes instead of text and decodes them with
    /// the right code page afterwards. ProcessStartInfo can only hand back a
    /// StreamReader, which decodes on the spot using one assumed page, and a shell
    /// command's bytes are not guaranteed to be in it: cmd.exe writes the console
    /// code page (866 on a Russian XP, 437 on an English one), a program that prints
    /// UTF-8 writes UTF-8, and "chcp 65001" inside a command switches cmd over
    /// mid-run. Guessing wrong turns any of those into mojibake, so the bytes are
    /// collected untouched and the page is chosen from their content.
    ///
    /// The bytes come off process.StandardOutput.BaseStream, never off
    /// process.StandardOutput: the StreamReader that ProcessStartInfo.StandardOutputEncoding
    /// installs still sniffs for a byte order mark, overrides the encoding you asked
    /// for, and decodes the rest as UTF-8. A UTF-8 BOM on a file echoed by "type"
    /// then arrives as text, and every byte after it is gone.
    /// </summary>
    public static string ExecuteProcessDecoded(string fileName, string arguments,
        string requestedEncoding, out int exitCode)
    {
        ProcessByteOutput raw = RunProcess(fileName, arguments, true);
        exitCode = raw.ExitCode;
        byte[] bytes = raw.Combined(true);
        string decoded = DecodeProcessOutput(bytes, requestedEncoding);
        return CapDecodedOutput(decoded, raw, fileName);
    }

    private sealed class ProcessByteOutput
    {
        public byte[] Standard = new byte[0];
        public byte[] Error = new byte[0];
        public long StandardTotal;
        public long ErrorTotal;
        public int ExitCode;

        public byte[] Combined(bool combineErrorOutput)
        {
            if (!combineErrorOutput || Error.Length == 0)
                return Standard;
            byte[] both = new byte[Standard.Length + Error.Length];
            Buffer.BlockCopy(Standard, 0, both, 0, Standard.Length);
            Buffer.BlockCopy(Error, 0, both, Standard.Length, Error.Length);
            return both;
        }
    }

    // ceiling < 0 means unlimited.
    private static long ReadBytes(Stream stream, MemoryStream sink, int ceiling)
    {
        byte[] buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;
            total += read;
            if (ceiling >= 0)
            {
                int room = ceiling - (int)sink.Length;
                if (room > 0)
                    sink.Write(buffer, 0, Math.Min(room, read));
            }
            else
                sink.Write(buffer, 0, read);
        }
        return total;
    }

    /// <summary>
    /// Starts a process and collects its output as raw bytes, with the same bounded
    /// reads, drain timeouts and teardown the string version had. Decoding is left to
    /// the caller so the code page can be decided from the bytes.
    /// </summary>
    private static ProcessByteOutput RunProcess(string fileName, string arguments, bool capOutput)
    {
        ProcessByteOutput output = new ProcessByteOutput();
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(psi))
            {
                MemoryStream outBuffer = new MemoryStream();
                MemoryStream errBuffer = new MemoryStream();
                long outTotal = 0;
                long errTotal = 0;
                Thread outThread = new Thread(() =>
                {
                    try
                    {
                        outTotal = ReadBytes(process.StandardOutput.BaseStream, outBuffer,
                            capOutput ? ProcessOutputReadCeilingChars : -1);
                    }
                    catch { }
                });
                Thread errThread = new Thread(() =>
                {
                    try
                    {
                        errTotal = ReadBytes(process.StandardError.BaseStream, errBuffer,
                            capOutput ? ProcessOutputReadCeilingChars : -1);
                    }
                    catch { }
                });
                outThread.IsBackground = true;
                errThread.IsBackground = true;
                outThread.Start();
                errThread.Start();

                process.WaitForExit();
                output.ExitCode = process.ExitCode;

                if (!outThread.Join(PipeDrainTimeoutMs))
                {
                    try { process.StandardOutput.Close(); } catch { }
                }
                if (!errThread.Join(PipeDrainTimeoutMs))
                {
                    try { process.StandardError.Close(); } catch { }
                }
                outThread.Join(PipeCloseJoinTimeoutMs);
                errThread.Join(PipeCloseJoinTimeoutMs);
                // Process.Dispose does not close redirected streams on .NET
                // Framework: without this every spawn leaks two pipe handles
                // until finalization.
                try { process.StandardOutput.Close(); } catch { }
                try { process.StandardError.Close(); } catch { }

                output.StandardTotal = outTotal;
                output.ErrorTotal = errTotal;
                output.Standard = outBuffer.ToArray();
                output.Error = errBuffer.ToArray();
                return output;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to execute " + fileName + ": " + ex.Message, ex);
        }
    }

    private static string CapDecodedOutput(string combined, ProcessByteOutput raw, string fileName)
    {
        long totalBytes = raw.StandardTotal + raw.ErrorTotal;
        if (totalBytes > ProcessOutputReadCeilingChars)
        {
            string boundedHead = combined.Length > ProcessOutputHeadChars
                ? combined.Substring(0, ProcessOutputHeadChars)
                : combined;
            return boundedHead
                + "\n[... " + (totalBytes - combined.Length) + " more characters omitted: process output exceeded the "
                + ProcessOutputReadCeilingChars + " character read limit; only the beginning of the output is shown.]\n";
        }

        return CapProcessOutput(combined, SafeFileToken(Path.GetFileNameWithoutExtension(fileName)));
    }

    /// <summary>
    /// Decodes bytes captured from a child process. A requestedEncoding of "auto"
    /// (or empty) detects the page by content via ConsoleOutputDecoder: strict
    /// UTF-8 first (deterministic), then the Ude statistical detector whenever it
    /// is confident it named a console page, and finally a scored fallback between
    /// the two Cyrillic Windows pages - CP866, what cmd writes to a pipe, and
    /// CP1251, what C-stdio utilities (reg, ipconfig) write on a Russian system.
    /// Naming a page ("utf-8", "866", "1251", "65001") always wins, for output that
    /// no detection rule can settle.
    /// </summary>
    public static string DecodeProcessOutput(byte[] bytes, string requestedEncoding)
    {
        if (bytes == null || bytes.Length == 0)
            return "";

        if (!IsAutoEncodingName(requestedEncoding))
        {
            Encoding requested = EncodingByName(requestedEncoding);
            if (requested != null)
            {
                // A UTF-8 BOM is not text, and leaving it in would prepend U+FEFF.
                int start = 0;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    start = 3;
                return requested.GetString(bytes, start, bytes.Length - start);
            }
        }

        return ConsoleOutputDecoder.Decode(bytes);
    }

    private static bool IsAutoEncodingName(string name)
    {
        return string.IsNullOrEmpty(name) || name.Trim().Length == 0
            || name.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding EncodingByName(string name)
    {
        if (IsAutoEncodingName(name))
            return null;

        string trimmed = name.Trim();
        if (trimmed.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("utf8", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("65001", StringComparison.Ordinal))
            return new UTF8Encoding(false);

        int codePage;
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out codePage))
        {
            try { return Encoding.GetEncoding(codePage); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        try { return Encoding.GetEncoding(trimmed); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static string GetRequiredArg(string arguments, string argName)
    {
        string value = JsonExtractString(arguments, argName)?.Trim() ?? "";
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("missing '" + argName + "' argument.");
        return value;
    }

    // After the direct child exits, wait this long for stdout/stderr EOF.
    // GUI apps launched via `start` inherit redirected pipe handles and would
    // otherwise keep ReadToEnd blocked until those apps close.
    private const int PipeDrainTimeoutMs = 500;
    private const int PipeCloseJoinTimeoutMs = 100;

    // Hard ceiling for raw tool output so a single tool result (e.g. a chatty
    // shell command or a huge web page) can never blow the conversation past
    // the context window on its own. Head and tail are kept; the middle is
    // replaced by a marker, and the full output is offloaded to a temp file
    // the model can read in parts with read_file (offset).
    public const int MaxProcessOutputChars = 20000;
    private const int ProcessOutputHeadChars = 12000;
    private const int ProcessOutputTailChars = 6000;
    private const int ProcessOutputReadCeilingChars = 2000000;

    /// <summary>Age after which stale webtools-offload files are purged.</summary>
    private const int OffloadRetentionDays = 7;

    /// <summary>
    /// Saves the full uncapped output to %TEMP%\webtools-offload so the model
    /// can read it in parts via read_file with offset. Returns the file path,
    /// or null when writing failed.
    /// </summary>
        public static string OffloadToTempFile(string text, string toolName)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "webtools-offload");
                Directory.CreateDirectory(dir);
                PurgeStaleOffloadFiles(dir);
                int pid;
                using (Process self = Process.GetCurrentProcess())
                    pid = self.Id;
                string name = SafeFileToken(toolName) + "-" + pid + "-"
                    + DateTime.Now.ToString("HHmmssfff") + ".txt";
                string file = Path.Combine(dir, name);
                File.WriteAllText(file, text, Encoding.UTF8);
                return file;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes offload files older than the retention window so the folder
        /// cannot grow without bound. Recent files survive: the model may still
        /// be reading them through read_file.
        /// </summary>
        private static void PurgeStaleOffloadFiles(string dir)
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-OffloadRetentionDays);
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }
            foreach (string f in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                        File.Delete(f);
                }
                catch { }
            }
        }

    private static string SafeFileToken(string name)
    {
        string token = (name ?? "").Trim();
        if (token.Length == 0)
            return "tool";
        return System.Text.RegularExpressions.Regex.Replace(token, @"[^a-zA-Z0-9_.-]", "_");
    }

    private static string CapProcessOutput(string text, string toolName)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxProcessOutputChars)
            return text;

        string head = text.Substring(0, ProcessOutputHeadChars);
        string tail = text.Substring(text.Length - ProcessOutputTailChars);
        int omitted = text.Length - ProcessOutputHeadChars - ProcessOutputTailChars;
        string offloadPath = OffloadToTempFile(text, toolName);
        string pathHint = offloadPath != null
            ? " Full output saved to " + offloadPath + " - read it in parts with the read_file tool (offset)."
            : "";
        return head
            + "\n[... " + omitted + " characters omitted: output exceeded the " + MaxProcessOutputChars + " character limit." + pathHint + "]\n"
            + tail;
    }

    public static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
            return "";
        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
                return values[i].Trim();
        }
        return "";
    }

    public static string NormalizeWhitespace(string text)
    {
        return Regex.Replace(text ?? "", @"\s+", " ").Trim();
    }

    /// <summary>
    /// Runs a process and returns its (optionally capped) output decoded as text.
    /// capOutput=false is for intermediate artifacts (e.g. raw HTML fetched for
    /// parsing) where the caller caps the final parsed result instead.
    /// </summary>
    public static string ExecuteProcess(string fileName, string arguments, out int exitCode, bool combineErrorOutput = true, Encoding outputEncoding = null, bool capOutput = true)
    {
        Encoding enc = outputEncoding ?? Encoding.UTF8;
        ProcessByteOutput raw = RunProcess(fileName, arguments, capOutput);
        exitCode = raw.ExitCode;

        string combined = DecodeWithEncoding(enc, raw.Combined(combineErrorOutput));
        if (!capOutput)
            return combined;

        return CapDecodedOutput(combined, raw, fileName);
    }

    /// <summary>
    /// Decodes with a known page. A UTF-8 byte order mark is dropped, which is what
    /// the StreamReader this replaced used to do on its own.
    /// </summary>
    private static string DecodeWithEncoding(Encoding enc, byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";

        int start = 0;
        if (enc.CodePage == 65001 && bytes.Length >= 3
            && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3;

        return enc.GetString(bytes, start, bytes.Length - start);
    }

    public static string JsonExtractString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
        {
            return "";
        }

        try
        {
            string trimmedJson = json.Trim();
            if (trimmedJson.Length == 0)
            {
                return "";
            }

            JToken root = JToken.Parse(trimmedJson);
            if (root.Type == JTokenType.Object)
            {
                JObject obj = (JObject)root;
                JToken token;
                if (!obj.TryGetValue(key, out token))
                {
                    foreach (JProperty property in obj.Properties())
                    {
                        if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                        {
                            token = property.Value;
                            break;
                        }
                    }
                }

                if (token != null && token.Type != JTokenType.Null)
                {
                    return token.Type == JTokenType.String ? token.Value<string>() ?? "" : token.ToString();
                }
            }
        }
        catch
        {
        }

        return "";
    }

    public static JArray JsonExtractArray(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
            return null;

        try
        {
            JToken root = JToken.Parse(json.Trim());
            if (root.Type != JTokenType.Object)
                return null;

            JObject obj = (JObject)root;
            JToken token;
            if (!obj.TryGetValue(key, out token))
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        token = property.Value;
                        break;
                    }
                }
            }

            return token as JArray;
        }
        catch
        {
            return null;
        }
    }
}
