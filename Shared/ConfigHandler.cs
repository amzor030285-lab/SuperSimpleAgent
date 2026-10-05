using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>
/// Shared configuration handler for both CLI and GUI projects.
/// Wraps IniFileHandler with typed getters for common settings.
/// </summary>
public class ConfigHandler
{
    private Dictionary<string, string> configMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ConfigHandler(string filename)
    {
        LoadConfig(filename);
    }

    public void LoadConfig(string filename)
    {
        configMap = IniFileHandler.LoadIni(filename);
    }

    public string GetConfigValue(string key, string defaultValue = "")
    {
        string value = configMap.ContainsKey(key) ? configMap[key] : defaultValue;
        return SecretProtector.IsSecretKey(key) ? SecretProtector.Unprotect(value) : value;
    }

    public int GetConfigInt(string key, int defaultValue)
    {
        if (configMap.ContainsKey(key) && int.TryParse(configMap[key], out int result))
            return result;
        return defaultValue;
    }

    public bool GetConfigBool(string key, bool defaultValue = false)
    {
        if (configMap.ContainsKey(key) && int.TryParse(configMap[key], out int result))
            return result == 1;
        return defaultValue;
    }

    public List<string> GetConfigList(string key, string defaultValue = "")
    {
        string value;
        if (!configMap.TryGetValue(key, out value))
            value = defaultValue ?? "";

        var list = new List<string>();
        foreach (string token in value.Split(','))
        {
            string trimmed = token.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                list.Add(trimmed);
        }
        return list;
    }

    public ChatBlockDisplayMode GetChatBlockDisplayMode(string key, ChatBlockDisplayMode defaultValue)
    {
        ChatBlockDisplayMode result;
        if (configMap.ContainsKey(key) && Enum.TryParse(configMap[key], true, out result))
            return result;
        return defaultValue;
    }

    /// <summary>
    /// Decodes stored prompts by removing outer wrapping quotes and unescaping all C-style escape sequences.
    /// Preserves inner/trailing quotes verbatim.
    /// </summary>
    public static string DecodeStoredPrompt(string storedPrompt)
    {
        if (storedPrompt == null)
            return string.Empty;

        // Remove only the outer wrapping quotes; preserve inner/trailing quotes verbatim.
        if (storedPrompt.Length >= 2 && storedPrompt[0] == '"' && storedPrompt[storedPrompt.Length - 1] == '"')
        {
            storedPrompt = storedPrompt.Substring(1, storedPrompt.Length - 2);
        }

        // Unescape all C-style escape sequences (\n, \t, \r, \\, etc.)
        return Regex.Unescape(storedPrompt);
    }
}
