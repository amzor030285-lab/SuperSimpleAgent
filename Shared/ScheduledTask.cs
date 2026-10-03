using System;
using System.Collections.Generic;

// A scheduled task and its run history. All times are UTC.

public sealed class RunRecord
{
    public DateTime StartedAtUtc;
    public DateTime FinishedAtUtc;
    public string Status;        // "ok" / "error" / "skipped"
    public int DurationMs;
    public string Error;
    public string OutputSnippet;
}

public sealed class ScheduledTask
{
    public string Id;
    public string Name;
    public bool Enabled;
    public string Schedule;      // raw expression: "at ...", "every ...", or cron / "@alias"
    public string Prompt;        // the message sent to the model when the task fires
    public string SessionName;    // the separate session file the run is appended to
    public DateTime CreatedAtUtc;
    public int TimeoutSeconds;    // 0 = use the default
    public int MaxConsecutiveErrors;

    // Runtime state, persisted alongside the task.
    public DateTime? NextRunAtUtc;
    public DateTime? LastRunAtUtc;
    public string LastStatus;
    public string LastError;
    public string LastOutputSnippet;
    public int LastDurationMs;
    public int ConsecutiveErrors;
    public bool AutoDisabled;
    public List<RunRecord> RecentRuns;

    // A defensive copy so the UI never reads a live task while the worker thread
    // mutates it. RecentRuns is copied (its RunRecords are never mutated after creation).
    public ScheduledTask Clone()
    {
        return new ScheduledTask
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            Schedule = Schedule,
            Prompt = Prompt,
            SessionName = SessionName,
            CreatedAtUtc = CreatedAtUtc,
            TimeoutSeconds = TimeoutSeconds,
            MaxConsecutiveErrors = MaxConsecutiveErrors,
            NextRunAtUtc = NextRunAtUtc,
            LastRunAtUtc = LastRunAtUtc,
            LastStatus = LastStatus,
            LastError = LastError,
            LastOutputSnippet = LastOutputSnippet,
            LastDurationMs = LastDurationMs,
            ConsecutiveErrors = ConsecutiveErrors,
            AutoDisabled = AutoDisabled,
            RecentRuns = RecentRuns != null ? new List<RunRecord>(RecentRuns) : null
        };
    }
}
