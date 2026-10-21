using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// Persists the list of scheduled tasks (and their run state) in a versioned JSON
/// envelope next to the executable, mirroring how sessions are stored.
/// </summary>
public static class ScheduledTaskStore
{
    public const int CurrentVersion = 1;

    public static string DefaultPath(string baseDir)
    {
        return Path.Combine(baseDir ?? ".", "scheduled-tasks.json");
    }

    private sealed class Envelope
    {
        public int Version;
        public List<ScheduledTask> Tasks;
    }

    // Returns the persisted tasks. A missing file yields an empty list (first run).
    // A present-but-unreadable/corrupt file throws so the caller can refuse to
    // overwrite it, instead of silently clearing every task on the next save.
    public static List<ScheduledTask> Load(string baseDir)
    {
        string path = DefaultPath(baseDir);
        if (!File.Exists(path))
            return new List<ScheduledTask>();

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new IOException("Failed to read scheduled tasks file: " + path, ex);
        }

        Envelope envelope;
        try
        {
            envelope = JsonConvert.DeserializeObject<Envelope>(json);
        }
        catch (Exception ex)
        {
            throw new IOException("Scheduled tasks file is not valid JSON: " + path, ex);
        }

        if (envelope == null || envelope.Tasks == null)
            throw new IOException("Scheduled tasks file is missing its task list: " + path);
        if (envelope.Version != CurrentVersion)
            throw new InvalidOperationException("Unsupported scheduled tasks file version: " + envelope.Version);

        List<ScheduledTask> result = new List<ScheduledTask>();
        foreach (ScheduledTask task in envelope.Tasks)
        {
            if (task == null)
                continue;
            Normalize(task);
            result.Add(task);
        }
        return result;
    }

    // Writes atomically: the payload goes to a sibling .tmp file first, then is
    // renamed/replaced over the real file, so a crash mid-write can never leave a
    // truncated file that the next Load would misread.
    public static bool Save(string baseDir, List<ScheduledTask> tasks)
    {
        string path = DefaultPath(baseDir);
        string tmp = path + ".tmp";
        try
        {
            Envelope envelope = new Envelope
            {
                Version = CurrentVersion,
                Tasks = tasks ?? new List<ScheduledTask>()
            };
            File.WriteAllText(tmp, JsonConvert.SerializeObject(envelope, Formatting.Indented), Encoding.UTF8);
        }
        catch
        {
            DeleteTmp(tmp);
            return false;
        }

        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
            return true;
        }
        catch
        {
            DeleteTmp(tmp);
            return false;
        }
    }

    private static void DeleteTmp(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    // Repairs values that hand-edited (or older) files may leave unset.
    private static void Normalize(ScheduledTask task)
    {
        if (task.RecentRuns == null)
            task.RecentRuns = new List<RunRecord>();
        if (task.MaxConsecutiveErrors <= 0)
            task.MaxConsecutiveErrors = ScheduledTaskEngine.DefaultMaxConsecutiveErrors;
        if (string.IsNullOrEmpty(task.Id))
            task.Id = Guid.NewGuid().ToString("N");
    }

    public static ScheduledTask Create(string name, string schedule, string prompt, string sessionName, DateTime nowUtc)
    {
        string safeSession = sessionName;
        if (string.IsNullOrWhiteSpace(safeSession))
            safeSession = "scheduled-" + (name ?? "task");
        return new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Enabled = true,
            Schedule = schedule,
            Prompt = prompt,
            SessionName = safeSession,
            CreatedAtUtc = nowUtc,
            TimeoutSeconds = 0,
            MaxConsecutiveErrors = ScheduledTaskEngine.DefaultMaxConsecutiveErrors,
            RecentRuns = new List<RunRecord>()
        };
    }
}
