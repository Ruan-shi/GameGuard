﻿// ============================================================
//  GameGuard - 游戏时长守卫（独立 C# 版本）
//  单文件 WinForms 应用：托盘常驻 + 左键控制面板 + 时段/连续双模式
//  编译：csc /target:winexe /win32icon:guard.ico
//        /r:System.Windows.Forms.dll /r:System.Drawing.dll
//        /r:System.Web.Extensions.dll GameGuardApp.cs
// ============================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Reflection;

// ---- 产品信息（exe 属性中可见）----
[assembly: AssemblyTitle("GameGuard 游戏时长守卫")]
[assembly: AssemblyProduct("GameGuard")]
[assembly: AssemblyDescription("轻量 Windows 托盘工具：控制游戏时长与可玩时段")]
[assembly: AssemblyCompany("GameGuard Project")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace GameGuard
{
    // ==================== 配置 ====================
    public class GameEntry
    {
        public string Name = "";
        public string[] Processes = new string[0];
        public string[] PathFilter = new string[0];

        public GameEntry() { }
        public GameEntry(string name, string[] procs, string[] filters)
        {
            Name = name; Processes = procs; PathFilter = filters;
        }
    }

    public static class Cfg
    {
        public static List<GameEntry> Games = new List<GameEntry> {
            new GameEntry("影之刃零", new string[] { "PBZ", "8za_v2" }, new string[] { "PBZ" })
        };
        public static int PlayMinutes = 60;
        public static int ExtendMinutes = 5;
        public static int MaxExtend = 2;
        public static int PopupTimeoutSec = 60;
        public static int PollSeconds = 15;
        public static string GameTitle = "游戏";
        public static bool UseSchedule = true;
        public static string ScheduleStart = "18:00";
        public static string ScheduleEnd = "20:00";
        public static int ScheduleWarnMin = 3;
        public static List<int> ScheduleDays = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
        public static int OutsideCountdown = 5;
        public static bool ScheduleWithContinuous = true;
        public static string TempDate = "";
        public static string TempStart = "";
        public static string TempEnd = "";

        public static string ScriptDir = "";
        public static string LogPath = "";
        public static string ConfigPath = "";
        public static string PauseFlag = "";

        public static void Load()
        {
            if (!File.Exists(ConfigPath))
            {
                Log.Write("未发现 config.json，使用默认值并生成一份");
                Save();
                return;
            }
            try
            {
                var ser = new JavaScriptSerializer();
                var raw = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                if (raw == null) throw new Exception("empty json");

                bool b;
                int i;
                string s;

                if (TryGetBool(raw, "useSchedule", out b)) UseSchedule = b;
                if (TryGetBool(raw, "scheduleWithContinuous", out b)) ScheduleWithContinuous = b;
                if (TryGetInt(raw, "scheduleWarnMin", out i)) ScheduleWarnMin = i;
                if (TryGetInt(raw, "outsideCountdown", out i)) OutsideCountdown = i;
                if (TryGetInt(raw, "playMinutes", out i)) PlayMinutes = i;
                if (TryGetInt(raw, "extendMinutes", out i)) ExtendMinutes = i;
                if (TryGetInt(raw, "maxExtend", out i)) MaxExtend = i;
                if (TryGetStr(raw, "scheduleStart", out s)) ScheduleStart = s;
                if (TryGetStr(raw, "scheduleEnd", out s)) ScheduleEnd = s;
                if (TryGetStr(raw, "tempDate", out s)) TempDate = s;
                if (TryGetStr(raw, "tempStart", out s)) TempStart = s;
                if (TryGetStr(raw, "tempEnd", out s)) TempEnd = s;

                object days;
                if (raw.TryGetValue("scheduleDays", out days) && days is IEnumerable<object>)
                {
                    var list = new List<int>();
                    foreach (var d in (IEnumerable<object>)days)
                        list.Add(Convert.ToInt32(d));
                    ScheduleDays = list;
                }

                object gamesObj;
                if (raw.TryGetValue("games", out gamesObj) && gamesObj is IEnumerable<object>)
                {
                    var loaded = new List<GameEntry>();
                    foreach (var g in (IEnumerable<object>)gamesObj)
                    {
                        var gd = g as Dictionary<string, object>;
                        if (gd == null) continue;
                        var ge = new GameEntry();
                        object v;
                        if (gd.TryGetValue("name", out v)) ge.Name = Convert.ToString(v);
                        ge.Processes = GetStringArray(gd, "processes");
                        ge.PathFilter = GetStringArray(gd, "pathFilter");
                        if (!string.IsNullOrEmpty(ge.Name)) loaded.Add(ge);
                    }
                    if (loaded.Count > 0) Games = loaded;
                }

                Log.Write("已加载 config.json，当前模式：" + (UseSchedule ? "固定时段" : "连续时长"));
            }
            catch (Exception ex)
            {
                Log.Write("config.json 读取失败，回退默认值：" + ex.Message);
            }
        }

        private static string[] GetStringArray(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return new string[0];
            var list = new List<string>();
            var en = v as IEnumerable<object>;
            if (en != null)
            {
                foreach (var x in en) list.Add(Convert.ToString(x));
            }
            else
            {
                string one = Convert.ToString(v);
                if (!string.IsNullOrEmpty(one)) list.Add(one);
            }
            return list.ToArray();
        }

        private static bool TryGetBool(Dictionary<string, object> d, string key, out bool val)
        {
            val = false; object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) { val = (bool)v; return true; }
            return bool.TryParse(Convert.ToString(v), out val);
        }

        private static bool TryGetInt(Dictionary<string, object> d, string key, out int val)
        {
            val = 0; object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            try { val = Convert.ToInt32(v); return true; }
            catch { return false; }
        }

        private static bool TryGetStr(Dictionary<string, object> d, string key, out string val)
        {
            val = null; object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            val = Convert.ToString(v);
            return val.Length > 0;
        }

        public static void Save()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var games = new List<object>();
                foreach (var g in Games)
                {
                    var gd = new Dictionary<string, object>();
                    gd["name"] = g.Name;
                    gd["processes"] = g.Processes;
                    gd["pathFilter"] = g.PathFilter;
                    games.Add(gd);
                }
                var root = new Dictionary<string, object>();
                root["games"] = games;
                root["useSchedule"] = UseSchedule;
                root["scheduleStart"] = ScheduleStart;
                root["scheduleEnd"] = ScheduleEnd;
                root["scheduleWarnMin"] = ScheduleWarnMin;
                root["scheduleDays"] = ScheduleDays;
                root["outsideCountdown"] = OutsideCountdown;
                root["scheduleWithContinuous"] = ScheduleWithContinuous;
                root["playMinutes"] = PlayMinutes;
                root["extendMinutes"] = ExtendMinutes;
                root["maxExtend"] = MaxExtend;
                root["tempDate"] = TempDate;
                root["tempStart"] = TempStart;
                root["tempEnd"] = TempEnd;
                File.WriteAllText(ConfigPath, ser.Serialize(root), new UTF8Encoding(false));
                Log.Write("配置已保存到 config.json");
            }
            catch (Exception ex)
            {
                Log.Write("配置保存失败：" + ex.Message);
            }
        }
    }

    // ==================== 日志 ====================
    public static class Log
    {
        private static readonly object _lock = new object();
        public static void Write(string msg)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(Cfg.LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ==================== Win32 ====================
    public static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }

    // ==================== UI 基础（配色 / DPI 缩放 / 控件工厂）====================
    public static class Ui
    {
        public static double Scale = 1.0;

        public static readonly Color Primary = Color.FromArgb(198, 40, 40);
        public static readonly Color PrimaryHover = Color.FromArgb(170, 32, 32);
        public static readonly Color PrimaryDown = Color.FromArgb(144, 26, 26);
        public static readonly Color TextDark = Color.FromArgb(38, 38, 38);
        public static readonly Color SubText = Color.FromArgb(132, 132, 132);
        public static readonly Color Line = Color.FromArgb(228, 228, 228);
        public static readonly Color BtnBg = Color.FromArgb(246, 246, 246);
        public static readonly Color BtnHover = Color.FromArgb(234, 234, 234);
        public static readonly Color BtnDown = Color.FromArgb(222, 222, 222);
        public static readonly Color Border = Color.FromArgb(206, 206, 206);
        public static readonly Color Green = Color.FromArgb(46, 125, 50);

        private static Font Base(double size, bool bold)
        {
            return new Font("Microsoft YaHei UI", (float)size,
                bold ? FontStyle.Bold : FontStyle.Regular);
        }

        public static void InitScale()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    double d = g.DpiX / 96.0;
                    if (d >= 1.0) Scale = Math.Min(d, 3.0);
                }
            }
            catch { }
        }

        public static int U(double v) { return (int)Math.Round(v * Scale); }

        public static Form MakeForm(string title, int w, int h, bool noControlBox)
        {
            var f = new Form();
            f.Text = title;
            f.AutoScaleMode = AutoScaleMode.None;
            f.ClientSize = new Size(U(w), U(h));
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            if (noControlBox) f.ControlBox = false;
            f.TopMost = true;
            f.BackColor = Color.White;
            f.Font = Base(10, false);
            try { f.Icon = MakeGuardIcon(); } catch { }
            return f;
        }

        public static void AddTopBar(Form f, int w)
        {
            var p = new Panel();
            p.BackColor = Primary;
            p.Bounds = new Rectangle(0, 0, U(w), U(6));
            f.Controls.Add(p);
        }

        public static Label MakeLabel(string text, int x, int y, int w, int h,
            double size, bool bold, Color color, string align)
        {
            var l = new Label();
            l.Text = text;
            l.Font = Base(size, bold);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.TextAlign = (ContentAlignment)Enum.Parse(typeof(ContentAlignment), align);
            l.Bounds = new Rectangle(U(x), U(y), U(w), U(h));
            return l;
        }

        public static Button MakeButton(string text, int x, int y, int w, int h, bool primary, double size)
        {
            var b = new Button();
            b.Text = text;
            b.Font = Base(size, false);
            b.Bounds = new Rectangle(U(x), U(y), U(w), U(h));
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            if (primary)
            {
                b.BackColor = Primary;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = PrimaryHover;
                b.FlatAppearance.MouseDownBackColor = PrimaryDown;
            }
            else
            {
                b.BackColor = BtnBg;
                b.ForeColor = TextDark;
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.BorderColor = Border;
                b.FlatAppearance.MouseOverBackColor = BtnHover;
                b.FlatAppearance.MouseDownBackColor = BtnDown;
            }
            return b;
        }

        public static Panel MakeLine(int x, int y, int w)
        {
            var p = new Panel();
            p.BackColor = Line;
            int h = U(1); if (h < 1) h = 1;
            p.Bounds = new Rectangle(U(x), U(y), U(w), h);
            return p;
        }

        public static DateTimePicker MakeTimePicker(int x, int y, int w, string value)
        {
            var d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = "HH:mm";
            d.ShowUpDown = true;
            d.Font = Base(10, false);
            d.Bounds = new Rectangle(U(x), U(y), U(w), U(30));
            try { d.Value = DateTime.ParseExact(value, "HH:mm", null); } catch { }
            return d;
        }

        public static NumericUpDown MakeNumber(int x, int y, int w, int min, int max, int val)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            if (val < min) val = min;
            if (val > max) val = max;
            n.Value = val;
            n.Font = Base(10, false);
            n.TextAlign = HorizontalAlignment.Center;
            n.Bounds = new Rectangle(U(x), U(y), U(w), U(30));
            return n;
        }

        public static CheckBox MakeCheck(string text, int x, int y, int w, int h, bool isChecked)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Font = Base(10, false);
            c.BackColor = Color.Transparent;
            c.UseVisualStyleBackColor = true;
            c.AutoSize = false;
            c.Bounds = new Rectangle(U(x), U(y), U(w), U(h));
            c.Checked = isChecked;
            return c;
        }

        public static RadioButton MakeRadio(string text, int x, int y, int w, int h, bool isChecked)
        {
            var r = new RadioButton();
            r.Text = text;
            r.Font = Base(10, false);
            r.BackColor = Color.Transparent;
            r.UseVisualStyleBackColor = true;
            r.AutoSize = false;
            r.Bounds = new Rectangle(U(x), U(y), U(w), U(h));
            r.Checked = isChecked;
            return r;
        }

        public static Icon MakeGuardIcon()
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Point[] pts = new Point[] {
                    new Point(16, 2), new Point(29, 7), new Point(29, 17),
                    new Point(16, 30), new Point(3, 17), new Point(3, 7)
                };
                using (var brush = new SolidBrush(Primary))
                    g.FillPolygon(brush, pts);
                using (var pen = new Pen(Color.White, 2))
                    g.DrawPolygon(pen, pts);
                using (var font = new Font("Arial", 9, FontStyle.Bold))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    var rect = new RectangleF(0, 7, 32, 18);
                    g.DrawString("60", font, Brushes.White, rect, sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ==================== 引擎（游戏检测 / 时段判定 / 弹窗）====================
    public static class Engine
    {
        public static DateTime? DueTime = null;
        public static DateTime? RoundStart = null;
        public static int ExtendUsed = 0;
        public static string WarnKey = null;
        private static bool _inPoll = false;

        public class SchedState
        {
            public string State = "Disabled";   // Inside / Before / Closed / Disabled
            public DateTime Start;
            public DateTime End;
        }

        public static SchedState GetScheduleState()
        {
            var now = DateTime.Now;
            var outp = new SchedState();
            if (!Cfg.UseSchedule) { outp.State = "Disabled"; return outp; }

            string useStart = Cfg.ScheduleStart;
            string useEnd = Cfg.ScheduleEnd;
            bool dayOk = Cfg.ScheduleDays.Count == 0 ||
                         Cfg.ScheduleDays.Contains((int)now.DayOfWeek);

            string todayKey = now.ToString("yyyy-MM-dd");
            if (!string.IsNullOrEmpty(Cfg.TempDate) && Cfg.TempDate == todayKey &&
                !string.IsNullOrEmpty(Cfg.TempStart) && !string.IsNullOrEmpty(Cfg.TempEnd))
            {
                useStart = Cfg.TempStart;
                useEnd = Cfg.TempEnd;
                dayOk = true;
            }

            DateTime todayStart;
            DateTime todayEnd;
            try
            {
                todayStart = DateTime.ParseExact(now.ToString("yyyy-MM-dd") + " " + useStart, "yyyy-MM-dd HH:mm", null);
                todayEnd = DateTime.ParseExact(now.ToString("yyyy-MM-dd") + " " + useEnd, "yyyy-MM-dd HH:mm", null);
            }
            catch
            {
                outp.State = "Disabled";
                return outp;
            }
            if (todayEnd <= todayStart) todayEnd = todayEnd.AddDays(1);

            if (dayOk && now >= todayStart && now < todayEnd)
            {
                outp.State = "Inside"; outp.Start = todayStart; outp.End = todayEnd;
                return outp;
            }

            // 跨零点时段：昨天开始的窗口是否仍生效
            DateTime yStart = todayStart.AddDays(-1);
            DateTime yEnd = todayEnd.AddDays(-1);
            bool yDayOk = Cfg.ScheduleDays.Count == 0 ||
                          Cfg.ScheduleDays.Contains((int)yStart.DayOfWeek);
            if (yDayOk && now >= yStart && now < yEnd)
            {
                outp.State = "Inside"; outp.Start = yStart; outp.End = yEnd;
                return outp;
            }

            outp.Start = todayStart; outp.End = todayEnd;
            outp.State = (now < todayStart) ? "Before" : "Closed";
            return outp;
        }

        public static List<System.Diagnostics.Process> GetGameProcesses()
        {
            var found = new List<System.Diagnostics.Process>();
            var seen = new HashSet<int>();
            foreach (var g in Cfg.Games)
            {
                var procs = new List<System.Diagnostics.Process>();
                foreach (var pn in g.Processes)
                {
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    try
                    {
                        procs.AddRange(System.Diagnostics.Process.GetProcessesByName(pn.Trim()));
                    }
                    catch { }
                }
                if (procs.Count > 0 && g.PathFilter != null &&
                    g.PathFilter.Count(f => !string.IsNullOrEmpty(f)) > 0)
                {
                    var filters = g.PathFilter.Where(f => !string.IsNullOrEmpty(f)).ToArray();
                    procs = procs.Where(p =>
                    {
                        string fp = null;
                        try { fp = p.MainModule.FileName; } catch { fp = null; }
                        if (string.IsNullOrEmpty(fp)) return false;
                        foreach (var frag in filters)
                            if (fp.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        return false;
                    }).ToList();
                }
                foreach (var p in procs)
                    if (seen.Add(p.Id)) found.Add(p);
            }
            return found;
        }

        public static GameEntry GetActiveGame()
        {
            foreach (var g in Cfg.Games)
            {
                foreach (var pn in g.Processes)
                {
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName(pn.Trim()).Length > 0)
                            return g;
                    }
                    catch { }
                }
            }
            return null;
        }

        public static bool TestFullScreenWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                Win32.RECT rect;
                if (!Win32.GetWindowRect(handle, out rect)) return false;
                var screen = Screen.PrimaryScreen.Bounds;
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                return w >= screen.Width - 8 && h >= screen.Height - 8;
            }
            catch { return false; }
        }

        public static int StopGameNow(string reason)
        {
            var procs = GetGameProcesses();
            foreach (var p in procs)
            {
                try { p.Kill(); } catch { }
            }
            Log.Write("结束游戏，原因：" + reason);
            DueTime = null;
            RoundStart = null;
            ExtendUsed = 0;
            return procs.Count;
        }

        public static bool GetPausedState()
        {
            if (!File.Exists(Cfg.PauseFlag)) return false;
            try
            {
                string raw = File.ReadAllText(Cfg.PauseFlag).Trim();
                DateTime until;
                if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", null,
                    System.Globalization.DateTimeStyles.None, out until))
                {
                    if (DateTime.Now < until) return true;
                    try { File.Delete(Cfg.PauseFlag); } catch { }
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        public static void PauseOneHour()
        {
            File.WriteAllText(Cfg.PauseFlag, DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss"), Encoding.ASCII);
            Log.Write("用户暂停管控 1 小时");
        }

        public static void ResumeControl()
        {
            try { if (File.Exists(Cfg.PauseFlag)) File.Delete(Cfg.PauseFlag); } catch { }
            Log.Write("用户恢复管控");
        }

        public static void InvokeCurfew()
        {
            var procs = GetGameProcesses();
            if (procs.Count == 0)
            {
                DueTime = null; RoundStart = null; ExtendUsed = 0;
                return;
            }

            System.Diagnostics.Process main = null;
            foreach (var p in procs)
                if (p.MainWindowHandle != IntPtr.Zero) { main = p; break; }

            bool wasMinimized = false;
            if (main != null && TestFullScreenWindow(main.MainWindowHandle))
            {
                try
                {
                    Win32.ShowWindow(main.MainWindowHandle, 6);   // SW_MINIMIZE
                    wasMinimized = true;
                    System.Threading.Thread.Sleep(800);
                }
                catch { }
            }

            int played = 0;
            if (RoundStart != null)
                played = (int)Math.Floor((DateTime.Now - RoundStart.Value).TotalMinutes);
            int remainExtend = Math.Max(0, Cfg.MaxExtend - ExtendUsed);

            var activeGame = GetActiveGame();
            string gname = activeGame != null ? activeGame.Name : Cfg.GameTitle;

            Log.Write(string.Format("弹出提醒：{0} 已玩 {1} 分钟，剩余续期 {2} 次", gname, played, remainExtend));
            string choice = UiDialogs.ShowGuardDialog(played, remainExtend, gname);

            if (choice == "extend")
            {
                ExtendUsed++;
                DueTime = DateTime.Now.AddMinutes(Cfg.ExtendMinutes);
                int left = Math.Max(0, Cfg.MaxExtend - ExtendUsed);
                Log.Write(string.Format("用户续期 {0} 分钟，剩余 {1} 次，新截止 {2}",
                    Cfg.ExtendMinutes, left, DueTime.Value.ToString("HH:mm:ss")));
                if (wasMinimized && main != null)
                {
                    try
                    {
                        Win32.ShowWindow(main.MainWindowHandle, 9);   // SW_RESTORE
                        Win32.SetForegroundWindow(main.MainWindowHandle);
                    }
                    catch { }
                }
            }
            else
            {
                Log.Write("结束游戏，原因：" + (choice == "kill" ? "用户选择" : "弹窗超时未响应"));
                foreach (var p in procs) { try { p.Kill(); } catch { } }
                DueTime = null;
                RoundStart = null;
                ExtendUsed = 0;
            }
        }

        // ---------- 主轮询（托盘 Timer 驱动，UI 线程）----------
        public static void Poll(ToolStripItem statusItem)
        {
            if (_inPoll) return;
            _inPoll = true;
            try
            {
                Application.DoEvents();

                if (GetPausedState())
                {
                    statusItem.Text = "状态：已暂停";
                    return;
                }

                var procs = GetGameProcesses();
                var sched = GetScheduleState();

                if (Cfg.UseSchedule)
                {
                    // ============ 固定时段模式 ============
                    if (sched.State == "Inside")
                    {
                        int remainSec = (int)Math.Ceiling((sched.End - DateTime.Now).TotalSeconds);
                        bool nearEnd = remainSec <= Cfg.ScheduleWarnMin * 60;

                        if (procs.Count > 0 && Cfg.ScheduleWithContinuous && !nearEnd && DueTime == null)
                        {
                            RoundStart = DateTime.Now;
                            DueTime = DateTime.Now.AddMinutes(Cfg.PlayMinutes);
                            Log.Write("时段内叠加连续时长，本轮截止 " + DueTime.Value.ToString("HH:mm:ss"));
                        }

                        if (procs.Count > 0)
                        {
                            if (DueTime != null)
                            {
                                int leftSec = (int)Math.Ceiling((DueTime.Value - DateTime.Now).TotalSeconds);
                                if (leftSec < 0) leftSec = 0;
                                statusItem.Text = string.Format("状态：游玩中，连续剩 {0} 分 / 时段剩 {1} 分",
                                    leftSec / 60, remainSec / 60);
                            }
                            else
                            {
                                statusItem.Text = string.Format("状态：游玩中，可玩时段剩余 {0} 分 {1} 秒",
                                    remainSec / 60, remainSec % 60);
                            }
                        }
                        else
                        {
                            statusItem.Text = string.Format("状态：可玩时段内，剩余 {0} 分",
                                (int)Math.Ceiling(remainSec / 60.0));
                        }

                        if (remainSec <= 0)
                        {
                            if (procs.Count > 0) StopGameNow("可玩时段结束");
                            statusItem.Text = "状态：可玩时段已结束";
                        }
                        else if (!nearEnd)
                        {
                            if (procs.Count > 0 && DueTime != null && DateTime.Now >= DueTime.Value)
                                InvokeCurfew();
                        }
                        else
                        {
                            // 临近时段结束：只预警，到点强杀
                            string warnKey = sched.End.ToString("yyyyMMddHHmm");
                            if (WarnKey != warnKey)
                            {
                                WarnKey = warnKey;
                                int warnMin = (int)Math.Ceiling(remainSec / 60.0);
                                Log.Write(string.Format("可玩时间仅剩 {0} 分 {1} 秒，弹出预警",
                                    remainSec / 60, remainSec % 60));
                                UiDialogs.ShowNotice(
                                    string.Format("可游玩时间剩余 {0} 分钟", warnMin),
                                    sched.End.ToString("HH:mm") + " 到点将自动结束游戏",
                                    "可玩时段：" + sched.Start.ToString("HH:mm") + " - " + sched.End.ToString("HH:mm"),
                                    10, "提示将在 {0} 秒后自动关闭");
                            }
                        }

                        if (procs.Count == 0 && DueTime != null)
                        {
                            Log.Write("游戏已退出，连续计时重置");
                            DueTime = null; RoundStart = null; ExtendUsed = 0;
                        }
                    }
                    else
                    {
                        // ============ 时段外 ============
                        if (procs.Count > 0)
                        {
                            string realStart = sched.Start.ToString("HH:mm");
                            string realEnd = sched.End.ToString("HH:mm");
                            string msg;
                            if (sched.State == "Before")
                            {
                                msg = string.Format("可玩时段还没开始（{0} - {1}）", realStart, realEnd);
                                statusItem.Text = "状态：不在可玩时段，正在关闭游戏";
                            }
                            else
                            {
                                msg = string.Format("今天的可玩时段已结束（{0} - {1}）", realStart, realEnd);
                                statusItem.Text = "状态：可玩时段已结束，正在关闭游戏";
                            }
                            Log.Write(string.Format("时段外检测到游戏运行，{0} 秒后关闭", Cfg.OutsideCountdown));
                            UiDialogs.ShowNotice("现在不在可玩时段", msg,
                                "每天可玩：" + realStart + " - " + realEnd,
                                Cfg.OutsideCountdown, "{0} 秒后关闭游戏");
                            StopGameNow("不在可玩时段");
                        }
                        else if (sched.State == "Before")
                        {
                            statusItem.Text = "状态：未到可玩时段（" + sched.Start.ToString("HH:mm") + " 开始）";
                        }
                        else
                        {
                            statusItem.Text = "状态：今天可玩时段已结束";
                        }
                    }
                }
                else
                {
                    // ============ 连续时长模式 ============
                    if (procs.Count > 0)
                    {
                        if (DueTime == null)
                        {
                            RoundStart = DateTime.Now;
                            DueTime = DateTime.Now.AddMinutes(Cfg.PlayMinutes);
                            Log.Write("检测到游戏运行，本轮截止 " + DueTime.Value.ToString("HH:mm:ss"));
                        }
                        int leftSec = (int)Math.Ceiling((DueTime.Value - DateTime.Now).TotalSeconds);
                        statusItem.Text = string.Format("状态：游玩中，剩余 {0} 分 {1} 秒",
                            leftSec / 60, leftSec % 60);
                        if (DateTime.Now >= DueTime.Value)
                            InvokeCurfew();
                    }
                    else
                    {
                        if (DueTime != null)
                        {
                            Log.Write("游戏已退出，计时重置");
                            DueTime = null; RoundStart = null; ExtendUsed = 0;
                        }
                        statusItem.Text = "状态：等待游戏启动";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("循环异常：" + ex.Message);
            }
            finally
            {
                _inPoll = false;
            }
        }
    }

    // ==================== Steam 扫描 ====================
    public class SteamGame
    {
        public string Name = "";
        public string Exe = "";
        public string Path = "";
    }

    public static class SteamScan
    {
        private static readonly string[] JunkNames = new string[] {
            "crashpad_handler", "unitycrashhandler", "epicwebhelper",
            "unrealcrashhelper", "crashreporter", "vcredist", "dxsetup", "unins"
        };

        private static bool IsJunk(FileInfo f)
        {
            string n = Path.GetFileNameWithoutExtension(f.Name).ToLower();
            foreach (var j in JunkNames)
                if (n.Contains(j)) return true;
            return false;
        }

        private static void CollectExes(string dir, int depth, int maxDepth, List<FileInfo> acc)
        {
            if (depth > maxDepth) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.exe"))
                    acc.Add(new FileInfo(f));
                if (depth < maxDepth)
                    foreach (var d in Directory.GetDirectories(dir))
                        CollectExes(d, depth + 1, maxDepth, acc);
            }
            catch { }
        }

        private static string FindMainExe(string dir)
        {
            var all = new List<FileInfo>();
            CollectExes(dir, 0, 6, all);
            var cands = all.Where(f => !IsJunk(f)).OrderByDescending(f => f.Length).ToList();
            if (cands.Count == 0) return null;

            foreach (var f in cands)
                if (Path.GetDirectoryName(f.FullName).Equals(dir, StringComparison.OrdinalIgnoreCase))
                    return f.FullName;                               // 根目录
            foreach (var f in cands)
                if (Path.GetDirectoryName(Path.GetDirectoryName(f.FullName))
                    .Equals(dir, StringComparison.OrdinalIgnoreCase))
                    return f.FullName;                               // 一级子目录
            foreach (var f in cands)
                if (f.DirectoryName.IndexOf("Binaries\\Win64", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f.FullName;                               // UE Binaries\Win64
            foreach (var f in cands)
                if (f.DirectoryName.IndexOf("Binaries", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f.FullName;
            return cands[0].FullName;
        }

        public static List<SteamGame> GetSteamGames()
        {
            var result = new List<SteamGame>();
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string steamPath = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (key != null) steamPath = key.GetValue("SteamPath") as string;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(steamPath)) return result;

            var libs = new List<string> { steamPath.Replace('/', '\\') };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    var content = File.ReadAllText(vdf, Encoding.UTF8);
                    foreach (Match m in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                        libs.Add(p);
                    }
                }
                catch { }
            }

            foreach (var lib in libs)
            {
                string common = Path.Combine(lib, "steamapps", "common");
                if (!Directory.Exists(common)) continue;
                string[] dirs = null;
                try { dirs = Directory.GetDirectories(common); } catch { }
                if (dirs == null) continue;

                foreach (var dir in dirs)
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName == "Steamworks Shared" || dirName == "Steam Controller Configs") continue;
                    if (!seenDirs.Add(dir)) continue;

                    string exe = FindMainExe(dir);
                    var sg = new SteamGame();
                    sg.Name = dirName;
                    sg.Exe = exe != null ? Path.GetFileNameWithoutExtension(exe) : "";
                    sg.Path = dir;
                    result.Add(sg);
                }
            }
            return result;
        }
    }

    // ==================== 对话框 ====================
    public static class UiDialogs
    {
        // ---------- 60 分钟提醒弹窗 ----------
        public static string ShowGuardDialog(int playedMinutes, int remainExtend, string gameName)
        {
            string choice = "timeout";
            int remain = Cfg.PopupTimeoutSec;

            var form = Ui.MakeForm("游戏时间提醒", 380, 204, true);
            Ui.AddTopBar(form, 380);

            var lblTitle = Ui.MakeLabel(string.Format("已经玩了 {0} 分钟", playedMinutes),
                20, 26, 340, 46, 20, true, Ui.Primary, "MiddleCenter");
            var lblDesc = Ui.MakeLabel(gameName + "还开着。要现在收手吗？",
                20, 80, 340, 26, 10, false, Ui.TextDark, "MiddleCenter");
            var lblCount = Ui.MakeLabel(string.Format("未操作将在 {0} 秒后自动结束游戏", remain),
                20, 108, 340, 22, 9, false, Ui.SubText, "MiddleCenter");
            var line = Ui.MakeLine(20, 138, 340);
            var btnKill = Ui.MakeButton("结束游戏", 20, 150, 152, 40, true, 11);
            var btnExtend = Ui.MakeButton("", 208, 150, 152, 40, false, 9);

            if (remainExtend > 0)
            {
                btnExtend.Text = string.Format("再玩 {0} 分钟（剩 {1} 次）", Cfg.ExtendMinutes, remainExtend);
                btnExtend.Enabled = true;
            }
            else
            {
                btnExtend.Text = "续期次数已用完";
                btnExtend.Enabled = false;
                btnExtend.ForeColor = Color.FromArgb(170, 170, 170);
            }

            form.Controls.AddRange(new Control[] { lblTitle, lblDesc, lblCount, line, btnKill, btnExtend });

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += (s, e) =>
            {
                remain--;
                if (remain <= 0) { timer.Stop(); form.Close(); }
                else lblCount.Text = string.Format("未操作将在 {0} 秒后自动结束游戏", remain);
            };

            btnKill.Click += (s, e) => { choice = "kill"; timer.Stop(); form.Close(); };
            btnExtend.Click += (s, e) => { choice = "extend"; timer.Stop(); form.Close(); };

            form.Shown += (s, e) =>
            {
                timer.Start();
                try { SystemSounds.Exclamation.Play(); } catch { }
            };
            form.FormClosed += (s, e) => { timer.Stop(); timer.Dispose(); };

            form.ShowDialog();
            form.Dispose();
            return choice;
        }

        // ---------- 通用提示弹窗 ----------
        public static void ShowNotice(string title, string message, string hint, int seconds, string countdownText)
        {
            int remain = seconds;
            var form = Ui.MakeForm("游戏时间提醒", 360, 190, true);
            Ui.AddTopBar(form, 360);

            var lblTitle = Ui.MakeLabel(title, 20, 24, 320, 40, 16, true, Ui.Primary, "MiddleCenter");
            var lblMsg = Ui.MakeLabel(message, 20, 68, 320, 26, 10, false, Ui.TextDark, "MiddleCenter");
            var lblHint = Ui.MakeLabel(hint, 20, 96, 320, 20, 9, false, Ui.SubText, "MiddleCenter");
            var lblCount = Ui.MakeLabel(string.Format(countdownText, remain), 20, 118, 320, 20, 9, false, Ui.SubText, "MiddleCenter");
            var btnOk = Ui.MakeButton("知道了", 125, 142, 110, 34, false, 10);

            form.Controls.AddRange(new Control[] { lblTitle, lblMsg, lblHint, lblCount, btnOk });

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += (s, e) =>
            {
                remain--;
                if (remain <= 0) { timer.Stop(); form.Close(); }
                else lblCount.Text = string.Format(countdownText, remain);
            };
            btnOk.Click += (s, e) => { timer.Stop(); form.Close(); };
            form.Shown += (s, e) =>
            {
                timer.Start();
                try { SystemSounds.Exclamation.Play(); } catch { }
            };
            form.FormClosed += (s, e) => { timer.Stop(); timer.Dispose(); };

            form.ShowDialog();
            form.Dispose();
        }

        // ---------- 手动添加游戏 ----------
        public static GameEntry ShowAddGameDialog()
        {
            GameEntry result = null;

            var form = Ui.MakeForm("手动添加游戏", 420, 250, false);
            Ui.AddTopBar(form, 420);

            var lbl1 = Ui.MakeLabel("游戏名称", 20, 31, 68, 26, 10, false, Ui.TextDark, "MiddleRight");
            var txtName = new TextBox();
            txtName.Bounds = new Rectangle(Ui.U(96), Ui.U(30), Ui.U(300), Ui.U(30));
            txtName.Font = new Font("Microsoft YaHei UI", 10);

            var lbl2 = Ui.MakeLabel("进程名", 20, 73, 68, 26, 10, false, Ui.TextDark, "MiddleRight");
            var txtProc = new TextBox();
            txtProc.Bounds = new Rectangle(Ui.U(96), Ui.U(72), Ui.U(300), Ui.U(30));
            txtProc.Font = new Font("Microsoft YaHei UI", 10);

            var lblTip = Ui.MakeLabel(
                "进程名不含 .exe，多个用逗号分隔。\n游戏运行时可在「任务管理器 → 详细信息」里查看进程名。",
                96, 112, 300, 58, 9, false, Ui.SubText, "TopLeft");

            var btnOk = Ui.MakeButton("添加", 192, 190, 100, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 304, 190, 96, 36, false, 10);

            form.Controls.AddRange(new Control[] { lbl1, txtName, lbl2, txtProc, lblTip, btnOk, btnCancel });

            btnCancel.Click += (s, e) => form.Close();
            btnOk.Click += (s, e) =>
            {
                string nm = txtName.Text.Trim();
                string raw = txtProc.Text.Trim();
                if (nm.Length == 0 || raw.Length == 0)
                {
                    MessageBox.Show(form, "游戏名称和进程名都要填写。", "手动添加游戏");
                    return;
                }
                var procs = raw.Split(new char[] { ',', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => Regex.Replace(x, @"\.exe$", "")).ToArray();
                result = new GameEntry(nm, procs, new string[0]);
                form.Close();
            };

            form.ShowDialog();
            form.Dispose();
            return result;
        }

        // ---------- Steam 游戏多选 ----------
        public static List<SteamGame> ShowPickSteamGames()
        {
            var all = SteamScan.GetSteamGames();
            if (all.Count == 0)
            {
                MessageBox.Show("没有扫描到 Steam 游戏。请确认 Steam 已安装且库目录可访问。", "扫描结果");
                return new List<SteamGame>();
            }

            List<SteamGame> picked = new List<SteamGame>();

            var form = Ui.MakeForm("选择要限制的游戏（可多选）", 460, 400, false);
            Ui.AddTopBar(form, 460);

            var lbl = Ui.MakeLabel(string.Format("扫描到 {0} 个游戏，按住 Ctrl 可多选", all.Count),
                20, 20, 420, 22, 9, false, Ui.SubText, "MiddleLeft");

            var list = new ListBox();
            list.Bounds = new Rectangle(Ui.U(20), Ui.U(48), Ui.U(420), Ui.U(286));
            list.SelectionMode = SelectionMode.MultiExtended;
            list.Font = new Font("Microsoft YaHei UI", 10);
            foreach (var g in all)
                list.Items.Add(string.Format("{0}    ({1})", g.Name, g.Exe.Length > 0 ? g.Exe + ".exe" : "未找到主程序"));

            var btnOk = Ui.MakeButton("添加选中", 208, 346, 124, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 344, 346, 96, 36, false, 10);

            form.Controls.AddRange(new Control[] { lbl, list, btnOk, btnCancel });
            btnCancel.Click += (s, e) => form.Close();
            btnOk.Click += (s, e) =>
            {
                foreach (int idx in list.SelectedIndices) picked.Add(all[idx]);
                form.Close();
            };

            form.ShowDialog();
            form.Dispose();
            return picked;
        }

        // ---------- 临时调整今天 ----------
        public static bool ShowTempDialog()
        {
            bool saved = false;
            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");

            string baseStart = Cfg.ScheduleStart;
            string baseEnd = Cfg.ScheduleEnd;
            if (Cfg.TempDate == todayKey && !string.IsNullOrEmpty(Cfg.TempStart) && !string.IsNullOrEmpty(Cfg.TempEnd))
            {
                baseStart = Cfg.TempStart;
                baseEnd = Cfg.TempEnd;
            }

            var form = Ui.MakeForm("临时调整今天", 400, 182, false);
            Ui.AddTopBar(form, 400);

            var lbl1 = Ui.MakeLabel("开始时间", 20, 59, 64, 26, 10, false, Ui.TextDark, "MiddleRight");
            var dtpStart = Ui.MakeTimePicker(90, 56, 104, baseStart);
            var lbl2 = Ui.MakeLabel("结束时间", 204, 59, 68, 26, 10, false, Ui.TextDark, "MiddleRight");
            var dtpEnd = Ui.MakeTimePicker(276, 56, 104, baseEnd);
            var lblTip = Ui.MakeLabel("仅对今天生效，明天自动恢复常规时段", 20, 22, 360, 22, 9, false, Ui.SubText, "MiddleLeft");
            var btnSave = Ui.MakeButton("今天就这样", 152, 126, 116, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 280, 126, 100, 36, false, 10);

            form.Controls.AddRange(new Control[] { lbl1, dtpStart, lbl2, dtpEnd, lblTip, btnSave, btnCancel });

            btnCancel.Click += (s, e) => form.Close();
            btnSave.Click += (s, e) =>
            {
                string st = dtpStart.Value.ToString("HH:mm");
                string en = dtpEnd.Value.ToString("HH:mm");
                if (st == en)
                {
                    MessageBox.Show(form, "开始时间和结束时间不能相同。", "临时调整");
                    return;
                }
                Cfg.TempDate = DateTime.Now.ToString("yyyy-MM-dd");
                Cfg.TempStart = st;
                Cfg.TempEnd = en;
                Engine.WarnKey = null;
                saved = true;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write(string.Format("临时调整今天：{0} - {1}（仅今日生效）", st, en));
                form.Close();
            };

            form.ShowDialog();
            form.Dispose();
            return saved;
        }

        // ---------- 管理游戏列表 ----------
        public static void ShowGamesDialog(List<GameEntry> gamesSnapshot)
        {
            List<GameEntry> gTemp = gamesSnapshot
                .Select(g => new GameEntry(g.Name, g.Processes, g.PathFilter)).ToList();

            var form = Ui.MakeForm("管理游戏列表", 460, 390, false);
            Ui.AddTopBar(form, 460);

            var lblTip = Ui.MakeLabel("下列游戏共用同一套时长规则：任一游戏运行即开始计时",
                20, 20, 420, 22, 9, false, Ui.SubText, "MiddleLeft");

            var list = new ListBox();
            list.Bounds = new Rectangle(Ui.U(20), Ui.U(48), Ui.U(420), Ui.U(216));
            list.Font = new Font("Microsoft YaHei UI", 10);

            var btnSteam = Ui.MakeButton("从 Steam 库添加", 20, 274, 136, 34, false, 10);
            var btnManual = Ui.MakeButton("手动添加", 166, 274, 100, 34, false, 10);
            var btnDel = Ui.MakeButton("删除选中", 276, 274, 100, 34, false, 10);
            var line = Ui.MakeLine(20, 324, 420);
            var btnSave = Ui.MakeButton("保存并关闭", 208, 336, 124, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 344, 336, 96, 36, false, 10);

            Action refresh = () =>
            {
                list.Items.Clear();
                foreach (var g in gTemp)
                    list.Items.Add(string.Format("{0}    [{1}]", g.Name, string.Join(", ", g.Processes)));
            };
            refresh();

            form.Controls.AddRange(new Control[] { lblTip, list, btnSteam, btnManual, btnDel, line, btnSave, btnCancel });

            btnCancel.Click += (s, e) => form.Close();

            btnDel.Click += (s, e) =>
            {
                var idxs = list.SelectedIndices.Cast<int>().ToList();
                if (idxs.Count == 0) return;
                gTemp = gTemp.Where((g, i) => !idxs.Contains(i)).ToList();
                refresh();
            };

            btnSteam.Click += (s, e) =>
            {
                var picked = ShowPickSteamGames();
                if (picked.Count == 0) return;
                foreach (var pg in picked)
                {
                    if (string.IsNullOrEmpty(pg.Exe)) continue;
                    if (gTemp.Any(g => g.Name == pg.Name)) continue;
                    gTemp.Add(new GameEntry(pg.Name, new string[] { pg.Exe }, new string[0]));
                }
                refresh();
            };

            btnManual.Click += (s, e) =>
            {
                var ng = ShowAddGameDialog();
                if (ng != null) { gTemp.Add(ng); refresh(); }
            };

            btnSave.Click += (s, e) =>
            {
                if (gTemp.Count == 0)
                {
                    var ans = MessageBox.Show(form, "列表为空将不会管控任何游戏，确定保存吗？",
                        "管理游戏列表", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes) return;
                }
                Cfg.Games = gTemp;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("游戏列表已更新：" + string.Join("、", gTemp.Select(g => g.Name).ToArray()));
                form.Close();
            };

            form.ShowDialog();
            form.Dispose();
        }
    }

    // ==================== 主控制面板 ====================
    public static class PanelWindow
    {
        private static Form _panel = null;
        private static Label _lblMode, _lblGame, _lblTip;
        private static RadioButton _rbSched, _rbCont;
        private static DateTimePicker _dtpStart, _dtpEnd;
        private static NumericUpDown _numWarn, _numPlay, _numExtend, _numMax;
        private static CheckBox _cbCont;
        private static CheckBox[] _dayChecks;
        private static Button _btnPause;
        private static System.Windows.Forms.Timer _timer, _tipTimer;
        private static bool _radioGuard = false;

        public static string[] GetStatusLines()
        {
            string modeText;
            if (Cfg.UseSchedule)
                modeText = string.Format("模式：固定时段 {0} - {1}", Cfg.ScheduleStart, Cfg.ScheduleEnd);
            else
                modeText = string.Format("模式：连续时长 {0} 分钟/轮，可续 {1} 次", Cfg.PlayMinutes, Cfg.MaxExtend);

            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            if (Cfg.TempDate == todayKey && !string.IsNullOrEmpty(Cfg.TempStart) && !string.IsNullOrEmpty(Cfg.TempEnd))
                modeText += string.Format("  |  今日临时 {0}-{1}", Cfg.TempStart, Cfg.TempEnd);
            if (Engine.GetPausedState()) modeText += "  |  已暂停";

            string gameText;
            var procs = Engine.GetGameProcesses();
            if (procs.Count > 0)
            {
                var g = Engine.GetActiveGame();
                string nm = g != null ? g.Name : Cfg.GameTitle;
                if (Engine.DueTime != null)
                {
                    int left = (int)Math.Ceiling((Engine.DueTime.Value - DateTime.Now).TotalSeconds);
                    if (left < 0) left = 0;
                    gameText = string.Format("● {0} 运行中 · 本轮剩余 {1} 分 {2} 秒",
                        nm, left / 60, left % 60);
                }
                else
                {
                    gameText = "● " + nm + " 运行中";
                }
            }
            else
            {
                gameText = "○ 当前没有游戏在运行";
            }
            return new string[] { modeText, gameText };
        }

        public static void UpdateState()
        {
            if (_panel == null || _panel.IsDisposed) return;
            try
            {
                var st = GetStatusLines();
                _lblMode.Text = st[0];
                _lblGame.Text = st[1];
                _radioGuard = true;
                _rbSched.Checked = Cfg.UseSchedule;
                _rbCont.Checked = !Cfg.UseSchedule;
                _radioGuard = false;
                _btnPause.Text = Engine.GetPausedState() ? "恢复管控" : "暂停 1 小时";
            }
            catch { }
        }

        public static Form Current { get { return _panel; } }

        public static void ShowPanel()
        {
            if (_panel != null && !_panel.IsDisposed)
            {
                try
                {
                    _panel.Show();
                    _panel.Activate();
                    UpdateState();
                    return;
                }
                catch { }
            }

            var form = Ui.MakeForm("游戏时长守卫", 420, 576, false);
            form.TopMost = false;
            Ui.AddTopBar(form, 420);

            var lblTitle = Ui.MakeLabel("游戏时长守卫", 20, 14, 300, 26, 15, true, Ui.TextDark, "MiddleLeft");
            var lblMode = Ui.MakeLabel("", 20, 42, 380, 22, 9, false, Ui.SubText, "MiddleLeft");
            var lblGame = Ui.MakeLabel("", 20, 64, 380, 22, 9, false, Ui.TextDark, "MiddleLeft");
            var line1 = Ui.MakeLine(20, 92, 380);

            var rbSched = Ui.MakeRadio("固定时段", 20, 100, 110, 28, Cfg.UseSchedule);
            var rbCont = Ui.MakeRadio("连续时长", 140, 100, 120, 28, !Cfg.UseSchedule);
            var line2 = Ui.MakeLine(20, 136, 380);

            var secA = Ui.MakeLabel("可玩时段", 20, 142, 200, 22, 10.5, true, Ui.TextDark, "MiddleLeft");

            // 栅格：标签列 20..98（右对齐），输入列从 104 开始；第二列标签右边缘 288，输入从 294 开始
            var lbl1 = Ui.MakeLabel("开始时间", 20, 171, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            var dtpStart = Ui.MakeTimePicker(104, 168, 100, Cfg.ScheduleStart);
            var lbl2 = Ui.MakeLabel("结束时间", 210, 171, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            var dtpEnd = Ui.MakeTimePicker(294, 168, 100, Cfg.ScheduleEnd);

            var lbl3 = Ui.MakeLabel("结束前预警", 20, 207, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            var numWarn = Ui.MakeNumber(104, 204, 74, 1, 60, Cfg.ScheduleWarnMin);
            var lblUnitWarn = Ui.MakeLabel("分钟", 184, 207, 40, 26, 9, false, Ui.SubText, "MiddleLeft");

            var lbl5 = Ui.MakeLabel("生效星期", 20, 243, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            string[] dayNames = new string[] { "日", "一", "二", "三", "四", "五", "六" };
            var dayChecks = new CheckBox[7];
            for (int i = 0; i < 7; i++)
            {
                bool ck = Cfg.ScheduleDays.Count == 0 || Cfg.ScheduleDays.Contains(i);
                var cb = Ui.MakeCheck(dayNames[i], 104 + i * 42, 242, 40, 28, ck);
                form.Controls.Add(cb);
                dayChecks[i] = cb;
            }
            var line3 = Ui.MakeLine(20, 280, 380);

            var secB = Ui.MakeLabel("连续时长", 20, 286, 200, 22, 10.5, true, Ui.TextDark, "MiddleLeft");

            var lblPlay = Ui.MakeLabel("连续时长", 20, 315, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            var numPlay = Ui.MakeNumber(104, 312, 74, 5, 480, Cfg.PlayMinutes);
            var lblUnitPlay = Ui.MakeLabel("分钟", 184, 315, 40, 26, 9, false, Ui.SubText, "MiddleLeft");

            var lblExt = Ui.MakeLabel("每次续期", 20, 351, 78, 26, 10, false, Ui.TextDark, "MiddleRight");
            var numExtend = Ui.MakeNumber(104, 348, 74, 1, 60, Cfg.ExtendMinutes);
            var lblUnitExt = Ui.MakeLabel("分钟", 184, 351, 40, 26, 9, false, Ui.SubText, "MiddleLeft");
            var lblMax = Ui.MakeLabel("最多", 240, 351, 48, 26, 10, false, Ui.TextDark, "MiddleRight");
            var numMax = Ui.MakeNumber(294, 348, 74, 0, 10, Cfg.MaxExtend);
            var lblUnitMax = Ui.MakeLabel("次", 374, 351, 26, 26, 9, false, Ui.SubText, "MiddleLeft");

            var cbCont = Ui.MakeCheck("时段内同时启用连续时长限制", 20, 384, 320, 30, Cfg.ScheduleWithContinuous);
            var line4 = Ui.MakeLine(20, 420, 380);

            var btnGames = Ui.MakeButton("管理游戏列表", 20, 428, 120, 34, false, 10);
            var btnTemp = Ui.MakeButton("临时调整今天", 152, 428, 120, 34, false, 10);
            var btnPause = Ui.MakeButton("暂停 1 小时", 284, 428, 116, 34, false, 10);
            var btnKill = Ui.MakeButton("立即结束游戏", 20, 470, 120, 34, false, 10);
            var btnExit = Ui.MakeButton("退出程序", 152, 470, 100, 34, false, 10);
            var line5 = Ui.MakeLine(20, 514, 380);

            var lblTip = Ui.MakeLabel("改完点保存生效", 20, 527, 130, 26, 9, false, Ui.SubText, "MiddleLeft");
            var btnSave = Ui.MakeButton("保存设置", 168, 522, 120, 36, true, 10);
            var btnClose = Ui.MakeButton("关闭", 300, 522, 100, 36, false, 10);

            form.Controls.AddRange(new Control[] {
                lblTitle, lblMode, lblGame, line1, rbSched, rbCont, line2,
                secA, lbl1, dtpStart, lbl2, dtpEnd, lbl3, numWarn, lblUnitWarn, lbl5,
                line3, secB, lblPlay, numPlay, lblUnitPlay, lblExt, numExtend, lblUnitExt,
                lblMax, numMax, lblUnitMax, cbCont, line4, btnGames, btnTemp, btnPause,
                btnKill, btnExit, line5, lblTip, btnSave, btnClose });

            _panel = form;
            _lblMode = lblMode; _lblGame = lblGame;
            _rbSched = rbSched; _rbCont = rbCont;
            _dtpStart = dtpStart; _dtpEnd = dtpEnd;
            _numWarn = numWarn; _numPlay = numPlay;
            _numExtend = numExtend; _numMax = numMax;
            _cbCont = cbCont; _dayChecks = dayChecks;
            _btnPause = btnPause; _lblTip = lblTip;

            // 模式切换：立即生效
            _radioGuard = true;
            rbSched.CheckedChanged += (s, e) =>
            {
                if (_radioGuard || !rbSched.Checked) return;
                _radioGuard = true;
                Cfg.UseSchedule = true;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("切换模式：固定时段（" + Cfg.ScheduleStart + " - " + Cfg.ScheduleEnd + "）");
                _radioGuard = false;
                UpdateState();
            };
            rbCont.CheckedChanged += (s, e) =>
            {
                if (_radioGuard || !rbCont.Checked) return;
                _radioGuard = true;
                Cfg.UseSchedule = false;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("切换模式：连续时长（" + Cfg.PlayMinutes + " 分钟 + 续期 " + Cfg.MaxExtend + " 次）");
                _radioGuard = false;
                UpdateState();
            };
            _radioGuard = false;

            // 保存设置
            btnSave.Click += (s, e) =>
            {
                string st = _dtpStart.Value.ToString("HH:mm");
                string en = _dtpEnd.Value.ToString("HH:mm");
                if (st == en)
                {
                    MessageBox.Show(form, "开始时间和结束时间不能相同。", "游戏时长设置");
                    return;
                }
                var days = new List<int>();
                for (int i = 0; i < 7; i++)
                    if (_dayChecks[i].Checked) days.Add(i);
                if (days.Count == 0)
                {
                    MessageBox.Show(form, "至少选择一个生效星期。", "游戏时长设置");
                    return;
                }
                Cfg.ScheduleStart = st;
                Cfg.ScheduleEnd = en;
                Cfg.ScheduleWarnMin = (int)_numWarn.Value;
                Cfg.ScheduleDays = days;
                Cfg.ScheduleWithContinuous = _cbCont.Checked;
                Cfg.PlayMinutes = (int)_numPlay.Value;
                Cfg.ExtendMinutes = (int)_numExtend.Value;
                Cfg.MaxExtend = (int)_numMax.Value;
                Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write(string.Format(
                    "面板保存设置：时段 {0}-{1}，预警 {2} 分，连续 {3} 分，续期 {4} 分×{5} 次，星期 {6}",
                    st, en, Cfg.ScheduleWarnMin, Cfg.PlayMinutes, Cfg.ExtendMinutes, Cfg.MaxExtend,
                    string.Join(",", days)));
                UpdateState();
                _lblTip.Text = "已保存 ✓";
                _lblTip.ForeColor = Ui.Green;
                try { _tipTimer.Stop(); _tipTimer.Start(); } catch { }
            };

            // 快捷操作
            btnGames.Click += (s, e) => { UiDialogs.ShowGamesDialog(Cfg.Games); UpdateState(); };
            btnTemp.Click += (s, e) => { UiDialogs.ShowTempDialog(); UpdateState(); };

            btnPause.Click += (s, e) =>
            {
                if (Engine.GetPausedState()) { Engine.ResumeControl(); }
                else { Engine.PauseOneHour(); }
                UpdateState();
            };

            btnKill.Click += (s, e) =>
            {
                var ps = Engine.GetGameProcesses();
                if (ps.Count == 0)
                {
                    MessageBox.Show(form, "当前没有检测到被管控的游戏在运行。", "立即结束游戏");
                    return;
                }
                foreach (var p in ps) { try { p.Kill(); } catch { } }
                Log.Write("面板：立即结束游戏");
                UpdateState();
            };

            btnExit.Click += (s, e) =>
            {
                var ans = MessageBox.Show(form,
                    "确定要退出游戏时长守卫吗？\n\n退出后本次开机内不再有任何管控。\n（下次开机仍会自动启动）",
                    "游戏时长守卫", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (ans != DialogResult.Yes) return;
                Log.Write("用户退出 GameGuard");
                try { MainForm.Notify.Visible = false; MainForm.Notify.Dispose(); } catch { }
                Application.Exit();
                Environment.Exit(0);
            };

            btnClose.Click += (s, e) => { try { form.Close(); } catch { } };

            // 状态刷新
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += (s, e) => UpdateState();

            _tipTimer = new System.Windows.Forms.Timer();
            _tipTimer.Interval = 2200;
            _tipTimer.Tick += (s, e) =>
            {
                _tipTimer.Stop();
                try
                {
                    _lblTip.Text = "改完点保存生效";
                    _lblTip.ForeColor = Ui.SubText;
                }
                catch { }
            };

            form.Shown += (s, e) => { _timer.Start(); UpdateState(); };
            form.FormClosed += (s, e) =>
            {
                try { _timer.Stop(); _timer.Dispose(); } catch { }
                try { _tipTimer.Stop(); _tipTimer.Dispose(); } catch { }
                _panel = null;
            };

            form.Show();
        }
    }

    // ==================== 主程序（托盘 + 轮询 Timer）====================
    public static class MainForm
    {
        public static NotifyIcon Notify;
        public static ToolStripMenuItem ItemStatus;
        public static ToolStripMenuItem ItemModeSched;
        public static ToolStripMenuItem ItemModeCont;
        public static ToolStripMenuItem ItemTempToday;
        public static ToolStripMenuItem ItemTempClear;

        public static void UpdateTrayMenuText()
        {
            ItemModeSched.Text = string.Format("固定时段（{0} - {1}）", Cfg.ScheduleStart, Cfg.ScheduleEnd);
            ItemModeSched.Checked = Cfg.UseSchedule;
            ItemModeCont.Checked = !Cfg.UseSchedule;

            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            if (Cfg.TempDate == todayKey && !string.IsNullOrEmpty(Cfg.TempStart) && !string.IsNullOrEmpty(Cfg.TempEnd))
            {
                ItemTempToday.Text = string.Format("临时调整今天（今日 {0} - {1}）", Cfg.TempStart, Cfg.TempEnd);
                ItemTempClear.Enabled = true;
            }
            else
            {
                ItemTempToday.Text = "临时调整今天...";
                ItemTempClear.Enabled = false;
            }

            try
            {
                Notify.Text = Cfg.UseSchedule ? "游戏时长守卫（固定时段）" : "游戏时长守卫（连续时长）";
            }
            catch { }
        }

        public static void Run()
        {
            bool gotLock = false;
            Mutex mutex = null;
            try
            {
                mutex = new Mutex(false, "Local\\GameGuard_SingleInstance");
                try { gotLock = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { gotLock = true; }   // 上个实例未释放锁，所有权已归本实例
                catch { gotLock = false; }
            }
            catch { gotLock = false; }
            if (!gotLock)
            {
                Log.Write("已有实例在运行，本次启动退出");
                return;
            }

            Log.Write("===== GameGuard 启动 =====");
            Cfg.Load();

            Notify = new NotifyIcon();
            try
            {
                Notify.Icon = Ui.MakeGuardIcon();
                Notify.Text = "游戏时长守卫（运行中）";
                Notify.Visible = true;
                Log.Write("托盘图标已创建");
                string tipText = Cfg.UseSchedule
                    ? string.Format("可玩时段 {0} - {1}，结束前 {2} 分钟预警。左键点击图标打开控制面板。",
                        Cfg.ScheduleStart, Cfg.ScheduleEnd, Cfg.ScheduleWarnMin)
                    : string.Format("连续游玩 {0} 分钟会提醒你。左键点击图标打开控制面板。", Cfg.PlayMinutes);
                Notify.ShowBalloonTip(4000, "游戏时长守卫已启动", tipText, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Write("托盘图标创建失败：" + ex.Message);
            }

            var menu = new ContextMenuStrip();

            ItemStatus = new ToolStripMenuItem("状态：等待游戏启动");
            ItemStatus.Enabled = false;
            menu.Items.Add(ItemStatus);
            menu.Items.Add(new ToolStripSeparator());

            var itemPanel = new ToolStripMenuItem("打开控制面板...");
            itemPanel.Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
            menu.Items.Add(itemPanel);

            var menuMode = new ToolStripMenuItem("切换模式");
            ItemModeSched = new ToolStripMenuItem("固定时段");
            ItemModeCont = new ToolStripMenuItem("连续时长（60 分钟 + 续期）");
            ItemModeSched.CheckOnClick = false;
            ItemModeCont.CheckOnClick = false;
            menuMode.DropDownItems.Add(ItemModeSched);
            menuMode.DropDownItems.Add(ItemModeCont);
            menu.Items.Add(menuMode);

            ItemTempToday = new ToolStripMenuItem("临时调整今天...");
            ItemTempClear = new ToolStripMenuItem("清除临时调整");
            menu.Items.Add(ItemTempToday);
            menu.Items.Add(ItemTempClear);
            menu.Items.Add(new ToolStripSeparator());

            var itemPause = new ToolStripMenuItem("暂停管控（1 小时）");
            var itemKillNow = new ToolStripMenuItem("立即结束游戏");
            menu.Items.Add(itemPause);
            menu.Items.Add(itemKillNow);
            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出程序");
            menu.Items.Add(itemExit);
            Notify.ContextMenuStrip = menu;

            // 左键点击 → 主控制面板；右键保留快捷菜单
            Notify.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    try { PanelWindow.ShowPanel(); }
                    catch (Exception ex) { Log.Write("打开控制面板失败：" + ex.Message); }
                }
            };
            Notify.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    try { PanelWindow.ShowPanel(); } catch { }
                }
            };
            itemPanel.Click += (s, e) => { try { PanelWindow.ShowPanel(); } catch { } };

            ItemModeSched.Click += (s, e) =>
            {
                if (Cfg.UseSchedule) return;
                Cfg.UseSchedule = true;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                UpdateTrayMenuText();
                Log.Write("切换模式：固定时段（" + Cfg.ScheduleStart + " - " + Cfg.ScheduleEnd + "）");
            };
            ItemModeCont.Click += (s, e) =>
            {
                if (!Cfg.UseSchedule) return;
                Cfg.UseSchedule = false;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                UpdateTrayMenuText();
                Log.Write("切换模式：连续时长（" + Cfg.PlayMinutes + " 分钟 + 续期 " + Cfg.MaxExtend + " 次）");
            };

            ItemTempToday.Click += (s, e) => { UiDialogs.ShowTempDialog(); };
            ItemTempClear.Click += (s, e) =>
            {
                Cfg.TempDate = ""; Cfg.TempStart = ""; Cfg.TempEnd = "";
                Engine.WarnKey = null;
                Cfg.Save();
                UpdateTrayMenuText();
                Log.Write("已清除今日临时调整，恢复常规时段");
            };

            itemPause.Click += (s, e) => { Engine.PauseOneHour(); };
            itemKillNow.Click += (s, e) =>
            {
                foreach (var p in Engine.GetGameProcesses()) { try { p.Kill(); } catch { } }
                Log.Write("用户通过托盘立即结束游戏");
            };
            itemExit.Click += (s, e) =>
            {
                var ans = MessageBox.Show(
                    "确定要退出游戏时长守卫吗？\n\n退出后本次开机内不再有任何管控。\n（下次开机仍会自动启动）",
                    "游戏时长守卫", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (ans != DialogResult.Yes) return;
                Log.Write("用户退出 GameGuard");
                try { Notify.Visible = false; Notify.Dispose(); } catch { }
                Application.Exit();
            };

            UpdateTrayMenuText();

            // 轮询 Timer（UI 线程，与 PS 版轮询逻辑一致）
            var pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = Math.Max(5, Cfg.PollSeconds) * 1000;
            pollTimer.Tick += (s, e) => Engine.Poll(ItemStatus);
            pollTimer.Start();

            var ctx = new ApplicationContext();
            Application.Run(ctx);

            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            Log.Write("===== GameGuard 退出 =====");
        }
    }

    // ==================== 入口 ====================
    public static class Program
    {
        // 自检模式：显示控制面板 1.2 秒后自动截图到 panel_check.png 并退出
        private static void RunSnap()
        {
            Cfg.Load();
            PanelWindow.ShowPanel();
            var t = new System.Windows.Forms.Timer();
            t.Interval = 1200;
            t.Tick += (s, e) =>
            {
                t.Stop();
                var f = PanelWindow.Current;
                if (f != null && !f.IsDisposed)
                {
                    try
                    {
                        var b = f.Bounds;
                        using (var bmp = new Bitmap(b.Width, b.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                            bmp.Save(Path.Combine(Cfg.ScriptDir, "panel_check.png"),
                                System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception ex) { Log.Write("snap 截图失败：" + ex.Message); }
                    try { f.Close(); } catch { }
                }
                Application.Exit();
            };
            t.Start();
            Application.Run(new ApplicationContext());
        }

        [STAThread]
        public static void Main(string[] args)
        {
            try { Win32.SetProcessDPIAware(); } catch { }
            Ui.InitScale();

            // exe 所在目录
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(exe))
            {
                try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
                catch { }
            }
            Cfg.ScriptDir = Path.GetDirectoryName(exe);
            Cfg.LogPath = Path.Combine(Cfg.ScriptDir, "gameguard.log");
            Cfg.ConfigPath = Path.Combine(Cfg.ScriptDir, "config.json");
            Cfg.PauseFlag = Path.Combine(Cfg.ScriptDir, "paused.flag");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
                Log.Write("UI 异常：" + e.Exception.Message);

            // 自检模式：--snap 截图后退出（用于界面核对）
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (string.Equals(a, "--snap", StringComparison.OrdinalIgnoreCase))
                    {
                        RunSnap();
                        return;
                    }
                }
            }

            MainForm.Run();
        }
    }
}
