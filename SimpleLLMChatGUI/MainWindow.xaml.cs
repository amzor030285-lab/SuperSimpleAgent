using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimpleLLMChatGUI
{
    public partial class MainWindow : Window
    {
        private ProcessHandler processHandler;
        private readonly ImageHandler imageHandler;
        private string _reasoningEffort = "";

        private string _approvalMode = AppConstants.DefaultApprovalMode;
        private TokenTracker tokenTracker;
        private ContextMenu reasoningEffortMenu;
        private bool _isGenerating;
        private DateTime _generationStartTime = DateTime.MinValue;
        private bool _startupPromptConsumed;
        private readonly ObservableCollection<ChatTurn> _chatTurns = new ObservableCollection<ChatTurn>();
        private ChatTurn _streamingTurn;
        private bool _compacting;
        private bool _summaryArrived;
        private string _compactTail = "";

        // False when the user has wheeled or dragged the chat view up to read
        // earlier text: autoscroll parks until they return to the bottom.
        private bool _autoScroll = true;
        private ScrollViewer _chatScrollViewer;

        /// <summary>
        /// Streamed stdout is buffered here and handed to the chat list on a timer
        /// instead of once per chunk. Appending to the turn document invalidates the
        /// whole FlowDocument layout, and the autoscroll measures it again, so one
        /// layout pass per streamed token makes a long turn cost O(n^2) and saturates
        /// the UI thread on a fast model. Batching at StreamFlushIntervalMs collapses
        /// hundreds of dispatcher operations per second into ~20.
        /// </summary>
        private const int StreamFlushIntervalMs = 50;
        private readonly StringBuilder _pendingStreamText = new StringBuilder();
        private DispatcherTimer _streamFlushTimer;

        private const double ChatTurnGapMin = 8;
        private const double ChatTurnGapMax = 24;

        // The CLI prints this line before the summary text on manual compaction;
        // seeing it lets the GUI keep the summary turn and clear the old history.
        private const string CompactedMarker = "[Conversation compacted]";
        private const int CompactedMarkerWindow = 64;

        /// <summary>
        /// Bottom gap between chat turns; scales with font size (~1em + 2), clamped.
        /// </summary>
        public static readonly DependencyProperty ChatTurnItemMarginProperty =
            DependencyProperty.Register(
                nameof(ChatTurnItemMargin),
                typeof(Thickness),
                typeof(MainWindow),
                new PropertyMetadata(new Thickness(0, 0, 0, AppConstants.DefaultChatFontSize + 2)));

        public Thickness ChatTurnItemMargin
        {
            get { return (Thickness)GetValue(ChatTurnItemMarginProperty); }
            set { SetValue(ChatTurnItemMarginProperty, value); }
        }

        private static readonly KeyValuePair<string, string>[] ReasoningEffortOptions =
        {
            new KeyValuePair<string, string>("Default", ""),
            new KeyValuePair<string, string>("None", "none"),
            new KeyValuePair<string, string>("Minimal", "minimal"),
            new KeyValuePair<string, string>("Low", "low"),
            new KeyValuePair<string, string>("Medium", "medium"),
            new KeyValuePair<string, string>("High", "high"),
            new KeyValuePair<string, string>("Extra High", "xhigh"),
        };

        public MainWindow()
        {
            InitializeComponent();

            // Software rendering (Tier 0) on weak GPUs: GDI-style text metrics
            // are markedly cheaper than the high-quality path and render
            // sharper on XP, so fall back to them for the whole window.
            if (RenderCapability.Tier >> 16 == 0)
                TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // Initialize image handler and centralize UI updates via events
            imageHandler = new ImageHandler();

            imageHandler.ImageSelected += () =>
            {
                attachButton.ToolTip = "Detach Image";
                attachButton.IsChecked = true;
                attachButton.Background = System.Windows.Media.Brushes.LightBlue;
            };

            imageHandler.ImageDetached += () =>
            {
                attachButton.ToolTip = "Attach Image";
                attachButton.IsChecked = false;
                attachButton.ClearValue(Button.BackgroundProperty);
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            chatList.SizeChanged += new SizeChangedEventHandler(chatList_SizeChanged);

            chatList.ItemsSource = _chatTurns;
            ChatTurn.ThinkingExpanderStyle = TryFindResource("ThinkingExpanderStyle") as Style;

            FontHandler.ApplyFontToWindow(this);
            FontHandler.ApplyCodeBlockFontFamily();
            LoadAndApplyFontSize();
            LoadAndApplyColors();
            UpdateReasoningEffortLabel();
            string configuredMode = App.Config == null ? null : App.Config.GetConfigValue("approvalmode");
            string normalizedApprovalMode = ToolApproval.NormalizeMode(configuredMode);
            if (normalizedApprovalMode != null)
                _approvalMode = normalizedApprovalMode;
            UpdateApprovalModeButton();
            BuildReasoningEffortMenu();
            tokenTracker = new TokenTracker(tokenStatusText);

            // Gate send until the CLI is up; leave input/attach usable meanwhile.
            sendButton.IsEnabled = false;

            // After first paint: Process.Start can take 100–1000ms and would white-out Loaded.
            EventHandler onRendered = null;
            onRendered = (s, args) =>
            {
                ContentRendered -= onRendered;
                OnboardingWizardWindow.ShowIfNeeded(this);
                StartLLMProcess();
                AutoRestoreLastSession();
            };
            ContentRendered += onRendered;
        }

        private void UpdateApprovalModeButton()
        {
            approvalModeButton.Content = _approvalMode;
        }

        private void approvalModeButton_Click(object sender, RoutedEventArgs e)
        {
            string nextMode = string.Equals(_approvalMode, ToolApproval.ModeAsk, StringComparison.OrdinalIgnoreCase)
                ? ToolApproval.ModeAuto
                : string.Equals(_approvalMode, ToolApproval.ModeAuto, StringComparison.OrdinalIgnoreCase)
                    ? ToolApproval.ModeConfig
                    : ToolApproval.ModeAsk;
            SetApprovalMode(nextMode);
        }

        private void SetApprovalMode(string mode)
        {
            string normalizedMode = ToolApproval.NormalizeMode(mode);
            if (normalizedMode == null)
                return;

            _approvalMode = normalizedMode;
            UpdateApprovalModeButton();
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.SendInput("/approval " + normalizedMode);
        }

        private void UpdateReasoningEffortLabel()
        {
            string label = ReasoningEffortOptions[0].Key;
            for (int i = 0; i < ReasoningEffortOptions.Length; i++)
            {
                if (string.Equals(ReasoningEffortOptions[i].Value, _reasoningEffort, StringComparison.OrdinalIgnoreCase))
                {
                    label = ReasoningEffortOptions[i].Key;
                    break;
                }
            }
            reasoningEffortLabel.Text = label;
        }

        private void BuildReasoningEffortMenu()
        {
            reasoningEffortMenu = new ContextMenu();
            foreach (KeyValuePair<string, string> option in ReasoningEffortOptions)
            {
                MenuItem item = new MenuItem();
                item.Header = option.Key;
                item.Tag = option.Value;
                item.IsCheckable = true;
                item.Click += reasoningEffortMenuItem_Click;
                reasoningEffortMenu.Items.Add(item);
            }
        }

        private void reasoningEffortLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            foreach (MenuItem item in reasoningEffortMenu.Items)
            {
                string value = item.Tag as string ?? "";
                item.IsChecked = string.Equals(value, _reasoningEffort, StringComparison.OrdinalIgnoreCase);
            }

            reasoningEffortMenu.PlacementTarget = (UIElement)sender;
            reasoningEffortMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            reasoningEffortMenu.IsOpen = true;
        }

        private void reasoningEffortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = (MenuItem)sender;

            _reasoningEffort = item.Tag as string ?? "";
            UpdateReasoningEffortLabel();
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.SendReasoningEffort(_reasoningEffort);
        }

        private void StartLLMProcess()
        {
            if (processHandler != null)
            {
                processHandler.Dispose();
                processHandler = null;
            }
            stopToolButton.IsEnabled = false;

            processHandler = new ProcessHandler();
            processHandler.SetAssistantName(GetAssistantName());
            _startupPromptConsumed = false;

            processHandler.OutputReceived += OnOutputReceived;
            processHandler.ErrorOccurred += OnErrorOccurred;
            processHandler.GenerationComplete += OnGenerationComplete;
            processHandler.ApprovalRequested += OnApprovalRequested;
            processHandler.StatusReceived += OnStatusReceived;
            processHandler.RunningToolReceived += OnRunningToolReceived;
            processHandler.ReadyReceived += OnReadyReceived;

            tokenTracker.Reset();

            // Start the process
            if (!processHandler.StartProcess("SimpleLLMChatCLI.exe"))
            {
                MessageBox.Show("Failed to start LLM process. Please check if SimpleLLMChatCLI.exe exists.");
                return;
            }

            if (!string.IsNullOrEmpty(_reasoningEffort))
                processHandler.SendReasoningEffort(_reasoningEffort);

            processHandler.SendInput("/approval " + _approvalMode);

            SetInputControlsEnabled(true);
            chatInput.Focus();
        }

        private void OnUi(Action action)
        {
            Dispatcher.BeginInvoke(action);
        }

        private void OnUiBackground(Action action)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)action);
        }

        private void OnStatusReceived(int tokens)
        {
            OnUiBackground(() => tokenTracker.SetTokens(tokens));
        }

        private void OnRunningToolReceived(string toolName)
        {
            OnUiBackground(() => stopToolButton.IsEnabled = !string.IsNullOrEmpty(toolName));
        }

        /// <summary>
        /// The CLI published "STATUS ready": it printed its prompt and is parked
        /// on ReadLine, so no generation is in flight and there is no tool to stop.
        /// </summary>
        private void OnReadyReceived()
        {
            OnUiBackground(() =>
            {
                // The CLI is parked on ReadLine: no further stream output is coming.
                FlushStreamText();
                SetInputControlsEnabled(true);
                stopToolButton.IsEnabled = false;
            });
        }

        private void OnOutputReceived(string text)
        {
            // Called on the stdout reader thread: only accumulate here. The UI work
            // (document append + autoscroll) happens on the flush timer instead of
            // once per chunk, which is what kept the UI thread saturated.
            lock (_pendingStreamText)
                _pendingStreamText.Append(text);

            EnsureStreamFlushTimer();
        }

        private void EnsureStreamFlushTimer()
        {
            if (_streamFlushTimer == null)
            {
                _streamFlushTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(StreamFlushIntervalMs)
                };
                _streamFlushTimer.Tick += (s, e) => FlushStreamText();
            }

            // The previous turn parked this timer at its boundary, so it has to be
            // started again. Without this the second turn would buffer everything
            // and only render at its end, which looks exactly like a hung GUI.
            if (!_streamFlushTimer.IsEnabled)
                _streamFlushTimer.Start();
        }

        /// <summary>
        /// Hands everything buffered so far to the chat list in a single append.
        /// Must run on the UI thread. Safe to call when the buffer is empty.
        /// </summary>
        private void FlushStreamText()
        {
            string pending;
            lock (_pendingStreamText)
            {
                if (_pendingStreamText.Length == 0)
                    return;
                pending = _pendingStreamText.ToString();
                _pendingStreamText.Clear();
            }

            AppendStreamingText(pending);
            ScrollChatToEnd();
            UpdateStreamFlushInterval();
        }

        /// <summary>
        /// Slows the flush cadence down as the active turn grows: every flush
        /// re-lays out the whole FlowDocument, so a fixed 50 ms interval costs
        /// O(n^2) over a long turn. 50 ms at the start, 250 ms once the turn
        /// is long enough to make the difference.
        /// </summary>
        private void UpdateStreamFlushInterval()
        {
            if (_streamFlushTimer == null)
                return;

            int length = _streamingTurn != null ? _streamingTurn.SourceText.Length : 0;
            int ms = Math.Min(50 + length / 2000, 250);
            if ((int)_streamFlushTimer.Interval.TotalMilliseconds != ms)
                _streamFlushTimer.Interval = TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>
        /// Flushes buffered stream text and parks the timer. Called at the turn
        /// boundary so the last chunk is never left sitting in the buffer.
        /// </summary>
        private void FlushAndStopStreamTimer()
        {
            FlushStreamText();

            if (_streamFlushTimer != null)
            {
                _streamFlushTimer.Stop();
            }
        }

        /// <summary>
        /// Each thinking / tool-call block gets its own turn, so the chat list's
        /// item margin separates them the way it separates user and LLM turns.
        /// </summary>
        private void AppendStreamingText(string text)
        {
            TrackCompactedMarker(text);

            string remaining = text;
            while (!string.IsNullOrEmpty(remaining))
            {
                if (_streamingTurn == null)
                {
                    _streamingTurn = AddTurn();
                    _streamingTurn.RoleLabel = GetAssistantName();
                }

                string leftover = _streamingTurn.AppendText(remaining);
                if (leftover == null)
                    return;

                EndStreamingTurn();
                remaining = leftover;
            }
        }

        private void EndStreamingTurn()
        {
            if (_streamingTurn == null)
                return;

            _streamingTurn.Complete();
            if (!_streamingTurn.HasRenderedContent())
                _chatTurns.Remove(_streamingTurn);
            _streamingTurn = null;
        }

        /// <summary>
        /// Watches the streamed stdout for the manual-compaction marker line. A
        /// rolling window catches the marker even when a read boundary splits it.
        /// </summary>
        private void TrackCompactedMarker(string text)
        {
            _compactTail = _compactTail + text;
            if (_compactTail.Length > CompactedMarkerWindow)
                _compactTail = _compactTail.Substring(_compactTail.Length - CompactedMarkerWindow);
            if (_compactTail.IndexOf(CompactedMarker, StringComparison.Ordinal) >= 0)
                _summaryArrived = true;
        }

        private void OnErrorOccurred(string errorMessage)
        {
            OnUi(() => MessageBox.Show(errorMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error));
        }

        private bool OnApprovalRequested(string toolName, string arguments)
        {
            bool approved = false;
            // Run after queued stdout updates so the approval block's leading
            // separator can be removed before the (hidden) GUI prompt is shown.
            Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
            {
                // Streamed text is flushed on a timer, so the last chunk may still be
                // buffered when the tool approval arrives. Render it first: the block
                // separator below is trimmed against what is already on screen.
                FlushStreamText();

                if (_streamingTurn != null)
                {
                    _streamingTurn.TrimTrailingBlankParagraphs();
                    // Keep half of the block separator; the CLI emits the
                    // second newline after receiving the approval response.
                    _streamingTurn.AppendText("\r\n");
                }

                string message = ToolApproval.FormatApprovalMessage(toolName, arguments);
                MessageBoxResult result = MessageBox.Show(
                    message,
                    "Tool Call",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);
                approved = result == MessageBoxResult.Yes;
            }));
            return approved;
        }

        private void OnGenerationComplete()
        {
            OnUi(() =>
            {
                // Buffered stream text belongs to the turn that is ending now, so it
                // must reach the chat list before the turn is closed.
                FlushAndStopStreamTimer();

                // The CLI emits its initial "You:" prompt right after start; that
                // first completion signal is not a real generation finish and must
                // not flip the button back to Send if a send was just issued.
                if (!_startupPromptConsumed)
                {
                    _startupPromptConsumed = true;
                    return;
                }
                EndStreamingTurn();
                // A generation now spans several turns; each tracks what it
                // already rendered, so processed blocks are not revisited.
                if (IsMarkdownParsingEnabled())
                    foreach (ChatTurn turn in _chatTurns)
                        turn.ProcessMarkdown();

                if (_compacting)
                {
                    _compacting = false;
                    // The summary (if produced) is the last turn; the CLI replaced
                    // the whole history with it, so mirror that in the chat.
                    if (_summaryArrived && _chatTurns.Count > 1)
                    {
                        ChatTurn summaryTurn = _chatTurns[_chatTurns.Count - 1];
                        _chatTurns.Clear();
                        _chatTurns.Add(summaryTurn);
                        ScrollChatToEnd();
                    }
                }

                ExitGeneratingMode();
                chatInput.Focus();
            });
        }

        private bool IsMarkdownParsingEnabled()
        {
            return App.Config.GetConfigBool("markdownparsing", true);
        }

        private void LoadAndApplyFontSize()
        {
            int fontSize = FontHandler.GetFontSize();
            FontSize = fontSize;
            ChatTurnItemMargin = new Thickness(0, 0, 0,
                Math.Max(ChatTurnGapMin, Math.Min(ChatTurnGapMax, fontSize + 2)));
            FontHandler.ApplyFontSizeToControl(chatList, fontSize);
            FontHandler.ApplyFontSizeToControl(chatInput, fontSize);
            foreach (ChatTurn turn in _chatTurns)
                turn.ApplyFontSize(fontSize);
            for (int i = 0; i < chatList.Items.Count; i++)
            {
                DependencyObject container = chatList.ItemContainerGenerator.ContainerFromIndex(i);
                if (container == null)
                    continue;
                ChatTurnView view = FindVisualChild(container);
                if (view != null)
                    view.ApplyFontSize(fontSize);
            }
        }

        private static ChatTurnView FindVisualChild(DependencyObject root)
        {
            ChatTurnView view = root as ChatTurnView;
            if (view != null)
                return view;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ChatTurnView found = FindVisualChild(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>
        /// The assistant name the CLI prefixes its output with ("LLM" by default).
        /// </summary>
        private string GetAssistantName()
        {
            string assistantName = App.Config.GetConfigValue("assistantname");
            return string.IsNullOrWhiteSpace(assistantName)
                ? AppConstants.DefaultAssistantName
                : assistantName;
        }

        private ChatTurn AddTurn()
        {
            ChatTurn turn = new ChatTurn();
            turn.ApplyFontSize(FontHandler.GetFontSize());
            ApplyDocumentPageWidth(turn);
            _chatTurns.Add(turn);
            return turn;
        }

        private void chatList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            foreach (ChatTurn turn in _chatTurns)
                ApplyDocumentPageWidth(turn);
        }

        private void ApplyDocumentPageWidth(ChatTurn turn)
        {
            double width = chatList.ActualWidth
                - SystemParameters.VerticalScrollBarWidth
                - 16;
            if (width > 50)
                turn.SetPageWidth(width);
        }

        private void ScrollChatToEnd()
        {
            if (_chatTurns.Count == 0)
                return;

            ScrollViewer scrollViewer = FindScrollViewer(chatList);
            if (scrollViewer == null)
                return;

            if (_chatScrollViewer != scrollViewer)
            {
                _chatScrollViewer = scrollViewer;
                _chatScrollViewer.ScrollChanged += OnChatScrollChanged;
            }

            // The user is reading earlier text: leave the viewport alone.
            if (!_autoScroll)
                return;

            // Clear selection before forcing scroll — scrolling while text is
            // highlighted during streaming can freeze the UI thread.
            ClearChatSelection(chatList);

            chatList.ScrollIntoView(_chatTurns[_chatTurns.Count - 1]);

            scrollViewer.ScrollToEnd();
        }

        /// <summary>
        /// Watches the chat ScrollViewer. A user-driven scroll (wheel or drag —
        /// extent unchanged, offset changed) parks autoscroll; scrolling back
        /// to the very bottom resumes it. Content growth changes the extent
        /// and must not flip the flag.
        /// </summary>
        private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0 && Math.Abs(e.VerticalChange) > 0.5)
                _autoScroll = _chatScrollViewer.VerticalOffset >= _chatScrollViewer.ScrollableHeight - 24;
        }

        private static void ClearChatSelection(DependencyObject root)
        {
            RichTextBox richTextBox = root as RichTextBox;
            if (richTextBox != null
                && richTextBox.Selection != null
                && !richTextBox.Selection.IsEmpty)
            {
                TextPointer end = richTextBox.Document.ContentEnd;
                richTextBox.Selection.Select(end, end);
            }
            else
            {
                TextBox textBox = root as TextBox;
                if (textBox != null && textBox.SelectionLength > 0)
                    textBox.Select(0, 0);
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                ClearChatSelection(VisualTreeHelper.GetChild(root, i));
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer)
                return viewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ScrollViewer child = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (child != null)
                    return child;
            }
            return null;
        }

        private void LoadAndApplyColors()
        {
            try
            {
                var colorSettings = IniFileHandler.LoadIni(App.ColorsFileName);
                ColorHelper.ApplyTheme(ColorHelper.GetThemeName(colorSettings), colorSettings);
            }
            catch
            {
                // On error, do nothing (use system defaults)
            }

            string theme = ColorHelper.GetTheme(App.ColorsFileName);
            themeButton.Content = ColorHelper.IsDarkTheme(theme) ? "☾" : "☀";
            themeButton.ToolTip = "Theme: " + ColorHelper.GetThemeDisplayName(theme);
        }

        private bool CliRunning(string title)
        {
            if (processHandler == null || !processHandler.IsProcessRunning)
            {
                MessageBox.Show("CLI is not running.", title);
                return false;
            }
            return true;
        }

        private void compactButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CliRunning("Compact"))
                return;

            if (_isGenerating || _compacting)
                return;

            // Block input while the CLI generates the summary. The summary text
            // arrives as a normal assistant turn; the next "You:" prompt ends the
            // compacting state and keeps only the summary turn (see OnGenerationComplete).
            // Unlike generation mode the send button is disabled (the summary pass
            // is not steerable from the GUI), so it shows "Compacting…".
            _summaryArrived = false;
            _compactTail = "";
            _compacting = true;
            attachButton.IsEnabled = false;
            chatInput.IsEnabled = false;
            sendButton.Content = "Compacting…";
            sendButton.IsEnabled = false;
            saveButton.IsEnabled = false;
            loadButton.IsEnabled = false;
            stopToolButton.IsEnabled = false;
            processHandler.SendInput("/compactfull");
        }

        private void themeButton_Click(object sender, RoutedEventArgs e)
        {
            string current = ColorHelper.GetTheme(App.ColorsFileName);
            int idx = Array.IndexOf(ColorHelper.ThemeOrder, current);
            string theme = ColorHelper.ThemeOrder[(idx + 1) % ColorHelper.ThemeOrder.Length];
            ColorHelper.SaveTheme(theme, App.ColorsFileName);
            LoadAndApplyColors();
        }


        private void SetInputControlsEnabled(bool enabled)
        {
            // Only used when generation is not running (startup, restart, failure paths).
            if (enabled)
                ExitGeneratingMode();

            attachButton.IsEnabled = enabled;
            chatInput.IsEnabled = enabled;
            sendButton.IsEnabled = enabled;
        }

        /// <summary>
        /// Enter generation mode: the send button becomes an enabled Stop button
        /// (the model cannot be interrupted via stdin, so Stop talks to the CLI's
        /// control pipe instead).
        /// </summary>
        private void EnterGeneratingMode()
        {
            _isGenerating = true;
            _generationStartTime = DateTime.UtcNow;
            attachButton.IsEnabled = false;
            chatInput.IsEnabled = false;
            sendButton.Content = "Stop";
            sendButton.IsEnabled = true;
            saveButton.IsEnabled = false;
            loadButton.IsEnabled = false;
        }

        private void ExitGeneratingMode()
        {
            _isGenerating = false;
            attachButton.IsEnabled = true;
            chatInput.IsEnabled = true;
            sendButton.Content = "Send";
            sendButton.IsEnabled = true;
            saveButton.IsEnabled = true;
            loadButton.IsEnabled = true;
            stopToolButton.IsEnabled = false;
        }

        private void stopToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (processHandler != null && processHandler.IsProcessRunning)
                processHandler.RequestStopTool();
        }

        private void StopGeneration()
        {
            // Ignore the stray second click of a double-click on Send.
            if ((DateTime.UtcNow - _generationStartTime).TotalMilliseconds
                < System.Windows.Forms.SystemInformation.DoubleClickTime)
                return;

            bool stopped = processHandler != null
                && processHandler.IsProcessRunning
                && processHandler.RequestStop();

            if (stopped)
            {
                // Wait for the CLI to return to its prompt before re-enabling input;
                // re-enable once GenerationComplete is raised for the "You:" prompt.
                sendButton.IsEnabled = false;
            }
            else
            {
                // The stop could not be delivered; recover once generation ends.
                ExitGeneratingMode();
            }
        }

        private void sendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isGenerating)
            {
                StopGeneration();
                return;
            }

            string userInput = chatInput.Text;

            ChatTurn userTurn = AddTurn();
            userTurn.Role = ChatTurnRole.User;
            userTurn.RoleLabel = "You";
            userTurn.AppendText(userInput);
            userTurn.Complete();
            _streamingTurn = AddTurn();
            _streamingTurn.RoleLabel = GetAssistantName();
            ScrollChatToEnd();

            // Switch the send button into a Stop button while the model generates.
            EnterGeneratingMode();

            if (processHandler != null && processHandler.IsProcessRunning)
            {
                bool withImage = imageHandler.IsImageAttached;
                bool sent = withImage
                    ? processHandler.SendInput("/image \"" + imageHandler.AttachedImagePath + "\" " + userInput)
                    : processHandler.SendInput(userInput);
                if (!sent)
                {
                    MessageBox.Show("Failed to send input to the process.");
                    SetInputControlsEnabled(true);
                    return;
                }
                // DetachImage also resets the attach button via its event.
                if (withImage)
                    imageHandler.DetachImage();

                chatInput.Clear();
            }
            else
            {
                MessageBox.Show("Error: CLI is not running!");
                SetInputControlsEnabled(false);
                chatInput.Clear();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (processHandler != null)
            {
                processHandler.Dispose();
            }
        }

        // Fired when the attach button is toggled ON
        private void attachButton_Checked(object sender, RoutedEventArgs e)
        {
            if (!imageHandler.SelectImage())
            {
                attachButton.IsChecked = false;
            }
        }

        // Fired when the attach button is toggled OFF
        private void attachButton_Unchecked(object sender, RoutedEventArgs e)
        {
            imageHandler.DetachImage();
        }

        private void chatInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Send on Enter (without Shift), create new line on Shift+Enter
            if (e.Key == Key.Enter && sendButton.IsEnabled)
            {
                if (Keyboard.Modifiers != ModifierKeys.Shift)
                {
                    e.Handled = true; // prevent the ding sound and new line
                    sendButton_Click(sendButton, new RoutedEventArgs());
                }
                // If Shift+Enter, allow default behavior (new line)
            }
        }

        private void ClearChatAndRestart()
        {
            // Queue the clear operation to run after all pending output has been processed.
            // Use BeginInvoke (fire-and-forget) so the UI thread doesn't pump re-entrantly
            // while waiting — that caused intermittent crashes when clicking Clear.
            OnUiBackground(() =>
            {
                EndStreamingTurn();
                _chatTurns.Clear();
                _streamingTurn = null;
                SetInputControlsEnabled(false);
                // Wipe the auto-saved last session so the restarted CLI does
                // not restore it into a freshly cleared chat.
                try
                {
                    SessionStore.Delete(
                        SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory),
                        "lastsession");
                }
                catch { }
                StartLLMProcess();
            });
        }

        private void saveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CliRunning("Save Session"))
                return;

            string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            var dialog = new SessionSaveDialog(sessions);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            string name = dialog.SessionName;
            processHandler.SendInput(string.IsNullOrEmpty(name) ? "/save" : "/save " + name);
        }

        private void loadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CliRunning("Load Session"))
                return;

            string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
            List<SessionStore.SessionInfo> sessions = SessionStore.List(sessionsDir);
            if (sessions.Count == 0)
            {
                MessageBox.Show("No saved sessions found.", "Load Session");
                return;
            }

            var dialog = new SessionLoadDialog(sessions);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true)
                return;

            string sessionName = dialog.SelectedSessionName;
            if (string.IsNullOrEmpty(sessionName))
                return;

            EndStreamingTurn();
            _chatTurns.Clear();
            _streamingTurn = null;
            RenderHistoricTurns(sessionsDir, sessionName);
            EnterGeneratingMode();
            processHandler.SendInput("/load " + sessionName);
        }

        /// <summary>
        /// Draws a saved session's past messages straight into the window so the
        /// user sees the conversation itself, not just the CLI's one-line
        /// "session loaded, N messages" acknowledgement.
        /// </summary>
        private void RenderHistoricTurns(string sessionsDir, string sessionName)
        {
            List<HistoricMessage> loaded = null;
            try
            {
                SessionStore.Load(sessionsDir, sessionName, out loaded);
            }
            catch
            {
                loaded = null;
            }

            if (loaded == null)
                return;

            foreach (HistoricMessage msg in loaded)
            {
                ChatTurn turn = AddTurn();
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    // User messages stay plain text; the role header carries the name.
                    turn.Role = ChatTurnRole.User;
                    turn.RoleLabel = "You";
                    turn.AppendText(DisplayedUserText(msg));
                    turn.Complete();
                    continue;
                }

                string text = BuildHistoricText(msg);
                if (string.IsNullOrEmpty(text))
                {
                    // Nothing visible (empty assistant message) — drop the turn.
                    _chatTurns.Remove(turn);
                    continue;
                }

                turn.RoleLabel = GetAssistantName();
                AppendHistoricText(turn, text).Complete();
            }
        }

        /// <summary>
        /// The user's own words from a historic turn. A session file stores what was
        /// sent to the model, and a user turn is their message plus whatever context
        /// the app baked in (the memory and skills snapshots). Recent files record
        /// the original text separately, so it is used as is; older ones only carry
        /// the combined text, where the baked block is recognised by its header.
        /// </summary>
        private static string DisplayedUserText(HistoricMessage msg)
        {
            if (!string.IsNullOrEmpty(msg.UserText))
                return msg.UserText;

            return StripBakedContext(msg.Content);
        }

        /// <summary>
        /// First lines the bundled context injectors start their snapshot with. Used
        /// only for sessions saved before user messages carried their own text; the
        /// baked snapshot is always the trailing block, so the earliest header wins.
        /// </summary>
        private static readonly string[] BakedContextHeaders =
        {
            "Current Memories:",
            "Skills:"
        };

        private static string StripBakedContext(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            int cut = -1;
            foreach (string header in BakedContextHeaders)
            {
                int at = content.StartsWith(header, StringComparison.Ordinal)
                    ? 0
                    : content.IndexOf("\n\n" + header, StringComparison.Ordinal);
                if (at >= 0 && (cut < 0 || at < cut))
                    cut = at;
            }

            return cut < 0 ? content : content.Substring(0, cut).TrimEnd('\r', '\n', ' ');
        }

        /// <summary>
        /// Appends reconstructed historic text the way streaming does: when a
        /// collapsible block boundary is reached the remaining text continues
        /// in a fresh turn.
        /// </summary>
        private ChatTurn AppendHistoricText(ChatTurn firstTurn, string text)
        {
            ChatTurn turn = firstTurn;
            string remaining = text;
            while (!string.IsNullOrEmpty(remaining))
            {
                string leftover = turn.AppendText(remaining);
                if (string.IsNullOrEmpty(leftover))
                    break;

                if (IsMarkdownParsingEnabled())
                    turn.ProcessMarkdown();
                turn.Complete();
                turn = AddTurn();
                turn.RoleLabel = GetAssistantName();
                remaining = leftover;
            }

            if (IsMarkdownParsingEnabled())
                turn.ProcessMarkdown();
            return turn;
        }

        /// <summary>
        /// Rebuilds the marker text the CLI streams live for a historic message,
        /// so saved tool calls and tool outputs render as the same collapsible
        /// blocks the user saw in the original session.
        /// </summary>
        private static string BuildHistoricText(HistoricMessage msg)
        {
            string content = msg.Content ?? string.Empty;

            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                if (App.Config.GetChatBlockDisplayMode("tooloutputdisplay", ChatBlockDisplayMode.Shown) == ChatBlockDisplayMode.Hidden)
                    // Hidden shows only the exit-code stub, like the live stream.
                    return "[tool output] " + ExtractExitCodeLine(content);
                return "[tool output]\n" + content + "\n[/tool output]";
            }

            if (msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                return content;

            bool hideArgs = App.Config.GetChatBlockDisplayMode("toolcalldisplay", ChatBlockDisplayMode.Collapsed) == ChatBlockDisplayMode.Hidden;
            StringBuilder sb = new StringBuilder(content);
            for (int i = 0; i < msg.ToolCalls.Count; i++)
            {
                HistoricToolCall call = msg.ToolCalls[i];
                sb.Append("[tool call] ").Append(string.IsNullOrEmpty(call.Name) ? "tool" : call.Name);
                if (!hideArgs && !string.IsNullOrEmpty(call.Arguments))
                {
                    sb.Append('\n').Append(call.Arguments);
                    if (!call.Arguments.EndsWith("\n"))
                        sb.Append('\n');
                }
                sb.Append("\n[/tool call]");
            }
            return sb.ToString();
        }

        private static string ExtractExitCodeLine(string content)
        {
            int start = content.IndexOf("Exit Code: ", StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;

            int end = content.IndexOf('\n', start);
            return end < 0 ? content.Substring(start) : content.Substring(start, end - start);
        }

        private void AutoRestoreLastSession()
        {
            try
            {
                string sessionsDir = SessionStore.DefaultDirectory(AppDomain.CurrentDomain.BaseDirectory);
                string sessionFile = Path.Combine(sessionsDir, "lastsession.json");
                if (File.Exists(sessionFile))
                {
                    // Render the previously saved conversation into the window so a
                    // freshly reopened app shows the actual past messages instead of
                    // an empty chat. The CLI itself also restores this session into
                    // its own context, so nothing is lost or overwritten.
                    RenderHistoricTurns(sessionsDir, "lastsession");
                }
            }
            catch
            {
                // Best-effort: never block startup on history rendering failures.
            }
        }

        private sealed class HistoricMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public List<HistoricToolCall> ToolCalls { get; set; }

            /// <summary>What the user actually typed, when Content also holds baked
            /// context (memory and skills snapshots) that must not be shown as theirs.</summary>
            public string UserText { get; set; }
        }

        private sealed class HistoricToolCall
        {
            public string Name { get; set; }
            public string Arguments { get; set; }
        }

        private void clearButton_Click(object sender, RoutedEventArgs e)
        {
            // Kill running process first to stop any new output
            if (processHandler != null)
            {
                processHandler.Dispose();
                processHandler = null;
            }

            ClearChatAndRestart();
        }

        private void optionsButton_Click(object sender, RoutedEventArgs e)
        {
            var optionsDialog = new Options(processHandler);
            optionsDialog.Owner = this;

            if (optionsDialog.ShowDialog() == true)
            {
                App.LoadSettings(); // Reload settings after options dialog saves
                FontHandler.ApplyFontToWindow(this);
                FontHandler.ApplyCodeBlockFontFamily();
                LoadAndApplyFontSize();
                LoadAndApplyColors();
                if (processHandler != null)
                    processHandler.SetAssistantName(GetAssistantName());
                tokenTracker.Refresh();

                if (processHandler != null && processHandler.IsProcessRunning)
                    processHandler.SendReload();
                else
                    ClearChatAndRestart();
            }
        }
    }
}