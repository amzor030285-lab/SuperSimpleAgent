namespace SuperSimpleAgentCLI
{
    public static class ConsecutiveToolFailures
    {
        public static void Record(ref int streak, int limit, bool ran, int exitCode)
        {
            if (limit <= 0 || !ran)
                return;

            if (exitCode == 0)
                streak = 0;
            else
                streak++;
        }
    }
}
