using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace WirelessBattery
{
    public class TrayContext : ApplicationContext
    {
        public readonly AppSettings Settings;
        private readonly BatteryService _service;
        private TrayManager _tray;
        private readonly Control _marshal = new Control();
        private readonly EventWaitHandle _showEvent;
        private readonly EventWaitHandle _settingsEvent;
        private readonly bool _singleInstanceOk;
        private readonly bool _startHidden;
        private System.Windows.Forms.Timer _timer;
        private System.Windows.Forms.Timer _trayRetry;
        private System.Windows.Forms.Timer _cleanupTimer;
        private System.Windows.Forms.Timer _recheckTimer;
        private System.Windows.Forms.Timer _modeTimer;
        private MainForm _form;
        private ScanResult _lastResult;
        private bool _busy;
        private bool _settingsOpen;
        // 上次生效的勾选集合（型号级键），用于在设置保存后判断谁被取消/重新勾选。
        private HashSet<string> _enabledSnapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _trayAttempts;
        private string _autoStartText = "";
        private volatile bool _exit;

        public bool Exiting { get { return _exit; } }
        public bool TrayAvailable { get { return _tray != null; } }

        public string TrayStatusText
        {
            get
            {
                if (_tray != null) return "托盘图标已就绪";
                if (Elevation.NeedsElevation()) return "托盘需要管理员权限，请退出后以管理员身份运行";
                return "托盘暂不可用";
            }
        }

        /// <summary>
        /// 状态栏里除托盘状态之外的两段附加信息：单实例保护降级提示 + 自启入口审计结论。
        /// 两者都为空时返回空串，由界面决定是否拼接。
        /// </summary>
        public string StatusExtraText
        {
            get
            {
                string text = _autoStartText;
                if (!_singleInstanceOk)
                {
                    text = string.IsNullOrEmpty(text) ? "未启用单实例保护" : "未启用单实例保护 · " + text;
                }
                return text;
            }
        }

        public TrayContext(bool startHidden, EventWaitHandle showEvent, EventWaitHandle settingsEvent, bool openSettings, bool singleInstanceOk)
        {
            _marshal.CreateControl();
            _showEvent = showEvent;
            _settingsEvent = settingsEvent;
            _singleInstanceOk = singleInstanceOk;
            _startHidden = startHidden;
            if (_showEvent != null || _settingsEvent != null)
            {
                // 一个常驻线程同时等“显示窗口”和“打开设置”两个命名事件，
                // 收到后一律回界面线程处理（不在这里碰任何控件）。
                WaitHandle[] handles = BuildWaitHandles();
                Thread waiter = new Thread(delegate ()
                {
                    while (!_exit)
                    {
                        int index;
                        try { index = WaitHandle.WaitAny(handles, 1000); }
                        catch { break; }
                        if (index == WaitHandle.WaitTimeout) continue;
                        try
                        {
                            if (_settingsEvent != null && index < handles.Length && handles[index] == _settingsEvent)
                                _marshal.BeginInvoke(new Action(ShowSettingsFromIpc));
                            else
                                _marshal.BeginInvoke(new Action(ShowMain));
                        }
                        catch { }
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();
            }
            Settings = AppSettings.Load();
            _service = new BatteryService(Settings);
            FillEnabledSnapshot();

            // 先用上次保存的电量把卡片/托盘画出来，避免首轮读取（尤其 MCHOSE
            // 休眠重试）期间卡片缺失。后台扫描完成后会原地刷新。
            _lastResult = Settings.BuildCachedResult();

            TrayProbe.CleanupStale();
            _cleanupTimer = new System.Windows.Forms.Timer();
            _cleanupTimer.Interval = 4000;
            _cleanupTimer.Tick += delegate
            {
                TrayProbe.CleanupStale();
                if (_cleanupTimer != null)
                {
                    _cleanupTimer.Stop();
                    _cleanupTimer.Dispose();
                    _cleanupTimer = null;
                }
            };
            _cleanupTimer.Start();

            TryInitTray();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(30, Settings.IntervalSeconds) * 1000;
            _timer.Tick += delegate { Refresh(); };
            _timer.Start();

            // 每次启动都后台核对一遍自启入口（计划任务/启动文件夹/vbs/注册表）是否仍
            // 指向当前 exe，不匹配就按现有优先级顺序静默重写；结果只呈现到状态栏。
            ThreadPool.QueueUserWorkItem(delegate { AuditAutoStart(); });

            if (!startHidden) ShowMain();
            if (_tray == null) StartTrayRetry();

            if (openSettings) ScheduleOpenSettings();

            Refresh();
        }

        private bool TryInitTray()
        {
            if (_tray != null) return true;
            if (_exit) return false;
            bool probe = false;
            try { probe = TrayProbe.Test(_marshal.Handle); }
            catch { }
            if (!probe) return false;
            try
            {
                _tray = new TrayManager(ShowMain, Refresh, ShowSettings, Exit);
                if (_lastResult != null) _tray.Update(_lastResult.Cards);
                return true;
            }
            catch
            {
                _tray = null;
                return false;
            }
        }

        private void StartTrayRetry()
        {
            if (_trayRetry != null || _exit) return;
            _trayAttempts = 0;
            _trayRetry = new System.Windows.Forms.Timer();
            _trayRetry.Interval = 1500;
            _trayRetry.Tick += delegate
            {
                _trayAttempts++;
                if (TryInitTray())
                {
                    _trayRetry.Stop();
                    _trayRetry.Dispose();
                    _trayRetry = null;
                    if (_lastResult != null && _tray != null) _tray.Update(_lastResult.Cards);
                    return;
                }
                if (_trayAttempts >= 20)
                {
                    _trayRetry.Stop();
                    _trayRetry.Dispose();
                    _trayRetry = null;
                    if (_startHidden && (_form == null || !_form.Visible))
                    {
                        ShowMain();
                        if (_form != null) _form.WindowState = FormWindowState.Minimized;
                    }
                    ShowStatus("系统托盘不可用，已最小化到任务栏；稍后会自动重试");
                }
            };
            _trayRetry.Start();
        }

        private WaitHandle[] BuildWaitHandles()
        {
            if (_showEvent != null && _settingsEvent != null)
                return new WaitHandle[] { _showEvent, _settingsEvent };
            if (_showEvent != null) return new WaitHandle[] { _showEvent };
            if (_settingsEvent != null) return new WaitHandle[] { _settingsEvent };
            return new WaitHandle[0];
        }

        /// <summary>
        /// 后台线程：核对自启入口是否仍指向当前 exe，必要时按现有优先级顺序静默重写。
        /// 异常一律吞掉（不得影响电量读取），结论回界面线程后再写进状态栏。
        /// </summary>
        private void AuditAutoStart()
        {
            AppSettings.AutoStartState state;
            try { state = Settings.AuditAutoStart(); }
            catch { state = AppSettings.AutoStartState.Failed; }
            try
            {
                if (_marshal.IsHandleCreated)
                    _marshal.BeginInvoke(new Action(delegate { ApplyAutoStartAudit(state); }));
            }
            catch { }
        }

        private void ApplyAutoStartAudit(AppSettings.AutoStartState state)
        {
            _autoStartText = DescribeAutoStart(state);
            if (_form != null && !_form.IsDisposed) _form.RefreshStatus();
        }

        private static string DescribeAutoStart(AppSettings.AutoStartState state)
        {
            switch (state)
            {
                case AppSettings.AutoStartState.Disabled: return "未开启开机自启";
                case AppSettings.AutoStartState.OkTask: return "自启正常（计划任务）";
                case AppSettings.AutoStartState.OkStartupFolder: return "自启正常（启动文件夹）";
                case AppSettings.AutoStartState.OkVbs: return "自启正常（VBS）";
                case AppSettings.AutoStartState.OkRun: return "自启正常（注册表）";
                case AppSettings.AutoStartState.RepairedTask: return "自启已修复（计划任务）";
                case AppSettings.AutoStartState.RepairedStartupFolder: return "自启已修复（启动文件夹）";
                case AppSettings.AutoStartState.RepairedVbs: return "自启已修复（VBS）";
                case AppSettings.AutoStartState.RepairedRun: return "自启已修复（注册表）";
                case AppSettings.AutoStartState.Failed: return "自启修复失败，可在设置里重试";
                default: return "";
            }
        }

        private void ScheduleOpenSettings()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 350;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                if (!_exit) ShowSettings();
            };
            t.Start();
        }

        public void ShowMain()
        {
            if (_exit) return;
            bool created = false;
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(this, Settings);
                created = true;
            }
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
            if (created && _lastResult != null) _form.UpdateCards(_lastResult);
        }

        /// <summary>
        /// 系统报告 HID/音频设备节点变化（插拔、配对）：清空型号级探测缓存，
        /// 让下一次刷新重新探测，而不是继续用旧的“未识别”结论。
        /// 由主窗体在界面线程调用。
        /// </summary>
        public void OnDeviceNodesChanged()
        {
            if (_exit) return;
            try { _service.InvalidateProbes(); }
            catch { }
        }

        public void Refresh()
        {
            if (_busy || _exit) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ScanResult result = null;
                string error = null;
                try { result = _service.Scan(); }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    if (_marshal.IsHandleCreated)
                        _marshal.BeginInvoke(new Action(delegate { Apply(result, error); }));
                    else
                        Apply(result, error);
                }
                catch { }
            });
        }

        private void Apply(ScanResult result, string error)
        {
            _busy = false;
            if (_exit) return;
            if (error != null)
            {
                if (_form != null && !_form.IsDisposed) _form.ShowStatus("读取失败: " + error);
                return;
            }
            if (result == null) return;
            // 竞态保护：扫描期间可能刚被取消勾选，这一轮结果里不能再出现它的卡片/托盘图标。
            List<DeviceCardModel> kept = new List<DeviceCardModel>();
            for (int i = 0; i < result.Cards.Count; i++)
            {
                DeviceCardModel c = result.Cards[i];
                if (c == null) continue;
                if (Settings.IsDeviceEnabled(c.DeviceKey)) kept.Add(c);
            }
            if (kept.Count != result.Cards.Count) result.Cards = kept;
            _lastResult = result;
            if (_tray != null)
            {
                try { _tray.Update(result.Cards); }
                catch { }
            }
            if (_form != null && !_form.IsDisposed) _form.UpdateCards(result);
            if (result.PendingDisconnect) ScheduleRecheck();
            if (result.RecheckAfterMs > 0) ScheduleModeRecheck(result.RecheckAfterMs);
        }

        private void ScheduleModeRecheck(int ms)
        {
            if (_modeTimer != null || _exit) return;
            _modeTimer = new System.Windows.Forms.Timer();
            _modeTimer.Interval = Math.Max(200, ms);
            _modeTimer.Tick += delegate
            {
                if (_modeTimer != null)
                {
                    _modeTimer.Stop();
                    _modeTimer.Dispose();
                    _modeTimer = null;
                }
                Refresh();
            };
            _modeTimer.Start();
        }

        private void ScheduleRecheck()
        {
            if (_recheckTimer != null || _exit) return;
            _recheckTimer = new System.Windows.Forms.Timer();
            _recheckTimer.Interval = 10500;
            _recheckTimer.Tick += delegate
            {
                if (_recheckTimer != null)
                {
                    _recheckTimer.Stop();
                    _recheckTimer.Dispose();
                    _recheckTimer = null;
                }
                Refresh();
            };
            _recheckTimer.Start();
        }

        /// <summary>
        /// 另一个实例带 --settings 启动时走这里：先回界面线程把主窗口显示出来，
        /// 再延后一帧打开设置页，避免和正在创建的窗口抢消息循环。
        /// </summary>
        private void ShowSettingsFromIpc()
        {
            if (_exit) return;
            ShowMain();
            ScheduleOpenSettings();
        }

        public void ShowSettings()
        {
            if (_settingsOpen) return;
            _settingsOpen = true;
            try
            {
                using (SettingsForm f = new SettingsForm(Settings, ApplySettings))
                {
                    if (_form != null && _form.Visible) f.ShowDialog(_form);
                    else f.ShowDialog();
                }
            }
            finally { _settingsOpen = false; }
        }

        public void ApplySettings()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Interval = Math.Max(30, Settings.IntervalSeconds) * 1000;
                if (Settings.IntervalSeconds > 0) _timer.Start();
            }
            if (_form != null && !_form.IsDisposed) _form.ApplySettings();
            SyncDeviceGating();
            Refresh();
        }

        private void FillEnabledSnapshot()
        {
            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> keys = Settings.GetDeviceKeys();
            for (int i = 0; i < keys.Count; i++)
            {
                if (Settings.IsDeviceEnabled(keys[i])) now.Add(keys[i]);
            }
            _enabledSnapshot = now;
        }

        /// <summary>
        /// 设置保存后立即生效：取消勾选马上裁剪卡片与托盘图标、暂停阶梯时钟；
        /// 重新勾选则把暂停时段补回计时基准，然后由 Refresh() 先定档再请求一次。
        /// </summary>
        private void SyncDeviceGating()
        {
            HashSet<string> now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> keys = Settings.GetDeviceKeys();
            for (int i = 0; i < keys.Count; i++)
            {
                if (Settings.IsDeviceEnabled(keys[i])) now.Add(keys[i]);
            }

            bool changed = false;
            foreach (string key in _enabledSnapshot)
            {
                if (!now.Contains(key))
                {
                    Settings.PauseLadder(key);
                    changed = true;
                }
            }
            foreach (string key in now)
            {
                if (!_enabledSnapshot.Contains(key))
                {
                    Settings.ResumeLadder(key);
                    changed = true;
                }
            }
            if (!changed) return;
            _enabledSnapshot = now;
            PruneDisabledCards();
        }

        private void PruneDisabledCards()
        {
            if (_lastResult == null) return;
            bool removed = false;
            List<DeviceCardModel> kept = new List<DeviceCardModel>();
            for (int i = 0; i < _lastResult.Cards.Count; i++)
            {
                DeviceCardModel c = _lastResult.Cards[i];
                if (c == null) continue;
                if (Settings.IsDeviceEnabled(c.DeviceKey)) kept.Add(c);
                else removed = true;
            }
            if (!removed) return;
            ScanResult pruned = new ScanResult();
            pruned.Cards = kept;
            pruned.Interfaces = _lastResult.Interfaces;
            pruned.Time = _lastResult.Time;
            Apply(pruned, null);
        }

        public void SetEstimateEnabled(string key, bool enabled)
        {
            EstimateConfig cfg = Settings.GetEstimateConfig(key);
            cfg.Enabled = enabled;
            Settings.SetEstimateConfig(key, cfg);
            Settings.Save();
            Refresh();
        }

        public void ShowEstimateSettings(string key, string title)
        {
            using (EstimateSettingsForm f = new EstimateSettingsForm(Settings, key, title, ApplySettings))
            {
                if (_form != null && _form.Visible) f.ShowDialog(_form);
                else f.ShowDialog();
            }
        }

        public void ShowGpwSettings(string key, string title)
        {
            using (GpwSettingsForm f = new GpwSettingsForm(Settings, key, title, ApplySettings))
            {
                if (_form != null && _form.Visible) f.ShowDialog(_form);
                else f.ShowDialog();
            }
        }

        public void ShowDeviceName(string key, string detectedTitle)
        {
            // 已经改过名的设备，卡片标题就是自定义名；此时提示里的“默认名称”要回退到型号默认名。
            string fallback = Settings.GetDeviceName(key).Length > 0 ? AppSettings.DeviceTitle(key) : detectedTitle;
            using (DeviceNameForm f = new DeviceNameForm(Settings, key, fallback, OnDeviceRenamed))
            {
                if (_form != null && _form.Visible) f.ShowDialog(_form);
                else f.ShowDialog();
            }
        }

        private void OnDeviceRenamed()
        {
            // 设置页的勾选列表也要跟着改名：先刷列表，再重建卡片。
            try
            {
                if (_form != null && !_form.IsDisposed) _form.ApplySettings();
            }
            catch (Exception) { }
            Refresh();
        }

        public void OnMainFormClosed()
        {
            if (!_exit && !Settings.CloseToTray) Exit();
        }

        public void Exit()
        {
            if (_exit) return;
            _exit = true;
            try { if (_showEvent != null) _showEvent.Set(); } catch { }
            try { if (_timer != null) _timer.Stop(); } catch { }
            try { if (_trayRetry != null) { _trayRetry.Stop(); _trayRetry.Dispose(); _trayRetry = null; } } catch { }
            try { if (_cleanupTimer != null) { _cleanupTimer.Stop(); _cleanupTimer.Dispose(); _cleanupTimer = null; } } catch { }
            try { if (_recheckTimer != null) { _recheckTimer.Stop(); _recheckTimer.Dispose(); _recheckTimer = null; } } catch { }
            try { if (_modeTimer != null) { _modeTimer.Stop(); _modeTimer.Dispose(); _modeTimer = null; } } catch { }
            try { if (_tray != null) _tray.Dispose(); } catch { }
            try { _service.StopLinkMonitor(); } catch { }
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Close();
                    _form.Dispose();
                }
            }
            catch { }
            try { _marshal.Dispose(); } catch { }
            ExitThread();
        }

        public void ShowStatus(string text)
        {
            if (_form != null && !_form.IsDisposed) _form.ShowStatus(text);
        }
    }
}
