using Newtonsoft.Json.Linq;
using SuperSimpleAgentCLI;
using SuperSimpleAgentCLI.RAG;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SuperSimpleAgentCLI.Tests
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            // The suite EXE doubles as a fake curl.exe for the curl-fallback tests
            // (it is copied to curl.exe next to the CLI under test). The mode env
            // var selects it: the CLI launches curl with its own fixed arguments.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAKE_CURL_MODE")))
                return FakeCurlMain(args);
            return SuiteMain.Run("SuperSimpleAgentCLI", args, Run);
        }

        static void Run()
        {
            TestRunner.Run("ConsecutiveToolFailures.streak", () =>
            {
                int streak = 0;

                ConsecutiveToolFailures.Record(ref streak, 0, true, 1);
                TestAssert.Equal(0, streak, "limit 0 ignores failure");

                ConsecutiveToolFailures.Record(ref streak, 3, true, 1);
                ConsecutiveToolFailures.Record(ref streak, 3, true, 1);
                TestAssert.Equal(2, streak, "two failures");

                ConsecutiveToolFailures.Record(ref streak, 3, false, 1);
                TestAssert.Equal(2, streak, "non-run leaves streak");

                ConsecutiveToolFailures.Record(ref streak, 3, true, 0);
                TestAssert.Equal(0, streak, "success resets");

                ConsecutiveToolFailures.Record(ref streak, 3, true, 1);
                ConsecutiveToolFailures.Record(ref streak, 3, true, 2);
                ConsecutiveToolFailures.Record(ref streak, 3, true, -1);
                TestAssert.Equal(3, streak, "three failures");

                ConsecutiveToolFailures.Record(ref streak, 3, true, 0);
                TestAssert.Equal(0, streak, "later success clears a reached limit");
            });

            // --- Schedule parser: at / every / cron ----------------------------------

            TestRunner.Run("Schedule.at_relative_offset", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("at 20m", now);
                TestAssert.True(p.Kind == Schedule.Kind.At, "kind At");
                TestAssert.True(p.AtUtc == now.AddMinutes(20), "resolved to now+20m");
                TestAssert.True(p.NextRunAfter(now).Value == now.AddMinutes(20), "next run is now+20m");
                TestAssert.True(p.NextRunAfter(now.AddMinutes(21)) == null, "passed one-shot has no next run");
            });

            TestRunner.Run("Schedule.at_absolute_utc", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("at 2026-10-05T10:00:00Z", now);
                DateTime expected = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
                TestAssert.True(p.AtUtc == expected, "absolute instant parsed as UTC");
                TestAssert.True(p.NextRunAfter(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc)).Value == expected, "next run before instant");
                TestAssert.True(p.NextRunAfter(new DateTime(2026, 10, 5, 10, 0, 1, DateTimeKind.Utc)) == null, "past instant has no next run");
            });

            TestRunner.Run("Schedule.every_grid", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("every 10m", now);
                TestAssert.True(p.Kind == Schedule.Kind.Every, "kind Every");
                TestAssert.True(p.Every == TimeSpan.FromMinutes(10), "interval is 10m");
                TestAssert.True(p.NextRunAfter(now).Value == now.AddMinutes(10), "first fire is +10m");
                TestAssert.True(p.NextRunAfter(now.AddMinutes(5)).Value == now.AddMinutes(10), "next on the grid");
                TestAssert.True(p.NextRunAfter(now.AddMinutes(10)).Value == now.AddMinutes(20), "strictly after");
                TestAssert.True(p.NextRunAfter(now.AddMinutes(15)).Value == now.AddMinutes(20), "next grid slot");
            });

            TestRunner.Run("Schedule.cron_weekday_time", () =>
            {
                // 2026-01-01 is a Thursday. "0 9 * * 1 UTC" = Monday 09:00 UTC.
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("cron 0 9 * * 1 UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), "next Monday 09:00");
            });

            TestRunner.Run("Schedule.cron_today_if_still_ahead", () =>
            {
                // Friday 14:30 UTC; reference is the same day before 14:30, so it fires today.
                DateTime now = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc); // a Friday
                Schedule.Parsed p = Schedule.Parse("30 14 * * 5 UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc), "fires later today");
            });

            TestRunner.Run("Schedule.cron_first_of_month", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("0 0 1 * * UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), "next 1st of month");
            });

            TestRunner.Run("Schedule.cron_step_minutes", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 10, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("*/15 * * * * UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 1, 8, 15, 0, DateTimeKind.Utc), "next 15-minute mark");
            });

            TestRunner.Run("Schedule.cron_hour_range_step", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("0 9-17/2 * * * UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), "9:00");
                TestAssert.True(p.NextRunAfter(new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)).Value == new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc), "11:00");
            });

            TestRunner.Run("Schedule.cron_dom_dow_or_rule", () =>
            {
                // "0 0 1 * 1 UTC" = day 1 OR Monday. After 2026-01-01 08:00 the next
                // candidate is 2026-01-05 (Monday); the next day-1 is 2026-02-01.
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("0 0 1 * 1 UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), "OR rule picks the Monday");
            });

            TestRunner.Run("Schedule.cron_names", () =>
            {
                // Sunday 00:00 UTC by name; reference is 2026-01-01 (Thursday).
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("0 0 * * SUN UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc), "Sunday by name");

                // "7" is Sunday; Sunday 12:00 UTC.
                Schedule.Parsed p7 = Schedule.Parse("0 12 * * 7 UTC", now);
                TestAssert.True(p7.NextRunAfter(now).Value == new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc), "7 maps to Sunday");
            });

            TestRunner.Run("Schedule.cron_month_name_range", () =>
            {
                // "0 0 1 JAN * UTC" = Jan 1 00:00 UTC; reference in February -> next is 2027.
                DateTime now = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
                Schedule.Parsed p = Schedule.Parse("0 0 1 JAN * UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "rolls into next year");
            });

            TestRunner.Run("Schedule.cron_aliases", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 10, 0, DateTimeKind.Utc);
                TestAssert.True(Schedule.Parse("@hourly UTC", now).NextRunAfter(now).Value == new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), "@hourly");
                TestAssert.True(Schedule.Parse("@daily UTC", now).NextRunAfter(now).Value == new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), "@daily");
            });

            TestRunner.Run("Schedule.cron_bare_and_bare_with_tz", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                // No keyword: five fields is a cron expression.
                TestAssert.True(Schedule.Parse("0 9 * * 1", now).Kind == Schedule.Kind.Cron, "bare 5-field is cron");
                // Six tokens: five fields plus a time zone id.
                Schedule.Parsed p = Schedule.Parse("0 9 * * 1 UTC", now);
                TestAssert.True(p.NextRunAfter(now).Value == new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc), "cron with explicit UTC zone");
            });

            TestRunner.Run("Schedule.rejects_malformed", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                string[] bad = { "", "nonsense", "every 10", "at 20x", "cron 60 9 * * 1", "cron 0 9 * * 8", "cron 1 2 3 4" };
                for (int i = 0; i < bad.Length; i++)
                {
                    bool threw = false;
                    try { Schedule.Parse(bad[i], now); }
                    catch (FormatException) { threw = true; }
                    TestAssert.True(threw, "expected FormatException for '" + bad[i] + "'");
                }
            });

            TestRunner.Run("Schedule.rejects_unknown_timezone", () =>
            {
                DateTime now = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
                bool threw = false;
                try { Schedule.Parse("cron 0 9 * * 1 Mars/Phobos", now); }
                catch (FormatException) { threw = true; }
                TestAssert.True(threw, "unknown time zone must be rejected");
            });

            TestRunner.Run("Schedule.cron_dst_and_leap_year", () =>
            {
                // A daily 09:00 job in a DST zone: the same wall-clock hour maps to a
                // different UTC instant in winter (EST, UTC-5) than in summer (EDT, UTC-4).
                Schedule.Parsed daily = Schedule.Parse(
                    "0 9 * * * America/New_York", new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc));
                TestAssert.True(
                    daily.NextRunAfter(new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc)).Value
                        == new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc),
                    "winter 09:00 local is 14:00 UTC (EST)");
                TestAssert.True(
                    daily.NextRunAfter(new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc)).Value
                        == new DateTime(2026, 7, 15, 13, 0, 0, DateTimeKind.Utc),
                    "summer 09:00 local is 13:00 UTC (EDT)");

                // Feb 29 only exists in leap years: a non-leap reference must skip
                // 2026 and 2027 and land on 2028.
                Schedule.Parsed leap = Schedule.Parse(
                    "0 0 29 2 * UTC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                TestAssert.True(
                    leap.NextRunAfter(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Value
                        == new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc),
                    "skips 2026/2027 to the next leap year");
            });

            // --- Scheduled task store + engine ---------------------------------------

            TestRunner.Run("ScheduledTaskStore.roundtrips", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "sllc-sched-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    List<ScheduledTask> jobs = new List<ScheduledTask>();
                    jobs.Add(ScheduledTaskStore.Create("digest", "cron 0 9 * * * UTC", "summarize today", "digest", created));
                    ScheduledTaskStore.Save(dir, jobs);

                    List<ScheduledTask> loaded = ScheduledTaskStore.Load(dir);
                    TestAssert.True(loaded.Count == 1, "one task loaded");
                    TestAssert.True(loaded[0].Name == "digest", "name");
                    TestAssert.True(loaded[0].Schedule == "cron 0 9 * * * UTC", "schedule");
                    TestAssert.True(loaded[0].Prompt == "summarize today", "prompt");
                    TestAssert.True(loaded[0].SessionName == "digest", "session");
                    TestAssert.True(loaded[0].CreatedAtUtc == created, "created");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            TestRunner.Run("ScheduledTaskStore.missing_file_is_empty", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "sllc-sched-none-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    TestAssert.True(ScheduledTaskStore.Load(dir).Count == 0, "no file -> empty list");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            TestRunner.Run("Engine.at_fires_once_then_disables", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("alarm", "at 2026-01-01T00:30:00Z", "wake me", "alarm", created));
                string id = jobs[0].Id;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "ran" };

                ScheduledTaskEngine.TickResult first = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(first.RanJobIds.Count == 0, "first tick only establishes the next run");
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc), "next run is the alarm time");

                ScheduledTaskEngine.TickResult fire = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 31, 0, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(fire.RanJobIds.Contains(id), "fires when due");
                TestAssert.True(jobs[0].LastStatus == "ok", "status ok");
                TestAssert.True(jobs[0].AutoDisabled && !jobs[0].Enabled, "one-shot disables itself");

                ScheduledTaskEngine.TickResult again = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(again.RanJobIds.Count == 0, "does not fire twice");
            });

            TestRunner.Run("Engine.at_missed_alarm_fires_once", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("missed", "at 2026-01-01T00:30:00Z", "late alarm", "missed", created));
                string id = jobs[0].Id;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "late" };

                // The app was closed past 00:30; the first tick at 01:00 must still
                // fire the missed alarm once, then disable it.
                ScheduledTaskEngine.TickResult first = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(first.RanJobIds.Contains(id), "missed alarm fires on first tick");
                TestAssert.True(jobs[0].LastStatus == "ok", "status ok");
                TestAssert.True(jobs[0].AutoDisabled && !jobs[0].Enabled, "disables after the missed run");

                ScheduledTaskEngine.TickResult again = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 1, 1, 0, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(again.RanJobIds.Count == 0, "does not fire twice");
            });

            TestRunner.Run("Engine.every_fires_and_advances", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("beat", "every 10m", "ping", "beat", created));
                string id = jobs[0].Id;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "pung" };

                ScheduledTaskEngine.Tick(jobs, created, (j) => ok);
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc), "establishes +10m");

                ScheduledTaskEngine.TickResult fire = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 10, 30, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(fire.RanJobIds.Contains(id), "fires on the grid");
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 1, 0, 20, 0, DateTimeKind.Utc), "advances to +20m");
            });

            TestRunner.Run("Engine.cron_fires_when_due", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("daily", "cron 0 9 * * * UTC", "morning brief", "daily", created));
                string id = jobs[0].Id;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "brief" };

                ScheduledTaskEngine.Tick(jobs, created, (j) => ok);
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), "next is 09:00");

                ScheduledTaskEngine.TickResult fire = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 9, 0, 30, DateTimeKind.Utc), (j) => ok);
                TestAssert.True(fire.RanJobIds.Contains(id), "fires at 09:00");
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), "advances to next day");
            });

            TestRunner.Run("Engine.catchup_skips_overdue_every", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("beat", "every 10m", "ping", "beat", created));
                bool ran = false;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "pung" };

                ScheduledTaskEngine.Tick(jobs, created, (j) => ok); // next = 00:10
                ScheduledTaskEngine.TickResult skip = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc), (j) => { ran = true; return ok; });
                TestAssert.True(!ran, "missed window is skipped, not run");
                TestAssert.True(jobs[0].LastStatus == "skipped", "recorded as skipped");
                TestAssert.True(jobs[0].NextRunAtUtc != null && jobs[0].NextRunAtUtc.Value == new DateTime(2026, 1, 1, 0, 40, 0, DateTimeKind.Utc), "advances past the miss");
            });

            TestRunner.Run("Engine.auto_disable_after_errors", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("flaky", "every 1m", "do it", "flaky", created));
                ScheduledTaskEngine.RunResult err = new ScheduledTaskEngine.RunResult { Success = false, Error = "boom" };

                ScheduledTaskEngine.Tick(jobs, created, (j) => err); // next = 00:01
                for (int i = 0; i < 3; i++)
                {
                    DateTime t = new DateTime(2026, 1, 1, 0, i * 2 + 2, 0, DateTimeKind.Utc); // 00:02, 00:04, 00:06
                    ScheduledTaskEngine.Tick(jobs, t, (j) => err);
                }
                TestAssert.True(jobs[0].ConsecutiveErrors == 3, "three errors");
                TestAssert.True(jobs[0].AutoDisabled && !jobs[0].Enabled, "auto-disabled");

                bool ranAfterDisable = false;
                ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 8, 0, DateTimeKind.Utc), (j) => { ranAfterDisable = true; return err; });
                TestAssert.True(!ranAfterDisable, "disabled task does not run");
            });

            TestRunner.Run("Engine.not_due_does_not_run", () =>
            {
                DateTime created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                List<ScheduledTask> jobs = new List<ScheduledTask>();
                jobs.Add(ScheduledTaskStore.Create("hourly", "every 1h", "tick", "hourly", created));
                bool ran = false;
                ScheduledTaskEngine.RunResult ok = new ScheduledTaskEngine.RunResult { Success = true, Output = "t" };

                ScheduledTaskEngine.Tick(jobs, created, (j) => ok); // next = 01:00
                ScheduledTaskEngine.TickResult r = ScheduledTaskEngine.Tick(jobs, new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc), (j) => { ran = true; return ok; });
                TestAssert.True(!ran && r.RanJobIds.Count == 0, "not due -> no run");
            });

            TestRunner.Run("cli.session_flag_targets_dedicated_session", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Scheduled reply.";
                    server.StreamDelayMs = 0;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path,
                        "--session digest \"run the digest\""))
                    {
                        CliRunner.WaitForText(cli, "Scheduled reply.", 30000);
                        // The reply streams, so wait for the one-shot process to fully
                        // exit (which persists the session) before disposing.
                        Stopwatch sw = Stopwatch.StartNew();
                        while (!cli.HasExited && sw.ElapsedMilliseconds < 30000)
                            Thread.Sleep(50);
                        // A hang would leave the session unsaved with an empty reply;
                        // assert the child actually exited so a hang is a clear failure.
                        TestAssert.True(cli.HasExited, "one-shot --session CLI exited instead of hanging");
                    }

                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    List<JObject> digest = null;
                    TestAssert.True(SessionStore.Load(sessionsDir, "digest", out digest),
                        "the --session run was saved to its dedicated session");
                    TestAssert.True(digest != null && digest.Count >= 2,
                        "user + assistant messages stored");
                    if (digest != null && digest.Count >= 1)
                    {
                        string content = (string)digest[0]["Content"];
                        TestAssert.True(content != null && content.IndexOf("run the digest", StringComparison.Ordinal) >= 0,
                            "the job prompt is stored in the session");
                    }

                    // The reserved lastsession mirror must not be clobbered by a
                    // --session one-shot run.
                    List<JObject> last = null;
                    TestAssert.True(!SessionStore.Load(sessionsDir, "lastsession", out last)
                        || (last != null && last.Count == 0),
                        "lastsession untouched by a --session run");
                }
            });

            // --- RepetitionGuard -----------------------------------------------------
            TestRunner.Run("RepetitionGuard.detects_degenerate_loop", () =>
          {
              RepetitionGuard guard = new RepetitionGuard();
              // A model looping on a single sentence, streamed token by token.
              for (int i = 0; i < 400; i++)
                  guard.Append("the quick brown fox jumps ");

              TestAssert.True(guard.CheckNow(), "loop must trip");
              TestAssert.True(guard.Reason.Length > 0, "reason reported");
          });

          TestRunner.Run("RepetitionGuard.tolerates_normal_text", () =>
          {
              RepetitionGuard guard = new RepetitionGuard();
              string prose = "Here is a list of the configuration keys I found in your file. "
                  + "First, llmserver names the endpoint. Second, apikey holds the credential. "
                  + "Third, streamstalltimeout bounds a silent stream. "
                  + "Fourth, requesttimeout bounds the whole request. "
                  + "Each key is documented in the README under configuration.";
              for (int i = 0; i < 4; i++)
                  guard.Append(prose);

              TestAssert.True(!guard.CheckNow(), "prose must not trip");
          });

          TestRunner.Run("RepetitionGuard.tolerates_structure_that_repeats", () =>
          {
              // A generated table or config legitimately repeats one row many times.
              // 1600 characters of a 16-character row is a long, perfectly valid
              // response, so the guard must not fire on repetition alone.
              RepetitionGuard guard = new RepetitionGuard();
              for (int i = 0; i < 100; i++)
                  guard.Append("| cell | cell |\n");

              TestAssert.True(!guard.CheckNow(), "repeated table rows must not trip");
          });

          TestRunner.Run("RepetitionGuard.interruption_breaks_the_run", () =>
          {
              // Same repeating block, but something else lands in the middle often
              // enough to keep every unbroken run short.
              RepetitionGuard guard = new RepetitionGuard();
              for (int i = 0; i < 100; i++)
              {
                  guard.Append("| cell | cell |\n");
                  guard.Append("| " + i + " | value |\n");
              }

              TestAssert.True(!guard.CheckNow(), "interrupted repeats must not trip");
          });

          TestRunner.Run("RepetitionGuard.ignores_empty_and_stops_after_trip", () =>
          {
              RepetitionGuard guard = new RepetitionGuard();
              guard.Append("");
              guard.Append(null);
              TestAssert.True(!guard.CheckNow(), "empty input does not trip");

              for (int i = 0; i < 400; i++)
                  guard.Append("loop loop loop loop ");

              TestAssert.True(guard.CheckNow(), "loop trips");
              string reason = guard.Reason;
              guard.Append("more content after the trip");
              TestAssert.Equal(reason, guard.Reason, "reason frozen after trip");
          });

          TestRunner.Run("RepetitionGuard.finds_period_not_in_any_fixed_list", () =>
          {
              // The block length is arbitrary, so the guard has to discover the
              // period from the text rather than test a hard-coded set.
              RepetitionGuard guard = new RepetitionGuard();
              for (int i = 0; i < 800; i++)
                  guard.Append("abcdefg ");

              TestAssert.True(guard.CheckNow(), "8-char period found");
          });

          TestRunner.Run("RepetitionGuard.streams_trip_before_response_ends", () =>
          {
              // The guard has to trip on the way in, not only when the stream ends:
              // the whole point is to cut a runaway response short.
              RepetitionGuard guard = new RepetitionGuard();
              int trippedAt = -1;
              for (int i = 0; i < 800; i++)
              {
                  guard.Append("abcdefg ");
                  if (guard.Tripped && trippedAt < 0)
                      trippedAt = i;
              }

              TestAssert.True(trippedAt >= 0, "trips while streaming");
              TestAssert.True(trippedAt < 800, "trips before the end of the stream");
          });

          TestRunner.Run("RepetitionGuard.cost_stays_flat_for_large_responses", () =>
          {
              // Append must not grow with the response: the guard keeps a fixed
              // window, so a very long answer costs the same as a short one.
              RepetitionGuard guard = new RepetitionGuard();
              System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
              for (int i = 0; i < 20000; i++)
                  guard.Append("chunk " + i + " of ordinary streamed content. ");
              watch.Stop();

              TestAssert.True(!guard.Tripped, "prose never trips");
              // Generous ceiling: the point is that this is not quadratic, and that
              // it is a small fraction of a millisecond per streamed character.
              TestAssert.True(watch.ElapsedMilliseconds < 2000,
                  "20k appends in " + watch.ElapsedMilliseconds + " ms");
          });

          TestRunner.Run("RepetitionGuard.cuts_a_loop_early", () =>
          {
              // The point of counting copies instead of characters: this loop is caught
              // after a few hundred characters, not after thousands the reader has
              // already scrolled past.
              RepetitionGuard guard = new RepetitionGuard();
              string block = "*Wait*, I will check the date.\n\n*Date Check Command:* `date /t`\n\n*Output:*\nOk.\n\n";
              int appended = 0;
              for (int i = 0; i < 200 && !guard.Tripped; i++)
              {
                  guard.Append(block);
                  appended += block.Length;
              }

              TestAssert.True(guard.Tripped, "loop must trip");
              TestAssert.True(appended < 1000,
                  "caught after " + appended + " characters, expected well under 1000");
          });

          TestRunner.Run("RepetitionGuard.survives_a_loop_with_one_irregular_copy", () =>
          {
              // Real loops are not byte-perfect: one copy in the log lost a full stop.
              // That must neither hide the loop nor be what catches it.
              RepetitionGuard guard = new RepetitionGuard();
              string block = "Checking the date again.\nResult: Ok.\n\n";
              guard.Append("Sure, let me look at that.\n\n");
              for (int i = 0; i < 60; i++)
              {
                  if (i == 7)
                      guard.Append("Checking the date again.\nResult: Ok\n\n");
                  else
                      guard.Append(block);
              }

              TestAssert.True(guard.CheckNow(), "loop with one irregular copy must trip");
          });

          TestRunner.Run("RepetitionGuard.needs_the_repeat_to_persist", () =>
          {
              // A long passage quoted back a bounded number of times is one confirmed
              // run, not a runaway. The guard has to see the same thing again on the
              // next checks before it cuts anything, which is what lets the copy count
              // stay as low as it is.
              RepetitionGuard guard = new RepetitionGuard();
              string prose = "Here is a list of the configuration keys I found in your file. "
                  + "First, llmserver names the endpoint. Second, apikey holds the credential. "
                  + "Third, streamstalltimeout bounds a silent stream. "
                  + "Fourth, requesttimeout bounds the whole request. "
                  + "Each key is documented in the README under configuration. ";
              for (int i = 0; i < 4; i++)
                  guard.Append(prose);

              TestAssert.True(!guard.CheckNow(), "one confirmed run must not be enough");
          });

          TestRunner.Run("RepetitionGuard.ignores_a_short_run", () =>
          {
              // A short block repeated a few times is what ordinary prose does by
              // accident; it never amounts to enough output to be worth cutting.
              RepetitionGuard guard = new RepetitionGuard();
              string block = "The quick brown fox jumps over the lazy dog.\n";
              for (int i = 0; i < 5; i++)
                  guard.Append(block);

              TestAssert.True(!guard.CheckNow(), "a short run must not trip");
          });

          TestRunner.Run("RepetitionGuard.ignores_repeated_code_blocks", () =>
          {
              // Valid code repeats its boilerplate; a code block emitted 30 times is
              // not a runaway and must not be cut. The lines are long enough that the
              // minimum run length alone would not save them.
              RepetitionGuard guard = new RepetitionGuard();
              for (int i = 0; i < 30; i++)
              {
                  guard.Append("```csharp\n");
                  guard.Append("for (int index = 0; index < items.Length; index++)\n");
                  guard.Append("{\n");
                  guard.Append("    total += items[index].Value;\n");
                  guard.Append("}\n");
                  guard.Append("```\n");
              }

              TestAssert.True(!guard.CheckNow(), "repeated code must not trip");
          });

          TestRunner.Run("RepetitionGuard.ignores_repeated_table_rows", () =>
          {
              // A table of identical rows is literally periodic. The rows are long
              // enough that only the table handling keeps it from being cut.
              RepetitionGuard guard = new RepetitionGuard();
              guard.Append("Here are the supported configuration keys:\n\n");
              guard.Append("| configuration key | default value | description |\n");
              for (int i = 0; i < 100; i++)
                  guard.Append("| apikeysource | environment | where the credential is read |\n");

              TestAssert.True(!guard.CheckNow(), "table rows must not trip");
          });

          TestRunner.Run("RepetitionGuard.ignores_line_endings_and_spacing", () =>
          {
              // Token streams split lines anywhere and mix \r\n with \n. A repeat that
              // only differs in its whitespace is still a repeat.
              RepetitionGuard guard = new RepetitionGuard();
              string block = "Result: done.\nSteps: 1, 2, 3.\nDone.\n\n";
              for (int i = 0; i < 40; i++)
                  guard.Append(i % 2 == 0
                      ? block
                      : block.Replace("\n", "\r\n").Replace("Result:", "Result:  "));

              TestAssert.True(guard.CheckNow(), "repeat across mixed line endings must trip");
          });

          TestRunner.Run("RepetitionGuard.detects_a_loop_that_follows_code", () =>
          {
              // Leaving a fence must restore detection; otherwise one code block early
              // in the answer would blind the guard for the rest of the response.
              RepetitionGuard guard = new RepetitionGuard();
              guard.Append("Here is the snippet you asked for.\n\n```\nprint(1)\nprint(2)\n```\n\n");
              for (int i = 0; i < 60; i++)
                  guard.Append("And here is another line of prose that repeats itself.\n");

              TestAssert.True(guard.CheckNow(), "loop after a code block must trip");
          });

          TestRunner.Run("RepetitionGuard.detects_a_loop_split_across_chunks", () =>
          {
              // Streaming cuts the block at arbitrary points, including inside the
              // fence marker, so neither the run nor the fence state may assume a
              // chunk boundary lines up with anything.
              RepetitionGuard guard = new RepetitionGuard();
              string block = "*Wait*, I will check the date.\n\n*Output:*\nOk.\n\n";
              for (int i = 0; i < 200 && !guard.Tripped; i++)
              {
                  for (int c = 0; c < block.Length && !guard.Tripped; c += 3)
                      guard.Append(block.Substring(c, Math.Min(3, block.Length - c)));
              }

               TestAssert.True(guard.Tripped, "loop split into 3-character chunks must trip");
           });

           TestRunner.Run("RepetitionGuard.trips_a_loop_whose_period_does_not_divide_the_floor", () =>
           {
               // The walk cap must be a ceiling, not a floor: with floor division a
               // 44-character block caps at 4 copies (176 < 200) and the loop is missed
               // entirely. 44 does not divide 200.
               RepetitionGuard guard = new RepetitionGuard();
               string block = "The quick brown fox jumps over the lazy dog ";
               for (int i = 0; i < 400 && !guard.Tripped; i++)
                   guard.Append(block);

               TestAssert.True(guard.Tripped, "non-divisor-period loop must trip");
           });

           TestRunner.Run("RepetitionGuard.table_row_with_a_fence_does_not_freeze_the_window", () =>
           {
               // A table row is held out of the comparison, so a backtick fence inside it
               // must not latch the fence state and freeze the window. A loop after the
               // row still has to trip.
               RepetitionGuard guard = new RepetitionGuard();
               guard.Append("| language | sample |\n");
               guard.Append("| csharp | ```csharp |\n");
               for (int i = 0; i < 400 && !guard.Tripped; i++)
                   guard.Append("repeating block again and again and over\n");

               TestAssert.True(guard.Tripped, "loop after a table row with a fence must trip");
           });

           TestRunner.Run("RepetitionGuard.frozen_window_does_not_trip_on_a_stale_tail", () =>
           {
               // A bounded repeat followed by a long code block leaves a stale repeat at
               // the window tail while the window is frozen. That must not earn the hits
               // that cut the legitimate code block.
               RepetitionGuard guard = new RepetitionGuard();
               string block = "The quick brown fox jumps over the lazy dog. ";
               guard.Append(block + block + block + block);
               guard.Append("```csharp\n");
               for (int i = 0; i < 20; i++)
                   guard.Append("int total = items[index].Value;\n");

               TestAssert.True(!guard.Tripped, "a frozen window after a bounded repeat must not trip");
           });

          TestRunner.Run("TokenEstimator.basic", () =>
            {
                TestAssert.Equal(0, TokenEstimator.ApproximateTokens(0), "zero");
                TestAssert.Equal(3, TokenEstimator.ApproximateTokens(10), "10/3");
                TestAssert.True(TokenEstimator.ShouldSummarize(3000, 1000), "summarize");
                TestAssert.True(!TokenEstimator.ShouldSummarize(100, 1000), "no summarize");
            });

            TestRunner.Run("TokenEstimator.calibration", () =>
            {
                double original = TokenEstimator.TokensPerCharacter;
                try
                {
                    TokenEstimator.Recalibrate(0, 100);
                    TokenEstimator.Recalibrate(-1, 100);
                    TestAssert.True(
                        Math.Abs(TokenEstimator.TokensPerCharacter - (1.0 / 3.0)) < 0.0001,
                        "invalid measurements ignored");

                    TokenEstimator.Recalibrate(300, 600); // measured 0.50 -> blend (1/3+0.5)/2
                    double expected = (1.0 / 3.0 + 0.5) / 2.0;
                    TestAssert.True(
                        Math.Abs(TokenEstimator.TokensPerCharacter - expected) < 0.0001,
                        "measured value blended in");
                    TestAssert.Equal((int)(1000 * expected), TokenEstimator.ApproximateTokens(1000), "approx uses ratio");

                    TokenEstimator.TokensPerCharacter = 1.0;
                    TokenEstimator.Recalibrate(1, 10000000); // measured ~0 -> blend clamps at floor 0.05
                    TestAssert.True(TokenEstimator.TokensPerCharacter >= 0.05, "lower clamp");

                    TokenEstimator.TokensPerCharacter = 0.05;
                    TokenEstimator.Recalibrate(int.MaxValue, 1); // measured huge -> clamps at ceiling 1.0
                    TestAssert.True(TokenEstimator.TokensPerCharacter <= 1.0, "upper clamp");
                }
                finally
                {
                    TokenEstimator.TokensPerCharacter = original;
                }
            });

            TestRunner.Run("ToolApproval.roundtrip", () =>
            {
                string msg = ToolApproval.FormatApprovalMessage("read_file", "{\\\"filename\\\":\\\"a.txt\\\"}");
                string tool, arguments;
                TestAssert.True(ToolApproval.TryParseApprovalPrompt(
                    msg + "\n" + ToolApproval.ApprovalPrompt, out tool, out arguments), "parse");
                TestAssert.Equal("read_file", tool, "tool");
            });

            TestRunner.Run("ToolResultParser.json", () =>
            {
                string text, image, mime;
                ToolResultParser.Parse("{\"text\":\"hi\",\"image\":{\"data\":\"QQ==\",\"mime\":\"image/png\"}}",
                    out text, out image, out mime);
                TestAssert.Equal("hi", text, "text");
                TestAssert.Equal("QQ==", image, "image");
                TestAssert.Equal("image/png", mime, "mime");
            });

            TestRunner.Run("ImageEncoder.mime", () =>
            {
                TestAssert.Equal("image/jpeg", ImageEncoder.GuessMime("photo.JPG"), "jpeg");
                TestAssert.Equal("image/jpeg", ImageEncoder.GuessMime("x.jpeg"), "jpeg long");
                TestAssert.Equal("image/png", ImageEncoder.GuessMime("shot.PNG"), "png");
                TestAssert.Equal("image/gif", ImageEncoder.GuessMime("a.gif"), "gif");
                TestAssert.Equal("image/webp", ImageEncoder.GuessMime("a.webp"), "webp");
                TestAssert.Equal("image/bmp", ImageEncoder.GuessMime("a.bmp"), "bmp");
                TestAssert.Equal("image/tiff", ImageEncoder.GuessMime("a.tiff"), "tiff");
                TestAssert.Equal("image/png", ImageEncoder.GuessMime("unknown.bin"), "default");
                TestAssert.Equal("image/png", ImageEncoder.GuessMime(null), "null");
            });

            TestRunner.Run("StatusPipe.ready_line", () =>
            {
                TestAssert.True(StatusPipe.TryParseReadyLine("STATUS ready"), "exact");
                TestAssert.True(StatusPipe.TryParseReadyLine("  STATUS ready  "), "trimmed");
                TestAssert.True(!StatusPipe.TryParseReadyLine("STATUS tokens=42"), "not ready");
                TestAssert.True(!StatusPipe.TryParseReadyLine(""), "empty");
                TestAssert.True(!StatusPipe.TryParseReadyLine(null), "null");
            });

            TestRunner.Run("VectorStore.search", () =>
            {
                VectorStore store = new VectorStore();
                store.Add(new ChunkVector
                {
                    File = "a.txt",
                    StartLine = 1,
                    EndLine = 2,
                    Embedding = new float[] { 1f, 0f, 0f }
                });
                store.Add(new ChunkVector
                {
                    File = "b.txt",
                    StartLine = 1,
                    EndLine = 2,
                    Embedding = new float[] { 0f, 1f, 0f }
                });
                List<ChunkHit> hits = store.Search(new float[] { 1f, 0f, 0f }, 1);
                TestAssert.Equal(1, hits.Count, "count");
                TestAssert.Equal("a.txt", hits[0].Chunk.File, "top file");
            });

            TestRunner.Run("Ini.ConfigHandler", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("cli-ini"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(path,
                        "[System]\r\napikey=k\r\nllmserver=http://127.0.0.1\r\nmodel=m\r\n",
                        Encoding.UTF8);
                    ConfigHandler cfg = new ConfigHandler(path);
                    TestAssert.Equal("k", cfg.GetConfigValue("apikey"), "apikey");
                    TestAssert.Equal("m", cfg.GetConfigValue("model"), "model");
                }
            });

            TestRunner.Run("Ini.approval_default", () =>
            {
                using (TempWorkspace ws = new TempWorkspace("cli-approval"))
                {
                    // Key absent: the tools that can run commands or write files
                    // fall back to requiring approval.
                    string absent = ws.Combine("absent.ini");
                    File.WriteAllText(absent, "[System]\r\napikey=k\r\n", Encoding.UTF8);
                    List<string> defaults = new ConfigHandler(absent).GetConfigList(
                        "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval);
                    TestAssert.Equal(2, defaults.Count, "default count");
                    TestAssert.Equal("run_shell_command", defaults[0], "run_shell_command default");
                    TestAssert.Equal("download_file", defaults[1], "download_file default");

                    // Key present but empty: the user opted out, so no default applies.
                    string optedOut = ws.Combine("opted-out.ini");
                    File.WriteAllText(optedOut, "[System]\r\ntoolsrequiringapproval=\r\n", Encoding.UTF8);
                    TestAssert.Equal(0, new ConfigHandler(optedOut).GetConfigList(
                        "toolsrequiringapproval", AppConstants.DefaultToolsRequiringApproval).Count,
                        "explicit empty stays empty");
                }
            });

            TestRunner.Run("Ini.secret_protection", () =>
            {
                string protectedKey = SecretProtector.Protect("sk-secret-123");
                TestAssert.True(SecretProtector.IsProtected(protectedKey), "prefixed");
                TestAssert.Equal("sk-secret-123", SecretProtector.Unprotect(protectedKey), "round trip");
                TestAssert.Equal(protectedKey, SecretProtector.Protect(protectedKey), "protect is idempotent");
                TestAssert.Equal("plain-key", SecretProtector.Unprotect("plain-key"), "plaintext passes through");
                TestAssert.Equal("dpapi:not-base64!", SecretProtector.Unprotect("dpapi:not-base64!"),
                    "undecryptable value is kept so the key can be re-entered");
                TestAssert.True(SecretProtector.IsSecretKey("apiKey"), "apikey is a secret");
                TestAssert.True(!SecretProtector.IsSecretKey("sysprompt"), "sysprompt is not a secret");

                using (TempWorkspace ws = new TempWorkspace("cli-secret"))
                {
                    string path = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(path, "[System]\r\napikey=" + protectedKey + "\r\n", Encoding.UTF8);
                    TestAssert.Equal("sk-secret-123", new ConfigHandler(path).GetConfigValue("apikey"),
                        "protected config value decrypts");

                    string legacy = ws.Combine("legacy.ini");
                    File.WriteAllText(legacy, "[System]\r\napikey=legacy-plain\r\n", Encoding.UTF8);
                    TestAssert.Equal("legacy-plain", new ConfigHandler(legacy).GetConfigValue("apikey"),
                        "legacy plaintext config still works");
                }
            });

            TestRunner.Run("ToolRegistry.stdin_payload_scoped", () =>
            {
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools"));
                string webToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "WebTools"));
                if (!File.Exists(Path.Combine(fileToolsDir, "FileTools.json"))
                    || !File.Exists(Path.Combine(webToolsDir, "WebTools.json")))
                {
                    TestRunner.Skip("tool manifests not found next to the test output");
                    return;
                }

                using (TempWorkspace ws = new TempWorkspace("cli-toolcfg"))
                {
                    // A config with secrets that must never reach a tool's stdin,
                    // plus declared options that must.
                    string ini = ws.Combine("LLMSettings.ini");
                    File.WriteAllText(ini,
                        "[System]\r\n" +
                        "apikey=super-secret-key\r\n" +
                        "embeddingsapikey=another-secret\r\n" +
                        "firecrawlapikey=fire-secret\r\n" +
                        "llmserver=http://127.0.0.1:9\r\n" +
                        "model=m\r\n" +
                        "sysprompt=\"secret prompt\"\r\n" +
                        "maxFileContentLength=1234\r\n",
                        Encoding.UTF8);

                    ConfigHandler cfg = new ConfigHandler(ini);
                    ToolRegistry registry = new ToolRegistry(cfg);
                    registry.LoadToolsFromDirectory(fileToolsDir);

                    // FileTools declares maxFileContentLength and outputEncoding, and
                    // nothing else: its stdin must carry exactly those (current
                    // value for the first, manifest default for the second).
                    JObject root = JObject.Parse(registry.BuildStdinPayload(
                        Path.Combine(fileToolsDir, "FileTools.exe"), "{}"));
                    JObject cfgObj = root["config"] as JObject;
                    TestAssert.True(cfgObj != null, "config object present");
                    TestAssert.True(!HasConfigKey(cfgObj, "apikey"), "apikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "embeddingsapikey"), "embeddingsapikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "firecrawlapikey"), "undeclared firecrawlapikey not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "llmserver"), "llmserver not forwarded");
                    TestAssert.True(!HasConfigKey(cfgObj, "sysprompt"), "sysprompt not forwarded");
                    TestAssert.True(HasConfigKey(cfgObj, "maxFileContentLength"), "declared option forwarded");
                    TestAssert.Equal("1234", cfgObj["maxFileContentLength"].ToString(), "current value wins");
                    TestAssert.Equal("auto", cfgObj["outputEncoding"].ToString(), "manifest default when unset");

                    // WebTools declares FirecrawlApiKey: a secret it declares must
                    // still reach its own package, while apikey must not.
                    registry.LoadToolsFromDirectory(webToolsDir);
                    JObject webRoot = JObject.Parse(registry.BuildStdinPayload(
                        Path.Combine(webToolsDir, "WebTools.exe"), "{}"));
                    JObject webCfg = webRoot["config"] as JObject;
                    TestAssert.True(cfgObj != null && webCfg != null, "web config object present");
                    TestAssert.True(HasConfigKey(webCfg, "firecrawlapikey"), "declared secret reaches its own package");
                    TestAssert.Equal("fire-secret", webCfg["FirecrawlApiKey"].ToString(), "declared secret value");
                    TestAssert.True(!HasConfigKey(webCfg, "apikey"), "llm apikey not forwarded to web tools");
                }
            });

            // A context injector returns a live snapshot (DesktopTools' get_desktop_context
            // is the focused window's control tree), so it differs on every turn. Each turn
            // therefore gets a fresh one, but it is BAKED into the stored user message:
            // the text of a message that was already sent must never change again, or the
            // provider no longer sees a byte-for-byte extension of the previous prompt and
            // rebuilds its cached prefix from the divergence onwards.
            TestRunner.Run("ToolRegistry.context_injection_baked_into_user_message", () =>
            {
                string turnOne = ToolRegistry.BakeContextInjection("first question", new List<string> { "DESKTOP TREE A" });

                TestAssert.Equal("first question" + "\n\n" + "DESKTOP TREE A", turnOne, "snapshot appended to the user text");
                TestAssert.True(!turnOne.Contains("You are"), "no system text involved");

                // Re-baking the same snapshot must not append it twice: the payload is
                // rebuilt from the stored message on every request, tool rounds included.
                string again = ToolRegistry.BakeContextInjection(turnOne, new List<string> { "DESKTOP TREE A" });
                TestAssert.Equal(turnOne, again, "baking is idempotent");

                // The next turn brings a different snapshot and lands on a different
                // message; the turn-1 text is already stored and stays untouched.
                string turnTwo = ToolRegistry.BakeContextInjection("second question", new List<string> { "DESKTOP TREE B" });
                TestAssert.Equal("second question" + "\n\n" + "DESKTOP TREE B", turnTwo, "turn 2 gets its own snapshot");
                TestAssert.Equal("first question" + "\n\n" + "DESKTOP TREE A", turnOne, "turn 1 message unchanged");

                TestAssert.Equal("q", ToolRegistry.BakeContextInjection("q", new List<string>()), "no injectors, no change");
                TestAssert.Equal("q", ToolRegistry.BakeContextInjection("q", null), "null list, no change");
                TestAssert.Equal("SNAP", ToolRegistry.BakeContextInjection("", new List<string> { "SNAP" }), "empty user text takes the snapshot alone");
                TestAssert.Equal("SNAP", ToolRegistry.BakeContextInjection(null, new List<string> { "SNAP" }), "null user text takes the snapshot alone");
            });

            // The condition a provider actually needs: the prompt sent on turn 2 must
            // start with the exact text sent on turn 1. Baking is what makes this hold -
            // the turn-1 user message keeps the snapshot it was sent with, so the history
            // is only ever appended to, never rewritten in the middle.
            TestRunner.Run("ToolRegistry.context_injection_multi_turn_prefix", () =>
            {
                List<string> turnOneInjections = new List<string> { "DESKTOP TREE A" };

                // Turn 1: user message stored (and sent) with its snapshot baked in.
                string turnOnePrompt =
                    "You are helpful.\n" +
                    ToolRegistry.BakeContextInjection("first question", turnOneInjections) + "\n";

                // Turn 2: the answer lands, a NEW snapshot is fetched for a NEW message.
                // The stored turn-1 message is re-serialized unchanged.
                string storedTurnOne = ToolRegistry.BakeContextInjection("first question", turnOneInjections);
                string turnTwoPrompt =
                    "You are helpful.\n" +
                    storedTurnOne + "\n" +
                    "first answer\n" +
                    ToolRegistry.BakeContextInjection("second question", new List<string> { "DESKTOP TREE B" }) + "\n";

                TestAssert.True(turnTwoPrompt.StartsWith(turnOnePrompt, StringComparison.Ordinal),
                    "turn 2 keeps turn 1 as a cacheable prefix"
                    + "\n      sent on turn 1: " + turnOnePrompt.Replace("\n", "\\n")
                    + "\n      sent on turn 2: " + turnTwoPrompt.Replace("\n", "\\n"));
            });

            // The regression this replaces: attaching the snapshot to the serialized copy
            // of the last user turn left the stored turn-1 message without it, so the
            // prefix diverged in the middle of the history.
            TestRunner.Run("ToolRegistry.context_injection_prefix_stable_across_turns", () =>
            {
                List<string> turnOneInjections = new List<string> { "DESKTOP TREE A" };
                string stored = ToolRegistry.BakeContextInjection("first question", turnOneInjections);

                // Every request rebuilds the payload from the conversation, so what goes
                // on the wire for turn 1 must be recoverable from storage on turn 2.
                for (int turn = 2; turn <= 5; turn++)
                {
                    string sent = ToolRegistry.BakeContextInjection("first question", turnOneInjections);
                    TestAssert.Equal(stored, sent, "turn " + turn + " re-serializes turn 1 byte-for-byte");
                }
            });

            TestRunner.Run("ToolRegistry.context_injection_tail", () =>
            {
                TestAssert.Equal("", ToolRegistry.BuildInjectionTail(null), "null list");
                TestAssert.Equal("", ToolRegistry.BuildInjectionTail(new List<string>()), "empty list");
                TestAssert.Equal("A", ToolRegistry.BuildInjectionTail(new List<string> { null, "", "A" }), "blank entries dropped");
                TestAssert.Equal("A" + "\n\n" + "B", ToolRegistry.BuildInjectionTail(new List<string> { "A", "B" }), "joined with a blank line");
            });

            // The bake fix in the shipped binary, not just in a unit test. DesktopTools
            // declares a context injector, so every turn gets a fresh snapshot. The
            // property that keeps the provider's cached prefix alive is that the FIRST
            // user message is sent byte-for-byte identically on turn 2 as on turn 1 -
            // whatever the snapshot happens to say. This fails if the snapshot is attached
            // to the serialized copy of the last user turn instead of being stored.
            TestRunner.Run("cli.context_injection_bake_keeps_prefix_stable", () =>
            {
                string desktopDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "DesktopTools", "bin", "Release"));
                string desktopExe = Path.Combine(desktopDir, "DesktopTools.exe");
                if (!File.Exists(desktopExe))
                {
                    TestRunner.Skip("DesktopTools.exe not built: " + desktopExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Turn one done.";
                    server.StreamDelayMs = 0;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(desktopExe, Path.Combine(toolsDir, "DesktopTools.exe"), true);
                    File.Copy(Path.Combine(desktopDir, "DesktopTools.json"), Path.Combine(toolsDir, "DesktopTools.json"), true);
                    File.Copy(Path.Combine(desktopDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "list_windows");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("first question");
                        CliRunner.WaitForTextAfter(cli, "Turn one done.", "You: ", 30000);

                        // A distinct reply per turn: WaitForTextAfter anchors on the LAST
                        // occurrence, so the same marker twice would match turn 1 again.
                        server.FixedReply = "Turn two done.";
                        cli.WriteLine("second question");
                        CliRunner.WaitForTextAfter(cli, "Turn two done.", "You: ", 30000);
                    }

                    TestAssert.True(server.ReceivedBodies.Count >= 2,
                        "two chat requests sent (got " + server.ReceivedBodies.Count + ")");
                    if (server.ReceivedBodies.Count < 2)
                        return;

                    JArray turnOne = MessagesOf(JObject.Parse(server.ReceivedBodies[0]));
                    JArray turnTwo = MessagesOf(JObject.Parse(server.ReceivedBodies[1]));

                    string firstOnTurnOne = FirstUserContent(turnOne);
                    string firstOnTurnTwo = FirstUserContent(turnTwo);

                    // Without a tool in the advertised schema the injector never runs, there is
                    // nothing to keep stable, and the comparison below would pass for the wrong
                    // reason - so prove both halves before comparing.
                    TestAssert.True(JObject.Parse(server.ReceivedBodies[0])["tools"] != null,
                        "the enabled tool is advertised in the request, which is what activates the injector"
                        + "\n      turn 1 body: " + server.ReceivedBodies[0]);

                    TestAssert.True(firstOnTurnOne.Length > "first question".Length,
                        "the context injector ran and was baked into the turn-1 user message"
                        + "\n      turn 1 user message: " + firstOnTurnOne);

                    TestAssert.Equal(firstOnTurnOne, firstOnTurnTwo,
                        "the turn-1 user message is re-sent unchanged, so the prompt stays a prefix of the previous one"
                        + "\n      turn 1: " + firstOnTurnOne.Replace("\n", "\\n")
                        + "\n      turn 2: " + firstOnTurnTwo.Replace("\n", "\\n"));
                }
            });

            // The manifest's parameter types are sent to the provider verbatim as JSON
            // Schema types. "int" is not one: strict providers (automatic parser
            // generation) reject the WHOLE request with HTTP 400 before the model
            // ever sees the message.
            TestRunner.Run("cli.tool_schema_types_are_valid_json_schema", () =>
            {
                string searchDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "SearchTools", "bin", "Release"));
                string searchExe = Path.Combine(searchDir, "SearchTools.exe");
                if (!File.Exists(searchExe))
                {
                    TestRunner.Skip("SearchTools.exe not built: " + searchExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Done.";
                    server.StreamDelayMs = 0;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(searchExe, Path.Combine(toolsDir, "SearchTools.exe"), true);
                    File.Copy(Path.Combine(searchDir, "SearchTools.json"), Path.Combine(toolsDir, "SearchTools.json"), true);
                    File.Copy(Path.Combine(searchDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "everything_search");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);
                        cli.WriteLine("hi");
                        CliRunner.WaitForTextAfter(cli, "Done.", "You: ", 30000);
                    }

                    TestAssert.True(server.ReceivedBodies.Count >= 1, "one chat request sent");
                    if (server.ReceivedBodies.Count < 1)
                        return;

                    JArray tools = JObject.Parse(server.ReceivedBodies[0])["tools"] as JArray;
                    TestAssert.True(tools != null && tools.Count > 0,
                        "the enabled tool is advertised in the request");
                    if (tools == null || tools.Count == 0)
                        return;

                    JObject props = tools[0]["function"]["parameters"]["properties"] as JObject;
                    TestAssert.True(props != null, "the function has properties");
                    if (props == null)
                        return;

                    foreach (JProperty property in props.Properties())
                    {
                        JObject propObj = property.Value as JObject;
                        string type = propObj == null ? null : (string)propObj["type"];
                        TestAssert.True(
                            type == "string" || type == "integer" || type == "number"
                            || type == "boolean" || type == "array" || type == "object",
                            "parameter '" + property.Name + "' uses '" + type + "', not a JSON Schema type");
                    }
                }
            });

            // Baking the snapshot into the stored message is what keeps the prompt a
            // byte-for-byte prefix of the previous one, but it also means the session
            // file holds text the user never typed. The original has to travel next to
            // it, or the chat window renders the memory and skills dumps as the
            // user's own message.
            TestRunner.Run("cli.baked_context_keeps_user_text_separate", () =>
            {
                string desktopDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "DesktopTools", "bin", "Release"));
                string desktopExe = Path.Combine(desktopDir, "DesktopTools.exe");
                if (!File.Exists(desktopExe))
                {
                    TestRunner.Skip("DesktopTools.exe not built: " + desktopExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Answer given.";
                    server.StreamDelayMs = 0;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(desktopExe, Path.Combine(toolsDir, "DesktopTools.exe"), true);
                    File.Copy(Path.Combine(desktopDir, "DesktopTools.json"), Path.Combine(toolsDir, "DesktopTools.json"), true);
                    File.Copy(Path.Combine(desktopDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "list_windows");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);
                        cli.WriteLine("напиши ок");
                        CliRunner.WaitForTextAfter(cli, "Answer given.", "You: ", 30000);
                        cli.WriteLine("/save baked");
                        CliRunner.WaitForTextAfter(cli, "Session saved: baked", "You: ", 15000);
                    }

                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    List<JObject> messages = null;
                    TestAssert.True(SessionStore.Load(sessionsDir, "baked", out messages),
                        "the session was saved");
                    if (messages == null || messages.Count == 0)
                        return;

                    JObject user = messages[0];
                    string content = (string)user["Content"];
                    string userText = (string)user["UserText"];

                    TestAssert.Equal("напиши ок", userText,
                        "the session keeps exactly what the user typed");
                    TestAssert.True(content.Length > userText.Length,
                        "the stored content still carries the baked context for the model"
                        + "\n      content: " + content);
                    TestAssert.True(content.StartsWith(userText, StringComparison.Ordinal),
                        "the baked context follows the user's own text"
                        + "\n      content: " + content);

                    // The model still receives the baked text: this field is for the UI
                    // only and must not shrink what is sent.
                    TestAssert.True(server.ReceivedBodies.Count >= 1, "a chat request was sent");
                    if (server.ReceivedBodies.Count < 1)
                        return;
                    JArray sent = MessagesOf(JObject.Parse(server.ReceivedBodies[0]));
                    TestAssert.Equal(content, FirstUserContent(sent),
                        "the request carries the baked content, not the display text");
                }
            });

            // A reasoning model emits "thinking then answer", and that token stream is what
            // the server holds in its KV cache. Storing only the answer makes the next
            // prompt diverge from the cached one right after the user message, so the
            // whole conversation is reprocessed every turn. Echo the thinking back.
            TestRunner.Run("cli.reasoning_echoed_in_history", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    const string thinking = "The user asked a simple question; answer briefly.";
                    server.ReasoningReply = thinking;
                    server.FixedReply = "First answer.";
                    server.StreamDelayMs = 0;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("first question");
                        CliRunner.WaitForTextAfter(cli, "First answer.", "You: ", 20000);

                        server.FixedReply = "Second answer.";
                        cli.WriteLine("second question");
                        CliRunner.WaitForTextAfter(cli, "Second answer.", "You: ", 20000);
                    }

                    TestAssert.True(server.ReceivedBodies.Count >= 2,
                        "two chat requests sent (got " + server.ReceivedBodies.Count + ")");

                    JObject turnOne = JObject.Parse(server.ReceivedBodies[0]);
                    JObject turnTwo = JObject.Parse(server.ReceivedBodies[1]);

                    // Turn 1 has nothing to echo yet.
                    TestAssert.Equal(0, CountReasoning(turnOne), "turn 1 carries no reasoning to echo");

                    JObject echo = FirstAssistantWithReasoning(turnTwo);
                    TestAssert.True(echo != null,
                        "turn 2 echoes the model thinking back as reasoning_content"
                        + "\n      turn 2 messages: " + turnTwo["messages"].ToString(Newtonsoft.Json.Formatting.None));
                    if (echo != null)
                        TestAssert.Equal(thinking, (string)echo["reasoning_content"], "echoed verbatim");
                }
            });

            // The same contract on the tool-call path: a model that thinks before calling a
            // tool stores that thinking on the assistant message that carries the call.
            TestRunner.Run("cli.reasoning_echoed_on_tool_call_message", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    const string thinking = "I should run a shell command to answer this.";
                    server.ReasoningReply = thinking;
                    server.FixedReply = "done";
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "echo reasoning-echo-probe";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);
                        cli.WriteLine("run the probe command");
                        CliRunner.WaitForTextAfter(cli, "done", "You: ", 60000);
                    }

                    // Find a request whose assistant tool-call message carries the thinking.
                    bool found = false;
                    foreach (string body in server.ReceivedBodies)
                    {
                        foreach (JObject msg in MessagesOf(JObject.Parse(body)))
                        {
                            if (msg["tool_calls"] == null)
                                continue;
                            if ((string)msg["reasoning_content"] == thinking)
                                found = true;
                        }
                    }
                    TestAssert.True(found,
                        "the assistant tool-call message echoes reasoning_content"
                        + "\n      requests: " + server.ReceivedBodies.Count);
                }
            });

            TestRunner.Run("cli.bad_reasoning_effort", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    ProcessResult r = ProcessRunner.Run(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"),
                        "--reasoning-effort nope",
                        ws.Path, null, 15000);
                    TestAssert.ContainsIgnoreCase(r.Stderr + r.Stdout, "Invalid reasoning effort", "error");
                }
            });

            TestRunner.Run("cli.missing_image_path", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    ProcessResult r = ProcessRunner.Run(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"),
                        "--image",
                        ws.Path, null, 15000);
                    TestAssert.ContainsIgnoreCase(r.Stderr + r.Stdout, "requires a file path", "error");
                }
            });

            TestRunner.Run("cli.stop_aborts_generation", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Long reply, slow cadence: plenty of streaming left to cut short.
                    server.FixedReply = new string('x', 2400);
                    server.StreamDelayMs = 120;
                    server.StreamChunkChars = 48;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("please stream for a while");

                        // Wait until the reply actually starts streaming.
                        CliRunner.WaitForText(cli, "x", 15000);

                        TestAssert.True(cli.SendStop(3000), "STOP delivered through control pipe");

                        CliRunner.WaitForText(cli, "[Generation stopped by user]", 15000);

                        // The CLI mirrors the conversation to "lastsession" between the
                        // notice and the re-prompt, so wait for a fresh prompt after the
                        // notice instead of reading stdout immediately.
                        CliRunner.WaitForTextAfter(cli, "[Generation stopped by user]", "You: ", 15000);

                        // The CLI must have returned to its prompt after the notice.
                        string outText = cli.Stdout;
                        TestAssert.Contains(outText, "[Generation stopped by user]", "notice present");
                        int noticeIndex = outText.IndexOf("[Generation stopped by user]", StringComparison.Ordinal);
                        TestAssert.True(
                            outText.LastIndexOf("You: ", StringComparison.Ordinal) > noticeIndex,
                            "re-prompted after stop");
                        // The stream was cut short — never the full 2400-char reply.
                        TestAssert.True(
                            outText.IndexOf(new string('x', 600), StringComparison.Ordinal) < 0,
                            "reply truncated");
                    }
                }
            });

            TestRunner.Run("cli.repetition_guard_stops_degenerate_loop", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A model that loops: the same sentence over and over, with no
                    // stall and no end. Nothing else bounds this stream, which is
                    // exactly the case the guard exists for.
                    string loop = "The answer is always the same. ";
                    server.FixedReply = Repeat(loop, 400);
                    server.StreamDelayMs = 0;
                    server.StreamChunkChars = 64;
                    // The guard is off unless asked for, so this test opts in.
                    WriteCliIni(ws.Path, server.BaseUrl, repetitionGuard: 1);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("tell me something");

                        CliRunner.WaitForText(cli, "repeating the same block in a loop", 30000);

                        string outText = cli.Stdout;
                        TestAssert.True(
                            outText.IndexOf("[Generation stopped by user]", StringComparison.Ordinal) < 0,
                            "not reported as a user stop");

                        // The stream was cut short, so most of the loop never rendered.
                        int rendered = outText.Split(new string[] { loop }, StringSplitOptions.None).Length - 1;
                        TestAssert.True(rendered < 300, "loop truncated, copies rendered=" + rendered);

                        // And the CLI is back at its prompt, ready for the next turn.
                        CliRunner.WaitForTextAfter(cli, "in a loop", "You: ", 15000);
                    }
                }
            });

            TestRunner.Run("cli.repetition_guard_can_be_disabled", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    string loop = "The answer is always the same. ";
                    server.FixedReply = Repeat(loop, 400);
                    server.StreamDelayMs = 0;
                    server.StreamChunkChars = 64;
                    // repetitionguard=0 turns the check off explicitly, for anyone who has a
                    // legitimate response it would otherwise cut short. This is the
                    // same state a fresh install is in, so the default is pinned by
                    // the next test rather than by this one.
                    WriteCliIni(ws.Path, server.BaseUrl, 0, "", 120, 300, "", 0);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("tell me something");

                        // Without the guard the whole loop streams through to its end,
                        // so the reply eventually re-prompts instead of being cut short.
                        CliRunner.WaitForTextAfter(cli, loop, "You: ", 60000);
                        TestAssert.True(
                            cli.Stdout.IndexOf("in a loop", StringComparison.Ordinal) < 0,
                            "guard stayed off, no loop notice");
                        int rendered = cli.Stdout.Split(new string[] { loop }, StringSplitOptions.None).Length - 1;
                        TestAssert.True(rendered > 300, "loop streamed whole, copies rendered=" + rendered);
                    }
                }
            });

            TestRunner.Run("cli.repetition_guard_is_off_by_default", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // How prone a model is to loop is a property of the model, so the
                    // check does not run unless the user asked for it. An INI with no
                    // repetitionguard key is what a fresh install and every existing
                    // config look like, and the runaway has to stream to its end.
                    string loop = "The answer is always the same. ";
                    server.FixedReply = Repeat(loop, 400);
                    server.StreamDelayMs = 0;
                    server.StreamChunkChars = 64;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("tell me something");

                        CliRunner.WaitForTextAfter(cli, loop, "You: ", 60000);
                        TestAssert.True(
                            cli.Stdout.IndexOf("in a loop", StringComparison.Ordinal) < 0,
                            "no loop notice when the guard was never enabled");
                        int rendered = cli.Stdout.Split(new string[] { loop }, StringSplitOptions.None).Length - 1;
                        TestAssert.True(rendered > 300, "loop streamed whole, copies rendered=" + rendered);
                    }
                }
            });

            TestRunner.Run("cli.image_not_supported_recovery", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    server.RejectImageRequests = true;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    // Minimal 1x1 PNG so /image has a real file to encode.
                    byte[] png = Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcS3AAAADUlEQVR4" +
                        "nGNgYGD4DwABHQAf" +
                        "aKT/3QAAAABJRU5ErkJggg==");
                    File.WriteAllBytes(Path.Combine(ws.Path, "tiny.png"), png);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/image \"tiny.png\" describe this");

                        // The first attempt carries the image and is rejected.
                        CliRunner.WaitForText(
                            cli, "[Model does not support images - image removed and retrying]", 30000);

                        // The retry goes out text-only and succeeds.
                        CliRunner.WaitForTextAfter(
                            cli, "[Model does not support images - image removed and retrying]",
                            "Hello there.", 30000);
                        CliRunner.WaitForTextAfter(cli, "Hello there.", "You: ", 15000);

                        // The conversation is no longer poisoned: a plain text turn works.
                        cli.WriteLine("next question");
                        CliRunner.WaitForTextAfter(cli, "You: ", "Hello there.", 30000);
                    }
                }
            });

            TestRunner.Run("cli.sse_error_word_not_api_error", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The whole reply is one content delta whose text is exactly the
                    // word "error". The old substring check matched "error" in the
                    // SSE JSON and aborted the stream as an API failure.
                    server.FixedReply = "error";
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("reply with one word");
                        CliRunner.WaitForTextAfter(cli, "You: ", "error", 15000);
                        TestAssert.True(
                            cli.Stdout.IndexOf("[API Error]", StringComparison.Ordinal) < 0,
                            "a content delta whose text is \"error\" is not an API failure");
                    }
                }
            });

            TestRunner.Run("cli.sse_midstream_error_event", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.MidStreamErrorAfterText = "partial text before the failure";
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("start a reply that fails mid-stream");
                        CliRunner.WaitForText(cli, "partial text before the failure", 15000);
                        CliRunner.WaitForText(cli, "[API Error]", 15000);
                        TestAssert.Contains(
                            cli.Stdout, "injected mid-stream error",
                            "a real error property mid-stream is still an API failure");
                    }
                }
            });

            TestRunner.Run("cli.sse_error_member_variants", () =>
            {
                // A string error is still an API failure (some gateways report it
                // that way); false and empty markers are not.
                string[] variants = {
                    "{\"error\":\"quota exceeded\"}",
                    "{\"error\":false}",
                    "{\"error\":\"\"}"
                };
                bool[] expectApiError = { true, false, false };

                for (int i = 0; i < variants.Length; i++)
                {
                    using (FakeOpenAiServer server = new FakeOpenAiServer())
                    using (TempWorkspace ws = PackageCliWorkspace())
                    {
                        server.MidStreamErrorAfterText = "text before the marker";
                        server.MidStreamErrorJson = variants[i];
                        server.StreamDelayMs = 5;
                        WriteCliIni(ws.Path, server.BaseUrl);

                        string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("start a reply that hits the marker");
                            // The marker event carries no choices, so the next
                            // prompt is the proof that it was processed.
                            CliRunner.WaitForTextAfter(cli, "text before the marker", "You: ", 15000);
                            bool hasApiError = cli.Stdout.IndexOf("[API Error]", StringComparison.Ordinal) >= 0;
                            TestAssert.True(expectApiError[i] == hasApiError, variants[i]);
                        }
                    }
                }
            });

            TestRunner.Run("cli.session_save_load", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, "Hello there.", 15000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/save my_session");
                        CliRunner.WaitForText(cli, "Session saved: my_session", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "my_session.json");
                        TestAssert.True(File.Exists(sessionFile), "session file created");
                        string json = File.ReadAllText(sessionFile, Encoding.UTF8);
                        TestAssert.Contains(json, "\"Role\": \"assistant\"", "assistant message");
                        TestAssert.Contains(json, "Hello there.", "content preserved");
                        TestAssert.Contains(json, "\"Version\": 1", "envelope version");

                        cli.WriteLine("/sessions");
                        CliRunner.WaitForText(cli, "  my_session", 15000);
                        TestAssert.Contains(cli.Stdout, "messages", "count printed");

                        cli.WriteLine("/load my_session");
                        CliRunner.WaitForText(cli, "Session loaded: my_session", 15000);
                    }
                }
            });

            TestRunner.Run("cli.session_auto_name_and_list", () =>
            {
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    WriteCliIni(ws.Path, "http://127.0.0.1:1");
                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/save");
                        CliRunner.WaitForText(cli, "Session saved: session_", 15000);

                        cli.WriteLine("/sessions");
                        CliRunner.WaitForText(cli, "  session_", 15000);
                        TestAssert.Contains(cli.Stdout, "messages", "count printed");

                        // No-keyword load picks the most recent session.
                        cli.WriteLine("/load");
                        CliRunner.WaitForText(cli, "Session loaded: session_", 15000);
                    }
                }
            });

            TestRunner.Run("cli.sse_data_no_space", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The space after "data:" is optional per the SSE spec; some
                    // servers/proxies omit it and the parser must accept both.
                    server.FixedReply = "no-space SSE works";
                    server.DataPrefixNoSpace = true;
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, "no-space SSE works", 15000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_ok", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // llmserver is an https:// URL pointed at a plain HTTP server: the
                    // .NET 4.0 client fails the TLS handshake, so the CLI must take
                    // the curl fallback. The bundled "curl.exe" is this test EXE in
                    // --fake-curl mode answering with a short SSE stream.
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SuperSimpleAgentCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "ok");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "curl works", 20000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_stall", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The fake curl never produces stream data; the 2 s stall watchdog
                    // must kill it instead of hanging the CLI forever (curl has no
                    // idle timeout of its own for streaming).
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SuperSimpleAgentCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl, streamStallTimeout: 2);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "hang");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                            TestAssert.Contains(cli.Stdout, "model stalled", "stall reason printed");
                            CliRunner.WaitForText(cli, "You:", 30000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
                    }
                }
            });

            TestRunner.Run("cli.curl_fallback_connect_timeout", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The fake curl simulates curl(1) honoring --connect-timeout (exit 28)
                    // only when the flag is present, proving the CLI passes it.
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                        "SuperSimpleAgentCLI.Tests.exe"), Path.Combine(ws.Path, "curl.exe"), true);
                    string httpsUrl = server.BaseUrl.Replace("http://", "https://");
                    WriteCliIni(ws.Path, httpsUrl, requestTimeout: 2);

                    Environment.SetEnvironmentVariable("FAKE_CURL_MODE", "hang-connect");
                    try
                    {
                        string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                        using (CliRunner cli = new CliRunner(exe, ws.Path))
                        {
                            CliRunner.WaitForText(cli, "You:", 15000);
                            cli.WriteLine("hello");
                            CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                            TestAssert.Contains(cli.Stdout, "cURL failed (exit 28)", "connect timeout surfaced");
                            CliRunner.WaitForText(cli, "You:", 30000);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("FAKE_CURL_MODE", null);
                    }
                }
            });

            TestRunner.Run("cli.context_high_auto_truncate", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // ~2 KB markdown per turn keeps the test offline and deterministic.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 850);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("first message");
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("second message");
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // The third turn starts far beyond 95%: the CLI must
                        // auto-truncate (no prompt, no stdin) and continue.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        CliRunner.WaitForText(cli, "# Reply turn 3", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);
                    }
                }
            });

            TestRunner.Run("cli.shell_output_capped", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A file much larger than the 20k process-output ceiling: the
                    // shell tool must cap the captured output (head + tail + marker)
                    // and offload the full output to a file instead of dumping
                    // ~300KB into the conversation.
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 30000; i++)
                        big.Append("0123456789");
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    File.WriteAllText(bigPath, big.ToString());

                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);
                        CliRunner.WaitForText(cli, "Full output saved to", 60000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.approval_mode_auto", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // --approval auto must run a tool call with no prompt at all:
                    // the CLI would otherwise block on stdin waiting for Y/N.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var autoArgs = new JObject();
                    autoArgs["command"] = "echo approval-auto-ok";
                    server.ToolCallArguments = autoArgs.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval auto"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("run it");
                        CliRunner.WaitForText(cli, "approval-auto-ok", 60000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            cli.Stdout.IndexOf(ToolApproval.ApprovalPrompt, StringComparison.Ordinal) < 0,
                            "auto mode shows no approval prompt");
                    }
                }
            });

            TestRunner.Run("cli.approval_mode_ask", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // --approval ask prompts for every tool call, including a tool
                    // that is not in the (empty) toolsRequiringApproval list, and N
                    // must cancel the call without running the command.
                    string sideEffect = Path.Combine(ws.Path, "ask-mode-must-not-exist.txt");
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var askArgs = new JObject();
                    askArgs["command"] = "echo hi > \"" + sideEffect + "\"";
                    server.ToolCallArguments = askArgs.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval ask"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("run it");
                        CliRunner.WaitForText(cli, ToolApproval.ApprovalPrompt, 60000);
                        cli.WriteLine("N");
                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);

                        TestAssert.True(!File.Exists(sideEffect),
                            "declined tool call never ran the command");
                    }
                }
            });

            TestRunner.Run("cli.plan_mode_readonly_tools", () =>
            {
                // Reads are allowed.
                TestAssert.True(PlanMode.IsReadOnlyTool("read_file"), "read_file allowed");
                TestAssert.True(PlanMode.IsReadOnlyTool("run_web_search"), "run_web_search allowed");
                TestAssert.True(PlanMode.IsReadOnlyTool("screenshot"), "screenshot allowed");
                TestAssert.True(PlanMode.IsReadOnlyTool("list_controls"), "list_controls allowed");

                // Everything that can act on the machine or change state is refused.
                // write_file/edit_file are absent on purpose: they write the plan
                // file and are checked by target path instead.
                string[] forbidden =
                {
                    "run_shell_command", "run_python_script", "download_file", "download_video",
                    "delete_file", "move_file", "copy_file", "extract_file",
                    "navigate", "set_control_text", "send_keys",
                    "save_memory", "delete_memory", "create_skill", "edit_skill", "remove_skill",
                };
                foreach (string name in forbidden)
                {
                    TestAssert.True(
                        PlanMode.IsForbiddenTool(name),
                        name + " must be refused in plan mode");
                }

                // The plan writers are the one documented exception.
                TestAssert.True(PlanMode.IsPlanWriterTool("write_file"), "write_file writes the plan");
                TestAssert.True(PlanMode.IsPlanWriterTool("edit_file"), "edit_file writes the plan");
                TestAssert.True(
                    !PlanMode.IsForbiddenTool("write_file"),
                    "write_file is not blanket-forbidden; its target is checked instead");
            });

            TestRunner.Run("cli.plan_mode_write_target", () =>
            {
                string planPath = PlanMode.GetPlanFilePath();

                // The exact plan file is allowed.
                TestAssert.True(
                    !PlanMode.IsPlanWriteOutsidePlanFile("write_file", planPath, null),
                    "writing the plan file itself is allowed");

                // Anything else is refused.
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("write_file", "C:\\Windows\\evil.txt", null),
                    "writing outside the plan file is refused");

                // A missing path cannot be trusted.
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("write_file", null, null),
                    "a write with no path is refused");
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("write_file", "", null),
                    "a write with an empty path is refused");

                // Relative paths resolve against the tool's working directory, the
                // same way FileTools resolves them. A traversal that lands on the
                // plan file is legitimately allowed; one that does not is refused.
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile(
                        "write_file", "..\\..\\..\\..\\Windows\\evil.txt", "C:\\tools\\FileTools"),
                    "a traversal out of the tool folder is refused");
                TestAssert.True(
                    !PlanMode.IsPlanWriteOutsidePlanFile(
                        "write_file", "plan.md", Path.GetDirectoryName(planPath)),
                    "plan.md in the plans folder resolves to the plan file");

                // Case and separator spelling must not matter.
                TestAssert.True(
                    !PlanMode.IsPlanWriteOutsidePlanFile(
                        "write_file", planPath.ToUpperInvariant(), null),
                    "plan path is compared case-insensitively");

                // edit_file is a plan writer too, so it obeys the same path rules.
                TestAssert.True(
                    !PlanMode.IsPlanWriteOutsidePlanFile("edit_file", planPath, null),
                    "edit_file may write the plan file");
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("edit_file", "C:\\Windows\\evil.txt", null),
                    "edit_file cannot write anywhere else");

                // Unusual spellings that point at something other than the local plan
                // file must still be refused: a UNC share and a \\?-extended external
                // path can never resolve to the plan file, so both are denied.
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("write_file", "\\\\server\\share\\plan.md", null),
                    "a UNC path is refused");
                TestAssert.True(
                    PlanMode.IsPlanWriteOutsidePlanFile("write_file", "\\\\?\\C:\\Windows\\evil.txt", null),
                    "a \\?-extended external path is refused");
            });

            TestRunner.Run("cli.plan_mode_mode_cycle", () =>
            {
                // ask -> auto -> config -> plan -> ask
                TestAssert.Equal(
                    ToolApproval.ModeAuto, ToolApproval.NextModeInCycle(ToolApproval.ModeAsk), "ask cycles to auto");
                TestAssert.Equal(
                    ToolApproval.ModeConfig, ToolApproval.NextModeInCycle(ToolApproval.ModeAuto), "auto cycles to config");
                TestAssert.Equal(
                    PlanMode.ModePlan, ToolApproval.NextModeInCycle(ToolApproval.ModeConfig), "config cycles to plan");

                // Leaving plan lands on ask: the most conservative mode, so exiting
                // plan never silently loosens anything.
                TestAssert.Equal(
                    ToolApproval.ModeAsk, ToolApproval.NextModeInCycle(PlanMode.ModePlan), "plan cycles back to ask");

                // plan is a valid approval mode and IsPlanMode recognises only it.
                TestAssert.True(ToolApproval.IsValidMode("plan"), "plan is a valid mode");
                TestAssert.Equal("plan", ToolApproval.NormalizeMode("PLAN"), "plan normalises to lowercase");
                TestAssert.True(ToolApproval.IsPlanMode("plan"), "IsPlanMode matches plan");
                TestAssert.True(!ToolApproval.IsPlanMode(ToolApproval.ModeAsk), "ask is not plan mode");
                TestAssert.True(!ToolApproval.IsPlanMode(null), "null is not plan mode");
            });

            TestRunner.Run("cli.plan_mode_refuses_write_end_to_end", () =>
            {
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release"));
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The model asks to write to a file that is not the plan. Plan
                    // mode must refuse it, and the file must not appear: this is the
                    // guarantee the mode exists for.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "write_file";
                    string victim = Path.Combine(ws.Path, "must-not-exist.txt");
                    var args = new JObject();
                    args["filename"] = victim;
                    args["content"] = "plan mode should never write this";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file,write_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval plan"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("write the file");
                        CliRunner.WaitForText(cli, "not available in plan mode", 60000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);

                        TestAssert.True(
                            !File.Exists(victim),
                            "plan mode refused the write, so the file was never created");

                        // No approval prompt either: plan mode either reads or refuses.
                        TestAssert.True(
                            cli.Stdout.IndexOf(ToolApproval.ApprovalPrompt, StringComparison.Ordinal) < 0,
                            "plan mode shows no approval prompt");
                    }
                }
            });

            TestRunner.Run("cli.plan_mode_writes_plan_file", () =>
            {
                // The positive path: a write to the plan file itself is allowed in
                // plan mode and lands on disk. This pins the contract that the pinned
                // schema description, the allowlist and FileTools' resolution all agree.
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release"));
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "write_file";
                    string planPath = Path.Combine(ws.Path, "plans", "plan.md");
                    var args = new JObject();
                    args["filename"] = planPath;
                    args["content"] = "the plan body";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.FixedReply = "Plan written reply.";
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file,write_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path, "--approval plan"))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("write the plan");
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);

                        TestAssert.True(File.Exists(planPath), "plan mode allowed the write to the plan file");
                        TestAssert.True(
                            File.ReadAllText(planPath).Contains("the plan body"),
                            "the plan file contains what the model wrote");
                    }
                }
            });

            TestRunner.Run("cli.plan_mode_history_marker", () =>
            {
                // The system prompt is rebuilt every turn, so it carries no record of
                // when the mode last changed. On a mid-session toggle the model would
                // otherwise have to guess what it was doing a turn ago, so the toggle
                // must also drop a marker into the conversation history (the same
                // trick OpenCode uses for its synthetic mode-switch parts).
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.StreamDelayMs = 5;

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        // Distinct reply per turn: a shared FixedReply would let the
                        // second wait match the first turn's text already on screen.
                        server.FixedReply = "Turn one reply.";
                        cli.WriteLine("/plan on");
                        cli.WriteLine("make a plan for my app");
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);

                        TestAssert.True(
                            AnyBodyHasMessageText(server.ReceivedBodies, "You have switched to PLAN MODE"),
                            "enabling plan injects the in-history plan marker");
                        TestAssert.True(
                            AnyBodyHasMessageText(server.ReceivedBodies, "PLAN MODE IS ACTIVE"),
                            "enabling plan puts the block in the system prompt");

                        server.FixedReply = "Turn two reply.";
                        cli.WriteLine("/plan off");
                        cli.WriteLine("now implement it");
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);

                        TestAssert.True(
                            AnyBodyHasMessageText(server.ReceivedBodies, "You have switched out of PLAN MODE"),
                            "disabling plan injects the exit marker");
                    }
                }
            });

            TestRunner.Run("cli.plan_mode_toggle_collapses", () =>
            {
                // Two toggles with no chat in between must not stack two synthetic
                // "user" messages: the second replaces the first, so the request
                // carries exactly one marker.
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.FixedReply = "Toggle reply.";
                    server.StreamDelayMs = 5;

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("/plan on");
                        cli.WriteLine("/plan off");
                        cli.WriteLine("hello there");
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);

                        JArray last = MessagesOf(JObject.Parse(server.ReceivedBodies[server.ReceivedBodies.Count - 1]));
                        TestAssert.Equal(
                            1, CountPlanMarkers(last),
                            "back-to-back toggles collapse to a single marker message");
                    }
                }
            });

            TestRunner.Run("cli.tool_cancelled_by_user", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A ~30 second sleep: long enough for the test to send
                    // STOPTOOL while it runs, with no configured timeout, so
                    // only the user can stop it.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(cli.SendStopTool(5000), "STOPTOOL delivered through the control pipe");

                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);
                        CliRunner.WaitForTextAfter(cli, "cancelled by the user", "You: ", 30000);
                    }
                }
            });

            TestRunner.Run("cli.tool_timeout", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // tooltimeout.run_shell_command=2 must kill the ~30s sleep
                    // after two seconds and report the timeout to the model.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command",
                        toolTimeout: "run_shell_command=2");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "timed out after 2 second(s)", 60000);
                        CliRunner.WaitForTextAfter(cli, "timed out after 2 second(s)", "You: ", 30000);
                    }
                }
            });

            TestRunner.Run("cli.tool_tree_killed_on_timeout", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The tool runs `start /b /wait ping ...`, so its child ping
                    // outlives the 5s tool timeout; the job must kill both.
                    // /b keeps the extra process level the test needs but stops
                    // `start` from flashing a console window on the desktop.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "start /b /wait ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command",
                        toolTimeout: "run_shell_command=5");

                    int baselinePings = CountProcesses("ping");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c > baselinePings, 3000),
                            "child ping never appeared while the tool ran; kill assertion is vacuous");

                        CliRunner.WaitForText(cli, "timed out after 5 second(s)", 60000);

                        TestAssert.True(PollProcessCount("ping", c => c == baselinePings, 5000),
                            "child ping survived the tool timeout: the process tree was not killed");
                    }
                }
            });

            TestRunner.Run("cli.tool_tree_killed_on_abort", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // No per-tool timeout here: the STOPTOOL control-pipe
                    // command must kill the tool and its child ping.
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallName = "run_shell_command";
                    var args = new JObject();
                    args["command"] = "start /b /wait ping 127.0.0.1 -n 30 > nul";
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);
                    server.StreamDelayMs = 5;

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "run_shell_command");

                    int baselinePings = CountProcesses("ping");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("sleep for a while");
                        CliRunner.WaitForText(cli, "[running tool: run_shell_command", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c > baselinePings, 3000),
                            "child ping never appeared while the tool ran; kill assertion is vacuous");

                        TestAssert.True(cli.SendStopTool(5000), "STOPTOOL delivered through the control pipe");
                        CliRunner.WaitForText(cli, "cancelled by the user", 30000);

                        TestAssert.True(PollProcessCount("ping", c => c == baselinePings, 5000),
                            "child ping survived the tool abort: the process tree was not killed");

                        CliRunner.WaitForTextAfter(cli, "cancelled by the user", "You: ", 30000);
                    }
                }
            });

            TestRunner.Run("cli.clear_wipes_lastsession", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string lastsessionFile = Path.Combine(ws.Path, "sessions", "lastsession.json");

                    using (CliRunner cli = new CliRunner(
                        Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // After a completed turn the CLI mirrors the
                        // conversation into lastsession.json; the re-prompt
                        // only appears once that save is done.
                        cli.WriteLine("hello");
                        CliRunner.WaitForTextAfter(cli, server.FixedReply, "You: ", 30000);
                        TestAssert.True(File.Exists(lastsessionFile), "lastsession saved after a turn");

                        // /clear must wipe the in-memory context AND the
                        // on-disk auto-saved session, otherwise the next
                        // startup would restore the conversation the user
                        // just cleared.
                        cli.WriteLine("/clear");
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(15000);
                        while (File.Exists(lastsessionFile) && DateTime.UtcNow < deadline)
                            System.Threading.Thread.Sleep(50);
                        TestAssert.True(!File.Exists(lastsessionFile), "lastsession wiped by /clear");
                        TestAssert.True(!cli.HasExited, "CLI still alive after /clear");
                    }
                }
            });

            TestRunner.Run("cli.restored_oversized_session_capped", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Hand-crafted session (pre-cap era) whose tool message is ~160KB.
                    // With contextWindowSize=0 the percentage ladder never fires, so
                    // the absolute per-message guard must cap it before the request.
                    StringBuilder huge = new StringBuilder();
                    for (int i = 0; i < 20000; i++)
                        huge.Append("abcdefgh");
                    string sessionJson =
                        "{\"Version\":1,\"SavedAtUtc\":\"2026-01-01T00:00:00Z\",\"Messages\":[" +
                        "{\"Role\":\"user\",\"Content\":\"do the thing\",\"ToolCallId\":\"\",\"ToolCalls\":[]}," +
                        "{\"Role\":\"tool\",\"Content\":\"" + huge.ToString() + "\",\"ToolCallId\":\"call_1\",\"ToolCalls\":[]}" +
                        "]}";
                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    Directory.CreateDirectory(sessionsDir);
                    File.WriteAllText(Path.Combine(sessionsDir, "huge.json"), sessionJson, Encoding.UTF8);

                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("/load huge");
                        CliRunner.WaitForText(cli, "Session loaded: huge", 15000);

                        cli.WriteLine("continue");
                        CliRunner.WaitForText(cli, "[Context safety - oversized message(s) capped to the per-message limit]", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.lastsession_save_capped", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A pre-cap-era lastsession: 100 messages of ~19000 chars each
                    // (~2 MB), far over the 1 MB cap.
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"Version\":1,\"SavedAtUtc\":\"2026-01-01T00:00:00Z\",\"Messages\":[");
                    for (int i = 0; i < 100; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        string content = i == 0 ? "OLD_MARKER_FIRST_MESSAGE" : new string('a', 19000);
                        sb.Append("{\"Role\":\"user\",\"Content\":\"" + content
                            + "\",\"ToolCallId\":\"\",\"ToolCalls\":[]}");
                    }
                    sb.Append("]}");
                    string sessionsDir = Path.Combine(ws.Path, "sessions");
                    Directory.CreateDirectory(sessionsDir);
                    File.WriteAllText(Path.Combine(sessionsDir, "lastsession.json"), sb.ToString(), Encoding.UTF8);

                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    string file = Path.Combine(sessionsDir, "lastsession.json");
                    long size = 0;
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "-- restored lastsession: 100 messages --", 20000);

                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        // The re-prompt after the reply proves the loop finished the
                        // iteration (the lastsession mirror runs at its end).
                        CliRunner.WaitForTextAfter(cli, server.FixedReply, "You: ", 15000);

                        // The auto-save after the turn must have capped the mirror.
                        TestAssert.True(File.Exists(file), "lastsession.json exists after the turn");
                        size = new FileInfo(file).Length;
                        TestAssert.True(size <= SessionStore.MaxLastSessionBytes,
                            "lastsession.json is within the cap: " + size + " <= "
                            + SessionStore.MaxLastSessionBytes + "\nCLI stderr:\n" + cli.Stderr);
                    }

                    List<JObject> messages = null;
                    TestAssert.True(SessionStore.Load(sessionsDir, "lastsession", out messages),
                        "capped lastsession.json still parses");
                    TestAssert.True(messages.Count < 102,
                        "oldest messages were dropped: " + messages.Count + " < 102");
                    TestAssert.True(messages.Count >= 2, "newest messages kept: " + messages.Count);
                    TestAssert.Equal(server.FixedReply, (string)messages[messages.Count - 1]["Content"],
                        "last message is the new reply");
                    TestAssert.Equal("hello", (string)messages[messages.Count - 2]["Content"],
                        "second-to-last is the new user turn");
                    bool oldGone = true;
                    for (int i = 0; i < messages.Count; i++)
                    {
                        string content = (string)messages[i]["Content"];
                        if (content != null && content.IndexOf("OLD_MARKER_FIRST_MESSAGE") >= 0)
                            oldGone = false;
                    }
                    TestAssert.True(oldGone, "the oldest (marker) message was dropped");
                }
            });

            TestRunner.Run("cli.compaction_micro_command", () =>
            {
                string fileToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release"));
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.ToolCallRoundsRemaining = 3;
                    server.ToolCallName = "read_file";
                    server.StreamDelayMs = 5;
                    var args = new JObject();
                    args["filename"] = Path.Combine(ws.Path, "LLMSettings.ini");
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("read the settings file");
                        // Three tool rounds then a plain reply => three tool groups in context.
                        CliRunner.WaitForText(cli, "[tool output]", 30000);
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("/compact");
                        CliRunner.WaitForText(cli, "Compaction: removed 2 old tool messages.", 20000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.compaction_full_command", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // 16 turns of ~2KB markdown replies (~29k chars) so the
                    // conversation grows well past the recent-tail window
                    // (~24k chars at the default char ratio) even with the
                    // tail's backward-walk overshoot; the older head must then
                    // be summarized (the fake server answers the summary
                    // request too).
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 16; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(cli, "[Conversation compacted]", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                    }
                }
            });

            TestRunner.Run("cli.compaction_full_short_noop", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.StreamDelayMs = 5;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("hi");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // The whole conversation fits in the recent-tail window:
                        // no LLM summary call, no marker, nothing is dropped.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(
                            cli, "[Compaction: conversation fits in the keep window - nothing to summarize]", 20000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.True(
                            !cli.Stdout.Contains("[Conversation compacted]"),
                            "no summary for a conversation that fits in the keep window");
                    }
                }
            });

            TestRunner.Run("cli.truncation_keeps_user_message", () =>
            {
                string fileToolsDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "FileTools", "bin", "Release");
                string fileToolsExe = Path.Combine(fileToolsDir, "FileTools.exe");
                if (!File.Exists(fileToolsExe))
                {
                    TestRunner.Skip("FileTools.exe not built: " + fileToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // LM Studio-style server: its Jinja chat template raises
                    // "No user query found in messages" (400) when the payload
                    // carries no "user" role. Twelve tool rounds reading the
                    // first 8000 chars of the file each (read_file's limit) keep
                    // the two newest groups at ~16k chars (~5.4k tokens) - past
                    // the 95% band of the 5000 window even after micro-compact,
                    // so the bounded 30% oldest-first truncation must chew toward
                    // the front, where the single user message sits. Dropping
                    // it used to leave a user-less payload and the 400.
                    server.RejectPayloadsWithoutUserMessage = true;
                    server.ToolCallRoundsRemaining = 12;
                    server.ToolCallName = "read_file";
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;

                    string bigFile = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 280; i++)
                        big.AppendLine("padding line " + i + " to make the tool output ~17k chars");
                    File.WriteAllText(bigFile, big.ToString(), Encoding.UTF8);

                    var args = new JObject();
                    args["filename"] = bigFile;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                    File.Copy(fileToolsExe, Path.Combine(toolsDir, "FileTools.exe"), true);
                    File.Copy(Path.Combine(fileToolsDir, "FileTools.json"), Path.Combine(toolsDir, "FileTools.json"), true);
                    File.Copy(Path.Combine(fileToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 5000, tools: "read_file");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        cli.WriteLine("read the big file");
                        CliRunner.WaitForText(cli, "[tool output]", 30000);
                        // The usage ladder fires mid-turn: micro-compact drops old
                        // groups, then the bounded 30% truncation eats toward the front.
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        // The turn must survive: the outgoing payload kept its
                        // "user" role so the template accepted it.
                        CliRunner.WaitForText(cli, server.FixedReply, 60000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            !cli.Stdout.Contains("No user query found in messages"),
                            "payload must always contain a user message");
                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the truncation loop");
                    }
                }
            });

            TestRunner.Run("cli.spike_summarized_before_truncate", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Thirteen turns of ~2KB markdown (~27k chars) then one big tool
                    // output (the 20k shell cap) puts the mid-turn spike far past
                    // 95% of the 12000 window. The old ladder chewed the oldest
                    // messages away in geometric 30% passes with no summary; the
                    // summary pass must now absorb the spike (recap + verbatim
                    // tail) and no destructive truncation must run at all.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallSkipFirstRequests = 13;
                    server.ToolCallName = "run_shell_command";
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 3000; i++)
                        big.Append("0123456789");
                    File.WriteAllText(bigPath, big.ToString(), Encoding.UTF8);
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 12000, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 13; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);

                        // The spike must be absorbed by the summarize pass.
                        CliRunner.WaitForText(cli, "[Context usage high - summarizing conversation...", 30000);
                        CliRunner.WaitForText(cli, "[Conversation summarized - continuing...]", 30000);
                        // Requests so far: 13 turns + tool call + summary = 15, so the
                        // continued reply is turn 16.
                        CliRunner.WaitForText(cli, "# Reply turn 16", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        TestAssert.True(
                            !cli.Stdout.Contains("[Context usage high - truncated"),
                            "no destructive truncation: the spike was absorbed by the summary");
                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the spike");
                    }
                }
            });

            TestRunner.Run("cli.truncate_after_summary_still_over", () =>
            {
                string shellToolsDir = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Tools", "ShellTools", "bin", "Release"));
                string shellToolsExe = Path.Combine(shellToolsDir, "ShellTools.exe");
                if (!File.Exists(shellToolsExe))
                {
                    TestRunner.Skip("ShellTools.exe not built: " + shellToolsExe);
                    return;
                }

                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The 6000 window is smaller than the 8k-token recent tail
                    // (~24k chars at the default char ratio), so even a successful
                    // summary leaves recap + tail far above 95%. The ladder must run
                    // the bounded 30% truncation AFTER the summary, and - because one
                    // 20k tool output dominates the tail - finish with the last-resort
                    // shrink of the single biggest message.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.ToolCallRoundsRemaining = 1;
                    server.ToolCallSkipFirstRequests = 6;
                    server.ToolCallName = "run_shell_command";
                    string bigPath = Path.Combine(ws.Path, "big.txt");
                    StringBuilder big = new StringBuilder();
                    for (int i = 0; i < 3000; i++)
                        big.Append("0123456789");
                    File.WriteAllText(bigPath, big.ToString(), Encoding.UTF8);
                    var args = new JObject();
                    args["command"] = "type " + bigPath;
                    server.ToolCallArguments = args.ToString(Newtonsoft.Json.Formatting.None);

                    string toolsDir = Path.Combine(ws.Path, "tools");
                    Directory.CreateDirectory(toolsDir);
                File.Copy(shellToolsExe, Path.Combine(toolsDir, "ShellTools.exe"), true);
                File.Copy(Path.Combine(shellToolsDir, "ShellTools.json"), Path.Combine(toolsDir, "ShellTools.json"), true);
                File.Copy(Path.Combine(shellToolsDir, "Newtonsoft.Json.dll"), Path.Combine(toolsDir, "Newtonsoft.Json.dll"), true);
                File.Copy(Path.Combine(shellToolsDir, "Ude.dll"), Path.Combine(toolsDir, "Ude.dll"), true);

                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 6000, tools: "run_shell_command");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 6; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        cli.WriteLine("print the big file");
                        CliRunner.WaitForText(cli, "characters omitted: output exceeded the 20000 character limit", 60000);

                        // Pass 2: the spike is summarized first...
                        CliRunner.WaitForText(cli, "[Context usage high - summarizing conversation...", 30000);
                        CliRunner.WaitForText(cli, "[Conversation summarized - continuing...]", 30000);
                        // ...pass 3: the recap + tail is still above 95%, so the bounded
                        // truncation runs AFTER the successful summary (oldest 30%
                        // prefix, user messages protected).
                        CliRunner.WaitForTextAfter(
                            cli, "[Conversation summarized - continuing...]",
                            "[Context usage high - truncated", 30000);
                        // ...pass 4: the single 20k tool output still dominates the tail,
                        // so the biggest message is shrunk to fit the window.
                        CliRunner.WaitForTextAfter(
                            cli, "[Context usage high - truncated",
                            "[Context usage high - message content truncated to fit the context window]", 30000);
                        // Requests so far: 6 turns + tool call + summary = 8, so the
                        // continued reply is turn 9.
                        CliRunner.WaitForText(cli, "# Reply turn 9", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "turn must survive the full ladder");
                    }
                }
            });

            TestRunner.Run("cli.resummarization_carries_prior_summary", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // Two manual /compactfull passes. Fifteen turns (~26k chars) push
                    // the conversation past the ~24k keep window so the first
                    // compaction actually summarizes the older head. The second one
                    // then summarizes a conversation that already holds a [Previous
                    // conversation summary] marker: ExtractPreviousSummary must lift
                    // the first recap out and BuildSummaryPrompt must embed it in a
                    // <prior-summary> block of the outgoing request.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 0);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 20000);

                        for (int turn = 1; turn <= 15; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + turn, 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        // First compaction: summary request #16 ("# Reply turn 16")
                        // becomes the stored recap.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForText(cli, "[Conversation compacted]", 30000);
                        CliRunner.WaitForTextAfter(cli, "[Conversation compacted]", "# Reply turn 16", 30000);
                        CliRunner.WaitForTextAfter(cli, "End of turn 16.", "You:", 15000);

                        // Two more turns (requests #17 and #18).
                        for (int turn = 16; turn <= 17; turn++)
                        {
                            cli.WriteLine("message " + turn);
                            CliRunner.WaitForText(cli, "# Reply turn " + (turn + 1), 30000);
                            CliRunner.WaitForText(cli, "You:", 15000);
                        }

                        // Second compaction: summary request #19 must embed the
                        // first recap in a <prior-summary> block.
                        cli.WriteLine("/compactfull");
                        CliRunner.WaitForTextAfter(cli, "# Reply turn 18", "[Conversation compacted]", 30000);
                        CliRunner.WaitForTextAfter(cli, "[Conversation compacted]", "# Reply turn 19", 30000);
                        CliRunner.WaitForTextAfter(cli, "End of turn 19.", "You:", 15000);

                        TestAssert.Contains(
                            server.LastBody, "<prior-summary>",
                            "second summary request must carry the prior-summary block");
                        TestAssert.Contains(
                            server.LastBody, "# Reply turn 16",
                            "second summary request must embed the first recap text");

                        // Ordering is the whole point of this test. The merge
                        // instructions must be the LAST thing the model reads before
                        // the template. When they were not, the prompt ended with
                        // "Summarize the conversation above", and since <conversation>
                        // holds only post-compaction messages the model dropped
                        // anything the old recap did not repeat - which is how a
                        // second compaction lost the first recap's content.
                        //
                        // Assertions run on the DECODED user prompt, not the raw
                        // body: JSON escaping would otherwise hide clauses that
                        // contain quotes, e.g. "Next Move".
                        string prompt = ExtractUserPrompt(server.LastBody);
                        int idxConversation = prompt.IndexOf("<conversation>", StringComparison.Ordinal);
                        int idxPriorOpen = prompt.IndexOf("<prior-summary>", StringComparison.Ordinal);
                        int idxPriorClose = prompt.IndexOf("</prior-summary>", StringComparison.Ordinal);
                        int idxTemplate = prompt.IndexOf("<template>", StringComparison.Ordinal);
                        TestAssert.True(
                            idxConversation >= 0 && idxConversation < idxPriorOpen,
                            "conversation block must precede the prior-summary block");
                        TestAssert.True(
                            idxPriorOpen < idxPriorClose,
                            "prior-summary block must be closed");
                        TestAssert.True(
                            idxPriorClose < idxTemplate,
                            "prior-summary block must precede the template");

                        // The merge instruction itself, and the clauses that keep the
                        // model carrying state forward instead of filtering the recap
                        // against the visible conversation.
                        int idxMerge = prompt.IndexOf("is discarded after this", StringComparison.Ordinal);
                        TestAssert.True(
                            idxMerge > idxPriorClose,
                            "merge instructions must come AFTER the prior-summary block, "
                            + "so they are the final instruction before the template "
                            + "(merge_at=" + idxMerge + " prior_close=" + idxPriorClose + ")");
                        TestAssert.True(
                            idxMerge > 0 && idxMerge < idxTemplate,
                            "merge instructions must sit between the prior summary and the template");
                        TestAssert.Contains(
                            prompt, "even when the <conversation> does not mention them",
                            "merge instructions must authorize carrying forward state the "
                            + "new window does not repeat");
                        TestAssert.Contains(
                            prompt, "Drop only what is finished and no longer needed",
                            "merge instructions must bound what may be dropped");
                        TestAssert.Contains(
                            prompt, "Next Move",
                            "merge instructions must require Objective/Next Move refresh");
                        TestAssert.Contains(
                            prompt, "reflect the current work state",
                            "merge instructions must require Objective/Next Move refresh");
                        TestAssert.True(
                            prompt.IndexOf("Summarize the conversation above", StringComparison.Ordinal) < 0,
                            "the merge prompt must not end by scoping the task to the "
                            + "conversation block, which drops the prior recap");

                        cli.WriteLine("continue");
                        CliRunner.WaitForText(cli, "# Reply turn 20", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        TestAssert.True(
                            !cli.Stdout.Contains("Request to LLM Failed"),
                            "chat must continue after the re-summarization");
                    }
                }
            });

            TestRunner.Run("cli.token_calibration_usage", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The server reports real-ish usage (0.48 tok/char). With a window of
                    // 1720, before turn 3 the legacy chars/3 heuristic would sit at ~0.70
                    // (below every compaction threshold), while the recalibrated counter
                    // crosses the ladder thresholds - so turn 3 MUST trigger an automatic
                    // compaction pass, and turn 2 must not.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.IncludeUsagePromptTokens = true;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 1720);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("first message");
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // Turn 2 is still below every threshold => plain reply, no compaction.
                        cli.WriteLine("second message");
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.True(
                            !cli.Stdout.Contains("[Context usage high")
                            && !cli.Stdout.Contains("[Conversation summarized"),
                            "no compaction before turn 3");

                        // Calibration accrued over turns 1-2 => turn 3 (before processing) crosses the
                        // ladder and truncates the oldest messages (the chars/3 heuristic
                        // would be at ~0.70 here and do nothing). The reply follows as #3.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - truncated", 30000);
                        CliRunner.WaitForText(cli, "# Reply turn 3", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);
                        TestAssert.Contains(cli.Stdout, "[Context usage high", "calibrated usage triggered compaction at turn 3");
                    }
                }
            });

            TestRunner.Run("cli.stream_stall_abort_and_save", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The first chat request sends one chunk then freezes. The stall
                    // watchdog must abort the request and return the CLI to its prompt
                    // so the user can still /save.
                    server.FixedReply = "Hello there.";
                    server.StreamDelayMs = 5;
                    server.StallFirstChatRequestMs = 20000;
                    WriteCliIni(ws.Path, server.BaseUrl, streamStallTimeout: 3, requestTimeout: 60);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        cli.WriteLine("hello stall");
                        CliRunner.WaitForText(cli, "partial ", 15000);

                        // The stream goes silent: the watchdog aborts after ~3 s.
                        CliRunner.WaitForText(cli, "Request to LLM Failed", 30000);
                        TestAssert.Contains(cli.Stdout, "model stalled", "stall reason printed");
                        CliRunner.WaitForText(cli, "You:", 30000);

                        // The prompt is back: saving must work right after a stall.
                        cli.WriteLine("/save after_stall");
                        CliRunner.WaitForText(cli, "Session saved: after_stall", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "after_stall.json");
                        TestAssert.True(File.Exists(sessionFile), "session file created");
                    }
                }
            });

            TestRunner.Run("cli.summary_fail_truncate_fallback", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // A short first turn plus one long message puts the start of
                    // turn 3 at ~92% (29591 chars / 3 / 10670): inside the
                    // silent-summarize band (90-95%) so the free truncate pass
                    // (>=95%) stays quiet and the summarize pass fires with a
                    // substantial head (the first turn). The summarize pass
                    // (request #3) is injected as a 500 so the CLI must fall
                    // back to truncation.
                    server.LongMarkdownReplies = true;
                    server.StreamDelayMs = 5;
                    server.StreamChunkChars = 64;
                    server.FailChatRequestAtTurn = 3;
                    WriteCliIni(ws.Path, server.BaseUrl, contextWindowSize: 10670);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);

                        StringBuilder shortFirst = new StringBuilder();
                        for (int i = 0; i < 2000; i++)
                            shortFirst.Append('a');
                        cli.WriteLine(shortFirst.ToString());
                        CliRunner.WaitForText(cli, "# Reply turn 1", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        StringBuilder longSecond = new StringBuilder();
                        for (int i = 0; i < 24000; i++)
                            longSecond.Append('b');
                        cli.WriteLine(longSecond.ToString());
                        CliRunner.WaitForText(cli, "# Reply turn 2", 30000);
                        CliRunner.WaitForText(cli, "You:", 15000);

                        // Turn 3 starts at ~92%: silent summarize fires, fails (500),
                        // and the CLI falls back to truncation. The failure reason
                        // is now printed in the notice.
                        cli.WriteLine("third message");
                        CliRunner.WaitForText(cli, "[Context usage high - summary failed (", 30000);
                        CliRunner.WaitForText(cli, "truncated", 15000);

                        // The main request still succeeds. It is the server's 4th chat
                        // request (#3 was the failed summary), so it is labeled turn 4.
                        CliRunner.WaitForText(cli, "# Reply turn 4", 30000);
                        CliRunner.WaitForText(cli, "You:", 30000);

                        cli.WriteLine("/save after_summary");
                        CliRunner.WaitForText(cli, "Session saved: after_summary", 15000);

                        string sessionFile = Path.Combine(ws.Path, "sessions", "after_summary.json");
                        string json = File.ReadAllText(sessionFile, Encoding.UTF8);
                        TestAssert.Contains(json, "Reply turn 4", "latest reply preserved");
                    }
                }
            });
            TestRunner.Run("endpoint_url.versioned_base_keeps_legacy_v1", () =>
            {
                // A base ending in /v1 already carries the version: adding another
                // "/v1" would be wrong, but so would dropping it — the endpoint must
                // be byte-identical to what the pre-existing code produced.
                TestAssert.Equal(
                    "https://api.openai.com/v1/chat/completions",
                    EndpointUrl.ChatCompletions("https://api.openai.com/v1", ""),
                    "openai v1 base");
                TestAssert.Equal(
                    "http://127.0.0.1:11434/v1/chat/completions",
                    EndpointUrl.ChatCompletions("http://127.0.0.1:11434/v1/", ""),
                    "ollama base with trailing slash");
                TestAssert.Equal(
                    "https://api.openai.com/v1/models",
                    EndpointUrl.Models("https://api.openai.com/v1"),
                    "models on a v1 base");
            });

            TestRunner.Run("endpoint_url.unversioned_base_keeps_v1", () =>
            {
                // Backward compatibility: a bare base has no version of its own, so it
                // keeps the historical /v1/... that every existing config relies on.
                TestAssert.Equal(
                    "http://127.0.0.1:8080/v1/chat/completions",
                    EndpointUrl.ChatCompletions("http://127.0.0.1:8080", ""),
                    "bare base");
                TestAssert.Equal(
                    "http://127.0.0.1:8080/v1/models",
                    EndpointUrl.Models("http://127.0.0.1:8080"),
                    "models on a bare base");
                TestAssert.Equal(
                    "http://127.0.0.1:8080/v1/embeddings",
                    EndpointUrl.Embeddings("http://127.0.0.1:8080"),
                    "embeddings on a bare base");
                TestAssert.Equal(
                    "http://127.0.0.1:8080/v1/chat/completions",
                    EndpointUrl.ChatCompletions("  http://127.0.0.1:8080  ", null),
                    "whitespace and null chatpath");
            });

            TestRunner.Run("endpoint_url.bigmodel_base_drops_the_extra_v1", () =>
            {
                // bigmodel.cn publishes its OpenAI-compatible base as
                // https://open.bigmodel.cn/api/paas/v4 and serves
                // /api/paas/v4/chat/completions. The old hardcoded "/v1" produced
                // /api/paas/v4/v1/chat/completions, which no provider accepts.
                TestAssert.Equal(
                    "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                    EndpointUrl.ChatCompletions("https://open.bigmodel.cn/api/paas/v4", ""),
                    "bigmodel chat");
                TestAssert.Equal(
                    "https://open.bigmodel.cn/api/paas/v4/models",
                    EndpointUrl.Models("https://open.bigmodel.cn/api/paas/v4"),
                    "bigmodel models");
                TestAssert.Equal(
                    "https://open.bigmodel.cn/api/paas/v4/embeddings",
                    EndpointUrl.Embeddings("https://open.bigmodel.cn/api/paas/v4"),
                    "bigmodel embeddings");
                // A non-numeric segment is not a version: /v1beta keeps /v1, and a host
                // that merely starts with v must not be mistaken for one.
                TestAssert.Equal(
                    "https://example.com/v1beta/v1/chat/completions",
                    EndpointUrl.ChatCompletions("https://example.com/v1beta", ""),
                    "v1beta is not a version segment");
                TestAssert.Equal(
                    "https://v1.example.com/v1/chat/completions",
                    EndpointUrl.ChatCompletions("https://v1.example.com", ""),
                    "host starting with v is not a version segment");
            });

            TestRunner.Run("endpoint_url.explicit_chatpath_wins", () =>
            {
                TestAssert.Equal(
                    "https://open.bigmodel.cn/api/paas/v4/chat/completions",
                    EndpointUrl.ChatCompletions("https://open.bigmodel.cn/api/paas/v4", "/chat/completions"),
                    "explicit path is used verbatim");
                // A leading slash is optional and a trailing one is trimmed, so pasting
                // "chat/completions/" still works.
                TestAssert.Equal(
                    "https://example.com/v1/completions",
                    EndpointUrl.ChatCompletions("https://example.com/v1", " completions/ "),
                    "path normalized without leading slash");
                // An explicit path overrides detection: even a bare base uses it.
                TestAssert.Equal(
                    "http://127.0.0.1:8080/openai/chat",
                    EndpointUrl.ChatCompletions("http://127.0.0.1:8080", "/openai/chat"),
                    "explicit path overrides the legacy /v1");
                // A full URL in chatpath is the directEndpoint escape hatch.
                TestAssert.Equal(
                    "https://gw.example.com/openai/v1/chat/completions",
                    EndpointUrl.ChatCompletions("https://gw.example.com", "https://gw.example.com/openai/v1/chat/completions"),
                    "full URL used as the complete endpoint");
                // A base that already is the full endpoint is passed through untouched.
                TestAssert.Equal(
                    "https://gw.example.com/openai/chat/completions",
                    EndpointUrl.ChatCompletions("https://gw.example.com/openai/chat/completions/", ""),
                    "base that already is an endpoint");
            });

            TestRunner.Run("cli.chat_path_resolved_from_versioned_base", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // llmserver carries a version segment the fake server does not
                    // route under /v1, so only a correctly resolved path gets a reply.
                    server.AcceptUnversionedChatPath = true;
                    WriteCliIni(ws.Path, server.BaseUrl + "/api/paas/v4");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                    }
                    TestAssert.Equal("/api/paas/v4/chat/completions", server.LastPath,
                        "request went to the versioned base without an extra /v1");
                }
            });

            TestRunner.Run("cli.chat_path_override_replaces_the_whole_suffix", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    server.AcceptUnversionedChatPath = true;
                    // A bare base with an explicit path: nothing of the default
                    // "/v1/chat/completions" may survive alongside it.
                    WriteCliIni(ws.Path, server.BaseUrl, chatPath: "/api/paas/v4/chat/completions");

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                    }
                    TestAssert.Equal("/api/paas/v4/chat/completions", server.LastPath,
                        "explicit chatpath replaced the whole default suffix");
                }
            });

            TestRunner.Run("cli.unversioned_base_still_uses_v1", () =>
            {
                using (FakeOpenAiServer server = new FakeOpenAiServer())
                using (TempWorkspace ws = PackageCliWorkspace())
                {
                    // The fake server only routes /v1/chat/completions here, so a
                    // regression in the default would 404 and time out.
                    WriteCliIni(ws.Path, server.BaseUrl);

                    string exe = Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe");
                    using (CliRunner cli = new CliRunner(exe, ws.Path))
                    {
                        CliRunner.WaitForText(cli, "You:", 15000);
                        cli.WriteLine("hello");
                        CliRunner.WaitForText(cli, server.FixedReply, 30000);
                    }
                    TestAssert.Equal("/v1/chat/completions", server.LastPath,
                        "bare base keeps the historical /v1 path");
                }
            });
        }

        /// <summary>
        /// Fake curl.exe for the curl-fallback tests. Behavior is selected by the
        /// FAKE_CURL_MODE env var:
        ///   "ok"           - read the request from the named pipe, emit a short SSE reply
        ///   "hang"         - never connect/respond (simulates a frozen server)
        ///   "hang-connect" - behave like curl hitting --connect-timeout (exit 28) only
        ///                    when the flag is actually present in the arguments
        /// </summary>
        static int FakeCurlMain(string[] args)
        {
            string mode = Environment.GetEnvironmentVariable("FAKE_CURL_MODE");
            if (mode == null) mode = "";
            bool hasConnectTimeout = false;
            string pipeName = null;

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--connect-timeout")
                    hasConnectTimeout = true;
                int pipeIdx = a.IndexOf("\\pipe\\", StringComparison.OrdinalIgnoreCase);
                if (pipeIdx >= 0)
                {
                    pipeName = a.Substring(pipeIdx + 6).Trim('"');
                }
            }

            if (mode == "hang-connect" && hasConnectTimeout)
                return 28; // curl(1)'s timeout exit code

            if (mode == "hang")
            {
                Thread.Sleep(60000);
                return 0;
            }

            if (pipeName != null)
            {
                // curl(1) opens the \\.\pipe\<name> body pipe directly. .NET's
                // FileStream rejects device paths, so open it via CreateFile.
                IntPtr handle = CreateFile("\\\\.\\pipe\\" + pipeName,
                    0x80000000 /*GENERIC_READ*/, 3 /*share r/w*/, IntPtr.Zero,
                    3 /*OPEN_EXISTING*/, 0x80 /*NORMAL*/, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while (ReadFile(handle, buf, (uint)buf.Length, out n, IntPtr.Zero) && n > 0) { }
                    CloseHandle(handle);
                }
            }

            string evt = "data: {\"choices\":[{\"delta\":{\"content\":\"curl works\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                + "data: [DONE]\n\n";
            Console.Write(evt);
            Console.Out.Flush();
            return 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr secAttrs,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        static extern bool ReadFile(IntPtr handle, byte[] buffer, uint count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        static JArray MessagesOf(JObject payload)
        {
            JArray messages = payload["messages"] as JArray;
            return messages ?? new JArray();
        }

        /// <summary>
        /// True when any captured request body has a message whose decoded content
        /// contains the needle. Used to assert the in-history plan-mode markers that
        /// land in the conversation (as opposed to the per-turn system prompt).
        /// </summary>
        static bool AnyBodyHasMessageText(List<string> bodies, string needle)
        {
            foreach (string body in bodies)
            {
                foreach (JToken token in MessagesOf(JObject.Parse(body)))
                {
                    JObject msg = token as JObject;
                    if (msg == null)
                        continue;
                    string content = (string)msg["content"];
                    if (content != null && content.IndexOf(needle, StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Number of synthetic plan-mode marker messages in one request body.</summary>
        static int CountPlanMarkers(JArray messages)
        {
            int count = 0;
            foreach (JToken token in messages)
            {
                JObject msg = token as JObject;
                if (msg == null)
                    continue;
                string content = (string)msg["content"];
                if (content != null && content.IndexOf("You have switched", StringComparison.Ordinal) >= 0)
                    count++;
            }
            return count;
        }

        static int CountReasoning(JObject payload)
        {
            int count = 0;
            foreach (JObject msg in MessagesOf(payload))
            {
                if (!string.IsNullOrEmpty((string)msg["reasoning_content"]))
                    count++;
            }
            return count;
        }

        static JObject FirstAssistantWithReasoning(JObject payload)
        {
            foreach (JObject msg in MessagesOf(payload))
            {
                if ((string)msg["role"] == "assistant" && !string.IsNullOrEmpty((string)msg["reasoning_content"]))
                    return msg;
            }
            return null;
        }

        static string FirstUserContent(JArray messages)
        {
            foreach (JToken token in messages)
            {
                JObject msg = token as JObject;
                if (msg == null || (string)msg["role"] != "user")
                    continue;
                string content = (string)msg["content"];
                if (content != null)
                    return content;
            }
            return "";
        }

        static bool HasConfigKey(JObject cfgObj, string name)
        {
            foreach (JProperty prop in cfgObj.Properties())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Decoded content of the last "user" message in a captured chat request body.
        /// Prompt-shape assertions must run on this rather than the raw JSON: escaping
        /// would otherwise break any needle containing a quote.
        /// </summary>
        static string ExtractUserPrompt(string body)
        {
            JObject obj = JObject.Parse(body);
            JArray messages = obj["messages"] as JArray;
            if (messages == null)
                throw new TestFailureException("request body has no messages array");

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                if ((string)messages[i]["role"] == "user")
                    return (string)messages[i]["content"] ?? "";
            }
            throw new TestFailureException("request body has no user message");
        }

        static TempWorkspace PackageCliWorkspace()
        {
            TempWorkspace ws = new TempWorkspace("cli-run");
            string srcExe = ToolClient.ProductExe("SuperSimpleAgentCLI.exe");
            string srcJson = ToolClient.ProductExe("Newtonsoft.Json.dll");
            if (!File.Exists(srcExe))
                throw new TestFailureException("SuperSimpleAgentCLI.exe not packaged: " + srcExe);
            File.Copy(srcExe, Path.Combine(ws.Path, "SuperSimpleAgentCLI.exe"), true);
            if (File.Exists(srcJson))
                File.Copy(srcJson, Path.Combine(ws.Path, "Newtonsoft.Json.dll"), true);
            return ws;
        }

        static int CountProcesses(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            int count = processes.Length;
            foreach (Process p in processes)
                p.Dispose();
            return count;
        }

        static bool PollProcessCount(string processName, Func<int, bool> predicate, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int count = CountProcesses(processName);
                if (predicate(count))
                    return true;
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(150);
            }
        }

        static string Repeat(string text, int times)
        {
            StringBuilder sb = new StringBuilder(text.Length * times);
            for (int i = 0; i < times; i++)
                sb.Append(text);
            return sb.ToString();
        }

        static void WriteCliIni(string dir, string llmServer, int contextWindowSize = 0, string tools = "",
            int streamStallTimeout = 120, int requestTimeout = 300, string toolTimeout = "",
            int repetitionGuard = -1, string chatPath = "")
        {
            string path = Path.Combine(dir, "LLMSettings.ini");
            File.WriteAllText(path,
                "[System]\r\n" +
                "apikey=test-key\r\n" +
                "llmserver=" + llmServer + "\r\n" +
                "chatpath=" + (chatPath ?? "") + "\r\n" +
                "model=test-model\r\n" +
                "sysprompt=\"You are a test assistant.\"\r\n" +
                "contextWindowSize=" + contextWindowSize + "\r\n" +
                "streamstalltimeout=" + streamStallTimeout + "\r\n" +
                "requesttimeout=" + requestTimeout + "\r\n" +
                (repetitionGuard < 0 ? "" : "repetitionguard=" + repetitionGuard + "\r\n") +
                "[Tools]\r\n" +
                "tools=" + (tools ?? "") + "\r\n" +
                "toolsrequiringapproval=\r\n" +
                (string.IsNullOrEmpty(toolTimeout) ? "" : "tooltimeout." + toolTimeout + "\r\n") +
                "[Appearance]\r\n" +
                "assistantname=LLM\r\n" +
                "markdownparsing=0\r\n" +
                "[RAG]\r\n" +
                "ragenabled=0\r\n",
                Encoding.UTF8);
        }
    }
}
