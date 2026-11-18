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
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace GameGuard
{
    // ==================== 配置 ====================
    public class GameEntry
    {
        public string Name = "";
        public string[] Processes = new string[0];
        public string[] PathFilter = new string[0];
        public string IconPath = "";    // 游戏主程序完整路径（用于提取图标，可空）

        public GameEntry() { }
        public GameEntry(string name, string[] procs, string[] filters)
        {
            Name = name; Processes = procs; PathFilter = filters;
        }
        public GameEntry(string name, string[] procs, string[] filters, string iconPath)
        {
            Name = name; Processes = procs; PathFilter = filters; IconPath = iconPath ?? "";
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
        // 三种模式：可独立运行，也可任意叠加（useSchedule / mode 为旧字段兼容）
        public static bool UseSchedule = true;      // 固定时段
        public static bool UseContinuous = false;   // 连续时长（单独启用=轮次模式，配合时段=叠加轮次限制）
        public static bool UseQuota = false;        // 每日配额
        public static string ModeName
        {
            get
            {
                var parts = new List<string>();
                if (UseSchedule) parts.Add("固定时段");
                if (UseContinuous) parts.Add("连续时长");
                if (UseQuota) parts.Add("每日配额");
                return parts.Count > 0 ? string.Join(" + ", parts.ToArray()) : "未启用";
            }
        }
        public static string ScheduleStart = "18:00";
        public static string ScheduleEnd = "20:00";
        public static int ScheduleWarnMin = 3;
        public static List<int> ScheduleDays = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
        public static int OutsideCountdown = 5;
        public static string TempDate = "";
        public static string TempStart = "";
        public static string TempEnd = "";
        // ---- 每日配额（模式 2）----
        public static int QuotaMinutes = 120;      // 每日累计可玩分钟
        public static int QuotaWarnMin = 10;       // 临近用完预警（分钟）
        public static int TempQuotaMin = 0;        // 今天的临时配额覆盖（0=不覆盖）
        // 配额运行状态：持久化，防止重启绕过
        public static string QuotaDate = "";
        public static double QuotaUsedSec = 0;
        public static double QuotaExtraSec = 0;
        // ---- 上调限制：每个数字每天只能上调 1 次（下调不限），跨天自动重置 ----
        public static string ModLimitDate = "";
        public static int QuotaUpCount = 0;    // 当日「每日配额」上调次数
        public static int SchedUpCount = 0;    // 当日「可玩时段」上调次数
        public static int PlayUpCount = 0;     // 当日「连续时长」上调次数

        // 日期变化时清零上调计数
        public static void EnsureModLimitDate()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (ModLimitDate != today)
            {
                ModLimitDate = today;
                QuotaUpCount = 0;
                SchedUpCount = 0;
                PlayUpCount = 0;
            }
        }

        public static string ScriptDir = "";
        public static string LogPath = "";
        public static string ConfigPath = "";
        public static string PauseFlag = "";

        // ---- 防手改：config.json 校验和 + 备份 ----
        public static string ConfigSumPath = "";    // 校验和文件
        public static string ConfigBakPath = "";    // 最近一次合法保存的备份
        public static bool TamperRestored = false;  // 启动时曾从备份恢复（用于提示）
        public static bool TamperReset = false;     // 配置被改且无有效备份，已重置默认
        private const string CfgSalt = "GameGuard::cfg::v1::";

        private static string CfgBakPath { get { return ConfigBakPath.Length > 0 ? ConfigBakPath : ConfigPath + ".bak.json"; } }
        private static string CfgSumPath { get { return ConfigSumPath.Length > 0 ? ConfigSumPath : ConfigPath + ".sum"; } }

        private static string ComputeFileSum(string path)
        {
            byte[] salt = Encoding.UTF8.GetBytes(CfgSalt);
            byte[] body = File.ReadAllBytes(path);
            byte[] all = new byte[salt.Length + body.Length];
            Array.Copy(salt, 0, all, 0, salt.Length);
            Array.Copy(body, 0, all, salt.Length, body.Length);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(all)).Replace("-", "").ToLower();
            }
        }

        // 加载前校验：篡改 → 尽量从备份恢复；无有效备份 → 重置默认并留档
        // 返回 true = 继续按正常流程解析 config.json
        private static bool VerifyConfigIntegrity()
        {
            try
            {
                if (!File.Exists(CfgSumPath))
                {
                    // 首次启用校验：采纳现状作为基线（老用户无感升级），同时建立备份
                    File.WriteAllText(CfgSumPath, ComputeFileSum(ConfigPath), Encoding.ASCII);
                    File.Copy(ConfigPath, CfgBakPath, true);
                    Log.Write("已为 config.json 建立校验基线与备份");
                    return true;
                }
                string expect = File.ReadAllText(CfgSumPath, Encoding.ASCII).Trim();
                if (ComputeFileSum(ConfigPath) == expect) return true;

                Log.Write("检测到 config.json 被外部修改（校验和不匹配）");
                if (File.Exists(CfgBakPath))
                {
                    bool bakValid = false;
                    try { bakValid = ComputeFileSum(CfgBakPath) == expect; } catch { }
                    if (bakValid)
                    {
                        File.Copy(CfgBakPath, ConfigPath, true);
                        TamperRestored = true;
                        Log.Write("配置已从 config.bak.json 恢复");
                        Events.Record("配置防护", "配置被手改，已自动恢复最近合法配置");
                        return true;
                    }
                }
                // 无有效备份：留档被改文件，重置默认
                try { File.Copy(ConfigPath, ConfigPath + ".bad", true); } catch { }
                try { File.Delete(CfgSumPath); } catch { }
                TamperReset = true;
                Log.Write("无有效备份，配置已重置为默认值（原文件留存为 config.json.bad）");
                Events.Record("配置防护", "配置被手改且无有效备份，已重置默认（留存 config.json.bad）");
                Save();   // 重新生成默认配置 + 备份 + 校验
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("配置完整性检查异常：" + ex.Message);
                return true;
            }
        }

        public static void Load()
        {
            if (!File.Exists(ConfigPath))
            {
                Log.Write("未发现 config.json，使用默认值并生成一份");
                Save();
                return;
            }

            // ---- 防手改校验：被篡改时内部已完成恢复/重置 ----
            if (!VerifyConfigIntegrity()) return;

            try
            {
                var ser = new JavaScriptSerializer();
                var raw = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                if (raw == null) throw new Exception("empty json");

                bool b;
                int i;
                string s;
                double dVal;

                // ---- 模式开关：v1.2+ 三个独立 bool；v1.1 mode 三态；v1.0 useSchedule ----
                bool useSched = true, useCont = false, useQuota = false;
                bool legacyCont = false;
                if (TryGetBool(raw, "scheduleWithContinuous", out b)) legacyCont = b;
                if (raw.ContainsKey("useContinuous") || raw.ContainsKey("useQuota"))
                {
                    if (TryGetBool(raw, "useSchedule", out b)) useSched = b;
                    if (TryGetBool(raw, "useContinuous", out b)) useCont = b;
                    if (TryGetBool(raw, "useQuota", out b)) useQuota = b;
                }
                else if (TryGetInt(raw, "mode", out i))
                {
                    int modeVal = (i >= 0 && i <= 2) ? i : 0;
                    useSched = (modeVal == 0);
                    useCont = (modeVal == 1) || (modeVal == 0 && legacyCont);   // v1.1 时段+叠加 = 连续开启
                    useQuota = (modeVal == 2);
                }
                else if (TryGetBool(raw, "useSchedule", out b))
                {
                    useSched = b;
                    useCont = b && legacyCont;
                }
                UseSchedule = useSched; UseContinuous = useCont; UseQuota = useQuota;
                if (!UseSchedule && !UseContinuous && !UseQuota)
                {
                    UseSchedule = true;   // 至少启用一种，防呆
                    Log.Write("配置中三种模式均未启用，回退为固定时段");
                }

                if (TryGetInt(raw, "quotaMinutes", out i)) QuotaMinutes = i;
                if (TryGetInt(raw, "quotaWarnMin", out i)) QuotaWarnMin = i;
                if (TryGetInt(raw, "tempQuotaMin", out i)) TempQuotaMin = i;
                if (TryGetStr(raw, "quotaDate", out s)) QuotaDate = s;
                if (TryGetDouble(raw, "quotaUsedSec", out dVal)) QuotaUsedSec = dVal;
                if (TryGetDouble(raw, "quotaExtraSec", out dVal)) QuotaExtraSec = dVal;
                if (TryGetStr(raw, "modLimitDate", out s)) ModLimitDate = s;
                if (TryGetInt(raw, "quotaUpCount", out i)) QuotaUpCount = i;
                if (TryGetInt(raw, "schedUpCount", out i)) SchedUpCount = i;
                if (TryGetInt(raw, "playUpCount", out i)) PlayUpCount = i;
                EnsureModLimitDate();
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
                        if (gd.TryGetValue("iconPath", out v)) ge.IconPath = Convert.ToString(v);
                        if (!string.IsNullOrEmpty(ge.Name)) loaded.Add(ge);
                    }
                    if (loaded.Count > 0) Games = loaded;
                }

                Log.Write("已加载 config.json，当前模式：" + ModeName);
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

        private static bool TryGetDouble(Dictionary<string, object> d, string key, out double val)
        {
            val = 0; object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            try { val = Convert.ToDouble(v); return true; }
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
                    if (!string.IsNullOrEmpty(g.IconPath)) gd["iconPath"] = g.IconPath;
                    games.Add(gd);
                }
                var root = new Dictionary<string, object>();
                root["games"] = games;
                root["useSchedule"] = UseSchedule;
                root["useContinuous"] = UseContinuous;
                root["useQuota"] = UseQuota;
                root["mode"] = (UseQuota && !UseSchedule && !UseContinuous) ? 2 : (UseSchedule ? 0 : 1);   // 旧字段兼容
                root["scheduleWithContinuous"] = UseContinuous;   // 旧字段兼容
                root["quotaMinutes"] = QuotaMinutes;
                root["quotaWarnMin"] = QuotaWarnMin;
                root["tempQuotaMin"] = TempQuotaMin;
                root["quotaDate"] = QuotaDate;
                root["quotaUsedSec"] = QuotaUsedSec;
                root["quotaExtraSec"] = QuotaExtraSec;
                root["modLimitDate"] = ModLimitDate;
                root["quotaUpCount"] = QuotaUpCount;
                root["schedUpCount"] = SchedUpCount;
                root["playUpCount"] = PlayUpCount;
                root["scheduleStart"] = ScheduleStart;
                root["scheduleEnd"] = ScheduleEnd;
                root["scheduleWarnMin"] = ScheduleWarnMin;
                root["scheduleDays"] = ScheduleDays;
                root["outsideCountdown"] = OutsideCountdown;
                root["playMinutes"] = PlayMinutes;
                root["extendMinutes"] = ExtendMinutes;
                root["maxExtend"] = MaxExtend;
                root["tempDate"] = TempDate;
                root["tempStart"] = TempStart;
                root["tempEnd"] = TempEnd;
                File.WriteAllText(ConfigPath, ser.Serialize(root), new UTF8Encoding(false));
                // 维护备份与校验和（防手改）
                try
                {
                    File.Copy(ConfigPath, CfgBakPath, true);
                    File.WriteAllText(CfgSumPath, ComputeFileSum(ConfigPath), Encoding.ASCII);
                }
                catch { }
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

    // ==================== 用量记录（usage.csv，按分钟落盘）====================
    public static class Usage
    {
        private static DateTime? _lastTick = null;
        private static Dictionary<string, double> _pending = new Dictionary<string, double>();   // 每个游戏未落盘的秒数

        private static string CsvPath { get { return Path.Combine(Cfg.ScriptDir, "usage.csv"); } }
        private static string BakPath { get { return Path.Combine(Cfg.ScriptDir, "usage.bak.csv"); } }
        private static string SumPath { get { return Path.Combine(Cfg.ScriptDir, "usage.sum"); } }
        private static string BakSumPath { get { return Path.Combine(Cfg.ScriptDir, "usage.bak.sum"); } }
        private const string UsageSalt = "GameGuard::usage::v1::";

        // 完整性状态：fresh=尚无记录 / ok / missing=csv 缺失但校验存在 / mismatch=内容对不上
        private static string CheckIntegrity()
        {
            bool hasCsv = File.Exists(CsvPath);
            bool hasSum = File.Exists(SumPath);
            if (!hasCsv && !hasSum) return "fresh";
            if (hasCsv && !hasSum)
            {
                // 首次启用校验：采纳现状作为基线
                File.WriteAllText(SumPath, ChainHash.Compute(CsvPath, UsageSalt), Encoding.ASCII);
                Log.Write("已为 usage.csv 建立校验基线");
                return "ok";
            }
            if (!hasCsv) return "missing";
            return ChainHash.Compute(CsvPath, UsageSalt) == File.ReadAllText(SumPath, Encoding.ASCII).Trim() ? "ok" : "mismatch";
        }

        // 启动与统计前调用：能恢复则从镜像备份恢复
        // 返回 ok / restored / damaged
        public static string VerifyAndRepair()
        {
            try
            {
                string v = CheckIntegrity();
                if (v == "ok" || v == "fresh") return "ok";
                Log.Write("使用记录完整性异常：" + v);

                bool bakOk = false;
                try
                {
                    bakOk = File.Exists(BakPath) && File.Exists(BakSumPath) &&
                            ChainHash.Compute(BakPath, UsageSalt) == File.ReadAllText(BakSumPath, Encoding.ASCII).Trim();
                }
                catch { }
                if (bakOk)
                {
                    File.Copy(BakPath, CsvPath, true);
                    File.Copy(BakSumPath, SumPath, true);
                    Log.Write("使用记录已从 usage.bak.csv 恢复");
                    Events.Record("记录防护", "使用记录被删改，已从备份恢复");
                    return "restored";
                }

                // 主文件与备份都无法通过校验：留档后重新开始
                try
                {
                    if (File.Exists(CsvPath)) File.Copy(CsvPath, Path.Combine(Cfg.ScriptDir, "usage.bad.csv"), true);
                    File.Delete(CsvPath);
                    File.Delete(SumPath);
                }
                catch { }
                Log.Write("使用记录无法恢复，已重新开始记录（损坏文件留存为 usage.bad.csv）");
                Events.Record("记录防护", "使用记录损坏无法恢复，已重新开始记录");
                return "damaged";
            }
            catch { return "ok"; }
        }

        // 每次轮询调用：游戏运行中就累计真实时间差（断档 >120 秒视为睡眠，不计）
        // 多游戏并行运行时，本轮时间均摊给每个运行中的游戏
        public static void Record(bool running)
        {
            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");

            var names = running ? Engine.GetRunningGameNames() : new List<string>();
            if (names.Count == 0)
            {
                FlushAll(today);
                _lastTick = null;
                return;
            }
            if (_lastTick == null) { _lastTick = now; return; }
            double gap = (now - _lastTick.Value).TotalSeconds;
            _lastTick = now;
            if (gap <= 0 || gap > 120) return;

            double each = gap / names.Count;
            foreach (var nm in names)
            {
                if (!_pending.ContainsKey(nm)) _pending[nm] = 0;
                _pending[nm] += each;
            }

            // 冲刷满 1 分钟的条目
            var done = new List<string>();
            foreach (var kv in _pending)
                if (kv.Value >= 60) done.Add(kv.Key);
            foreach (var nm in done)
            {
                int mins = (int)(_pending[nm] / 60.0);
                _pending[nm] -= mins * 60;
                AppendRow(today, nm, mins);
            }
        }

        // 退出程序前把零头落盘
        public static void FinalFlush()
        {
            FlushAll(DateTime.Now.ToString("yyyy-MM-dd"));
        }

        private static void FlushAll(string today)
        {
            foreach (var kv in _pending)
            {
                int mins = (int)Math.Round(kv.Value / 60.0);
                if (mins > 0) AppendRow(today, kv.Key, mins);
            }
            _pending.Clear();
        }

        private static void AppendRow(string today, string gname, int mins)
        {
            try
            {
                File.AppendAllText(CsvPath,
                    today + "," + gname + "," + mins + Environment.NewLine, Encoding.UTF8);
                // 维护链式校验与镜像备份（防删改）
                File.WriteAllText(SumPath, ChainHash.Compute(CsvPath, UsageSalt), Encoding.ASCII);
                File.Copy(CsvPath, BakPath, true);
                File.Copy(SumPath, BakSumPath, true);
            }
            catch { }
        }
    }

    // ==================== 链式哈希（防删改校验的共用工具）====================
    public static class ChainHash
    {
        public static string Step(string prev, string line)
        {
            byte[] data = Encoding.UTF8.GetBytes(prev + "|" + line);
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLower();
            }
        }

        public static string Compute(string path, string salt)
        {
            string h = salt;
            foreach (var ln in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (ln.Trim().Length == 0) continue;
                h = Step(h, ln);
            }
            return h;
        }
    }

    // ==================== 事件记录（events.csv：续期/强制结束/上调/防护拦截）====================
    public static class Events
    {
        private static string CsvPath { get { return Path.Combine(Cfg.ScriptDir, "events.csv"); } }
        private static string BakPath { get { return Path.Combine(Cfg.ScriptDir, "events.bak.csv"); } }
        private static string SumPath { get { return Path.Combine(Cfg.ScriptDir, "events.sum"); } }
        private static string BakSumPath { get { return Path.Combine(Cfg.ScriptDir, "events.bak.sum"); } }
        private const string Salt = "GameGuard::events::v1::";

        public static void Record(string type, string detail)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "," +
                    type + "," + detail.Replace(",", "，").Replace("\r", " ").Replace("\n", " ");
                File.AppendAllText(CsvPath, line + Environment.NewLine, Encoding.UTF8);
                File.WriteAllText(SumPath, ChainHash.Compute(CsvPath, Salt), Encoding.ASCII);
                File.Copy(CsvPath, BakPath, true);
                File.Copy(SumPath, BakSumPath, true);
            }
            catch { }
        }

        // 完整性：fresh / ok / missing / mismatch
        private static string CheckIntegrity()
        {
            bool hasCsv = File.Exists(CsvPath);
            bool hasSum = File.Exists(SumPath);
            if (!hasCsv && !hasSum) return "fresh";
            if (hasCsv && !hasSum)
            {
                File.WriteAllText(SumPath, ChainHash.Compute(CsvPath, Salt), Encoding.ASCII);
                Log.Write("已为 events.csv 建立校验基线");
                return "ok";
            }
            if (!hasCsv) return "missing";
            return ChainHash.Compute(CsvPath, Salt) == File.ReadAllText(SumPath, Encoding.ASCII).Trim() ? "ok" : "mismatch";
        }

        public static string VerifyAndRepair()
        {
            try
            {
                string v = CheckIntegrity();
                if (v == "ok" || v == "fresh") return "ok";
                Log.Write("事件记录完整性异常：" + v);
                bool bakOk = false;
                try
                {
                    bakOk = File.Exists(BakPath) && File.Exists(BakSumPath) &&
                            ChainHash.Compute(BakPath, Salt) == File.ReadAllText(BakSumPath, Encoding.ASCII).Trim();
                }
                catch { }
                if (bakOk)
                {
                    File.Copy(BakPath, CsvPath, true);
                    File.Copy(BakSumPath, SumPath, true);
                    Log.Write("事件记录已从备份恢复");
                    return "restored";
                }
                try
                {
                    if (File.Exists(CsvPath)) File.Copy(CsvPath, Path.Combine(Cfg.ScriptDir, "events.bad.csv"), true);
                    File.Delete(CsvPath);
                    File.Delete(SumPath);
                }
                catch { }
                Log.Write("事件记录无法恢复，已重新开始（留存为 events.bad.csv）");
                return "damaged";
            }
            catch { return "ok"; }
        }

        // 读取近 days 天事件：每项 [时间, 类型, 明细]
        public static List<string[]> ReadDays(int days)
        {
            var res = new List<string[]>();
            try
            {
                VerifyAndRepair();
                if (!File.Exists(CsvPath)) return res;
                DateTime min = DateTime.Now.AddDays(-days);
                foreach (var ln in File.ReadAllLines(CsvPath, Encoding.UTF8))
                {
                    var seg = ln.Split(new char[] { ',' }, 3);
                    if (seg.Length < 3) continue;
                    DateTime t;
                    if (!DateTime.TryParse(seg[0], out t)) continue;
                    if (t < min) continue;
                    res.Add(new string[] { seg[0], seg[1], seg[2] });
                }
            }
            catch { }
            return res;
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

        // 分组标题带：浅灰底 + 红色标题字，用于界面分区（视觉分组感）
        public static Label MakeSection(string text, int x, int y, int w, int h)
        {
            var lbl = new Label();
            lbl.Text = text;
            lbl.Font = Base(10, true);
            lbl.ForeColor = Primary;
            lbl.BackColor = Color.FromArgb(246, 246, 248);
            lbl.Padding = new Padding(8, 0, 0, 0);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Bounds = new Rectangle(U(x), U(y), U(w), U(h));
            return lbl;
        }

        // 与自动高度控件（数字框/时间框：其真实高度恒等于字体行高，赋值再大也会被钳制）
        // 严格对齐的行标签：高度取字体行高、顶部与控件同 y → 任意 DPI 下文字与框内文字完全重叠
        public static Label MakeRowLabel(string text, int x, int y, int w,
            double size, bool bold, Color color, string align)
        {
            var l = MakeLabel(text, x, y, w, 30, size, bold, color, align);
            l.Height = l.Font.Height;
            return l;
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
        // ---- 每日配额运行时状态 ----
        public static DateTime? QuotaLastTick = null;   // 上次计费时刻
        public static string QuotaWarnKey = null;       // 预警去重（每天一次）
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

        public static List<string> GetRunningGameNames()
        {
            var names = new List<string>();
            var seen = new HashSet<string>();
            foreach (var g in Cfg.Games)
            {
                bool found = false;
                foreach (var pn in g.Processes)
                {
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    try
                    {
                        if (System.Diagnostics.Process.GetProcessesByName(pn.Trim()).Length > 0)
                        {
                            found = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (found && seen.Add(g.Name)) names.Add(g.Name);
            }
            return names;
        }

        public static GameEntry GetActiveGame()
        {
            foreach (var name in GetRunningGameNames())
                foreach (var g in Cfg.Games)
                    if (g.Name == name) return g;
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
            var g = GetActiveGame();
            Events.Record("强制结束", (g != null ? g.Name : Cfg.GameTitle) + "：" + reason);
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
                if (Cfg.UseQuota)
                {
                    Cfg.QuotaExtraSec += Cfg.ExtendMinutes * 60;   // 叠加配额时同步追加配额，避免立刻被配额拦下
                    Cfg.Save();
                }
                int left = Math.Max(0, Cfg.MaxExtend - ExtendUsed);
                Log.Write(string.Format("用户续期 {0} 分钟，剩余 {1} 次，新截止 {2}",
                    Cfg.ExtendMinutes, left, DueTime.Value.ToString("HH:mm:ss")));
                Events.Record("续期", gname + " 续期 " + Cfg.ExtendMinutes + " 分钟（剩 " + left + " 次）");
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
                string why = choice == "kill" ? "用户选择结束" : "弹窗超时未响应";
                Log.Write("结束游戏，原因：" + why);
                Events.Record("强制结束", gname + " 轮次到点，" + why);
                foreach (var p in procs) { try { p.Kill(); } catch { } }
                DueTime = null;
                RoundStart = null;
                ExtendUsed = 0;
            }
        }

        // ---------- 每日配额辅助 ----------
        // 今日配额总额（分钟）：临时覆盖优先，其次常规配置
        public static int QuotaTotalMin()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (Cfg.TempDate == today && Cfg.TempQuotaMin > 0) return Cfg.TempQuotaMin;
            return Cfg.QuotaMinutes;
        }

        // 今日剩余秒数 = 总额 + 续期追加 - 已消耗
        public static double QuotaRemainSec()
        {
            return QuotaTotalMin() * 60.0 + Cfg.QuotaExtraSec - Cfg.QuotaUsedSec;
        }

        // 跨天重置：清零并持久化
        private static void QuotaDailyReset(string today)
        {
            Cfg.QuotaDate = today;
            Cfg.QuotaUsedSec = 0;
            Cfg.QuotaExtraSec = 0;
            ExtendUsed = 0;
            QuotaWarnKey = null;
            QuotaLastTick = null;
            Cfg.Save();
            Log.Write("每日配额已重置（" + today + "）");
        }

        // ---------- 主轮询（托盘 Timer 驱动，UI 线程）----------
        // 组合规则：三种模式可任意叠加。优先级：时段外收口 > 配额用尽 > 连续轮次/时段倒计时
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
                var sched = GetScheduleState();      // 未启用时段时 State = Disabled
                bool schedOn = Cfg.UseSchedule;

                // ---- 用量记录：与管控模式无关，只要游戏在跑就记（统计报表数据源）----
                Usage.Record(procs.Count > 0);

                // ---- 每日配额：启用且游戏运行中即累计（时段内外都算），跨天自动重置 ----
                if (Cfg.UseQuota)
                {
                    string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
                    if (Cfg.QuotaDate != todayKey)
                        QuotaDailyReset(todayKey);
                    QuotaTick(procs.Count > 0);
                }
                else
                {
                    QuotaLastTick = null;
                }

                // ---- 无游戏运行：复位轮次，显示待机状态 ----
                if (procs.Count == 0)
                {
                    if (DueTime != null)
                    {
                        Log.Write("游戏已退出，计时重置");
                        DueTime = null; RoundStart = null; ExtendUsed = 0;
                    }
                    if (schedOn && sched.State == "Before")
                        statusItem.Text = "状态：未到可玩时段（" + sched.Start.ToString("HH:mm") + " 开始）";
                    else if (schedOn && sched.State == "Closed")
                        statusItem.Text = "状态：今天可玩时段已结束";
                    else if (Cfg.UseQuota)
                    {
                        int usedMin = (int)Math.Floor(Cfg.QuotaUsedSec / 60.0);
                        int totalMin = QuotaTotalMin() + (int)Math.Floor(Cfg.QuotaExtraSec / 60.0);
                        statusItem.Text = string.Format("状态：今日已用 {0} 分 / {1} 分", usedMin, totalMin);
                    }
                    else
                        statusItem.Text = "状态：等待游戏启动";
                    return;
                }

                // ---- 游戏运行中 ----

                // 1) 时段外：提示倒计时后关闭
                if (schedOn && sched.State != "Inside")
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
                    UiDialogs.ShowToast("现在不在可玩时段",
                        msg + "，将在 " + Cfg.OutsideCountdown + " 秒后关闭游戏\n每天可玩：" + realStart + " - " + realEnd);
                    System.Threading.Thread.Sleep(Cfg.OutsideCountdown * 1000);   // 与旧弹窗倒计时等宽的缓冲
                    StopGameNow("不在可玩时段");
                    return;
                }

                // 2) 配额用尽：守护对话框（续期同时追加配额与轮次）
                if (Cfg.UseQuota && QuotaRemainSec() <= 0)
                {
                    QuotaExhausted();
                    statusItem.Text = "状态：今日配额已用完";
                    return;
                }

                // 3) 时段内：连续轮次 + 时段倒计时 + 预警
                int remainSec = schedOn ? (int)Math.Ceiling((sched.End - DateTime.Now).TotalSeconds) : int.MaxValue;
                bool nearEnd = schedOn && remainSec <= Cfg.ScheduleWarnMin * 60;

                if (Cfg.UseContinuous && !nearEnd && DueTime == null)
                {
                    RoundStart = DateTime.Now;
                    DueTime = DateTime.Now.AddMinutes(Cfg.PlayMinutes);
                    Log.Write("检测到游戏运行，本轮截止 " + DueTime.Value.ToString("HH:mm:ss"));
                }

                // 状态行：按启用的模式组合显示
                var parts = new List<string>();
                if (Cfg.UseContinuous && DueTime != null)
                {
                    int leftSec = (int)Math.Ceiling((DueTime.Value - DateTime.Now).TotalSeconds);
                    if (leftSec < 0) leftSec = 0;
                    parts.Add(string.Format("本轮剩 {0} 分", leftSec / 60));
                }
                if (schedOn)
                    parts.Add(string.Format("时段剩 {0} 分", remainSec / 60));
                if (Cfg.UseQuota)
                {
                    int qMin = (int)Math.Floor(QuotaRemainSec() / 60.0);
                    if (qMin < 0) qMin = 0;
                    parts.Add(string.Format("今日剩 {0} 分", qMin));
                }
                statusItem.Text = parts.Count > 0
                    ? "状态：游玩中，" + string.Join(" / ", parts.ToArray())
                    : "状态：游玩中";

                if (schedOn && remainSec <= 0)
                {
                    StopGameNow("可玩时段结束");
                    statusItem.Text = "状态：可玩时段已结束";
                    return;
                }

                if (schedOn && nearEnd)
                {
                    // 临近时段结束：只预警，不再开新轮次/续期，到点强杀
                    string warnKey = sched.End.ToString("yyyyMMddHHmm");
                    if (WarnKey != warnKey)
                    {
                        WarnKey = warnKey;
                        int warnMin = (int)Math.Ceiling(remainSec / 60.0);
                        Log.Write(string.Format("可玩时间仅剩 {0} 分 {1} 秒，弹出预警",
                            remainSec / 60, remainSec % 60));
                        UiDialogs.ShowToast(
                            string.Format("可游玩时间剩余 {0} 分钟", warnMin),
                            sched.End.ToString("HH:mm") + " 到点将自动结束游戏\n可玩时段：" +
                                sched.Start.ToString("HH:mm") + " - " + sched.End.ToString("HH:mm"));
                    }
                }
                else if (Cfg.UseContinuous && DueTime != null && DateTime.Now >= DueTime.Value)
                {
                    InvokeCurfew();
                }

                // 配额临近用完预警：每天只弹一次（与其它模式叠加时同样生效）
                if (Cfg.UseQuota)
                {
                    double qRemain = QuotaRemainSec();
                    string todayKey2 = DateTime.Now.ToString("yyyy-MM-dd");
                    if (qRemain > 0 && qRemain <= Cfg.QuotaWarnMin * 60 && QuotaWarnKey != todayKey2)
                    {
                        QuotaWarnKey = todayKey2;
                        int qMin2 = (int)Math.Floor(qRemain / 60.0);
                        Log.Write(string.Format("今日配额仅剩约 {0} 分钟，弹出预警", qMin2));
                        UiDialogs.ShowToast("今日游戏时间即将用完",
                            string.Format("剩余约 {0} 分钟", qMin2) +
                            "\n每日配额：" + QuotaTotalMin() + " 分钟" +
                                (Cfg.TempDate == todayKey2 && Cfg.TempQuotaMin > 0 ? "（今日临时调整）" : ""));
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

        // 配额计费：游戏运行中用两次轮询的真实时间差累计；断档 >120 秒视为睡眠，不计费
        private static void QuotaTick(bool running)
        {
            if (!running) { QuotaLastTick = null; return; }
            if (QuotaLastTick == null) { QuotaLastTick = DateTime.Now; return; }
            double gap = (DateTime.Now - QuotaLastTick.Value).TotalSeconds;
            if (gap > 0 && gap <= 120)
                Cfg.QuotaUsedSec += gap;
            QuotaLastTick = DateTime.Now;
        }

        // 配额耗尽：复用守护对话框（结束 / 再玩 N 分钟），续期按天计数
        private static void QuotaExhausted()
        {
            var procs = GetGameProcesses();
            if (procs.Count == 0) return;

            int usedMin = (int)Math.Floor(Cfg.QuotaUsedSec / 60.0);
            int remainExtend = Math.Max(0, Cfg.MaxExtend - ExtendUsed);
            var activeGame = GetActiveGame();
            string gname = activeGame != null ? activeGame.Name : Cfg.GameTitle;

            Log.Write(string.Format("今日配额已用完（已玩 {0} 分钟），弹出提醒，剩余续期 {1} 次",
                usedMin, remainExtend));
            string choice = UiDialogs.ShowGuardDialog(usedMin, remainExtend, gname);

            if (choice == "extend")
            {
                ExtendUsed++;
                Cfg.QuotaExtraSec += Cfg.ExtendMinutes * 60;
                QuotaLastTick = DateTime.Now;
                if (Cfg.UseContinuous)
                {
                    RoundStart = DateTime.Now;
                    DueTime = DateTime.Now.AddMinutes(Cfg.ExtendMinutes);   // 叠加连续轮次时同步重启本轮
                }
                Cfg.Save();
                int left = Math.Max(0, Cfg.MaxExtend - ExtendUsed);
                Log.Write(string.Format("配额续期 {0} 分钟，剩余 {1} 次",
                    Cfg.ExtendMinutes, left));
                Events.Record("续期", gname + " 配额用完后续期 " + Cfg.ExtendMinutes + " 分钟（剩 " + left + " 次）");
            }
            else
            {
                string why = choice == "kill" ? "配额用完，用户选择结束" : "配额用完，弹窗超时未响应";
                Log.Write("结束游戏，原因：" + why);
                Events.Record("强制结束", gname + " " + why);
                foreach (var p in procs) { try { p.Kill(); } catch { } }
                QuotaLastTick = null;
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
                    sg.Path = exe != null ? exe : dir;
                    result.Add(sg);
                }
            }
            return result;
        }
    }

    // ==================== 对话框 ====================
    public static class UiDialogs
    {
        // ---------- 游戏图标提取（带缓存） ----------
        public static class GameIcons
        {
            static Dictionary<string, Icon> _cache = new Dictionary<string, Icon>();

            public static Icon Get(GameEntry g)
            {
                string key = g.IconPath ?? "";
                if (key.Length > 0 && _cache.ContainsKey(key))
                {
                    Icon ic = _cache[key];
                    if (ic != null) return ic;
                }
                Icon result = null;
                try
                {
                    if (!string.IsNullOrEmpty(g.IconPath) && File.Exists(g.IconPath))
                        result = Icon.ExtractAssociatedIcon(g.IconPath);
                }
                catch { }
                if (result == null)
                {
                    using (var fb = Ui.MakeGuardIcon())
                    {
                        result = (Icon)fb.Clone();
                    }
                }
                if (key.Length > 0) _cache[key] = result;
                return result;
            }

            public static void Clear()
            {
                foreach (var kv in _cache) { try { kv.Value.Dispose(); } catch { } }
                _cache.Clear();
            }
        }

        // ---------- 解析桌面快捷方式（.lnk）真实目标 ----------
        public static string ResolveShortcutTarget(string lnkPath)
        {
            try
            {
                // 使用 COM 反射创建 WScript.Shell
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                var shell = Activator.CreateInstance(shellType);
                var createShortcut = shellType.GetMethod("CreateShortcut");
                if (createShortcut == null) return null;
                var shortcut = createShortcut.Invoke(shell, new object[] { lnkPath });
                if (shortcut == null) return null;
                var targetType = shortcut.GetType();
                var targetPath = targetType.GetProperty("TargetPath");
                string tp = targetPath != null ? Convert.ToString(targetPath.GetValue(shortcut, null)) : "";
                if (string.IsNullOrEmpty(tp) || !File.Exists(tp)) return null;
                // 指向的是另一个快捷方式（常见于 UWP/特殊安装），尝试再解析一层
                if (tp.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    return ResolveShortcutTarget(tp);
                // 目录快捷方式（游戏文件夹），尝试在文件夹内找主 exe
                if (!tp.ToLower().EndsWith(".exe"))
                {
                    if (Directory.Exists(tp))
                    {
                        string found = FindExeInDir(tp, 2);
                        if (found != null) return found;
                    }
                    return null;
                }
                return tp;
            }
            catch { return null; }
        }

        static string FindExeInDir(string dir, int depth)
        {
            if (depth < 0) return null;
            string[] files = null;
            try { files = Directory.GetFiles(dir, "*.exe"); } catch { return null; }
            // 优先取体积最大的 exe（通常是主程序）
            string best = null; long bestLen = -1;
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > bestLen) { bestLen = fi.Length; best = f; }
                }
                catch { }
            }
            if (best != null) return best;
            if (depth > 0)
            {
                string[] subs = null;
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (subs != null)
                {
                    foreach (var s in subs)
                    {
                        string found = FindExeInDir(s, depth - 1);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

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

        // ---------- Toast 原生通知（Win10/11 通知中心），失败兜底托盘气泡 ----------
        public static void ShowToast(string title, string body)
        {
            Log.Write("Toast：" + title + " | " + body.Replace("\n", " / "));
            if (TryWinRTToast(title, body)) return;
            try
            {
                if (MainForm.Notify != null && MainForm.Notify.Visible)
                    MainForm.Notify.ShowBalloonTip(8000, title, body, ToolTipIcon.Info);
            }
            catch { }
        }

        private static string PsEsc(string s)
        {
            return s.Replace("'", "''");
        }

        private static bool TryWinRTToast(string title, string body)
        {
            try
            {
                // 经由系统 PowerShell 调用 WinRT Toast API（Win10/11 均内置），EncodedCommand 传参避免转义问题
                string appId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";
                string ps =
                    "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null; " +
                    "$t = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02); " +
                    "$x = $t.GetElementsByTagName('text'); " +
                    "$x.Item(0).AppendChild($t.CreateTextNode('" + PsEsc(title) + "')) | Out-Null; " +
                    "$x.Item(1).AppendChild($t.CreateTextNode('" + PsEsc(body) + "')) | Out-Null; " +
                    "$n = [Windows.UI.Notifications.ToastNotification]::new($t); " +
                    "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('" + appId + "').Show($n)";
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\WindowsPowerShell\\v1.0\\powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                // 等最多 2 秒：出错快速退出 → 气泡兜底；仍在跑 → 视为成功不阻塞
                if (p.WaitForExit(2000)) return p.ExitCode == 0;
                return true;
            }
            catch { return false; }
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

            var form = Ui.MakeForm("临时调整今天", 400, 224, false);
            Ui.AddTopBar(form, 400);

            var lbl1 = Ui.MakeRowLabel("开始时间", 20, 56, 64, 10, false, Ui.TextDark, "MiddleRight");
            var dtpStart = Ui.MakeTimePicker(90, 56, 104, baseStart);
            var lbl2 = Ui.MakeRowLabel("结束时间", 204, 56, 68, 10, false, Ui.TextDark, "MiddleRight");
            var dtpEnd = Ui.MakeTimePicker(276, 56, 104, baseEnd);
            var lblQuota = Ui.MakeRowLabel("今日配额", 20, 92, 64, 10, false, Ui.TextDark, "MiddleRight");
            var numQuota = Ui.MakeNumber(90, 92, 74, 0, 1440, Cfg.TempQuotaMin);
            var lblUnitQ = Ui.MakeRowLabel("分钟", 170, 92, 40, 9, false, Ui.SubText, "MiddleLeft");
            var lblQTip = Ui.MakeRowLabel("0 = 不调整配额（只调时段）", 216, 92, 170, 9, false, Ui.SubText, "MiddleLeft");
            var lblTip = Ui.MakeSection("临时调整（仅对今天生效，明天自动恢复）", 20, 20, 360, 24);
            var btnSave = Ui.MakeButton("今天就这样", 152, 164, 116, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 280, 164, 100, 36, false, 10);

            form.Controls.AddRange(new Control[] { lbl1, dtpStart, lbl2, dtpEnd, lblQuota, numQuota, lblUnitQ, lblQTip, lblTip, btnSave, btnCancel });

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
                Cfg.TempQuotaMin = (int)numQuota.Value;
                Engine.WarnKey = null;
                saved = true;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write(string.Format("临时调整今天：{0} - {1}，配额 {2}（仅今日生效）",
                    st, en, Cfg.TempQuotaMin > 0 ? Cfg.TempQuotaMin + " 分钟" : "不调整"));
                form.Close();
            };

            form.ShowDialog();
            form.Dispose();
            return saved;
        }

        // ---------- 退出冷静期确认（防冲动退出）----------
        // 确认按钮在 30 秒倒计时结束前不可点；取消随时可点
        public static bool ShowExitConfirm()
        {
            bool confirmed = false;

            var form = Ui.MakeForm("退出游戏时长守卫", 400, 232, false);
            Ui.AddTopBar(form, 400);

            var lblMsg = Ui.MakeLabel("退出后本次开机内不再有任何管控。\n（下次开机仍会自动启动）",
                20, 28, 360, 58, 10, false, Ui.TextDark, "MiddleLeft");
            var lblCount = Ui.MakeLabel("为避免冲动退出，请利用这段时间冷静想想",
                20, 90, 360, 26, 9, false, Ui.SubText, "MiddleLeft");
            var line = Ui.MakeLine(20, 126, 360);
            var btnYes = Ui.MakeButton("请稍候 30 秒", 96, 170, 160, 36, true, 10);
            btnYes.Enabled = false;
            var btnCancel = Ui.MakeButton("取消退出", 266, 170, 114, 36, false, 10);

            form.Controls.AddRange(new Control[] { lblMsg, lblCount, line, btnYes, btnCancel });

            int left = 30;
            var t = new System.Windows.Forms.Timer();
            t.Interval = 1000;
            t.Tick += (s, e) =>
            {
                left--;
                if (left > 0)
                {
                    btnYes.Text = "还剩 " + left + " 秒";
                }
                else
                {
                    t.Stop();
                    btnYes.Text = "确认退出";
                    btnYes.Enabled = true;
                    lblCount.Text = "冷静期已过，点「确认退出」才会真正退出";
                }
            };
            t.Start();

            btnCancel.Click += (s, e) =>
            {
                Log.Write("冷静期内取消退出，管控继续");
                Events.Record("退出", "冷静期内取消退出，管控继续");
                form.Close();
            };
            btnYes.Click += (s, e) =>
            {
                confirmed = true;
                Log.Write("冷静期结束，用户确认退出");
                Events.Record("退出", "冷静期结束，确认退出程序");
                form.Close();
            };
            form.FormClosed += (s, e) =>
            {
                try { t.Stop(); t.Dispose(); } catch { }
            };

            form.ShowDialog();
            form.Dispose();
            return confirmed;
        }

        // ---------- 本周统计 ----------
        public static void ShowStatsDialog()
        {
            // 防删改校验：必要时先从镜像备份恢复
            string integrity = "ok";
            try { integrity = Usage.VerifyAndRepair(); } catch { }

            // 读 usage.csv（每行：日期,游戏,分钟），聚合出每日、每游戏与今日时长
            var dayMin = new Dictionary<string, int>();
            var gameMin = new Dictionary<string, int>();
            var todayGameMin = new Dictionary<string, int>();
            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            try
            {
                string path = Path.Combine(Cfg.ScriptDir, "usage.csv");
                if (File.Exists(path))
                {
                    foreach (var ln in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        var seg = ln.Split(',');
                        if (seg.Length < 3) continue;
                        int mins;
                        if (!int.TryParse(seg[2], out mins) || mins <= 0) continue;
                        string d = seg[0].Trim(), g = seg[1].Trim();
                        if (!dayMin.ContainsKey(d)) dayMin[d] = 0;
                        dayMin[d] += mins;
                        if (!gameMin.ContainsKey(g)) gameMin[g] = 0;
                        gameMin[g] += mins;
                        if (d == todayKey)
                        {
                            if (!todayGameMin.ContainsKey(g)) todayGameMin[g] = 0;
                            todayGameMin[g] += mins;
                        }
                    }
                }
            }
            catch { }

            var form = Ui.MakeForm("本周游戏统计", 460, 590, false);
            Ui.AddTopBar(form, 460);

            // 事件统计：续期 / 上调 / 拦截 / 强制结束
            var evts = Events.ReadDays(7);
            int nRenew = 0, nUp = 0, nBlock = 0, nKill = 0;
            foreach (var e in evts)
            {
                string ty = e[1];
                if (ty == "续期") nRenew++;
                else if (ty == "上调") nUp++;
                else if (ty == "上调被拒" || ty == "配置防护" || ty == "记录防护") nBlock++;
                else if (ty == "强制结束") nKill++;
            }

            string[] weekNames = new string[] { "日", "一", "二", "三", "四", "五", "六" };

            // 月度趋势：近 7 天 / 近 30 天切换
            int range = 7;
            var lblSummary = Ui.MakeLabel("", 20, 22, 420, 24, 10.5, true, Ui.TextDark, "MiddleLeft");
            var rb7 = Ui.MakeRadio("近 7 天", 20, 50, 90, 26, true);
            var rb30 = Ui.MakeRadio("近 30 天", 116, 50, 110, 26, false);

            Action updateSummary = () =>
            {
                int total = 0;
                for (int i = 0; i < range; i++)
                {
                    string d = DateTime.Now.AddDays(-(range - 1 - i)).ToString("yyyy-MM-dd");
                    total += dayMin.ContainsKey(d) ? dayMin[d] : 0;
                }
                lblSummary.Text = string.Format("近 {0} 天合计 {1} 小时 {2} 分（日均 {3} 分）",
                    range, total / 60, total % 60, total / range);
            };
            updateSummary();

            var pic = new PictureBox();
            pic.Bounds = new Rectangle(Ui.U(20), Ui.U(84), Ui.U(420), Ui.U(176));
            pic.BackColor = Color.White;
            pic.Paint += (s, e) =>
            {
                var g2 = e.Graphics;
                g2.SmoothingMode = SmoothingMode.AntiAlias;
                int w = pic.ClientSize.Width, h = pic.ClientSize.Height;
                int n = range;
                int baseY = h - 34;
                int slot = w / n;
                int barW = Math.Max(6, slot - (n <= 7 ? 26 : 6));

                var vals = new int[n];
                int maxMin = 30;
                DateTime today = DateTime.Now;
                for (int i = 0; i < n; i++)
                {
                    string d = today.AddDays(-(n - 1 - i)).ToString("yyyy-MM-dd");
                    vals[i] = dayMin.ContainsKey(d) ? dayMin[d] : 0;
                    if (vals[i] > maxMin) maxMin = vals[i];
                }
                using (var penAxis = new Pen(Ui.Line, 1))
                    g2.DrawLine(penAxis, 4, baseY, w - 4, baseY);
                for (int i = 0; i < n; i++)
                {
                    int v = vals[i];
                    int x = i * slot + (slot - barW) / 2;
                    int bh = v <= 0 ? 0 : (int)Math.Max(3.0, (double)v / maxMin * (baseY - 40));
                    string dstr = today.AddDays(-(n - 1 - i)).ToString("yyyy-MM-dd");
                    bool isToday = dstr == todayKey;
                    if (bh > 0)
                    {
                        using (var br = new SolidBrush(isToday ? Ui.Primary : Color.FromArgb(120, 120, 120)))
                            g2.FillRectangle(br, x, baseY - bh, barW, bh);
                    }
                    if (n <= 7)
                    {
                        if (bh > 0)
                        {
                            string vt = v >= 60 ? (v / 60) + "时" + (v % 60) + "分" : v + "分";
                            using (var fV = new Font("Microsoft YaHei UI", 7.5f))
                            using (var brT = new SolidBrush(Ui.TextDark))
                                g2.DrawString(vt, fV, brT, x - 4, baseY - bh - 15);
                        }
                        var d = DateTime.ParseExact(dstr, "yyyy-MM-dd", null);
                        using (var fD = new Font("Microsoft YaHei UI", 7.5f))
                        using (var brD = new SolidBrush(isToday ? Ui.Primary : Ui.SubText))
                            g2.DrawString(d.ToString("M-d") + " 周" + weekNames[(int)d.DayOfWeek],
                                fD, brD, x - 6, baseY + 4);
                    }
                    else if (i % 5 == 0 || i == n - 1)
                    {
                        var d = DateTime.ParseExact(dstr, "yyyy-MM-dd", null);
                        using (var fD = new Font("Microsoft YaHei UI", 7f))
                        using (var brD = new SolidBrush(isToday ? Ui.Primary : Ui.SubText))
                            g2.DrawString(d.ToString("M-d"), fD, brD, x - 8, baseY + 4);
                    }
                }
                // 30 天视图：叠加 7 日移动平均趋势线（红线）
                if (n > 7)
                {
                    var pts = new List<PointF>();
                    for (int i = 0; i < n; i++)
                    {
                        int start = Math.Max(0, i - 6);
                        int sum = 0, c = 0;
                        for (int j = start; j <= i; j++) { sum += vals[j]; c++; }
                        double avg = (double)sum / c;
                        int bh = (int)Math.Max(2.0, avg / maxMin * (baseY - 40));
                        pts.Add(new PointF(i * slot + slot / 2f, baseY - bh));
                    }
                    using (var penT = new Pen(Ui.Primary, 2))
                        g2.DrawLines(penT, pts.ToArray());
                }
            };
            form.Controls.Add(pic);
            form.Controls.AddRange(new Control[] { rb7, rb30 });
            rb7.CheckedChanged += (s, e) => { if (rb7.Checked) { range = 7; updateSummary(); pic.Invalidate(); } };
            rb30.CheckedChanged += (s, e) => { if (rb30.Checked) { range = 30; updateSummary(); pic.Invalidate(); } };

            // 每个游戏合计
            var bandGame = Ui.MakeSection("各游戏合计", 20, 262, 420, 24);
            var list = new ListBox();
            list.Bounds = new Rectangle(Ui.U(20), Ui.U(290), Ui.U(420), Ui.U(76));
            list.Font = new Font("Microsoft YaHei UI", 9);
            list.BorderStyle = BorderStyle.FixedSingle;
            var sorted = new List<KeyValuePair<string, int>>(gameMin);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (sorted.Count == 0)
                list.Items.Add("还没有使用记录——游戏运行满 1 分钟后这里会出现数据");
            if (integrity == "restored")
                list.Items.Add("！使用记录曾被删改，已自动从备份恢复");
            if (integrity == "damaged")
                list.Items.Add("！使用记录损坏且无法恢复，已重新开始记录（原文件留存为 usage.bad.csv）");
            foreach (var kv in sorted)
            {
                string row = string.Format("{0}    合计 {1} 小时 {2} 分", kv.Key, kv.Value / 60, kv.Value % 60);
                int td = todayGameMin.ContainsKey(kv.Key) ? todayGameMin[kv.Key] : 0;
                if (td > 0) row += string.Format("  ·  今日 {0} 分", td);
                list.Items.Add(row);
            }
            form.Controls.Add(bandGame);
            form.Controls.Add(list);

            // 近 7 天事件
            var bandEv = Ui.MakeSection(string.Format(
                "本周事件：续期 {0} 次 · 上调 {1} 次 · 拦截 {2} 次 · 强制结束 {3} 次",
                nRenew, nUp, nBlock, nKill), 20, 372, 420, 24);

            var evList = new ListBox();
            evList.Bounds = new Rectangle(Ui.U(20), Ui.U(400), Ui.U(420), Ui.U(112));
            evList.Font = new Font("Microsoft YaHei UI", 8.5f);
            evList.BorderStyle = BorderStyle.FixedSingle;
            if (evts.Count == 0)
                evList.Items.Add("本周没有事件记录（续期/上调/拦截/结束都会出现在这里）");
            for (int i = evts.Count - 1; i >= 0 && i > evts.Count - 21; i--)
            {
                var e = evts[i];
                evList.Items.Add(string.Format("{0}  [{1}]  {2}",
                    e[0].Length >= 16 ? e[0].Substring(11, 5) : e[0], e[1], e[2]));
            }
            form.Controls.Add(evList);

            var line = Ui.MakeLine(20, 520, 420);
            var btnClose = Ui.MakeButton("关闭", 340, 528, 100, 36, false, 10);
            btnClose.Click += (s, e) => form.Close();
            form.Controls.AddRange(new Control[] { lblSummary, bandEv, line, btnClose });

            form.ShowDialog();
            form.Dispose();
        }

        // ---------- 管理游戏列表 ----------
        public static void ShowGamesDialog(List<GameEntry> gamesSnapshot)
        {
            List<GameEntry> gTemp = gamesSnapshot
                .Select(g => new GameEntry(g.Name, g.Processes, g.PathFilter, g.IconPath)).ToList();

            var form = Ui.MakeForm("管理游戏列表", 460, 404, false);
            Ui.AddTopBar(form, 460);

            var lblTip = Ui.MakeSection("游戏列表（共用同一套时长规则 · 支持拖入桌面快捷方式）", 20, 18, 420, 24);

            var list = new ListBox();
            list.Bounds = new Rectangle(Ui.U(20), Ui.U(48), Ui.U(420), Ui.U(226));
            list.Font = new Font("Microsoft YaHei UI", 10);
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = 42;
            list.AllowDrop = true;
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var g = (GameEntry)list.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (var bg = new SolidBrush(sel ? Color.FromArgb(252, 235, 235) : Color.White))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                var icon = GameIcons.Get(g);
                using (var bmp = icon.ToBitmap())
                    e.Graphics.DrawImage(bmp, e.Bounds.X + 6, e.Bounds.Y + 5, 32, 32);
                using (var fTitle = new Font("Microsoft YaHei UI", 10, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, g.Name, fTitle,
                        new Point(e.Bounds.X + 46, e.Bounds.Y + 4), Ui.TextDark);
                TextRenderer.DrawText(e.Graphics,
                    "[" + string.Join(", ", g.Processes) + "]", list.Font,
                    new Point(e.Bounds.X + 46, e.Bounds.Y + 22), Ui.SubText);
                e.DrawFocusRectangle();
            };

            var btnSteam = Ui.MakeButton("从 Steam 库添加", 20, 284, 136, 34, false, 10);
            var btnManual = Ui.MakeButton("手动添加", 166, 284, 100, 34, false, 10);
            var btnDel = Ui.MakeButton("删除选中", 276, 284, 100, 34, false, 10);
            var line = Ui.MakeLine(20, 334, 420);
            var btnSave = Ui.MakeButton("保存并关闭", 208, 346, 124, 36, true, 10);
            var btnCancel = Ui.MakeButton("取消", 344, 346, 96, 36, false, 10);

            Action refresh = () =>
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var g in gTemp) list.Items.Add(g);
                list.EndUpdate();
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

            // ---------- 拖拽 .lnk / .exe 添加游戏 ----------
            list.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            list.DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null) return;
                int added = 0, skipped = 0;
                foreach (var f in files)
                {
                    string target = null;
                    bool isLnk = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                    if (isLnk)
                        target = ResolveShortcutTarget(f);
                    else if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
                        target = f;
                    if (string.IsNullOrEmpty(target)) { skipped++; continue; }

                    string procName = Path.GetFileNameWithoutExtension(target);
                    string gameName = procName;
                    if (isLnk)
                    {
                        string lnkName = Path.GetFileNameWithoutExtension(f);
                        if (!string.IsNullOrEmpty(lnkName)) gameName = lnkName;
                    }
                    bool dup = false;
                    foreach (var g in gTemp)
                        if (g.Name == gameName)
                        {
                            dup = true; break;
                        }
                        else
                        {
                            foreach (var p in g.Processes)
                                if (p == procName) { dup = true; break; }
                            if (dup) break;
                        }
                    if (dup) { skipped++; continue; }
                    gTemp.Add(new GameEntry(gameName, new string[] { procName }, new string[0], target));
                    added++;
                }
                refresh();
                if (skipped > 0)
                    MessageBox.Show(form,
                        string.Format("添加 {0} 个，跳过 {1} 个。\n\n跳过原因：重名/重复，或快捷方式无法解析出有效游戏程序。",
                            added, skipped),
                        "拖拽添加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            btnSteam.Click += (s, e) =>
            {
                var picked = ShowPickSteamGames();
                if (picked.Count == 0) return;
                foreach (var pg in picked)
                {
                    if (string.IsNullOrEmpty(pg.Exe)) continue;
                    if (gTemp.Any(g => g.Name == pg.Name)) continue;
                    gTemp.Add(new GameEntry(pg.Name, new string[] { pg.Exe }, new string[0], pg.Path));
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
        private static CheckBox _cbSched, _cbContMode, _cbQuota;
        private static DateTimePicker _dtpStart, _dtpEnd;
        private static NumericUpDown _numWarn, _numPlay, _numExtend, _numMax;
        private static NumericUpDown _numQuota, _numQWarn;
        private static CheckBox[] _dayChecks;
        private static Button _btnPause;
        private static System.Windows.Forms.Timer _timer, _tipTimer;
        private static bool _radioGuard = false;

        public static string[] GetStatusLines()
        {
            string modeText = "模式：" + Cfg.ModeName;
            if (Cfg.UseSchedule)
                modeText += string.Format("（{0}-{1}", Cfg.ScheduleStart, Cfg.ScheduleEnd);
            if (Cfg.UseContinuous)
                modeText += (modeText.Contains("（") ? "+" : "（") + string.Format("连续{0}分/轮", Cfg.PlayMinutes);
            if (Cfg.UseQuota)
                modeText += (modeText.Contains("（") ? "+" : "（") + string.Format("配额{0}分/天", Engine.QuotaTotalMin());
            if (modeText.Contains("（")) modeText += "）";

            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            if (Cfg.TempDate == todayKey && !string.IsNullOrEmpty(Cfg.TempStart) && !string.IsNullOrEmpty(Cfg.TempEnd))
                modeText += string.Format("  |  今日临时 {0}-{1}", Cfg.TempStart, Cfg.TempEnd);
            if (Cfg.TempDate == todayKey && Cfg.TempQuotaMin > 0)
                modeText += string.Format("  |  今日配额 {0} 分", Cfg.TempQuotaMin);
            if (Engine.GetPausedState()) modeText += "  |  已暂停";

            string gameText;
            var procs = Engine.GetGameProcesses();
            if (procs.Count > 0)
            {
                var names = Engine.GetRunningGameNames();
                string nm = names.Count == 0 ? Cfg.GameTitle
                    : (names.Count == 1 ? names[0] : names[0] + " 等 " + names.Count + " 个游戏");
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
            if (Cfg.UseQuota)
            {
                int usedMin = (int)Math.Floor(Cfg.QuotaUsedSec / 60.0);
                int remainMin = (int)Math.Ceiling(Engine.QuotaRemainSec() / 60.0);
                if (remainMin < 0) remainMin = 0;
                gameText += string.Format("  |  今日已用 {0} 分，剩 {1} 分", usedMin, remainMin);
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
                _cbSched.Checked = Cfg.UseSchedule;
                _cbContMode.Checked = Cfg.UseContinuous;
                _cbQuota.Checked = Cfg.UseQuota;
                _radioGuard = false;
                _btnPause.Text = Engine.GetPausedState() ? "恢复管控" : "暂停 1 小时";
            }
            catch { }
        }

        public static Form Current { get { return _panel; } }

        // 上调审批（数值型：配额 / 连续时长）：delta<=0 或小幅上调直接放行；>=30 分钟弹窗确认；当日已上调过则拒绝
        // 规则：每个数字每天只能上调 1 次（下调不限）
        private static bool ApproveUp(Form owner, string name, int oldV, int newV, int usedToday)
        {
            int delta = newV - oldV;
            if (delta <= 0) return true;
            if (usedToday >= 1)
            {
                MessageBox.Show(owner,
                    string.Format("【{0}】今天已经上调过 1 次。\n\n规则说明：每个数字每天只能上调 1 次（下调随时可以）。\n本次上调不会生效；如确需上调，请明天再改。",
                        name),
                    "每日上调上限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log.Write(string.Format("上调被拒绝：{0} {1}→{2}（今日已上调 {3} 次）", name, oldV, newV, usedToday));
                Events.Record("上调被拒", name + " " + oldV + "→" + newV + "（当日额度已用完）");
                return false;
            }
            if (delta >= 30)
            {
                var ans = MessageBox.Show(owner,
                    string.Format("你正在把【{0}】从 {1} 上调到 {2}（+{3} 分钟）。\n\n上调规则说明：\n· 上调幅度 ≥30 分钟会弹出本确认\n· 每个数字每天只能上调 1 次（下调随时可以）\n· 确认后，{0} 今天就不能再上调了\n\n确认上调吗？",
                        name, oldV, newV, delta),
                    "上调确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                Log.Write(string.Format("上调确认：{0} {1}→{2}（+{3}），用户选择{4}",
                    name, oldV, newV, delta, ans == DialogResult.Yes ? "确认" : "取消"));
                if (ans == DialogResult.Yes) Events.Record("上调", name + " " + oldV + "→" + newV + "（+" + delta + "）");
                return ans == DialogResult.Yes;
            }
            return true;
        }

        // 时段上调审批（双边缘判定）：
        //   开始时间提前 ≥30 分钟 / 结束时间推后 ≥30 分钟 / 总时长增加 ≥30 分钟，任一命中即视为上调
        // 规则：时段每天只能上调 1 次（缩短随时可以）
        private static bool ApproveSched(Form owner, DateTime oldS, DateTime oldE, DateTime newS, DateTime newE, int usedToday)
        {
            bool startEarlier = newS < oldS.AddMinutes(-30);          // 开始时间提前超 30 分钟
            bool endLater = newE > oldE.AddMinutes(30);               // 结束时间推后超 30 分钟
            int oldLen = (int)(oldE - oldS).TotalMinutes;
            int newLen = (int)(newE - newS).TotalMinutes;
            bool lenGrow = (newLen - oldLen) >= 30;                   // 两端各挪一点但总时长明显增加
            if (!startEarlier && !endLater && !lenGrow) return true;

            if (usedToday >= 1)
            {
                MessageBox.Show(owner,
                    "【可玩时段】今天已经上调过 1 次。\n\n规则说明：时段每天只能上调 1 次（开始时间提前、结束时间推后、总时长增加都算上调；缩短随时可以）。\n本次调整不会生效；如确需上调，请明天再改。",
                    "每日上调上限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log.Write(string.Format("上调被拒绝：可玩时段 {0:HH:mm}-{1:HH:mm}→{2:HH:mm}-{3:HH:mm}（今日已上调 {4} 次）",
                    oldS, oldE, newS, newE, usedToday));
                Events.Record("上调被拒", "时段 " + oldS.ToString("HH:mm") + "-" + oldE.ToString("HH:mm") +
                    " → " + newS.ToString("HH:mm") + "-" + newE.ToString("HH:mm") + "（当日额度已用完）");
                return false;
            }

            string detail = "";
            if (startEarlier) detail += string.Format("· 开始时间提前 {0} 分钟\n", (int)(oldS - newS).TotalMinutes);
            if (endLater) detail += string.Format("· 结束时间推后 {0} 分钟\n", (int)(newE - oldE).TotalMinutes);
            if (!startEarlier && !endLater && lenGrow)
                detail += string.Format("· 总时长增加 {0} 分钟\n", newLen - oldLen);
            detail += string.Format("· 时段总长 {0} → {1} 分钟", oldLen, newLen);

            var ans = MessageBox.Show(owner,
                string.Format("你正在扩大可玩时段：\n\n{0}\n上调规则说明：\n· 开始时间提前 / 结束时间推后 / 总时长增加 ≥30 分钟都会弹出本确认\n· 时段每天只能上调 1 次（缩短随时可以）\n· 确认后，时段今天就不能再上调了\n\n确认上调吗？",
                    detail),
                "上调确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            Log.Write(string.Format("上调确认：可玩时段 {0:HH:mm}-{1:HH:mm}→{2:HH:mm}-{3:HH:mm}，用户选择{4}",
                oldS, oldE, newS, newE, ans == DialogResult.Yes ? "确认" : "取消"));
            if (ans == DialogResult.Yes)
                Events.Record("上调", "时段 " + oldS.ToString("HH:mm") + "-" + oldE.ToString("HH:mm") +
                    " → " + newS.ToString("HH:mm") + "-" + newE.ToString("HH:mm") + "（" + oldLen + "→" + newLen + " 分钟）");
            return ans == DialogResult.Yes;
        }

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

            var form = Ui.MakeForm("游戏时长守卫", 420, 648, false);
            form.TopMost = false;
            Ui.AddTopBar(form, 420);

            var lblTitle = Ui.MakeLabel("游戏时长守卫", 20, 14, 300, 26, 15, true, Ui.TextDark, "MiddleLeft");
            var lblMode = Ui.MakeLabel("", 20, 42, 380, 22, 9, false, Ui.SubText, "MiddleLeft");
            var lblGame = Ui.MakeLabel("", 20, 64, 380, 22, 9, false, Ui.TextDark, "MiddleLeft");
            var line1 = Ui.MakeLine(20, 92, 380);

            var cbSched = Ui.MakeCheck("固定时段", 20, 100, 110, 28, Cfg.UseSchedule);
            var cbContMode = Ui.MakeCheck("连续时长", 140, 100, 120, 28, Cfg.UseContinuous);
            var cbQuota = Ui.MakeCheck("每日配额", 270, 100, 110, 28, Cfg.UseQuota);
            var line2 = Ui.MakeLine(20, 136, 380);

            var secA = Ui.MakeSection("可玩时段", 20, 140, 380, 26);

            // 栅格：标签列 20..98（右对齐），输入列从 104 开始；第二列标签右边缘 288，输入从 294 开始
            var lbl1 = Ui.MakeRowLabel("开始时间", 20, 168, 78, 10, false, Ui.TextDark, "MiddleRight");
            var dtpStart = Ui.MakeTimePicker(104, 168, 100, Cfg.ScheduleStart);
            var lbl2 = Ui.MakeRowLabel("结束时间", 210, 168, 78, 10, false, Ui.TextDark, "MiddleRight");
            var dtpEnd = Ui.MakeTimePicker(294, 168, 100, Cfg.ScheduleEnd);

            var lbl3 = Ui.MakeRowLabel("结束前预警", 20, 204, 78, 10, false, Ui.TextDark, "MiddleRight");
            var numWarn = Ui.MakeNumber(104, 204, 74, 1, 60, Cfg.ScheduleWarnMin);
            var lblUnitWarn = Ui.MakeRowLabel("分钟", 184, 204, 40, 9, false, Ui.SubText, "MiddleLeft");

            var lbl5 = Ui.MakeLabel("生效星期", 20, 241, 78, 30, 10, false, Ui.TextDark, "MiddleRight");
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

            var secB = Ui.MakeSection("连续时长", 20, 284, 380, 26);

            var lblPlay = Ui.MakeRowLabel("连续时长", 20, 312, 78, 10, false, Ui.TextDark, "MiddleRight");
            var numPlay = Ui.MakeNumber(104, 312, 74, 5, 480, Cfg.PlayMinutes);
            var lblUnitPlay = Ui.MakeRowLabel("分钟", 184, 312, 40, 9, false, Ui.SubText, "MiddleLeft");

            var lblExt = Ui.MakeRowLabel("每次续期", 20, 348, 78, 10, false, Ui.TextDark, "MiddleRight");
            var numExtend = Ui.MakeNumber(104, 348, 74, 1, 60, Cfg.ExtendMinutes);
            var lblUnitExt = Ui.MakeRowLabel("分钟", 184, 348, 40, 9, false, Ui.SubText, "MiddleLeft");
            var lblMax = Ui.MakeRowLabel("最多", 240, 348, 48, 10, false, Ui.TextDark, "MiddleRight");
            var numMax = Ui.MakeNumber(294, 348, 74, 0, 10, Cfg.MaxExtend);
            var lblUnitMax = Ui.MakeRowLabel("次", 374, 348, 26, 9, false, Ui.SubText, "MiddleLeft");

            var cbStackTip = Ui.MakeLabel("三种模式可同时勾选、叠加生效，至少保留一种\n上调规则：每个数字每天只能上调 1 次，上调 ≥30 分钟需确认，下调不限", 20, 384, 380, 34, 9, false, Ui.SubText, "MiddleLeft");
            var line4 = Ui.MakeLine(20, 420, 380);

            var secC = Ui.MakeSection("每日配额", 20, 424, 380, 26);

            var lblQuota = Ui.MakeRowLabel("每日配额", 20, 452, 78, 10, false, Ui.TextDark, "MiddleRight");
            var numQuota = Ui.MakeNumber(104, 452, 74, 5, 1440, Cfg.QuotaMinutes);
            var lblUnitQuota = Ui.MakeRowLabel("分钟", 184, 452, 40, 9, false, Ui.SubText, "MiddleLeft");
            var lblQWarn = Ui.MakeRowLabel("预警", 240, 452, 48, 10, false, Ui.TextDark, "MiddleRight");
            var numQWarn = Ui.MakeNumber(294, 452, 74, 1, 120, Cfg.QuotaWarnMin);
            var lblUnitQWarn = Ui.MakeRowLabel("分钟", 374, 452, 40, 9, false, Ui.SubText, "MiddleLeft");
            var lblQTip = Ui.MakeLabel("当天累计玩满即结束，续期沿用上面的续期设置", 20, 488, 380, 22, 9, false, Ui.SubText, "MiddleLeft");
            var line6 = Ui.MakeLine(20, 512, 380);

            var btnGames = Ui.MakeButton("管理游戏列表", 20, 520, 120, 34, false, 10);
            var btnTemp = Ui.MakeButton("临时调整今天", 152, 520, 120, 34, false, 10);
            var btnPause = Ui.MakeButton("暂停 1 小时", 284, 520, 116, 34, false, 10);
            var btnKill = Ui.MakeButton("立即结束游戏", 20, 562, 120, 34, false, 10);
            var btnExit = Ui.MakeButton("退出程序", 152, 562, 100, 34, false, 10);
            var btnStats = Ui.MakeButton("本周统计", 262, 562, 118, 34, false, 10);
            var line5 = Ui.MakeLine(20, 606, 380);

            var lblTip = Ui.MakeLabel("改完点保存生效", 20, 619, 130, 26, 9, false, Ui.SubText, "MiddleLeft");
            var btnSave = Ui.MakeButton("保存设置", 168, 614, 120, 36, true, 10);
            var btnClose = Ui.MakeButton("关闭", 300, 614, 100, 36, false, 10);

            form.Controls.AddRange(new Control[] {
                lblTitle, lblMode, lblGame, line1, cbSched, cbContMode, cbQuota, line2,
                secA, lbl1, dtpStart, lbl2, dtpEnd, lbl3, numWarn, lblUnitWarn, lbl5,
                line3, secB, lblPlay, numPlay, lblUnitPlay, lblExt, numExtend, lblUnitExt,
                lblMax, numMax, lblUnitMax, cbStackTip, line4,
                secC, lblQuota, numQuota, lblUnitQuota, lblQWarn, numQWarn, lblUnitQWarn, lblQTip, line6,
                btnGames, btnTemp, btnPause,
                btnKill, btnExit, btnStats, line5, lblTip, btnSave, btnClose });

            _panel = form;
            _lblMode = lblMode; _lblGame = lblGame;
            _cbSched = cbSched; _cbContMode = cbContMode; _cbQuota = cbQuota;
            _dtpStart = dtpStart; _dtpEnd = dtpEnd;
            _numWarn = numWarn; _numPlay = numPlay;
            _numExtend = numExtend; _numMax = numMax;
            _numQuota = numQuota; _numQWarn = numQWarn;
            _dayChecks = dayChecks;
            _btnPause = btnPause; _lblTip = lblTip;

            // 模式开关：立即生效；至少保留一种模式
            _radioGuard = true;
            cbSched.CheckedChanged += (s, e) =>
            {
                if (_radioGuard) return;
                if (!cbSched.Checked && !cbContMode.Checked && !cbQuota.Checked)
                {
                    MessageBox.Show(form, "至少需要启用一种模式。", "游戏时长守卫");
                    _radioGuard = true; cbSched.Checked = true; _radioGuard = false;
                    return;
                }
                _radioGuard = true;
                Cfg.UseSchedule = cbSched.Checked;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("模式调整：现为 " + Cfg.ModeName);
                _radioGuard = false;
                UpdateState();
            };
            cbContMode.CheckedChanged += (s, e) =>
            {
                if (_radioGuard) return;
                if (!cbSched.Checked && !cbContMode.Checked && !cbQuota.Checked)
                {
                    MessageBox.Show(form, "至少需要启用一种模式。", "游戏时长守卫");
                    _radioGuard = true; cbContMode.Checked = true; _radioGuard = false;
                    return;
                }
                _radioGuard = true;
                Cfg.UseContinuous = cbContMode.Checked;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("模式调整：现为 " + Cfg.ModeName);
                _radioGuard = false;
                UpdateState();
            };
            cbQuota.CheckedChanged += (s, e) =>
            {
                if (_radioGuard) return;
                if (!cbSched.Checked && !cbContMode.Checked && !cbQuota.Checked)
                {
                    MessageBox.Show(form, "至少需要启用一种模式。", "游戏时长守卫");
                    _radioGuard = true; cbQuota.Checked = true; _radioGuard = false;
                    return;
                }
                _radioGuard = true;
                Cfg.UseQuota = cbQuota.Checked;
                Engine.DueTime = null; Engine.RoundStart = null;
                Engine.ExtendUsed = 0; Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write("模式调整：现为 " + Cfg.ModeName);
                _radioGuard = false;
                UpdateState();
            };
            _radioGuard = false;

            // 保存设置
            btnSave.Click += (s, e) =>
            {
                string st = _dtpStart.Value.ToString("HH:mm");
                string en = _dtpEnd.Value.ToString("HH:mm");
                if (Cfg.UseSchedule && st == en)
                {
                    MessageBox.Show(form, "开始时间和结束时间不能相同。", "游戏时长设置");
                    return;
                }
                var days = new List<int>();
                for (int i = 0; i < 7; i++)
                    if (_dayChecks[i].Checked) days.Add(i);
                if (Cfg.UseSchedule && days.Count == 0)
                {
                    MessageBox.Show(form, "固定时段模式下至少选择一个生效星期。", "游戏时长设置");
                    return;
                }

                // ---- 上调限制：配额 / 时段 / 连续时长，每个数字每天只能上调 1 次 ----
                Cfg.EnsureModLimitDate();
                int oldQuota = Cfg.QuotaMinutes, newQuota = (int)_numQuota.Value;
                int oldPlay = Cfg.PlayMinutes, newPlay = (int)_numPlay.Value;

                // 时段边缘比较（支持跨零点）：开始提前 / 结束推后 / 总长增加，任一 ≥30 分钟都算上调
                DateTime oldS, oldE, newS, newE;
                oldS = DateTime.ParseExact("2000-01-01 " + Cfg.ScheduleStart, "yyyy-MM-dd HH:mm", null);
                oldE = DateTime.ParseExact("2000-01-01 " + Cfg.ScheduleEnd, "yyyy-MM-dd HH:mm", null);
                if (oldE <= oldS) oldE = oldE.AddDays(1);
                newS = DateTime.ParseExact("2000-01-01 " + st, "yyyy-MM-dd HH:mm", null);
                newE = DateTime.ParseExact("2000-01-01 " + en, "yyyy-MM-dd HH:mm", null);
                if (newE <= newS) newE = newE.AddDays(1);

                bool okQuota = ApproveUp(form, "每日配额", oldQuota, newQuota, Cfg.QuotaUpCount);
                bool okPlay = ApproveUp(form, "连续时长", oldPlay, newPlay, Cfg.PlayUpCount);
                bool okSched = ApproveSched(form, oldS, oldE, newS, newE, Cfg.SchedUpCount);
                if (!okQuota) _numQuota.Value = oldQuota;
                if (!okPlay) _numPlay.Value = oldPlay;
                if (!okSched)
                {
                    st = Cfg.ScheduleStart;
                    en = Cfg.ScheduleEnd;
                    try
                    {
                        _dtpStart.Value = DateTime.ParseExact("2000-01-01 " + st, "yyyy-MM-dd HH:mm", null);
                        _dtpEnd.Value = DateTime.ParseExact("2000-01-01 " + en, "yyyy-MM-dd HH:mm", null);
                    }
                    catch { }
                }
                if (newQuota > oldQuota && okQuota) Cfg.QuotaUpCount++;
                if (newPlay > oldPlay && okPlay) Cfg.PlayUpCount++;
                if (okSched && (newS < oldS || newE > oldE)) Cfg.SchedUpCount++;   // 任一边缘扩大才计数

                Cfg.ScheduleStart = st;
                Cfg.ScheduleEnd = en;
                Cfg.ScheduleWarnMin = (int)_numWarn.Value;
                Cfg.ScheduleDays = days;
                Cfg.PlayMinutes = (int)_numPlay.Value;
                Cfg.ExtendMinutes = (int)_numExtend.Value;
                Cfg.MaxExtend = (int)_numMax.Value;
                Cfg.QuotaMinutes = (int)_numQuota.Value;
                Cfg.QuotaWarnMin = (int)_numQWarn.Value;
                Engine.WarnKey = null;
                Cfg.Save();
                MainForm.UpdateTrayMenuText();
                Log.Write(string.Format(
                    "面板保存设置：时段 {0}-{1}，预警 {2} 分，连续 {3} 分，续期 {4} 分×{5} 次，配额 {6} 分/天，星期 {7}（今日上调：配额 {8} 次/时段 {9} 次/连续 {10} 次）",
                    st, en, Cfg.ScheduleWarnMin, Cfg.PlayMinutes, Cfg.ExtendMinutes, Cfg.MaxExtend,
                    Cfg.QuotaMinutes, string.Join(",", days),
                    Cfg.QuotaUpCount, Cfg.SchedUpCount, Cfg.PlayUpCount));
                UpdateState();
                _lblTip.Text = "已保存 ✓";
                _lblTip.ForeColor = Ui.Green;
                try { _tipTimer.Stop(); _tipTimer.Start(); } catch { }
            };

            // 快捷操作
            btnGames.Click += (s, e) => { UiDialogs.ShowGamesDialog(Cfg.Games); UpdateState(); };
            btnTemp.Click += (s, e) => { UiDialogs.ShowTempDialog(); UpdateState(); };
            btnStats.Click += (s, e) =>
            {
                try { UiDialogs.ShowStatsDialog(); }
                catch (Exception ex) { Log.Write("打开统计失败：" + ex.Message); }
            };

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
                Engine.StopGameNow("面板手动结束");
                UpdateState();
            };

            btnExit.Click += (s, e) =>
            {
                if (!UiDialogs.ShowExitConfirm()) return;
                Usage.FinalFlush();
                try
                {
                    File.WriteAllText(Path.Combine(Cfg.ScriptDir, "watchdog_off.flag"), "off", Encoding.ASCII);
                    Log.Write("看门狗已暂停（正常退出），下次启动时恢复");
                }
                catch { }
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

        public static void UpdateTrayMenuText()
        {
            // 右键菜单已精简为仅退出，这里只维护托盘悬浮文字
            try
            {
                Notify.Text = "游戏时长守卫（" + Cfg.ModeName + "）";
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

            // 正常启动：清除「看门狗暂停」标志，看门狗恢复值守（正常退出时会写入该标志）
            try
            {
                string wdFlag = Path.Combine(Cfg.ScriptDir, "watchdog_off.flag");
                if (File.Exists(wdFlag))
                {
                    File.Delete(wdFlag);
                    Log.Write("看门狗已恢复值守");
                }
            }
            catch { }

            // 使用记录与事件记录完整性检查（防删改，能恢复则恢复）
            try { Usage.VerifyAndRepair(); } catch { }
            try { Events.VerifyAndRepair(); } catch { }

            Notify = new NotifyIcon();
            try
            {
                Notify.Icon = Ui.MakeGuardIcon();
                Notify.Text = "游戏时长守卫（运行中）";
                Notify.Visible = true;
                Log.Write("托盘图标已创建");
                var tips = new List<string>();
                if (Cfg.UseSchedule)
                    tips.Add(string.Format("可玩时段 {0} - {1}", Cfg.ScheduleStart, Cfg.ScheduleEnd));
                if (Cfg.UseContinuous)
                    tips.Add(string.Format("每轮 {0} 分钟会提醒", Cfg.PlayMinutes));
                if (Cfg.UseQuota)
                    tips.Add(string.Format("每天最多 {0} 分钟", Cfg.QuotaMinutes));
                string tipText = (tips.Count > 0 ? string.Join("；", tips.ToArray()) : "未启用任何模式")
                    + "。左键点击图标打开控制面板。";
                Notify.ShowBalloonTip(4000, "游戏时长守卫已启动", tipText, ToolTipIcon.Info);
                // 防手改结果的启动提示
                if (Cfg.TamperRestored)
                    UiDialogs.ShowToast("配置已恢复",
                        "检测到 config.json 被外部修改，已自动恢复为最近一次保存的配置。");
                else if (Cfg.TamperReset)
                    UiDialogs.ShowToast("配置已重置",
                        "检测到 config.json 被外部修改且无有效备份，已重置为默认配置（原文件留存为 config.json.bad）。");
            }
            catch (Exception ex)
            {
                Log.Write("托盘图标创建失败：" + ex.Message);
            }

            var menu = new ContextMenuStrip();

            // 右键菜单仅保留：状态显示（只读）+ 退出。全部设置功能集中在左键控制面板
            ItemStatus = new ToolStripMenuItem("状态：等待游戏启动");
            ItemStatus.Enabled = false;
            menu.Items.Add(ItemStatus);
            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出程序");
            menu.Items.Add(itemExit);
            Notify.ContextMenuStrip = menu;

            // 左键点击 → 主控制面板；右键仅退出
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
            itemExit.Click += (s, e) =>
            {
                if (!UiDialogs.ShowExitConfirm()) return;
                Usage.FinalFlush();
                try
                {
                    File.WriteAllText(Path.Combine(Cfg.ScriptDir, "watchdog_off.flag"), "off", Encoding.ASCII);
                    Log.Write("看门狗已暂停（正常退出），下次启动时恢复");
                }
                catch { }
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
            Cfg.ConfigSumPath = Path.Combine(Cfg.ScriptDir, "config.sum");
            Cfg.ConfigBakPath = Path.Combine(Cfg.ScriptDir, "config.bak.json");
            Cfg.PauseFlag = Path.Combine(Cfg.ScriptDir, "paused.flag");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
                Log.Write("UI 异常：" + e.Exception.Message);

            // 自检模式：--snap 截图后退出（用于界面核对）；--toast 发送一条测试通知后退出
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (string.Equals(a, "--snap", StringComparison.OrdinalIgnoreCase))
                    {
                        RunSnap();
                        return;
                    }
                    if (string.Equals(a, "--toast", StringComparison.OrdinalIgnoreCase))
                    {
                        Cfg.Load();
                        UiDialogs.ShowToast("Toast 通知测试", "如果你在屏幕右下角/通知中心看到这条消息，说明原生通知工作正常。");
                        System.Threading.Thread.Sleep(3000);   // 给 PowerShell 子进程留出发送时间
                        return;
                    }
                }
            }

            MainForm.Run();
        }
    }
}
