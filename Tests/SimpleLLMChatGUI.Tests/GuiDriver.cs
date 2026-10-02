using System;
using System.Collections.Generic;
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

        // The two elements the waits below poll. Cached because the alternative
        // - FindFirst over Descendants every 50 ms - walks the whole window tree
        // up to 600 times per wait, and that cross-process traffic competes with
        // the very UI thread whose state the wait is for. The symptom was a
        // rotating handful of cases failing with "sendButton never entered Stop
        // state", worse under load, on cases with nothing in common.
        private readonly Dictionary<string, AutomationElement> _hot =
            new Dictionary<string, AutomationElement>(StringComparer.Ordinal);

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
            AutomationElement input = Hot("chatInput");
            AutomationElement send = Hot("sendButton");
            if (input == null || send == null)
                throw new TestFailureException("chatInput/sendButton not found");

            input.SetFocus();
            ValuePattern value = input.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
            if (value != null)
                value.SetValue(text ?? "");
            else
                throw new TestFailureException("chatInput does not support ValuePattern");

            // Confirm the text landed before clicking. SetValue is asynchronous
            // with respect to the app, and if the click wins that race the app is
            // asked to send an empty turn - the wait below would then blame the
            // app for a timeout the harness caused, and say nothing about the
            // real cause.
            string wanted = text ?? "";
            string landed = "";
            try { landed = value.Current.Value ?? ""; } catch { }
            if (!string.Equals(landed, wanted, StringComparison.Ordinal))
                throw new TestFailureException(
                    "chatInput did not take the text: set " + wanted.Length +
                    " chars, read back " + landed.Length);

            InvokePattern invoke = send.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            if (invoke == null)
                throw new TestFailureException("sendButton does not support InvokePattern");
            invoke.Invoke();

            // Wait until generation starts. The app switches the caption and
            // disables the input together in EnterGeneratingMode, before any
            // request is made, so neither can be delayed by a slow model.
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (IsGenerating())
                    return;
                Thread.Sleep(50);
            }
            throw new TestFailureException(
                "sendButton never entered Stop state within " + timeoutMs + " ms" + DescribeState());
        }

        /// <summary>
        /// Generation is on when the caption reads "Stop" or the input is disabled.
        /// Either alone is conclusive, so a late or stale name read cannot hide a
        /// turn that did start.
        /// </summary>
        private bool IsGenerating()
        {
            AutomationElement send = Hot("sendButton");
            if (send != null && NameOf(send) == "Stop")
                return true;

            AutomationElement input = Hot("chatInput");
            if (input != null && !IsEnabled(input))
                return true;

            return false;
        }

        /// <summary>
        /// What the app actually looked like when a wait gave up, so the next
        /// failure says which of the three possible causes it was.
        /// </summary>
        private string DescribeState()
        {
            string caption = "<unreadable>";
            string inputState = "<unreadable>";
            string guiState = "unknown";

            try
            {
                AutomationElement send = Hot("sendButton");
                caption = send == null ? "absent" : NameOf(send);
            }
            catch { }

            try
            {
                AutomationElement input = Hot("chatInput");
                inputState = input == null ? "absent" : (IsEnabled(input) ? "enabled" : "disabled");
            }
            catch { }

            try { guiState = _gui.HasExited ? "exited" : "alive"; } catch { }

            return "; observed: sendButton=" + caption + ", chatInput=" + inputState +
                   ", gui=" + guiState;
        }

        private static string NameOf(AutomationElement element)
        {
            return element.Current.Name ?? "";
        }

        private static bool IsEnabled(AutomationElement element)
        {
            return element.Current.IsEnabled;
        }

        /// <summary>
        /// A polled element, remembered between polls and re-found only if it has
        /// actually gone away. Reading a property is what proves it is still live.
        /// </summary>
        private AutomationElement Hot(string automationId)
        {
            AutomationElement cached;
            if (_hot.TryGetValue(automationId, out cached) && cached != null)
            {
                try
                {
                    NameOf(cached);
                    return cached;
                }
                catch (ElementNotAvailableException) { _hot.Remove(automationId); }
                catch (InvalidOperationException) { _hot.Remove(automationId); }
            }

            AutomationElement found = FindById(automationId);
            if (found != null)
                _hot[automationId] = found;
            return found;
        }

        /// <summary>
        /// Waits until the turn is over: the button is no longer a Stop button and
        /// the transcript has stopped growing.
        ///
        /// The caption alone is not enough, and neither is a grace period. The app
        /// flips the caption back to Send when generation completes, but the last
        /// of the text can still be arriving - which is exactly what these waits
        /// used to hand back, and the caller then asserted on a truncated reply.
        /// A fixed "assume it was a fast reply" timeout had the same flaw from the
        /// other side: during the gap between the click and the first token nothing
        /// is appended, so a stable transcript proves nothing either. Both signals
        /// together are what actually identify the end of a turn.
        /// </summary>
        public void WaitUntilIdle(int timeoutMs)
        {
            const int StableMs = 800;   // wider than the 300 ms inter-chunk gap the tests stream with
            const int PollMs = 100;

            Stopwatch sw = Stopwatch.StartNew();
            string previous = null;
            long stableSince = -1;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                AutomationElement send = Hot("sendButton");
                bool generating = send != null && NameOf(send) == "Stop";

                // Walking the tree while a reply streams is pure overhead: the
                // caption already answers the question.
                string now = generating ? null : SafeAllText();

                if (now != null && string.Equals(now, previous, StringComparison.Ordinal))
                {
                    if (stableSince < 0)
                        stableSince = sw.ElapsedMilliseconds;
                    else if (sw.ElapsedMilliseconds - stableSince >= StableMs)
                        return;
                }
                else
                {
                    previous = now;
                    stableSince = -1;
                }

                Thread.Sleep(PollMs);
            }

            throw new TestFailureException(
                "Button did not return to Send within " + timeoutMs + " ms" + DescribeState());
        }

        private string SafeAllText()
        {
            try { return GetAllText(); } catch { return null; }
        }

        /// <summary>Current sendButton caption ("Send", "Stop"), or null if not found.</summary>
        public string SendButtonName()
        {
            AutomationElement send = Hot("sendButton");
            return send == null ? null : NameOf(send);
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

        /// <summary>Accessible name (label) of the n-th button in the main window by automation id.</summary>
        public string MainButtonName(string automationId, int index = 0)
        {
            if (_window == null)
                throw new TestFailureException("Main-window button not found: " + automationId);

            AutomationElementCollection els = _window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            if (index < 0 || index >= els.Count)
                throw new TestFailureException(
                    "Main-window button not found: " + automationId + " (index " + index + " of " + els.Count + ")");

            return els[index].Current.Name;
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

        /// <summary>
        /// Finds an element by automation id inside a titled (dialog) window.
        /// Retries until <paramref name="timeoutMs"/>: a window that is still
        /// closing after an invoke does not answer descendant queries at once.
        /// </summary>
        public AutomationElement FindByIdInWindow(string windowTitle, string automationId, int timeoutMs)
        {
            Condition byId = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                AutomationElement window = FindWindowByTitle(windowTitle, timeoutMs);
                if (window == null)
                    throw new TestFailureException("Dialog window not found: " + windowTitle);

                AutomationElement el = window.FindFirst(TreeScope.Descendants, byId);
                if (el != null)
                    return el;
                if (sw.ElapsedMilliseconds >= timeoutMs)
                    return null;

                Thread.Sleep(100);
            }
        }

        /// <summary>Waits until a titled dialog is gone. Invoke is async, so effects land after it returns.</summary>
        public void WaitForDialogClosed(string windowTitle, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (FindWindowNative((windowTitle ?? "").Trim()) == IntPtr.Zero)
                    return;
                Thread.Sleep(100);
            }
            throw new TestFailureException("Dialog window still open: " + windowTitle);
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
        /// Reads a checkbox without touching it, so a test can assert on the state the
        /// app came up with rather than on a state it asked for.
        /// </summary>
        public bool GetCheckBoxStateInWindow(string windowTitle, string automationId, int timeoutMs)
        {
            AutomationElement el = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (el == null)
                throw new TestFailureException("Checkbox not found: " + automationId + " in " + windowTitle);
            TogglePattern toggle = el.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern;
            if (toggle == null)
                throw new TestFailureException(automationId + " does not support TogglePattern");
            return toggle.Current.ToggleState == ToggleState.On;
        }

        /// <summary>
        /// Sets a checkbox to <paramref name="state"/> and returns what it read back,
        /// so a test can assert on the result instead of on the click.
        /// </summary>
        public bool SetCheckBoxInWindow(string windowTitle, string automationId, bool state, int timeoutMs)
        {
            AutomationElement el = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (el == null)
                throw new TestFailureException("Checkbox not found: " + automationId + " in " + windowTitle);
            TogglePattern toggle = el.GetCurrentPattern(TogglePattern.Pattern) as TogglePattern;
            if (toggle == null)
                throw new TestFailureException(automationId + " does not support TogglePattern");

            if (toggle.Current.ToggleState != (state ? ToggleState.On : ToggleState.Off))
                toggle.Toggle();

            // Read back through a fresh query: out-of-process providers cache Current
            // properties, so reusing the pattern above can return a stale state.
            return GetCheckBoxStateInWindow(windowTitle, automationId, timeoutMs);
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

        /// <summary>
        /// Automation ids inside a titled window that start with
        /// <paramref name="prefix"/> (UIA has no "starts with" condition).
        /// </summary>
        public List<string> AutomationIdsInWindow(string windowTitle, string prefix, int timeoutMs)
        {
            AutomationElement window = FindWindowByTitle(windowTitle, timeoutMs);
            if (window == null)
                throw new TestFailureException("Dialog window not found: " + windowTitle);

            var ids = new List<string>();
            AutomationElementCollection all = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement el in all)
            {
                string id = "";
                try { id = el.Current.AutomationId ?? ""; } catch { }
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                    ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Opens a combo box in a titled window and picks an item by text.
        /// Fails if the dropdown does not expand or the item is not exposed:
        /// both are what a broken combo template looks like from outside.
        /// </summary>
        public void SelectComboItemInWindow(string windowTitle, string automationId, string itemText, int timeoutMs)
        {
            AutomationElement combo = FindByIdInWindow(windowTitle, automationId, timeoutMs);
            if (combo == null)
                throw new TestFailureException("ComboBox not found: " + automationId + " in " + windowTitle);

            ExpandCollapsePattern expand =
                combo.GetCurrentPattern(ExpandCollapsePattern.Pattern) as ExpandCollapsePattern;
            if (expand == null)
                throw new TestFailureException(automationId + " does not support ExpandCollapsePattern");
            expand.Expand();

            Stopwatch sw = Stopwatch.StartNew();
            bool wasExpanded = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                        wasExpanded = true;
                }
                catch { }

                AutomationElement item = FindSelectable(combo, itemText);
                if (item == null)
                {
                    AutomationElement popup = FindOpenPopup();
                    if (popup != null)
                        item = FindSelectable(popup, itemText);
                }
                if (item != null)
                {
                    SelectionItemPattern select = item.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                    if (select != null)
                    {
                        select.Select();
                        return;
                    }
                }
                Thread.Sleep(100);
            }

            LogTopLevelWindows("combo dropdown timeout: " + windowTitle);
            throw new TestFailureException(automationId + " item \"" + itemText + "\" not selectable"
                + (wasExpanded ? " (dropdown was expanded)" : " (dropdown never expanded)"));
        }

        private static AutomationElement FindSelectable(AutomationElement parent, string containsText)
        {
            try
            {
                Condition selectable = new PropertyCondition(
                    AutomationElement.IsSelectionItemPatternAvailableProperty, true);
                AutomationElementCollection items = parent.FindAll(TreeScope.Descendants, selectable);
                foreach (AutomationElement item in items)
                {
                    string name = "";
                    try { name = item.Current.Name ?? ""; } catch { }
                    if (name.IndexOf(containsText, StringComparison.OrdinalIgnoreCase) >= 0)
                        return item;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Top-level WPF popup window (combo dropdown, tooltip) if one is open.</summary>
        private static AutomationElement FindOpenPopup()
        {
            try
            {
                Condition popupClass = new PropertyCondition(AutomationElement.ClassNameProperty, "Popup");
                AutomationElementCollection popups =
                    AutomationElement.RootElement.FindAll(TreeScope.Children, popupClass);
                foreach (AutomationElement popup in popups)
                {
                    if (CountListItems(popup) > 0)
                        return popup;
                }
            }
            catch { }
            return null;
        }

        private static int CountListItems(AutomationElement parent)
        {
            try
            {
                Condition listItem = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.ListItem);
                return parent.FindAll(TreeScope.Descendants, listItem).Count;
            }
            catch
            {
                return 0;
            }
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
