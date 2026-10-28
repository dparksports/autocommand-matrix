using System;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace AutoCommand.Matrix
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private MonitorEngine _engine;
        private MainWindow _window;
        private TrayIcon _tray;
        private ConfigWatcher _configWatcher;
        private MatrixRainConfig _cfg;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // The cores read/write small state files relative to the exe
            // (ignored_processes.txt) — pin the working directory regardless
            // of where the shortcut or shell launched from.
            Environment.CurrentDirectory = AppContext.BaseDirectory;

            _singleInstance = new Mutex(true, @"Local\AutoCommand.Matrix.SingleInstance", out bool first);
            if (!first)
            {
                Shutdown();
                return;
            }

            _cfg = MatrixRainConfig.LoadOrDefault();

            _engine = new MonitorEngine(_cfg.BlockPollSeconds);
            _window = new MainWindow(_cfg, _engine);
            _window.Show();

            _tray = new TrayIcon(() => _cfg, _window);
            _engine.OnStatus += _tray.Status;
            _engine.Start();

            // Edit matrix_rain.json, save — the rain re-skins itself.
            _configWatcher = new ConfigWatcher();
            _configWatcher.Changed += cfg =>
            {
                _cfg = cfg;
                _engine.UpdateBlockPoll(cfg.BlockPollSeconds);
                _window.ApplyNewConfig(cfg);
                _tray.SyncFrom(cfg);
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _configWatcher?.Dispose();
            _tray?.Dispose();
            _engine?.Dispose();
            _singleInstance?.ReleaseMutex();
            base.OnExit(e);
        }
    }
}
