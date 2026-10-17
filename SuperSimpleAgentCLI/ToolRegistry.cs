using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SuperSimpleAgentCLI
{
    public class ToolRegistry
    {
        public class ToolCall
        {
            public string Id;
            public string Name;
            public string Arguments;

            public ToolCall()
            {
                Id = "";
                Name = "";
                Arguments = "";
            }
        }

        public struct ToolParameterInfo
        {
            public string Name;
            public string Type;
            public string Description;
            public bool Required;
            public JObject Items;
        }

        public struct ToolDefinition
        {
            public string Name;
            public string Description;
            public List<ToolParameterInfo> Parameters;
            public string ExecutablePath;
        }

        private readonly Dictionary<string, ToolDefinition> Tools = new Dictionary<string, ToolDefinition>(StringComparer.OrdinalIgnoreCase);

        // Context injectors declared by manifests via the "context_injector" field (executablePath -> commandName).
        private readonly Dictionary<string, string> contextInjectorsByExecutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Default values for options declared in manifests, keyed by executable
        // path then by option name. Per-package scoping so a tool process only
        // ever receives the config keys its own manifest declares.
        private readonly Dictionary<string, Dictionary<string, string>> optionDefaultsByExecutable =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Config values to pass to tool executables
        private readonly ConfigHandler config;

        /// <summary>
        /// True while the read-only plan mode is selected. Toggled by /plan and by
        /// the GUI mode button, checked on every tool call.
        /// </summary>
        public bool planModeActive;

        public ToolRegistry(ConfigHandler config)
        {
            this.config = config;
        }

        /// <summary>
        /// Clears loaded tools, context injectors, and option defaults so they can be reloaded.
        /// </summary>
        public void Clear()
        {
            Tools.Clear();
            contextInjectorsByExecutable.Clear();
            optionDefaultsByExecutable.Clear();
        }

        /// <summary>
        /// Scans a directory for *.json tool manifests and loads all tool definitions.
        /// </summary>
        public void LoadToolsFromDirectory(string toolsDir)
        {
            foreach (string jsonFile in ManifestScanner.GetManifestFiles(toolsDir))
            {
                try
                {
                    LoadManifest(jsonFile);
                }
                catch (Exception ex)
                {
                    // Skip malformed manifests, but say which one and why: a silent
                    // skip looks exactly like "the tool does not exist".
                    Console.Error.WriteLine(
                        "Skipping tool manifest '" + jsonFile + "': " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Replaces {config.key} placeholders in text with actual config values.
        /// Also handles {config.key*N} for simple multiplication.
        /// </summary>
        private string ReplacePlaceholders(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return Regex.Replace(text, @"\{config\.(\w+?)(?:\*(\d+))?\}", match =>
            {
                string key = match.Groups[1].Value;
                string value = config.GetConfigValue(key);
                if (string.IsNullOrEmpty(value))
                    return match.Value; // Leave unresolved placeholders as-is

                if (match.Groups[2].Success)
                {
                    int multiplier;
                    int intValue;
                    if (int.TryParse(match.Groups[2].Value, out multiplier) && int.TryParse(value, out intValue))
                        return (intValue * multiplier).ToString();
                }

                return value;
            });
        }

        private void LoadManifest(string jsonFilePath)
        {
            string json = File.ReadAllText(jsonFilePath, Encoding.UTF8);
            JObject manifest = JObject.Parse(json);

            string executable = (string)manifest["executable"] ?? "";
            string manifestDir = Path.GetDirectoryName(jsonFilePath);
            string executablePath = Path.Combine(manifestDir, executable);

            string contextInjectorCommand = (string)manifest["context_injector"];
            if (!string.IsNullOrEmpty(contextInjectorCommand))
                contextInjectorsByExecutable[executablePath] = contextInjectorCommand;

            JArray toolsArray = manifest["tools"] as JArray;
            if (toolsArray == null)
                return;

            // Load option defaults from manifest (scoped to this executable so a
            // tool only receives the config keys its own package declares).
            JArray optionsArray = manifest["options"] as JArray;
            if (optionsArray != null)
            {
                Dictionary<string, string> perExecutable = null;
                if (!optionDefaultsByExecutable.TryGetValue(executablePath, out perExecutable))
                {
                    perExecutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    optionDefaultsByExecutable[executablePath] = perExecutable;
                }
                foreach (JObject optObj in optionsArray)
                {
                    string optName = (string)optObj["name"];
                    string optDefault = (string)optObj["default"];
                    if (!string.IsNullOrEmpty(optName) && optDefault != null)
                        perExecutable[optName] = optDefault;
                }
            }

            foreach (JObject toolObj in toolsArray)
            {
                string name = (string)toolObj["name"];
                if (string.IsNullOrEmpty(name))
                    continue;

                ToolDefinition def = new ToolDefinition
                {
                    Name = name,
                    Description = ReplacePlaceholders((string)toolObj["description"] ?? ""),
                    ExecutablePath = executablePath,
                    Parameters = new List<ToolParameterInfo>()
                };

                JArray paramsArray = toolObj["parameters"] as JArray;
                if (paramsArray != null)
                {
                    foreach (JObject paramObj in paramsArray)
                    {
                        def.Parameters.Add(new ToolParameterInfo
                        {
                            Name = (string)paramObj["name"] ?? "",
                            Type = (string)paramObj["type"] ?? "string",
                            Description = ReplacePlaceholders((string)paramObj["description"] ?? ""),
                            Required = paramObj["required"]?.Value<bool>() ?? false,
                            Items = paramObj["items"] as JObject
                        });
                    }
                }

                Tools[name] = def;
            }
        }

        /// <summary>
        /// Builds the OpenAI-compatible tools JSON array for the given enabled tool names.
        /// </summary>
        public JArray BuildToolsArray(List<string> enabledTools)
        {
            JArray toolsArray = new JArray();

            foreach (string toolName in enabledTools)
            {
                ToolDefinition def;
                if (!Tools.TryGetValue(toolName, out def))
                    continue;

                // Plan mode hides state-changing tools from the model entirely, so
                // it never plans around them. ExecuteToolCall still refuses them -
                // this is the second of the two layers, not the only one.
                if (planModeActive && PlanMode.IsForbiddenTool(toolName))
                    continue;

                JObject props = new JObject();
                JArray required = new JArray();

                foreach (var param in def.Parameters)
                {
                    JObject propObj = new JObject
                    {
                        ["type"] = param.Type,
                        ["description"] = param.Description
                    };
                    if (param.Items != null)
                        propObj["items"] = param.Items;
                    props[param.Name] = propObj;

                    if (param.Required)
                        required.Add(param.Name);
                }

                JObject parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required
                };

                JObject func = new JObject
                {
                    ["name"] = def.Name,
                    ["description"] = def.Description,
                    ["parameters"] = parameters
                };

                // write_file/edit_file stay in the schema in plan mode, but only for
                // the plan file. Say so in the description and pin the argument, so
                // the model is not left guessing where it may write.
                if (planModeActive && PlanMode.IsPlanWriterTool(toolName))
                {
                    func["description"] = def.Description
                        + " In plan mode this may only be used to write the plan to "
                        + PlanMode.GetPlanFilePath()
                        + "; any other path is refused.";

                    JObject planPathProperty = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Must be " + PlanMode.GetPlanFilePath()
                            + " while in plan mode."
                    };
                    if (props[PlanMode.PlanWriterPathArgument] != null)
                        props[PlanMode.PlanWriterPathArgument] = planPathProperty;
                }

                JObject tool = new JObject
                {
                    ["type"] = "function",
                    ["function"] = func
                };

                toolsArray.Add(tool);
            }

            return toolsArray;
        }

        /// <summary>
        /// Manifest description of a tool, as the model sees it in the schema.
        /// Fed to the Jev decision gate; empty for unknown tools.
        /// </summary>
        public string GetToolDescription(string toolName)
        {
            ToolDefinition def;
            if (!Tools.TryGetValue(toolName, out def))
                return string.Empty;
            return def.Description ?? string.Empty;
        }

        /// <summary>
        /// Calls each package's context injector for packages that have at least
        /// one enabled tool, and returns all non-empty results for injection into the system prompt.
        /// </summary>
        public List<string> GetContextInjections(List<string> enabledTools)
        {
            var results = new List<string>();

            if (enabledTools.Count == 0 || contextInjectorsByExecutable.Count == 0)
                return results;

            // Determine which executables are active (have at least one enabled tool).
            var activeExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string toolName in enabledTools)
            {
                ToolDefinition def;
                if (Tools.TryGetValue(toolName, out def) && !string.IsNullOrEmpty(def.ExecutablePath))
                    activeExecutables.Add(def.ExecutablePath);
            }

            foreach (var kvp in contextInjectorsByExecutable)
            {
                if (!activeExecutables.Contains(kvp.Key))
                    continue;

                string output = InvokeContextProvider(kvp.Key, kvp.Value);
                if (!string.IsNullOrWhiteSpace(output))
                    results.Add(output.Trim());
            }

            return results;
        }

    /// <summary>
    /// Bakes the context injectors into a user message as it is stored in the
    /// conversation, so the snapshot the model actually saw stays part of the
    /// message forever.
    ///
    /// Injectors are volatile (the focused window and its control tree change between
    /// turns), so each turn gets a fresh one - and the text of an already-sent message
    /// must never change again. A provider reuses its cached prompt prefix only while
    /// the previous prompt is byte-for-byte a prefix of the next one. Appending the
    /// injection to the serialized copy of the last user turn instead made that turn's
    /// stored text differ from what was sent, so the prefix diverged in the middle of
    /// the history and the whole conversation was recomputed every turn. Returns the
    /// user content with the tail appended; idempotent, so re-baking is a no-op.
    /// </summary>
    public static string BakeContextInjection(string userContent, List<string> contextInjections)
    {
        string tail = BuildInjectionTail(contextInjections);
        if (tail.Length == 0)
            return userContent ?? "";

        string text = userContent ?? "";
        if (text.EndsWith(tail, StringComparison.Ordinal))
            return text;

        return text.Length > 0 ? text + "\n\n" + tail : tail;
    }

        public static string BuildInjectionTail(List<string> contextInjections)
        {
            if (contextInjections == null || contextInjections.Count == 0)
                return "";

            List<string> parts = new List<string>();
            foreach (string injection in contextInjections)
            {
                if (!string.IsNullOrEmpty(injection))
                    parts.Add(injection);
            }
            return string.Join("\n\n", parts.ToArray());
        }

        private string InvokeContextProvider(string executablePath, string commandName)
        {
            try
            {
                string stdinData = BuildStdinPayload(executablePath, null);
                ProcessStartInfo psi = BuildToolProcessInfo(executablePath, commandName);

                IntPtr job = IntPtr.Zero;
                using (Process process = Process.Start(psi))
                {
                    job = ProcessJob.CreateToolJob();
                    ProcessJob.AssignToJob(job, process.Id);

                    try
                    {
                        process.StandardInput.Write(stdinData);
                        process.StandardInput.Close();

                        var stdoutTask = Task.Factory.StartNew(() => process.StandardOutput.ReadToEnd());
                        // Drain stderr too, but the context provider's diagnostics
                        // are not part of the injected context: the task is kept so
                        // the read is not collected mid-flight, and its output is
                        // discarded.
                        var stderrDrain = Task.Factory.StartNew(() => process.StandardError.ReadToEnd());
                        process.WaitForExit();

                        string raw = stdoutTask.Result;
                        string text;
                        string imageBase64;
                        string imageMime;
                        ToolResultParser.Parse(raw, out text, out imageBase64, out imageMime);
                        return text;
                    }
                    finally
                    {
                        ProcessJob.CloseJob(job);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    "Context provider '" + commandName + "' failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Builds the stdin JSON shared by tool calls and context injectors:
        /// <c>{ "arguments": ..., "config": { declared options } }</c>.
        /// Only the option names declared in the package's manifest are passed
        /// (current config value, or the manifest default when unset), so API
        /// keys, server URLs, prompts and other unrelated settings never reach
        /// tool processes.
        /// </summary>
        private static ProcessStartInfo BuildToolProcessInfo(string executablePath, string commandName)
        {
            return new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = commandName,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
        }

        internal string BuildStdinPayload(string executablePath, string argumentsJson)
        {
            JObject stdinPayload = new JObject();
            stdinPayload["arguments"] = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JObject()
                : JToken.Parse(argumentsJson);

            JObject configObj = new JObject();
            Dictionary<string, string> declared = null;
            if (executablePath != null
                && optionDefaultsByExecutable.TryGetValue(executablePath, out declared))
            {
                foreach (var kvp in declared)
                {
                    string value = config.GetConfigValue(kvp.Key);
                    configObj[kvp.Key] = string.IsNullOrEmpty(value) ? kvp.Value : value;
                }
            }
            stdinPayload["config"] = configObj;

            return stdinPayload.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Applies the plan-mode allowlist to one tool call.
        /// </summary>
        /// <returns>Refusal text to hand back to the model, or null when allowed.</returns>
        private string EvaluatePlanMode(string toolName, string arguments, ToolDefinition def)
        {
            if (PlanMode.IsForbiddenTool(toolName))
            {
                return PlanMode.FormatRefusal(
                    toolName,
                    "It changes state, so plan mode refuses it.");
            }

            if (PlanMode.IsPlanWriterTool(toolName))
            {
                string target = ReadStringArgument(arguments, PlanMode.PlanWriterPathArgument);
                string workingDirectory = def.ExecutablePath == null
                    ? null
                    : Path.GetDirectoryName(def.ExecutablePath);

                if (PlanMode.IsPlanWriteOutsidePlanFile(toolName, target, workingDirectory))
                {
                    return PlanMode.FormatRefusal(
                        toolName,
                        "It can only write " + PlanMode.PlanFileName
                        + " in the " + PlanMode.PlansDirectoryName + " directory, and '"
                        + (string.IsNullOrEmpty(target) ? "(no path)" : target) + "' is not that file.");
                }
            }

            return null;
        }

        /// <summary>
        /// Reads one string property out of a tool call's argument JSON. Returns
        /// null when the JSON is malformed or the property is absent, which plan
        /// mode treats as a missing path rather than trusting it.
        /// </summary>
        internal static string ReadStringArgument(string argumentsJson, string name)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return null;

            try
            {
                JToken token = JToken.Parse(argumentsJson);
                JToken value = token[name];
                if (value == null || value.Type == JTokenType.Null)
                    return null;
                return value.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Executes a tool by spawning its executable with the tool name as argv[1]
        /// and passing argument JSON + config via stdin.
        /// </summary>
        public void ExecuteToolCall(string toolName, string arguments, out string toolContent, out int exitCode,
            out string imageBase64, out string imageMime)
        {
            toolContent = "";
            exitCode = 0;
            imageBase64 = null;
            imageMime = null;

            ToolDefinition def;
            if (!Tools.TryGetValue(toolName, out def))
            {
                toolContent = FormatCommandResult(toolName, "error: unknown tool '" + toolName + "'.", 1);
                exitCode = 1;
                return;
            }

            // Plan mode refuses anything that could change state, before the tool
            // process is started, so a denied write never reaches the filesystem
            // no matter what the model asked for.
            if (planModeActive)
            {
                string refusal = EvaluatePlanMode(toolName, arguments, def);
                if (refusal != null)
                {
                    toolContent = FormatCommandResult(toolName, refusal, 1);
                    exitCode = 1;
                    return;
                }
            }

            try
            {
                string stdinData = BuildStdinPayload(def.ExecutablePath, arguments);
                ProcessStartInfo psi = BuildToolProcessInfo(def.ExecutablePath, toolName);

                // Determine per-tool timeout from config (key: tooltimeout.<toolname>)
                string timeoutVal = config.GetConfigValue("tooltimeout." + toolName.ToLowerInvariant());
                int timeoutSecs = 0;
                int.TryParse(timeoutVal, out timeoutSecs);
                int timeoutMs = timeoutSecs > 0 ? timeoutSecs * 1000 : 0;

                IntPtr job = IntPtr.Zero;
                using (Process process = Process.Start(psi))
                {
                    // Confine the tool and everything it spawns to a job so
                    // cancel/timeout kills the whole tree (cmd, curl, 7za,
                    // python), not just the tool process itself.
                    job = ProcessJob.CreateToolJob();
                    ProcessJob.AssignToJob(job, process.Id);

                    // Write arguments via stdin
                    process.StandardInput.Write(stdinData);
                    process.StandardInput.Close();

                    // Read stdout/stderr asynchronously to avoid deadlock
                    var stdoutTask = Task.Factory.StartNew(() => process.StandardOutput.ReadToEnd());
                    var stderrTask = Task.Factory.StartNew(() => process.StandardError.ReadToEnd());

                    // Poll instead of blocking so the GUI's STOPTOOL (control pipe)
                    // can interrupt a hung tool; the deadline enforces the per-tool
                    // timeout when one is configured.
                    AbortSupport.RegisterToolProcess(process, job);
                    try
                    {
                        DateTime deadline = timeoutMs > 0
                            ? DateTime.UtcNow.AddMilliseconds(timeoutMs)
                            : DateTime.MaxValue;

                        while (!process.HasExited
                            && !AbortSupport.ToolAbortRequested
                            && DateTime.UtcNow < deadline)
                        {
                            process.WaitForExit(200);
                        }

                        if (AbortSupport.ToolAbortRequested)
                        {
                            ProcessJob.KillJobTree(job);
                            try { process.Kill(); } catch { }
                            toolContent = FormatCommandResult(toolName, "error: tool call was cancelled by the user.", -1);
                            exitCode = -1;
                            return;
                        }

                        if (!process.HasExited)
                        {
                            ProcessJob.KillJobTree(job);
                            try { process.Kill(); } catch { }
                            toolContent = FormatCommandResult(toolName, "error: tool call timed out after " + timeoutSecs + " second(s).", -1);
                            exitCode = -1;
                            return;
                        }

                        string stdout = stdoutTask.Result;
                        string stderr = stderrTask.Result;
                        exitCode = process.ExitCode;

                        string output = stdout;
                        if (!string.IsNullOrEmpty(stderr))
                            output += stderr;

                        string text;
                        ToolResultParser.Parse(output, out text, out imageBase64, out imageMime);
                        toolContent = FormatCommandResult(toolName, text, exitCode);
                    }
                    finally
                    {
                        AbortSupport.UnregisterToolProcess();
                        ProcessJob.CloseJob(job);
                    }
                }
            }
            catch (Exception ex)
            {
                exitCode = -1;
                toolContent = FormatCommandResult(toolName, "error: " + ex.Message, exitCode);
            }
        }

        /// <summary>
        /// Formats tool output consistently.
        /// </summary>
        public static string FormatCommandResult(string command, string output, int exitCode)
        {
            return "Command: " + command + "\nExit Code: " + exitCode + "\nOutput:\n" + output;
        }
    }
}
