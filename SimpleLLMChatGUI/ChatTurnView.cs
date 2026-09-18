using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Keeps pixel scrolling on .NET 4: off-screen documents are replaced by their
    /// last measured height. Only small Border placeholders remain in the list.
    /// </summary>
    public sealed class ChatTurnView : Border
    {
        public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(
            "Turn", typeof(ChatTurn), typeof(ChatTurnView), new PropertyMetadata(null, OnTurnChanged));
        public ChatTurn Turn
        {
            get { return (ChatTurn)GetValue(TurnProperty); }
            set { SetValue(TurnProperty, value); }
        }

        private static readonly DependencyProperty PendingCorrectionProperty = DependencyProperty.RegisterAttached(
            "PendingCorrection", typeof(double), typeof(ChatTurnView), new PropertyMetadata(0.0));
        private static readonly DependencyProperty CorrectionQueuedProperty = DependencyProperty.RegisterAttached(
            "CorrectionQueued", typeof(bool), typeof(ChatTurnView), new PropertyMetadata(false));

        private const double UserBubbleCornerRadius = 8;
        private const double UserBubbleMaxWidthFraction = 0.8;
        private const double RoleLabelScale = 0.86;

        private ScrollViewer _scrollViewer;
        private RichTextBox _editor;
        private TextBlock _roleHeader;
        private Button _copyButton;
        private DockPanel _contentRoot;
        private ListBoxItem _listBoxItem;
        private bool _updating;

        public ChatTurnView()
        {
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += delegate
            {
                UpdateVisibility();
                UpdateUserBubbleMaxWidth();
            };
        }

        private static void OnTurnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ChatTurnView view = (ChatTurnView)sender;
            view.Detach(args.OldValue as ChatTurn);
            view.Height = double.NaN;
            if (view.IsLoaded) view.ShowDocument();
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
            DependencyObject parent = VisualTreeHelper.GetParent(this);
            while (parent != null && !(parent is ScrollViewer))
                parent = VisualTreeHelper.GetParent(parent);
            _scrollViewer = parent as ScrollViewer;
            if (_scrollViewer != null) _scrollViewer.ScrollChanged += OnScrollChanged;

            parent = VisualTreeHelper.GetParent(this);
            while (parent != null && !(parent is ListBoxItem))
                parent = VisualTreeHelper.GetParent(parent);
            _listBoxItem = parent as ListBoxItem;
            if (_listBoxItem != null)
                _listBoxItem.SizeChanged += OnListItemSizeChanged;

            ShowDocument();
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            if (_scrollViewer != null) _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
            if (_listBoxItem != null)
            {
                _listBoxItem.SizeChanged -= OnListItemSizeChanged;
                _listBoxItem = null;
            }
            Detach(Turn);
        }

        private void OnListItemSizeChanged(object sender, SizeChangedEventArgs args)
        {
            UpdateUserBubbleMaxWidth();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
        {
            if (ReferenceEquals(args.OriginalSource, _scrollViewer)) UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            if (_updating || _scrollViewer == null || Turn == null || !IsLoaded
                || !_scrollViewer.IsAncestorOf(this)) return;
            _updating = true;
            try
            {
                double viewport = _scrollViewer.ViewportHeight;
                if (viewport <= 0) return;
                double top = TransformToAncestor(_scrollViewer).Transform(new Point()).Y;
                // One viewport of prefetch on each side avoids loading at the visible edge.
                bool nearby = top <= viewport * 2 && top + ActualHeight >= -viewport;
                if (nearby || !Turn.IsCompleted)
                    ShowDocument();
                else if (_editor != null && _editor.Selection.IsEmpty && !_editor.IsKeyboardFocusWithin)
                {
                    Height = ActualHeight;
                    Detach(Turn);
                }
            }
            finally { _updating = false; }
        }

        /// <summary>
        /// Constrains user bubbles to 80% of the row width so they read as bubbles,
        /// while assistant turns keep the full document width.
        /// </summary>
        private void UpdateUserBubbleMaxWidth()
        {
            if (Turn == null || Turn.Role != ChatTurnRole.User || _listBoxItem == null)
                return;

            double width = _listBoxItem.ActualWidth;
            if (width > 0)
                MaxWidth = width * UserBubbleMaxWidthFraction;
        }

        /// <summary>
        /// Rescales the role header label when the chat font size changes.
        /// </summary>
        public void ApplyFontSize(double fontSize)
        {
            if (_roleHeader != null)
                _roleHeader.FontSize = fontSize * RoleLabelScale;
        }

        private void ShowDocument()
        {
            if (_editor != null || Turn == null) return;
            double previousHeight = Height;
            bool aboveViewport = _scrollViewer != null && _scrollViewer.IsAncestorOf(this)
                && !double.IsNaN(previousHeight)
                && TransformToAncestor(_scrollViewer).Transform(new Point()).Y + previousHeight <= 0;
            _editor = new RichTextBox
            {
                IsReadOnly = true,
                IsUndoEnabled = false,
                IsDocumentEnabled = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _editor.SetResourceReference(Control.ForegroundProperty, "ChatTextColorBrush");
            Style paragraph = new Style(typeof(Paragraph));
            paragraph.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            paragraph.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(0)));
            _editor.Resources.Add(typeof(Paragraph), paragraph);
            _editor.Document = Turn.Document;

            _roleHeader = new TextBlock
            {
                Text = BuildRoleHeaderText(),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalAlignment = Turn.Role == ChatTurnRole.User
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left
            };
            _roleHeader.SetResourceReference(TextBlock.ForegroundProperty, "RoleLabelBrush");
            _roleHeader.FontSize = FontHandler.GetFontSize() * RoleLabelScale;

            _copyButton = new Button
            {
                Content = "Copy",
                MinWidth = 55,
                MinHeight = 22,
                Margin = new Thickness(8, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetAutomationId(_copyButton, "copyButton");
            ChatTurn turn = Turn;
            var copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            copyTimer.Tick += (s, e) =>
            {
                _copyButton.Content = "Copy";
                copyTimer.Stop();
            };
            _copyButton.Click += (s, e) =>
            {
                try { Clipboard.SetText(turn.SourceText); } catch { }
                _copyButton.Content = "Copied";
                copyTimer.Stop();
                copyTimer.Start();
            };

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_roleHeader, 0);
            headerRow.Children.Add(_roleHeader);
            Grid.SetColumn(_copyButton, 1);
            headerRow.Children.Add(_copyButton);

            _contentRoot = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(headerRow, Dock.Top);
            _contentRoot.Children.Add(headerRow);
            _contentRoot.Children.Add(_editor);

            if (Turn.Role == ChatTurnRole.User)
            {
                CornerRadius = new CornerRadius(UserBubbleCornerRadius);
                Padding = new Thickness(10, 8, 10, 8);
                HorizontalAlignment = HorizontalAlignment.Right;
                SetResourceReference(BackgroundProperty, "UserBubbleBackgroundBrush");
                UpdateUserBubbleMaxWidth();
            }
            else
            {
                ClearValue(BackgroundProperty);
                ClearValue(CornerRadiusProperty);
                Padding = new Thickness(0);
                HorizontalAlignment = HorizontalAlignment.Stretch;
                // .NET 4 does not accept NaN for MaxWidth; clear the value instead.
                ClearValue(MaxWidthProperty);
            }

            Height = double.NaN;
            Child = _contentRoot;
            if (aboveViewport && ActualWidth > 0)
            {
                // Formatting or a font/width change may alter an archived row's height.
                // Correct for rows above the reader, keeping the visible content anchored.
                _contentRoot.Measure(new Size(ActualWidth, double.PositiveInfinity));
                QueueScrollCorrection(_scrollViewer, _contentRoot.DesiredSize.Height - previousHeight);
            }
        }

        private string BuildRoleHeaderText()
        {
            string label = Turn.RoleLabel;
            if (string.IsNullOrEmpty(label))
                label = Turn.Role == ChatTurnRole.User ? "You" : AppConstants.DefaultAssistantName;
            return label + " · " + Turn.Created.ToString("HH:mm");
        }

        private static void QueueScrollCorrection(ScrollViewer scroll, double delta)
        {
            if (Math.Abs(delta) < 0.1) return;
            scroll.SetValue(PendingCorrectionProperty, (double)scroll.GetValue(PendingCorrectionProperty) + delta);
            if ((bool)scroll.GetValue(CorrectionQueuedProperty)) return;
            scroll.SetValue(CorrectionQueuedProperty, true);
            scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                double correction = (double)scroll.GetValue(PendingCorrectionProperty);
                scroll.SetValue(PendingCorrectionProperty, 0.0);
                scroll.SetValue(CorrectionQueuedProperty, false);
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + correction);
            }));
        }

        private void Detach(ChatTurn turn)
        {
            if (_editor != null)
            {
                _editor.Document = new FlowDocument();
                _editor = null;
            }
            _contentRoot = null;
            _roleHeader = null;
            _copyButton = null;
            Child = null;
            if (turn != null) turn.ReleaseDocument();
        }
    }
}
