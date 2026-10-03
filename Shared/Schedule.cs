using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Parses and evaluates task schedules. Three kinds are supported:
///   at    - one-shot, fires at a fixed instant (ISO 8601 time, or a relative
///           offset such as "20m" resolved against the creation time);
///   every - fixed interval, fires at creation + k * interval;
///   cron  - standard five-field Vixie cron (minute hour day-of-month month
///           day-of-week), optionally followed by a time-zone id.
///
/// All times returned are UTC. A cron schedule without an explicit time zone
/// uses the host's local time zone (mirroring how cron behaves on a server).
/// </summary>
public static class Schedule
{
    public enum Kind
    {
        At,
        Every,
        Cron
    }

    /// <summary>
    /// A parsed, fully resolved schedule. For "at" the instant is fixed at
    /// parse time; for "every" the origin and interval are fixed; for "cron"
    /// the field sets and time zone are fixed. Call NextRunAfter to find the
    /// next fire time strictly after a given instant.
    /// </summary>
    public sealed class Parsed
    {
        public Kind Kind;
        public DateTime AtUtc;            // At only
        public TimeSpan Every;           // Every only
        public DateTime EveryOriginUtc;   // Every only (creation time)
        public Cron Cron;                 // Cron only
        public TimeZoneInfo TimeZone;     // Cron only (host local when unset)
        public string Source;             // original input, for display

        public string Display
        {
            get
            {
                if (Kind == Kind.At)
                    return "at " + AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " local";
                if (Kind == Kind.Every)
                    return "every " + DescribeInterval(Every);
                return "cron " + (Cron != null ? Cron.Source : "");
            }
        }

        // Next fire time strictly after afterUtc, or null when the schedule can
        // no longer fire (a one-shot "at" whose instant has passed).
        public DateTime? NextRunAfter(DateTime afterUtc)
        {
            if (Kind == Kind.At)
                return AtUtc > afterUtc ? (DateTime?)AtUtc : null;
            if (Kind == Kind.Every)
                return NextEveryAfter(Every, EveryOriginUtc, afterUtc);
            return Cron.NextRunAfter(afterUtc, TimeZone);
        }
    }

    // A parsed five-field cron expression.
    public sealed class Cron
    {
        public bool[] Minutes;   // length 60, index = minute
        public bool[] Hours;     // length 24
        public bool[] Dom;       // length 31, index = day of month (1..31)
        public bool[] Months;    // length 13, index = month (1..12)
        public bool[] Dow;       // length 7, index = 0..6 (0 = Sunday)
        public bool DomIsStar;   // original DOM field was exactly "*"
        public bool DowIsStar;   // original DOW field was exactly "*"
        public string Source;

        public DateTime? NextRunAfter(DateTime afterUtc, TimeZoneInfo tz)
        {
            DateTime local = TimeZoneInfo.ConvertTime(afterUtc.ToUniversalTime(), tz);
            // Start strictly after: truncate to the minute, then step one minute.
            DateTime t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0)
                         .AddMinutes(1);

            int guard = 0;
            while (guard++ < 50000)
            {
                if (!Months[t.Month])
                {
                    t = new DateTime(t.Year, t.Month, 1, 0, 0, 0).AddMonths(1);
                    continue;
                }
                if (!DayMatches(t))
                {
                    t = new DateTime(t.Year, t.Month, t.Day, 0, 0, 0).AddDays(1);
                    continue;
                }
                if (!Hours[t.Hour])
                {
                    t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1);
                    continue;
                }
                if (!Minutes[t.Minute])
                {
                    t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).AddMinutes(1);
                    continue;
                }
                return WallClockToUtc(t, tz);
            }
            throw new FormatException("No matching time within the search window for cron '" + Source + "'.");
        }

        private bool DayMatches(DateTime t)
        {
            bool domOk = Dom[t.Day];
            bool dowOk = Dow[(int)t.DayOfWeek]; // DayOfWeek: Sunday = 0
            if (DomIsStar && DowIsStar) return true;
            if (DomIsStar) return dowOk;
            if (DowIsStar) return domOk;
            return domOk || dowOk; // Vixie OR rule when both are restricted
        }
    }

    private static readonly string[] MonthNames =
    {
        "", "JAN", "FEB", "MAR", "APR", "MAY", "JUN",
        "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"
    };

    private static readonly string[] DayNames =
    {
        "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"
    };

    private static Dictionary<string, int> BuildNameMap(string[] names)
    {
        Dictionary<string, int> map = new Dictionary<string, int>();
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Length == 0)
                continue;
            map[names[i]] = i;
        }
        return map;
    }

    private static Dictionary<string, int> _monthMap = BuildNameMap(MonthNames);
    private static Dictionary<string, int> _dayMap = BuildNameMap(DayNames);

    // Parse a schedule string. nowUtc resolves relative "at" offsets and anchors
    // "every" intervals at the creation instant.
    public static Parsed Parse(string input, DateTime nowUtc)
    {
        if (input == null)
            throw new FormatException("Empty schedule.");
        string s = input.Trim();
        if (s.Length == 0)
            throw new FormatException("Empty schedule.");

        string lower = s.ToLowerInvariant();
        if (lower.StartsWith("at ", StringComparison.Ordinal))
            return ParseAt(s.Substring(3).Trim(), nowUtc, s);
        if (lower.StartsWith("every ", StringComparison.Ordinal))
            return ParseEvery(s.Substring(6).Trim(), nowUtc, s);
        if (lower.StartsWith("cron ", StringComparison.Ordinal))
            return ParseCron(s.Substring(5).Trim(), s);

        if (lower.StartsWith("@", StringComparison.Ordinal))
            return ParseCron(s, s);

        string[] tokens = SplitFields(s);
        if (tokens.Length == 5)
            return ParseCronFields(new string[] { tokens[0], tokens[1], tokens[2], tokens[3], tokens[4] }, null, s);
        if (tokens.Length == 6)
            return ParseCronFields(new string[] { tokens[0], tokens[1], tokens[2], tokens[3], tokens[4] }, tokens[5], s);

        throw new FormatException(
            "Unrecognized schedule '" + s + "'. Use 'at <time|offset>', 'every <interval>' or 'cron <5-field [tz]>'.");
    }

    private static Parsed ParseAt(string body, DateTime nowUtc, string source)
    {
        Parsed p = new Parsed { Kind = Kind.At, Source = source };

        // Relative offset: a single number followed by s/m/h/d.
        Match m = Regex.Match(body, @"^\s*(\d+)\s*([smhdSMHD])\s*$");
        if (m.Success)
        {
            int n;
            if (!int.TryParse(m.Groups[1].Value, out n) || n < 1)
                throw new FormatException("Invalid 'at' offset: " + body);
            string unit = m.Groups[2].Value.ToLowerInvariant();
            TimeSpan offset;
            if (unit == "s") offset = TimeSpan.FromSeconds(n);
            else if (unit == "m") offset = TimeSpan.FromMinutes(n);
            else if (unit == "h") offset = TimeSpan.FromHours(n);
            else offset = TimeSpan.FromDays(n);
            p.AtUtc = nowUtc.ToUniversalTime().Add(offset);
            return p;
        }

        // Absolute time. A trailing 'Z' marks UTC; anything else is host-local.
        DateTime atUtc;
        if (body.EndsWith("Z", StringComparison.Ordinal))
        {
            string num = body.Substring(0, body.Length - 1);
            DateTime dt = DateTime.ParseExact(num, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
            atUtc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }
        else
        {
            DateTime dt;
            if (!DateTime.TryParse(body, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                throw new FormatException("Invalid 'at' time: " + body);
            atUtc = dt.ToUniversalTime();
        }
        p.AtUtc = atUtc;
        return p;
    }

    private static Parsed ParseEvery(string body, DateTime nowUtc, string source)
    {
        Parsed p = new Parsed { Kind = Kind.Every, Source = source, EveryOriginUtc = nowUtc.ToUniversalTime() };

        // Allow a single number+unit or a simple "Nh Nm" style; keep it to one unit.
        Match m = Regex.Match(body, @"^\s*(\d+)\s*([smhdSMHD])\s*$");
        if (!m.Success)
            throw new FormatException("Invalid 'every' interval: " + body);
        int n;
        if (!int.TryParse(m.Groups[1].Value, out n) || n < 1)
            throw new FormatException("Invalid 'every' interval: " + body);
        string unit = m.Groups[2].Value.ToLowerInvariant();
        if (unit == "s") p.Every = TimeSpan.FromSeconds(n);
        else if (unit == "m") p.Every = TimeSpan.FromMinutes(n);
        else if (unit == "h") p.Every = TimeSpan.FromHours(n);
        else p.Every = TimeSpan.FromDays(n);
        return p;
    }

    private static Parsed ParseCron(string body, string source)
    {
        string[] tokens = SplitFields(body);
        string tzText = null;
        string[] fields;

        if (tokens.Length >= 1 && tokens[0].StartsWith("@", StringComparison.Ordinal))
        {
            // Alias form: "@hourly" or "@hourly UTC".
            fields = ExpandAlias(tokens[0]);
            if (tokens.Length == 2)
                tzText = tokens[1];
            else if (tokens.Length != 1)
                throw new FormatException("Invalid cron expression: " + body);
        }
        else if (tokens.Length == 5)
        {
            fields = new string[] { tokens[0], tokens[1], tokens[2], tokens[3], tokens[4] };
        }
        else if (tokens.Length == 6)
        {
            fields = new string[] { tokens[0], tokens[1], tokens[2], tokens[3], tokens[4] };
            tzText = tokens[5];
        }
        else
        {
            throw new FormatException("Invalid cron expression: " + body);
        }

        return ParseCronFields(fields, tzText, source);
    }

    private static Parsed ParseCronFields(string[] fields, string tzText, string source)
    {
        if (fields == null || fields.Length != 5)
            throw new FormatException("Cron requires 5 fields: " + source);

        bool[] minutes = ParseField(fields[0], 0, 59, null);
        bool[] hours = ParseField(fields[1], 0, 23, null);
        bool[] dom = ParseField(fields[2], 1, 31, null);
        bool[] months = ParseField(fields[3], 1, 12, _monthMap);
        // DOW accepts 0..7 (both 0 and 7 mean Sunday); normalize 7 -> 0.
        bool[] dowRaw = ParseField(fields[4], 0, 7, _dayMap);
        bool[] dow = new bool[7];
        for (int v = 0; v <= 6; v++)
            dow[v] = dowRaw[v] || (v == 0 && dowRaw[7]);

        TimeZoneInfo tz = ResolveTimeZone(tzText);

        Parsed p = new Parsed
        {
            Kind = Kind.Cron,
            Source = source,
            TimeZone = tz,
            Cron = new Cron
            {
                Minutes = minutes,
                Hours = hours,
                Dom = dom,
                Months = months,
                Dow = dow,
                DomIsStar = fields[2].Trim() == "*",
                DowIsStar = fields[4].Trim() == "*",
                Source = fields[0] + " " + fields[1] + " " + fields[2] + " " + fields[3] + " " + fields[4]
            }
        };
        return p;
    }

    private static string[] ExpandAlias(string alias)
    {
        switch (alias.ToLowerInvariant())
        {
            case "@midnight": return new string[] { "0", "0", "*", "*", "*" };
            case "@hourly": return new string[] { "0", "*", "*", "*", "*" };
            case "@daily":
            case "@daily ": return new string[] { "0", "0", "*", "*", "*" };
            case "@weekly": return new string[] { "0", "0", "*", "*", "0" };
            case "@monthly": return new string[] { "0", "0", "1", "*", "*" };
            case "@yearly":
            case "@annually": return new string[] { "0", "0", "1", "1", "*" };
            default:
                throw new FormatException("Unknown cron alias: " + alias);
        }
    }

    // One cron field -> allowed-values bit vector over [min..max].
    private static bool[] ParseField(string raw, int min, int max, Dictionary<string, int> names)
    {
        bool[] allowed = new bool[max + 1];
        string field = raw.Trim();
        if (field.Length == 0)
            throw new FormatException("Empty cron field.");

        foreach (string part in field.Split(','))
        {
            string item = part.Trim();
            if (item.Length == 0)
                throw new FormatException("Empty item in cron field: " + raw);

            int step = 1;
            string basePart = item;
            int slash = item.IndexOf('/');
            if (slash >= 0)
            {
                basePart = item.Substring(0, slash);
                string stepText = item.Substring(slash + 1).Trim();
                int stepValue;
                if (!int.TryParse(stepText, out stepValue) || stepValue < 1)
                    throw new FormatException("Bad step in cron field: " + item);
                step = stepValue;
            }

            int start;
            int end;
            if (basePart == "*")
            {
                start = min;
                end = max;
            }
            else
            {
                int dash = basePart.IndexOf('-');
                if (dash >= 0)
                {
                    start = ParseValue(basePart.Substring(0, dash), min, max, names);
                    end = ParseValue(basePart.Substring(dash + 1), min, max, names);
                    if (start > end)
                        throw new FormatException("Inverted range in cron field: " + basePart);
                }
                else
                {
                    start = ParseValue(basePart, min, max, names);
                    // "n/step" expands to n..max stepping by step; a bare "n" is just
                    // that single value.
                    end = slash >= 0 ? max : start;
                }
            }

            for (int v = start; v <= end; v += step)
                allowed[v] = true;
        }
        return allowed;
    }

    private static int ParseValue(string token, int min, int max, Dictionary<string, int> names)
    {
        token = token.Trim();
        int num;
        if (int.TryParse(token, out num))
        {
            if (num < min || num > max)
                throw new FormatException("Value " + num + " out of range " + min + "-" + max + ".");
            return num;
        }
        string upper = token.ToUpperInvariant();
        if (names != null && names.ContainsKey(upper))
        {
            int value = names[upper];
            if (value < min || value > max)
                throw new FormatException("Name " + upper + " out of range " + min + "-" + max + ".");
            return value;
        }
        throw new FormatException("Bad cron value: " + token);
    }

    private static string[] SplitFields(string s)
    {
        return s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Resolve an optional time-zone id. Empty means the host's local zone.
    // Accepts "UTC"/"GMT", Windows zone ids, and a small set of common IANA ids.
    private static TimeZoneInfo ResolveTimeZone(string tzText)
    {
        if (string.IsNullOrEmpty(tzText))
            return TimeZoneInfo.Local;

        string id = tzText.Trim();
        if (id.IndexOf("=") >= 0) // tolerate "tz=Name"
            id = id.Substring(id.IndexOf("=") + 1).Trim();

        if (id.ToUpperInvariant() == "UTC" || id.ToUpperInvariant() == "GMT")
            return TimeZoneInfo.Utc;

        // A few common IANA ids mapped to their Windows zone ids (the set that
        // .NET Framework 4.0's FindSystemTimeZoneById actually resolves).
        string windowsId = id;
        switch (id)
        {
            case "Europe/Moscow": windowsId = "Russian Standard Time"; break;
            case "Asia/Novosibirsk": windowsId = "GTB Standard Time"; break;
            case "America/New_York": windowsId = "Eastern Standard Time"; break;
            case "America/Chicago": windowsId = "Central Standard Time"; break;
            case "America/Denver": windowsId = "Mountain Standard Time"; break;
            case "America/Los_Angeles": windowsId = "Pacific Standard Time"; break;
            case "Europe/London": windowsId = "GMT Standard Time"; break;
            case "Asia/Tokyo": windowsId = "Tokyo Standard Time"; break;
            case "Australia/Sydney": windowsId = "Eastern Standard Time (Australia)"; break;
        }

        try
        {
            TimeZoneInfo resolved = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            if (resolved != null)
                return resolved;
        }
        catch
        {
        }
        throw new FormatException("Unknown time zone: " + id + " (use a Windows zone id, UTC, or a supported IANA name).");
    }

    // Convert a wall-clock instant that lives in a given time zone to UTC. The
    // (DateTime, TimeZoneInfo, DateTimeKind) ConvertTime overload is not available
    // on .NET Framework 4.0, so iterate on the zone's offset instead.
    private static DateTime WallClockToUtc(DateTime wallClockInTz, TimeZoneInfo tz)
    {
        DateTime utcGuess = DateTime.SpecifyKind(wallClockInTz, DateTimeKind.Utc);
        TimeSpan offset = tz.GetUtcOffset(utcGuess);
        DateTime utc = wallClockInTz - offset;
        TimeSpan offset2 = tz.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        if (offset2 != offset)
            utc = wallClockInTz - offset2;
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    private static DateTime? NextEveryAfter(TimeSpan interval, DateTime originUtc, DateTime afterUtc)
    {
        if (interval.TotalMilliseconds <= 0)
            throw new FormatException("Invalid interval.");
        long intervalMs = (long)interval.TotalMilliseconds;
        long deltaMs = (long)(afterUtc.ToUniversalTime().Ticks - originUtc.ToUniversalTime().Ticks) / 10000L;
        long periods = deltaMs / intervalMs + 1;
        if (periods < 1)
            periods = 1;
        return originUtc.ToUniversalTime().AddMilliseconds(periods * intervalMs);
    }

    private static string DescribeInterval(TimeSpan ts)
    {
        if (ts.TotalDays % 1 == 0)
            return (int)ts.TotalDays + "d";
        if (ts.TotalHours % 1 == 0)
            return (int)ts.TotalHours + "h";
        if (ts.TotalMinutes % 1 == 0)
            return (int)ts.TotalMinutes + "m";
        return (int)ts.TotalSeconds + "s";
    }
}
