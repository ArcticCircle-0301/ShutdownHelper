// ============================================================================
// 定时关机助手 (ShutdownHelper)  ——  单文件完整版
// ----------------------------------------------------------------------------
// 功能：
//   1. 多组定时计划：名称 / 操作类型(关机·重启·注销·睡眠) / 执行星期(多选)
//      / 执行时间 / 是否询问 / 询问等待秒数
//   2. 支持"按指定日期执行"（yyyy-MM-dd），勾选后仅在该日触发一次
//   3. 到点弹出环形倒计时询问窗口：深色背景 + 蓝→红渐变进度环 + 数字倒计时
//      + 最后5秒闪红；超时自动执行，点"取消"本次不执行
//   4. 自动写入 Windows 任务计划程序：睡眠时唤醒执行、错过补跑，
//      关闭本软件后依然生效
//   5. 配置持久化到程序同目录 config.json
//   6. 首次运行自动创建默认计划（周一~周四、周日 22:58 关机询问）
//
// 用法（编译）：
//   csc /nologo /target:winexe /out:ShutdownHelper.exe /reference:... ShutdownHelper.cs
//   详见同目录 compile.ps1（可一键编译）
//
// 用法（运行）：
//   ShutdownHelper.exe          启动图形管理界面
//   ShutdownHelper.exe -run <id>   由计划任务触发执行（按计划设置弹窗或直接执行）
//   ShutdownHelper.exe -preview <id> 预览询问窗口（测试模式，不真正执行）
//
// 环境要求：Windows 7 及以上，无需安装任何额外软件
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ===== 程序集信息（右键 exe → 属性可看到，正规版本信息可降低安全软件误报）=====
[assembly: AssemblyTitle("定时关机助手")]
[assembly: AssemblyDescription("定时电源管理工具：定时关机/重启/注销/睡眠，带环形倒计时询问窗口")]
[assembly: AssemblyProduct("定时关机助手")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ShutdownHelper
{
    /// <summary>电源操作类型</summary>
    public enum PowerAction
    {
        Shutdown, // 关机
        Restart,  // 重启
        Logoff,   // 注销
        Sleep     // 睡眠
    }

    /// <summary>单个定时计划</summary>
    public class ShutdownPlan
    {
        public int Id = 0;                    // 计划唯一编号
        public string Name = "新计划";          // 计划名称
        public bool Enabled = true;           // 是否启用
        public bool[] WeekDays = new bool[7]; // 周一~周日，是否执行
        public int Hour = 22;                 // 执行小时（0-23）
        public int Minute = 58;               // 执行分钟（0-59）
        public PowerAction Action = PowerAction.Shutdown; // 操作类型
        public bool AskConfirm = true;        // 到点是否弹出询问确认窗口
        public int WaitSeconds = 30;          // 询问等待秒数
        public string TaskName = "";          // 对应的 Windows 计划任务名
        public string ExecuteDate = "";       // 执行日期（yyyy-MM-dd，空=按星期循环执行）

        /// <summary>获取星期描述（如“周一,周三”）</summary>
        public string WeekText
        {
            get
            {
                string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
                var list = new List<string>();
                for (int i = 0; i < 7; i++)
                {
                    if (WeekDays[i]) list.Add(names[i]);
                }
                return list.Count == 0 ? "未选" : string.Join(",", list.ToArray());
            }
        }

        /// <summary>获取时间描述（如“22:58”）</summary>
        public string TimeText
        {
            get { return Hour.ToString("00") + ":" + Minute.ToString("00"); }
        }

        /// <summary>获取执行日期描述（yyyy-MM-dd，未设置则返回空字符串）</summary>
        public string DateText
        {
            get
            {
                DateTime dt;
                if (!string.IsNullOrEmpty(ExecuteDate) &&
                    DateTime.TryParseExact(ExecuteDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                {
                    return ExecuteDate;
                }
                return "";
            }
        }
    }

    /// <summary>应用配置（持久化到 config.json）</summary>
    public class AppConfig
    {
        public int NextId = 1;                                    // 下一个计划编号
        public List<ShutdownPlan> Plans = new List<ShutdownPlan>(); // 全部计划
    }

    /// <summary>配置文件的读取与保存</summary>
    public static class Config
    {
        /// <summary>配置文件路径：程序同目录下的 config.json</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
        }

        /// <summary>读取配置（文件不存在或解析失败时返回默认配置）</summary>
        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var ser = new JavaScriptSerializer();
                    AppConfig cfg = ser.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null && cfg.Plans != null)
                    {
                        // 兼容旧数据：为没有任务名的计划自动补任务名
                        foreach (var p in cfg.Plans)
                        {
                            if (string.IsNullOrEmpty(p.TaskName)) p.TaskName = "ShutdownHelper_" + p.Id;
                        }
                        return cfg;
                    }
                }
            }
            catch { }
            return new AppConfig();
        }

        /// <summary>保存配置</summary>
        public static void Save(AppConfig cfg)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                File.WriteAllText(ConfigPath, ser.Serialize(cfg));
            }
            catch { }
        }
    }

    /// <summary>电源操作执行器：关机 / 重启 / 注销 / 睡眠</summary>
    public static class PowerExec
    {
        // 调用系统 API 进入睡眠
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool SetSuspendState(bool Hibernate, bool ForceCritical, bool DisableWakeEvent);

        /// <summary>以无窗口后台方式调用 shutdown，避免黑色控制台窗口闪现</summary>
        private static void RunShutdown(string args)
        {
            var psi = new ProcessStartInfo("shutdown", args)
            {
                CreateNoWindow = true,      // 不创建新的控制台窗口
                UseShellExecute = false,    // 直接启动进程，不经过 shell
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }

        /// <summary>根据操作类型执行对应动作</summary>
        public static void Execute(PowerAction action)
        {
            try
            {
                switch (action)
                {
                    case PowerAction.Shutdown: // 关机
                        RunShutdown("/s /t 0");
                        break;
                    case PowerAction.Restart:   // 重启
                        RunShutdown("/r /t 0");
                        break;
                    case PowerAction.Logoff:    // 注销
                        RunShutdown("/l");
                        break;
                    case PowerAction.Sleep:     // 睡眠（进入睡眠而非休眠）
                        SetSuspendState(false, true, false);
                        break;
                }
            }
            catch { }
        }

        /// <summary>获取操作类型的中文名称</summary>
        public static string ActionName(PowerAction action)
        {
            switch (action)
            {
                case PowerAction.Shutdown: return "关机";
                case PowerAction.Restart:  return "重启";
                case PowerAction.Logoff:   return "注销";
                case PowerAction.Sleep:    return "睡眠";
                default:                   return "关机";
            }
        }
    }

    /// <summary>
    /// Windows 任务计划程序管理（通过 COM 接口调用，无需额外依赖）
    /// 负责把软件里的计划同步为系统计划任务，这样关闭软件后依然会定时触发
    /// </summary>
    public static class TaskServiceHelper
    {
        /// <summary>创建或更新一个计划对应的系统任务；计划未启用时自动删除对应任务</summary>
        public static void InstallPlan(ShutdownPlan plan)
        {
            if (string.IsNullOrEmpty(plan.TaskName)) return;
            if (!plan.Enabled) { RemovePlan(plan.TaskName); return; }

            try
            {
                dynamic svc = Connect();
                dynamic folder = svc.GetFolder("\\");
                dynamic def = svc.NewTask(0);

                // 任务基本信息与运行设置
                def.RegistrationInfo.Description = "定时关机助手 - " + plan.Name;
                def.Settings.Enabled = true;
                def.Settings.StartWhenAvailable = true;   // 错过计划后尽快补跑
                def.Settings.WakeToRun = true;            // 电脑睡眠时唤醒执行
                def.Settings.ExecutionTimeLimit = "PT5M"; // 执行时限 5 分钟

                // 触发器：指定了执行日期则用一次性触发器，否则按星期循环
                DateTime once = default(DateTime);
                bool hasDate = !string.IsNullOrEmpty(plan.ExecuteDate) &&
                    DateTime.TryParseExact(plan.ExecuteDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out once);
                if (hasDate)
                {
                    // 一次性触发器：仅在该日期的指定时刻执行一次
                    DateTime t = new DateTime(once.Year, once.Month, once.Day, plan.Hour, plan.Minute, 0);
                    if (t <= DateTime.Now) { RemovePlan(plan.TaskName); return; } // 日期已过，不安装
                    dynamic trig = def.Triggers.Create(1); // 1 = TASK_TRIGGER_ONCE
                    trig.StartBoundary = t.ToString("yyyy-MM-ddTHH:mm:ss");
                }
                else
                {
                    // 每周触发器：按计划的星期与时间触发
                    dynamic trig = def.Triggers.Create(3); // 3 = TASK_TRIGGER_WEEKLY（每周触发器）
                    int mask = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        if (plan.WeekDays[i]) mask |= (1 << ((i + 1) % 7));
                    }
                    if (mask == 0) mask = 1; // 至少一个有效星期（默认周日）
                    trig.DaysOfWeek = mask;
                    // 起始时间设为下一次实际触发时间，保证首次触发正确
                    trig.StartBoundary = NextTriggerTime(plan).ToString("yyyy-MM-ddTHH:mm:ss");
                }

                // 执行动作：启动本程序并传入 -run 参数，由程序按计划设置弹出询问或直接执行
                dynamic act = def.Actions.Create(0); // 0 = EXEC
                act.Path = Assembly.GetExecutingAssembly().Location;
                act.Arguments = "-run " + plan.Id;

                // 注册任务：6=创建或更新；3=仅在用户交互式登录时运行（询问窗口可正常显示）
                folder.RegisterTaskDefinition(plan.TaskName, def, 6, null, null, 3);
            }
            catch { }
        }

        /// <summary>删除指定名称的系统任务</summary>
        public static void RemovePlan(string taskName)
        {
            if (string.IsNullOrEmpty(taskName)) return;
            try
            {
                dynamic svc = Connect();
                dynamic folder = svc.GetFolder("\\");
                folder.DeleteTask(taskName, 0);
            }
            catch { }
        }

        /// <summary>计算一个计划的下一次实际触发时间（比当前时间晚的最近一次）</summary>
        public static DateTime NextTriggerTime(ShutdownPlan plan)
        {
            DateTime now = DateTime.Now;
            // 指定了执行日期：只在当天该时刻触发；已过期返回 9999 表示不再安排
            if (!string.IsNullOrEmpty(plan.ExecuteDate))
            {
                DateTime once = default(DateTime);
                if (DateTime.TryParseExact(plan.ExecuteDate, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out once))
                {
                    DateTime t = new DateTime(once.Year, once.Month, once.Day, plan.Hour, plan.Minute, 0);
                    if (t > now) return t;
                    return new DateTime(9999, 12, 31);
                }
            }
            for (int i = 0; i < 8; i++)
            {
                DateTime d = now.AddDays(i);
                int dow = ((int)d.DayOfWeek + 6) % 7; // 周一=0 ... 周日=6
                if (dow < 0 || dow > 6 || !plan.WeekDays[dow]) continue;
                DateTime t = new DateTime(d.Year, d.Month, d.Day, plan.Hour, plan.Minute, 0);
                if (t > now) return t;
            }
            // 兜底：一周之后
            return now.AddDays(7);
        }

        /// <summary>连接任务计划程序服务</summary>
        private static dynamic Connect()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc;
        }
    }

    /// <summary>扫描发现的相关任务项</summary>
    public class ScanItem
    {
        public string Name = "";        // 完整任务路径
        public string ShortName = "";   // 简短任务名
        public string Trigger = "";     // 触发类型
        public string Action = "";      // 执行内容
        public string Author = "";      // 作者
        public int Risk = 0;            // 风险等级：0 信息 / 1 低 / 2 中 / 3 高
        public bool IsManaged;          // 是否本软件创建
        public bool IsOrphan;           // 是否孤立任务（本软件创建但配置中已删除）
        public bool IsPower;            // 是否电源操作
        public bool IsAutoStart;        // 是否开机/登录触发
    }

    /// <summary>
    /// 任务扫描器：自动识别电脑中所有与关机/电源、开机自启相关的计划任务和启动项
    /// </summary>
    public static class TaskScanner
    {
        /// <summary>连接任务计划服务</summary>
        private static dynamic Connect()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc;
        }

        /// <summary>扫描全部相关任务，返回结果列表</summary>
        public static List<ScanItem> Scan(AppConfig cfg)
        {
            var result = new List<ScanItem>();
            var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in cfg.Plans)
            {
                if (!string.IsNullOrEmpty(p.TaskName)) managed.Add(p.TaskName);
            }
            try
            {
                dynamic svc = Connect();
                dynamic root = svc.GetFolder("\\");
                ScanFolder(svc, root, managed, result);
            }
            catch { }
            ScanStartupItems(result);
            return result;
        }

        /// <summary>递归扫描一个任务文件夹（含子文件夹）</summary>
        private static void ScanFolder(dynamic svc, dynamic folder, HashSet<string> managed, List<ScanItem> result)
        {
            try
            {
                dynamic tasks = folder.GetTasks(1); // 1=包含隐藏任务
                foreach (dynamic task in tasks)
                {
                    var item = new ScanItem();
                    item.Name = SafeStr(() => task.Path);
                    int slash = item.Name.LastIndexOf('\\');
                    item.ShortName = slash >= 0 ? item.Name.Substring(slash + 1) : item.Name;
                    try
                    {
                        dynamic def = task.Definition;
                        item.Author = SafeStr(() => def.RegistrationInfo.Author);
                        item.Trigger = DescribeTriggers(def);
                        item.Action = DescribeActions(def);
                    }
                    catch { }
                    Classify(item, managed);
                    result.Add(item);
                }
            }
            catch { }
            try
            {
                foreach (dynamic sub in folder.GetFolders(1))
                {
                    ScanFolder(svc, sub, managed, result);
                }
            }
            catch { }
        }

        /// <summary>判断任务的分类与风险等级</summary>
        private static void Classify(ScanItem item, HashSet<string> managed)
        {
            string n = item.ShortName;
            item.IsManaged = n.StartsWith("ShutdownHelper_", StringComparison.OrdinalIgnoreCase);
            item.IsPower = IsPowerAction(item.Action);
            item.IsAutoStart = item.Trigger.Contains("开机") || item.Trigger.Contains("登录");
            item.IsOrphan = item.IsManaged && !managed.Contains(n);

            if (item.IsPower && !item.IsManaged) item.Risk = 3;  // 第三方电源任务：高风险
            else if (item.IsOrphan) item.Risk = 2;                // 孤立任务：中
            else if (item.IsPower) item.Risk = 1;                 // 本软件电源任务：低
            else if (item.IsAutoStart) item.Risk = 1;
            else item.Risk = 0;
        }

        /// <summary>判断执行内容是否为关机/电源操作</summary>
        private static bool IsPowerAction(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.Contains("shutdown") || s.Contains("stop-computer") || s.Contains("restart-computer")
                || s.Contains("logoff") || s.Contains("powrprof") || s.Contains("setsuspendstate")
                || s.Contains("关机") || s.Contains("注销") || s.Contains("user32.dll");
        }

        /// <summary>描述任务的全部触发器</summary>
        private static string DescribeTriggers(dynamic def)
        {
            var sb = new StringBuilder();
            try
            {
                int count = (int)def.Triggers.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic tr = def.Triggers[i];
                    int type = (int)tr.Type;
                    string s;
                    switch (type)
                    {
                        case 1: s = "一次性"; break;
                        case 2: s = "每天"; break;
                        case 3: s = "每周"; break;
                        case 4: s = "每月"; break;
                        case 5: s = "每月(星期)"; break;
                        case 6: s = "空闲时"; break;
                        case 7: s = "注册时"; break;
                        case 8: s = "开机时"; break;
                        case 9: s = "登录时"; break;
                        case 11: s = "会话状态"; break;
                        default: s = "其他(" + type + ")"; break;
                    }
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(s);
                }
            }
            catch { }
            return sb.Length == 0 ? "无触发器" : sb.ToString();
        }

        /// <summary>描述任务的全部动作</summary>
        private static string DescribeActions(dynamic def)
        {
            var sb = new StringBuilder();
            try
            {
                int count = (int)def.Actions.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic act = def.Actions[i];
                    int type = (int)act.Type;
                    if (type == 0)
                    {
                        sb.Append(SafeStr(() => act.Path));
                        string args = SafeStr(() => act.Arguments);
                        if (!string.IsNullOrEmpty(args)) sb.Append(" " + args);
                    }
                    else
                    {
                        sb.Append("动作类型:" + type);
                    }
                    if (i < count) sb.Append(" | ");
                }
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>扫描注册表开机启动项中的电源程序</summary>
        private static void ScanStartupItems(List<ScanItem> result)
        {
            Microsoft.Win32.RegistryKey[] roots = { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine };
            foreach (var root in roots)
            {
                try
                {
                    using (var rk = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        if (rk == null) continue;
                        foreach (var nm in rk.GetValueNames())
                        {
                            string val = rk.GetValue(nm) as string;
                            if (IsPowerAction(val))
                            {
                                result.Add(new ScanItem
                                {
                                    Name = "启动项: " + nm,
                                    ShortName = nm,
                                    Trigger = "登录时",
                                    Action = val,
                                    Author = "开机启动项",
                                    IsAutoStart = true,
                                    IsPower = true,
                                    Risk = 3
                                });
                            }
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>启用/禁用指定任务（按完整路径）</summary>
        public static bool SetEnabled(string taskPath, bool enabled)
        {
            try
            {
                dynamic svc = Connect();
                string folder, name;
                SplitPath(taskPath, out folder, out name);
                dynamic f = svc.GetFolder(folder);
                dynamic task = f.GetTask(name);
                task.Enabled = enabled ? 1 : 0;
                return true;
            }
            catch { return false; }
        }

        /// <summary>删除指定任务</summary>
        public static bool DeleteTask(string taskPath)
        {
            try
            {
                dynamic svc = Connect();
                string folder, name;
                SplitPath(taskPath, out folder, out name);
                dynamic f = svc.GetFolder(folder);
                f.DeleteTask(name, 0);
                return true;
            }
            catch { return false; }
        }

        /// <summary>将完整任务路径拆分为文件夹与任务名</summary>
        private static void SplitPath(string path, out string folder, out string name)
        {
            int slash = path.LastIndexOf('\\');
            if (slash <= 0) { folder = "\\"; name = path.TrimStart('\\'); }
            else { folder = path.Substring(0, slash); name = path.Substring(slash + 1); }
            if (string.IsNullOrEmpty(folder)) folder = "\\";
        }

        /// <summary>安全读取 COM 字符串，避免异常中断</summary>
        private static string SafeStr(Func<object> getter)
        {
            try { object o = getter(); return o == null ? "" : o.ToString(); }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 扫描结果窗口：自动识别并展示电脑中的相关任务，支持禁用/启用/删除第三方电源任务
    /// </summary>
    public class ScanForm : Form
    {
        private ListView _lv;
        private Label _summary;
        private List<ScanItem> _items;

        public ScanForm(List<ScanItem> items)
        {
            _items = items ?? new List<ScanItem>();
            Text = "自动识别电脑相关任务";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(900, 580);
            Font = new Font("Microsoft YaHei UI", 9f);
            BuildUI();
            LoadItems();
        }

        private void BuildUI()
        {
            _summary = new Label { Location = new Point(12, 12), Size = new Size(876, 44) };
            Controls.Add(_summary);

            _lv = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                Location = new Point(12, 60),
                Size = new Size(876, 460),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _lv.Columns.Add("风险", 70);
            _lv.Columns.Add("任务名", 190);
            _lv.Columns.Add("触发类型", 140);
            _lv.Columns.Add("执行内容", 386);
            _lv.Columns.Add("作者", 80);
            _lv.DoubleClick += (s, e) => ShowDetail();
            Controls.Add(_lv);

            var btnRescan = new Button { Text = "重新扫描", Location = new Point(12, 534), Size = new Size(100, 34) };
            btnRescan.Click += (s, e) => Rescan();
            Controls.Add(btnRescan);

            var btnDisable = new Button { Text = "禁用选中", Location = new Point(600, 534), Size = new Size(92, 34) };
            btnDisable.Click += (s, e) => ChangeSelected(false);
            Controls.Add(btnDisable);

            var btnEnable = new Button { Text = "启用选中", Location = new Point(698, 534), Size = new Size(88, 34) };
            btnEnable.Click += (s, e) => ChangeSelected(true);
            Controls.Add(btnEnable);

            var btnDelete = new Button { Text = "删除选中", Location = new Point(792, 534), Size = new Size(96, 34), ForeColor = Color.FromArgb(200, 60, 60) };
            btnDelete.Click += (s, e) => DeleteSelected();
            Controls.Add(btnDelete);
        }

        private void LoadItems()
        {
            var ordered = _items.OrderByDescending(x => x.Risk).ThenBy(x => x.ShortName).ToList();
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (var it in ordered)
            {
                var lvi = new ListViewItem(RiskText(it.Risk));
                lvi.SubItems.Add(it.ShortName);
                lvi.SubItems.Add(it.Trigger);
                lvi.SubItems.Add(Trim(it.Action, 86));
                lvi.SubItems.Add(Trim(it.Author, 16));
                lvi.ForeColor = RiskColor(it.Risk);
                lvi.Tag = it;
                _lv.Items.Add(lvi);
            }
            _lv.EndUpdate();

            int third = _items.Count(x => x.IsPower && !x.IsManaged);
            int managed = _items.Count(x => x.IsManaged);
            int orphan = _items.Count(x => x.IsOrphan);
            int auto = _items.Count(x => x.IsAutoStart);
            _summary.Text = "共发现 " + _items.Count + " 个相关任务：第三方电源 " + third +
                " 个（高风险，建议禁用）｜本软件 " + managed + " 个｜孤立 " + orphan +
                " 个｜开机/登录自启 " + auto + " 个";
            _summary.ForeColor = third > 0 ? Color.FromArgb(200, 60, 60) : Color.FromArgb(60, 120, 200);
        }

        private List<ScanItem> SelectedItems()
        {
            var list = new List<ScanItem>();
            foreach (ListViewItem lvi in _lv.SelectedItems)
            {
                var it = lvi.Tag as ScanItem;
                if (it != null) list.Add(it);
            }
            return list;
        }

        private static bool IsSystem(ScanItem it)
        {
            return it.Name.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        }

        private void ChangeSelected(bool enabled)
        {
            int ok = 0;
            foreach (var it in SelectedItems())
            {
                if (IsSystem(it)) { MessageBox.Show("系统任务不建议修改：" + it.ShortName, "提示"); continue; }
                if (TaskScanner.SetEnabled(it.Name, enabled)) ok++;
            }
            if (ok > 0) Rescan();
            else MessageBox.Show("没有可操作的任务。", "提示");
        }

        private void DeleteSelected()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            foreach (var it in sel)
            {
                if (IsSystem(it)) { MessageBox.Show("系统任务不能删除：" + it.ShortName, "提示"); return; }
            }
            var dr = MessageBox.Show("确认删除选中的 " + sel.Count + " 个任务吗？此操作不可恢复。", "确认删除",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dr != DialogResult.OK) return;
            int ok = 0;
            foreach (var it in sel) { if (TaskScanner.DeleteTask(it.Name)) ok++; }
            if (ok > 0) Rescan();
        }

        private void Rescan()
        {
            _items = TaskScanner.Scan(Config.Load());
            LoadItems();
        }

        private void ShowDetail()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) return;
            var it = sel[0];
            MessageBox.Show("任务名：" + it.Name + "\n触发类型：" + it.Trigger +
                "\n执行内容：" + it.Action + "\n作者：" + it.Author +
                "\n风险等级：" + RiskText(it.Risk), "任务详情");
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string RiskText(int r)
        {
            switch (r)
            {
                case 3: return "高";
                case 2: return "中";
                case 1: return "低";
                default: return "信息";
            }
        }

        private static Color RiskColor(int r)
        {
            switch (r)
            {
                case 3: return Color.FromArgb(200, 60, 60);
                case 2: return Color.FromArgb(210, 130, 30);
                case 1: return Color.FromArgb(60, 120, 200);
                default: return Color.FromArgb(90, 90, 90);
            }
        }
    }

    /// <summary>
    /// 环形倒计时询问窗口（方案一）
    /// 深色背景 + 中央环形进度（蓝→红渐变）+ 数字倒计时，最后 5 秒闪红，超时自动执行
    /// </summary>
    public class CountdownForm : Form
    {
        private readonly int _waitSeconds;  // 询问等待总秒数（来自配置）
        private int _remaining;             // 当前剩余秒数
        private bool _blink;                // 最后 5 秒闪烁开关
        private readonly PowerAction _action; // 到点要执行的操作
        private readonly bool _testMode;    // 测试模式：不真正执行
        private System.Windows.Forms.Timer _timer;
        private Panel _ringPanel;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="action">操作类型</param>
        /// <param name="waitSeconds">询问等待秒数</param>
        /// <param name="testMode">是否测试模式（不真正执行）</param>
        public CountdownForm(PowerAction action, int waitSeconds, bool testMode)
        {
            _action = action;
            _waitSeconds = Math.Max(1, Math.Min(120, waitSeconds));
            _remaining = _waitSeconds;
            _testMode = testMode;
            BuildUI();
        }

        /// <summary>构建窗口界面</summary>
        private void BuildUI()
        {
            // 窗口基础属性
            Text = "定时关机提醒";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(380, 380);
            TopMost = true;
            BackColor = Color.FromArgb(30, 30, 46); // 深色背景

            // 标题
            var lblTitle = new Label
            {
                Text = "定时关机提醒",
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 14, FontStyle.Bold),
                Location = new Point(0, 16),
                Size = new Size(380, 30),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(lblTitle);

            // 副标题（显示具体操作）
            var lblSub = new Label
            {
                Text = "是否现在" + PowerExec.ActionName(_action) + "？",
                ForeColor = Color.FromArgb(180, 180, 190),
                Font = new Font("Microsoft YaHei", 9),
                Location = new Point(0, 48),
                Size = new Size(380, 22),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(lblSub);

            // 环形倒计时绘制面板
            _ringPanel = new Panel
            {
                Location = new Point(110, 76),
                Size = new Size(160, 160),
                BackColor = Color.FromArgb(30, 30, 46)
            };
            _ringPanel.Paint += RingPanel_Paint;
            Controls.Add(_ringPanel);

            // 立即执行按钮（红色）
            var btnYes = new Button
            {
                Text = "立即" + PowerExec.ActionName(_action),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(229, 72, 77),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 10),
                Location = new Point(70, 265),
                Size = new Size(112, 38)
            };
            btnYes.FlatAppearance.BorderSize = 0;
            btnYes.Click += (s, e) => { Close(); DoAction(); };
            Controls.Add(btnYes);

            // 取消按钮（灰色）
            var btnNo = new Button
            {
                Text = "取消",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(75, 85, 99),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 10),
                Location = new Point(198, 265),
                Size = new Size(112, 38)
            };
            btnNo.FlatAppearance.BorderSize = 0;
            btnNo.Click += (s, e) => Close();
            Controls.Add(btnNo);

            // 倒计时定时器：每秒刷新剩余秒数并重绘进度环
            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                _remaining--;
                if (_remaining <= 0)
                {
                    // 超时：关闭窗口并执行动作
                    _timer.Stop();
                    Close();
                    DoAction();
                }
                else
                {
                    // 最后 5 秒切换闪烁状态，并重绘环形面板
                    if (_remaining <= 5) _blink = !_blink;
                    _ringPanel.Invalidate();
                }
            };
            _timer.Start();
        }

        /// <summary>绘制环形进度与中心倒计时数字</summary>
        private void RingPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; // 抗锯齿，边缘更平滑
            var rect = new Rectangle(6, 6, 148, 148);

            // 灰色底环
            using (var penBg = new Pen(Color.FromArgb(60, 60, 80), 12))
            {
                g.DrawEllipse(penBg, rect);
            }

            // 进度环：按剩余比例绘制，颜色从蓝渐变到红
            if (_remaining > 0)
            {
                double ratio = (double)_remaining / _waitSeconds;
                double sweep = 360.0 * ratio;
                double t = 1.0 - ratio; // 0=满环(蓝)，1=空环(红)
                int cr = (int)(59 + (239 - 59) * t);
                int cg = (int)(130 + (68 - 130) * t);
                int cb = (int)(246 + (68 - 246) * t);
                using (var penProg = new Pen(Color.FromArgb(cr, cg, cb), 12))
                {
                    penProg.StartCap = LineCap.Round;
                    penProg.EndCap = LineCap.Round;
                    g.DrawArc(penProg, rect, -90, (float)sweep);
                }
            }

            // 中心大号数字（最后 5 秒闪红）
            Color numColor = Color.White;
            if (_remaining <= 5 && _blink) numColor = Color.FromArgb(239, 68, 68);
            using (var fontNum = new Font("Microsoft YaHei", 36, FontStyle.Bold))
            using (var brush = new SolidBrush(numColor))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(_remaining.ToString(), fontNum, brush, new RectangleF(0, 22, 160, 92), sf);
            }

            // 环下方小字（“秒后自动关机”等）
            using (var fontSub = new Font("Microsoft YaHei", 9))
            using (var brushSub = new SolidBrush(Color.FromArgb(160, 160, 170)))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString("秒后自动" + PowerExec.ActionName(_action), fontSub, brushSub, new RectangleF(0, 112, 160, 30), sf);
            }
        }

        /// <summary>执行电源动作（测试模式不真正执行）</summary>
        private void DoAction()
        {
            if (_testMode) return; // 测试模式：只体验窗口效果，不真正执行
            PowerExec.Execute(_action);
        }
    }

    /// <summary>
    /// 主管理窗口：计划列表 + 计划设置编辑 + 快捷操作 + 状态栏
    /// </summary>
    public class MainForm : Form
    {
        private readonly AppConfig _config;
        private ListView _list;
        private TextBox _txtName;
        private ComboBox _cmbAction;
        private DateTimePicker _dtpTime;
        private CheckBox[] _chkDays = new CheckBox[7];
        private CheckBox _chkAsk;
        private CheckBox _chkDate;     // 是否按指定日期执行
        private DateTimePicker _dtpDate; // 执行日期（yyyy-MM-dd）
        private NumericUpDown _numWait;
        private StatusStrip _status;
        private ToolStripStatusLabel _statusLabel;

        public MainForm()
        {
            // 加载配置；首次运行时生成一个默认计划（周一~周四、周日 22:58 关机询问）
            _config = Config.Load();
            if (_config.Plans.Count == 0) CreateDefaultPlan();

            Text = "定时关机助手";
            ClientSize = new Size(760, 545);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9);
            BackColor = Color.FromArgb(245, 245, 248);

            BuildUI();
            RefreshList();
            RefreshStatus();
        }

        /// <summary>生成默认计划</summary>
        private void CreateDefaultPlan()
        {
            var p = NewPlan("工作日自动关机");
            p.Hour = 22; p.Minute = 58;
            p.WeekDays[0] = true; p.WeekDays[1] = true;
            p.WeekDays[2] = true; p.WeekDays[3] = true; p.WeekDays[6] = true; // 周一~周四、周日
            _config.Plans.Add(p);
            Config.Save(_config);
        }

        /// <summary>创建一个新计划并分配编号与任务名</summary>
        private ShutdownPlan NewPlan(string name)
        {
            var p = new ShutdownPlan
            {
                Id = _config.NextId++,
                Name = name,
                Enabled = true,
                Action = PowerAction.Shutdown,
                Hour = 22,
                Minute = 0,
                AskConfirm = true,
                WaitSeconds = 30
            };
            p.TaskName = "ShutdownHelper_" + p.Id;
            return p;
        }

        /// <summary>构建界面控件</summary>
        private void BuildUI()
        {
            // ===== 左侧：计划列表 =====
            var g1 = new GroupBox { Text = "定时计划", Location = new Point(10, 10), Size = new Size(300, 380) };
            Controls.Add(g1);

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true, // 复选框控制启用/停用
                Location = new Point(14, 22),
                Size = new Size(272, 300),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _list.Columns.Add("名称", 70);
            _list.Columns.Add("时间", 48);
            _list.Columns.Add("操作", 40);
            _list.Columns.Add("日期/星期", 112);
            _list.SelectedIndexChanged += (s, e) => LoadToEditor(SelectedPlan());
            _list.ItemChecked += (s, e) =>
            {
                var p = e.Item.Tag as ShutdownPlan;
                if (p != null) { p.Enabled = e.Item.Checked; Config.Save(_config); InstallPlan(p); RefreshStatus(); }
            };
            g1.Controls.Add(_list);

            var btnNew = new Button { Text = "新增计划", Location = new Point(14, 332), Size = new Size(82, 34) };
            btnNew.Click += (s, e) => AddPlan();
            g1.Controls.Add(btnNew);

            var btnApply = new Button { Text = "应用修改", Location = new Point(104, 332), Size = new Size(82, 34) };
            btnApply.Click += (s, e) => ApplyEdit();
            g1.Controls.Add(btnApply);

            var btnDel = new Button { Text = "删除计划", Location = new Point(194, 332), Size = new Size(82, 34) };
            btnDel.Click += (s, e) => DeletePlan();
            g1.Controls.Add(btnDel);

            // ===== 右侧：计划设置 =====
            var g2 = new GroupBox { Text = "计划设置", Location = new Point(320, 10), Size = new Size(430, 380) };
            Controls.Add(g2);

            g2.Controls.Add(new Label { Text = "计划名称", Location = new Point(14, 26), Size = new Size(70, 18) });
            _txtName = new TextBox { Location = new Point(14, 46), Size = new Size(280, 24) };
            g2.Controls.Add(_txtName);

            g2.Controls.Add(new Label { Text = "操作类型", Location = new Point(14, 80), Size = new Size(70, 18) });
            _cmbAction = new ComboBox { Location = new Point(14, 100), Size = new Size(120, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            _cmbAction.Items.AddRange(new object[] { "关机", "重启", "注销", "睡眠" });
            g2.Controls.Add(_cmbAction);

            g2.Controls.Add(new Label { Text = "执行时间", Location = new Point(150, 80), Size = new Size(70, 18) });
            _dtpTime = new DateTimePicker { Location = new Point(150, 100), Size = new Size(120, 26), Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true };
            g2.Controls.Add(_dtpTime);

            g2.Controls.Add(new Label { Text = "执行星期（可多选）", Location = new Point(14, 136), Size = new Size(130, 18) });
            string[] dayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            for (int i = 0; i < 7; i++)
            {
                _chkDays[i] = new CheckBox { Text = dayNames[i], Location = new Point(14 + i * 58, 158), Size = new Size(56, 24), Checked = true };
                g2.Controls.Add(_chkDays[i]);
            }

            // 执行日期（可选）：勾选后按指定日期执行一次，否则按上方星期循环
            g2.Controls.Add(new Label { Text = "执行日期（可选）", Location = new Point(14, 196), Size = new Size(120, 18) });
            _chkDate = new CheckBox { Text = "按指定日期执行", Location = new Point(14, 216), Size = new Size(130, 24) };
            _dtpDate = new DateTimePicker { Location = new Point(150, 216), Size = new Size(140, 24), Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Enabled = false };
            _chkDate.CheckedChanged += (s, e) =>
            {
                _dtpDate.Enabled = _chkDate.Checked;
                foreach (var c in _chkDays) c.Enabled = !_chkDate.Checked; // 指定日期时星期不再生效
            };
            g2.Controls.Add(_chkDate);
            g2.Controls.Add(_dtpDate);

            _chkAsk = new CheckBox { Text = "到点弹出询问确认窗口", Location = new Point(14, 252), Size = new Size(200, 24), Checked = true };
            g2.Controls.Add(_chkAsk);

            g2.Controls.Add(new Label { Text = "询问等待秒数", Location = new Point(14, 286), Size = new Size(90, 18) });
            _numWait = new NumericUpDown { Location = new Point(14, 306), Size = new Size(80, 24), Minimum = 1, Maximum = 120, Value = 30 };
            g2.Controls.Add(_numWait);
            g2.Controls.Add(new Label { Text = "秒（1~120）", Location = new Point(100, 309), Size = new Size(80, 18), ForeColor = Color.Gray });

            var tip = new Label
            {
                Text = "说明：勾选“按指定日期执行”后仅在该日触发一次；\r\n点“应用修改”或“安装全部计划”后写入系统任务。",
                Location = new Point(14, 338),
                Size = new Size(396, 36),
                ForeColor = Color.FromArgb(110, 110, 120)
            };
            g2.Controls.Add(tip);

            // ===== 底部：快捷操作 =====
            var g3 = new GroupBox { Text = "快捷操作", Location = new Point(10, 400), Size = new Size(740, 82) };
            Controls.Add(g3);

            var btnRun = new Button { Text = "立即执行", Location = new Point(14, 18), Size = new Size(110, 34) };
            btnRun.Click += (s, e) => RunNow();
            g3.Controls.Add(btnRun);

            var btnPreview = new Button { Text = "预览询问窗口", Location = new Point(136, 18), Size = new Size(120, 34) };
            btnPreview.Click += (s, e) => Preview();
            g3.Controls.Add(btnPreview);

            var btnInstall = new Button { Text = "安装全部计划", Location = new Point(268, 18), Size = new Size(120, 34) };
            btnInstall.Click += (s, e) => InstallAll();
            g3.Controls.Add(btnInstall);

            var btnDisable = new Button { Text = "全部禁用", Location = new Point(400, 18), Size = new Size(100, 34) };
            btnDisable.Click += (s, e) => DisableAll();
            g3.Controls.Add(btnDisable);

            var btnScan = new Button { Text = "扫描相关任务", Location = new Point(520, 18), Size = new Size(120, 34) };
            btnScan.Click += (s, e) => OpenScan();
            g3.Controls.Add(btnScan);

            var hint = new Label
            {
                Text = "自动识别电脑中其他\n关机/电源任务",
                Location = new Point(648, 14),
                Size = new Size(82, 56),
                ForeColor = Color.FromArgb(110, 110, 120)
            };
            g3.Controls.Add(hint);

            // ===== 状态栏 =====
            _status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("就绪");
            _status.Items.Add(_statusLabel);
            Controls.Add(_status);
        }

        /// <summary>获取当前选中的计划</summary>
        private ShutdownPlan SelectedPlan()
        {
            if (_list.SelectedItems.Count > 0)
            {
                return _list.SelectedItems[0].Tag as ShutdownPlan;
            }
            return null;
        }

        /// <summary>刷新计划列表</summary>
        private void RefreshList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in _config.Plans)
            {
                var item = new ListViewItem(p.Name) { Tag = p, Checked = p.Enabled };
                item.SubItems.Add(p.TimeText);
                item.SubItems.Add(PowerExec.ActionName(p.Action));
                // 第4列：有指定日期显示日期，否则显示星期
                item.SubItems.Add(string.IsNullOrEmpty(p.DateText) ? p.WeekText : p.DateText);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }

        /// <summary>把选中计划加载到编辑区</summary>
        private void LoadToEditor(ShutdownPlan p)
        {
            if (p == null) return;
            _txtName.Text = p.Name;
            _cmbAction.SelectedIndex = (int)p.Action;
            _dtpTime.Value = new DateTime(2000, 1, 1, p.Hour, p.Minute, 0);
            for (int i = 0; i < 7; i++) _chkDays[i].Checked = p.WeekDays[i];
            _chkAsk.Checked = p.AskConfirm;
            _numWait.Value = Math.Max(1, Math.Min(120, p.WaitSeconds));
            // 执行日期
            bool hasDate = !string.IsNullOrEmpty(p.ExecuteDate);
            _chkDate.Checked = hasDate;
            DateTime dt;
            _dtpDate.Value = (hasDate && DateTime.TryParseExact(p.ExecuteDate, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) ? dt : DateTime.Today;
        }

        /// <summary>把编辑区内容写回指定计划</summary>
        private ShutdownPlan ReadFromEditor(ShutdownPlan p)
        {
            p.Name = _txtName.Text.Trim();
            if (p.Name.Length == 0) p.Name = "未命名计划";
            p.Action = (PowerAction)_cmbAction.SelectedIndex;
            p.Hour = _dtpTime.Value.Hour;
            p.Minute = _dtpTime.Value.Minute;
            for (int i = 0; i < 7; i++) p.WeekDays[i] = _chkDays[i].Checked;
            p.AskConfirm = _chkAsk.Checked;
            p.WaitSeconds = (int)_numWait.Value;
            p.ExecuteDate = _chkDate.Checked ? _dtpDate.Value.ToString("yyyy-MM-dd") : "";
            return p;
        }

        /// <summary>新增计划</summary>
        private void AddPlan()
        {
            var p = NewPlan("新计划");
            _config.Plans.Add(p);
            Config.Save(_config);
            RefreshList();
            // 选中新增项并加载到编辑区
            foreach (ListViewItem item in _list.Items)
            {
                if ((item.Tag as ShutdownPlan) == p) { item.Selected = true; break; }
            }
            RefreshStatus();
        }

        /// <summary>应用修改：保存编辑区内容并同步到系统任务</summary>
        private void ApplyEdit()
        {
            var p = SelectedPlan();
            if (p == null) { MessageBox.Show("请先在左侧选择一个计划。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            ReadFromEditor(p);
            Config.Save(_config);
            InstallPlan(p);
            RefreshList();
            RefreshStatus();
            _statusLabel.Text = "已保存并更新系统任务：" + p.Name;
        }

        /// <summary>删除计划（同时删除对应系统任务）</summary>
        private void DeletePlan()
        {
            var p = SelectedPlan();
            if (p == null) return;
            if (MessageBox.Show("确定删除计划“" + p.Name + "”吗？", "确认删除",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TaskServiceHelper.RemovePlan(p.TaskName);
            _config.Plans.Remove(p);
            Config.Save(_config);
            RefreshList();
            RefreshStatus();
        }

        /// <summary>立即执行选中计划（按计划设置弹窗或直接执行）</summary>
        private void RunNow()
        {
            var p = SelectedPlan();
            if (p == null) { MessageBox.Show("请先在左侧选择一个计划。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (p.AskConfirm)
            {
                // 弹询问窗口，超时自动执行
                using (var f = new CountdownForm(p.Action, p.WaitSeconds, false))
                {
                    f.ShowDialog(this);
                }
            }
            else
            {
                PowerExec.Execute(p.Action);
            }
        }

        /// <summary>预览询问窗口（测试模式，不真正执行）</summary>
        private void Preview()
        {
            var p = SelectedPlan();
            if (p == null) { MessageBox.Show("请先在左侧选择一个计划。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (var f = new CountdownForm(p.Action, p.WaitSeconds, true))
            {
                f.ShowDialog(this);
            }
        }

        /// <summary>打开"自动识别电脑相关任务"窗口，扫描并展示其他关机/电源任务</summary>
        private void OpenScan()
        {
            var items = TaskScanner.Scan(_config);
            using (var f = new ScanForm(items))
            {
                f.ShowDialog(this);
            }
        }

        /// <summary>把当前编辑内容应用到选中计划并同步系统任务（内部辅助）</summary>
        private void InstallPlan(ShutdownPlan p)
        {
            TaskServiceHelper.InstallPlan(p);
        }

        /// <summary>安装全部计划到系统任务</summary>
        private void InstallAll()
        {
            foreach (var p in _config.Plans) TaskServiceHelper.InstallPlan(p);
            Config.Save(_config);
            RefreshStatus();
            _statusLabel.Text = "已安装全部计划到系统任务计划程序";
        }

        /// <summary>全部禁用（停用所有计划并清除对应系统任务）</summary>
        private void DisableAll()
        {
            foreach (var p in _config.Plans) p.Enabled = false;
            Config.Save(_config);
            foreach (var p in _config.Plans) TaskServiceHelper.InstallPlan(p); // 未启用会自动删除任务
            RefreshList();
            RefreshStatus();
        }

        /// <summary>刷新状态栏：显示计划统计与下次执行时间</summary>
        private void RefreshStatus()
        {
            int total = _config.Plans.Count;
            int enabled = _config.Plans.Count(p => p.Enabled);
            DateTime? next = null;
            foreach (var p in _config.Plans)
            {
                if (!p.Enabled) continue;
                var t = TaskServiceHelper.NextTriggerTime(p);
                if (next == null || t < next) next = t;
            }
            string nextText = "无";
            if (next.HasValue && next.Value.Year >= 9999) nextText = "无（日期已过/未安排）";
            else if (next.HasValue) nextText = next.Value.ToString("yyyy-MM-dd HH:mm");
            _statusLabel.Text = string.Format("共 {0} 个计划，启用 {1} 个    |    下次执行：{2}", total, enabled, nextText);
        }
    }

    /// <summary>
    /// 程序入口：负责启动主窗口，以及处理命令行参数（计划任务触发、预览）
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // 启用可视化样式（让按钮、窗口外观更现代）
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 读取配置
            AppConfig cfg = Config.Load();

            // 命令行参数分发
            // -run <id>    ：由 Windows 计划任务触发，执行指定编号的计划（按计划是否询问决定弹窗或直接执行）
            // -preview <id>：预览指定编号计划的询问窗口（测试模式，不真正执行）
            if (args != null && args.Length >= 2 && args[0] == "-run")
            {
                int id;
                if (int.TryParse(args[1], out id))
                {
                    ShutdownPlan plan = cfg.Plans.FirstOrDefault(p => p.Id == id);
                    if (plan != null && plan.Enabled)
                    {
                        if (plan.AskConfirm)
                        {
                            // 弹出环形倒计时询问窗口，超时后自动执行
                            Application.Run(new CountdownForm(plan.Action, plan.WaitSeconds, false));
                        }
                        else
                        {
                            // 不询问，直接执行
                            PowerExec.Execute(plan.Action);
                        }
                    }
                }
                return;
            }
            if (args != null && args.Length >= 2 && args[0] == "-preview")
            {
                int id;
                if (int.TryParse(args[1], out id))
                {
                    ShutdownPlan plan = cfg.Plans.FirstOrDefault(p => p.Id == id);
                    if (plan != null)
                    {
                        // 测试模式预览询问窗口（不会真正执行）
                        Application.Run(new CountdownForm(plan.Action, plan.WaitSeconds, true));
                    }
                }
                return;
            }

            // 默认启动主管理窗口
            Application.Run(new MainForm());
        }
    }
}
