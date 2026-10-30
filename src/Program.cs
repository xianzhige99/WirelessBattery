using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WirelessBattery
{
    static class Program
    {
        private const string MutexName = "WirelessBattery_SingleInstance_v2";
        private const string ShowEventName = "WirelessBattery_Show_v2";
        private const string SettingsEventName = "WirelessBattery_Settings_v2";

        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;
        private static EventWaitHandle _settingsEvent;

        // 命名互斥对象创建/打开失败（权限倒挂、访问控制）时置 false：
        // 不崩溃、也不当成“已有实例”，照常启动并在状态栏提示未启用单实例保护。
        private static bool _singleInstanceOk = true;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch { }

            // 必须在创建任何窗口/控件句柄之前完成 WinForms 初始化，
            // 否则 SetUnhandledExceptionMode 会抛异常，进程会静默退出。
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                WriteCrash(ex == null ? "unknown" : ex.GetType().FullName + " " + ex.Message + "\r\n" + ex.StackTrace);
            };
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                WriteCrash("UI " + e.Exception.GetType().FullName + " " + e.Exception.Message + "\r\n" + e.Exception.StackTrace);
            };

            bool once = HasArg(args, "--once");
            if (once)
            {
                RunOnce();
                return;
            }

            bool startHidden = HasArg(args, "--tray") || HasArg(args, "--minimized");
            bool openSettings = HasArg(args, "--settings");
            bool created = false;
            try
            {
                _mutex = new Mutex(true, MutexName, out created);
            }
            catch (Exception)
            {
                // 无法创建/打开命名互斥对象：放弃单实例保护继续启动，
                // 由状态栏提示“未启用单实例保护”，不弹窗、不崩溃。
                _mutex = null;
                created = true;
                _singleInstanceOk = false;
            }
            if (_mutex != null && !created)
            {
                // 已有实例（可能是旧版普通权限实例）：把这次的命令行意图转发过去，
                // 由它在界面线程显示窗口或打开设置页。不弹 UAC，也不按窗口标题找窗口。
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
                ForwardToExistingInstance(openSettings);
                return;
            }
            _showEvent = OpenOrCreateEvent(ShowEventName);
            _settingsEvent = OpenOrCreateEvent(SettingsEventName);

            // Explorer 若以管理员权限运行，而本程序是普通权限，UIPI 会阻止
            // Shell_NotifyIcon 注册托盘图标。此时释放单实例锁，用 runas 启动
            // 管理员实例；取消 UAC 则重新拿回锁，继续以普通权限运行。
            if (!HasArg(args, "--elevated"))
            {
                bool trayWorks = false;
                try
                {
                    using (Control probe = new Control())
                    {
                        probe.CreateControl();
                        trayWorks = TrayProbe.Test(probe.Handle);
                    }
                }
                catch { }
                if (!trayWorks && Elevation.NeedsElevation())
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                    try { _mutex.Dispose(); } catch { }
                    _mutex = null;
                    if (Elevation.RelaunchElevated(args))
                    {
                        CloseInstanceHandles();
                        return;
                    }

                    created = false;
                    try { _mutex = new Mutex(true, MutexName, out created); }
                    catch { _mutex = null; created = true; _singleInstanceOk = false; }
                    if (_mutex != null && !created)
                    {
                        ForwardToExistingInstance(openSettings);
                        return;
                    }
                }
            }

            // 不再按进程名强杀同名进程：多开已由上面的 v2 命名互斥挡住，
            // 强杀会误伤用户另存的调试副本，且违背“不粗暴终止进程”。

            try
            {
                AppSettings.EnsureLauncher();
                string link = Path.Combine(Application.StartupPath, "打开设备电量.lnk");
                AppSettings.CreateOpenShortcut(link);
            }
            catch { }

            try
            {
                Application.Run(new TrayContext(startHidden, _showEvent, _settingsEvent, openSettings, _singleInstanceOk));
            }
            catch (Exception ex)
            {
                WriteCrash("RUN " + ex.GetType().FullName + " " + ex.Message + "\r\n" + ex.StackTrace);
            }
            finally
            {
                try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
                try { if (_mutex != null) _mutex.Dispose(); } catch { }
                CloseInstanceHandles();
            }
        }

        private static EventWaitHandle OpenOrCreateEvent(string name)
        {
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, name); }
            catch { return null; }
        }

        /// <summary>
        /// 把本次启动的命令行意图交给已有实例：--settings 通知它打开设置页，
        /// 其余情况只唤醒窗口。进程间协作只靠命名事件，不按窗口标题找窗口。
        /// </summary>
        private static void ForwardToExistingInstance(bool openSettings)
        {
            if (openSettings && SignalEvent(SettingsEventName)) return;
            if (SignalEvent(ShowEventName)) return;

            // 两个事件都发不出去（典型场景：主实例以管理员运行、本实例是普通权限，
            // 或事件对象被策略挡住）。此时不再静默退出，否则用户看到的是“双击没反应”。
            try
            {
                MessageBox.Show(
                    "设备电量已经在运行，但无法唤出它的窗口。\r\n\r\n" +
                    "如果它是以管理员身份运行的，请从任务栏托盘图标打开；或先退出它再启动。",
                    "设备电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        private static bool SignalEvent(string name)
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(name))
                {
                    ev.Set();
                    return true;
                }
            }
            catch { return false; }
        }

        private static void CloseInstanceHandles()
        {
            try { if (_showEvent != null) _showEvent.Dispose(); } catch { }
            try { if (_settingsEvent != null) _settingsEvent.Dispose(); } catch { }
            _showEvent = null;
            _settingsEvent = null;
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 崩溃转储仅用于定位问题：写在 exe 同目录，超过 64KB 先清空再写，
        /// 避免长期运行时无限增长（README 的“不写日志”指的是不写行为日志）。
        /// </summary>
        private static void WriteCrash(string text)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 64 * 1024)
                {
                    try { fi.Delete(); } catch { }
                }
                File.AppendAllText(path, DateTime.Now + " " + text + "\r\n");
            }
            catch { }
        }

        private static void RunOnce()
        {
            List<string> logs = new List<string>();
            AppSettings settings = AppSettings.Load();
            BatteryService service = new BatteryService(settings);
            service.Log = delegate (string s) { logs.Add(s); };
            ScanResult result = service.Scan();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SCAN " + result.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("INTERFACES " + result.Interfaces.Count);
            sb.AppendLine("CARDS " + result.Cards.Count);
            for (int i = 0; i < result.Cards.Count; i++)
            {
                DeviceCardModel c = result.Cards[i];
                sb.AppendLine(string.Format("CARD {0} | mode={1} | connected={2} | estimated={3} | {4} | {5} | invalid={6} | hint={7} | {8}",
                    c.Title, c.Mode, c.Connected, c.IsEstimated, c.LevelText, c.StatusLine, c.EstimateInvalid, c.EstimateHint, c.Detail));
            }
            sb.AppendLine("BT");
            try
            {
                List<BtBatteryInfo> bt = BluetoothBattery.Scan();
                for (int i = 0; i < bt.Count; i++)
                {
                    BtBatteryInfo b = bt[i];
                    sb.AppendLine(string.Format("BT {0} | hasProperty={1} | level={2} | {3}", b.Name, b.HasProperty, b.Level.HasValue ? b.Level.Value.ToString() : "-", b.Detail));
                }
            }
            catch (Exception ex) { sb.AppendLine("BT error " + ex.Message); }
            sb.AppendLine("LOG");
            for (int i = 0; i < logs.Count; i++) sb.AppendLine(logs[i]);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan.txt"), sb.ToString(), Encoding.UTF8);
        }
    }
}
