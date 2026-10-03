using System;
using System.Collections.Generic;

/// <summary>
/// The pure scheduling decision engine. It walks the task list, fires any task whose
/// next-run time has passed, records the outcome, and advances each task to its next
/// occurrence. It is clock- and runner-injected so it can be unit-tested with a fake
/// "now" and a fake runner.
///
/// Policy:
///   - A task fires when now >= NextRunAtUtc.
///   - One-shot "at" tasks fire even if they were missed while the app was closed.
///   - Recurring tasks missed by more than their own cadence (an interval for "every",
///     a day for "cron") are a catch-up: they are skipped and advanced, not fired.
///   - A task that fails MaxConsecutiveErrors times in a row is auto-disabled.
/// </summary>
public static class ScheduledTaskEngine
{
    public const int DefaultMaxConsecutiveErrors = 3;
    public const int DefaultTimeoutSeconds = 300;
    public const int MaxRecentRuns = 20;

    public sealed class RunResult
    {
        public bool Success;
        public string Output;
        public string Error;
    }

    public sealed class TickResult
    {
        public List<ScheduledTask> Jobs;
        public List<string> RanJobIds;
    }

    public static TickResult Tick(List<ScheduledTask> jobs, DateTime nowUtc, Func<ScheduledTask, RunResult> runJob)
    {
        List<string> ran = new List<string>();
        if (jobs == null)
            return new TickResult { Jobs = new List<ScheduledTask>(), RanJobIds = ran };

        foreach (ScheduledTask job in jobs)
        {
            Schedule.Parsed parsed;
            try
            {
                parsed = Schedule.Parse(job.Schedule, job.CreatedAtUtc);
            }
            catch (FormatException ex)
            {
                job.LastStatus = "error";
                job.LastError = "Bad schedule: " + ex.Message;
                job.AutoDisabled = true;
                job.Enabled = false;
                continue;
            }

            // First tick (or a task whose clock was cleared): compute the next run.
            if (job.NextRunAtUtc == null)
            {
                job.NextRunAtUtc = parsed.NextRunAfter(nowUtc);
                if (job.NextRunAtUtc != null)
                {
                    continue; // future occurrence: wait for it
                }

                if (parsed.Kind == Schedule.Kind.At)
                {
                    // A one-shot alarm whose instant already passed still fires once
                    // (per policy); fall through so it runs now and disables below.
                    job.NextRunAtUtc = parsed.AtUtc;
                }
                else
                {
                    job.AutoDisabled = true;
                    job.Enabled = false;
                    continue;
                }
            }

            if (job.NextRunAtUtc > nowUtc)
                continue; // not due yet

            // Due (possibly overdue).
            if (ShouldRun(job, parsed, nowUtc))
            {
                RunResult result = runJob != null ? runJob(job) : new RunResult { Success = false, Error = "no runner" };
                job.LastRunAtUtc = nowUtc;
                job.LastDurationMs = 0; // the runner does not report duration; the GUI sets it

                if (result.Success)
                {
                    job.LastStatus = "ok";
                    job.LastError = null;
                    job.LastOutputSnippet = MakeSnippet(result.Output);
                    job.ConsecutiveErrors = 0;
                }
                else
                {
                    job.LastStatus = "error";
                    job.LastError = result.Error;
                    job.ConsecutiveErrors++;
                }
                AddRecentRun(job, nowUtc, nowUtc, result.Success ? "ok" : "error", result.Error, MakeSnippet(result.Output));
                ran.Add(job.Id);

                if (job.ConsecutiveErrors >= job.MaxConsecutiveErrors)
                {
                    job.AutoDisabled = true;
                    job.Enabled = false;
                }
            }
            else
            {
                // Catch-up: the window to run it closed, so skip and advance.
                job.LastStatus = "skipped";
            }

            // Advance to the next occurrence strictly after now.
            job.NextRunAtUtc = parsed.NextRunAfter(nowUtc);
            if (job.NextRunAtUtc == null)
            {
                job.AutoDisabled = true;
                job.Enabled = false;
            }
        }

        return new TickResult { Jobs = jobs, RanJobIds = ran };
    }

    private static bool ShouldRun(ScheduledTask job, Schedule.Parsed parsed, DateTime nowUtc)
    {
        if (!job.Enabled)
            return false;
        TimeSpan overdue = nowUtc - job.NextRunAtUtc.Value;
        if (parsed.Kind == Schedule.Kind.At)
            return true; // one-shot alarms fire even if late
        TimeSpan maxOverdue = parsed.Kind == Schedule.Kind.Every ? parsed.Every : TimeSpan.FromDays(1);
        return overdue <= maxOverdue;
    }

    private static void AddRecentRun(ScheduledTask job, DateTime startUtc, DateTime endUtc, string status, string error, string snippet)
    {
        if (job.RecentRuns == null)
            job.RecentRuns = new List<RunRecord>();
        job.RecentRuns.Add(new RunRecord
        {
            StartedAtUtc = startUtc,
            FinishedAtUtc = endUtc,
            Status = status,
            DurationMs = (int)Math.Max(0, (endUtc - startUtc).TotalMilliseconds),
            Error = error,
            OutputSnippet = snippet
        });
        while (job.RecentRuns.Count > MaxRecentRuns)
            job.RecentRuns.RemoveAt(0);
    }

    public static string MakeSnippet(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 300;
        if (flat.Length <= max)
            return flat;
        return flat.Substring(0, max) + "...";
    }
}
