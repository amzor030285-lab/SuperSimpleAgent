using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

/// <summary>
/// Decision of the deterministic safety guard for one tool call.
/// </summary>
public enum GuardVerdict
{
    Run,
    Block
}

/// <summary>
/// T1 = locally reversible (blocked, model may retry; 3 hits on the same rule stop the turn).
/// T2 = irreversible / RCE / exfiltration (first hit taints the turn, any further block stops it).
/// </summary>
public enum GuardTier
{
    T1,
    T2
}

public class GuardResult
{
    public GuardVerdict Verdict = GuardVerdict.Run;
    public GuardTier Tier = GuardTier.T1;
    public string RuleId = "";
    public string Reason = "";

    public bool IsBlock
    {
        get { return Verdict == GuardVerdict.Block; }
    }

    public static GuardResult Allow()
    {
        return new GuardResult();
    }

    internal static GuardResult Block(string ruleId, GuardTier tier, string reason)
    {
        GuardResult r = new GuardResult();
        r.Verdict = GuardVerdict.Block;
        r.Tier = tier;
        r.RuleId = ruleId ?? "";
        r.Reason = reason ?? "";
        return r;
    }
}

/// <summary>
/// Deterministic safety guard for unattended tool execution (auto/config modes).
/// Evaluates tool calls against the bundled guardrules.json before they reach
/// the tools; it never executes anything itself. Unknown commands are allowed
/// (blacklist philosophy) after the pattern and protected-path scans pass.
/// </summary>
public class CommandGuard
{
    private static readonly object CacheLock = new object();
    private static CommandGuard _cached;

    private readonly List<BannedRule> _banned;
    private readonly List<PatternRule> _patterns;
    private readonly Dictionary<string, GuardTier> _verbs;
    private readonly List<string> _protectedT1;
    private readonly List<string> _protectedT2;
    private readonly List<string> _profileRoots;
    private readonly string _currentProfile;
    private readonly Dictionary<string, ToolRule> _toolRules;

    public bool Loaded { get; private set; }

    private sealed class BannedRule
    {
        public string Id;
        public string Command;
        public string Sub;
        public string SubPrefix;
        public string Flag;
        public GuardTier Tier;
        public string Reason;
    }

    private sealed class PatternRule
    {
        public string Id;
        public Regex Regex;
        public GuardTier Tier;
        public string Reason;
    }

    private sealed class ToolRule
    {
        public string[] PathArgs = new string[0];
        public string UrlArg;
    }

    public CommandGuard(string rulesPath)
    {
        _banned = new List<BannedRule>();
        _patterns = new List<PatternRule>();
        _verbs = new Dictionary<string, GuardTier>(StringComparer.OrdinalIgnoreCase);
        _protectedT1 = new List<string>();
        _protectedT2 = new List<string>();
        _profileRoots = new List<string>();
        _toolRules = new Dictionary<string, ToolRule>(StringComparer.OrdinalIgnoreCase);

        try
        {
            _currentProfile = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            JObject root = JObject.Parse(File.ReadAllText(rulesPath));
            LoadBanned(root);
            LoadPatterns(root);
            LoadVerbs(root);
            LoadProtected(root);
            LoadToolRules(root);
            Loaded = true;
        }
        catch
        {
            Loaded = false;
        }
    }

    public static CommandGuard Load()
    {
        lock (CacheLock)
        {
            if (_cached == null)
            {
                _cached = new CommandGuard(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "guardrules.json"));
            }
            return _cached;
        }
    }

    private static GuardTier TierFromJson(string s)
    {
        return string.Equals(s, "t2", StringComparison.OrdinalIgnoreCase) ? GuardTier.T2 : GuardTier.T1;
    }

    private static List<string> ReadStringArray(JToken owner, string name)
    {
        List<string> result = new List<string>();
        JToken arr = owner[name];
        if (arr == null)
            return result;
        foreach (JToken t in arr)
        {
            string s = (string)t;
            if (!string.IsNullOrEmpty(s))
                result.Add(s);
        }
        return result;
    }

    private void LoadBanned(JObject root)
    {
        JToken arr = root["banned"];
        if (arr == null)
            return;
        foreach (JToken t in arr)
        {
            JObject o = (JObject)t;
            string id = (string)o["id"];
            string command = (string)o["command"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(command))
                continue;
            _banned.Add(new BannedRule
            {
                Id = id,
                Command = command.ToLowerInvariant(),
                Sub = ((string)o["sub"] ?? "").ToLowerInvariant(),
                SubPrefix = (string)o["subPrefix"],
                Flag = (string)o["flag"],
                Tier = TierFromJson((string)o["tier"]),
                Reason = (string)o["reason"]
            });
        }
    }

    private void LoadPatterns(JObject root)
    {
        JToken arr = root["denyPatterns"];
        if (arr == null)
            return;
        foreach (JToken t in arr)
        {
            JObject o = (JObject)t;
            string pattern = (string)o["pattern"];
            if (string.IsNullOrEmpty(pattern))
                continue;
            try
            {
                _patterns.Add(new PatternRule
                {
                    Id = (string)o["id"] ?? "pattern",
                    Regex = new Regex(pattern, RegexOptions.IgnoreCase),
                    Tier = TierFromJson((string)o["tier"]),
                    Reason = (string)o["reason"]
                });
            }
            catch (ArgumentException)
            {
                // An invalid pattern is skipped rather than disabling the guard.
            }
        }
    }

    private void LoadVerbs(JObject root)
    {
        JObject o = (JObject)root["verbs"];
        if (o == null)
            return;
        foreach (JProperty p in o.Properties())
            _verbs[p.Name.ToLowerInvariant()] = TierFromJson(p.Value.ToString());
    }

    private void LoadProtected(JObject root)
    {
        foreach (string s in ReadStringArray(root, "protectedPaths"))
            _protectedT1.Add(NormalizePath(s));
        foreach (string s in ReadStringArray(root, "protectedPathsHigh"))
            _protectedT2.Add(NormalizePath(s));
        foreach (string s in ReadStringArray(root, "profileRoots"))
            _profileRoots.Add(NormalizePath(s));
    }

    private void LoadToolRules(JObject root)
    {
        JObject o = (JObject)root["tools"];
        if (o == null)
            return;
        foreach (JProperty p in o.Properties())
        {
            JObject rule = (JObject)p.Value;
            List<string> pathArgs = ReadStringArray(rule, "pathArgs");
            _toolRules[p.Name.ToLowerInvariant()] = new ToolRule
            {
                PathArgs = pathArgs.ToArray(),
                UrlArg = (string)rule["urlArg"]
            };
        }
    }

    /// <summary>
    /// Evaluates one tool call. Unknown tools are allowed.
    /// </summary>
    public GuardResult Evaluate(string toolName, string argumentsJson)
    {
        if (string.IsNullOrEmpty(toolName))
            return GuardResult.Allow();

        ToolRule toolRule;
        if (_toolRules.TryGetValue(toolName, out toolRule))
            return EvaluateTool(toolName, toolRule, argumentsJson);

        if (string.Equals(toolName, "run_shell_command", StringComparison.OrdinalIgnoreCase))
        {
            JObject args = TryParseArgs(argumentsJson);
            string command = args != null ? (string)args["command"] : null;
            if (string.IsNullOrEmpty(command))
                return GuardResult.Allow();
            return EvaluateShell(command);
        }

        return GuardResult.Allow();
    }

    private GuardResult EvaluateTool(string toolName, ToolRule rule, string argumentsJson)
    {
        JObject args = TryParseArgs(argumentsJson);
        if (args == null)
            return GuardResult.Allow();

        if (!string.IsNullOrEmpty(rule.UrlArg))
        {
            string url = (string)args[rule.UrlArg];
            if (!string.IsNullOrEmpty(url)
                && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return GuardResult.Block(
                    "tool-" + toolName, GuardTier.T1,
                    toolName + ": only http:// and https:// URLs are allowed");
        }

        foreach (string argName in rule.PathArgs)
        {
            string path = (string)args[argName];
            GuardTier tier;
            if (!string.IsNullOrEmpty(path) && IsProtectedPath(path, out tier))
                return GuardResult.Block(
                    "tool-" + toolName, tier,
                    toolName + " touches a protected path");
        }
        return GuardResult.Allow();
    }

    private static JObject TryParseArgs(string argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson))
            return null;
        try
        {
            return JObject.Parse(argumentsJson);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates a full command line: cross-segment patterns first (pipes,
    /// exfiltration), then each && / || / ; segment separately. The worst
    /// block wins.
    /// </summary>
    public GuardResult EvaluateShell(string command)
    {
        if (string.IsNullOrEmpty(command))
            return GuardResult.Allow();

        foreach (PatternRule p in _patterns)
        {
            if (p.Regex.IsMatch(command))
                return GuardResult.Block(p.Id, p.Tier, p.Reason);
        }

        GuardResult worst = null;
        foreach (string segment in SplitSegments(command))
        {
            GuardResult r = EvaluateSegment(segment, 0);
            if (r != null && r.IsBlock)
            {
                if (worst == null || r.Tier > worst.Tier)
                    worst = r;
            }
        }
        return worst != null ? worst : GuardResult.Allow();
    }

    private List<string> SplitSegments(string command)
    {
        List<string> result = new List<string>();
        StringBuilder cur = new StringBuilder();
        bool inDq = false;
        bool inSq = false;
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '^' && i + 1 < command.Length)
            {
                cur.Append(command[i + 1]);
                i++;
                continue;
            }
            if (c == '"')
            {
                inDq = !inDq;
                cur.Append(c);
                continue;
            }
            if (c == '\'')
            {
                inSq = !inSq;
                cur.Append(c);
                continue;
            }
            if (!inDq && !inSq && (c == '&' || c == '|' || c == ';'))
            {
                string s = cur.ToString().Trim();
                if (s.Length > 0)
                    result.Add(s);
                cur = new StringBuilder();
                continue;
            }
            cur.Append(c);
        }
        string tail = cur.ToString().Trim();
        if (tail.Length > 0)
            result.Add(tail);
        return result;
    }

    private GuardResult EvaluateSegment(string segment, int depth)
    {
        List<string> tokens = Tokenize(segment);
        if (tokens.Count == 0)
            return null;

        int idx = 0;
        while (idx < tokens.Count && LooksLikeEnvAssignment(tokens[idx]))
            idx++;
        if (idx >= tokens.Count)
            return null;

        string baseCmd = BaseCommand(tokens[idx]);
        List<string> rest = new List<string>();
        for (int i = idx + 1; i < tokens.Count; i++)
            rest.Add(tokens[i]);

        // cmd /c "inner" and cmd /k "inner": evaluate the inner command.
        // Nesting past the analysis depth is an unanalyzable form.
        if (baseCmd == "cmd")
        {
            int pos = IndexOfToken(rest, "/c");
            if (pos < 0)
                pos = IndexOfToken(rest, "/k");
            if (pos >= 0 && pos + 1 < rest.Count)
            {
                if (depth >= 3)
                    return GuardResult.Block(
                        "cmd-nested-too-deep", GuardTier.T2,
                        "cmd nesting beyond the analysis depth");
                List<string> inner = new List<string>();
                for (int i = pos + 1; i < rest.Count; i++)
                    inner.Add(rest[i]);
                return EvaluateSegment(string.Join(" ", inner.ToArray()), depth + 1);
            }
        }

        foreach (BannedRule b in _banned)
        {
            if (baseCmd != b.Command)
                continue;
            if (!string.IsNullOrEmpty(b.Sub) && FirstPositionalAfter(rest, null) != b.Sub)
                continue;
            if (!string.IsNullOrEmpty(b.SubPrefix))
            {
                // The prefix is matched against the first positional after the sub
                // (e.g. the key in "reg delete HKLM\...").
                string prefixArg = FirstPositionalAfter(rest, b.Sub);
                if (prefixArg == null || !prefixArg.StartsWith(b.SubPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (!string.IsNullOrEmpty(b.Flag)
                && !rest.Exists(t => string.Equals(t, b.Flag, StringComparison.OrdinalIgnoreCase)))
                continue;
            return GuardResult.Block(b.Id, b.Tier, b.Reason);
        }

        GuardTier verbTier;
        bool hasVerb = _verbs.TryGetValue(baseCmd, out verbTier);

        // Path targets: for state-changing verbs every path-like token is checked;
        // for everything else only redirect targets.
        List<string> pathTargets = new List<string>();
        if (hasVerb)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (IsPathLike(tokens[i]))
                    pathTargets.Add(tokens[i]);
            }
        }
        else
        {
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                string op = tokens[i];
                if ((op == ">" || op == ">>" || op == "<") && IsPathLike(tokens[i + 1]))
                    pathTargets.Add(tokens[i + 1]);
            }
        }

        if (pathTargets.Count > 0)
        {
            GuardTier tier = hasVerb ? verbTier : GuardTier.T1;
            bool hit = false;
            foreach (string p in pathTargets)
            {
                GuardTier hitTier;
                if (IsProtectedPath(p, out hitTier))
                {
                    hit = true;
                    if (hitTier > tier)
                        tier = hitTier;
                }
            }
            if (hit)
                return GuardResult.Block("path:" + baseCmd, tier, "touches a protected path");
        }

        // Known read-only command, or an unknown command (blacklist philosophy).
        return null;
    }

    private static List<string> Tokenize(string segment)
    {
        List<string> tokens = new List<string>();
        StringBuilder cur = new StringBuilder();
        bool inDq = false;
        bool inSq = false;
        bool has = false;
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            if (c == '^' && i + 1 < segment.Length)
            {
                cur.Append(segment[i + 1]);
                has = true;
                i++;
                continue;
            }
            if (c == '"')
            {
                inDq = !inDq;
                continue;
            }
            if (c == '\'')
            {
                inSq = !inSq;
                continue;
            }
            if (!inDq && !inSq && char.IsWhiteSpace(c))
            {
                if (has)
                {
                    tokens.Add(cur.ToString());
                    cur = new StringBuilder();
                    has = false;
                }
                continue;
            }
            if (!inDq && !inSq && (c == '>' || c == '<'))
            {
                if (has)
                {
                    tokens.Add(cur.ToString());
                    cur = new StringBuilder();
                    has = false;
                }
                int j = i;
                while (j < segment.Length && segment[j] == c)
                    j++;
                tokens.Add(new string(c, j - i));
                i = j - 1;
                continue;
            }
            cur.Append(c);
            has = true;
        }
        if (has)
            tokens.Add(cur.ToString());
        return tokens;
    }

    private static string BaseCommand(string token)
    {
        string s = token;
        int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (slash >= 0)
            s = s.Substring(slash + 1);
        if (s.Length == 0)
            return "";
        string lower = s.ToLowerInvariant();
        string[] exts = { ".exe", ".com", ".bat", ".cmd", ".msc", ".vbs" };
        foreach (string ext in exts)
        {
            if (lower.EndsWith(ext))
            {
                s = s.Substring(0, s.Length - ext.Length);
                break;
            }
        }
        if (s.ToLowerInvariant() == "%comspec%")
            return "cmd";
        return s.ToLowerInvariant();
    }

    private static bool LooksLikeEnvAssignment(string token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0)
            return false;
        string name = token.Substring(0, eq);
        return name.IndexOf('\\') < 0 && name.IndexOf('/') < 0 && name.IndexOf(':') < 0;
    }

    private static string FirstPositionalAfter(List<string> tokens, string sub)
    {
        foreach (string t in tokens)
        {
            if (t.Length == 0 || t[0] == '/' || t[0] == '-')
                continue;
            if (!string.IsNullOrEmpty(sub) && t.ToLowerInvariant() == sub)
                continue;
            return t.ToLowerInvariant();
        }
        return null;
    }

    private static int IndexOfToken(List<string> tokens, string token)
    {
        string t = token.ToLowerInvariant();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].ToLowerInvariant() == t)
                return i;
        }
        return -1;
    }

    private static bool IsPathLike(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        if (token == ">" || token == ">>" || token == "<")
            return false;
        if (token.IndexOf('\\') >= 0 || token.IndexOf('/') >= 0)
            return true;
        if (token.Length >= 2 && char.IsLetter(token[0]) && token[1] == ':')
            return true;
        return false;
    }

    private bool IsProtectedPath(string path, out GuardTier tier)
    {
        tier = GuardTier.T1;
        if (string.IsNullOrEmpty(path))
            return false;
        string p = NormalizePath(path);
        foreach (string prot in _protectedT2)
        {
            if (StartsWithPath(p, prot))
            {
                tier = GuardTier.T2;
                return true;
            }
        }
        foreach (string prot in _protectedT1)
        {
            if (StartsWithPath(p, prot))
                return true;
        }
        foreach (string root in _profileRoots)
        {
            // Another user's profile is protected; the current one is not.
            // If the current profile is unknown, fail closed (protect it too).
            if (!string.IsNullOrEmpty(_currentProfile))
            {
                if (StartsWithPath(p, root) && !StartsWithPath(p, _currentProfile))
                {
                    tier = GuardTier.T2;
                    return true;
                }
            }
            else if (StartsWithPath(p, root))
            {
                tier = GuardTier.T2;
                return true;
            }
        }
        return false;
    }

    private static bool StartsWithPath(string path, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || path.Length < prefix.Length)
            return false;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Length == prefix.Length)
            return true;
        char next = path[prefix.Length];
        return next == '\\' || next == '/';
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        string p = path.Replace('/', '\\').Trim();
        // Strip the \\?\ and \??\ device-path prefix so that protected-path
        // matching still applies.
        if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            p = p.Substring(4);
        while (p.Length > 1 && p.EndsWith("\\"))
            p = p.Substring(0, p.Length - 1);
        return p;
    }
}
