using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
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
            string name = SafeFileToken(toolName) + "-" + Process.GetCurrentProcess().Id + "-"
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

    private static long ReadBounded(TextReader reader, StringBuilder sink, int ceiling)
    {
        char[] buffer = new char[4096];
        long total = 0;
        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;
            total += read;
            int room = ceiling - sink.Length;
            if (room > 0)
                sink.Append(buffer, 0, Math.Min(room, read));
        }
        return total;
    }

    /// <summary>
    /// Runs a process and returns its (optionally capped) output. capOutput=false
    /// is for intermediate artifacts (e.g. raw HTML fetched for parsing) where
    /// the caller caps the final parsed result instead.
    /// </summary>
    public static string ExecuteProcess(string fileName, string arguments, out int exitCode, bool combineErrorOutput = true, Encoding outputEncoding = null, bool capOutput = true)
    {
        Encoding enc = outputEncoding ?? Encoding.UTF8;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };

            using (Process process = Process.Start(psi))
            {
                StringBuilder outBuffer = new StringBuilder();
                StringBuilder errBuffer = new StringBuilder();
                long outTotal = 0;
                long errTotal = 0;
                Thread outThread = new Thread(() =>
                {
                    try
                    {
                        if (capOutput)
                            outTotal = ReadBounded(process.StandardOutput, outBuffer, ProcessOutputReadCeilingChars);
                        else
                            outTotal = outBuffer.Append(process.StandardOutput.ReadToEnd()).Length;
                    }
                    catch { }
                });
                Thread errThread = new Thread(() =>
                {
                    try
                    {
                        if (capOutput)
                            errTotal = ReadBounded(process.StandardError, errBuffer, ProcessOutputReadCeilingChars);
                        else
                            errTotal = errBuffer.Append(process.StandardError.ReadToEnd()).Length;
                    }
                    catch { }
                });
                outThread.IsBackground = true;
                errThread.IsBackground = true;
                outThread.Start();
                errThread.Start();

                process.WaitForExit();
                exitCode = process.ExitCode;

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

                string combined = combineErrorOutput && errBuffer.Length > 0
                    ? outBuffer.ToString() + errBuffer.ToString()
                    : outBuffer.ToString();
                if (!capOutput)
                    return combined;

                long totalChars = outTotal + errTotal;
                if (totalChars > ProcessOutputReadCeilingChars)
                {
                    string boundedHead = combined.Length > ProcessOutputHeadChars
                        ? combined.Substring(0, ProcessOutputHeadChars)
                        : combined;
                    return boundedHead
                        + "\n[... " + (totalChars - combined.Length) + " more characters omitted: process output exceeded the "
                        + ProcessOutputReadCeilingChars + " character read limit; only the beginning of the output is shown.]\n";
                }

                return CapProcessOutput(combined, SafeFileToken(Path.GetFileNameWithoutExtension(fileName)));
            }
        }
        catch (Exception ex)
        {
            exitCode = -1;
            throw new InvalidOperationException("Failed to execute " + fileName + ": " + ex.Message, ex);
        }
    }

    public static string ExecuteProcess(string fileName, string arguments, out int exitCode, Encoding outputEncoding)
    {
        return ExecuteProcess(fileName, arguments, out exitCode, true, outputEncoding);
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
