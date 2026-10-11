// BonsaiTray.cs -- Local Models 启动器（托盘 + 窗口）
//
// 本文件基于对原版 exe 的 IL 完整还原，**两处有意改动**：
//
//   【改动 1】品牌名：界面文字里的 "Bonsai2" 全部改为 "Local Models"
//             （原版这些字符串编译在 IL 里，改二进制会破坏元数据，只能重编）
//
//   【改动 2】修一个原版的真 bug：**卸载后再载入会报「日志文件正由另一进程使用」**
//             根因：StartHidden 里 new 出来的 StreamWriter 被直接丢弃
//             （原版 39 个字段里没有任何 StreamWriter 字段），
//             KillOwned 只 Dispose 了 _engine / _web，日志句柄永远泄漏。
//             修法：把 writer 存进字段，卸载/退出时先关它再杀进程；并加写入重试。
//
// 其余（配色 40 项、结构、文案、40 项引擎 argv、路径常量）逐条对齐原版。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal sealed class BonsaiTray : ApplicationContext
{
    // ==== 路径常量 ====
    // ★ 分发版关键修正：安装根目录 = **本 exe 所在目录**，不再写死开发机路径。
    //   原版写死 KitRoot/WsRoot/NodeExe，装到别人电脑上必然失效。
    //   引擎 argv 里的模型路径、engine 工作目录、webui 目录全部基于 Root 解析。
    private static readonly string Root = Path.GetDirectoryName(Application.ExecutablePath);
    private static string EngineDir { get { return Path.Combine(Root, "engine"); } }
    private static string ModelDir { get { return Path.Combine(Root, "models"); } }
    private static string WebDir { get { return Path.Combine(Root, "webui"); } }

    private const int EnginePort = 8095;
    private const int WebPort = 8096;
    /// 程序版本（「关于」页显示）—— 定义在 Version.cs，三个程序共用同一处
    private const string AppVersion = AppInfo.Version;
    // 模型 ID 不再是常量 —— 由 CurrentModelId() 按实际 .ninfer 文件名推导
    // API 密钥同理：首次运行随机生成，存在「引擎密钥.json」里。
    // 这里保留一个转发属性，界面上那几处（显示、复制）继续用 ApiKey 这个名字。
    private static string ApiKey { get { return WebHost.ApiKey; } }
    private const string WebUrl = "http://127.0.0.1:8096";

    /// 引擎 exe 名：从 launch-config.json 读（安装器按本机架构写入）；
    /// 读不到就按显卡架构现场判断。
    private static string EngineExeName
    {
        get
        {
            string n = LaunchConfig.Load(Root).EngineExe;
            if (!string.IsNullOrEmpty(n)) return n;
            return ArchEngine();
        }
    }

    /// 按计算能力返回引擎文件名（与安装器的 ArchToExe 一致）
    private static string ArchEngine()
    {
        try
        {
            string cc = Hardware.ComputeCap();
            if (cc == "8.6") return "ninfer-serve-86.exe";
            if (cc == "8.9") return "ninfer-serve-89.exe";
            if (cc == "12.0") return "ninfer-serve-120a.exe";
        }
        catch { }
        return "ninfer-serve-120a.exe";
    }

    /// 引擎 argv 里的模型路径：models\ 下第一个 .ninfer
    /// 模型路径：取 models\ 下的第一个 .ninfer（支持任意模型，不写死文件名）。
    /// 找不到时返回空串，调用方据此给出明确提示。
    private static string ModelPath()
    {
        try
        {
            if (Directory.Exists(ModelDir))
            {
                string[] f = Directory.GetFiles(ModelDir, "*.ninfer");
                if (f.Length > 0) return f[0];
            }
        }
        catch { }
        return "";
    }

    /// 日志与运行期数据目录。
    /// 原版把日志写在工作区（WsRoot）；集成后改为程序自己的目录，
    /// 这样不再需要那个联接，换台电脑也不用重建目录结构。
    private static string DataDir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalModels");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// 引擎 argv 模板。`{model}` 占位符在运行时替换为按实际模型推导出的 ID。
    /// （其余参数是本机适配参数，与具体模型无关。）
    private static readonly string[] EngineArgTemplate = new string[]
    {
        "--host", "::",
        "--port", "8095",
        "--model-id", "{model}",
        "--api-key", "{key}",
        "--max-context", "262144",
        "--kv-capacity", "{kv}",
        "--kv-dtype", "k8v4",
        "--host-kv-mib", "8192",
        "--cuda-graph-allowance-mib", "{graph}",
        "--prefill-chunk", "1024",
        "--spec", "mtp",
        "--draft-tokens", "4",
        "--default-max-tokens", "32768",
        "--default-reasoning-effort", "none",
        "--max-concurrency", "1",
        "--max-shared-prefixes", "0",
        "--presence-penalty", "0",
        "--temperature", "0.7",
        "--top-p", "0.9",
        "--top-k", "20"
    };

    /// 从 argv 模板里取某个开关的值。
    /// 界面显示用它，保证「界面上写的」与「实际传给引擎的」永远一致。
    private static string ArgValue(string flag)
    {
        for (int i = 0; i + 1 < EngineArgTemplate.Length; i++)
            if (EngineArgTemplate[i] == flag) return EngineArgTemplate[i + 1];
        return "";
    }

    /// 按实际模型生成引擎 argv。
    ///   modelId —— 模型 ID（文件名推导）
    ///   kvCapacity —— 设备池容量；显存紧张时会被自动下调后重试
    ///   graphMiB —— CUDA 图态预留
    private static List<string> EngineArgsFor(string modelId, int kvCapacity, int graphMiB)
    {
        var a = new List<string>();
        for (int i = 0; i < EngineArgTemplate.Length; i++)
        {
            string s = EngineArgTemplate[i];
            if (s == "{model}") a.Add(modelId);
            else if (s == "{kv}")
            {
                // 模板里 "--kv-capacity" 的下一项就是 "{kv}"，这里替换成实际值
                a.Add(kvCapacity.ToString());
            }
            else if (s == "{graph}")
            {
                a.Add(graphMiB.ToString());
            }
            else if (s == "{key}")
            {
                // 密钥首次运行随机生成 —— 模板是 static readonly，
                // 初始化时还没有密钥，所以只能在这里替换。
                a.Add(ApiKey);
            }
            else a.Add(s);
        }

        // 磁盘 KV 缓存（可选，见 DiskKv.cs）。
        // 开启后换出的 KV 写磁盘，上下文长度改由磁盘空间决定，突破显存/内存硬顶。
        // 代价是速度 —— 取回换出的 KV 要读盘。
        if (DiskKv.Enabled)
        {
            a.Add("--disk-kv-path");
            a.Add(DiskKv.EffectiveDir(Root));
            a.Add("--disk-kv-gib");
            a.Add(DiskKv.Gib.ToString());
            a.Add("--disk-kv-restore");
        }
        return a;
    }

    /// 模型 ID = **模型文件名去掉扩展名，原样保留**。
    ///
    /// 为什么用文件名：.ninfer 是引擎的模型容器，没有对外约定的「模型 ID」字段；
    /// 引擎的 --model-id 只是个**任意的 API 标识**，客户端必须用同一个值。
    /// 用文件名推导的好处是：换什么模型，ID 就跟着变，且和文件一一对应、不做任何加工，
    /// 看到 ID 就知道是哪个模型文件。
    ///
    ///   bonsai2_27b_ternary_ptq1_native_mtp.ninfer  ->  bonsai2_27b_ternary_ptq1_native_mtp
    ///   Qwen3-8B-Instruct.ninfer                    ->  Qwen3-8B-Instruct
    private static string ModelIdFromFile(string modelPath)
    {
        if (string.IsNullOrEmpty(modelPath)) return "local-model";
        string stem = Path.GetFileNameWithoutExtension(modelPath).Trim();
        return stem.Length > 0 ? stem : "local-model";
    }

    /// 当前模型的 ID（按 models\ 里实际那个 .ninfer 推导）
    private static string CurrentModelId()
    {
        return ModelIdFromFile(ModelPath());
    }

    // ==== 状态（字段名与原版一致）====
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miLoad, _miUnload, _miOpen, _miExit, _miStatus;
    private ToolStripMenuItem _miThemeSystem, _miThemeLight, _miThemeDark;
    private W11Button _btnLoad, _btnUnload, _btnOpen;
    private W11Card _logCard, _stateCard;
    private Label _lblState, _lblPill;
    private TextBox _txtLog;
    private Process _engine; private WebHost _web;
    private bool _busy;
    private readonly object _gate = new object();
    private Form _pump;
    private Form _win;
    private bool _lastSystemDark;
    private bool _switchingTheme;
    internal static float _dpiScale = 1f;

    // ★ 修 bug 用：把日志写入器存下来，卸载时才能关掉它释放句柄
    private StreamWriter _engineLogWriter;

    // ★ 适配任意模型用：详细信息页的网格引用（引擎启动后可刷新），
    //   以及引擎自己识别出的模型名称
    private TableLayoutPanel _infoGrid;
    private static string _engineModelName = "";

    // ★ 局域网访问控制（设置页 · 三级内容）
    private RadioButton _rbOpen, _rbList;
    private TextBox _txtAllow;
    private Label _lblAllow, _lblAcHint;

    // 连接日志去重：同一个 IP 短时间内只报一次，免得刷屏
    private readonly Dictionary<string, DateTime> _clientSeen = new Dictionary<string, DateTime>();

    private static Palette C { get { return Theme.Current; } }

    public BonsaiTray()
    {
        // 访问控制配置（公开 / 指定 IP）随程序目录的「访问控制.json」加载
        AccessConfig.Load(Root);
        // 自定义 API 密钥随「API密钥.json」加载
        ApiKeys.Load(Root);
        // 全局推理等级随「推理等级.json」加载
        Reasoning.Load(Root);
        // 磁盘 KV 缓存随「磁盘KV.json」加载
        DiskKv.Load(Root);
        // KVMem 环形缓存参数随「KVMem.json」加载
        Kvmem.Load(Root);
        // 引擎密钥：首次运行随机生成，之后每次读同一个文件。
        // 必须在加载完 ApiKeys 之后、启动引擎之前调用。
        EngineKey.Ensure(Root);
        // ★ 清掉上一轮残留的引擎进程：
        //   引擎的 stdout 是重定向到启动器的管道里的。启动器被重启/强杀后，
        //   那台引擎还活着但管道没读者了 —— 日志不再更新，
        //   而且引擎往管道写满之后会**阻塞**，表现为请求没反应、客户端超时。
        //   所以宁可让它重载一次模型（约 6 秒），也不要留一个可能卡死的孤儿进程。
        KillOrphanEngines();

        _miStatus = new ToolStripMenuItem("未载入") { Enabled = false };
        _miLoad = new ToolStripMenuItem("载入模型", null, (s, e) => LoadModel());
        _miUnload = new ToolStripMenuItem("卸载模型", null, (s, e) => UnloadModel());
        _miOpen = new ToolStripMenuItem("打开模型", null, (s, e) => OpenPage());
        _miExit = new ToolStripMenuItem("退出", null, (s, e) => ExitApp());

        _miThemeSystem = new ToolStripMenuItem("跟随系统", null, (s, e) => SetTheme(ThemeMode.System));
        _miThemeLight = new ToolStripMenuItem("浅色模式", null, (s, e) => SetTheme(ThemeMode.Light));
        _miThemeDark = new ToolStripMenuItem("深色模式", null, (s, e) => SetTheme(ThemeMode.Dark));

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Local Models",
            ContextMenuStrip = BuildTrayMenu(),
            Visible = true
        };
        _tray.DoubleClick += (s, e) => ShowWindow();
        _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        UpdateThemeChecks();
        _lastSystemDark = Theme.IsDark();
        RefreshMenu();

        _pump = new ThemeWatcher(this)
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0,
            ShowIcon = false
        };
        _pump.Shown += (s, e) => _pump.BeginInvoke(new Action(() =>
        {
            // 不再弹「启动成功」—— 启动本来就会自动把窗口打开，
            // 用户看得见，再弹个通知是多余的打扰。
            ShowWindow();
        }));
        _pump.Show();

        // 性能测试钩子（排查切主题卡顿用）：
        //   设环境变量 LM_PERF_THEME=N 启动，就会自动来回切 N 次主题，
        //   每段耗时写进 %TEMP%\lm-perf.log。正常使用不受影响。
        try
        {
            string v = Environment.GetEnvironmentVariable("LM_PERF_THEME");
            int times;
            if (!string.IsNullOrEmpty(v) && int.TryParse(v, out times) && times > 0)
            {
                _perfMode = true;              // 只有这种测试模式下才写 lm-perf.log
                RunPerfThemeTest(times);
            }
        }
        catch { }
    }

    /// 见构造函数里的 LM_PERF_THEME 说明
    private void RunPerfThemeTest(int times)
    {
        int n = 0;
        var tm = new System.Windows.Forms.Timer { Interval = 1500 };
        tm.Tick += delegate
        {
            if (n >= times)
            {
                tm.Stop(); tm.Dispose();
                PerfLog("==== 测试结束（共 " + times + " 次）====");
                return;
            }
            n++;
            PerfLog("---- 第 " + n + " 次切换 ----");
            try { SetTheme(n % 2 == 1 ? ThemeMode.Dark : ThemeMode.Light); }
            catch (Exception ex) { PerfLog("  切换异常 " + ex.Message); }
        };
        tm.Start();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var miTheme = new ToolStripMenuItem("外观");
        miTheme.DropDownItems.Add(_miThemeSystem);
        miTheme.DropDownItems.Add(_miThemeLight);
        miTheme.DropDownItems.Add(_miThemeDark);
        // 「设置…」固定跳到设置页的第一个选项；「关于…」跳到「关于」
        var miSettings = new ToolStripMenuItem("设置…", null, (s, e) => ShowSettingsPage(0));
        var miAbout = new ToolStripMenuItem("关于…", null, (s, e) => ShowSettingsPage(5));

        var menu = new Win11Menu(C);
        menu.Items.Add(_miStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miLoad);
        menu.Items.Add(_miUnload);
        menu.Items.Add(_miOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(miTheme);
        menu.Items.Add(miSettings);
        menu.Items.Add(miAbout);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miExit);
        return menu;
    }

    internal void OnSystemSettingsChanged()
    {
        if (Theme.Mode != ThemeMode.System) return;
        bool d = Theme.IsDark();
        if (d == _lastSystemDark) return;
        _lastSystemDark = d;
        SetTheme(ThemeMode.System);
    }

    private sealed class ThemeWatcher : Form
    {
        private readonly BonsaiTray _o;
        public ThemeWatcher(BonsaiTray o) { _o = o; }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x001A) _o.OnSystemSettingsChanged();
        }
    }

    // ---------- 主题 ----------
    private void SetTheme(ThemeMode m)
    {
        Theme.Mode = m;
        SaveTheme(m);
        UpdateThemeChecks();
        _rebuilding = true;                 // 重建期间挡住后台线程往日志框写
        try
        {
            SetThemeCore(m);
        }
        finally { _rebuilding = false; }
    }

    private void SetThemeCore(ThemeMode m)
    {
        var swAll = Stopwatch.StartNew();
        if (_tray != null)
        {
            var old = _tray.ContextMenuStrip;
            long t0 = swAll.ElapsedMilliseconds;
            _tray.ContextMenuStrip = BuildTrayMenu();
            PerfLog("主题 重建托盘菜单 " + (swAll.ElapsedMilliseconds - t0) + " ms");
            if (old != null) old.Dispose();
        }
        bool wasOpen = _win != null && !_win.IsDisposed && _win.Visible;
        int tab = 0;
        if (_win != null && !_win.IsDisposed && _win.Controls.Count > 0)
        {
            foreach (Control c in _win.Controls)
            {
                var t = c as TabControl; if (t != null) { tab = t.SelectedIndex; break; }
                foreach (Control c2 in c.Controls)
                { var t2 = c2 as TabControl; if (t2 != null) { tab = t2.SelectedIndex; break; } }
            }
            long t1 = swAll.ElapsedMilliseconds;
            _win.Hide(); _win.Dispose(); _win = null;
            PerfLog("主题 销毁旧窗口 " + (swAll.ElapsedMilliseconds - t1) + " ms");
        }
        if (wasOpen)
        {
            long t2 = swAll.ElapsedMilliseconds;
            ShowWindow(tab);
            PerfLog("主题 重建窗口   " + (swAll.ElapsedMilliseconds - t2) + " ms");
        }
        long t3 = swAll.ElapsedMilliseconds;
        RefreshMenu();
        PerfLog("主题 RefreshMenu " + (swAll.ElapsedMilliseconds - t3) + " ms");
        PerfLog("主题 ==== 总计 " + swAll.ElapsedMilliseconds + " ms ====");
    }

    private void UpdateThemeChecks()
    {
        _miThemeSystem.Checked = Theme.Mode == ThemeMode.System;
        _miThemeLight.Checked = Theme.Mode == ThemeMode.Light;
        _miThemeDark.Checked = Theme.Mode == ThemeMode.Dark;
    }

    private static string ThemeFile
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bonsai2");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "theme.txt");
        }
    }
    private static void SaveTheme(ThemeMode m) { try { File.WriteAllText(ThemeFile, ((int)m).ToString()); } catch { } }
    internal static void LoadTheme()
    {
        try
        {
            if (File.Exists(ThemeFile))
            {
                int v;
                if (int.TryParse(File.ReadAllText(ThemeFile).Trim(), out v) && v >= 0 && v <= 2)
                    Theme.Mode = (ThemeMode)v;
            }
        }
        catch { }
    }

    // ---------- DPI / 字体 ----------
    private static int S(int v) { return (int)Math.Round(v * _dpiScale); }
    private static Font UiFont(float size, FontStyle st = FontStyle.Regular)
    {
        try { return new Font("Microsoft YaHei UI", size, st, GraphicsUnit.Point); }
        catch { return new Font(FontFamily.GenericSansSerif, size, st, GraphicsUnit.Point); }
    }

    /// 给控件开双缓冲（Control.DoubleBuffered 是 protected，只能反射设）。
    /// 原版只对 head 面板做了这件事；这里对其它自绘/背景面板也一并加上，
    /// 目的都是同一个：**不让控件先刷白底、再画内容**。
    private static void TryDoubleBuffer(Control c)
    {
        if (c == null) return;
        try
        {
            var pi = typeof(Control).GetProperty("DoubleBuffered",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (pi != null) pi.SetValue(c, true, null);
        }
        catch { }
    }

    /// 递归给整棵控件树开双缓冲
    private static void TryDoubleBufferTree(Control root)
    {
        if (root == null) return;
        TryDoubleBuffer(root);
        foreach (Control c in root.Controls) TryDoubleBufferTree(c);
    }

    /// 网页文件的磁盘回落目录（index.html 已编译进 exe 作为嵌入资源，
    /// 这里只是为了能放额外静态文件；找不到就返回 null，不影响运行
    /// （index.html 本体已编译进 exe 作为嵌入资源）。
    private static string FindWebDir()
    {
        try
        {
            if (Directory.Exists(WebDir)) return WebDir;
        }
        catch { }
        return null;
    }

    // ---------- 主窗口 ----------
    private void ShowWindow() { ShowWindow(0); }

    private void ShowWindow(int tabIndex)
    {
        if (_win != null && !_win.IsDisposed)
        {
            if (_win.WindowState == FormWindowState.Minimized) _win.WindowState = FormWindowState.Normal;
            _win.Show(); _win.Activate(); _win.BringToFront();
            if (tabIndex > 0) SelectTab(tabIndex);
            return;
        }

        var f = new W11Form
        {
            // 标题不含模型名 —— 本程序适配任意 .ninfer 模型
            Text = "Local Models",
            ClientSize = new Size(S(760), S(600)),
            MinimumSize = new Size(S(640), S(480)),
            StartPosition = FormStartPosition.CenterScreen,
            Icon = _tray.Icon,
            BackColor = C.Bg,
            ForeColor = C.Fg,
            Font = UiFont(11F)
        };
        var swBuild = Stopwatch.StartNew();
        PerfLog("窗口 ---- 开始构建 ----");
        // ★ 先挂起布局，等整棵控件树搭完再一次性算。
        //   切主题会重建整个窗口（≈200 个控件），不挂起的话每 Add 一个控件
        //   都可能触发一轮布局计算 —— 启动后紧接着切主题时的偶发卡顿就是这么来的。
        f.SuspendLayout();
        TryDoubleBuffer(f);
        f.FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                f.Hide();
                Balloon(1500, "Local Models", "窗口已收起，程序仍在托盘运行。右键图标可退出。", ToolTipIcon.Info);
            }
        };

        var head = new Panel { Dock = DockStyle.Top, Height = S(104), BackColor = C.Header };
        // ★ 防闪关键：原版是把 DoubleBuffered 设在 **head 面板** 上（不是 Form）。
        //   反汇编原版 ShowWindow 偏移 340-382 可见目标对象是 field.head：
        //       340: field.head
        //       356: ldstr "DoubleBuffered"
        //       370: field.head
        //       382: PropertyInfo::SetValue
        //   之前我错设在 Form 上，所以标题区文字仍会先白块占位再出现。
        //   必须在添加 Label **之前**设，且要在控件创建阶段。
        TryDoubleBuffer(head);
        head.Controls.Add(new Label
        {
            Text = "Local Models",
            Font = UiFont(17F, FontStyle.Bold), ForeColor = C.Fg, AutoSize = true,
            Location = new Point(S(26), S(18)), BackColor = C.Header
        });
        head.Controls.Add(new Label
        {
            // 副标题不含模型名 —— 本程序适配任意 .ninfer 模型
            Text = "完全离线运行",
            Font = UiFont(10F), ForeColor = C.Dim, AutoSize = true,
            Location = new Point(S(28), S(56)), BackColor = C.Header
        });
        _lblPill = new Label
        {
            Text = "未载入", Font = UiFont(10.5F, FontStyle.Bold), ForeColor = C.Fg,
            AutoSize = false, Size = new Size(S(112), S(32)),
            TextAlign = ContentAlignment.MiddleCenter, BackColor = C.PillBg
        };
        Action place = () => { _lblPill.Location = new Point(head.ClientSize.Width - S(140), S(34)); };
        head.Resize += (s, e) => place();
        head.Controls.Add(_lblPill);
        place();

        var tabs = new Win11TabControl(C) { Dock = DockStyle.Fill, Font = UiFont(10.5F) };
        tabs.ItemSize = new Size(S(150), S(40));
        // 计时埋点：三个选项卡各建了多久（排查切主题卡顿，见 PerfLog）
        var swTab = Stopwatch.StartNew();
        tabs.TabPages.Add(BuildInfoTab());
        PerfLog("  选项卡 详细信息 " + swTab.ElapsedMilliseconds + " ms");
        swTab.Restart();
        tabs.TabPages.Add(BuildControlTab());
        PerfLog("  选项卡 控制模型 " + swTab.ElapsedMilliseconds + " ms");
        swTab.Restart();
        tabs.TabPages.Add(BuildSettingsTab());
        PerfLog("  选项卡 设置     " + swTab.ElapsedMilliseconds + " ms");

        // ★ 严格照原版 IL（偏移 995-1241）：用外层 TableLayoutPanel 2 行布局
        //   （ColumnCount=1, RowCount=2；ColumnStyle Percent100；
        //     RowStyle[0]=Absolute S(104) 放 head，RowStyle[1]=Percent100 放 tabs）。
        //   之前某轮我误判"原版没有 TableLayoutPanel"把它删了，直接挂 Form，
        //   这改变了布局引擎，是白色占位的可疑来源之一。
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = C.Bg,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(104)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        head.Dock = DockStyle.Fill;
        tabs.Dock = DockStyle.Fill;
        head.Margin = new Padding(0);
        tabs.Margin = new Padding(0);
        root.Controls.Add(head, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        f.Controls.Add(root);
        f.ResumeLayout(false);      // 触发一次总布局（配合上面的 SuspendLayout）

        _win = f;
        f.Show();
        f.Activate();
        Theme.ApplyWin11(f.Handle, C.Dark, true);
        // 原版 Show 之后**没有 RefreshMenu**（IL 1266 Show 后只有 Activate + ApplyWin11 + 设 SelectedIndex）
        if (tabIndex > 0) SelectTab(tabIndex);

        // ★ 滚动条刷深色。必须放在 Show() 之后 ——
        //   SetWindowTheme 要有真实句柄，构造函数阶段子控件的句柄还没建。
        //   TabPage 里的控件句柄是**切到那一页时才创建**的，所以每次切换还得再刷一遍
        //   （见 SelectTab / OnTabChanged）。
        if (C.Dark) Theme.DarkScrollbars(f);
        W11.NoHorizontalScroll(f);   // 只留竖向滚动条

        PerfLog("窗口 总计 " + swBuild.ElapsedMilliseconds + " ms（含 Show）");
    }

    /// 把窗口切到第 idx 个选项卡。
    ///
    /// ★ 必须**递归**找 TabControl：窗口结构是
    ///     Form → TableLayoutPanel(root) → TabControl
    ///   TabControl 不是 Form 的直接子控件。之前只遍历 _win.Controls，
    ///   永远找不到它，于是托盘菜单「设置…」「关于…」的跳转全部静默失效。
    private void SelectTab(int idx)
    {
        if (_win == null || _win.IsDisposed) return;
        TabControl t = FindTabControl(_win);
        if (t != null && idx >= 0 && idx < t.TabPages.Count) t.SelectedIndex = idx;
        // 切页后新页里的控件句柄才刚创建，滚动条要重新刷一次深色
        DarkScrollbarsSoon();
    }

    /// 稍后再刷一次滚动条主题。
    ///
    /// 为什么要延迟：TabPage 的控件句柄是「切到那一页」时才创建的，
    /// 而且 SetWindowTheme 要在句柄真正就绪之后才生效 ——
    /// 切页的同一瞬间调用会打在还没建好的句柄上，白刷。
    /// 用 BeginInvoke 排到消息队列后面，那时句柄已经建好了。
    private void DarkScrollbarsSoon()
    {
        if (!C.Dark) return;
        Form f = _win;
        if (f == null || f.IsDisposed) return;
        try
        {
            f.BeginInvoke(new Action(delegate
            {
                try { Theme.DarkScrollbars(f); W11.NoHorizontalScroll(f); } catch { }
            }));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// 程序窗口是不是正在前台（用户正看着它）
    private bool WindowInFront()
    {
        try
        {
            if (_win == null || _win.IsDisposed || !_win.Visible) return false;
            return GetForegroundWindow() == _win.Handle;
        }
        catch { return false; }
    }

    /// 弹气泡通知（参数顺序与 ShowBalloonTip 一致，方便直接替换）。
    /// ★ 窗口在前台时不弹 —— 用户正看着界面，状态在界面上已经写得很清楚，
    ///   再弹一个通知反而打扰（尤其是载入/卸载这种常用操作）。
    private void Balloon(int ms, string title, string text, ToolTipIcon icon)
    {
        try
        {
            if (WindowInFront()) return;
            _tray.ShowBalloonTip(ms, title, text, icon);
        }
        catch { }
    }

    private static TabControl FindTabControl(Control root)
    {
        if (root == null) return null;
        foreach (Control c in root.Controls)
        {
            TabControl t = c as TabControl;
            if (t != null) return t;
            TabControl d = FindTabControl(c);
            if (d != null) return d;
        }
        return null;
    }

    // ---------- 选项卡 1：详细信息 ----------
    // ★ 严格按原版 BuildInfoTab 的 IL 还原（IL 见 _还原依据\原版-IL-完整.txt）
    //   与原版一致的关键点：
    //     · TableLayoutPanel 是 **2 列**（ColumnCount=2），不是我上版的 1 列
    //     · 两个 ColumnStyle：第一列 Absolute，第二列 Percent
    //     · 每行 Panel 用 Controls.Add **先加值 Label 再加键 Label**（顺序影响 Dock 布局）
    //     · 每行 Panel 加进 grid 后要 SetColumnSpan(panel, 2)
    private TabPage BuildInfoTab()
    {
        var page = new TabPage("详细信息") { BackColor = C.Bg, ForeColor = C.Fg };
        _infoGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(S(14), S(14), S(14), S(14)),
            AutoScroll = true,
            BackColor = C.Bg
        };
        _infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(150)));
        _infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        PopulateInfoGrid();
        page.Controls.Add(_infoGrid);
        return page;
    }

    /// 组装「详细信息」的数据行。
    ///
    /// ★ 适配任意模型：所有与模型有关的项都从**实际那个 .ninfer 文件**推导
    ///   （文件名、大小、模型 ID），而不是写死；
    ///   引擎启动后再补上引擎自己识别出的模型名称。
    ///   其余各行是本机/启动参数，与具体模型无关。
    private List<string[]> BuildInfoRows()
    {
        var cfg = LaunchConfig.Load(Root);
        string modelPath = ModelPath();
        string modelFile = modelPath.Length > 0 ? Path.GetFileName(modelPath) : "";
        long modelBytes = 0;
        if (modelPath.Length > 0)
        {
            try { modelBytes = new FileInfo(modelPath).Length; }
            catch { }
        }

        string engName = EngineExeName;
        // 架构标签直接从引擎文件名取（ninfer-serve-120a.exe -> 120a），
        // 避免把计算能力 "12.0" 直接拼成 sm_12.0 这种读着别扭的写法。
        string archTag = "";
        Match am = Regex.Match(engName, @"ninfer-serve-([0-9a-z]+)\.exe", RegexOptions.IgnoreCase);
        if (am.Success) archTag = am.Groups[1].Value;
        if (archTag.Length == 0) archTag = cfg.Arch.Length > 0 ? cfg.Arch : Hardware.ComputeCap();

        var rows = new List<string[]>();
        rows.Add(new string[] { "模型文件", modelFile.Length > 0
            ? modelFile : "（models 文件夹里还没有模型）" });
        rows.Add(new string[] { "文件大小", modelBytes > 0
            ? string.Format("{0:N0} 字节  ({1:N2} GiB)", modelBytes, modelBytes / 1073741824.0)
            : "—" });
        rows.Add(new string[] { "模型 ID", modelPath.Length > 0
            ? CurrentModelId() + "   （由模型文件名推导，API 里填这个）" : "—" });
        if (_engineModelName.Length > 0)
            rows.Add(new string[] { "模型名称", _engineModelName + "   （引擎识别）" });
        rows.Add(new string[] { "推理引擎", engName + (archTag.Length > 0 ? "   (sm_" + archTag + ")" : "") });
        rows.Add(new string[] { "KV 设备池", cfg.KvCapacity + " token · k8v4" });
        rows.Add(new string[] { "KV 主机池", cfg.HostKvMiB + " MiB   （钉住的内存，不是磁盘）" });
        // KVMem —— 设置页改了这里要跟着变
        rows.Add(new string[] { "KVMem 缓存", Kvmem.Ring
            ? "已开启 · 窗口 " + Kvmem.Window.ToString("N0") + " token · 取回 " + Kvmem.Retrieve.ToString("N0") + " token   （设置页可改）"
            : "关闭 —— 不检索历史（设置页可开）" });
        // 磁盘 KV 缓存 —— 设置页改了这里要跟着变（SaveDiskKvSettings 会调 RefreshInfoTab）
        rows.Add(new string[] { "磁盘 KV 缓存", DiskKv.Enabled
            ? "已开启 · " + DiskKv.EffectiveDir(Root) + " · 上限 " + DiskKv.Gib + " GB   （设置页可改）"
            : "关闭 —— 上下文受显存 + 内存限制（设置页可开）" });
        // 物理上下文 = 三层 KV 加起来**实际能装**多少（与下面的「逻辑上下文」相对）。
        // 逻辑上下文是"声明上限"，固定值；这一项才会随磁盘 KV 开关变化。
        // 每 token 字节数从引擎日志的 capacity 行现算 —— 不同模型差别很大
        // （Bonsai 27B 约 169 KiB/token，Qwen3.8 约 380 KiB/token），不能套固定值。
        long bytesPerTok = BytesPerToken();
        long devTok = cfg.KvCapacity;
        long hostTok = BytesToTokens((long)cfg.HostKvMiB * 1024L * 1024L, bytesPerTok);
        long diskTok = DiskKv.Enabled ? BytesToTokens((long)DiskKv.Gib * 1073741824L, bytesPerTok) : 0;

        string phys = "设备 " + devTok.ToString("N0") + " token + 主机 " + cfg.HostKvMiB + " MiB";
        if (DiskKv.Enabled) phys += " + 磁盘 " + DiskKv.Gib + " GiB";
        if (bytesPerTok > 0)
            phys += "   ≈ " + (devTok + hostTok + diskTok).ToString("N0") + " token";
        else
            phys += "   （载入一次模型后可折算成 token）";
        rows.Add(new string[] { "物理上下文", phys });
        // 这几项从实际启动参数取，避免界面与真实 argv 不一致
        string maxCtx = ArgValue("--max-context");
        long maxCtxN = 0;
        long.TryParse(maxCtx, out maxCtxN);
        rows.Add(new string[] { "逻辑上下文", (maxCtxN > 0
            ? string.Format("{0:N0} token", maxCtxN) : "—") + "   （--max-context）" });

        string spec = ArgValue("--spec"), draft = ArgValue("--draft-tokens");
        rows.Add(new string[] { "推测解码", spec.Length > 0
            ? spec.ToUpper() + "，草稿 " + draft + " token" : "未启用" });
        rows.Add(new string[] { "服务端口", "引擎 " + EnginePort + "（需 API key）·  网页 " + WebPort + "（免 key）" });
        rows.Add(new string[] { "网页地址", "http://127.0.0.1:" + WebPort });
        // 逐一列出本机所有局域网地址 —— 多网卡（WiFi + 有线 + 热点/共享）时，
        // 手机该用哪个地址一试便知，不用去猜。
        List<string> lanIps = AllLanIps();
        if (lanIps.Count == 0)
            rows.Add(new string[] { "局域网", "（没有可用的局域网地址）" });
        else
            for (int i = 0; i < lanIps.Count; i++)
                rows.Add(new string[] { i == 0 ? "局域网" : "",
                    "http://" + lanIps[i] + ":" + WebPort + "   （同网络设备可直接打开）" });
        rows.Add(new string[] { "访问控制", AccessConfig.Public
            ? "公开 —— 同一网络里任意设备都能访问"
            : "指定 IP —— 只允许 " + AccessConfig.Allow.Count + " 条规则（设置页可改）" });
        rows.Add(new string[] { "API 密钥", WebHost.ApiKey
            + (ApiKeys.Keys.Count > 0
                ? "   （另有 " + ApiKeys.Keys.Count + " 条自定义，设置页可改）"
                : "   （设置页可加更多条）") });
        rows.Add(new string[] { "推理等级", Reasoning.Effort == "none"
            ? "关闭（最快）—— 设置页可选 minimal～max"
            : Reasoning.Effort + "（思考开启）—— 设置页可改" });
        rows.Add(new string[] { "显卡", cfg.Gpu.Length > 0
            ? cfg.Gpu + " · " + cfg.VramMiB + " MiB" : "（未探测）" });
        rows.Add(new string[] { "驱动", cfg.Driver.Length > 0 ? cfg.Driver : "（未探测）" });
        rows.Add(new string[] { "思考代价", "推理文本上 MTP 基本无效，开启后明显变慢" });
        return rows;
    }

    /// 把数据行填进 _infoGrid（可重复调用以刷新）
    private void PopulateInfoGrid()
    {
        if (_infoGrid == null || _infoGrid.IsDisposed) return;
        _infoGrid.SuspendLayout();

        // 表格要整张重建，先清掉旧行跟 ToolTip 的关联，否则会越攒越多
        if (_tipInfo != null) _tipInfo.RemoveAll();

        _infoGrid.Controls.Clear();
        _infoGrid.RowStyles.Clear();
        _infoGrid.RowCount = 0;

        List<string[]> rows = BuildInfoRows();
        for (int i = 0; i < rows.Count; i++)
        {
            int rowH = S(40);
            // 斑马纹改得更淡一点，配圆角卡片更像 Win11 的列表
            Color bg = (i % 2 == 0) ? C.Card : W11.Mix(C.Card, C.Bg, 0.5f);

            // 圆角行（Win11 列表那种卡片）
            var row = new W11Row
            {
                Dock = DockStyle.Fill,
                Bg = C.Bg,
                Fill = bg,
                Border = Color.Empty,
                Radius = W11.S(8),
                BackColor = bg,
                Padding = new Padding(S(14), 0, S(12), 0),
                Margin = new Padding(0, 0, 0, S(5))
            };
            var key = new Label
            {
                Text = rows[i][0],
                AutoSize = false,
                Dock = DockStyle.Left,
                Width = S(146),
                ForeColor = C.Dim,
                Font = UiFont(10F),
                BackColor = bg,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0)
            };

            // ★ 值这一列用「无边框只读文本框」而不是 Label ——
            //   Label 的文本无法选中，没法复制；只读文本框外观一样，
            //   但可以框选、Ctrl+A、Ctrl+C，右键还有系统的复制菜单。
            var val = new TextBox
            {
                Text = rows[i][1],
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = bg,
                ForeColor = C.Fg,
                Font = UiFont(10.5F),
                TabStop = false,
                WordWrap = false,
                ShortcutsEnabled = true,
                Dock = DockStyle.Top
            };
            int valH = val.PreferredHeight;
            val.Height = valH;

            // 用顶部内边距把它压到行的垂直中间（文本框自身不支持垂直居中）
            var valHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = bg,
                Padding = new Padding(0, Math.Max(0, (rowH - valH) / 2), 0, 0)
            };
            valHost.Controls.Add(val);

            // 先 Add(值) 再 Add(键)（与原版 IL 一致）
            row.Controls.Add(valHost);
            row.Controls.Add(key);
            row.Height = rowH;
            row.MinimumSize = new Size(0, rowH);

            // 术语行的悬停解释。要挂在**每一个**子控件上 ——
            // 鼠标实际停在的是 key / valHost / val 上，只挂 row 是弹不出来的。
            string tip = InfoTipFor(rows[i][0]);
            if (tip.Length > 0)
            {
                ToolTip t = InfoTip;
                t.SetToolTip(row, tip);
                t.SetToolTip(key, tip);
                t.SetToolTip(valHost, tip);
                t.SetToolTip(val, tip);
            }

            _infoGrid.Controls.Add(row, 0, i);
            _infoGrid.SetColumnSpan(row, 2);
        }

        _infoGrid.RowCount = rows.Count + 1;
        _infoGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = "模型各项按 models\\ 里的实际 .ninfer 文件填写；其余为本机探测（launch-config.json / nvidia-smi）与启动参数。"
                 + "　文本可框选复制，右键本页可「复制全部信息」。",
            AutoSize = false,
            Dock = DockStyle.Fill,
            Height = S(34),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = C.Dim,
            Font = UiFont(9F),
            Padding = new Padding(S(12), 0, 0, 0),
            Margin = new Padding(0, S(10), 0, S(10)),
            BackColor = C.Bg
        };
        _infoGrid.Controls.Add(hint, 0, rows.Count);
        _infoGrid.SetColumnSpan(hint, 2);

        // 右键本页 → 一键复制全部（便于把配置发给别人或排查）
        if (_infoGrid.ContextMenuStrip == null)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("复制全部信息", null, delegate { CopyAllInfo(); });
            _infoGrid.ContextMenuStrip = menu;
        }

        _infoGrid.ResumeLayout();
    }

    /// 把「详细信息」整页内容按「键：值」逐行复制到剪贴板
    private void CopyAllInfo()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string[] r in BuildInfoRows())
            {
                sb.Append(r[0]).Append('：').Append(r[1]);
                sb.Append(Environment.NewLine);
            }
            Clipboard.SetText(sb.ToString());
            Log("「详细信息」已复制到剪贴板（" + BuildInfoRows().Count + " 项）");
        }
        catch (Exception ex)
        {
            Log("复制失败：" + ex.Message);
        }
    }

    /// 重新填一遍「详细信息」（引擎启动后用它补上模型名称）
    ///
    /// 注意：不能用 _infoGrid.InvokeRequired 判断 —— 标签页不可见时该控件的
    /// 句柄可能未创建，InvokeRequired 会返回 false，导致在后台线程直接改控件。
    /// 改用窗口（_win）做线程编组，窗口没开时直接返回，下次开会带上新数据。
    private void RefreshInfoTab()
    {
        Form owner = _win;
        if (owner == null || owner.IsDisposed) return;
        if (_infoGrid == null || _infoGrid.IsDisposed) return;
        Action a = () => { try { PopulateInfoGrid(); } catch { } };
        if (owner.InvokeRequired) owner.BeginInvoke(a); else a();
    }

    /// 每 token 的 KV 占多少字节 —— 从引擎日志的 capacity 行现算。
    ///
    /// 引擎就绪后会打这一行：
    ///   capacity | KV 4,032 tokens, k8v4, explicit | pages 63/4,096 | runtime 665.6 MiB | free 297.0 MiB
    ///
    /// 于是：每 token 字节数 = runtime 字节数 ÷ token 数
    ///   Bonsai 27B：697,904,384 ÷ 4,032 ≈ 173,091 字节（约 169 KiB）
    ///   Qwen3.8  ：1,570,010,368 ÷ 4,032 ≈ 389,388 字节（约 380 KiB）
    ///
    /// ★ 不同模型差别能有两倍多，所以必须按当前模型现算，不能写死。
    /// 返回 0 表示还没有可用数据（没成功载入过模型）。
    public static long BytesPerToken()
    {
        try
        {
            string path = Path.Combine(DataDir, "engine.log");
            if (!File.Exists(path)) return 0;
            string[] lines = ReadLogLines(path);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                Match m = Regex.Match(lines[i],
                    @"capacity\s*\|.*?([\d,]+)\s+tokens.*?runtime\s+([\d.]+)\s*(GiB|MiB|KiB)");
                if (!m.Success) continue;

                long tok = 0;
                long.TryParse(m.Groups[1].Value.Replace(",", ""), out tok);
                if (tok <= 0) continue;

                double v = 0;
                double.TryParse(m.Groups[2].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out v);
                string unit = m.Groups[3].Value;
                double bytes = v * (unit == "GiB" ? 1073741824.0
                                     : unit == "MiB" ? 1048576.0 : 1024.0);
                long per = (long)Math.Round(bytes / tok);
                if (per > 0) return per;
            }
        }
        catch { }
        return 0;
    }

    private static long BytesToTokens(long bytes, long perToken)
    {
        if (perToken <= 0 || bytes <= 0) return 0;
        return bytes / perToken;
    }

    /// 「详细信息」里各行的悬停解释。返回空串 = 不加提示。
    ///
    /// 只给**含专业术语或容易被误解**的行写，不搞每行都弹 —— 那样反而吵。
    /// 用词尽量避开行话，必须先说人话再补术语。
    private static string InfoTipFor(string key)
    {
        switch (key)
        {
            case "模型 ID":
            {
                // 基座模型：优先从 .ninfer 头部元数据读（权威），认不出才回退到文件名
                string mp = ModelPath();
                string baseModel = BaseModelFromMeta(mp);
                if (baseModel.Length == 0) baseModel = BaseModelOf(Path.GetFileName(mp));
                string head = (baseModel.Length > 0 && baseModel[0] != '（')
                    ? "基于" + baseModel + "模型训练。\r\n\r\n"
                    : "";
                return head +
                       "ID 本身 = models\\ 里那个 .ninfer 的文件名去掉扩展名，一字不改，\r\n" +
                       "同一个值给三处用：引擎的 --model-id、API 请求的 model 字段、界面显示。\r\n" +
                       "换模型只换文件，ID 自动跟着变。";
            }

            case "推理引擎":
                return "sm_120a 表示这份引擎是按显卡计算能力 12.0（Blackwell）编译的。\r\n" +
                       "\r\n" +
                       "其他显卡同理：sm_86 = 8.6（RTX 30 系）、sm_89 = 8.9（RTX 40 系）。\r\n" +
                       "一个架构配一个二进制，没有 PTX 回退 ——\r\n" +
                       "所以安装器只释放匹配你显卡的那一个，另外两档不落盘，省约 3 GB。";

            case "KV 设备池":
                return "KV 缓存放在「显存」里的那一层（KV = 键值缓存，模型记上下文用的）。\r\n" +
                       "\r\n" +
                       "容量按显卡总显存分档：\r\n" +
                       "· 13 GB 以上 → 17920 token\r\n" +
                       "· 10 GB 以上 → 8192 token\r\n" +
                       "· 不到 10 GB → 4032 token（你这一档）\r\n" +
                       "\r\n" +
                       "token 越多能同时记住的上下文越长，但会挤占放模型的显存。";

            case "KV 主机池":
                return "KV 缓存换页到「内存」里的那一层。\r\n" +
                       "这块是「钉住的」（pinned）—— 不可换页，必须真实占用物理内存。\r\n" +
                       "\r\n" +
                       "比显存慢，但容量大得多。\r\n" +
                       "关掉磁盘 KV 缓存时，上下文能装多少基本就受它限制。";

            case "磁盘 KV 缓存":
                return "把换出的 KV 写到「磁盘」的那一层。\r\n" +
                       "\r\n" +
                       "开启后上下文长度改由磁盘空间决定，能突破「显存 + 内存」的硬顶；\r\n" +
                       "代价是速度 —— 取回换出的 KV 要读盘。\r\n" +
                       "\r\n" +
                       "完整利弊见 设置 → 磁盘 KV 缓存。";

            case "物理上下文":
                return "三层 KV（显存 + 内存 + 磁盘）加起来「实际能装」多少 —— 桌子有多大。\r\n" +
                       "\r\n" +
                       "和下面的「逻辑上下文」成对：\r\n" +
                       "· 物理上下文 = 桌子有多大\r\n" +
                       "· 逻辑上下文 = 题目允许你写多长\r\n" +
                       "\r\n" +
                       "开磁盘 KV 缓存会让这一项变大。";

            case "逻辑上下文":
                return "--max-context：请求「最多能声明」多少 token。\r\n" +
                       "\r\n" +
                       "这是启动参数里的固定值（本程序为 262144），属于「上限声明」，\r\n" +
                       "不代表真能装下 —— 能不能撑到那么长要看「物理上下文」。";

            case "推测解码":
                return "MTP = Multi-Token Prediction（多 token 预测）。\r\n" +
                       "\r\n" +
                       "一次草拟多个 token 再一起验证，命中的话一轮就前进好几个，能明显提速。\r\n" +
                       "「草稿 N token」是每轮草拟几个：越大越激进，命中率低时反而更慢。";

            case "API 密钥":
                return "引擎端口 8095「必须」带这个 key；网页端口 8096 不需要。\r\n" +
                       "\r\n" +
                       "第三方客户端如果报 401，先查这里。\r\n" +
                       "设置页可以再加自定义密钥。";

            case "推理等级":
                return "控制模型是否输出「思考过程」（reasoning）。\r\n" +
                       "\r\n" +
                       "优先级：模型名带 -think 别名 ＞ 客户端发的 reasoning_effort ＞ 这里的全局设置。\r\n" +
                       "档位越高想得越久，也越慢。";

            case "思考代价":
                return "MTP 推测解码在「推理类文本」上命中率低，基本没有加速效果。\r\n" +
                       "\r\n" +
                       "所以开启思考后生成会明显变慢，这是正常的，不是出故障。";

            default:
                return "";
        }
    }

    /// 从引擎日志里解析它自己识别的模型名称：
    ///   2026-10-05 13:48:44  INFO  engine ready | bonsai2-27b | total 4.5s | ...
    private static string ParseEngineModelName(string logPath)
    {
        try
        {
            string[] lines = ReadLogLines(logPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                Match m = Regex.Match(lines[i], @"engine ready\s*\|\s*([^|]+?)\s*\|");
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch { }
        return "";
    }

    private static string LanIp()
    {
        List<string> ips = AllLanIps();
        return ips.Count > 0 ? ips[0] : "127.0.0.1";
    }

    // ---------- 选项卡 2：控制模型 ----------
    private TabPage BuildControlTab()
    {
        var page = new TabPage("控制模型") { BackColor = C.Bg, ForeColor = C.Fg };
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = C.Bg,
            Padding = new Padding(S(18), S(16), S(18), S(16))
        };

        // ---- 运行日志：圆角卡片本身就是日志底色，圆角里再放文本框 ----
        _txtLog = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BackColor = C.LogBg, ForeColor = C.LogFg, BorderStyle = BorderStyle.None,
            Font = UiFont(10F), WordWrap = false
        };
        _logCard = new W11Card
        {
            Dock = DockStyle.Fill, Bg = C.Bg, Fill = C.LogBg, Border = C.Line,
            Radius = 10, Padding = new Padding(S(12), S(8), S(10), S(10))
        };
        _logCard.Controls.Add(_txtLog);
        RestoreLog();      // 切主题重建窗口时，把之前的日志接着显示

        var logTitle = new Label
        {
            Dock = DockStyle.Top, Height = S(32), Text = "运行日志",
            Font = UiFont(9.5F, FontStyle.Bold), ForeColor = C.Dim, BackColor = C.Bg,
            Padding = new Padding(S(4), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft
        };

        // ---- 按钮条（Win11 圆角按钮）----
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = S(60), FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, BackColor = C.Bg, Padding = new Padding(0, S(2), 0, S(10))
        };
        _btnLoad = MakeW11Btn("载入模型", C.Accent, C.AccentHover, Color.White);
        _btnUnload = MakeW11Btn("卸载模型", C.Btn2, W11.Mix(C.Btn2, C.Fg, 0.16f), C.Fg);
        _btnOpen = MakeW11Btn("打开模型", C.Btn2, W11.Mix(C.Btn2, C.Fg, 0.16f), C.Fg);
        _btnLoad.Clicked += delegate { LoadModel(); };
        _btnUnload.Clicked += delegate { UnloadModel(); };
        _btnOpen.Clicked += delegate { OpenPage(); };
        bar.Controls.Add(_btnLoad);
        bar.Controls.Add(_btnUnload);
        bar.Controls.Add(_btnOpen);

        var gap = new Panel { Dock = DockStyle.Top, Height = S(12), BackColor = C.Bg };

        // ---- 状态卡片 ----
        //   注意内边距：里面的 Label 是不透明的，Dock=Fill 会盖满整张卡，
        //   圆角就被"填成方角"了。留出内边距让圆角露出来。
        _lblState = new Label
        {
            Dock = DockStyle.Fill, Font = UiFont(12F, FontStyle.Bold),
            ForeColor = C.Dim, TextAlign = ContentAlignment.MiddleLeft, BackColor = C.Card,
            Padding = new Padding(S(14), 0, 0, 0), Text = "● 模型未载入"
        };
        _stateCard = new W11Card
        {
            Dock = DockStyle.Top, Height = S(54), Bg = C.Bg, Fill = C.Card, Border = C.Line,
            Radius = 10, Padding = new Padding(S(8), S(7), S(8), S(7))
        };
        _stateCard.Controls.Add(_lblState);

        host.Controls.Add(_logCard);
        host.Controls.Add(logTitle);
        host.Controls.Add(bar);
        host.Controls.Add(gap);
        host.Controls.Add(_stateCard);
        page.Controls.Add(host);
        return page;
    }

    /// Win11 风格的圆角按钮
    private W11Button MakeW11Btn(string text, Color fill, Color hover, Color textColor)
    {
        return new W11Button
        {
            Text = text, Width = S(132), Height = S(42),
            Margin = new Padding(0, 0, S(10), 0),
            Bg = C.Bg, Fill = fill, FillHover = hover, FillDown = hover,
            TextColor = textColor, Font = UiFont(11F, FontStyle.Bold),
            Radius = W11.S(8), Border = Color.Empty
        };
    }

    // ---------- 选项卡 3：设置 ----------
    // ==================== 设置页：二级菜单 + 三级内容 ====================

    /// 左侧二级菜单的项（顺序即显示顺序）
    private static readonly string[] SetNames = new string[]
        { "推理等级", "磁盘 KV 缓存", "KVMem 缓存", "局域网访问控制", "API 密钥", "外观", "关于" };

    private W11NavItem[] _navItems;
    private Panel[] _setPages;
    private W11OptionRow[] _themeRows;
    private W11DropDown _effortCombo;
    private Panel _keyListHost;
    private Label _lblKeyCount;

    private TabPage BuildSettingsTab()
    {
        W11.Dpi = _dpiScale;
        var page = new TabPage("设置") { BackColor = C.Bg, ForeColor = C.Fg };
        var host = new Panel
        {
            Dock = DockStyle.Fill, BackColor = C.Bg,
            Padding = new Padding(S(20), S(18), S(20), S(18))
        };

        // ---------- 左侧：二级菜单 ----------
        var nav = new Panel { Dock = DockStyle.Left, Width = S(196), BackColor = C.Bg };
        _navItems = new W11NavItem[SetNames.Length];
        int ny = 0;
        for (int i = 0; i < SetNames.Length; i++)
        {
            var it = new W11NavItem
            {
                Text = SetNames[i], Left = 0, Top = ny, Width = nav.Width, Height = S(42),
                Bg = C.Bg, Card = C.Card, CardAlt = C.CardAlt,
                Accent = C.Accent, Fg = C.Fg, Dim = C.Dim, Font = UiFont(10.5F)
            };
            int idx = i;
            it.Picked += delegate { SelectSettingsPage(idx); };
            nav.Controls.Add(it);
            _navItems[i] = it;
            ny += S(46);
        }
        nav.Resize += delegate
        {
            for (int i = 0; i < _navItems.Length; i++) _navItems[i].Width = nav.ClientSize.Width;
        };

        // ---------- 右侧：三级内容 ----------
        var content = new Panel
        {
            Dock = DockStyle.Fill, BackColor = C.Bg,
            Padding = new Padding(S(22), 0, 0, 0)
        };
        // 顺序必须与上面的 SetNames 一一对应：
        //   0 推理等级 · 1 局域网访问控制 · 2 API 密钥 · 3 外观 · 4 关于
        _setPages = new Panel[SetNames.Length];
        _setPages[0] = BuildReasoningPage();
        _setPages[2] = BuildKvmemPage();
        _setPages[3] = BuildAccessPage();
        _setPages[4] = BuildKeysPage();
        _setPages[5] = BuildAppearancePage();
        _setPages[1] = BuildDiskKvPage();
        _setPages[6] = BuildAboutPage();
        for (int i = 0; i < _setPages.Length; i++)
        {
            _setPages[i].Dock = DockStyle.Fill;
            _setPages[i].Visible = false;
            content.Controls.Add(_setPages[i]);
        }

        host.Controls.Add(content);
        host.Controls.Add(nav);
        page.Controls.Add(host);
        SelectSettingsPage(0);
        return page;
    }

    /// 切换二级菜单的选中项，右边显示对应的三级内容
    private void SelectSettingsPage(int idx)
    {
        if (_navItems == null || idx < 0 || idx >= _navItems.Length) return;
        for (int i = 0; i < _navItems.Length; i++)
        {
            _navItems[i].Selected = (i == idx);
            _navItems[i].Invalidate();
        }
        for (int i = 0; i < _setPages.Length; i++)
            if (_setPages[i] != null) _setPages[i].Visible = (i == idx);

        // 按索引刷新对应的内容（索引见 SetNames 的顺序）
        if (idx == 0) RefreshReasoningRows();   // 推理等级
        if (idx == 1) RefreshDiskKvRows();      // 磁盘 KV 缓存
        if (idx == 2) RefreshKvmemRows();       // KVMem 缓存
        if (idx == 4) RefreshKeyRows();         // API 密钥
        if (idx == 5) RefreshAppearanceRows();  // 外观
        // 刚显示出来的这一页：滚动条刷深色 + 去掉横向那条
        DarkScrollbarsSoon();
        ApplyScrollbarStyleSoon();
    }

    /// 稍后统一处理滚动条外观（深色 + 去横向）。
    ///
    /// 为什么要延迟：TabPage 里的控件句柄是「切到那一页」时才创建的，
    /// 而窗口样式只能在句柄就绪后改 —— 切页的同一瞬间调用会打空。
    private void ApplyScrollbarStyleSoon()
    {
        Form f = _win;
        if (f == null || f.IsDisposed) return;
        try
        {
            f.BeginInvoke(new Action(delegate
            {
                try
                {
                    if (C.Dark) Theme.DarkScrollbars(f);
                    W11.NoHorizontalScroll(f);
                }
                catch { }
            }));
        }
        catch { }
    }

    /// 页面大标题 + 一句说明
    private void AddPageHeader(Panel p, string title, string tip)
    {
        p.Controls.Add(new Label
        {
            Text = title, AutoSize = true, Font = UiFont(15F, FontStyle.Bold),
            ForeColor = C.Fg, BackColor = C.Bg, Location = new Point(0, 0)
        });
        p.Controls.Add(new Label
        {
            Text = tip, AutoSize = true, Font = UiFont(9F),
            ForeColor = C.Dim, BackColor = C.Bg, Location = new Point(0, S(36))
        });
    }

    private W11OptionRow MakeOptRow(string title, string sub, int top, int width, int height)
    {
        return new W11OptionRow
        {
            Title = title, Sub = sub, Left = 0, Top = top, Width = width, Height = height,
            Bg = C.Bg, Card = C.Card, CardAlt = C.CardAlt,
            Accent = C.Accent, Fg = C.Fg, Dim = C.Dim, Font = UiFont(10.5F)
        };
    }

    /// 让选项行宽度跟随容器
    private static void FitRows(W11OptionRow[] rows, Panel host, int defW)
    {
        if (rows == null) return;
        int w = host.ClientSize.Width - W11.S(14);
        if (w < W11.S(240)) w = defW;
        for (int i = 0; i < rows.Length; i++) if (rows[i] != null) rows[i].Width = w;
    }

    // ---------- 三级：外观（Win11 风格）----------
    private Panel BuildAppearancePage()
    {
        var p = new Panel { BackColor = C.Bg };
        AddPageHeader(p, "外观", "选择界面的浅色 / 深色，切换后立即生效。");

        string[] tt = new string[] { "跟随系统", "浅色模式", "深色模式" };
        string[] ss = new string[] { "随 Windows 的浅色/深色设置自动切换", "始终使用浅色界面", "始终使用深色界面" };
        ThemeMode[] modes = new ThemeMode[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark };

        _themeRows = new W11OptionRow[3];
        int y = S(66);
        for (int i = 0; i < 3; i++)
        {
            var rr = MakeOptRow(tt[i], ss[i], y, S(520), S(62));
            int idx = i;
            rr.Picked += delegate
            {
                _switchingTheme = true;
                try { SetTheme(modes[idx]); } finally { _switchingTheme = false; }
            };
            p.Controls.Add(rr);
            _themeRows[i] = rr;
            y += S(70);
        }
        p.Resize += delegate { FitRows(_themeRows, p, S(520)); };
        RefreshAppearanceRows();
        return p;
    }

    private void RefreshAppearanceRows()
    {
        if (_themeRows == null) return;
        int cur = Theme.Mode == ThemeMode.Light ? 1 : (Theme.Mode == ThemeMode.Dark ? 2 : 0);
        for (int i = 0; i < _themeRows.Length; i++)
        {
            if (_themeRows[i] == null) continue;
            _themeRows[i].Selected = (i == cur);
            _themeRows[i].Invalidate();
        }
    }

    // ---------- 三级：局域网访问控制 ----------
    private Panel BuildAccessPage()
    {
        var p = new Panel { BackColor = C.Bg };
        AddPageHeader(p, "局域网访问控制", "决定同一网络里哪些设备能访问本机的模型服务。");

        _rbOpen = new RadioButton
        {
            Text = "公开 —— 同一网络里任意设备都能访问",
            AutoSize = true, Location = new Point(0, S(74)),
            Font = UiFont(10.5F), ForeColor = C.Fg, BackColor = C.Bg, Cursor = Cursors.Hand
        };
        _rbList = new RadioButton
        {
            Text = "指定 IP —— 只允许下面列出的设备访问",
            AutoSize = true, Location = new Point(0, S(102)),
            Font = UiFont(10.5F), ForeColor = C.Fg, BackColor = C.Bg, Cursor = Cursors.Hand
        };
        _lblAllow = new Label
        {
            Text = "允许的 IP（一行一个，可写 192.168.1.7 / 192.168.1.0/24 / 192.168.1.*），数量不限：",
            AutoSize = true, Location = new Point(0, S(132)),
            Font = UiFont(9F), ForeColor = C.Dim, BackColor = C.Bg
        };
        _txtAllow = new TextBox
        {
            Location = new Point(0, S(156)),
            Width = S(520), Height = S(150),
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            WordWrap = false, AcceptsReturn = true,
            Font = UiFont(10F), BackColor = C.LogBg, ForeColor = C.LogFg,
            BorderStyle = BorderStyle.FixedSingle
        };
        var btn = new W11Button
        {
            Text = "保存并生效", Left = 0, Top = S(320), Width = S(132), Height = S(38),
            Fill = C.Accent, FillHover = C.AccentHover, FillDown = C.AccentHover,
            Bg = C.Bg, TextColor = Color.White, Font = UiFont(10.5F, FontStyle.Bold), BackColor = C.Bg
        };
        btn.Clicked += delegate { SaveAccessControl(); };

        _lblAcHint = new Label
        {
            Text = "", AutoSize = true, Location = new Point(S(146), S(330)),
            Font = UiFont(9F), ForeColor = C.Dim, BackColor = C.Bg
        };

        p.Controls.Add(_rbOpen);
        p.Controls.Add(_rbList);
        p.Controls.Add(_lblAllow);
        p.Controls.Add(_txtAllow);
        p.Controls.Add(btn);
        p.Controls.Add(_lblAcHint);

        _rbOpen.Checked = AccessConfig.Public;
        _rbList.Checked = !AccessConfig.Public;
        _txtAllow.Text = string.Join("\r\n", AccessConfig.Allow.ToArray());
        _rbOpen.CheckedChanged += delegate { RefreshAccessUi(); };
        _rbList.CheckedChanged += delegate { RefreshAccessUi(); };
        p.Resize += delegate
        {
            int w = p.ClientSize.Width - S(14);
            if (w > S(240)) _txtAllow.Width = w;
        };
        RefreshAccessUi();
        return p;
    }

    private void RefreshAccessUi()
    {
        if (_txtAllow == null) return;
        bool useList = _rbList != null && _rbList.Checked;
        _txtAllow.Enabled = useList;
        _txtAllow.BackColor = useList ? C.LogBg : C.Card;
        if (_lblAcHint != null && !useList)
            _lblAcHint.Text = "当前：公开，任何设备都能访问";
    }

    /// 局域网访问控制保存后立即生效（不需要重开网页服务）
    private void SaveAccessControl()
    {
        try
        {
            AccessConfig.Public = _rbOpen.Checked;
            var list = new List<string>();
            foreach (string line in _txtAllow.Text.Replace("\r", "").Split('\n'))
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (!AccessConfig.LooksValid(t))
                {
                    if (_lblAcHint != null) _lblAcHint.Text = "格式不对：" + t;
                    return;
                }
                if (!list.Contains(t)) list.Add(t);
            }
            AccessConfig.Allow = list;
            AccessConfig.Save(Root);
            RefreshAccessUi();

            if (_lblAcHint != null)
                _lblAcHint.Text = AccessConfig.Public
                    ? "已保存：公开（任意设备可访问）"
                    : "已保存：只允许 " + list.Count + " 条规则";

            Log("访问控制已保存：" + (AccessConfig.Public
                ? "公开（任意设备可访问）"
                : "指定 IP，" + list.Count + " 条规则"));
            if (!AccessConfig.Public && list.Count == 0)
                Log("提示：指定 IP 模式下列表为空，将没有任何设备能访问网页");

            foreach (string ip in AllLanIps())
                Log("  局域网地址：http://" + ip + ":" + WebPort);
        }
        catch (Exception ex)
        {
            if (_lblAcHint != null) _lblAcHint.Text = "保存失败：" + ex.Message;
        }
    }

    // ---------- 三级：API 密钥（Win11 风格）----------
    private Panel BuildKeysPage()
    {
        var p = new Panel { BackColor = C.Bg };
        AddPageHeader(p, "API 密钥", "给不同设备发不同的 key；删掉一条就等于吊销。点一下即生效。");

        // 内置密钥（只读卡片 + 复制）
        var card = new W11Card
        {
            Left = 0, Top = S(70), Width = S(520), Height = S(74),
            Bg = C.Bg, Fill = C.Card, Border = C.Line, BackColor = C.Bg
        };
        card.Controls.Add(new Label
        {
            Text = "程序内置密钥（一直有效）", AutoSize = true,
            Font = UiFont(9F), ForeColor = C.Dim, BackColor = C.Card,
            Location = new Point(S(16), S(10))
        });
        card.Controls.Add(new Label
        {
            Text = WebHost.ApiKey, AutoSize = true,
            Font = UiFont(11F, FontStyle.Bold), ForeColor = C.Fg, BackColor = C.Card,
            Location = new Point(S(16), S(34))
        });
        var bCopyBuiltin = new W11Button
        {
            Text = "复制", Left = S(420), Top = S(20), Width = S(84), Height = S(34),
            Fill = C.CardAlt, FillHover = W11.Mix(C.CardAlt, C.Fg, 0.12f), FillDown = C.CardAlt,
            Bg = C.Bg, TextColor = C.Fg, Font = UiFont(10F), BackColor = C.Card
        };
        bCopyBuiltin.Clicked += delegate { CopyText(WebHost.ApiKey); };
        card.Controls.Add(bCopyBuiltin);
        p.Controls.Add(card);

        // 自定义密钥标题 + 生成按钮
        _lblKeyCount = new Label
        {
            Text = "", AutoSize = true, Location = new Point(0, S(158)),
            Font = UiFont(10.5F, FontStyle.Bold), ForeColor = C.Fg, BackColor = C.Bg
        };
        p.Controls.Add(_lblKeyCount);

        var bGen = new W11Button
        {
            Text = "＋ 生成一条", Left = S(400), Top = S(152), Width = S(120), Height = S(34),
            Fill = C.Accent, FillHover = C.AccentHover, FillDown = C.AccentHover,
            Bg = C.Bg, TextColor = Color.White, Font = UiFont(10F, FontStyle.Bold), BackColor = C.Bg
        };
        bGen.Clicked += delegate
        {
            string k = ApiKeys.Generate();
            ApiKeys.Keys.Add(k);
            ApiKeys.Save(Root);
            RefreshKeyRows();
            Log("已生成新 key：" + k);
        };
        p.Controls.Add(bGen);

        _keyListHost = new Panel
        {
            Left = 0, Top = S(194), Width = S(520), Height = S(230),
            BackColor = C.Bg, AutoScroll = true
        };
        p.Controls.Add(_keyListHost);

        p.Resize += delegate
        {
            int w = p.ClientSize.Width - S(14);
            if (w < S(240)) w = S(520);
            card.Width = w;
            bCopyBuiltin.Left = card.Width - S(100);
            bGen.Left = w - S(132);
            _keyListHost.Width = w;
            // ★ 只调整已有行的宽度，**不要重建**。
            //   以前这里调 RefreshKeyRows()，而它增删控件又会触发布局，
            //   窗口构建/缩放时反复跑，主题切换那种「整个窗口重建」的场景下
            //   就会明显卡顿。
            int rw = _keyListHost.ClientSize.Width - S(20);
            if (rw < S(200)) rw = S(480);
            for (int i = 0; i < _keyListHost.Controls.Count; i++)
                _keyListHost.Controls[i].Width = rw;
        };
        RefreshKeyRows();
        return p;
    }

    private void CopyText(string s)
    {
        try { Clipboard.SetText(s); Log("已复制到剪贴板：" + s); }
        catch (Exception ex) { Log("复制失败：" + ex.Message); }
    }

    /// 重建自定义密钥行（增删后调用）
    private void RefreshKeyRows()
    {
        if (_keyListHost == null) return;
        _keyListHost.SuspendLayout();
        _keyListHost.Controls.Clear();

        int w = _keyListHost.ClientSize.Width - S(20);
        if (w < S(200)) w = S(480);

        if (ApiKeys.Keys.Count == 0)
        {
            _keyListHost.Controls.Add(new Label
            {
                Text = "（还没有自定义密钥。点右上角「＋ 生成一条」，或直接改 程序目录\\API密钥.json）",
                AutoSize = true, Location = new Point(0, S(12)),
                Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
            });
        }

        int y = 0;
        for (int i = 0; i < ApiKeys.Keys.Count; i++)
        {
            string k = ApiKeys.Keys[i];
            var row = new W11Card
            {
                Left = 0, Top = y, Width = w, Height = S(48),
                Bg = C.Bg, Fill = C.Card, Border = C.Line, BackColor = C.Bg
            };
            row.Controls.Add(new Label
            {
                Text = k, AutoSize = true, Location = new Point(S(16), S(15)),
                Font = UiFont(10.5F), ForeColor = C.Fg, BackColor = C.Card
            });

            var bc = new W11Button
            {
                Text = "复制", Top = S(8), Height = S(32), Width = S(70),
                Fill = C.CardAlt, FillHover = W11.Mix(C.CardAlt, C.Fg, 0.12f), FillDown = C.CardAlt,
                Bg = C.Bg, TextColor = C.Fg, Font = UiFont(9.5F), BackColor = C.Card
            };
            bc.Left = w - S(172);
            bc.Clicked += delegate { CopyText(k); };
            row.Controls.Add(bc);

            var bd = new W11Button
            {
                Text = "删除", Top = S(8), Height = S(32), Width = S(70),
                Fill = W11.Mix(C.Card, C.Red, 0.22f),
                FillHover = W11.Mix(C.Card, C.Red, 0.38f),
                FillDown = W11.Mix(C.Card, C.Red, 0.22f),
                Bg = C.Bg, TextColor = C.Fg, Font = UiFont(9.5F), BackColor = C.Card
            };
            bd.Left = w - S(94);
            bd.Clicked += delegate
            {
                ApiKeys.Keys.Remove(k);
                ApiKeys.Save(Root);
                RefreshKeyRows();
                Log("已删除 key：" + k);
            };
            row.Controls.Add(bd);

            _keyListHost.Controls.Add(row);
            y += S(56);
        }

        if (_lblKeyCount != null)
            _lblKeyCount.Text = "自定义密钥（共 " + ApiKeys.Keys.Count + " 条）";
        _keyListHost.ResumeLayout();
    }

    // ---------- 三级：推理等级 ----------
    private Panel BuildReasoningPage()
    {
        var p = new Panel { BackColor = C.Bg, AutoScroll = true };
        AddPageHeader(p, "推理等级（思考）", "客户端自己带了就以客户端的为准；否则一律用这里的档位。");

        var titles = new string[Reasoning.Levels.Length];
        var subs = new string[Reasoning.Levels.Length];
        int sel = 0;
        for (int i = 0; i < Reasoning.Levels.Length; i++)
        {
            string lv = Reasoning.Levels[i];
            titles[i] = (lv == "none") ? "关闭 —— 不思考" : ("思考：" + lv);
            subs[i] = Reasoning.Label(lv);
            if (lv == Reasoning.Effort) sel = i;
        }

        var dd = new W11DropDown
        {
            Left = 0, Top = S(92), Width = S(520),
            Titles = titles, Subs = subs, Selected = sel,
            Bg = C.Bg, Fill = C.Card, Card = C.Card, Line = C.Line,
            Fg = C.Fg, Dim = C.Dim, Accent = C.Accent,
            Font = UiFont(10.5F), BackColor = C.Bg
        };
        dd.Picked += delegate(int idx)
        {
            Reasoning.Effort = Reasoning.Levels[idx];
            Reasoning.Save(Root);
            RefreshReasoningRows();
            RefreshInfoTab();
            Log("推理等级已设为：" + Reasoning.Label(Reasoning.Effort));
        };
        Tip.SetToolTip(dd, "思考开关与档位。\r\n" +
            "客户端自己带了 reasoning_effort 就以客户端为准。\r\n" +
            "档位越高想得越久，也越慢。");
        p.Controls.Add(dd);
        p.Controls.Add(new Label
        {
            Text = "推理等级", AutoSize = true, Location = new Point(0, S(70)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });
        _effortCombo = dd;
        p.Resize += delegate
        {
            int w = p.ClientSize.Width - S(14);
            dd.Width = w < S(260) ? S(520) : w;
        };
        return p;
    }

    private void RefreshReasoningRows()
    {
        if (_effortCombo == null || _effortCombo.IsDisposed) return;
        for (int i = 0; i < Reasoning.Levels.Length; i++)
            if (Reasoning.Levels[i] == Reasoning.Effort && _effortCombo.Selected != i)
            { _effortCombo.Selected = i; _effortCombo.Invalidate(); }
    }

    /// 杀掉上一轮残留的引擎进程（见构造函数的说明）。
    /// 只认引擎自己的进程名，不碰别的东西。
    private static void KillOrphanEngines()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName;
                    if (n != null && n.StartsWith("ninfer-serve", StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
    }

    // ---------- 三级：磁盘 KV 缓存 ----------
    private W11DropDown _dkCombo;
    private TextBox _dkDir, _dkGib;
    private Label _dkConv;
    private ToolTip _tip;

    /// 悬停提示：「关闭」那一项
    /// 「关闭」那一项的提示（精简）
    private const string DiskKvOffTip =
        "关闭磁盘 KV 缓存（当前选中）\r\n" +
        "最快，不读盘，不占磁盘。\r\n" +
        "但上下文受显存 + 内存限制，超出的部分要重算。";

    /// 「开启」那一项的提示（精简）
    private const string DiskKvOnTip =
        "开启磁盘 KV 缓存\r\n" +
        "上下文长度改由磁盘空间决定，长对话不必重算前缀。\r\n" +
        "代价：读盘变慢，需放 SSD，卸载不会自动清理缓存目录。";
    /// 页面标题下方那句话用的（保持中性，不偏向开关任何一方）
    private const string DiskKvTip = DiskKvOnTip;

    private ToolTip Tip
    {
        get
        {
            if (_tip == null)
                _tip = new ToolTip
                {
                    AutoPopDelay = 30000,     // 提示文字很长，给足停留时间
                    InitialDelay = 350,
                    ReshowDelay = 100,
                    ShowAlways = true
                };
            return _tip;
        }
    }

    /// 「详细信息」专用的一份 ToolTip。
    /// 单独一份是因为这张表每次刷新都会重建全部行，
    /// 需要 RemoveAll() 清掉旧行的关联 —— 不能和历史那条共用。
    private ToolTip _tipInfo;

    private ToolTip InfoTip
    {
        get
        {
            if (_tipInfo == null)
                _tipInfo = new ToolTip
                {
                    AutoPopDelay = 30000,
                    InitialDelay = 350,
                    ReshowDelay = 100,
                    ShowAlways = true
                };
            return _tipInfo;
        }
    }

    private Panel BuildDiskKvPage()
    {
        var p = new Panel { BackColor = C.Bg, AutoScroll = true };
        AddPageHeader(p, "磁盘 KV 缓存",
            "把换出的 KV 换页到磁盘，让上下文突破显存/内存的硬顶。鼠标悬停在选项上可看利弊。");

        // ---- 开关：下拉式 ----
        p.Controls.Add(new Label
        {
            Text = "磁盘 KV 缓存", AutoSize = true, Location = new Point(0, S(70)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });
        _dkCombo = new W11DropDown
        {
            Left = 0, Top = S(92), Width = S(520),
            Titles = new string[] { "关闭（默认，最快）", "开启 —— 换出的 KV 写磁盘" },
            Subs = new string[]
            {
                "不写磁盘。上下文受显存 + 内存限制（本机约 4032 token + 8192 MiB）",
                "上下文改由磁盘空间决定，代价是速度"
            },
            Bg = C.Bg, Fill = C.Card, Card = C.Card, Line = C.Line,
            Fg = C.Fg, Dim = C.Dim, Accent = C.Accent,
            Font = UiFont(10.5F), BackColor = C.Bg
        };
        _dkCombo.Selected = DiskKv.Enabled ? 1 : 0;
        _dkCombo.Picked += delegate(int idx)
        {
            DiskKv.Enabled = (idx == 1);
            DiskKv.Save(Root);
            RefreshDiskKvRows();
            RefreshInfoTab();     // 「详细信息」里的磁盘 KV 那行要跟着变
            Log("磁盘 KV 缓存已" + (DiskKv.Enabled ? "开启" : "关闭") + "（下次载入模型时生效）");
        };
        Tip.SetToolTip(_dkCombo, DiskKvTip);
        p.Controls.Add(_dkCombo);

        int y = S(150);

        // ---- 缓存目录 ----
        p.Controls.Add(new Label
        {
            Text = "缓存目录（留空 = 安装目录下的 kv-cache\\）",
            AutoSize = true, Location = new Point(0, y + S(6)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });
        _dkDir = new TextBox
        {
            Text = DiskKv.Dir, Left = 0, Top = y + S(28), Width = S(410), Height = S(30),
            AutoSize = false, Font = UiFont(10F),
            BackColor = C.Card, ForeColor = C.Fg, BorderStyle = BorderStyle.FixedSingle
        };
        var bBrowse = new W11Button
        {
            Text = "浏览…", Left = S(420), Top = y + S(28), Width = S(90), Height = S(30),
            Fill = C.CardAlt, FillHover = W11.Mix(C.CardAlt, C.Fg, 0.14f), FillDown = C.CardAlt,
            Bg = C.Bg, TextColor = C.Fg, Font = UiFont(9.5F), BackColor = C.Bg
        };
        bBrowse.Clicked += delegate
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择磁盘 KV 缓存目录";
                string cur = _dkDir.Text.Trim();
                if (cur.Length > 0 && Directory.Exists(cur)) dlg.SelectedPath = cur;
                // BonsaiTray 本身不是 Form，有窗口就用窗口当 owner，没有就不指定
                DialogResult r = (_win != null && !_win.IsDisposed)
                    ? dlg.ShowDialog(_win)
                    : dlg.ShowDialog();
                if (r == DialogResult.OK && dlg.SelectedPath.Length > 0)
                    _dkDir.Text = dlg.SelectedPath;
            }
        };
        Tip.SetToolTip(_dkDir, "换出的 KV 写到这里。\r\n建议放 SSD —— 机械硬盘取回时非常慢。\r\n留空则用安装目录下的 kv-cache\\。");
        Tip.SetToolTip(bBrowse, "选择一个目录作为磁盘 KV 缓存的位置。");
        p.Controls.Add(_dkDir);
        p.Controls.Add(bBrowse);

        y += S(70);

        // ---- 容量预算 ----
        p.Controls.Add(new Label
        {
            Text = "磁盘层预算（GB）—— 上下文长度大致就受这个限制",
            AutoSize = true, Location = new Point(0, y + S(6)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });
        _dkGib = new TextBox
        {
            Text = DiskKv.Gib.ToString(), Left = 0, Top = y + S(28), Width = S(120), Height = S(30),
            AutoSize = false, Font = UiFont(10F),
            BackColor = C.Card, ForeColor = C.Fg, BorderStyle = BorderStyle.FixedSingle
        };
        Tip.SetToolTip(_dkGib, "磁盘上最多放多少 GB 的 KV。引擎默认 64。\r\n越大能支持的上下文越长，占的磁盘也越多。");
        p.Controls.Add(_dkGib);

        var bSave = new W11Button
        {
            Text = "保存", Left = S(132), Top = y + S(28), Width = S(100), Height = S(30),
            Fill = C.Accent, FillHover = C.AccentHover, FillDown = C.AccentHover,
            Bg = C.Bg, TextColor = Color.White, Font = UiFont(10F, FontStyle.Bold), BackColor = C.Bg
        };
        bSave.Clicked += delegate { SaveDiskKvSettings(); };
        p.Controls.Add(bSave);

        y += S(78);

        // ---- 换算说明卡片（公式 + 随模型变化的数值）----
        var conv = new W11Card
        {
            Left = 0, Top = y, Width = S(520), Height = S(186),
            Bg = C.Bg, Fill = C.Card, Border = C.Line, Radius = 10, BackColor = C.Bg
        };
        var lConvTitle = new Label
        {
            Text = "怎么折算成 token",
            AutoSize = false, AutoEllipsis = false, Location = new Point(S(16), S(12)), Size = new Size(S(486), S(24)),
            Font = UiFont(10.5F, FontStyle.Bold), ForeColor = C.Fg, BackColor = C.Card
        };
        var lConvFormula = new Label
        {
            Text = "每 token 字节数 = 引擎的 KV runtime ÷ KV token 数（从引擎日志读，随模型变化）\r\n"
                 + "物理上下文 token ≈ （显存设备池 + 内存主机池 + 磁盘层）÷ 每 token 字节数",
            AutoSize = false, AutoEllipsis = false, Location = new Point(S(16), S(40)), Size = new Size(S(486), S(46)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Card
        };
        _dkConv = new Label
        {
            Text = "", AutoSize = false, AutoEllipsis = false,
            Location = new Point(S(16), S(92)), Size = new Size(S(486), S(84)),
            Font = UiFont(9.5F), ForeColor = C.Fg, BackColor = C.Card
        };
        conv.Controls.Add(lConvTitle);
        conv.Controls.Add(lConvFormula);
        conv.Controls.Add(_dkConv);
        Tip.SetToolTip(conv, DiskKvTip);
        p.Controls.Add(conv);

        // ★ 这张卡片整体按内容自动定高。
        //   原来高度写死 S(186)、_dkConv 写死 S(84)，而实际有 5 行文字，
        //   「物理上下文」那一行就被卡片底边裁掉了。公式那两行也可能换行。
        Action layoutConv = delegate
        {
            int cw = Math.Max(S(260), conv.Width - S(32));
            int hTitle = AutoHeight(lConvTitle, cw);
            int hFormula = AutoHeight(lConvFormula, cw);
            lConvTitle.Size = new Size(cw, hTitle);
            lConvFormula.Location = new Point(S(16), S(12) + hTitle + S(4));
            lConvFormula.Size = new Size(cw, hFormula);
            _dkConv.Location = new Point(S(16), lConvFormula.Top + hFormula + S(8));
            int hConv = AutoHeight(_dkConv, cw);
            _dkConv.Size = new Size(cw, hConv);
            conv.Height = _dkConv.Top + hConv + S(14);
        };
        layoutConv();
        RefreshDiskKvConv();
        layoutConv();       // 填完内容后再量一次（行数会变）

        y += conv.Height + S(12);

        // ---- 利弊说明卡片 ----
        // ★ 卡片高度不写死：文字换行后行数不定，写死会把内容裁掉。
        //   先按窄宽度量一遍需要的行数，再定高度。
        var card = new W11Card
        {
            Left = 0, Top = y, Width = S(520), Height = S(172),
            Bg = C.Bg, Fill = C.Card, Border = C.Line, Radius = 10, BackColor = C.Bg
        };

        var lPros = new Label
        {
            Text = "利：上下文长度改由磁盘空间决定，不受 8 GB 显存限制；长对话不必反复重算前缀；不占显存和内存。",
            AutoSize = false, AutoEllipsis = false, Location = new Point(S(16), S(14)), Size = new Size(S(486), S(44)),
            Font = UiFont(9.5F), ForeColor = C.Fg, BackColor = C.Card
        };
        var lCons = new Label
        {
            Text = "弊：速度明显下降（取回 KV 要读盘），务必用 SSD；持续占用磁盘；"
                 + "首次遇到的新前缀仍要完整计算；卸载不会自动清理缓存目录。",
            AutoSize = false, AutoEllipsis = false, Location = new Point(S(16), S(64)), Size = new Size(S(486), S(60)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Card
        };
        var lNote = new Label
        {
            Text = "改完需要重新「载入模型」才生效。",
            AutoSize = false, AutoEllipsis = false, Location = new Point(S(16), S(132)), Size = new Size(S(486), S(24)),
            Font = UiFont(9F, FontStyle.Bold), ForeColor = C.Amber, BackColor = C.Card
        };
        card.Controls.Add(lPros);
        card.Controls.Add(lCons);
        card.Controls.Add(lNote);
        Tip.SetToolTip(card, DiskKvTip);
        p.Controls.Add(card);

        // 按当前宽度重新排版这张卡片（换行后自动定高）
        Action layoutCard = delegate
        {
            int cw = Math.Max(S(260), card.Width - S(32));
            int h1 = AutoHeight(lPros, cw);
            int h2 = AutoHeight(lCons, cw);
            int h3 = AutoHeight(lNote, cw);
            lPros.Size = new Size(cw, h1);
            lCons.Location = new Point(S(16), S(14) + h1 + S(8));
            lCons.Size = new Size(cw, h2);
            lNote.Location = new Point(S(16), lCons.Top + h2 + S(8));
            lNote.Size = new Size(cw, h3);
            card.Height = lNote.Top + h3 + S(14);
        };
        layoutCard();

        p.Resize += delegate
        {
            int w = p.ClientSize.Width - S(14);
            if (w < S(260)) w = S(520);
            _dkCombo.Width = w;
            card.Width = w;
            conv.Width = w;
            _dkDir.Width = Math.Max(S(200), w - S(110));
            bBrowse.Left = w - S(100);
            layoutConv();
            // 卡片高度会随换行变化，后面那张卡片得跟着挪
            card.Top = conv.Top + conv.Height + S(12);
            layoutCard();
        };
        RefreshDiskKvRows();
        return p;
    }

    /// 刷新换算数值后也要重新排版（行数可能变多）
    private void RelayoutDiskKvConv()
    {
        if (_dkConv == null || _dkConv.IsDisposed) return;
        Control card = _dkConv.Parent;
        if (card == null) return;
        int cw = Math.Max(S(260), card.Width - S(32));
        int hConv = AutoHeight(_dkConv, cw);
        _dkConv.Size = new Size(cw, hConv);
        card.Height = _dkConv.Top + hConv + S(14);
    }

    /// 让 Label 按给定宽度自动换行，并算出需要多高。
    /// 用 TextRenderer.MeasureText（和实际绘制同一套度量），加上 WordBreak。
    private static int AutoHeight(Label l, int width)
    {
        try
        {
            Size sz = TextRenderer.MeasureText(l.Text, l.Font,
                new Size(Math.Max(20, width), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return Math.Max(sz.Height, l.Font.Height);
        }
        catch { return l.Font.Height * 2; }
    }

    private void RefreshDiskKvRows()
    {
        if (_dkCombo != null && !_dkCombo.IsDisposed)
        {
            int want = DiskKv.Enabled ? 1 : 0;
            if (_dkCombo.Selected != want) { _dkCombo.Selected = want; _dkCombo.Invalidate(); }
        }
        RefreshDiskKvConv();
    }

    /// 换算卡片里的实时数值。**随模型变化** —— 每 token 字节数是按当前模型实测的。
    private void RefreshDiskKvConv()
    {
        if (_dkConv == null || _dkConv.IsDisposed) return;
        try
        {
            long per = BonsaiTray.BytesPerToken();
            var cfg = LaunchConfig.Load(Root);
            if (per <= 0)
            {
                _dkConv.Text = "当前模型还没有可用数据。\r\n"
                             + "先成功载入一次模型，引擎会打印 KV runtime，之后这里就能算出具体 token 数。";
                return;
            }
            long hostB = (long)cfg.HostKvMiB * 1024L * 1024L;
            long diskB = (long)DiskKv.Gib * 1073741824L;
            long devTok = cfg.KvCapacity;
            long hostTok = BytesToTokens(hostB, per);
            long diskTok = BytesToTokens(diskB, per);

            string t = "当前模型：每 token ≈ " + per.ToString("N0") + " 字节（"
                     + (per / 1024.0).ToString("N1") + " KiB）\r\n"
                     + "· 显存设备池 " + devTok.ToString("N0") + " token\r\n"
                     + "· 内存主机池 " + cfg.HostKvMiB + " MiB  ≈ " + hostTok.ToString("N0") + " token\r\n"
                     + "· 磁盘层 " + DiskKv.Gib + " GiB  ≈ " + diskTok.ToString("N0") + " token\r\n"
                     + "物理上下文 ≈ " + (devTok + hostTok + (DiskKv.Enabled ? diskTok : 0)).ToString("N0")
                     + " token" + (DiskKv.Enabled ? "" : "（磁盘层未开启，不计入）");
            _dkConv.Text = t;
        }
        catch { }
    }

    private void SaveDiskKvSettings()
    {
        if (_dkDir != null && !_dkDir.IsDisposed) DiskKv.Dir = _dkDir.Text.Trim();
        if (_dkGib != null && !_dkGib.IsDisposed)
        {
            int v;
            if (int.TryParse(_dkGib.Text.Trim(), out v) && v >= 1 && v <= 4096) DiskKv.Gib = v;
            else { _dkGib.Text = DiskKv.Gib.ToString(); Log("磁盘层预算要是 1~4096 之间的整数，已改回 " + DiskKv.Gib); }
        }
        DiskKv.Save(Root);
        RefreshInfoTab();     // 「详细信息」里的磁盘 KV 那行要跟着变
        Log("磁盘 KV 缓存已保存：" + (DiskKv.Enabled ? "开启" : "关闭")
            + " · 目录 " + DiskKv.EffectiveDir(Root) + " · 预算 " + DiskKv.Gib + " GB");
    }

    // ---------- 三级：KVMem 缓存 ----------
    private W11DropDown _kvCombo, _kvWinCombo, _kvRetCombo;
    private Panel _kvWinCustom, _kvRetCustom;
    private TextBox _kvWinBox, _kvRetBox;
    private W11Card _kvCard;
    private Label _kvWinLabel, _kvRetLabel;
    private bool _kvWinCustomOn, _kvRetCustomOn;
    private Label _kvEst;

    /// 估算卡片上的总体提示（精简版）
    private const string KvmemTip =
        "KVMem 把超出显存的历史 KV 换出到内存，需要时按内容相关度检索回来。\r\n" +
        "显存占用固定 32 MiB，不受这里设置影响。";
    // 三个下拉各自的提示 —— 不能共用一份（否则鼠标停在哪都显示同样一大段）
    private const string KvmemRingTip =
        "环形缓存开关。\r\n" +
        "开：换出的历史 KV 建索引，需要时检索回来。\r\n" +
        "关：不检索历史，超出的部分直接丢弃。";

    private const string KvmemWinTip =
        "环形窗口：能缓存多长的历史。\r\n" +
        "调大利：能记住更早的内容。\r\n" +
        "调大弊：主机内存需求成比例上升，主机池不够会启动失败。\r\n" +
        "显存不受影响。";

    private const string KvmemRetTip =
        "每次取回：一次从历史里捞回多少。\r\n" +
        "调大利：命中后连续复用更久。\r\n" +
        "调大弊：容易稀释上下文，首 token 延迟变长。";

    /// 做一行「自定义数值」输入：标签 + 输入框 + 单位 + 实时估算
    ///
    /// 返回的 Panel 由调用方负责显示/隐藏（选了「自定义」才显示）。
    /// onChanged 在输入合法时回调，参数是夹到范围内的值。
    private Panel MakeCustomRow(string label, int min, int max, int cur,
                                string unit, Func<int, string> estimate,
                                Action<int> onChanged, out TextBox box, out Label hint)
    {
        var host = new Panel { BackColor = C.Bg, Height = S(54) };
        host.Controls.Add(new Label
        {
            Text = label + "（" + min.ToString("N0") + " ~ " + max.ToString("N0") + "）",
            AutoSize = true, Location = new Point(0, S(2)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });

        var tb = new TextBox
        {
            Text = cur.ToString(), Left = 0, Top = S(24), Width = S(150), Height = S(30),
            AutoSize = false, Font = UiFont(10F),
            BackColor = C.Card, ForeColor = C.Fg, BorderStyle = BorderStyle.FixedSingle
        };
        var lb = new Label
        {
            Text = unit, AutoSize = true, Location = new Point(S(160), S(30)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        };
        var tipLbl = new Label
        {
            Text = "", AutoSize = false,
            Location = new Point(S(210), S(30)), Size = new Size(S(300), S(22)),
            Font = UiFont(9.5F), ForeColor = C.Accent, BackColor = C.Bg
        };

        // 输入时就校验 + 实时算 —— 不等失焦，改一个数字立刻能看到内存变化
        tb.TextChanged += delegate
        {
            int v;
            if (!int.TryParse(tb.Text.Trim(), out v)) { tipLbl.Text = "请输入数字"; tipLbl.ForeColor = C.Amber; return; }
            bool clamped = (v < min || v > max);
            int c = v < min ? min : (v > max ? max : v);
            tipLbl.Text = estimate(c) + (clamped ? "（已超出范围，按 " + c.ToString("N0") + " 算）" : "");
            tipLbl.ForeColor = clamped ? C.Amber : C.Accent;
            onChanged(c);
        };
        tb.Leave += delegate
        {
            int v;
            if (int.TryParse(tb.Text.Trim(), out v))
            {
                int c = v < min ? min : (v > max ? max : v);
                if (c != v) tb.Text = c.ToString();
            }
            else tb.Text = cur.ToString();
        };

        host.Controls.Add(tb); host.Controls.Add(lb); host.Controls.Add(tipLbl);
        box = tb; hint = tipLbl;
        return host;
    }

    private Panel BuildKvmemPage()
    {
        var p = new Panel { BackColor = C.Bg, AutoScroll = true };
        AddPageHeader(p, "KVMem 缓存",
            "环形缓存的窗口与取回量。鼠标停在选项上可看利弊，下方会实时估算内存需求。");

        int y = S(70);

        // ---- 1) 启用开关（下拉式）----
        p.Controls.Add(new Label
        {
            Text = "环形缓存", AutoSize = true, Location = new Point(0, y),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        });
        _kvCombo = new W11DropDown
        {
            Left = 0, Top = y + S(22), Width = S(520),
            Titles = new string[] { "开启（默认）", "关闭 —— 不使用环形缓存" },
            Subs = new string[]
            {
                "把换出的历史 KV 建索引，需要时按内容相关度取回",
                "关掉后不检索历史，超出的部分直接丢弃"
            },
            Bg = C.Bg, Fill = C.Card, Card = C.Card, Line = C.Line,
            Fg = C.Fg, Dim = C.Dim, Accent = C.Accent,
            Font = UiFont(10.5F), BackColor = C.Bg
        };
        _kvCombo.Selected = Kvmem.Ring ? 0 : 1;
        _kvCombo.Picked += delegate(int idx)
        {
            Kvmem.Ring = (idx == 0);
            Kvmem.Save(Root);
            RefreshKvmemRows();
            Log("KVMem 环形缓存已" + (Kvmem.Ring ? "开启" : "关闭") + "（下次载入模型时生效）");
        };
        Tip.SetToolTip(_kvCombo, KvmemRingTip);
        p.Controls.Add(_kvCombo);
        y += S(76);

        // ---- 2) 窗口（下拉式，最后一项是自定义）----
        _kvWinLabel = new Label
        {
            Text = "环形窗口", AutoSize = true, Location = new Point(0, y),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        };
        p.Controls.Add(_kvWinLabel);
        int wN = Kvmem.WindowChoices.Length;
        string[] wt = new string[wN + 1];
        string[] ws = new string[wN + 1];
        // 数值不在预设档位里，就当作「自定义」并展开输入行
        _kvWinCustomOn = !Kvmem.IsPresetWindow(Kvmem.Window);
        int wsel = _kvWinCustomOn ? wN : 0;
        for (int i = 0; i < wN; i++)
        {
            int w = Kvmem.WindowChoices[i];
            wt[i] = w.ToString("N0") + " token";
            ws[i] = (w == 16384) ? "默认 —— 实测可用的值"
                                 : ("主机内存需求约 " + Kvmem.HostMiBFor(w) + " MiB");
            if (w == Kvmem.Window) wsel = i;
        }
        wt[wN] = "自定义…";
        ws[wN] = "自己填数值，下面会实时算出内存占用";

        _kvWinCombo = new W11DropDown
        {
            Left = 0, Top = y + S(22), Width = S(520),
            Titles = wt, Subs = ws, Selected = wsel,
            Bg = C.Bg, Fill = C.Card, Card = C.Card, Line = C.Line,
            Fg = C.Fg, Dim = C.Dim, Accent = C.Accent,
            Font = UiFont(10.5F), BackColor = C.Bg
        };
        Tip.SetToolTip(_kvWinCombo, KvmemWinTip);
        p.Controls.Add(_kvWinCombo);
        y += S(76);

        // 自定义输入行（只在选了「自定义」时显示）
        TextBox winBox; Label winHint;
        var winCustom = MakeCustomRow("窗口", Kvmem.WindowMin, Kvmem.WindowMax,
            Kvmem.Window, "token",
            delegate(int v) { return "约需主机内存 " + Kvmem.HostMiBFor(v).ToString("N0") + " MiB"; },
            delegate(int v)
            {
                Kvmem.Window = v;
                Kvmem.Save(Root);
                RefreshKvmemRows();          // 估算卡片跟着变
                LayoutKvmem();
            },
            out winBox, out winHint);
        winCustom.Left = 0; winCustom.Top = y; winCustom.Width = S(520);
        winCustom.Visible = _kvWinCustomOn;
        p.Controls.Add(winCustom);
        _kvWinCustom = winCustom;
        _kvWinBox = winBox;
        // ★ 不要在这里无条件 y += 高度 —— 自定义行隐藏时不该占位置，
        //   否则下面会留一块空白。位置统一由 LayoutKvmem() 算。
        y += _kvWinCustomOn ? winCustom.Height : 0;

        _kvWinCombo.Picked += delegate(int idx)
        {
            if (idx >= wN)
            {
                // 选「自定义」→ 显示输入行，把当前值填进去
                Kvmem.Window = Kvmem.ClampWindow(Kvmem.Window);
                _kvWinBox.Text = Kvmem.Window.ToString();
                _kvWinCustom.Visible = true;
                _kvWinCustomOn = true;
                _kvWinCombo.Selected = wN;
                _kvWinCombo.Invalidate();
            }
            else
            {
                Kvmem.Window = Kvmem.WindowChoices[idx];
                _kvWinCustom.Visible = false;
                _kvWinCustomOn = false;
            }
            Kvmem.Save(Root);
            RefreshKvmemRows();
            LayoutKvmem();
            Log("KVMem 窗口已设为 " + Kvmem.Window.ToString("N0") + " token（下次载入模型时生效）");
        };

        // ---- 3) 取回量（下拉式，最后一项是自定义）----
        _kvRetLabel = new Label
        {
            Text = "每次取回", AutoSize = true, Location = new Point(0, y),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Bg
        };
        p.Controls.Add(_kvRetLabel);
        int rN = Kvmem.RetrieveChoices.Length;
        string[] rt2 = new string[rN + 1];
        string[] rs2 = new string[rN + 1];
        _kvRetCustomOn = !Kvmem.IsPresetRetrieve(Kvmem.Retrieve);
        int rsel = _kvRetCustomOn ? rN : 0;
        for (int i = 0; i < rN; i++)
        {
            int r = Kvmem.RetrieveChoices[i];
            rt2[i] = r.ToString("N0") + " token";
            rs2[i] = (r == 8192) ? "默认"
                   : (r > 8192 ? "取回更多，更容易稀释上下文" : "取回更少，上下文更聚焦");
            if (r == Kvmem.Retrieve) rsel = i;
        }
        rt2[rN] = "自定义…";
        rs2[rN] = "自己填数值";

        _kvRetCombo = new W11DropDown
        {
            Left = 0, Top = y + S(22), Width = S(520),
            Titles = rt2, Subs = rs2, Selected = rsel,
            Bg = C.Bg, Fill = C.Card, Card = C.Card, Line = C.Line,
            Fg = C.Fg, Dim = C.Dim, Accent = C.Accent,
            Font = UiFont(10.5F), BackColor = C.Bg
        };
        Tip.SetToolTip(_kvRetCombo, KvmemRetTip);
        p.Controls.Add(_kvRetCombo);
        y += S(76);

        TextBox retBox; Label retHint;
        var retCustom = MakeCustomRow("取回量", Kvmem.RetrieveMin, Kvmem.RetrieveMax,
            Kvmem.Retrieve, "token",
            delegate(int v)
            {
                return v > Kvmem.Window
                    ? "比窗口还大，会按窗口封顶"
                    : ("占窗口的 " + Math.Round(100.0 * v / Math.Max(1, Kvmem.Window)) + "%");
            },
            delegate(int v)
            {
                Kvmem.Retrieve = v;
                Kvmem.Save(Root);
                RefreshKvmemRows();
                LayoutKvmem();
            },
            out retBox, out retHint);
        retCustom.Left = 0; retCustom.Top = y; retCustom.Width = S(520);
        retCustom.Visible = _kvRetCustomOn;
        p.Controls.Add(retCustom);
        _kvRetCustom = retCustom;
        _kvRetBox = retBox;
        y += _kvRetCustomOn ? retCustom.Height : 0;

        _kvRetCombo.Picked += delegate(int idx)
        {
            if (idx >= rN)
            {
                Kvmem.Retrieve = Kvmem.ClampRetrieve(Kvmem.Retrieve);
                _kvRetBox.Text = Kvmem.Retrieve.ToString();
                _kvRetCustom.Visible = true;
                _kvRetCustomOn = true;
                _kvRetCombo.Selected = rN;
                _kvRetCombo.Invalidate();
            }
            else
            {
                Kvmem.Retrieve = Kvmem.RetrieveChoices[idx];
                _kvRetCustom.Visible = false;
                _kvRetCustomOn = false;
            }
            Kvmem.Save(Root);
            RefreshKvmemRows();
            LayoutKvmem();
            Log("KVMem 取回量已设为 " + Kvmem.Retrieve.ToString("N0") + " token（下次载入模型时生效）");
        };

        // ---- 4) 实时估算 ----
        var card = new W11Card
        {
            Left = 0, Top = y, Width = S(520), Height = S(132),
            Bg = C.Bg, Fill = C.Card, Border = C.Line, Radius = 10, BackColor = C.Bg
        };
        card.Controls.Add(new Label
        {
            Text = "当前设置的估算", AutoSize = false,
            Location = new Point(S(16), S(12)), Size = new Size(S(486), S(24)),
            Font = UiFont(10.5F, FontStyle.Bold), ForeColor = C.Fg, BackColor = C.Card
        });
        _kvEst = new Label
        {
            // ★ Label 默认单行、超出直接截掉 —— AutoEllipsis=false 配合
            //   AutoHeight 量出的高度，文字才会真正换行显示。
            Text = "", AutoSize = false, AutoEllipsis = false,
            Location = new Point(S(16), S(40)), Size = new Size(S(486), S(82)),
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Card
        };
        card.Controls.Add(_kvEst);
        Tip.SetToolTip(card, KvmemTip);
        p.Controls.Add(card);
        _kvCard = card;

        // 卡片高度按内容自动定 —— 原来写死 S(132)，文字换行后末行会被裁掉。
        // 页面整体高度也随之调整，避免出现底部滚动条。
        Action fitKv = delegate
        {
            int cw = Math.Max(S(260), card.Width - S(32));
            int h = AutoHeight(_kvEst, cw);
            _kvEst.Size = new Size(cw, h);
            card.Height = _kvEst.Top + h + S(14);
        };
        fitKv();

        p.Resize += delegate
        {
            int w = p.ClientSize.Width - S(14);
            if (w < S(260)) w = S(520);
            _kvCombo.Width = w;
            _kvWinCombo.Width = w;
            _kvRetCombo.Width = w;
            card.Width = w;
            fitKv();
        };
        RefreshKvmemRows();
        fitKv();   // 填完内容后再量一次（行数可能变）
        return p;
    }

    /// 自定义输入行显示/隐藏后，把下面的内容整体下移或上移。
    ///
    /// 不重排的话，隐藏的自定义行仍占着位置（或者显示出来时盖住下面的卡片）。
    /// 这里按「标签 → 下拉 →（自定义行）→ 标签 → 下拉 →（自定义行）→ 估算卡片」
    /// 的顺序把 y 重新算一遍。
    private void LayoutKvmem()
    {
        try
        {
            if (_kvWinLabel == null || _kvRetLabel == null || _kvCard == null) return;

            int y = _kvWinLabel.Top;
            _kvWinLabel.Top = y;
            _kvWinCombo.Top = y + S(22);
            y += S(76);

            _kvWinCustom.Top = y;
            if (_kvWinCustom.Visible) y += _kvWinCustom.Height;

            _kvRetLabel.Top = y;
            _kvRetCombo.Top = y + S(22);
            y += S(76);

            _kvRetCustom.Top = y;
            if (_kvRetCustom.Visible) y += _kvRetCustom.Height;

            _kvCard.Top = y + S(6);
        }
        catch { }
    }

    private void RefreshKvmemRows()
    {
        if (_kvCombo != null && !_kvCombo.IsDisposed)
        {
            int want = Kvmem.Ring ? 0 : 1;
            if (_kvCombo.Selected != want) { _kvCombo.Selected = want; _kvCombo.Invalidate(); }
        }
        // 窗口下拉：不是预设档位就落在「自定义」那一项
        if (_kvWinCombo != null && !_kvWinCombo.IsDisposed)
        {
            int want;
            // ★ 别只看数值 —— 数值等于预设值也可能是"自定义时正好填了同样的数"。
            //   用户明确选过「自定义」就一直保持，否则才按数值反推档位。
            if (_kvWinCustomOn) want = Kvmem.WindowChoices.Length;
            else
            {
                want = Kvmem.WindowChoices.Length;
                for (int i = 0; i < Kvmem.WindowChoices.Length; i++)
                    if (Kvmem.WindowChoices[i] == Kvmem.Window) { want = i; break; }
            }
            if (_kvWinCombo.Selected != want) { _kvWinCombo.Selected = want; _kvWinCombo.Invalidate(); }
            // 输入框里的数值也要跟着同步（换档位时）
            if (_kvWinBox != null && !_kvWinBox.IsDisposed && _kvWinBox.Text != Kvmem.Window.ToString()
                && !_kvWinBox.Focused)
                _kvWinBox.Text = Kvmem.Window.ToString();
        }
        if (_kvRetCombo != null && !_kvRetCombo.IsDisposed)
        {
            int want;
            if (_kvRetCustomOn) want = Kvmem.RetrieveChoices.Length;
            else
            {
                want = Kvmem.RetrieveChoices.Length;
                for (int i = 0; i < Kvmem.RetrieveChoices.Length; i++)
                    if (Kvmem.RetrieveChoices[i] == Kvmem.Retrieve) { want = i; break; }
            }
            if (_kvRetCombo.Selected != want) { _kvRetCombo.Selected = want; _kvRetCombo.Invalidate(); }
            if (_kvRetBox != null && !_kvRetBox.IsDisposed && _kvRetBox.Text != Kvmem.Retrieve.ToString()
                && !_kvRetBox.Focused)
                _kvRetBox.Text = Kvmem.Retrieve.ToString();
        }

        if (_kvEst == null || _kvEst.IsDisposed) return;
        try
        {
            if (!Kvmem.Ring)
            {
                _kvEst.Text = "环形缓存已关闭 —— 不建索引、不检索历史。\r\n显存与内存都不额外占用。";
                _kvEst.ForeColor = C.Dim;
                return;
            }
            var cfg = LaunchConfig.Load(Root);
            long need = (long)Kvmem.HostMiBFor(Kvmem.Window);
            long have = cfg.HostKvMiB;
            bool warn = need > have;

            _kvEst.Text = "窗口 " + Kvmem.Window.ToString("N0") + " token · 取回 " + Kvmem.Retrieve.ToString("N0") + " token\r\n"
                        + "· 显存：32 MiB（固定，不受窗口影响）\r\n"
                        + "· 内存索引：256 MiB（引擎编译期固定，非本页可调）\r\n"
                        + "· 主机 KV 池：当前 " + have + " MiB，本窗口估算需约 " + need + " MiB\r\n"
                        + (warn ? "⚠ 估算需求超过主机池 —— 引擎可能拒绝启动，需调大 --host-kv-mib"
                                : "✓ 主机池看起来够用（估算为经验值，实际以下次载入的日志为准）");
            _kvEst.ForeColor = warn ? C.Amber : C.Dim;

            // 文字行数可能变了（警告那行更长），重新量一次高度，
            // 否则末行会被卡片下沿裁掉。
            Control card = _kvEst.Parent;
            if (card != null)
            {
                int cw = Math.Max(S(260), card.Width - S(32));
                int h = AutoHeight(_kvEst, cw);
                _kvEst.Size = new Size(cw, h);
                card.Height = _kvEst.Top + h + S(14);
            }
        }
        catch { }
    }

    // ---------- 三级：关于 ----------
    private Panel BuildAboutPage()
    {
        var p = new Panel { BackColor = C.Bg, AutoScroll = true };
        AddPageHeader(p, "关于", "版本号、程序信息、内置引擎来源、第三方许可与声明。");

        int w = S(520);
        int y = S(70);
        var cards = new List<W11Card>();

        // ---- 1) 版本号 ----
        var cVer = MakeAboutCard(y, w, S(86));
        cVer.Controls.Add(AboutTitle("版本号"));
        cVer.Controls.Add(new Label
        {
            Text = AppVersion, AutoSize = true, Location = new Point(S(16), S(36)),
            Font = UiFont(15F, FontStyle.Bold), ForeColor = C.Fg, BackColor = C.Card
        });
        // 版本号右侧的「查看更新日志」链接
        var lnkLog = new Label
        {
            Text = "查看更新日志 ›", AutoSize = true,
            Location = new Point(S(16) + TextRenderer.MeasureText(AppVersion,
                UiFont(15F, FontStyle.Bold)).Width + S(16), S(41)),
            Font = UiFont(9.5F), ForeColor = C.Accent, BackColor = C.Card,
            Cursor = Cursors.Hand
        };
        lnkLog.Click += delegate { OpenChangelog(); };
        Tip.SetToolTip(lnkLog, "用系统默认程序打开安装目录里的「更新日志.txt」");
        cVer.Controls.Add(lnkLog);
        cards.Add(cVer);
        y += S(98);

        // ---- 2) 程序 ----
        var cApp = MakeAboutCard(y, w, S(214));
        cApp.Controls.Add(AboutTitle("程序"));
        AddAboutRow(cApp, 0, "名称", "Local Models —— 完全离线运行");
        AddAboutRow(cApp, 1, "引擎", EngineExeName);
        AddAboutRow(cApp, 2, "模型", AboutModelName());
        AddAboutRow(cApp, 3, "服务", "引擎 " + EnginePort + " · 网页 " + WebPort);
        AddAboutNote(cApp, 152,
            "模型由使用者放入安装目录的 models\\ 文件夹，支持任意 .ninfer 模型。", 44);
        cards.Add(cApp);
        y += S(226);

        // ---- 3) 内置推理引擎 ----
        var cEng = MakeAboutCard(y, w, S(224));
        cEng.Controls.Add(AboutTitle("内置推理引擎"));
        AddAboutRow(cEng, 0, "引擎", "NInfer");
        AddAboutRow(cEng, 1, "许可", "Apache License 2.0");
        AddAboutRow(cEng, 2, "上游", "github.com/1314521gjy/ninfer-fusion-kvmem");
        AddAboutNote(cEng, 126,
            "署名：本引擎为上游 NInfer 的下游衍生，包含 Neroued/ninfer、ashalliants/ninfer-3090、"
            + "iamwavecut/ninfer-3090、tancau/ninfer-kvmem-ring、kvmem/kvmem-qw3（作者 Di Chai）"
            + "的成果，均为 Apache-2.0。", 88);
        cards.Add(cEng);
        y += S(236);

        // ---- 4) 第三方许可证 ----
        var cLic = MakeAboutCard(y, w, S(300));
        cLic.Controls.Add(AboutTitle("第三方许可证"));
        string licDir = Path.Combine(Root, "licenses");
        int licCount = 0;
        try { if (Directory.Exists(licDir)) licCount = Directory.GetFiles(licDir).Length; }
        catch { }
        AddAboutRow(cLic, 0, "目录", licDir);
        AddAboutRow(cLic, 1, "数量", licCount > 0 ? (licCount + " 个文件") : "（未找到 licenses 目录）");
        AddAboutNote(cLic, 96,
            "编入引擎的第三方：cpp-httplib(MIT) · ggml-quants(MIT) · llama-jinja(MIT) · "
            + "nlohmann json(MIT) · spdlog+{fmt}(MIT) · utf8proc(MIT) · "
            + "xgrammar+dlpack(Apache-2.0) · picojson(BSD-2-Clause) · "
            + "Qwen chat 模板(Apache-2.0, © Alibaba Cloud)。", 88);
        AddAboutNote(cLic, 188,
            "随包运行库：NVIDIA CUDA（EULA 可再分发条款）· FFmpeg（GNU GPL v2 or later）· "
            + "curl（MIT 类）。完整文本见安装目录的 licenses\\ 目录，请勿删除。", 66);

        var bOpen = new W11Button
        {
            Text = "打开许可文件夹", Left = S(16), Top = S(258), Width = S(150), Height = S(32),
            Fill = C.CardAlt, FillHover = W11.Mix(C.CardAlt, C.Fg, 0.12f), FillDown = C.CardAlt,
            Bg = C.Card, TextColor = C.Fg, Font = UiFont(9.5F), BackColor = C.Card
        };
        bOpen.Clicked += delegate
        {
            try
            {
                if (Directory.Exists(licDir)) Process.Start("explorer.exe", "\"" + licDir + "\"");
                else Log("找不到 licenses 目录：" + licDir);
            }
            catch (Exception ex) { Log("打开失败：" + ex.Message); }
        };
        cLic.Controls.Add(bOpen);
        cards.Add(cLic);
        y += S(312);

        // ---- 5) 声明 ----
        var cDecl = MakeAboutCard(y, w, S(140));
        cDecl.Controls.Add(AboutTitle("声明"));
        cDecl.Controls.Add(new Label
        {
            Text = "本程序所有代码(除引擎)及图形UI由Deepseek Harness V4.1Flash编写",
            AutoSize = false, Location = new Point(S(16), S(40)), Size = new Size(S(480), S(30)),
            Font = UiFont(10.5F), ForeColor = C.Fg, BackColor = C.Card
        });
        AddAboutNote(cDecl, 78,
            "免责：本启动器基于上游 Apache-2.0 项目打包，上游未对其背书。", 40);
        cards.Add(cDecl);

        for (int i = 0; i < cards.Count; i++) p.Controls.Add(cards[i]);
        p.Resize += delegate
        {
            int nw = p.ClientSize.Width - S(14);
            if (nw < S(260)) nw = w;
            for (int i = 0; i < cards.Count; i++) cards[i].Width = nw;
        };
        return p;
    }

    /// 当前模型是基于什么训练的 —— 从文件名里认。
    ///
    /// 引擎的 .ninfer 没有对外约定的「基座模型」字段，只能按常见命名规则猜：
    ///   Qwen / Bonsai / Llama / DeepSeek / Mistral / GLM / Yi / Phi / Gemma 等。
    /// 认不出来就返回空串（界面就不显示这一项，别硬编一个可能错的）。
    private static string BaseModelOf(string modelFile)
    {
        if (string.IsNullOrEmpty(modelFile)) return "";
        string s = modelFile.ToUpperInvariant();

        // 参数规模：27B / 8B / 3.8B 这种
        Match pm = Regex.Match(modelFile, @"(\d+(?:\.\d+)?)\s*[Bb](?![a-zA-Z])");
        string size = pm.Success ? pm.Groups[1].Value + "B" : "";

        // 版本号：qwen3.8 / qwen38 / qwen2.5 / glm4 —— 紧跟在名字后面
        // 注意别把参数规模当成版本号：Mistral-7B 里的 7 是规模不是版本，
        // 所以匹配到的数字后面若紧跟 B 就丢弃。
        string ver = "";
        Match vm = Regex.Match(modelFile,
            @"(?i)(qwen|llama|deepseek|glm|gemma|phi|mistral|mixtral)[-_]?(\d+(?:\.\d+)?)(?!\s*[Bb])");
        if (vm.Success) ver = vm.Groups[2].Value;

        string name = "";
        if (s.Contains("QWEN"))          name = "Qwen";
        else if (s.Contains("BONSAI"))   name = "Bonsai";
        else if (s.Contains("LLAMA"))    name = "Llama";
        else if (s.Contains("DEEPSEEK")) name = "DeepSeek";
        else if (s.Contains("MISTRAL") || s.Contains("MIXTRAL")) name = "Mistral";
        else if (s.Contains("GEMMA"))    name = "Gemma";
        else if (s.Contains("GLM"))      name = "GLM";
        else if (Regex.IsMatch(s, @"(^|[^A-Z])PHI[-_0-9]")) name = "Phi";
        else if (Regex.IsMatch(s, @"(^|[^A-Z])YI[-_0-9]")) name = "Yi";

        if (name.Length == 0)
            return size.Length > 0 ? ("（规模 " + size + "，基座未知）") : "";

        // 拼成「Qwen3.8 27B」这种：版本号紧贴名字，规模用空格分开
        string r = name + ver;
        if (size.Length > 0) r += " " + size;
        return r;
    }

    /// .ninfer 头部元数据的缓存（同一个文件不用反复读）
    private static string _metaFile = "";
    private static string _metaJson = null;

    /// 引擎 /v1/models 报出的参数规模（单位：十亿个参数）。
    /// 文件头里没有这个值，只能等引擎加载后从接口拿。
    private static double _engineParamB = 0;

    /// 引擎就绪后调一次：从 /v1/models 读 n_params，供「基座模型」用。
    internal static void RefreshEngineParams(string apiKey)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + EnginePort + "/v1/models");
            req.Timeout = 3000; req.ReadWriteTimeout = 3000;
            if (!string.IsNullOrEmpty(apiKey)) req.Headers["Authorization"] = "Bearer " + apiKey;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string t = sr.ReadToEnd();
                Match m = Regex.Match(t, "\"n_params\"\\s*:\\s*(\\d+)");
                if (m.Success)
                {
                    double v = 0;
                    double.TryParse(m.Groups[1].Value, out v);
                    if (v > 0) _engineParamB = v / 1e9;
                }
            }
        }
        catch { }
    }

    /// 读 .ninfer 文件头部那段元数据（引擎自己写的 config JSON）。
    ///
    /// 文件开头长这样：
    ///   NINFER...<二进制>...{"components":{"text":{"config":
    ///     {"architectures":["Qwen3_5ForCausalLM"],"model_type":"qwen3_5_text",
    ///      "hidden_size":5120,"num_hidden_layers":64,...}}}}
    ///
    /// 这是**权威**信息 —— 比猜文件名可靠得多。文件很大（几 GB），
    /// 但元数据都在最前面。
    ///
    /// ★ 读多少才够：实测这个模型的元数据 JSON 有 **276 KB** ——
    ///   原来只读 256 KB，JSON 被截断，配平失败，于是"认不出基座"。
    ///   现在读 1 MB，留足余量（只多花几毫秒，反正都在文件头）。
    private static string ReadNinferMeta(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            if (_metaJson != null && _metaFile == path) return _metaJson;

            string json = "";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(1048576, fs.Length);
                byte[] buf = new byte[len];
                int got = 0, r;
                while (got < len && (r = fs.Read(buf, got, len - got)) > 0) got += r;
                string txt = Encoding.UTF8.GetString(buf, 0, got);

                // 从第一个 {"components" 开始，往后找到能配平的 }
                int i = txt.IndexOf("{\"components\"");
                if (i < 0) i = txt.IndexOf("{\"architectures\"");
                if (i < 0) i = txt.IndexOf("{\"config\"");
                if (i < 0) return "";
                int depth = 0, end = -1;
                for (int k = i; k < txt.Length; k++)
                {
                    if (txt[k] == '{') depth++;
                    else if (txt[k] == '}')
                    {
                        depth--;
                        if (depth == 0) { end = k; break; }
                    }
                }
                if (end > i) json = txt.Substring(i, end - i + 1);
            }
            _metaFile = path; _metaJson = json;
            return json;
        }
        catch { return ""; }
    }

    /// 从元数据里取一个字符串字段（正则够了，不必上 JSON 解析器）
    private static string MetaStr(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// 基座模型：从 .ninfer 元数据读。
    /// 例如 model_type = "qwen3_5_text" → "Qwen3.5"；architectures[0] = "Qwen3_5ForCausalLM"。
    /// 读不到就返回空串（界面不显示这一项）。
    private static string BaseModelFromMeta(string ninferPath)
    {
        string json = ReadNinferMeta(ninferPath);
        if (json.Length == 0) return "";

        string mt = MetaStr(json, "model_type");
        string arch = "";
        Match am = Regex.Match(json, "\"architectures\"\\s*:\\s*\\[\\s*\"([^\"]*)\"");
        if (am.Success) arch = am.Groups[1].Value;

        string src = mt.Length > 0 ? mt : arch;
        if (src.Length == 0) return "";

        // qwen3_5_text → Qwen 3.5 ; llama → Llama ; deepseek_v3 → DeepSeek V3
        Match nm = Regex.Match(src, @"^([a-zA-Z]+)[-_]?v?(\d+(?:[._]\d+)?)");
        if (!nm.Success)
        {
            // 没有版本号的，只取名字
            Match n2 = Regex.Match(src, @"^([a-zA-Z]+)");
            if (!n2.Success) return "";
            return Cap(n2.Groups[1].Value);
        }

        string family = Cap(nm.Groups[1].Value);
        string ver = nm.Groups[2].Value.Replace('_', '.');

        // 参数规模：文件头里通常没有 n_params（那是引擎加载后算的），
        // 所以优先用引擎 /v1/models 报的那个；拿不到再从文件名猜。
        string size = "";
        Match pm = Regex.Match(json, "\"n_params\"\\s*:\\s*(\\d+)");
        if (pm.Success)
        {
            double p = 0;
            double.TryParse(pm.Groups[1].Value, out p);
            if (p > 0) size = Math.Round(p / 1e9, 1).ToString("0.#") + "B";
        }
        if (size.Length == 0 && _engineParamB > 0)
            size = _engineParamB.ToString("0.#") + "B";
        if (size.Length == 0)
        {
            Match pm2 = Regex.Match(Path.GetFileName(ninferPath), @"(\d+(?:\.\d+)?)\s*[Bb](?![a-zA-Z])");
            if (pm2.Success) size = pm2.Groups[1].Value + "B";
        }

        string r = family + ver;
        if (size.Length > 0) r += " " + size;
        return r;
    }

    /// 首字母大写，其余保留（qwen → Qwen，deepseek → DeepSeek 需特殊处理）
    private static string Cap(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string low = s.ToLowerInvariant();
        if (low == "qwen") return "Qwen";
        if (low == "llama") return "Llama";
        if (low == "deepseek") return "DeepSeek";
        if (low == "mistral") return "Mistral";
        if (low == "mixtral") return "Mixtral";
        if (low == "gemma") return "Gemma";
        if (low == "glm") return "GLM";
        if (low == "phi") return "Phi";
        if (low == "yi") return "Yi";
        if (low == "bonsai") return "Bonsai";
        return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant();
    }

    /// 打开更新日志。
    ///
    /// 日志是一个纯文本文档，随包放在安装目录的「更新日志.txt」。
    /// 用系统默认程序打开（记事本之类）—— 这样用户在哪儿都能直接看，
    /// 也能自己复制、另存，不必被锁在程序界面里。
    ///
    /// 找不到文件时退回「打开安装目录」，至少让人能自己找一眼。
    private void OpenChangelog()
    {
        try
        {
            string p = Path.Combine(Root, "更新日志.txt");
            if (File.Exists(p))
            {
                // UseShellExecute=true 才会用系统关联的程序打开，而不是当成参数
                Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                Log("已打开更新日志：" + p);
            }
            else
            {
                Log("找不到更新日志：" + p + "，改为打开安装目录");
                Process.Start(new ProcessStartInfo(Root) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log("打开更新日志失败：" + ex.Message);
        }
    }

    /// 「关于」页里的一张卡片
    private W11Card MakeAboutCard(int top, int width, int h)
    {
        return new W11Card
        {
            Left = 0, Top = top, Width = width, Height = h,
            Bg = C.Bg, Fill = C.Card, Border = C.Line, BackColor = C.Bg
        };
    }

    /// 「关于」页里的多行说明块（自动换行）
    private void AddAboutNote(W11Card card, int top, string text, int height)
    {
        card.Controls.Add(new Label
        {
            Text = text, AutoSize = false,
            Location = new Point(W11.S(16), W11.S(top)),
            Size = new Size(W11.S(472), W11.S(height)),
            Font = UiFont(9F), ForeColor = C.Dim, BackColor = C.Card
        });
    }

    /// 「关于」页里各卡片的小标题
    private static Label AboutTitle(string t)
    {
        return new Label
        {
            Text = t, AutoSize = true, Location = new Point(W11.S(16), W11.S(10)),
            Font = new Font("Microsoft YaHei UI", 8.6f, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 140, 140, 140),
            BackColor = Color.Transparent
        };
    }

    /// 「关于」页里的「键 ... 值」行（第 idx 行）
    private void AddAboutRow(W11Card card, int idx, string key, string val)
    {
        int y = W11.S(38) + idx * W11.S(28);
        var k = new Label
        {
            Text = key, AutoSize = false, Width = W11.S(64), Height = W11.S(22),
            Location = new Point(W11.S(16), y), TextAlign = ContentAlignment.MiddleLeft,
            Font = UiFont(9.5F), ForeColor = C.Dim, BackColor = C.Card
        };
        var v = new TextBox
        {
            Text = val, ReadOnly = true, BorderStyle = BorderStyle.None,
            Location = new Point(W11.S(84), y + W11.S(2)), Width = W11.S(400), Height = W11.S(20),
            BackColor = C.Card, ForeColor = C.Fg, Font = UiFont(10F),
            TabStop = false, WordWrap = false, ShortcutsEnabled = true
        };
        card.Controls.Add(k);
        card.Controls.Add(v);
    }

    private static string AboutModelName()
    {
        string mp = ModelPath();
        return mp.Length > 0 ? Path.GetFileName(mp) : "（models 目录里还没有模型）";
    }

    /// 客户端连接日志（同一个来源 20 秒内只报一次，免得页面刷新就刷屏）
    private void LogClient(string info)
    {
        try
        {
            string key = info;
            int sp = info.IndexOf(' ');
            if (sp > 0) key = info.Substring(0, sp);
            lock (_clientSeen)
            {
                DateTime last;
                if (_clientSeen.TryGetValue(key, out last) &&
                    (DateTime.UtcNow - last).TotalSeconds < 20) return;
                _clientSeen[key] = DateTime.UtcNow;
            }
            Log("来自 " + info);
        }
        catch { }
    }

    /// 本机可用于局域网访问的 IPv4 地址（除 loopback）。
    ///
    /// 会排掉虚拟网卡（Hyper-V / VirtualBox / VMware / 蓝牙 / WAN Miniport）——
    /// 手机永远连不上那些地址，列出来只会误导。
    /// 真实网卡排前面：WiFi、有线、手机 USB 共享网络产生的网卡都算。
    // 网卡枚举有缓存：切主题会重建整个窗口，「详细信息」页每次都要取一遍，
    // 而 GetAllNetworkInterfaces() 在有些机器上要几百毫秒。
    private static List<string> _lanIpCache;
    private static DateTime _lanIpAt = DateTime.MinValue;

    private static List<string> AllLanIps()
    {
        if (_lanIpCache != null && (DateTime.UtcNow - _lanIpAt).TotalSeconds < 10)
            return _lanIpCache;

        var phys = new List<string>();
        var virt = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                string desc = ni.Description ?? "";
                bool isVirtual =
                    desc.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("TAP-Windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("WAN Miniport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    desc.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip)) continue;
                    string s = ip.ToString();
                    if (s.StartsWith("169.254.")) continue;      // 自动私有地址，连不通
                    if (isVirtual) { if (!virt.Contains(s)) virt.Add(s); }
                    else if (!phys.Contains(s)) phys.Add(s);
                }
            }
        }
        catch { }

        var all = new List<string>();
        all.AddRange(phys);
        if (all.Count == 0) all.AddRange(virt);      // 只剩虚拟网卡时也得给点什么
        _lanIpCache = all;
        _lanIpAt = DateTime.UtcNow;
        return all;
    }

    private Button MakeButton(string text, Color back, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, Width = S(132), Height = S(42), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, Margin = new Padding(0, 0, S(10), 0),
            Font = UiFont(11F, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Lighten(back, 0.12f);
        b.FlatAppearance.MouseDownBackColor = Lighten(back, -0.10f);
        b.Click += onClick;
        return b;
    }

    private static Color Lighten(Color c, float amount)
    {
        return Color.FromArgb(c.A, Clamp255(c.R + 255 * amount), Clamp255(c.G + 255 * amount), Clamp255(c.B + 255 * amount));
    }
    private static int Clamp255(double v) { int i = (int)Math.Round(v); return i < 0 ? 0 : (i > 255 ? 255 : i); }

    // 运行日志留一份副本。
    // 切主题会重建整个窗口，_txtLog 是新建的 —— 不存一份的话日志会被清空。
    private volatile bool _rebuilding;
    private readonly StringBuilder _logBuf = new StringBuilder();
    private readonly object _logGate = new object();

    /// 性能埋点：把耗时追加到 %TEMP%\lm-perf.log。
    /// **只有设了环境变量 LM_PERF_THEME 才会写**，正常使用不产生任何文件。
    private static bool _perfMode;

    private static void PerfLog(string msg)
    {
        if (!_perfMode) return;
        try
        {
            string p = Path.Combine(Path.GetTempPath(), "lm-perf.log");
            File.AppendAllText(p, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
        }
        catch { }
    }

    private void Log(string msg)
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n";
            lock (_logGate)
            {
                _logBuf.Append(line);
                if (_logBuf.Length > 200000)              // 顶多留 20 万字符，别无限涨
                    _logBuf.Remove(0, _logBuf.Length - 150000);
            }
            if (_txtLog == null || _txtLog.IsDisposed) return;
            if (_rebuilding) return;      // 重建期间日志框随时会被释放，别去碰它
            Action a = () => _txtLog.AppendText(line);
            if (_txtLog.InvokeRequired) _txtLog.BeginInvoke(a); else a();
        }
        catch { }
    }

    /// 窗口重建后把历史日志灌回新的日志框
    private void RestoreLog()
    {
        try
        {
            if (_txtLog == null || _txtLog.IsDisposed) return;
            string s;
            lock (_logGate) s = _logBuf.ToString();
            if (s.Length > 0) _txtLog.Text = s;
        }
        catch { }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase))
                    using (Stream s = asm.GetManifestResourceStream(n)) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    // ---------- 探活 ----------
    private static bool PortAlive(int port)
    {
        try
        {
            using (var c = new System.Net.Sockets.TcpClient())
            {
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                c.EndConnect(ar); return true;
            }
        }
        catch { return false; }
    }

    private static bool Probe(string url, bool acceptAuth)
    {
        try
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Timeout = 8000;
            using (var resp = (HttpWebResponse)r.GetResponse())
                return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
        }
        catch (WebException we)
        {
            if (acceptAuth && we.Response != null &&
                (int)((HttpWebResponse)we.Response).StatusCode == 401) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool WaitReady(string url, bool acceptAuth, int seconds)
    {
        return WaitReady(url, acceptAuth, seconds, null);
    }

    /// 等待就绪。
    /// ★ 修正：原实现只死等到超时，即使引擎进程 0.3 秒就崩了也要白等 150 秒。
    ///   现在每轮都检查进程是否已退出，退出就立即失败 —— 用户能马上看到真实原因。
    private static bool WaitReady(string url, bool acceptAuth, int seconds, Process watch)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (Probe(url, acceptAuth)) return true;
            if (watch != null)
            {
                try { if (watch.HasExited) return false; }
                catch { }
            }
            Thread.Sleep(600);
        }
        return false;
    }

    /// 读日志文件的所有行。
    ///
    /// ★ 必须用 FileShare.ReadWrite 打开：启动器自己正以 FileShare.ReadWrite
    ///   持有该文件，而 File.ReadAllLines 默认以 FileShare.Read 打开 ——
    ///   这个共享模式不允许已存在的写入端，会抛 IOException 导致读不到日志。
    private static string[] ReadLogLines(string path)
    {
        var list = new List<string>();
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null) list.Add(line);
            }
        }
        catch { }
        return list.ToArray();
    }

    /// 取日志文件最后一行非空内容（用于把引擎的真实报错回显给用户）
    private static string LastLogLine(string path)
    {
        try
        {
            string[] lines = ReadLogLines(path);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string s = lines[i].Trim();
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return "";
    }

    /// 等引擎就绪。
    ///
    /// 两点考虑：
    ///   1) 进程一退出立即返回失败 —— 不必白等满超时。
    ///   2) 超时给足（默认 900 秒）并每 20 秒报一次进度。
    ///
    /// 关于"慢"：**首次安装本来就慢**，因为引擎要给这块显卡标定推理路线，
    /// 这一步是一次性的，之后会走缓存。只有"装过多次还是这么慢"才该怀疑显卡性能。
    /// 所以下面第一次超时时把这两种可能都讲清楚，避免用户一上来就以为显卡不行。
    private bool WaitEngineReady(Process proc, int timeoutSec)
    {
        var until = DateTime.UtcNow.AddSeconds(timeoutSec);
        var nextTip = DateTime.UtcNow.AddSeconds(20);
        int waited = 0;
        while (DateTime.UtcNow < until)
        {
            if (Probe("http://127.0.0.1:" + EnginePort + "/v1/models", true)) return true;
            if (proc != null) { try { if (proc.HasExited) return false; } catch { } }
            if (DateTime.UtcNow >= nextTip)
            {
                waited += 20;
                nextTip = DateTime.UtcNow.AddSeconds(20);
                Log("  引擎仍在启动…（已等待 " + waited + " 秒）");
                if (waited == 20)
                {
                    // 只在第一次超时解释一次，后面每 20 秒只报进度，免得刷屏
                    Log("    · 如果是初次安装：引擎要先为这块显卡标定推理路线，"
                        + "这一步本来就慢，标定一次就缓存下来了，请耐心等");
                    Log("    · 如果已经装过多次、还是这么慢：那可能是显卡性能较差");
                }
            }
            Thread.Sleep(600);
        }
        return false;
    }

    /// 从引擎日志里解析"显存不足"的报错，取出「需要多少」和「实际可用多少」：
    ///
    ///   FATAL server failed during startup | requested Engine runtime reservation
    ///   requires 697904384 bytes, but only 302477312 bytes are available for runtime capacity
    ///
    /// 有了这两个数就能算出该把设备池降到多少才装得下。
    private static bool TryParseReservation(string logPath, out long need, out long avail)
    {
        need = 0; avail = 0;
        string[] lines = ReadLogLines(logPath);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            Match m = Regex.Match(lines[i],
                @"reservation requires (\d+) bytes, but only (\d+) bytes are available");
            if (!m.Success) continue;
            long.TryParse(m.Groups[1].Value, out need);
            long.TryParse(m.Groups[2].Value, out avail);
            return need > 0 && avail > 0;
        }
        return false;
    }

    /// 启动引擎并等它就绪；显存不足时**自动下调设备池重试**。
    ///
    /// 为什么要重试：设备池是安装时按「显卡总显存」定的档，
    /// 但真正能不能装下取决于**当下可用的显存** ——
    /// 用户开着浏览器、播放器、其它占显存的程序时，可用量会小很多。
    /// 与其直接失败让用户去猜，不如按引擎报出的实际可用量算一个装得下的值再试。
    private bool StartEngineWithRetry(string exePath, string exeName, string modelPath,
                                      string modelId, string engineLog)
    {
        var cfg = LaunchConfig.Load(Root);
        int kv = cfg.KvCapacity > 0 ? cfg.KvCapacity : 4032;
        int graph = cfg.GraphAllowanceMiB;

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            Log("启动引擎 " + exeName + "（设备池 " + kv + " token，图态预留 " + graph + " MiB）");

            var a = new List<string>();
            a.Add(modelPath);
            a.AddRange(EngineArgsFor(modelId, kv, graph));
            lock (_gate)
                _engine = StartHidden(exePath, a, EngineDir, engineLog, out _engineLogWriter);

            if (WaitEngineReady(_engine, 900)) return true;

            bool alive = false;
            try { alive = _engine != null && !_engine.HasExited; } catch { }
            if (alive)
            {
                Log("引擎进程仍在运行，但未在时限内就绪");
                return false;
            }

            long need, avail;
            if (!TryParseReservation(engineLog, out need, out avail))
            {
                Log("引擎已退出（不是显存原因，不做降档重试）");
                return false;
            }

            // 按实际可用量等比缩，留 10% 余量
            int newKv = (int)((long)kv * avail * 9 / 10 / need);
            if (newKv < 256) newKv = 256;
            if (newKv >= kv) return false;

            Log("显存不足：这次需要 " + (need / 1048576) + " MiB，只有 " + (avail / 1048576)
                + " MiB 可用 —— 设备池从 " + kv + " 降到 " + newKv + " 后重试");
            kv = newKv;
            if (graph > 0) { graph = 0; Log("图态预留同时降为 0 MiB"); }

            KillOwned();
            Thread.Sleep(1500);
        }
        Log("已尝试降低设备池仍无法启动 —— 请关闭占用显存的程序（浏览器、播放器、游戏）后重试");
        return false;
    }

    /// 快速探活（默认 1.5 秒超时），用于界面状态判断，不能卡住 UI 线程
    private static bool ProbeFast(string url, int timeoutMs)
    {
        try
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Timeout = timeoutMs;
            r.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)r.GetResponse())
                return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
        }
        catch (WebException we)
        {
            var rr = we.Response as HttpWebResponse;
            if (rr != null) { int c = (int)rr.StatusCode; return c == 401 || c == 400; }
            return false;
        }
        catch { return false; }
    }

    // 就绪状态缓存：界面刷新时不必每次都发请求
    private static bool _rdyCache;
    private static DateTime _rdyAt = DateTime.MinValue;

    /// 主动作废就绪缓存（载入/卸载后调用，保证状态立刻刷新）
    private static void InvalidateReady() { _rdyAt = DateTime.MinValue; }

    /// 引擎是否真的可用。
    ///
    /// 与「引擎就绪」用同一口径：**真的能回应 /v1/models 才算已载入**。
    /// 原来只看端口是否打开 —— 那样在"引擎已经死掉但网页服务还占着端口"
    /// 或"端口开了但还没就绪"时会误报已载入，和运行日志自相矛盾。
    ///
    /// ★ 超时和缓存都经过实测调整：
    ///   RefreshMenu() 会走这里，而切主题会调 RefreshMenu。
    ///   原来的「缓存 4 秒 + 超时 1500 毫秒」组合，一旦缓存过期又碰上
    ///   端口开着但不响应（引擎正在启动），界面就会**冻住整整 1.5 秒** ——
    ///   实测抓到过一次 RefreshMenu 耗时 1524 ms，正是这个原因。
    ///   本机回环探测正常 50ms 内必答，400ms 足够；缓存放宽到 20 秒，
    ///   反正载入/卸载都会显式作废缓存。
    private static bool ServerReady()
    {
        if ((DateTime.UtcNow - _rdyAt).TotalSeconds < 20) return _rdyCache;
        _rdyCache = ProbeFast("http://127.0.0.1:" + EnginePort + "/v1/models", 400);
        _rdyAt = DateTime.UtcNow;
        return _rdyCache;
    }

    /// 载入/卸载后立即失效缓存，让界面马上反映新状态。
    /// （和 InvalidateReady 是同一件事，留个旧名字给既有调用点用）
    private static void InvalidateReadyCache() { InvalidateReady(); }

    private bool IsLoaded() { return ServerReady(); }

    private void SyncState() { RefreshMenu(); }

    // ---------- 菜单可用性 ----------
    private void RefreshMenu()
    {
        if (InvokeRequiredSafe())
        {
            try { _tray.ContextMenuStrip.Invoke(new Action(RefreshMenu)); return; } catch { }
        }
        bool loaded = IsLoaded();
        _miLoad.Enabled = !_busy && !loaded;
        _miUnload.Enabled = !_busy && loaded;
        _miOpen.Enabled = !_busy && loaded;
        _miExit.Enabled = !_busy;
        _miStatus.Text = _busy ? "处理中…" : (loaded ? "已载入" : "未载入");
        _tray.Text = "Local Models · " + (loaded ? "已载入" : "未载入");

        if (_btnLoad != null && !_btnLoad.IsDisposed) _btnLoad.Enabled = !_busy && !loaded;
        if (_btnUnload != null && !_btnUnload.IsDisposed) _btnUnload.Enabled = !_busy && loaded;
        if (_btnOpen != null && !_btnOpen.IsDisposed) _btnOpen.Enabled = !_busy && loaded;
        if (_lblPill != null && !_lblPill.IsDisposed)
        {
            if (_busy) { _lblPill.Text = "处理中…"; _lblPill.ForeColor = C.Amber; _lblPill.BackColor = C.PillWarn; }
            else if (loaded) { _lblPill.Text = "已载入"; _lblPill.ForeColor = C.Green; _lblPill.BackColor = C.PillOk; }
            else { _lblPill.Text = "未载入"; _lblPill.ForeColor = C.Dim; _lblPill.BackColor = C.PillBg; }
        }
        if (_lblState != null && !_lblState.IsDisposed)
        {
            if (_busy) { _lblState.Text = "● 处理中…"; _lblState.ForeColor = C.Amber; }
            else if (loaded) { _lblState.Text = "● 模型已载入"; _lblState.ForeColor = C.Green; }
            else { _lblState.Text = "● 模型未载入"; _lblState.ForeColor = C.Dim; }
        }
    }

    private bool InvokeRequiredSafe()
    {
        try { return _tray.ContextMenuStrip != null && _tray.ContextMenuStrip.InvokeRequired; }
        catch { return false; }
    }

    // ---------- 载入 ----------
    private void LoadModel()
    {
        if (_busy) return;
        InvalidateReady();      // 立刻反映状态变化（下同）
        _busy = true; RefreshMenu();
        var t = new Thread(() =>
        {
            try
            {
                Log("开始载入模型…");
                Balloon(3000, "Local Models", "正在载入模型…", ToolTipIcon.Info);

                string engineLog = Path.Combine(DataDir, "engine.log");

                // 模型必须先存在，否则白启一次引擎
                string modelPath = ModelPath();
                if (modelPath.Length == 0)
                {
                    _busy = false;
                    Log("未找到模型文件，请把 .ninfer 放进 " + ModelDir);
                    Balloon(8000, "Local Models 载入失败",
                        "找不到模型文件。\n\n请把 .ninfer 模型放进：\n" + ModelDir, ToolTipIcon.Warning);
                    RefreshMenu(); return;
                }
                // 模型 ID 由文件名推导，同一份值既给引擎也给界面/网页
                string modelId = ModelIdFromFile(modelPath);

                if (!PortAlive(EnginePort))
                {
                    string exeName = EngineExeName;
                    string exePath = Path.Combine(EngineDir, exeName);
                    if (!File.Exists(exePath))
                    {
                        _busy = false;
                        Log("引擎缺失：" + exePath);
                        Balloon(6000, "Local Models 载入失败",
                            "引擎文件不存在：\n" + exePath, ToolTipIcon.Warning);
                        RefreshMenu(); return;
                    }
                    Log("模型文件：" + Path.GetFileName(modelPath));
                    Log("模型 ID：" + modelId);

                    // 引擎可能因显存不足起不来 —— 内部会自动下调设备池重试。
                    // 弱显卡（如 4060 Laptop，24 SM）标定路线慢，等待时间给足 15 分钟。
                    if (!StartEngineWithRetry(exePath, exeName, modelPath, modelId, engineLog))
                    {
                        _busy = false;
                        Log("引擎未就绪");
                        string last = LastLogLine(engineLog);
                        if (last.Length > 0) Log("引擎最后一行输出：" + last);
                        Balloon(9000, "Local Models 载入失败",
                            "引擎启动失败。\n\n详见日志：\n" + engineLog, ToolTipIcon.Warning);
                        RefreshMenu(); return;
                    }
                }
                else
                {
                    Log("引擎已在运行，跳过启动");
                    if (!WaitEngineReady(_engine, 900))
                    {
                        _busy = false;
                        Log("引擎未就绪");
                        string last = LastLogLine(engineLog);
                        if (last.Length > 0) Log("引擎最后一行输出：" + last);
                        RefreshMenu(); return;
                    }
                }

                Log("引擎就绪");
                InvalidateReadyCache();
                // 引擎报出的参数规模（文件头里没有），「模型 ID」的基座说明会用到
                RefreshEngineParams(ApiKey);

                // 取引擎自己识别出的模型名称，补进「详细信息」页。
                // 日志是异步重定向写入的，可能比"就绪"晚一点落盘，所以重试几次。
                string en = "";
                for (int k = 0; k < 8 && en.Length == 0; k++)
                {
                    en = ParseEngineModelName(engineLog);
                    if (en.Length == 0) Thread.Sleep(400);
                }
                if (en.Length > 0 && en != _engineModelName)
                {
                    _engineModelName = en;
                    Log("引擎识别模型：" + en);
                    RefreshInfoTab();
                }

                // 网页由内置 WebHost 提供，不再依赖外部的 node.exe / webui-server.js
                if (!PortAlive(WebPort))
                {
                    Log("启动内置网页服务");
                    lock (_gate)
                    {
                        _web = new WebHost(WebPort, EnginePort, FindWebDir());
                        // 告诉网页服务当前模型名，用于识别「思考别名」
                        // （<模型名>-think / -think-high …，方便第三方客户端控制思考）
                        _web.ModelId = modelId;
                        // 每次有设备连进来（或被白名单拒绝）都写一条日志 ——
                        // 手机连不上时靠它判断"请求有没有到达这台电脑"
                        _web.OnClient = delegate (string info) { LogClient(info); };
                        if (_web.Start())
                        {
                            Log("网页已启动（" + _web.Err + "）");
                            if (_web.FirewallNote.Length > 0) Log("防火墙：" + _web.FirewallNote);
                        }
                        else { Log("网页启动失败：" + _web.Err); _web = null; }
                    }
                }
                else Log("网页已在运行，跳过启动");

                bool webOk = WaitReady(WebUrl + "/", false, 60);
                Log(webOk ? "网页就绪 " + WebUrl : "网页未就绪");

                _busy = false;
                if (webOk)
                {
                    Log("载入成功");
                    Log("局域网访问：http://" + LanIp() + ":" + WebPort);
                    Balloon(4000, "Local Models 载入成功", "模型已就绪。点「打开模型」开始使用。", ToolTipIcon.Info);
                }
                else
                {
                    Log("载入失败：网页未就绪");
                    Balloon(5000, "Local Models 载入失败",
                        "网页服务未就绪。\n日志：" + engineLog, ToolTipIcon.Warning);
                }
                RefreshMenu();
            }
            catch (Exception ex)
            {
                _busy = false; Log("载入出错：" + ex.Message);
                Balloon(5000, "Local Models 载入出错", ex.Message, ToolTipIcon.Error);
                RefreshMenu();
            }
        });
        t.IsBackground = true; t.Start();
    }

    /// ★ 修 bug 版 StartHidden：把 StreamWriter 通过 out 参数交回调用方保存。
    ///   原版把它丢弃了，导致卸载后句柄不释放、再次载入报「文件正由另一进程使用」。
    private static Process StartHidden(string exe, IEnumerable<string> args, string workDir,
                                       string logFile, out StreamWriter writer)
    {
        var sb = new StringBuilder();
        foreach (string a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (a.IndexOf(' ') >= 0) sb.Append('"').Append(a).Append('"');
            else sb.Append(a);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = sb.ToString(),
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // KVMem 环形缓存参数 —— 从 Kvmem.json 读，可在设置里改（原来写死在代码里）。
        // 关掉 ring 就等于不使用 KVMem。
        if (Kvmem.Ring)
        {
            psi.EnvironmentVariables["NINFER_KV_WINDOW"] = Kvmem.Window.ToString();
            psi.EnvironmentVariables["NINFER_KV_RETRIEVE"] = Kvmem.Retrieve.ToString();
            psi.EnvironmentVariables["NINFER_KV_RING"] = "1";
        }
        psi.EnvironmentVariables["NINFER_HOST_PAGEABLE"] = "1";
        psi.EnvironmentVariables["NINFER_KV_REUSE_HOSTBACKED"] = "1";
        psi.EnvironmentVariables["NINFER_TERNARY_PTQ1_FAST"] = "1";

        // 日志文件用 FileShare.ReadWrite 打开：即使上一个句柄还没完全释放也能继续写，
        // 这是第二层保险（第一层是调用方主动 Dispose）。
        StreamWriter w = OpenLogShared(logFile);
        writer = w;

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (s, e) => { if (e.Data != null) TryW(w, e.Data); };
        p.ErrorDataReceived += (s, e) => { if (e.Data != null) TryW(w, e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// 以共享方式打开日志；被占用时先尝试占用者释放，再退到带时间戳的新文件。
    private static StreamWriter OpenLogShared(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (IOException)
            {
                Thread.Sleep(300);   // 句柄释放有延迟，等一下再试
            }
            catch
            {
                break;
            }
        }
        // 实在打不开就换一个文件名，绝不因为日志失败而让载入失败
        try
        {
            string alt = Path.Combine(Path.GetDirectoryName(path),
                Path.GetFileNameWithoutExtension(path) + "-" + DateTime.Now.ToString("HHmmss") + ".log");
            var fs2 = new FileStream(alt, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(fs2, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { return null; }
    }

    private static void TryW(StreamWriter w, string s)
    {
        if (w == null) return;
        try { w.WriteLine(s); } catch { }
    }

    // ---------- 卸载 ----------
    private void UnloadModel()
    {
        if (_busy) return;
        InvalidateReady();
        _busy = true; RefreshMenu();
        var t = new Thread(() =>
        {
            Log("开始卸载模型…");
            KillOwned();
            KillByPort(WebPort); KillByPort(EnginePort);

            // 等端口真的关掉，最多等 10 秒。
            // 只有确认关干净了才往下走 —— 否则会提前报"已卸载"，而实际上
            // 引擎可能还占着端口，界面和日志就会互相矛盾。
            bool closed = false;
            for (int i = 0; i < 40; i++)
            {
                if (!PortAlive(EnginePort) && !PortAlive(WebPort)) { closed = true; break; }
                Thread.Sleep(250);
            }

            if (!closed)
            {
                // 没关干净就别谎报成功，把实际情况说清楚
                _busy = false;
                InvalidateReady();
                Log("⚠ 卸载未完成：引擎端口 " + EnginePort + " 或网页端口 " + WebPort + " 仍被占用");
                Log("  可能有别的程序占着这两个端口，或引擎没响应结束信号。");
                RefreshMenu();
                return;
            }

            _busy = false;

            // ★ 必须**再作废一次**就绪缓存。
            //
            //   坑在哪：上面 _busy = true 那次 RefreshMenu() 会去探测端口 ——
            //   那时引擎还活着，探测结果 true 就被缓存住了（有效期 20 秒）。
            //   卸载过程要跑十几秒，等这里 RefreshMenu() 时缓存还没过期，
            //   于是界面继续显示"已载入"，日志却写着"已卸载" —— 自相矛盾。
            //
            //   所以端口确认关闭之后，作废缓存再报"已卸载"、再刷新界面。
            InvalidateReady();
            Log("已卸载");
            Balloon(2500, "Local Models", "模型已卸载，启动器仍在托盘运行。", ToolTipIcon.Info);
            RefreshMenu();
        });
        t.IsBackground = true; t.Start();
    }

    /// ★ 修 bug 的关键：先关日志句柄，再杀进程。
    ///   原版这里只 Dispose 了 _engine / _web，日志 writer 从来没关。
    private void KillOwned()
    {
        lock (_gate)
        {
            try { if (_web != null) _web.Stop(); } catch { }
            _web = null;

            try { if (_engine != null && !_engine.HasExited) _engine.Kill(); } catch { }
            try { if (_engine != null) _engine.Dispose(); } catch { }
            _engine = null;

            // 关闭日志写入器 —— 释放 engine.log 的句柄
            try { if (_engineLogWriter != null) { _engineLogWriter.Flush(); _engineLogWriter.Dispose(); } } catch { }
            _engineLogWriter = null;
        }
        // 给 Windows 一点时间回收句柄
        for (int i = 0; i < 20; i++)
        {
            if (CanWrite(Path.Combine(DataDir, "engine.log"))) break;
            Thread.Sleep(150);
        }
    }

    private static bool CanWrite(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                return true;
        }
        catch { return false; }
    }

    private static void KillByPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(4000);
                foreach (string raw in o.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (line.IndexOf(":" + port + " ", StringComparison.Ordinal) < 0) continue;
                    string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5) continue;
                    int pid;
                    if (!int.TryParse(parts[parts.Length - 1], out pid)) continue;
                    if (pid == Process.GetCurrentProcess().Id) continue;
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                }
            }
        }
        catch { }
    }

    private void OpenPage()
    {
        if (!IsLoaded())
        {
            Balloon(3000, "Local Models", "模型尚未载入，请先点「载入模型」。", ToolTipIcon.Warning);
            return;
        }
        try { Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Balloon(3000, "Local Models", "打不开浏览器：" + ex.Message, ToolTipIcon.Error); }
    }

    /// 打开窗口并直接定位到「设置 → 关于」。
    ///
    /// 之前托盘里那个「关于…」弹的是自己单独的对话框，内容跟设置页里那份
    /// 是两套，迟早会不一致。现在只留设置页那一份，这里直接跳过去。
    private void ShowSettingsPage(int navIndex)
    {
        ShowWindow(2);                       // 2 = 「设置」选项卡
        try { SelectSettingsPage(navIndex); } catch { }
        // 窗口刚创建时导航项可能还没排完，等消息循环跑起来再选一次
        try
        {
            if (_win != null && !_win.IsDisposed)
                _win.BeginInvoke(new Action(delegate
                {
                    try { SelectSettingsPage(navIndex); } catch { }
                }));
        }
        catch { }
    }

    private void ExitApp()
    {
        if (_busy) return;
        _busy = true; RefreshMenu();
        var t = new Thread(() =>
        {
            KillOwned();
            KillByPort(WebPort); KillByPort(EnginePort);
            for (int i = 0; i < 40 && (PortAlive(EnginePort) || PortAlive(WebPort)); i++) Thread.Sleep(250);
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            ExitThread();
        });
        t.IsBackground = true; t.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        }
        base.Dispose(disposing);
    }
}

internal static class Program
{
    private static Mutex _single;

    [STAThread]
    private static void Main()
    {
        bool createdNew;
        _single = new Mutex(true, "LocalModels_SingleInstance", out createdNew);
        if (!createdNew) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                BonsaiTray._dpiScale = Math.Max(1f, g.DpiX / 96f);
        }
        catch { BonsaiTray._dpiScale = 1f; }

        BonsaiTray.LoadTheme();
        Application.Run(new BonsaiTray());
        GC.KeepAlive(_single);
    }
}
