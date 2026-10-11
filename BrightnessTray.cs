// BrightnessTray — управление яркостью монитора через DDC/CI.
// Иконка в трее + окно с ползунком. Автозапуск через HKCU Run.
// Совместимо с C# 5 (встроенный компилятор .NET Framework).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightnessTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\BrightnessTray_4F2A", out createdNew))
            {
                if (!createdNew) return; // уже запущено
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm form = new MainForm();
                form.InitializeTray();
                Application.Run();
                GC.KeepAlive(mutex);
            }
        }
    }

    // ---------- DDC/CI interop ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szDescription;
    }

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    internal static class Native
    {
        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

        [DllImport("dxva2.dll")]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll")]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll")]
        internal static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll")]
        internal static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint min, ref uint cur, ref uint max);

        [DllImport("dxva2.dll")]
        internal static extern bool SetMonitorBrightness(IntPtr hMonitor, uint value);
    }

    internal class MonitorItem
    {
        public IntPtr Handle;
        public string Name;
        public uint Min, Max;
        public bool Supports;

        public int Current
        {
            get
            {
                if (!Supports) return -1;
                uint min = 0, cur = 0, max = 0;
                if (!Native.GetMonitorBrightness(Handle, ref min, ref cur, ref max)) return -1;
                Min = min; Max = max;
                if (max <= min) return -1;
                int pct = (int)Math.Round((cur - min) * 100.0 / (max - min));
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                return pct;
            }
        }

        public bool SetBrightness(int percent)
        {
            if (!Supports || Max <= Min) return false;
            int value = (int)Math.Round(Min + (Max - Min) * percent / 100.0);
            if (value < Min) value = (int)Min;
            if (value > Max) value = (int)Max;
            return Native.SetMonitorBrightness(Handle, (uint)value);
        }

        public void Close()
        {
            if (Handle != IntPtr.Zero) Native.DestroyPhysicalMonitor(Handle);
            Handle = IntPtr.Zero;
        }

        public override string ToString()
        {
            return Name + (Supports ? "" : " (без DDC/CI)");
        }
    }

    internal static class Monitors
    {
        public static List<MonitorItem> Enumerate()
        {
            List<MonitorItem> list = new List<MonitorItem>();
            MonitorEnumProc proc = delegate(IntPtr hMon, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                uint count = 0;
                if (Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref count) && count > 0)
                {
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[count];
                    if (Native.GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            bool dup = false;
                            foreach (MonitorItem existing in list)
                                if (existing.Handle == arr[i].hPhysicalMonitor) dup = true;
                            if (dup) continue;

                            MonitorItem mi = new MonitorItem();
                            mi.Handle = arr[i].hPhysicalMonitor;
                            mi.Name = string.IsNullOrEmpty(arr[i].szDescription)
                                ? "Монитор " + (list.Count + 1)
                                : arr[i].szDescription;
                            uint min = 0, cur = 0, max = 0;
                            mi.Supports = Native.GetMonitorBrightness(mi.Handle, ref min, ref cur, ref max);
                            mi.Min = min;
                            mi.Max = max;
                            list.Add(mi);
                        }
                    }
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            return list;
        }
    }

    // ---------- Настройки ----------

    internal static class Settings
    {
        public static bool RestoreOnStartup = true;
        public static Dictionary<string, int> Saved = new Dictionary<string, int>();

        private static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightnessTray"); }
        }

        private static string FilePath
        {
            get { return Path.Combine(Dir, "settings.txt"); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.StartsWith("restore=", StringComparison.OrdinalIgnoreCase))
                    {
                        RestoreOnStartup = line.Substring(8).Trim() == "1";
                    }
                    else if (line.StartsWith("b|"))
                    {
                        string[] parts = line.Split(new char[] { '|' }, 3);
                        int v;
                        if (parts.Length == 3 && int.TryParse(parts[2], out v) && v >= 0 && v <= 100)
                            Saved[parts[1]] = v;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("restore=" + (RestoreOnStartup ? "1" : "0"));
                foreach (KeyValuePair<string, int> kv in Saved)
                    sb.AppendLine("b|" + kv.Key + "|" + kv.Value);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }

    // ---------- Автозапуск ----------

    internal static class Autostart
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "BrightnessTray";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        public static void EnsureEnabled()
        {
            try { if (!IsEnabled()) SetEnabled(true); }
            catch { }
        }
    }

    // ---------- Главная форма ----------

    internal class MainForm : Form
    {
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miOpen, _miAutostart, _miRestore, _miExit;
        private ComboBox _combo;
        private TrackBar _slider;
        private NumericUpDown _num;
        private CheckBox _chkAll;
        private Label _lblStatus;
        private System.Windows.Forms.Timer _applyTimer;

        private List<MonitorItem> _monitors = new List<MonitorItem>();
        private bool _updating;   // подавление событий при программной установке
        private bool _exiting;
        private bool _applying;

        public MainForm()
        {
            BuildUi();
            BuildMenu();
            Settings.Load();

            Autostart.EnsureEnabled();
            _miAutostart.Checked = Autostart.IsEnabled();
            _miRestore.Checked = Settings.RestoreOnStartup;

            StartRestoreIfNeeded();
        }

        public void InitializeTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = CreateIcon();
            _tray.Text = "Яркость экрана";
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleWindow();
            };
            _tray.Visible = true;
        }

        // ---------- UI ----------

        private void BuildUi()
        {
            Text = "Яркость экрана";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 120);
            Font = new Font("Segoe UI", 9F);

            _combo = new ComboBox();
            _combo.DropDownStyle = ComboBoxStyle.DropDownList;
            _combo.SetBounds(12, 12, 336, 23);
            _combo.SelectedIndexChanged += OnComboChanged;
            Controls.Add(_combo);

            _slider = new TrackBar();
            _slider.Minimum = 0;
            _slider.Maximum = 100;
            _slider.TickFrequency = 10;
            _slider.SmallChange = 1;
            _slider.LargeChange = 5;
            _slider.SetBounds(6, 40, 256, 45);
            _slider.ValueChanged += OnSliderChanged;
            Controls.Add(_slider);

            _num = new NumericUpDown();
            _num.Minimum = 0;
            _num.Maximum = 100;
            _num.SetBounds(268, 48, 80, 23);
            _num.ValueChanged += OnNumChanged;
            Controls.Add(_num);

            _chkAll = new CheckBox();
            _chkAll.Text = "Применять ко всем мониторам";
            _chkAll.SetBounds(12, 90, 300, 24);
            _chkAll.Visible = false;
            Controls.Add(_chkAll);

            _lblStatus = new Label();
            _lblStatus.SetBounds(12, 92, 336, 20);
            _lblStatus.ForeColor = SystemColors.GrayText;
            Controls.Add(_lblStatus);

            _applyTimer = new System.Windows.Forms.Timer();
            _applyTimer.Interval = 350; // защита от спама DDC/CI при перетаскивании
            _applyTimer.Tick += delegate { _applyTimer.Stop(); ApplyBrightness(_slider.Value); };
        }

        private void BuildMenu()
        {
            _miOpen = new ToolStripMenuItem("Открыть");
            _miOpen.Click += delegate { ShowWindow(); };

            _miAutostart = new ToolStripMenuItem("Запускать с Windows");
            _miAutostart.Click += delegate
            {
                bool on = !_miAutostart.Checked;
                try
                {
                    Autostart.SetEnabled(on);
                    _miAutostart.Checked = on;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось изменить автозапуск: " + ex.Message, Text,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            _miRestore = new ToolStripMenuItem("Восстанавливать яркость при запуске");
            _miRestore.Click += delegate
            {
                _miRestore.Checked = !_miRestore.Checked;
                Settings.RestoreOnStartup = _miRestore.Checked;
                Settings.Save();
            };

            _miExit = new ToolStripMenuItem("Выход");
            _miExit.Click += delegate { ExitApp(); };

            _menu = new ContextMenuStrip();
            _menu.Items.AddRange(new ToolStripItem[]
            {
                _miOpen,
                new ToolStripSeparator(),
                _miAutostart,
                _miRestore,
                new ToolStripSeparator(),
                _miExit
            });
        }

        private Icon CreateIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.Orange, 3f))
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4.0;
                        float x1 = 16 + (float)(Math.Cos(a) * 10);
                        float y1 = 16 + (float)(Math.Sin(a) * 10);
                        float x2 = 16 + (float)(Math.Cos(a) * 14);
                        float y2 = 16 + (float)(Math.Sin(a) * 14);
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
                using (Brush b = new SolidBrush(Color.Gold)) g.FillEllipse(b, 8, 8, 16, 16);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ---------- Окно ----------

        private void ToggleWindow()
        {
            if (Visible) Hide(); else ShowWindow();
        }

        private void ShowWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            RefreshMonitors();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void ExitApp()
        {
            _exiting = true;
            Settings.Save();
            CloseHandles();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }

        // ---------- Мониторы ----------

        private MonitorItem SelectedMonitor()
        {
            int idx = _combo.SelectedIndex;
            if (idx >= 0 && idx < _monitors.Count) return _monitors[idx];
            return null;
        }

        private void RefreshMonitors()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<MonitorItem> fresh = Monitors.Enumerate();
                BeginInvoke((MethodInvoker)delegate
                {
                    CloseHandles();
                    _monitors = fresh;

                    _updating = true;
                    _combo.Items.Clear();
                    foreach (MonitorItem m in _monitors) _combo.Items.Add(m);

                    bool multi = _monitors.Count > 1;
                    _chkAll.Visible = multi;
                    if (multi)
                    {
                        _lblStatus.SetBounds(12, 118, 336, 20);
                        ClientSize = new Size(360, 146);
                    }
                    else
                    {
                        _lblStatus.SetBounds(12, 92, 336, 20);
                        ClientSize = new Size(360, 120);
                    }
                    _updating = false;

                    if (_monitors.Count > 0) _combo.SelectedIndex = 0;
                    else
                    {
                        _lblStatus.Text = "Мониторы не обнаружены";
                        SetControlsSilent(0, false);
                    }
                });
            });
        }

        private void CloseHandles()
        {
            foreach (MonitorItem m in _monitors) m.Close();
            _monitors = new List<MonitorItem>();
        }

        // ---------- События контролов ----------

        private void SetControlsSilent(int v, bool enabled)
        {
            _updating = true;
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            _slider.Value = v;
            _num.Value = v;
            _slider.Enabled = enabled;
            _num.Enabled = enabled;
            _updating = false;
        }

        private void OnComboChanged(object sender, EventArgs e)
        {
            MonitorItem m = SelectedMonitor();
            if (m == null) return;

            if (!m.Supports)
            {
                SetControlsSilent(0, false);
                _lblStatus.Text = "Монитор не поддерживает DDC/CI";
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                int cur = m.Current; // медленный DDC/CI-запрос — в фоне
                BeginInvoke((MethodInvoker)delegate
                {
                    if (cur < 0)
                    {
                        SetControlsSilent(0, false);
                        _lblStatus.Text = "Не удалось получить яркость";
                        return;
                    }
                    SetControlsSilent(cur, true);
                    _lblStatus.Text = string.Format("Текущая яркость: {0}%", cur);
                    _tray.Text = string.Format("Яркость: {0}%", cur);
                });
            });
        }

        private void OnSliderChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            _updating = true;
            _num.Value = _slider.Value;
            _updating = false;
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        private void OnNumChanged(object sender, EventArgs e)
        {
            if (_updating) return;
            _updating = true;
            _slider.Value = (int)_num.Value;
            _updating = false;
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        // ---------- Установка яркости ----------

        private void ApplyBrightness(int value)
        {
            if (_applying)
            {
                _applyTimer.Stop();
                _applyTimer.Start();
                return;
            }

            List<MonitorItem> targets = new List<MonitorItem>();
            bool all = _chkAll.Visible && _chkAll.Checked;
            if (all)
            {
                foreach (MonitorItem m in _monitors)
                    if (m.Supports) targets.Add(m);
            }
            else
            {
                MonitorItem sel = SelectedMonitor();
                if (sel != null && sel.Supports) targets.Add(sel);
            }

            if (targets.Count == 0)
            {
                _lblStatus.Text = "Нет мониторов с поддержкой DDC/CI";
                return;
            }

            _applying = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool okAll = true;
                foreach (MonitorItem m in targets)
                {
                    if (m.SetBrightness(value)) Settings.Saved[m.Name] = value;
                    else okAll = false;
                }
                Settings.Save();
                BeginInvoke((MethodInvoker)delegate
                {
                    _applying = false;
                    if (okAll)
                    {
                        _lblStatus.Text = string.Format("Установлено: {0}%", value);
                        _tray.Text = string.Format("Яркость: {0}%", value);
                    }
                    else
                    {
                        _lblStatus.Text = "Ошибка установки яркости";
                    }
                });
            });
        }

        // ---------- Восстановление при старте ----------

        private void StartRestoreIfNeeded()
        {
            if (!Settings.RestoreOnStartup || Settings.Saved.Count == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(attempt == 0 ? 5000 : 3000); // ждём инициализации мониторов
                    List<MonitorItem> list = Monitors.Enumerate();
                    Dictionary<string, MonitorItem> byName = new Dictionary<string, MonitorItem>();
                    foreach (MonitorItem m in list)
                        if (m.Supports && !byName.ContainsKey(m.Name)) byName[m.Name] = m;

                    bool allOk = true;
                    int matched = 0;
                    foreach (KeyValuePair<string, int> kv in Settings.Saved)
                    {
                        MonitorItem m;
                        if (byName.TryGetValue(kv.Key, out m))
                        {
                            matched++;
                            if (!m.SetBrightness(kv.Value)) allOk = false;
                        }
                    }
                    foreach (MonitorItem m in list) m.Close();

                    if (matched > 0 && allOk) break;
                }
            });
        }
    }
}
