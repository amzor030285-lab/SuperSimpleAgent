using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Save dialog: optionally names the session; an empty name lets the CLI pick an
    /// auto-generated one. When existing sessions are supplied, they are listed so the
    /// user can pick one to overwrite (the name is copied in) or just type a new name.
    /// Name + automation ids are the contract for GUI tests.
    /// </summary>
    public sealed class SessionSaveDialog : Window
    {
        private readonly TextBox nameInput;
        private readonly ListBox existingList;

        public SessionSaveDialog(List<SessionStore.SessionInfo> existing = null)
        {
            Title = "Save Session";
            Width = 320;
            Height = 150;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.WidthAndHeight;

            var panel = new Grid { Margin = new Thickness(12) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "Session name (leave empty for auto name):",
                Margin = new Thickness(0, 0, 0, 4)
            };
            Grid.SetRow(label, 0);
            panel.Children.Add(label);

            existingList = new ListBox
            {
                Margin = new Thickness(0, 4, 0, 8),
                MaxHeight = 110,
                Visibility = existing != null && existing.Count > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            existingList.SetValue(AutomationProperties.AutomationIdProperty, "existingSessionListBox");
            existingList.Name = "existingSessionListBox";
            if (existing != null)
            {
                foreach (SessionStore.SessionInfo info in existing)
                    existingList.Items.Add(new SessionListItem(info));
            }
            existingList.SelectionChanged += (s, e) =>
            {
                var item = existingList.SelectedItem as SessionListItem;
                if (item != null)
                    nameInput.Text = item.Info.Name;
            };
            Grid.SetRow(existingList, 1);
            panel.Children.Add(existingList);

            var inside = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            nameInput = new TextBox { MinWidth = 150 };
            nameInput.SetValue(AutomationProperties.AutomationIdProperty, "saveNameInput");
            nameInput.Name = "saveNameInput";
            Grid.SetColumn(nameInput, 0);
            inside.Children.Add(nameInput);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var saveButton = new Button { Content = "Save", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            saveButton.SetValue(AutomationProperties.AutomationIdProperty, "dialogOkButton");
            saveButton.Name = "dialogOkButton";
            saveButton.Click += (s, e) =>
            {
                SessionName = (nameInput.Text ?? string.Empty).Trim();
                DialogResult = true;
            };
            buttonPanel.Children.Add(saveButton);

            var cancelButton = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            cancelButton.SetValue(AutomationProperties.AutomationIdProperty, "dialogCancelButton");
            cancelButton.Name = "dialogCancelButton";
            cancelButton.Click += (s, e) => DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            Grid.SetColumn(buttonPanel, 1);
            inside.Children.Add(buttonPanel);

            Grid.SetRow(inside, 2);
            panel.Children.Add(inside);

            Content = panel;
            Loaded += (s, e) => nameInput.Focus();
        }

        /// <summary>Trimmed session name; empty when the user wants an auto name.</summary>
        public string SessionName { get; private set; }

        private sealed class SessionListItem
        {
            public SessionListItem(SessionStore.SessionInfo info)
            {
                Info = info;
            }

            public SessionStore.SessionInfo Info { get; }

            public override string ToString()
            {
                DateTime savedUtc = SessionStore.EffectiveSavedAtUtc(Info);
                DateTime local = savedUtc != DateTime.MinValue
                    ? savedUtc.ToLocalTime()
                    : DateTime.MinValue;
                string when = local != DateTime.MinValue ? local.ToString("yyyy-MM-dd HH:mm") : "unknown";
                double kb = (double)Info.SizeBytes / 1024.0;
                return Info.Name + "  (" + when + ", " + Info.MessageCount + " msgs, " + kb.ToString("F1") + " KB)";
            }
        }
    }

    /// <summary>
    /// Load dialog: pick one of the saved session files to restore. The list is built
    /// from SessionStore so the dialog works even when the CLI is not running.
    /// </summary>
    public sealed class SessionLoadDialog : Window
    {
        private readonly ListBox sessionList;

        public SessionLoadDialog(List<SessionStore.SessionInfo> sessions)
        {
            Title = "Load Session";
            Width = 340;
            Height = 260;
            MinWidth = 300;
            MinHeight = 200;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new Grid { Margin = new Thickness(12) };
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            sessionList = new ListBox
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            sessionList.SetValue(AutomationProperties.AutomationIdProperty, "sessionListBox");
            sessionList.Name = "sessionListBox";

            if (sessions != null)
            {
                foreach (SessionStore.SessionInfo info in sessions)
                {
                    sessionList.Items.Add(new SessionListItem(info));
                }
            }

            Grid.SetRow(sessionList, 0);
            panel.Children.Add(sessionList);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button { Content = "Load", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            okButton.SetValue(AutomationProperties.AutomationIdProperty, "dialogOkButton");
            okButton.Name = "dialogOkButton";
            okButton.Click += (s, e) => DialogResult = true;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0) };
            cancelButton.SetValue(AutomationProperties.AutomationIdProperty, "dialogCancelButton");
            cancelButton.Name = "dialogCancelButton";
            cancelButton.Click += (s, e) => DialogResult = false;
            buttonPanel.Children.Add(cancelButton);

            Grid.SetRow(buttonPanel, 1);
            panel.Children.Add(buttonPanel);

            Content = panel;

            sessionList.SelectionChanged += (s, e) =>
            {
                if (sessionList.SelectedItem == null)
                    okButton.IsEnabled = false;
                else
                    okButton.IsEnabled = true;
            };
            okButton.IsEnabled = sessions != null && sessions.Count > 0;

            sessionList.MouseDoubleClick += (s, e) =>
            {
                if (sessionList.SelectedItem != null)
                    DialogResult = true;
            };
        }

        public string SelectedSessionName
        {
            get
            {
                SessionListItem item = sessionList.SelectedItem as SessionListItem;
                return item != null ? item.SessionName : null;
            }
        }

        private sealed class SessionListItem
        {
            public SessionListItem(SessionStore.SessionInfo info)
            {
                SessionName = info.Name;
                Info = info;
            }

            public string SessionName { get; }
            public SessionStore.SessionInfo Info { get; }

            public override string ToString()
            {
                DateTime savedUtc = SessionStore.EffectiveSavedAtUtc(Info);
                DateTime local = savedUtc != DateTime.MinValue
                    ? savedUtc.ToLocalTime()
                    : DateTime.MinValue;
                string when = local != DateTime.MinValue ? local.ToString("yyyy-MM-dd HH:mm") : "unknown";
                double kb = (double)Info.SizeBytes / 1024.0;
                return SessionName + "  (" + when + ", " + Info.MessageCount + " msgs, " + kb.ToString("F1") + " KB)";
            }
        }
    }
}
