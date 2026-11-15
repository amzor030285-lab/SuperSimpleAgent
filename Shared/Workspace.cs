using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Workspace: the one project folder the agent operates in.
///
/// Resolution is driven by three LLMSettings.ini keys (case-insensitive):
/// - workspaceenabled (default 1). 0 turns the whole feature off: no
///   instructions are injected and no state folder is redirected, so the
///   app behaves exactly like it did before this file existed.
/// - workspacepath (default empty).
/// - workspacemaxkb (default 32, same budget the Codex CLI applies to
///   project_doc_max_bytes).
///
/// Two modes:
/// - workspacepath empty ("launch folder" mode): the workspace is the
///   directory the CLI process was started in (beside the EXE when the GUI
///   launches it). AGENTS.md / CLAUDE.md are read from there only; no
///   ancestor walk, and no .ssa folder is ever created. State folders stay
///   beside the EXE exactly as before.
/// - workspacepath set (explicit mode): the user picked the folder, so the
///   root is trusted. Instructions are read from the root, and the mutable
///   state folders (sessions, memories, skills, plans, knowledge, rag-index)
///   move to &lt;root&gt;\.ssa\... to keep the project directory clean.
///   LLMSettings.ini and guardrules.json stay beside the executable: the
///   guard rules must never be loadable from the workspace, or a workspace
///   file could weaken its own guard.
///
/// The assembled instructions block always goes at the END of the system
/// prompt: untrusted file content must not claim the authoritative first
/// position. A trust marker inside the block says where the text came from.
/// The hard safety boundary is unchanged in both modes: tool approval, plan
/// mode and guard rules gate every action, whatever the instructions file
/// says. Files loaded in launch-folder mode are marked trusted="no".
///
/// Instances: ConfigHandler is bound once per process (Workspace.Bind) in
/// the CLI and GUI entry points, so the state-folder helpers can be called
/// from static code (PlanMode, DocumentIndex) without threading config
/// through every call site.
/// </summary>
public static class Workspace
{
    public const string EnabledKey = "workspaceenabled";
    public const string PathKey = "workspacepath";
    public const string MaxKbKey = "workspacemaxkb";

    public const int DefaultMaxKb = 32;

    /// <summary>Holds every per-project state folder, keeping the workspace root tidy.</summary>
    public const string StateFolderName = ".ssa";

    public const string BeginTag = "<workspace-instructions";
    public const string EndTag = "</workspace-instructions>";
    public const string TruncationMarker = "[... truncated {0} bytes of {1} total ...]";

    public const string AgentsFileName = "AGENTS.md";
    public const string ClaudeFileName = "CLAUDE.md";

    private static ConfigHandler _config;

    private sealed class CachedFile
    {
        public long LastWriteTicksUtc;
        public string Text;
    }

    private static readonly Dictionary<string, CachedFile> FileCache =
        new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

    /// <summary>One instruction file that actually contributed content.</summary>
    public sealed class InstructionsFile
    {
        public string Path;
        public string Content;
        public long SizeBytes;
        public bool Truncated;
    }

    public sealed class State
    {
        public bool Enabled;
        public bool HasExplicitRoot;
        public string ExplicitRootPath;
        public string EffectiveRoot;
        public string StateRoot;
        public bool StateRedirected;
        public string Error;
        public int MaxBytes;
        public int TotalBytes;
        public int TruncatedBytes;
    }

    /// <summary>
    /// Hints the folder the CLI process was started in. Used as the
    /// instruction source when workspacepath is empty.
    /// </summary>
    public static string LaunchDirectory()
    {
        try
        {
            string current = Directory.GetCurrentDirectory();
            return string.IsNullOrEmpty(current) ? AppDomain.CurrentDomain.BaseDirectory : current;
        }
        catch
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    public static void Bind(ConfigHandler config)
    {
        _config = config;
    }

    private static bool IsEnabled()
    {
        if (_config == null)
            return false;
        return _config.GetConfigInt(EnabledKey, 1) != 0;
    }

    private static int GetMaxBytes()
    {
        if (_config == null)
            return DefaultMaxKb;
        int kb = _config.GetConfigInt(MaxKbKey, DefaultMaxKb);
        if (kb < 1)
            kb = DefaultMaxKb;
        return kb * 1024;
    }

    /// <summary>The configured workspace root, expanded, or null when not set.</summary>
    public static string ExplicitRoot()
    {
        if (_config == null)
            return null;
        string path = _config.GetConfigValue(PathKey);
        if (string.IsNullOrEmpty(path))
            return null;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch
        {
            return path.Trim();
        }
    }

    /// <summary>
    /// The redirected state root (workspace\.ssa), or null when the feature
    /// is off, no explicit root is configured, or the folder does not exist.
    /// Callers must fall back to their beside-the-EXE location when null.
    /// </summary>
    public static string StateRoot()
    {
        if (!IsEnabled())
            return null;
        string root = ExplicitRoot();
        if (string.IsNullOrEmpty(root))
            return null;
        try
        {
            if (!Directory.Exists(root))
                return null;
        }
        catch
        {
            return null;
        }
        return Path.Combine(root, StateFolderName);
    }

    /// <summary>
    /// Creates the workspace state root and every state subfolder that is still
    /// missing. Idempotent, and a no-op when the feature is off, no explicit
    /// workspace is configured, or the configured folder does not exist: launch-
    /// folder mode must never create a .ssa folder.
    /// </summary>
    public static bool EnsureStateFolders(out string error)
    {
        error = null;
        string stateRoot = StateRoot();
        if (string.IsNullOrEmpty(stateRoot))
            return true;

        try
        {
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(Path.Combine(stateRoot, "sessions"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "memories"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "skills"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "plans"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "knowledge"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "rag-index"));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Best-effort variant used at process startup: failures are surfaced later
    /// in Options -&gt; Workspace instead of being printed to a stderr stream the
    /// GUI turns into a chat turn.
    /// </summary>
    public static void EnsureStateFoldersBestEffort()
    {
        string error;
        EnsureStateFolders(out error);
    }

    /// <summary>
    /// Effective directory for one state folder ("sessions", "memories",
    /// "skills", "plans", "knowledge", "rag-index"). Null means "no redirect;
    /// keep using the beside-the-EXE folder".
    /// </summary>
    public static string GetStateDirectory(string name)
    {
        string stateRoot = StateRoot();
        if (string.IsNullOrEmpty(stateRoot))
            return null;
        return Path.Combine(stateRoot, name);
    }

    /// <summary>
    /// The sessions folder: workspace\.ssa\sessions when an explicit
    /// workspace owns the mutable state, otherwise sessions next to the
    /// executable. Used by the CLI and the GUI session dialogs.
    /// </summary>
    public static string SessionDirectory(string installBaseDir)
    {
        string redirected = GetStateDirectory("sessions");
        return string.IsNullOrEmpty(redirected)
            ? SessionStore.DefaultDirectory(installBaseDir)
            : redirected;
    }

    /// <summary>
    /// Tool-config overrides passed through the tool manifest options: when
    /// the workspace owns mutable state, memory and skill folders live under
    /// its .ssa root instead of the tool's own folder.
    /// </summary>
    public static string GetToolDirectoryOverride(string key)
    {
        string stateRoot = StateRoot();
        if (string.IsNullOrEmpty(stateRoot) || string.IsNullOrEmpty(key))
            return null;
        if (string.Equals(key, "memoriesDirectory", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(stateRoot, "memories");
        if (string.Equals(key, "skillsDirectory", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(stateRoot, "skills");
        return null;
    }

    /// <summary>
    /// Resolves the current workspace state. Public for status display (the
    /// GUI Options page and the CLI banner); GetInstructionsBlock and
    /// Describe use it internally.
    /// </summary>
    public static State Resolve()
    {
        State s = new State();
        s.Enabled = IsEnabled();
        s.HasExplicitRoot = ExplicitRoot() != null;
        s.ExplicitRootPath = ExplicitRoot();
        s.MaxBytes = GetMaxBytes();

        if (!s.Enabled)
        {
            // Everything behaves as before: no instructions, no redirect --
            // and importantly no .ssa folder is created anywhere.
            s.StateRoot = AppDomain.CurrentDomain.BaseDirectory;
            return s;
        }

        if (s.HasExplicitRoot)
        {
            try
            {
                if (Directory.Exists(s.ExplicitRootPath))
                {
                    s.EffectiveRoot = s.ExplicitRootPath;
                    s.StateRoot = Path.Combine(s.EffectiveRoot, StateFolderName);
                    s.StateRedirected = true;
                }
                else
                {
                    s.Error = "workspace folder not found: " + s.ExplicitRootPath;
                }
            }
            catch (Exception)
            {
                s.Error = "workspace folder not found: " + s.ExplicitRootPath;
            }
        }

        if (!s.HasExplicitRoot)
        {
            // Launch-folder mode: instructions are read from the folder the
            // CLI was started in; state stays beside the EXE (no redirect).
            s.EffectiveRoot = LaunchDirectory();
            s.StateRoot = AppDomain.CurrentDomain.BaseDirectory;
        }
        else if (s.EffectiveRoot == null)
        {
            // A configured but missing root is a stale path, not an invitation
            // to read the install folder instead: report the error, inject
            // nothing, and keep the state beside the EXE.
            s.StateRoot = AppDomain.CurrentDomain.BaseDirectory;
        }

        return s;
    }

    /// <summary>
    /// Builds the system prompt block from the workspace instructions, or
    /// null when the feature is off, the root has no instruction files, or
    /// nothing fits the size budget. Files are cached by path + mtime, so an
    /// edited AGENTS.md is picked up on the next request without a restart.
    /// </summary>
    public static string GetInstructionsBlock()
    {
        List<InstructionsFile> files;
        State s = LoadInstructions(out files);
        if (files == null || files.Count == 0)
            return null;

        bool trusted = s.HasExplicitRoot && s.StateRedirected;
        List<string> parts = new List<string>();
        foreach (InstructionsFile file in files)
        {
            StringBuilder part = new StringBuilder();
            part.Append(BeginTag);
            part.Append(" source=\"" + file.Path.Replace("\"", "'") + "\"");
            part.Append(" trusted=\"" + (trusted ? "yes" : "no") + "\">");
            if (!trusted)
            {
                part.Append("\r\nUntrusted: auto-discovered in the launch folder; the user did not pick this file.");
            }
            part.Append("\r\nWorkspace root: " + s.EffectiveRoot + " -- resolve relative paths mentioned"
                + " in these instructions against this folder; tools run from their own folders.");
            part.Append("\r\n\r\n");
            part.Append(file.Content);
            part.Append("\r\n" + EndTag);
            parts.Add(part.ToString());
        }

        if (s.TruncatedBytes > 0)
        {
            parts.Add(string.Format(
                TruncationMarker, s.TruncatedBytes, s.TotalBytes));
        }

        return string.Join("\n\n", parts.ToArray());
    }

    /// <summary>
    /// Resolves and loads the instruction files into one state object, so
    /// both the system prompt block and the user-facing description report
    /// the same numbers. Cheap: reads hit the mtime cache.
    /// </summary>
    private static State LoadInstructions(out List<InstructionsFile> files)
    {
        files = null;
        State s = Resolve();
        if (s.EffectiveRoot == null)
            return s;

        bool trusted = s.HasExplicitRoot && s.StateRedirected;
        files = new List<InstructionsFile>();
        List<string> candidates = new List<string>();
        candidates.Add(Path.Combine(s.EffectiveRoot, AgentsFileName));
        candidates.Add(Path.Combine(s.EffectiveRoot, ClaudeFileName));

        int remaining = s.MaxBytes;
        foreach (string path in candidates)
        {
            if (!File.Exists(path))
                continue;
            long fileBytes;
            try
            {
                fileBytes = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                continue;
            }
            if (remaining <= 0)
            {
                // No budget left: this file is cut entirely, but it must still
                // count in the totals, otherwise its rules vanish without a
                // trace (the marker would report only the first file).
                s.TotalBytes += (int)Math.Min((long)int.MaxValue, fileBytes);
                s.TruncatedBytes += (int)Math.Min((long)int.MaxValue, fileBytes);
                continue;
            }

            bool fromCache;
            string content = ReadFileCached(path, out fromCache);
            if (content == null)
                continue;

            InstructionsFile file = new InstructionsFile();
            file.Path = path;
            file.SizeBytes = fileBytes;
            s.TotalBytes += (int)Math.Min((long)int.MaxValue, fileBytes);

            if (fileBytes > remaining)
            {
                // The byte budget is approximated on the character count; the
                // leftover is reported honestly by the truncation marker.
                file.Content = EscapeTagBreakout(SliceByBytes(content, remaining));
                file.Truncated = true;
                s.TruncatedBytes += (int)Math.Max(0, fileBytes - remaining);
                remaining = 0;
            }
            else
            {
                remaining -= (int)fileBytes;
                file.Content = EscapeTagBreakout(content);
            }
            files.Add(file);
        }

        if (files.Count == 0)
            files = null;
        return s;
    }

    /// <summary>
    /// True when there is something worth announcing at startup: an explicit
    /// workspace root (even a broken one) or at least one instruction file.
    /// A stock installation with no AGENTS.md and no workspace keys stays as
    /// quiet as it was before this feature existed.
    /// </summary>
    public static bool IsActive()
    {
        State s = Resolve();
        if (!s.Enabled)
            return false;
        if (s.HasExplicitRoot)
            return true;
        List<InstructionsFile> files;
        LoadInstructions(out files);
        return files != null && files.Count > 0;
    }

    /// <summary>
    /// Multi-line, human-readable workspace summary for the GUI Options page
    /// and the CLI banner.
    /// </summary>
    public static string Describe()
    {
        if (!IsEnabled())
            return "Workspace: disabled";

        List<InstructionsFile> files;
        State s = LoadInstructions(out files);
        List<string> lines = new List<string>();

        if (s.HasExplicitRoot)
        {
            lines.Add("Workspace root: " + s.ExplicitRootPath);
            if (s.Error != null)
                lines.Add("Workspace error: " + s.Error);
        }
        else
        {
            lines.Add("Workspace: not set; using the launch folder " + s.EffectiveRoot);
        }

        bool loadedAny = false;
        if (files != null && files.Count > 0)
        {
            StringBuilder names = new StringBuilder();
            foreach (InstructionsFile f in files)
            {
                if (names.Length > 0)
                    names.Append(", ");
                names.Append(Path.GetFileName(f.Path) + " (" + FormatKb(f.SizeBytes) + ")");
                loadedAny = true;
            }
            lines.Add("Instructions: " + names);
        }
        if (!loadedAny && s.EffectiveRoot != null)
            lines.Add("Instructions: none found in " + s.EffectiveRoot);

        if (s.TruncatedBytes > 0)
            lines.Add(string.Format(TruncationMarker, s.TruncatedBytes, s.TotalBytes));

        if (s.StateRedirected)
            lines.Add("State folder: " + s.StateRoot + " (sessions, memories, skills, plans, knowledge, rag-index)");
        else
            lines.Add("State folders: beside the EXE (unchanged)");
        return string.Join(Environment.NewLine, lines.ToArray());
    }

    private static string FormatKb(long bytes)
    {
        return (bytes / 1024.0).ToString("0.0") + " KB";
    }

    /// <summary>Reads the file honoring the mtime cache; null when unreadable.</summary>
    private static string ReadFileCached(string path, out bool fromCache)
    {
        fromCache = false;
        CachedFile cached;
        if (FileCache.TryGetValue(path, out cached))
        {
            try
            {
                long ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (ticks == cached.LastWriteTicksUtc)
                {
                    fromCache = true;
                    return cached.Text;
                }
            }
            catch (Exception)
            {
                return cached.Text;
            }
        }

        string text;
        try
        {
            text = DecodeText(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }

        CachedFile entry = new CachedFile();
        entry.LastWriteTicksUtc = File.GetLastWriteTimeUtc(path).Ticks;
        entry.Text = text;
        FileCache[path] = entry;
        return text;
    }

    /// <summary>
    /// BOM-aware decode: UTF-8 BOM and UTF-16 LE BOM are honored, otherwise
    /// strict UTF-8 is tried and ANSI (the system default) is the fallback,
    /// so a Windows-1251 AGENTS.md does not turn into mojibake.
    /// </summary>
    public static string DecodeText(byte[] raw)
    {
        if (raw == null || raw.Length == 0)
            return string.Empty;

        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return new UTF8Encoding(false).GetString(raw, 3, raw.Length - 3);
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);

        try
        {
            return new UTF8Encoding(false, true).GetString(raw);   // throws on real ANSI
        }
        catch (Exception)
        {
            return Encoding.Default.GetString(raw);
        }
    }

    /// <summary>
    /// Keeps the workspace content from closing the block early: any literal
    /// end tag inside the file gets a space injected, so the model can still
    /// read it but the envelope stays intact.
    /// </summary>
    public static string EscapeTagBreakout(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        // Case-insensitive: a file may close the block with any casing, and the
        // envelope must not end early because of it.
        string replacement = EndTag.Insert(EndTag.Length - 1, " ");
        return Regex.Replace(text, Regex.Escape(EndTag), replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// UTF-8 byte-budgeted left slice of a string.
    /// Internal so the test suites can cover the edge cases directly.
    /// </summary>
    internal static string SliceByBytes(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || maxBytes <= 0)
            return string.Empty;
        Encoding utf8 = new UTF8Encoding(false);
        try
        {
            // One code point takes up to four UTF-8 bytes. When even the first one
            // does not fit, nothing fits at all: the shrink loop below would keep
            // clamping back to a single character and never end.
            int firstChars = IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1;
            if (utf8.GetByteCount(text.Substring(0, firstChars)) > maxBytes)
                return string.Empty;

            int chars = text.Length;
            while (chars > 0)
            {
                int used;
                try
                {
                    used = utf8.GetByteCount(text.Substring(0, chars));
                }
                catch (Exception)
                {
                    chars = 0;
                    break;
                }
                if (used <= maxBytes)
                    break;
                chars = (int)(chars * ((double)maxBytes / used) * 0.98);
                if (chars < 1)
                    chars = 1;
            }

            // Never end on half a surrogate pair: a lone surrogate is not text.
            if (chars > 0 && chars < text.Length && IsHighSurrogate(text[chars - 1]))
                chars--;

            return text.Substring(0, chars);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>True for the leading half of a UTF-16 surrogate pair.</summary>
    private static bool IsHighSurrogate(char c)
    {
        return c >= '\uD800' && c <= '\uDBFF';
    }
}
