using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// The in-app scheduled-task runner. A DispatcherTimer fires on the UI thread; each
    /// tick hops to a background worker (so a long-running job never freezes the UI),
    /// runs any due tasks through the headless CLI ("--session"), then persists the list
    /// and raises JobsChanged on the UI thread.
    /// </summary>
    public sealed class ScheduledTaskScheduler : IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer;
        private readonly List<ScheduledTask> _jobs;
        private readonly string _baseDir;
        private readonly object _listLock = new object();
        private bool _busy;
        private bool _started;
        private bool _persistOk;
        private bool _disposed;

        public ScheduledTaskScheduler()
        {
            _ui = Dispatcher.CurrentDispatcher;
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                _jobs = ScheduledTaskStore.Load(_baseDir);
                _persistOk = true;
            }
            catch
            {
                // The file is unreadable/corrupt: keep an empty in-memory list but do NOT
                // let later saves overwrite the file the user still owns.
                _jobs = new List<ScheduledTask>();
                _persistOk = false;
            }
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(20)
            };
            _timer.Tick += delegate { OnTimerTick(); };
        }

        public event Action JobsChanged;

        // A consistent view of the task list for display. Each job is cloned so the UI
        // never reads (or iterates) a live task while the worker thread mutates it.
        public List<ScheduledTask> GetJobs()
        {
            lock (_listLock)
            {
                List<ScheduledTask> clones = new List<ScheduledTask>(_jobs.Count);
                foreach (ScheduledTask job in _jobs)
                    clones.Add(job.Clone());
                return clones;
            }
        }

        public void Start()
        {
            if (_started)
                return;
            _started = true;
            _timer.Start();
        }

        public void Stop()
        {
            _timer.Stop();
            _started = false;
        }

        public void AddTask(string name, string schedule, string prompt, string sessionName)
        {
            ScheduledTask task = ScheduledTaskStore.Create(name, schedule, prompt, sessionName, DateTime.UtcNow);
            lock (_listLock)
            {
                _jobs.Add(task);
            }
            Save();
            RaiseChanged();
        }

        public void RemoveTask(string id)
        {
            lock (_listLock)
            {
                for (int i = _jobs.Count - 1; i >= 0; i--)
                {
                    if (_jobs[i].Id == id)
                    {
                        _jobs.RemoveAt(i);
                        break;
                    }
                }
            }
            Save();
            RaiseChanged();
        }

        public void SetEnabled(string id, bool enabled)
        {
            lock (_listLock)
            {
                foreach (ScheduledTask job in _jobs)
                {
                    if (job.Id == id)
                    {
                        job.Enabled = enabled;
                        if (enabled)
                            job.AutoDisabled = false;
                        break;
                    }
                }
            }
            Save();
            RaiseChanged();
        }

        private void Save()
        {
            if (!_persistOk)
                return;
            List<ScheduledTask> snapshot;
            lock (_listLock)
            {
                snapshot = new List<ScheduledTask>(_jobs.Count);
                foreach (ScheduledTask job in _jobs)
                    snapshot.Add(job.Clone());
            }
            ScheduledTaskStore.Save(_baseDir, snapshot);
        }

        private void OnTimerTick()
        {
            if (_busy)
                return;
            int count;
            lock (_listLock)
            {
                count = _jobs.Count;
            }
            if (count == 0)
                return;
            _busy = true;

            ThreadStart work = delegate
            {
                try
                {
                    List<ScheduledTask> snapshot;
                    lock (_listLock)
                    {
                        snapshot = new List<ScheduledTask>(_jobs);
                    }

                    // Runs due jobs off the UI thread (may spawn CLI processes).
                    ScheduledTaskEngine.Tick(snapshot, DateTime.UtcNow, RunJob);

                    Save();
                }
                finally
                {
                    _busy = false;
                    if (!_disposed)
                    {
                        try
                        {
                            _ui.BeginInvoke(DispatcherPriority.Normal, new Action(RaiseChanged));
                        }
                        catch
                        {
                            // The dispatcher is shutting down; dropping the notify is fine.
                        }
                    }
                }
            };
            Thread worker = new Thread(work);
            worker.IsBackground = true;
            worker.Start();
        }

        private void RaiseChanged()
        {
            if (JobsChanged != null)
                JobsChanged();
        }

        // Launches the headless CLI for one task and captures its stdout as the output.
        private ScheduledTaskEngine.RunResult RunJob(ScheduledTask job)
        {
            string exe = Path.Combine(_baseDir, "SimpleLLMChatCLI.exe");
            if (!File.Exists(exe))
                return new ScheduledTaskEngine.RunResult { Success = false, Error = "SimpleLLMChatCLI.exe not found" };

            int timeout = job.TimeoutSeconds > 0 ? job.TimeoutSeconds : ScheduledTaskEngine.DefaultTimeoutSeconds;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--no-banners --session " + Quote(job.SessionName) + " " + Quote(job.Prompt),
                    WorkingDirectory = _baseDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    StringBuilder output = new StringBuilder();
                    StringBuilder error = new StringBuilder();
                    p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            output.AppendLine(e.Data);
                    };
                    // Drain stderr too: RedirectStandardError=true but never reading it
                    // deadlocks the child once >4KB of stderr is produced.
                    p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                            error.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    Stopwatch sw = Stopwatch.StartNew();
                    if (!p.WaitForExit(timeout * 1000))
                    {
                        try { p.Kill(); } catch { }
                        return new ScheduledTaskEngine.RunResult { Success = false, Error = "timed out after " + timeout + "s" };
                    }
                    sw.Stop();
                    int durationMs = (int)sw.ElapsedMilliseconds;

                    string text = output.ToString();
                    if (p.ExitCode == 0)
                        return new ScheduledTaskEngine.RunResult { Success = true, Output = text, DurationMs = durationMs };
                    string errText = error.ToString().Trim();
                    string detail = errText.Length > 0 ? errText : text;
                    return new ScheduledTaskEngine.RunResult
                    {
                        Success = false,
                        Error = "exit code " + p.ExitCode + ": " + ScheduledTaskEngine.MakeSnippet(detail),
                        DurationMs = durationMs
                    };
                }
            }
            catch (Exception ex)
            {
                return new ScheduledTaskEngine.RunResult { Success = false, Error = ex.Message };
            }
        }

        // Escapes for CommandLineToArgvW so embedded quotes/backslashes in a session
        // name or prompt can't corrupt the child's argv or inject flags.
        private static string Quote(string value)
        {
            string v = value ?? string.Empty;
            v = v.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + v + "\"";
        }

        public void Dispose()
        {
            _timer.Stop();
            _disposed = true;
        }
    }
}
