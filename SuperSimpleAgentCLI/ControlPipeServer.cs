using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace SuperSimpleAgentCLI
{
    /// <summary>
    /// Listens on SuperSimpleAgent.Control.{pid} for the GUI's stop requests.
    /// A background thread accepts connections (re-accepting after a disconnect)
    /// and raises AbortSupport for each STOP / STOPTOOL command line.
    /// </summary>
    internal sealed class ControlPipeServer : IDisposable
    {
        private readonly string _pipeName;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Thread _thread;
        private volatile bool _running;

        public ControlPipeServer()
        {
            _pipeName = ControlPipe.GetPipeName(Process.GetCurrentProcess().Id);
        }

        public void Start()
        {
            if (_thread != null)
                return;

            _running = true;
            _thread = new Thread(ServerLoop);
            _thread.IsBackground = true;
            _thread.Name = "ControlPipeServer";
            _thread.Start();
        }

        private void ServerLoop()
        {
            while (_running)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte))
                    {
                        pipe.WaitForConnection();
                        if (!_running)
                            return;

                        using (var reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string line;
                            while (_running && (line = reader.ReadLine()) != null && line.Length > 0)
                            {
                                string command = line.Trim();
                                if (string.Equals(command, ControlPipe.StopCommand, StringComparison.OrdinalIgnoreCase))
                                    AbortSupport.RequestAbort();
                                else if (string.Equals(command, ControlPipe.StopToolCommand, StringComparison.OrdinalIgnoreCase))
                                    AbortSupport.RequestToolAbort();
                            }
                        }
                    }
                }
                catch
                {
                    if (_running)
                        Thread.Sleep(50);
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _stop.Set();

            try
            {
                using (var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out))
                {
                    client.Connect(100);
                }
            }
            catch { }

            if (_thread != null && _thread.IsAlive)
            {
                try { _thread.Join(100); } catch { }
            }
            _thread = null;

            try { _stop.Close(); } catch { }
        }
    }
}