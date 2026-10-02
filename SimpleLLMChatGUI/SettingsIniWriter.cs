using System;
using System.Collections.Generic;
using System.IO;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Shared LLMSettings.ini writer for Options and the first-run onboarding wizard.
    /// </summary>
    public static class SettingsIniWriter
    {
        /// <summary>
        /// Escapes a system prompt for INI storage (backslash, newlines, tabs).
        /// </summary>
        public static string EscapePromptForStorage(string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
                return string.Empty;

            return prompt.Replace("\\", "\\\\")
                        .Replace("\r\n", "\\n")
                        .Replace("\n", "\\n")
                        .Replace("\r", "\\r")
                        .Replace("\t", "\\t");
        }

        private static void AddIniSection(List<string> allLines, string name, List<string> lines)
        {
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            if (allLines.Count > 0)
                allLines.Add(string.Empty);
            allLines.Add("[" + name + "]");
            allLines.AddRange(lines);
        }

        /// <summary>
        /// Writes an INI file from an ordered list of (section name, lines) pairs.
        /// This is the single place that touches disk for LLMSettings.ini.
        /// </summary>
        public static void WriteSections(string path, IEnumerable<KeyValuePair<string, List<string>>> sections)
        {
            var allLines = new List<string>();
            foreach (var section in sections)
                AddIniSection(allLines, section.Key, section.Value);
            File.WriteAllLines(path, allLines);
        }

        /// <summary>
        /// Writes a complete LLMSettings.ini with System values from onboarding
        /// and defaults for Appearance, RAG, and Tools.
        /// </summary>
        public static void WriteInitialConfig(
            string path,
            string apiKey,
            string llmServer,
            string model,
            string sysPrompt,
            int contextWindowSize)
        {
            var sections = new List<KeyValuePair<string, List<string>>>
            {
                new KeyValuePair<string, List<string>>("Appearance", GetDefaultAppearanceSettings()),
                new KeyValuePair<string, List<string>>("RAG", GetDefaultRagSettings()),
                // chatpath stays empty on a fresh install: the CLI detects the path from
                // llmserver, which is right for every provider that keeps the version in
                // the base URL.
                new KeyValuePair<string, List<string>>("System", GetSystemSettings(apiKey, llmServer, string.Empty, model, sysPrompt, contextWindowSize)),
                new KeyValuePair<string, List<string>>("Tools", GetDefaultToolSettings()),
            };
            WriteSections(path, sections);
        }

        public static List<string> GetSystemSettings(
            string apiKey,
            string llmServer,
            string chatPath,
            string model,
            string sysPrompt,
            int contextWindowSize,
            bool repetitionGuard = false,
            int requestTimeout = 300,
            int streamStallTimeout = 120)
        {
            return new List<string>
            {
                "apikey=" + SecretProtector.Protect(apiKey),
                "chatpath=" + (chatPath ?? string.Empty).Trim(),
                "contextWindowSize=" + contextWindowSize,
                "llmserver=" + (llmServer ?? string.Empty),
                "model=" + (model ?? string.Empty),
                "repetitionguard=" + (repetitionGuard ? "1" : "0"),
                "requesttimeout=" + requestTimeout,
                "streamstalltimeout=" + streamStallTimeout,
                "sysprompt=\"" + EscapePromptForStorage(sysPrompt) + "\"",
            };
        }

        private static List<string> GetDefaultAppearanceSettings()
        {
            return new List<string>
            {
                "assistantname=" + AppConstants.DefaultAssistantName,
                "codeblockfontfamily=",
                "customfontfamily=",
                "fontsize=" + AppConstants.DefaultChatFontSize,
                "markdownparsing=1",
                "thinkingdisplay=collapsed",
                "toolcalldisplay=collapsed",
                "tooloutputdisplay=shown",
            };
        }

        private static List<string> GetDefaultRagSettings()
        {
            string allowedExt = RagExtensionList.FormatForStorage(AppConstants.DefaultRagAllowedExtensions);
            return new List<string>
            {
                "ragenabled=0",
                "ragallowedextensions=" + allowedExt,
                "indexchunkoverlap=10",
                "indexchunklines=60",
                "embeddingsapikey=",
                "embeddingsendpoint=",
                "embeddingsmodel=",
                "ragknowledgepath=",
                "ragmaxsnippetlength=2000",
                "ragmaxresults=5",
                "ragretrievemode=newchat",
            };
        }

        private static List<string> GetDefaultToolSettings()
        {
            return new List<string>
            {
                "tools=",
                "toolsrequiringapproval=" + AppConstants.DefaultToolsRequiringApproval,
            };
        }
    }
}
