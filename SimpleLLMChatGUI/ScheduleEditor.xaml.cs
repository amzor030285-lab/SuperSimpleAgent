using System;
using System.Windows;
using System.Windows.Controls;

namespace SimpleLLMChatGUI
{
    // Builds a schedule expression from friendly controls (one-shot / every / cron),
    // live-validates it against Schedule.Parse and shows the next run time.
    public partial class ScheduleEditor : UserControl
    {
        // The raw schedule string, or null when the current settings are invalid/empty.
        public string ScheduleText { get; private set; }
        public bool IsValid { get; private set; }
        public string ValidationMessage { get; private set; }

        public ScheduleEditor()
        {
            InitializeComponent();

            PopulateHours(atHour);
            PopulateMinutes(atMinute);
            PopulateHours(cronHour);
            PopulateMinutes(cronMinute);
            for (int i = 1; i <= 31; i++)
                cronDom.Items.Add(i);

            atHour.SelectedIndex = 9;
            atMinute.SelectedIndex = 0;
            cronHour.SelectedIndex = 9;
            cronMinute.SelectedIndex = 0;
            cronDom.SelectedIndex = 1;

            if (atDate.SelectedDate == null)
                atDate.SelectedDate = DateTime.Today;

            Recompute();
        }

        // Restore the defaults (used after a task is added).
        public void Reset()
        {
            modeCombo.SelectedIndex = 1;
            atInRadio.IsChecked = true;
            atInValue.Text = "15";
            atInUnit.SelectedIndex = 1;
            if (atDate.SelectedDate == null)
                atDate.SelectedDate = DateTime.Today;
            atHour.SelectedIndex = 9;
            atMinute.SelectedIndex = 0;
            everyValue.Text = "15";
            everyUnit.SelectedIndex = 1;
            cronHour.SelectedIndex = 9;
            cronMinute.SelectedIndex = 0;
            cronDow.SelectedIndex = 0;
            cronDom.SelectedIndex = 1;
            cronCustom.Text = string.Empty;
            Recompute();
        }

        private static void PopulateHours(ComboBox c)
        {
            for (int i = 0; i < 24; i++)
                c.Items.Add(i.ToString("00"));
        }

        private static void PopulateMinutes(ComboBox c)
        {
            for (int i = 0; i < 60; i++)
                c.Items.Add(i.ToString("00"));
        }

        private static int C(ComboBox c, int fallback)
        {
            int i = c.SelectedIndex;
            return i < 0 ? fallback : i;
        }

        private void AnyChanged(object sender, RoutedEventArgs e)
        {
            Recompute();
        }

        private void Recompute()
        {
            // During InitializeComponent the XAML already applies early defaults
            // (e.g. modeCombo SelectedIndex), which raise change events before the
            // later-named controls are assigned. Skip until the full layout exists.
            if (nextRunText == null)
                return;

            UpdateVisibility();

            string expr = BuildExpression().Trim();
            bool valid = false;
            string message = string.Empty;
            string next = string.Empty;

            if (expr.Length == 0)
            {
                message = "No schedule entered.";
            }
            else
            {
                try
                {
                    Schedule.Parsed p = Schedule.Parse(expr, DateTime.UtcNow);
                    valid = true;
                    next = FormatNextRun(p.NextRunAfter(DateTime.UtcNow));
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                }
            }

            IsValid = valid;
            ScheduleText = valid ? expr : null;
            ValidationMessage = message;

            exprText.Text = "Expression: " + (expr.Length > 0 ? expr : "(empty)");
            nextRunText.Text = valid ? next : "Invalid: " + message;
        }

        private void UpdateVisibility()
        {
            int mode = C(modeCombo, 1);
            atPanel.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
            everyPanel.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
            cronPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (mode == 2)
            {
                int preset = C(cronPreset, 0);
                bool needTime = preset == 0 || preset == 1 || preset == 2 || preset == 3;
                cronTimeRow.Visibility = needTime ? Visibility.Visible : Visibility.Collapsed;
                cronDowRow.Visibility = preset == 2 ? Visibility.Visible : Visibility.Collapsed;
                cronDomRow.Visibility = preset == 3 ? Visibility.Visible : Visibility.Collapsed;
                cronCustom.Visibility = preset == 4 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private string BuildExpression()
        {
            int mode = C(modeCombo, 1);
            if (mode == 0)
            {
                if (atInRadio.IsChecked == true)
                {
                    string v = atInValue.Text.Trim();
                    if (v.Length == 0)
                        return string.Empty;
                    return "at " + v + UnitSuffix(C(atInUnit, 1));
                }
                return "at " + AtDateTimeString();
            }
            if (mode == 1)
            {
                string v = everyValue.Text.Trim();
                if (v.Length == 0)
                    return string.Empty;
                return "every " + v + UnitSuffix(C(everyUnit, 1));
            }
            return CronExpression();
        }

        private static string UnitSuffix(int index)
        {
            switch (index)
            {
                case 0: return "s";
                case 1: return "m";
                case 2: return "h";
                default: return "d";
            }
        }

        private string AtDateTimeString()
        {
            DateTime? sel = atDate.SelectedDate;
            DateTime date = (sel != null ? sel.Value : DateTime.Today).Date;
            int h = C(atHour, 0);
            int m = C(atMinute, 0);
            return date.ToString("yyyy-MM-dd") + " " + h.ToString("00") + ":" + m.ToString("00");
        }

        private string CronExpression()
        {
            int preset = C(cronPreset, 0);
            int min = C(cronMinute, 0);
            int hour = C(cronHour, 0);
            switch (preset)
            {
                case 0: return min + " " + hour + " * * *";            // daily
                case 1: return min + " " + hour + " * * 1-5";          // weekdays
                case 2: return min + " " + hour + " * * " + C(cronDow, 0); // weekly
                case 3:
                {
                    // The ComboBox items are the day values 1..31, not 0-based indices;
                    // using SelectedIndex would pick a day one earlier than shown.
                    object dom = cronDom.SelectedItem;
                    int day = (dom is int) ? (int)dom : 1;
                    return min + " " + hour + " " + day + " * *"; // monthly
                }
                default: return cronCustom.Text.Trim();                // custom
            }
        }

        private static string FormatNextRun(DateTime? nextUtc)
        {
            if (nextUtc == null)
                return "Next run: the instant has already passed - it will fire once on the next tick.";
            return "Next run: " + nextUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " (local)";
        }
    }
}
