// HomeworkReminder —— 作业润色提醒（托盘常驻，低占用，单文件）
//
// 工作方式：
//   1. 全局鼠标钩子检测「双击文档」和「拖拽放下」两个动作；
//   2. 动作发生时用 Shell COM / UI Automation 读取当前选中的文件；
//   3. 若文件名包含 config.json 里的学号/姓名（及别名），查询本地 polished.json 润色记录：
//        有记录  → 弹气泡「已润色 ✅」（不打扰）；
//        无记录  → 弹窗警告，并可一键「标记为已润色」；
//   4. 记录按文件内容 SHA-256 匹配，改名也能认出来；同一文件 15 分钟内只提醒一次。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace HomeworkReminder
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 协议激活入口（toast 按钮点击 → homeworkreminder:// URI → 本程序命令行）
            string pendingCommand = null;
            if (args != null && args.Length > 0 && args[0] != null &&
                args[0].StartsWith("homeworkreminder://", StringComparison.OrdinalIgnoreCase))
            {
                pendingCommand = args[0];
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lastcmd.txt"), pendingCommand);
                }
                catch { }
                EventWaitHandle existing;
                if (EventWaitHandle.TryOpenExisting("HomeworkReminder_Command", out existing))
                {
                    try { existing.Set(); } finally { existing.Dispose(); }
                    return; // 已有常驻实例，转发后退出
                }
                // 没有运行中的实例：继续启动，启动后自行处理这条命令
            }

            bool createdNew;
            Mutex mutex = new Mutex(true, "HomeworkReminder_SingleInstance", out createdNew);
            if (!createdNew) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ReminderContext(pendingCommand));
            GC.KeepAlive(mutex);
        }
    }

    public class Config
    {
        public string StudentId { get; set; }
        public string StudentName { get; set; }
        public string[] Aliases { get; set; }
        public bool RemindWhenPolished { get; set; }
        public bool DoubleClickDetect { get; set; }
        public bool DragDetect { get; set; }
        public int CooldownMinutes { get; set; }
        public bool DebugLog { get; set; }
        public string NotificationStyle { get; set; } // "toast"（Win11 系统通知）或 "popup"（程序弹窗），二选一

        public Config()
        {
            StudentId = "";
            StudentName = "";
            Aliases = new string[0];
            RemindWhenPolished = true;
            DoubleClickDetect = true;
            DragDetect = true;
            CooldownMinutes = 15;
            DebugLog = false;
            NotificationStyle = "toast";
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(StudentId) || !string.IsNullOrWhiteSpace(StudentName); }
        }

        public string[] Keywords
        {
            get
            {
                List<string> ks = new List<string>();
                if (!string.IsNullOrWhiteSpace(StudentId)) ks.Add(StudentId.Trim());
                if (!string.IsNullOrWhiteSpace(StudentName)) ks.Add(StudentName.Trim());
                if (Aliases != null)
                {
                    foreach (string a in Aliases)
                        if (!string.IsNullOrWhiteSpace(a)) ks.Add(a.Trim());
                }
                return ks.ToArray();
            }
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            try
            {
                if (File.Exists(path))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Config loaded = ser.Deserialize<Config>(File.ReadAllText(path));
                    if (loaded != null) c = loaded;
                }
            }
            catch { }
            return c;
        }
    }

    public class PolishRecord
    {
        public string Hash { get; set; }        // "sha:xxxx"，按内容匹配
        public string NameKey { get; set; }     // 去扩展名小写文件名，按名字匹配
        public string Display { get; set; }     // 标记时的原始文件名
        public string Path { get; set; }        // 标记时的完整路径（可能为空）
        public string MarkedAt { get; set; }    // 标记时间
    }

    public class RecordStore
    {
        private readonly string _file;
        private List<PolishRecord> _records;

        public RecordStore(string file)
        {
            _file = file;
            _records = new List<PolishRecord>();
            try
            {
                if (File.Exists(file))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    List<PolishRecord> loaded = ser.Deserialize<List<PolishRecord>>(File.ReadAllText(file));
                    if (loaded != null) _records = loaded;
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(_file, ser.Serialize(_records), Encoding.UTF8);
            }
            catch { }
        }

        public void Add(PolishRecord rec)
        {
            _records.RemoveAll(r =>
                (rec.Hash != null && r.Hash == rec.Hash) ||
                (rec.NameKey != null && r.NameKey == rec.NameKey));
            _records.Add(rec);
            while (_records.Count > 1000) _records.RemoveAt(0);
            Save();
        }

        public PolishRecord Find(string hash, string nameKey)
        {
            foreach (PolishRecord r in _records)
            {
                if (hash != null && r.Hash == hash) return r;
                if (nameKey != null && r.NameKey != null && r.NameKey == nameKey) return r;
            }
            return null;
        }
    }

    public class CaptureResult
    {
        public string Name;                              // UIA 兜底读到的显示名（可能无扩展名）
        public List<string> Paths = new List<string>();  // Shell COM 读到的完整路径
        public Point Point;
        public DateTime At;
    }

    internal class MouseHook : IDisposable
    {
        public event Action<Point, IntPtr> LeftDown;        // 参数：位置、根窗口句柄
        public event Action<Point> LeftDoubleClick;
        public event Action<Point> DragDropped;

        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x200;
        private const int WM_LBUTTONDOWN = 0x201;
        private const int WM_LBUTTONUP = 0x202;
        private const int WM_LBUTTONDBLCLK = 0x203;

        private IntPtr _hook;
        private readonly HookProc _proc;
        private bool _lDown;
        private bool _dragging;
        private Point _downPt;
        private DateTime _lastDownAt = DateTime.MinValue;
        private Point _lastDownPt;

        public MouseHook()
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }

        public bool IsInstalled { get { return _hook != IntPtr.Zero; } }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                Point pt = new Point(info.pt.x, info.pt.y);
                switch ((int)wParam)
                {
                    case WM_LBUTTONDOWN:
                        _lDown = true;
                        _dragging = false;
                        _downPt = pt;
                        // 低级钩子收不到 WM_LBUTTONDBLCLK（那是系统上层合成的），
                        // 这里自己判双击：同一位置、系统双击时限内连续两次按下
                        DateTime nowUtc = DateTime.UtcNow;
                        bool isDbl = (nowUtc - _lastDownAt).TotalMilliseconds <= GetDoubleClickTime() &&
                                     Math.Abs(pt.X - _lastDownPt.X) <= 10 &&
                                     Math.Abs(pt.Y - _lastDownPt.Y) <= 10;
                        _lastDownAt = nowUtc;
                        _lastDownPt = pt;
                        IntPtr root = GetAncestor(WindowFromPoint(info.pt), 2); // GA_ROOT
                        if (LeftDown != null) LeftDown(pt, root);
                        if (isDbl && LeftDoubleClick != null) LeftDoubleClick(pt);
                        break;
                    case WM_MOUSEMOVE:
                        if (_lDown && !_dragging)
                        {
                            int dx = pt.X - _downPt.X, dy = pt.Y - _downPt.Y;
                            if (dx * dx + dy * dy > 36) _dragging = true; // 移动超过 6px 视为拖拽
                        }
                        break;
                    case WM_LBUTTONDBLCLK:
                        _dragging = false;
                        if (LeftDoubleClick != null) LeftDoubleClick(pt);
                        break;
                    case WM_LBUTTONUP:
                        if (_dragging && DragDropped != null) DragDropped(pt);
                        _dragging = false;
                        _lDown = false;
                        break;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public NativePoint pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint p);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();
    }

    public class ReminderContext : ApplicationContext
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private static readonly string[] DocExtensions = new[] { ".doc", ".docx", ".pdf" };

        private readonly string _baseDir = AppDomain.CurrentDomain.BaseDirectory;
        private Config _cfg;
        private RecordStore _store;
        private NotifyIcon _tray;
        private Form _invoker;
        private MouseHook _hook;
        private MenuItem _pauseItem;
        private MenuItem _autostartItem;
        private bool _paused;
        private bool _configWarned;
        private readonly object _cooldownLock = new object();
        private Dictionary<string, DateTime> _cooldown = new Dictionary<string, DateTime>();
        private CaptureState _capture;

        private class CaptureState
        {
            public Task<CaptureResult> Task;
            public Point Point;
            public DateTime At;
        }

        private EventWaitHandle _cmdEvent;

        public ReminderContext(string pendingCommand)
        {
            string configPath = Path.Combine(_baseDir, "config.json");
            if (!File.Exists(configPath)) SaveDefaultConfig(configPath);
            _cfg = Config.Load(configPath);
            _store = new RecordStore(Path.Combine(_baseDir, "polished.json"));

            _invoker = new Form();
            _invoker.WindowState = FormWindowState.Minimized;
            _invoker.ShowInTaskbar = false;
            IntPtr forceHandle = _invoker.Handle; // 访问 Handle 强制创建句柄（CreateControl 对不可见窗体无效）

            _tray = new NotifyIcon();
            _tray.Icon = CreateIcon();
            _tray.Text = "作业润色提醒";
            _tray.Visible = true;
            _tray.ContextMenu = BuildMenu();

            _hook = new MouseHook();
            _hook.LeftDown += OnLeftDown;
            _hook.LeftDoubleClick += OnDoubleClick;
            _hook.DragDropped += OnDragDropped;
            EnsureToastAumid(); // 注册 Win11 系统通知来源（开始菜单快捷方式 + AppUserModelID）
            EnsureProtocol();   // 注册 homeworkreminder:// 协议（toast 按钮回调用）
            StartCommandLoop();
            Log("程序启动, exe=" + Application.ExecutablePath +
                " hook=" + (_hook.IsInstalled ? "已安装" : "安装失败!") +
                " toast=" + (_toastAvailable ? "可用" : "不可用(降级程序弹窗)") +
                " 关键词=[" + string.Join(", ", _cfg.Keywords) + "]");

            if (pendingCommand != null) ProcessCommand(); // 无常驻实例时点击的旧通知，启动后补处理

            if (!_cfg.IsConfigured)
                _tray.ShowBalloonTip(6000, "作业润色提醒",
                    "请先编辑程序目录下的 config.json，填写学号和姓名（改完即生效，无需重启）。",
                    ToolTipIcon.Warning);
        }

        // ---------- 鼠标事件 ----------

        private void OnLeftDown(Point pt, IntPtr rootHwnd)
        {
            Log("LeftDown pt=" + pt.X + "," + pt.Y + " hwnd=0x" + rootHwnd.ToString("X"));
            CaptureState st = new CaptureState();
            st.Point = pt;
            st.At = DateTime.UtcNow;
            IntPtr hwnd = rootHwnd;
            st.Task = Task.Run(delegate() { return DoCapture(pt, hwnd); });
            _capture = st;
        }

        private void OnDoubleClick(Point pt)
        {
            Log("DoubleClick 信号");
            if (!_cfg.DoubleClickDetect) return;
            TriggerCheck(pt, "dblclick");
        }

        private void OnDragDropped(Point pt)
        {
            Log("DragDrop 信号");
            if (!_cfg.DragDetect) return;
            TriggerCheck(pt, "drop");
        }

        private void TriggerCheck(Point evtPt, string source)
        {
            if (_paused) { Log("已暂停，跳过 (" + source + ")"); return; }
            CaptureState cap = _capture;
            Task.Run(delegate()
            {
                try
                {
                    CaptureResult r = null;
                    if (cap != null && (DateTime.UtcNow - cap.At).TotalSeconds < 3)
                    {
                        Task finished = Task.WhenAny(cap.Task, Task.Delay(1500)).Result;
                        if (finished == cap.Task && cap.Task.Status == TaskStatus.RanToCompletion)
                            r = cap.Task.Result;
                    }
                    if (r == null || (r.Paths.Count == 0 && string.IsNullOrEmpty(r.Name)))
                    {
                        try { r = DoCapture(evtPt, IntPtr.Zero); } catch (Exception ex) { Log("重试捕获异常: " + ex.Message); }
                    }
                    Log("TriggerCheck(" + source + ") 捕获结果: paths=" + (r != null ? r.Paths.Count : -1) +
                        " name=" + (r != null && r.Name != null ? r.Name : "(null)"));
                    if (r != null) ProcessCandidate(r);
                }
                catch (Exception ex) { Log("TriggerCheck 异常: " + ex); }
            });
        }

        // ---------- 读取当前选中的文件 ----------

        private CaptureResult DoCapture(Point pt, IntPtr rootHwnd)
        {
            CaptureResult r = new CaptureResult();
            r.Point = pt;
            r.At = DateTime.UtcNow;
            if (rootHwnd != IntPtr.Zero)
            {
                try { r.Paths = ShellSelectionPaths(rootHwnd); }
                catch (Exception ex) { Log("ShellSelectionPaths 异常: " + ex.Message); }
            }
            if (r.Paths.Count == 0)
            {
                try { r.Name = UiItemNameAt(pt); }
                catch (Exception ex) { Log("UiItemNameAt 异常: " + ex.Message); }
            }
            Log("DoCapture pt=" + pt.X + "," + pt.Y + " hwnd=0x" + rootHwnd.ToString("X") +
                " paths=" + r.Paths.Count + (r.Paths.Count > 0 ? " [" + string.Join("; ", r.Paths) + "]" : "") +
                " name=" + (r.Name ?? "(null)"));
            return r;
        }

        // 通过 Shell COM 拿资源管理器窗口的选中文件（能拿到完整路径）
        private static List<string> ShellSelectionPaths(IntPtr rootHwnd)
        {
            List<string> list = new List<string>();
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return list;
            dynamic shell = Activator.CreateInstance(shellType);
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    long wh = (long)w.HWND;
                    if (wh != rootHwnd.ToInt64() && (wh & 0xFFFFFFFFL) != (rootHwnd.ToInt64() & 0xFFFFFFFFL))
                        continue;
                    dynamic doc = w.Document;
                    if (doc == null) continue;
                    dynamic items = doc.SelectedItems();
                    if (items == null) continue;
                    foreach (dynamic it in items)
                    {
                        try
                        {
                            string p = (string)it.Path;
                            if (!string.IsNullOrEmpty(p)) list.Add(p);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return list.Distinct().ToList();
        }

        // UI Automation 兜底：读取鼠标位置下的列表项显示名（桌面图标、扩展名隐藏时用）
        private static string UiItemNameAt(Point pt)
        {
            AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (el == null) return null;
            AutomationElement e = el;
            for (int i = 0; i < 10 && e != null; i++)
            {
                System.Windows.Automation.ControlType ct = e.Current.ControlType;
                if (ct == System.Windows.Automation.ControlType.ListItem ||
                    ct == System.Windows.Automation.ControlType.DataItem ||
                    ct == System.Windows.Automation.ControlType.TreeItem)
                {
                    return e.Current.Name;
                }
                e = System.Windows.Automation.TreeWalker.RawViewWalker.GetParent(e);
            }
            return null;
        }

        // ---------- 匹配与提醒 ----------

        private void ProcessCandidate(CaptureResult r)
        {
            _cfg = Config.Load(Path.Combine(_baseDir, "config.json")); // 配置热更新（修 bug：原来只在 HandleMatch 里刷新，未配置时会提前跳出）
            if (!_cfg.IsConfigured) { Log("配置未填写学号/姓名，跳过"); WarnConfigOnce(); return; }

            List<KeyValuePair<string, string>> matched = new List<KeyValuePair<string, string>>(); // 文件名 -> 完整路径(可为null)
            if (r.Paths.Count > 0)
            {
                foreach (string p in r.Paths)
                {
                    string name = SafeFileName(p);
                    if (name == null) continue;
                    Log("候选: " + name + " doc=" + IsDocName(name) + " kw=" + MatchKeyword(name));
                    if (IsDocName(name) && MatchKeyword(name))
                        matched.Add(new KeyValuePair<string, string>(name, p));
                }
            }
            else if (!string.IsNullOrEmpty(r.Name))
            {
                Log("候选(UIA): " + r.Name + " kw=" + MatchKeyword(r.Name));
                if (MatchKeyword(r.Name))
                    matched.Add(new KeyValuePair<string, string>(r.Name, ResolveLoosePath(r.Name)));
            }

            Log("匹配到 " + matched.Count + " 个作业文件");
            int shown = 0;
            foreach (KeyValuePair<string, string> kv in matched)
            {
                if (shown >= 3) break; // 一次最多提醒 3 个，防止框选一堆文件时刷屏
                HandleMatch(kv.Key, kv.Value);
                shown++;
            }
        }

        private void HandleMatch(string displayName, string fullPath)
        {
            string nameKey = NormKey(displayName);
            string hash = null;
            try
            {
                if (fullPath != null && File.Exists(fullPath)) hash = HashFile(fullPath);
            }
            catch (Exception ex) { Log("计算哈希失败: " + ex.Message); }

            string key = hash ?? ("name:" + nameKey);
            DateTime now = DateTime.Now;
            lock (_cooldownLock)
            {
                DateTime last;
                if (_cooldown.TryGetValue(key, out last) && (now - last).TotalMinutes < _cfg.CooldownMinutes)
                {
                    Log("冷却期内，跳过: " + key);
                    return;
                }
            }

            PolishRecord rec = _store.Find(hash, nameKey);
            Log("HandleMatch file=" + displayName + " path=" + (fullPath ?? "(null)") +
                " hash=" + (hash != null ? "有" : "无") + " 记录=" + (rec != null ? "已润色" : "无记录"));

            // 注意：冷却时间在界面成功显示后才写入，避免弹窗失败后 15 分钟内无法重试
            bool popupStyle = _cfg.NotificationStyle == "popup";
            if (rec != null)
            {
                if (!_cfg.RemindWhenPolished) return;
                string d = displayName, mk = rec.MarkedAt, k = key;
                _invoker.BeginInvoke((Action)delegate()
                {
                    try
                    {
                        if (popupStyle)
                            _tray.ShowBalloonTip(3000, "作业已润色 ✅", d + "\n标记时间：" + mk, ToolTipIcon.Info);
                        else if (!ShowToast("作业已润色 ✅", d + "\n标记时间：" + mk, null))
                            _tray.ShowBalloonTip(3000, "作业已润色 ✅", d + "\n标记时间：" + mk, ToolTipIcon.Info);
                        MarkCooldown(k);
                    }
                    catch (Exception ex) { Log("已润色提示失败: " + ex); }
                });
            }
            else
            {
                string d = displayName, fp = fullPath, h = hash, nk = nameKey, k = key;
                _invoker.BeginInvoke((Action)delegate()
                {
                    try
                    {
                        bool shown = false;
                        if (!popupStyle)
                        {
                            // toast 上带「标记为已润色」按钮（协议激活回传）；arguments 有 256 字符限制，
                            // 所以只带 h(哈希) 和 nk(去扩展名文件名)，足够写记录
                            string markUri = "homeworkreminder://mark?h=" + Uri.EscapeDataString(h ?? "") +
                                             "&nk=" + Uri.EscapeDataString(nk);
                            shown = ShowToast("⚠ 作业未润色提醒",
                                d + "\n未找到润色记录，提交前请确认是否已润色。", markUri);
                        }
                        if (!shown) // popup 风格，或 toast 发送失败时的降级（不同时出现两种）
                        {
                            ReminderForm form = new ReminderForm(d, fp, h, nk, AddPolishRecord);
                            form.Show();
                        }
                        MarkCooldown(k);
                    }
                    catch (Exception ex) { Log("弹窗失败: " + ex); }
                });
            }
        }

        private void MarkCooldown(string key)
        {
            DateTime now = DateTime.Now;
            lock (_cooldownLock)
            {
                _cooldown[key] = now;
                if (_cooldown.Count > 200)
                {
                    List<string> stale = _cooldown.Where(x => (now - x.Value).TotalHours > 24).Select(x => x.Key).ToList();
                    foreach (string k in stale) _cooldown.Remove(k);
                }
            }
        }

        private void AddPolishRecord(string hash, string nameKey, string display, string fullPath)
        {
            if (hash == null && fullPath != null)
            {
                try { if (File.Exists(fullPath)) hash = HashFile(fullPath); } catch { }
            }
            PolishRecord rec = new PolishRecord();
            rec.Hash = hash;
            rec.NameKey = nameKey ?? NormKey(display);
            rec.Display = display;
            rec.Path = fullPath;
            rec.MarkedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _store.Add(rec);
            if (!ShowToast("已标记为已润色 ✅", display + "\n之后打开/上传这个文件时会提示已润色。", null))
                _tray.ShowBalloonTip(3000, "已标记为已润色 ✅",
                    display + "\n之后打开/上传这个文件时会提示已润色。", ToolTipIcon.Info);
        }

        private bool MatchKeyword(string name)
        {
            foreach (string k in _cfg.Keywords)
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsDocName(string name)
        {
            string ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) return true; // 扩展名被隐藏时交给关键词匹配
            return DocExtensions.Contains(ext.ToLowerInvariant());
        }

        private void WarnConfigOnce()
        {
            if (_configWarned) return;
            _configWarned = true;
            _invoker.BeginInvoke((Action)delegate()
            {
                _tray.ShowBalloonTip(5000, "作业润色提醒",
                    "检测到作业文件，但 config.json 里还没填学号/姓名，暂时无法提醒。", ToolTipIcon.Warning);
            });
        }

        // ---------- 工具方法 ----------

        private static string SafeFileName(string path)
        {
            try
            {
                string n = Path.GetFileName(path);
                return string.IsNullOrEmpty(n) ? null : n;
            }
            catch { return null; }
        }

        private static string NormKey(string displayName)
        {
            try { return Path.GetFileNameWithoutExtension(displayName).Trim().ToLowerInvariant(); }
            catch { return (displayName ?? "").Trim().ToLowerInvariant(); }
        }

        private static string HashFile(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create())
            {
                return "sha:" + BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
        }

        // UIA 只拿到显示名时，尝试在常见目录里找到真实文件以便按内容哈希匹配
        private string ResolveLoosePath(string name)
        {
            string[] dirs = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };
            string[] exts = new string[] { "", ".docx", ".doc", ".pdf" };
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (string ext in exts)
                {
                    string cand = Path.Combine(dir, name + ext);
                    if (File.Exists(cand)) return cand;
                }
            }
            return null;
        }

        // ---------- 托盘菜单 ----------

        private ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            _pauseItem = new MenuItem("暂停提醒", delegate { TogglePause(); });
            _autostartItem = new MenuItem("开机自启", delegate { ToggleAutostart(); });
            _autostartItem.Checked = IsAutostart();
            menu.MenuItems.Add(_pauseItem);
            menu.MenuItems.Add(_autostartItem);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("手动标记文件为已润色…", delegate { ManualMark(); }));
            menu.MenuItems.Add(new MenuItem("测试系统通知", delegate
            {
                if (!ShowToast("测试通知", "看到这条说明 Win11 系统通知可用。", null))
                    MessageBox.Show("系统 toast 不可用，已降级为程序弹窗。", "测试");
            }));
            menu.MenuItems.Add(new MenuItem("编辑配置 (config.json)", delegate { OpenConfig(); }));
            menu.MenuItems.Add(new MenuItem("查看润色记录", delegate { OpenFile(Path.Combine(_baseDir, "polished.json")); }));
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("退出", delegate { ExitApp(); }));
            return menu;
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _pauseItem.Text = _paused ? "恢复提醒" : "暂停提醒";
            _tray.Text = _paused ? "作业润色提醒（已暂停）" : "作业润色提醒";
        }

        private bool IsAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("HomeworkReminder") != null;
        }

        private void ToggleAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsAutostart()) k.DeleteValue("HomeworkReminder", false);
                else k.SetValue("HomeworkReminder", "\"" + Application.ExecutablePath + "\"");
            }
            _autostartItem.Checked = IsAutostart();
        }

        private void ManualMark()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要标记为已润色的文件";
                dlg.Filter = "作业文档|*.doc;*.docx;*.pdf";
                if (dlg.ShowDialog() == DialogResult.OK)
                    AddPolishRecord(null, NormKey(Path.GetFileName(dlg.FileName)),
                        Path.GetFileName(dlg.FileName), dlg.FileName);
            }
        }

        private void OpenConfig()
        {
            string path = Path.Combine(_baseDir, "config.json");
            if (!File.Exists(path)) SaveDefaultConfig(path);
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
        }

        private void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path)) _store.Save();
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch { }
        }

        private void ExitApp()
        {
            if (_hook != null) _hook.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Exit();
        }

        private void SaveDefaultConfig(string path)
        {
            string json =
                "{\r\n" +
                "  \"StudentId\": \"你的学号\",\r\n" +
                "  \"StudentName\": \"你的姓名\",\r\n" +
                "  \"Aliases\": [],\r\n" +
                "  \"DoubleClickDetect\": true,\r\n" +
                "  \"DragDetect\": true,\r\n" +
                "  \"RemindWhenPolished\": true,\r\n" +
                "  \"CooldownMinutes\": 15,\r\n" +
                "  \"NotificationStyle\": \"toast\",\r\n" +
                "  \"DebugLog\": false\r\n" +
                "}\r\n";
            try { File.WriteAllText(path, json, Encoding.UTF8); } catch { }
        }

        private readonly object _logLock = new object();

        // ---------- Win11 系统通知（Toast）----------
        private const string ToastAumid = "HomeworkReminder.Reminder";
        private bool _toastAvailable;

        // Win11 要求通知来源是开始菜单里带 AppUserModelID 的快捷方式，启动时自动创建。
        // 按 MS 官方 DesktopToasts 示例做法：内存中构建 ShellLink → QI IPropertyStore 写 AUMID → IPersistFile.Save 一次性落盘
        private void EnsureToastAumid()
        {
            try
            {
                string programs = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                string lnk = Path.Combine(programs, "HomeworkReminder.lnk");

                Type linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")); // CLSID_ShellLink
                object link = Activator.CreateInstance(linkType);
                IShellLinkW sl = (IShellLinkW)link;
                sl.SetPath(Application.ExecutablePath);
                sl.SetWorkingDirectory(_baseDir);

                IPropertyStore store = (IPropertyStore)link; // QI
                PROPERTYKEY key = new PROPERTYKEY();
                key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); // PKEY_AppUserModel_ID
                key.pid = 5;
                PropVariant pv = new PropVariant();
                pv.vt = 31; // VT_LPWSTR
                pv.pointerValue = Marshal.StringToCoTaskMemUni(ToastAumid);
                try { store.SetValue(ref key, ref pv); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(pv.pointerValue); }

                System.Runtime.InteropServices.ComTypes.IPersistFile pf = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                pf.Save(lnk, true);

                Marshal.ReleaseComObject(pf);
                Marshal.ReleaseComObject(store);
                Marshal.ReleaseComObject(sl);
                Marshal.ReleaseComObject(link);

                _toastAvailable = File.Exists(lnk);
            }
            catch (Exception ex)
            {
                _toastAvailable = false;
                Log("注册通知 AUMID 失败(用气泡兜底): " + ex.Message);
            }
        }

        // 发系统 Toast。markUri 非空时带「标记为已润色」按钮（协议激活，点击回调经 homeworkreminder:// 回传）。
        // 失败返回 false，由调用方决定降级。
        private bool ShowToast(string title, string body, string markUri)
        {
            if (!_toastAvailable) return false;
            try
            {
                StringBuilder xml = new StringBuilder();
                xml.Append("<toast activationType=\"protocol\" launch=\"homeworkreminder://ignore\">");
                xml.Append("<visual><binding template=\"ToastGeneric\">");
                xml.Append("<text>" + XmlEscape(title) + "</text>");
                string[] lines = (body ?? "").Split('\n');
                for (int i = 0; i < lines.Length && i < 2; i++)
                    xml.Append("<text>" + XmlEscape(lines[i]) + "</text>");
                xml.Append("</binding></visual>");
                if (markUri != null)
                {
                    xml.Append("<actions>");
                    xml.Append("<action content=\"标记为已润色\" arguments=\"" + XmlEscape(markUri) + "\" activationType=\"protocol\"/>");
                    xml.Append("<action content=\"忽略\" arguments=\"homeworkreminder://ignore\" activationType=\"protocol\"/>");
                    xml.Append("</actions>");
                }
                xml.Append("</toast>");

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml.ToString());
                ToastNotificationManager.CreateToastNotifier(ToastAumid).Show(new ToastNotification(doc));
                return true;
            }
            catch (Exception ex)
            {
                Log("Toast 失败: " + ex.Message);
                return false;
            }
        }

        private static string XmlEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // 注册 homeworkreminder:// 协议 → 本程序（HKCU，无需管理员）。
        // 关键：根键下必须有 "URL Protocol" 空字符串值，否则系统不认这是 URL 协议，
        // 点击按钮会跳到 Microsoft Store 找应用
        private void EnsureProtocol()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey("Software\\Classes\\homeworkreminder"))
                {
                    k.SetValue(null, "HomeworkReminder Protocol");
                    k.SetValue("URL Protocol", "");
                }
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(
                    "Software\\Classes\\homeworkreminder\\shell\\open\\command"))
                {
                    k.SetValue(null, "\"" + Application.ExecutablePath + "\" \"%1\"");
                }
            }
            catch (Exception ex) { Log("注册协议失败: " + ex.Message); }
        }

        private void StartCommandLoop()
        {
            try
            {
                _cmdEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "HomeworkReminder_Command");
            }
            catch (Exception ex) { Log("创建命令事件失败: " + ex.Message); return; }
            Thread t = new Thread(CommandLoop);
            t.IsBackground = true;
            t.Start();
        }

        private void CommandLoop()
        {
            while (true)
            {
                try { _cmdEvent.WaitOne(); }
                catch { return; }
                ProcessCommand();
            }
        }

        // 处理 toast 按钮点击回传的命令（写在 lastcmd.txt 的 URI）
        private void ProcessCommand()
        {
            try
            {
                string cmd = File.ReadAllText(Path.Combine(_baseDir, "lastcmd.txt")).Trim();
                Log("收到通知按钮命令: " + cmd);
                if (cmd.IndexOf("homeworkreminder://mark", StringComparison.OrdinalIgnoreCase) != 0)
                    return;

                string q = cmd.Substring(cmd.IndexOf('?') + 1);
                string hash = null, nameKey = null, display = null;
                foreach (string pair in q.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string k2 = pair.Substring(0, eq);
                    string v = Uri.UnescapeDataString(pair.Substring(eq + 1));
                    if (k2 == "h") hash = v.Length > 0 ? v : null;
                    else if (k2 == "nk") nameKey = v;
                    else if (k2 == "d") display = v;
                }
                if (string.IsNullOrEmpty(nameKey)) return;
                if (nameKey == "__test__") { Log("测试命令，不写记录"); return; }
                if (display == null) display = nameKey;

                _invoker.BeginInvoke((Action)delegate() { AddPolishRecord(hash, nameKey, display, null); });
            }
            catch (Exception ex) { Log("处理命令失败: " + ex); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pointerValue;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY key, out PropVariant pv);
            void SetValue(ref PROPERTYKEY key, ref PropVariant pv);
            void Commit();
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        // DebugLog=true 时写 log.txt（与 exe 同目录），用于排查"为什么不弹"
        private void Log(string msg)
        {
            try
            {
                if (_cfg == null || !_cfg.DebugLog) return;
                lock (_logLock)
                {
                    File.AppendAllText(Path.Combine(_baseDir, "log.txt"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        private static Icon CreateIcon()
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0x2d, 0x8c, 0xf0)))
                    g.FillEllipse(b, 1, 1, 14, 14);
                using (Font f = new Font("Arial", 8f, FontStyle.Bold))
                    g.DrawString("✓", f, Brushes.White, 2f, 1f);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // 未润色时的提醒弹窗：一键标记，30 秒无操作自动关闭
    public class ReminderForm : Form
    {
        private readonly string _hash;
        private readonly string _nameKey;
        private readonly string _display;
        private readonly string _fullPath;
        private readonly Action<string, string, string, string> _onMark;

        public ReminderForm(string display, string fullPath, string hash, string nameKey,
            Action<string, string, string, string> onMark)
        {
            _display = display;
            _fullPath = fullPath;
            _hash = hash;
            _nameKey = nameKey;
            _onMark = onMark;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "作业润色提醒";
            Font = new Font("微软雅黑", 9f);
            ClientSize = new Size(430, 150);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);

            Label title = new Label();
            title.Text = "📄 " + display;
            title.Location = new Point(14, 14);
            title.MaximumSize = new Size(400, 0);
            title.AutoSize = true;
            Controls.Add(title);

            Label msg = new Label();
            msg.Text = "⚠ 未找到润色记录：这份作业可能还没润色过。\n若已润色过，请点下方按钮标记，以后不再弹窗警告。";
            msg.ForeColor = Color.FromArgb(0xB4, 0x23, 0x18);
            msg.Location = new Point(14, 52);
            msg.MaximumSize = new Size(400, 0);
            msg.AutoSize = true;
            Controls.Add(msg);

            Button mark = new Button();
            mark.Text = "标记为已润色";
            mark.Size = new Size(130, 30);
            mark.Location = new Point(14, 108);
            mark.FlatStyle = FlatStyle.Flat;
            mark.BackColor = Color.FromArgb(0x2d, 0x8c, 0xf0);
            mark.ForeColor = Color.White;
            mark.Click += delegate
            {
                if (_onMark != null) _onMark(_hash, _nameKey, _display, _fullPath);
                Close();
            };
            Controls.Add(mark);

            Button ignore = new Button();
            ignore.Text = "忽略";
            ignore.Size = new Size(80, 30);
            ignore.Location = new Point(156, 108);
            ignore.Click += delegate { Close(); };
            Controls.Add(ignore);

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 30000;
            t.Tick += delegate { t.Stop(); t.Dispose(); Close(); };
            t.Start();
        }
    }
}
