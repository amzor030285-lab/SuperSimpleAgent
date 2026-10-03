using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SimpleLLMChatGUI
{
    public partial class ScheduledTasksWindow : Window
    {
        private readonly ScheduledTaskScheduler _scheduler;
        private List<ScheduledTask> _jobs;

        public ScheduledTasksWindow(ScheduledTaskScheduler scheduler)
        {
            _scheduler = scheduler;
            InitializeComponent();
            _scheduler.JobsChanged += OnJobsChanged;
            _jobs = _scheduler.GetJobs();
            RefreshList();
        }

        protected override void OnClosed(EventArgs e)
        {
            _scheduler.JobsChanged -= OnJobsChanged;
            base.OnClosed(e);
        }

        private void OnJobsChanged()
        {
            _jobs = _scheduler.GetJobs();
            RefreshList();
            // RefreshList re-selects the same item by id but that may not re-raise
            // SelectionChanged, so push the freshly-read state into the summary panel.
            ScheduledTask job = SelectedJob();
            if (job != null)
                selectedTaskStatus.Text = BuildSummary(job);
        }

        private void RefreshList()
        {
            string selectedId = null;
            object sel = scheduledTasksList.SelectedItem;
            if (sel is ListBoxItem)
                selectedId = (string)((ListBoxItem)sel).Tag;

            scheduledTasksList.Items.Clear();
            foreach (ScheduledTask job in _jobs)
            {
                ListBoxItem item = new ListBoxItem();
                item.Tag = job.Id;
                item.Content = job.Name + "   [" + job.Schedule + "]" + (job.Enabled ? string.Empty : "   (disabled)");
                scheduledTasksList.Items.Add(item);
            }

            if (selectedId != null)
            {
                foreach (object item in scheduledTasksList.Items)
                {
                    if (item is ListBoxItem && (string)((ListBoxItem)item).Tag == selectedId)
                    {
                        scheduledTasksList.SelectedItem = item;
                        break;
                    }
                }
            }

            if (scheduledTasksList.SelectedItem == null)
            {
                selectedTaskStatus.Text = "No task selected.";
                historyList.Items.Clear();
            }
        }

        private void scheduledTasksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScheduledTask job = SelectedJob();
            if (job == null)
            {
                selectedTaskStatus.Text = "No task selected.";
                historyList.Items.Clear();
                return;
            }
            selectedTaskStatus.Text = BuildSummary(job);
            historyList.Items.Clear();
            for (int i = job.RecentRuns.Count - 1; i >= 0; i--)
            {
                RunRecord r = job.RecentRuns[i];
                string line = r.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + "   " + r.Status;
                if (r.Error != null && r.Error.Length > 0)
                    line += "   " + r.Error;
                historyList.Items.Add(line);
            }
        }

        private ScheduledTask SelectedJob()
        {
            object sel = scheduledTasksList.SelectedItem;
            if (sel is ListBoxItem)
            {
                string id = (string)((ListBoxItem)sel).Tag;
                foreach (ScheduledTask job in _jobs)
                {
                    if (job.Id == id)
                        return job;
                }
            }
            return null;
        }

        private string BuildSummary(ScheduledTask job)
        {
            string next = job.NextRunAtUtc.HasValue
                ? job.NextRunAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " (local)"
                : "(none)";
            string last = job.LastStatus == null ? "never" : job.LastStatus;
            string lastAt = job.LastRunAtUtc.HasValue ? job.LastRunAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty;
            string state = job.Enabled ? "enabled" : (job.AutoDisabled ? "auto-disabled" : "disabled");

            return "Name: " + job.Name + "\n"
                + "Schedule: " + job.Schedule + "\n"
                + "Session: " + job.SessionName + "\n"
                + "State: " + state + "\n"
                + "Next run: " + next + "\n"
                + "Last: " + last + (lastAt.Length > 0 ? " at " + lastAt : string.Empty)
                + (job.LastError != null && job.LastError.Length > 0 ? "\nLastError: " + job.LastError : string.Empty)
                + (job.LastOutputSnippet != null && job.LastOutputSnippet.Length > 0 ? "\nOutput: " + job.LastOutputSnippet : string.Empty);
        }

        private void addTaskButton_Click(object sender, RoutedEventArgs e)
        {
            string name = newTaskName.Text.Trim();
            string schedule = scheduleEditor.ScheduleText ?? string.Empty;
            string prompt = newTaskPrompt.Text.Trim();

            if (name.Length == 0 || schedule.Length == 0 || prompt.Length == 0)
            {
                string msg = "Name, schedule and prompt are all required.";
                if (!scheduleEditor.IsValid && scheduleEditor.ValidationMessage != null)
                    msg += " Schedule: " + scheduleEditor.ValidationMessage;
                MessageBox.Show(msg, "Scheduled tasks");
                return;
            }

            _scheduler.AddTask(name, schedule, prompt, null);
            newTaskName.Text = string.Empty;
            newTaskPrompt.Text = string.Empty;
            scheduleEditor.Reset();
        }

        private void toggleEnableButton_Click(object sender, RoutedEventArgs e)
        {
            ScheduledTask job = SelectedJob();
            if (job == null)
                return;
            _scheduler.SetEnabled(job.Id, !job.Enabled);
        }

        private void removeTaskButton_Click(object sender, RoutedEventArgs e)
        {
            ScheduledTask job = SelectedJob();
            if (job == null)
                return;
            _scheduler.RemoveTask(job.Id);
        }

        private void refreshButton_Click(object sender, RoutedEventArgs e)
        {
            _jobs = _scheduler.GetJobs();
            RefreshList();
        }

        private void closeButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
