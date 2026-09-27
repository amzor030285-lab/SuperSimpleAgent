using System;
using System.IO;
using System.Text;

namespace FileTools
{
    public static class GrepSearchTool
    {
        public static string Search(
            string pattern,
            string directoryPath,
            string filePattern,
            string caseInsensitive,
            int maxContentLength,
            out int exitCode)
        {
            exitCode = 0;

            try
            {
                string searchPath = string.IsNullOrEmpty(directoryPath)
                    ? Environment.CurrentDirectory
                    : Environment.ExpandEnvironmentVariables(directoryPath.Trim());

                if (!Directory.Exists(searchPath))
                {
                    exitCode = 1;
                    return "error: Directory not found: " + searchPath;
                }

                StringBuilder args = new StringBuilder();
                args.Append("-r ");

                if (!string.IsNullOrEmpty(caseInsensitive) &&
                    caseInsensitive.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    args.Append("-i ");
                }

                args.Append("-E ");

                if (!string.IsNullOrEmpty(filePattern) && filePattern.Trim().Length > 0)
                {
                    args.Append("--include=");
                    args.Append("\"" + filePattern.Trim() + "\" ");
                }

                foreach (string excludeDir in GrepExcludeLists.ExcludedDirectoryNames)
                    args.Append("--exclude-dir=").Append(excludeDir).Append(" ");
                foreach (string excludeGlob in GrepExcludeLists.ExcludedFileGlobs)
                    args.Append("--exclude=").Append(excludeGlob).Append(" ");

                // The pattern reaches grep through a file, never through the command
                // line. Windows encodes argv with the ANSI code page, so a non-ASCII
                // pattern (Cyrillic) arrives as mojibake bytes that cannot match the
                // UTF-8 file contents. grep -f reads the pattern as raw bytes instead.
                string patternFile = null;
                string output;
                try
                {
                    if (!string.IsNullOrEmpty(pattern))
                    {
                        patternFile = Path.GetTempFileName();
                        File.WriteAllText(patternFile, pattern, new UTF8Encoding(false));
                        args.Append("-f \"" + patternFile + "\" ");
                    }
                    else
                    {
                        args.Append("\"\" ");
                    }
                    args.Append("\"" + searchPath + "\"");

                    output = ToolHelper.ExecuteProcess("grep.exe", args.ToString(), out exitCode, combineErrorOutput: false);
                }
                finally
                {
                    if (patternFile != null)
                    {
                        try { File.Delete(patternFile); }
                        catch { }
                    }
                }

                // GNU grep: 0 = matches, 1 = no matches, >=2 = error
                if (exitCode == 1 || (exitCode == 0 && string.IsNullOrWhiteSpace(output)))
                {
                    exitCode = 0;
                    return "No matches found for pattern: " + pattern;
                }

                if (exitCode != 0)
                {
                    if (string.IsNullOrWhiteSpace(output))
                        return "error: grep exited with code " + exitCode;
                    return "error: grep exited with code " + exitCode + ":\n" + output;
                }

                if (maxContentLength > 0 && output.Length > maxContentLength)
                {
                    return output.Substring(0, maxContentLength) + "\n...[truncated]";
                }

                return output;
            }
            catch (Exception ex)
            {
                exitCode = 1;
                return "error: running grep.exe: " + ex.Message;
            }
        }
    }
}
