using System;

namespace ShellTools
{
    internal class Program
    {
        static int Main(string[] args)
        {
            return ToolHelper.RunToolMain(args, (toolName, argumentsJson) =>
            {
                switch (toolName)
                {
                    case "run_shell_command":
                        {
                            string command = ToolHelper.GetRequiredArg(argumentsJson, "command");
                            // The per-call "encoding" argument wins over the configured
                            // outputEncoding option, which stays the default so existing
                            // setups keep working.
                            string requested = ToolHelper.GetOptionalArg(argumentsJson, "encoding");
                            if (string.IsNullOrEmpty(requested))
                                requested = ToolHelper.GetConfiguredOutputEncodingName();

                            int exitCode;
                            string output = ToolHelper.ExecuteProcessDecoded(
                                "cmd.exe", "/s /c " + command, requested, out exitCode);
                            return new ToolResult(output, exitCode);
                        }

                    default:
                        return new ToolResult("error: unknown tool '" + toolName + "'.", 1);
                }
            });
        }
    }
}
