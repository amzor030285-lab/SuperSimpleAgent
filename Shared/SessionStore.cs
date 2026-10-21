using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// JSON session persistence for SuperSimpleAgent. Stores a chat conversation as a
/// versioned envelope so a future schema change can migrate old files.
///
/// The GUI never serializes messages itself (the CLI owns the authoritative
/// conversation); it uses this store only to list sessions next to the exe.
/// </summary>
public static class SessionStore
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Hard size limit for the auto-mirrored "lastsession" file. Long chats would
    /// otherwise grow it without bound (each auto-save re-serializes the whole
    /// conversation), which is expensive to write and read on constrained
    /// 32-bit machines.
    /// </summary>
    public const long MaxLastSessionBytes = 1048576;

    /// <summary>Marker prefix carried on the synthetic summary user message after compaction.</summary>
    public const string SummaryMarker = "[Previous conversation summary]";

    public class SessionInfo
    {
        public string Name;          // file name without ".json"
        public string FullPath;
        public DateTime SavedAtUtc;
        public DateTime LastSavedAtUtc;
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
        public DateTime LastSavedAtUtc;
        public List<T> Messages;
    }

    /// <summary>Directory, sanitized name and the full file path for a session file.</summary>
    private static string SessionPath(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SanitizeName(name) + ".json");
    }

    public static bool Save<T>(string dir, string name, List<T> messages, out string fullPath)
    {
        fullPath = null;
        try
        {
            fullPath = SessionPath(dir, name);

            Envelope<T> envelope = new Envelope<T>
            {
                Version = CurrentVersion,
                SavedAtUtc = StableSavedAtUtc<T>(fullPath),
                LastSavedAtUtc = DateTime.UtcNow,
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

    /// <summary>
    /// Saves like <see cref="Save"/>, but keeps the file within
    /// <paramref name="maxBytes"/>: when the full conversation does not fit, the
    /// oldest messages are dropped (the newest tail is kept, which is what matters
    /// for continuity). Used for the auto-mirrored "lastsession" file.
    /// </summary>
    public static bool SaveCapped<T>(string dir, string name, List<T> messages, long maxBytes, out string fullPath)
    {
        fullPath = null;
        try
        {
            fullPath = SessionPath(dir, name);

            DateTime savedAtUtc = StableSavedAtUtc<T>(fullPath);

            int count = messages != null ? messages.Count : 0;

            // Serialize each message once so the oldest can be dropped without
            // re-serializing the whole conversation on every step.
            string[] fragments = new string[count];
            for (int i = 0; i < count; i++)
                fragments[i] = JsonConvert.SerializeObject(messages[i]);

            // Envelope overhead with an empty message list (compact format); the
            // final file is exactly this plus the kept fragments and commas.
            Envelope<T> probe = new Envelope<T>
            {
                Version = CurrentVersion,
                SavedAtUtc = savedAtUtc,
                LastSavedAtUtc = DateTime.UtcNow,
                Messages = new List<T>()
            };
            long budget = maxBytes
                - Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(probe, Formatting.None));

            // Keep the longest suffix (newest messages) that fits the budget.
            int start = count;
            long used = 0;
            for (int i = count - 1; i >= 0; i--)
            {
                long add = Encoding.UTF8.GetByteCount(fragments[i]);
                if (i < count - 1)
                    add++; // separating comma
                if (start < count && used + add > budget)
                    break;
                used += add;
                start = i;
            }

            List<T> kept = new List<T>(count - start);
            for (int i = start; i < count; i++)
                kept.Add(messages[i]);

            Envelope<T> envelope = new Envelope<T>
            {
                Version = CurrentVersion,
                SavedAtUtc = savedAtUtc,
                LastSavedAtUtc = DateTime.UtcNow,
                Messages = kept
            };
            File.WriteAllText(fullPath, JsonConvert.SerializeObject(envelope, Formatting.None), Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The SavedAtUtc of an existing file, or now. Keeping the leading
    /// SavedAtUtc stable across auto-saves stops the header line from churning
    /// on every write; LastSavedAtUtc tracks the most recent write.
    /// </summary>
    private static DateTime StableSavedAtUtc<T>(string fullPath)
    {
        DateTime savedAtUtc = DateTime.UtcNow;
        try
        {
            if (File.Exists(fullPath))
            {
                Envelope<T> previous = JsonConvert.DeserializeObject<Envelope<T>>(
                    File.ReadAllText(fullPath, Encoding.UTF8));
                if (previous != null && previous.SavedAtUtc != DateTime.MinValue)
                    savedAtUtc = previous.SavedAtUtc;
            }
        }
        catch
        {
        }
        return savedAtUtc;
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
                    info.LastSavedAtUtc = envelope.LastSavedAtUtc;
                    if (envelope.Messages != null)
                        info.MessageCount = envelope.Messages.Count;
                }
            }
            catch { }

            result.Add(info);
        }

        result.Sort((a, b) => EffectiveSavedAtUtc(b).CompareTo(EffectiveSavedAtUtc(a)));
        return result;
    }

    /// <summary>Most recent write time; falls back to the stable header time for old files.</summary>
    public static DateTime EffectiveSavedAtUtc(SessionInfo info)
    {
        if (info == null)
            return DateTime.MinValue;
        return info.LastSavedAtUtc != DateTime.MinValue ? info.LastSavedAtUtc : info.SavedAtUtc;
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