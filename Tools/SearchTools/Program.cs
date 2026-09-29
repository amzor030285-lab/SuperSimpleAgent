using System;
using System.Globalization;

namespace SearchTools
{
    internal class Program
    {
        static int Main(string[] args)
        {
            return ToolHelper.RunToolMain(args, (toolName, argumentsJson) =>
            {
                switch (toolName)
                {
                    case "everything_search":
                        {
                            string query = ToolHelper.GetRequiredArg(argumentsJson, "query");
                            int maxResults = IntArg(
                                ToolHelper.JsonExtractString(argumentsJson, "max_results"),
                                EverythingSearchTool.MaxResultsDefault);
                            int offset = IntArg(
                                ToolHelper.JsonExtractString(argumentsJson, "offset"),
                                0);
                            int port = ToolHelper.GetConfigInt("port", EverythingSearchTool.DefaultPort);
                            int timeout = ToolHelper.GetConfigInt(
                                "timeout", EverythingSearchTool.DefaultTimeoutSeconds);

                            int exitCode;
                            string output = EverythingSearchTool.Search(
                                query, maxResults, offset, port, timeout, out exitCode);
                            return new ToolResult(output, exitCode);
                        }

                    default:
                        return new ToolResult("error: unknown tool '" + toolName + "'.", 1);
                }
            });
        }

        static int IntArg(string raw, int defaultValue)
        {
            int parsed;
            if (!string.IsNullOrEmpty(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
            return defaultValue;
        }
    }
}
