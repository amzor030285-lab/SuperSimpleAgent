using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// JSON session persistence for SimpleLLMChat. Stores a chat conversation as a
/// versioned envelope so a future schema change can migrate old files.
///
/// The GUI never serializes messages itself (the CLI owns the authoritative
/// conversation); it uses this store only to list sessions next to the exe.
/// </summary>
public static class SessionStore
{
    public const int CurrentVersion = 1;

    /// <summary>Marker prefix carried on the synthetic summary user message after compaction.</summary>
    public const string SummaryMarker = "[Previous conversation summary]";

    public class SessionInfo
    {
        public string Name;          // file name without ".json"
        public string FullPath;
        public DateTime SavedAtUtc;
        public int MessageCount;
        public long SizeBytes;
    }

    public static string DefaultDirectory(string baseDir)
    {
        return Path.Combine(baseDir ?? ".", "sessions");
    }

    /// <summary>Sanitizes a user-supplied session name to a safe file-name base.</summary>
    public static string SanitizeName(string name)
    {
        if (name == null)
            return "session";

        name = name.Trim();
        if (name.Length == 0)
            return "session";

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder sb = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            sb.Append(Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i]);
        }

        string result = sb.ToString().Trim();
        return result.Length == 0 ? "session" : result;
    }

    private sealed class Envelope<T>
    {
        public int Version;
        public DateTime SavedAtUtc;
        public List<T> Messages;
    }

    public static bool Save<T>(string dir, string name, List<T> messages, out string fullPath)
    {
        fullPath = null;
        try
        {
            Directory.CreateDirectory(dir);
            string safe = SanitizeName(name);
            fullPath = Path.Combine(dir, safe + ".json");
            Envelope<T> envelope = new Envelope<T>
            {
                Version = CurrentVersion,
                SavedAtUtc = DateTime.UtcNow,
                Messages = messages ?? new List<T>()
            };
            File.WriteAllText(fullPath, JsonConvert.SerializeObject(envelope, Formatting.Indented), Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Load<T>(string dir, string name, out List<T> messages)
    {
        messages = null;
        try
        {
            string fullPath = Path.Combine(dir, SanitizeName(name) + ".json");
            if (!File.Exists(fullPath))
                return false;

            string json = File.ReadAllText(fullPath, Encoding.UTF8);
            Envelope<T> envelope = JsonConvert.DeserializeObject<Envelope<T>>(json);
            messages = envelope != null && envelope.Messages != null ? envelope.Messages : new List<T>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lists sessions newest-first. Ignores unreadable/corrupt files.</summary>
    public static List<SessionInfo> List(string dir)
    {
        List<SessionInfo> result = new List<SessionInfo>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return result;

        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            SessionInfo info = new SessionInfo
            {
                Name = name,
                FullPath = file,
                SavedAtUtc = DateTime.MinValue,
                MessageCount = 0,
                SizeBytes = 0
            };

            try
            {
                info.SizeBytes = new FileInfo(file).Length;
            }
            catch { }

            try
            {
                Envelope<object> envelope =
                    JsonConvert.DeserializeObject<Envelope<object>>(File.ReadAllText(file, Encoding.UTF8));
                if (envelope != null)
                {
                    info.SavedAtUtc = envelope.SavedAtUtc;
                    if (envelope.Messages != null)
                        info.MessageCount = envelope.Messages.Count;
                }
            }
            catch { }

            result.Add(info);
        }

        result.Sort((a, b) => b.SavedAtUtc.CompareTo(a.SavedAtUtc));
        return result;
    }

    public static bool Delete(string dir, string name)
    {
        try
        {
            string fullPath = Path.Combine(dir, SanitizeName(name) + ".json");
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Renames a session. Returns an error message, or null on success.</summary>
    public static string Rename(string dir, string oldName, string newName)
    {
        try
        {
            string oldPath = Path.Combine(dir, SanitizeName(oldName) + ".json");
            string newPath = Path.Combine(dir, SanitizeName(newName) + ".json");
            if (!File.Exists(oldPath))
                return "Source session not found.";
            if (File.Exists(newPath))
                return "A session with that name already exists.";
            File.Move(oldPath, newPath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}