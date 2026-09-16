using System;
using System.Text;

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
                            Encoding enc = ToolHelper.ResolveOutputEncoding();
                            int exitCode;
                            string output = ToolHelper.ExecuteProcess("cmd.exe", "/s /c " + command, out exitCode, enc);
                            return new ToolResult(output, exitCode);
                        }

                    default:
                        return new ToolResult("error: unknown tool '" + toolName + "'.", 1);
                }
            });
        }
    }
}
