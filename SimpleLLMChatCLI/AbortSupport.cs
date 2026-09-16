using System.Net;

namespace SimpleLLMChatCLI
{
    /// <summary>
    /// Process-wide generation-abort flag, raised by the CLI's control pipe when
    /// the GUI requests a stop. Aborts the in-flight HTTP request so the model
    /// stops mid-stream; the flag stays set until the next user turn, which also
    /// short-circuits any following tool round without a new request to the server.
    /// </summary>
    internal static class AbortSupport
    {
        public static volatile bool AbortRequested;

        private static volatile HttpWebRequest currentRequest;

        public static void RegisterRequest(HttpWebRequest request)
        {
            currentRequest = request;
        }

        public static void UnregisterRequest(HttpWebRequest request)
        {
            if (ReferenceEquals(currentRequest, request))
                currentRequest = null;
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

        /// <summary>Clears the abort state at the start of a new user turn.</summary>
        public static void Reset()
        {
            AbortRequested = false;
            currentRequest = null;
        }
    }
}