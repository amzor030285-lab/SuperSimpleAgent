using System;
using System.Diagnostics;
using System.Net;

namespace SuperSimpleAgentCLI
{
    /// <summary>
    /// Process-wide abort flags, raised by the CLI's control pipe when the GUI
    /// requests a stop. AbortRequested aborts the in-flight HTTP request so the
    /// model stops mid-stream; the flag stays set until the next user turn, which
    /// also short-circuits any following tool round without a new request to the
    /// server. ToolAbortRequested is a one-shot flag: it is cleared when the
    /// running tool unwinds (UnregisterToolProcess) and at the start of each new
    /// turn (Reset). RegisterToolProcess must NOT clear it, because a STOPTOOL
    /// can legitimately arrive before the tool process is registered and would
    /// be lost otherwise.
    /// </summary>
    internal static class AbortSupport
    {
        public static volatile bool AbortRequested;
        public static volatile bool ToolAbortRequested;

        /// <summary>
        /// Set when the repetition guard aborted the request. Distinguishes a
        /// degenerate model loop from a user stop, so the reported reason is accurate.
        /// </summary>
        public static volatile bool RepetitionAbortRequested;

        private static volatile HttpWebRequest currentRequest;
        private static volatile Process currentToolProcess;
        private static volatile IntPtr currentToolJob;

        public static void RegisterRequest(HttpWebRequest request)
        {
            currentRequest = request;
        }

        public static void UnregisterRequest(HttpWebRequest request)
        {
            if (ReferenceEquals(currentRequest, request))
                currentRequest = null;
        }

        /// <summary>
        /// Registers the running tool process (and its job, when one was
        /// created) so RequestToolAbort can kill the whole process tree.
        /// An already-pending cancel is honored, not cleared.
        /// </summary>
        public static void RegisterToolProcess(Process process, IntPtr job)
        {
            currentToolProcess = process;
            currentToolJob = job;
        }

        /// <summary>Drops the registration and clears the one-shot cancel flag.</summary>
        public static void UnregisterToolProcess()
        {
            currentToolProcess = null;
            currentToolJob = IntPtr.Zero;
            ToolAbortRequested = false;
        }

        /// <summary>Requests cancellation of the running tool: one-shot flag plus kill of the process tree.</summary>
        public static void RequestToolAbort()
        {
            ToolAbortRequested = true;
            ProcessJob.KillJobTree(currentToolJob);
            Process process = currentToolProcess;
            if (process != null)
            {
                try { process.Kill(); }
                catch { }
            }
        }

        /// <summary>Requests an abort of the current generation. Thread-safe; called from the control-pipe thread.</summary>
        public static void RequestAbort()
        {
            AbortRequested = true;
            HttpWebRequest request = currentRequest;
            if (request != null)
            {
                try { request.Abort(); }
                catch { }
            }
        }

        /// <summary>
        /// Aborts the in-flight request because the response degenerated into a loop.
        /// AbortRequested is raised as well so the curl fallback transport kills its
        /// process too; RepetitionAbortRequested records the real cause.
        /// </summary>
        public static void RequestRepetitionAbort()
        {
            RepetitionAbortRequested = true;
            RequestAbort();
        }

        /// <summary>Clears the abort state at the start of a new user turn.</summary>
        public static void Reset()
        {
            AbortRequested = false;
            ToolAbortRequested = false;
            RepetitionAbortRequested = false;
            currentRequest = null;
            currentToolProcess = null;
            currentToolJob = IntPtr.Zero;
        }
    }
}
