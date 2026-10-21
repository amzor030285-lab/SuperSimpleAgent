using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SuperSimpleAgentGUI
{
    /// <summary>
    /// A saved provider + model combination. Only the values that change when the
    /// operator or the model changes live in a preset: sysprompt, tools, appearance
    /// and the RAG index stay with the user in LLMSettings.ini, so switching provider
    /// can never silently rewrite the assistant's personality or its tool set.
    /// </summary>
    public class LlmPreset
    {
        public string Name { get; set; }
        public string ServerUrl { get; set; }
        public string ApiKey { get; set; }
        public string ChatPath { get; set; }
        public string Model { get; set; }

        /// <summary>0 means "not set": the CLI then sizes its budget from real usage
        /// instead of a percentage of a window (see LLMClient.CapOversizedMessages).</summary>
        public int ContextWindowSize { get; set; }

        public string EmbeddingsEndpoint { get; set; }
        public string EmbeddingsApiKey { get; set; }
        public string EmbeddingsModel { get; set; }
    }

    /// <summary>
    /// Reads and writes LLMPresets.ini, one section per preset.
    ///
    /// Presets deliberately live outside LLMSettings.ini. ConfigHandler and
    /// IniFileHandler flatten an INI into a single key dictionary, so two presets in
    /// one file would overwrite each other, and preset keys would collide with the
    /// live [System] values. Keeping a separate file also means applying a preset
    /// touches no ConfigHandler call site: the values are written into [System] and
    /// [RAG] exactly as the Options dialog already writes them, and the CLI picks
    /// them up through the existing /reload.
    /// </summary>
    public static class PresetStore
    {
        public const string FileName = "LLMPresets.ini";

        /// <summary>Path of the preset file next to the executable.</summary>
        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        /// <summary>Reads every preset, in file order. Returns an empty list if the file is missing.</summary>
        public static List<LlmPreset> Load(string path)
        {
            var presets = new List<LlmPreset>();
            if (!File.Exists(path))
                return presets;

            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            {
                LlmPreset current = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                        continue;

                    if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        current = null;
                        string name = trimmed.Substring(1, trimmed.Length - 2).Trim();
                        if (!IsValidName(name))
                            continue;
                        current = new LlmPreset { Name = name };
                        presets.Add(current);
                        continue;
                    }

                    if (current == null)
                        continue;

                    int eq = trimmed.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    ApplyKey(current, trimmed.Substring(0, eq).Trim(), trimmed.Substring(eq + 1).Trim());
                }
            }

            return presets;
        }

        /// <summary>Rewrites the whole file. Entries with an unusable name are skipped.</summary>
        public static void Save(string path, IEnumerable<LlmPreset> presets)
        {
            var lines = new List<string>();
            lines.Add("# Saved provider + model combinations, managed by Options (System page).");
            lines.Add("# sysprompt, tools, appearance and the RAG index stay in LLMSettings.ini.");
            lines.Add("# Empty embeddings* values mean \"inherit the chat endpoint\".");

            foreach (LlmPreset preset in presets)
            {
                if (preset == null || !IsValidName(preset.Name))
                    continue;

                lines.Add(string.Empty);
                lines.Add("[" + preset.Name.Trim() + "]");
                lines.Add("llmserver=" + (preset.ServerUrl ?? string.Empty));
                lines.Add("apikey=" + SecretProtector.Protect(preset.ApiKey));
                lines.Add("chatpath=" + (preset.ChatPath ?? string.Empty).Trim());
                lines.Add("model=" + (preset.Model ?? string.Empty));
                lines.Add("contextWindowSize=" + preset.ContextWindowSize.ToString(CultureInfo.InvariantCulture));
                lines.Add("embeddingsendpoint=" + (preset.EmbeddingsEndpoint ?? string.Empty));
                lines.Add("embeddingsapikey=" + SecretProtector.Protect(preset.EmbeddingsApiKey));
                lines.Add("embeddingsmodel=" + (preset.EmbeddingsModel ?? string.Empty));
            }

            File.WriteAllLines(path, lines);
        }

        /// <summary>Removes one preset by name. Returns false when no such preset existed.</summary>
        public static bool Delete(string path, string name)
        {
            var presets = Load(path);
            int removed = presets.RemoveAll(delegate (LlmPreset p)
            {
                return string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);
            });

            if (removed == 0)
                return false;

            Save(path, presets);
            return true;
        }

        /// <summary>
        /// A name becomes an INI section header, so brackets and line breaks are
        /// rejected. An empty name is rejected too. Comparison is case-insensitive,
        /// matching IniFileHandler's keys.
        /// </summary>
        private static bool IsValidName(string name)
        {
            return !string.IsNullOrEmpty(name)
                && name.IndexOfAny(new[] { '[', ']', '\r', '\n' }) < 0;
        }

        private static void ApplyKey(LlmPreset preset, string key, string value)
        {
            if (string.Equals(key, "llmserver", StringComparison.OrdinalIgnoreCase))
                preset.ServerUrl = value;
            else if (string.Equals(key, "apikey", StringComparison.OrdinalIgnoreCase))
                preset.ApiKey = SecretProtector.Unprotect(value);
            else if (string.Equals(key, "chatpath", StringComparison.OrdinalIgnoreCase))
                preset.ChatPath = value;
            else if (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
                preset.Model = value;
            else if (string.Equals(key, "contextWindowSize", StringComparison.OrdinalIgnoreCase))
            {
                int size;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size > 0)
                    preset.ContextWindowSize = size;
            }
            else if (string.Equals(key, "embeddingsendpoint", StringComparison.OrdinalIgnoreCase))
                preset.EmbeddingsEndpoint = value;
            else if (string.Equals(key, "embeddingsapikey", StringComparison.OrdinalIgnoreCase))
                preset.EmbeddingsApiKey = SecretProtector.Unprotect(value);
            else if (string.Equals(key, "embeddingsmodel", StringComparison.OrdinalIgnoreCase))
                preset.EmbeddingsModel = value;
        }
    }
}
