using System;

public static partial class StatusPipe
{
    public const string RunningToolPrefix = "STATUS running_tool=";

    /// <summary>
    /// Parses "STATUS running_tool=&lt;name&gt;". An empty name means no tool is
    /// running; the tool name never contains spaces.
    /// </summary>
    public static bool TryParseRunningToolLine(string line, out string toolName)
    {
        toolName = null;
        string rest;
        if (!TryStripPrefix(line, RunningToolPrefix, out rest))
            return false;
        toolName = SanitizeToken(rest);
        return true;
    }
}
