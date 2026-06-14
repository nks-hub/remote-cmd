using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace RemoteCmd.Client48
{
    /// <summary>Windows Service host wrapping the polling loop on a worker thread.</summary>
    internal sealed class ClientService : ServiceBase
    {
        private readonly ClientConfig _config;
        private readonly string _logPath;
        private CancellationTokenSource _cts;
        private Thread _worker;
        private readonly object _logLock = new object();

        public ClientService(ClientConfig config)
        {
            _config = config;
            ServiceName = "RemoteCmdClient";
            CanStop = true;
            CanShutdown = true;

            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteCmd");
            try { Directory.CreateDirectory(logDir); } catch { }
            _logPath = Path.Combine(logDir, "client48-service.log");
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();
            _worker = new Thread(() =>
            {
                try { PollLoop.Run(_config, Log, _cts.Token); }
                catch (Exception ex) { Log("[FATAL] " + ex.Message); }
            })
            { IsBackground = true };
            _worker.Start();
        }

        protected override void OnStop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _worker?.Join(5000); } catch { }
        }

        private void Log(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message;
            lock (_logLock)
            {
                try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
            }
        }

        public static void RunAsService(ClientConfig config)
            => Run(new ClientService(config));
    }
}
