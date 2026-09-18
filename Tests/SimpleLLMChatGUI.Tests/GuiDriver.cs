using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        /// <summary>
        /// Concatenates the text of every text-pattern element under the window
        /// (chat input and each turn's FlowDocument), plus the Name of Text controls
        /// without a text pattern (e.g. table cell TextBlocks hosted in BlockUIContainers).
        /// </summary>
        public string GetAllText()
        {
            var sb = new StringBuilder();
            if (_window == null)
                return sb.ToString();

            AutomationElementCollection elements = _window.FindAll(TreeScope.Descendants,
                Condition.TrueCondition);
            foreach (AutomationElement element in elements)
            {
                try
                {
                    object pattern;
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                    {
                        string text = (pattern as TextPattern).DocumentRange.GetText(int.MaxValue);
                        if (!string.IsNullOrEmpty(text))
                            sb.Append(text).Append("\n");
                    }
                    else if (element.Current.ControlType == ControlType.Edit)
                    {
                        // Hosted read-only TextBoxes (code blocks, table cells, list
                        // items) expose their text via ValuePattern.
                        object valuePattern;
                        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern))
                        {
                            string value = (valuePattern as ValuePattern).Current.Value;
                            if (!string.IsNullOrEmpty(value))
                                sb.Append(value).Append("\n");
                        }
                    }
                    else if (element.Current.ControlType == ControlType.Text)
                    {
                        string name = element.Current.Name;
                        if (!string.IsNullOrEmpty(name))
                            sb.Append(name).Append("\n");
                    }
                }
                catch
                {
                    // Element stopped exposing text; skip it.
                }
            }
            return sb.ToString();
        }

        /// <summary>Dumps every descendant's control type / name / text-pattern flag (diagnostics).</summary>
        public void DumpAutomationTree(string context)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("Automation tree (" + context + "):");
                AutomationElementCollection all = _window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                foreach (AutomationElement el in all)
                {
                    string ct = "";
                    string name = "";
                    string text = "?";
                    try { ct = el.Current.ControlType.ProgrammaticName; } catch { }
                    try
                    {
                        name = (el.Current.Name ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
                        if (name.Length > 80)
                            name = name.Substring(0, 80) + "...";
                    }
                    catch { }
                    object textPattern;
                    try { text = el.TryGetCurrentPattern(TextPattern.Pattern, out textPattern) ? "text" : "-"; } catch { }
                    sb.Append("\n  [" + ct + "] text=" + text + " name='" + name + "'");
                }
                TestLog.Detail(sb.ToString());
            }
            catch (Exception ex)
            {
                TestLog.Detail("DumpAutomationTree failed: " + ex.Message);
            }
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

        /// <summary>Invokes a button in the main window by automation id.</summary>
        public void ClickMainButton(string automationId)
        {
            ClickMainButton(automationId, 0);
        }

        /// <summary>Invokes the n-th button in the main window by automation id (document order).</summary>
        public void ClickMainButton(string automationId, int index)
        {
            if (_window == null)
                throw new TestFailureException("Main-window button not found: " + automationId);

            AutomationElementCollection els = _window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            if (index < 0 || index >= els.Count)
                throw new TestFailureException(
                    "Main-window button not found: " + automationId + " (index " + index + " of " + els.Count + ")");

            AutomationElement el = els[index];
            InvokePattern invoke = el.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke == null)
                throw new TestFailureException(automationId + " does not support InvokePattern");
            invoke.Invoke();
        }

        /// <summary>Clipboard APIs require an STA thread; the test host is MTA.</summary>
        private static void RunOnStaThread(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null)
                throw new TestFailureException("STA clipboard access failed: " + error.Message);
        }

        public void ClearClipboard()
        {
            RunOnStaThread(() => { System.Windows.Clipboard.SetText(string.Empty); });
        }

        /// <summary>Returns the current clipboard text (null if absent).</summary>
        private string ReadClipboardOnce()
        {
            string text = null;
            RunOnStaThread(() =>
            {
                if (System.Windows.Clipboard.ContainsText())
                    text = System.Windows.Clipboard.GetText();
            });
            return text;
        }

        /// <summary>
        /// Polls until the clipboard text contains the expected part (the GUI
        /// may apply the click asynchronously); returns the matching text.
        /// </summary>
        public string ReadClipboardContaining(string expected, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string last = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                last = ReadClipboardOnce();
                if (last != null && last.Contains(expected))
                    return last;
                Thread.Sleep(100);
            }
            return last ?? string.Empty;
        }

        /// <summary>
        /// Finds a (dialog) window by its title; the dialog may take a moment to open.
        /// Enumerates native top-level windows via EnumWindows and wraps the matching
        /// hwnd with <see cref="AutomationElement.FromHandle"/> — UIA's desktop tree
        /// can be blind to a brand-new WPF modal dialog. Falls back to a desktop-tree
        /// probe so exotic providers are still covered.
        /// </summary>
        public static AutomationElement FindWindowByTitle(string title, int timeoutMs)
        {
            string target = (title ?? "").Trim();
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr hwnd = FindWindowNative(target);
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        AutomationElement el = AutomationElement.FromHandle(hwnd);
                        if (el != null)
                            return el;
                    }
                    catch (Exception ex)
                    {
                        TestLog.Detail("FromHandle failed: " + ex.Message);
                    }
                }

                // Fallback probe of the desktop tree (covers non-hwnd providers).
                AutomationElement root = AutomationElement.RootElement;
                AutomationElement w = root.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.NameProperty, target));
                if (w != null)
                    return w;

                Thread.Sleep(100);
            }

            LogTopLevelWindows("timeout waiting for window: " + title);
            return null;
        }

        private static IntPtr FindWindowNative(string title)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lParam) =>
            {
                if (!IsWindowVisible(hwnd))
                    return true;

                StringBuilder sb = new StringBuilder(512);
                if (GetWindowText(hwnd, sb, sb.Capacity) > 0 &&
                    string.Equals(sb.ToString(), title, StringComparison.Ordinal))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>Logs every visible top-level window (name / class) for diagnostics.</summary>
        public static void LogTopLevelWindows(string context)
        {
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                AutomationElementCollection all = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                StringBuilder sb = new StringBuilder();
                sb.Append("Top-level windows (" + context + "): " + all.Count);
                foreach (AutomationElement el in all)
                {
                    string name = "";
                    string cls = "";
                    try { name = el.Current.Name ?? ""; } catch { }
                    try { cls = el.Current.ClassName ?? ""; } catch { }
                    sb.Append("\n  name='" + name + "' class=" + cls);
                }
                TestLog.Detail(sb.ToString());
            }
            catch (Exception ex)
            {
                TestLog.Detail("LogTopLevelWindows failed: " + ex.Message);
            }
        }

        /// <summary>Finds an element by automation id inside a titled (dialog) window.</summary>
        public AutomationElement FindByIdInWindow(string windowTitle, string automationId, int timeoutMs)
        {
            AutomationElement window = FindWindowByTitle(windowTitle, timeoutMs);
            if (window == null)
                throw new TestFailureException("Dialog window not found: " + windowTitle);
            return window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }

        public void SetTextBoxInWindow(string windowTitle, string automationId, string text, int timeoutMs)
        {
            AutomationElement el = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (el == null)
                throw new TestFailureException("Text input not found: " + automationId + " in " + windowTitle);
            ValuePattern value = el.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
            if (value == null)
                throw new TestFailureException(automationId + " does not support ValuePattern");
            value.SetValue(text ?? "");
        }

        public void ClickButtonInWindow(string windowTitle, string automationId, int timeoutMs)
        {
            AutomationElement el = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (el == null)
                throw new TestFailureException("Button not found: " + automationId + " in " + windowTitle);
            InvokePattern invoke = el.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke == null)
                throw new TestFailureException(automationId + " does not support InvokePattern");
            invoke.Invoke();
        }

        /// <summary>
        /// Selects the first list item whose text contains <paramref name="containsText"/>.
        /// </summary>
        public void SelectListItemInWindow(string windowTitle, string automationId, string containsText, int timeoutMs)
        {
            AutomationElement el = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (el == null)
                throw new TestFailureException("List not found: " + automationId + " in " + windowTitle);

            Condition selectable = new PropertyCondition(
                AutomationElement.IsSelectionItemPatternAvailableProperty, true);
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElementCollection items = el.FindAll(TreeScope.Descendants, selectable);
                foreach (AutomationElement item in items)
                {
                    string name = item.Current.Name ?? "";
                    if (name.IndexOf(containsText, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SelectionItemPattern sip = item.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                        if (sip != null)
                        {
                            sip.Select();
                            return;
                        }
                    }
                }
                Thread.Sleep(100);
            }
            throw new TestFailureException(
                "List item \"" + containsText + "\" not found in " + automationId + " in " + windowTitle);
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
