using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace SimpleLLMChatGUI.Tests
{
    /// <summary>
    /// Drives SimpleLLMChatGUI.exe via UI Automation.
    /// </summary>
    public sealed class GuiDriver : IDisposable
    {
        private Process _gui;
        private AutomationElement _window;
        private readonly string _workDir;

        public Process GuiProcess { get { return _gui; } }
        public string WorkDir { get { return _workDir; } }

        public GuiDriver(string workDir)
        {
            _workDir = workDir;
            string exe = Path.Combine(workDir, "SimpleLLMChatGUI.exe");
            if (!File.Exists(exe))
                throw new TestFailureException("GUI EXE missing: " + exe);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
            _gui = Process.Start(psi);
            if (_gui == null)
                throw new TestFailureException("Failed to start GUI");

            _window = WaitForWindow(_gui, 30000);
            if (_window == null)
                throw new TestFailureException("GUI window never appeared");
        }

        public void WaitUntilReady(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement send = FindById("sendButton");
                if (send != null && send.Current.IsEnabled)
                    return;
                Thread.Sleep(100);
            }
            throw new TestFailureException("sendButton never became enabled (CLI not ready?)");
        }

        public void SendTurn(string text, int timeoutMs)
        {
            StartTurn(text, timeoutMs);
            WaitUntilIdle(timeoutMs);
        }

        /// <summary>
        /// Fills chatInput and clicks the Send button; returns once the button
        /// shows "Stop" (generation active). The button stays enabled while
        /// generating so the user can interrupt.
        /// </summary>
        public void StartTurn(string text, int timeoutMs)
        {
            AutomationElement input = FindById("chatInput");
            AutomationElement send = FindById("sendButton");
            if (input == null || send == null)
                throw new TestFailureException("chatInput/sendButton not found");

            input.SetFocus();
            ValuePattern value = input.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
            if (value != null)
                value.SetValue(text ?? "");
            else
                throw new TestFailureException("chatInput does not support ValuePattern");

            InvokePattern invoke = send.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke == null)
                throw new TestFailureException("sendButton does not support InvokePattern");
            invoke.Invoke();

            // Wait until the button switches from Send to Stop (generation started).
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement current = FindById("sendButton");
                if (current != null && current.Current.Name == "Stop")
                    return;
                Thread.Sleep(50);
            }
            throw new TestFailureException(
                "sendButton never entered Stop state within " + timeoutMs + " ms");
        }

        /// <summary>
        /// Waits until the button returns from "Stop" to "Send" (generation complete).
        /// Fast replies may complete before the first poll.
        /// </summary>
        public void WaitUntilIdle(int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool sawStop = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement send = FindById("sendButton");
                if (send == null)
                {
                    Thread.Sleep(50);
                    continue;
                }
                string name = send.Current.Name ?? "";
                if (name == "Stop")
                {
                    sawStop = true;
                }
                else if (sawStop)
                {
                    return;
                }
                else if (sw.ElapsedMilliseconds > 1500)
                {
                    // Never observed Stop: very fast reply, already back to Send.
                    return;
                }
                Thread.Sleep(50);
            }
            throw new TestFailureException(
                "Button did not return to Send within " + timeoutMs + " ms");
        }

        /// <summary>Current sendButton caption ("Send", "Stop"), or null if not found.</summary>
        public string SendButtonName()
        {
            AutomationElement send = FindById("sendButton");
            return send == null ? null : send.Current.Name;
        }

        /// <summary>Clicks the send/stop button (whatever its current mode).</summary>
        public void ClickSend()
        {
            AutomationElement send = FindById("sendButton");
            if (send == null)
                throw new TestFailureException("sendButton not found");

            InvokePattern invoke = send.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke == null)
                throw new TestFailureException("sendButton does not support InvokePattern");
            invoke.Invoke();
        }

        public Process FindCliProcess()
        {
            foreach (Process p in Process.GetProcessesByName("SimpleLLMChatCLI"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (path != null &&
                        path.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                catch
                {
                    // access denied / exited
                }
            }
            return null;
        }

        private AutomationElement FindById(string automationId)
        {
            if (_window == null) return null;
            Condition cond = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            return _window.FindFirst(TreeScope.Descendants, cond);
        }

        private static AutomationElement WaitForWindow(Process process, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                process.Refresh();
                if (process.HasExited)
                    throw new TestFailureException("GUI exited early with code " + process.ExitCode);
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    AutomationElement el = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (el != null)
                        return el;
                }
                Thread.Sleep(100);
            }
            return null;
        }

        public void Dispose()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("SimpleLLMChatCLI"))
                {
                    try
                    {
                        string path = null;
                        try { path = p.MainModule.FileName; } catch { }
                        if (path != null && path.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                            p.Kill();
                    }
                    catch { }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }

            if (_gui != null)
            {
                try
                {
                    if (!_gui.HasExited)
                        _gui.Kill();
                }
                catch { }
                try { _gui.Dispose(); } catch { }
                _gui = null;
            }
        }
    }
}
