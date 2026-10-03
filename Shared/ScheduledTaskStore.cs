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

    public static List<ScheduledTask> Load(string baseDir)
    {
        List<ScheduledTask> result = new List<ScheduledTask>();
        try
        {
            string path = DefaultPath(baseDir);
            if (!File.Exists(path))
                return result;

            Envelope envelope = JsonConvert.DeserializeObject<Envelope>(File.ReadAllText(path, Encoding.UTF8));
            if (envelope != null && envelope.Tasks != null)
                result = envelope.Tasks;
        }
        catch
        {
        }
        return result;
    }

    public static bool Save(string baseDir, List<ScheduledTask> tasks)
    {
        try
        {
            string path = DefaultPath(baseDir);
            Envelope envelope = new Envelope
            {
                Version = CurrentVersion,
                Tasks = tasks ?? new List<ScheduledTask>()
            };
            File.WriteAllText(path, JsonConvert.SerializeObject(envelope, Formatting.Indented), Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
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
            MaxConsecutiveErrors = 3,
            RecentRuns = new List<RunRecord>()
        };
    }
}
