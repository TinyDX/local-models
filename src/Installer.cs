// Installer.cs -- 引擎包安装器（Local Models）
//
// 修复的核心 bug（原安装器只复制 payload\engine\，导致安装目录下没有启动器/webui/许可）：
//   · 现在按 payload 的**整棵目录树**释放到安装目录
//   · engine\ 下只保留本机架构匹配的那一个引擎 exe（其余两个跳过，省 3.1 GB 磁盘）
//   · 同时释放：启动器 exe、webui\、licenses\、README、教程
//   · 写 launch-config.json（本机探测结果）
//   · 可选建桌面快捷方式（指向安装目录里的启动器）
//
// 界面沿用原安装器风格：欢迎 → 许可 → 硬件 → 选项 → 安装 → 完成
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ---------------- 显卡信息 ----------------
internal sealed class GpuInfo
{
    public string Name = "";
    public string ComputeCap = "";
    public int VramMiB;
    public int DriverMajor;
    public string DriverRaw = "";
    public bool ProbeOk;
    public string Error = "";

    public string ArchKey
    {
        get
        {
            if (ComputeCap.Length == 0) return "";
            return ComputeCap;
        }
    }

    public string ArchLabel
    {
        get
        {
            switch (ArchKey)
            {
                case "8.6": return "RTX 30 系 (sm_86)";
                case "8.9": return "RTX 40 系 (sm_89)";
                case "12.0": return "RTX 50 系 (sm_120a)";
                default: return ComputeCap;
            }
        }
    }

    public bool ArchSupported
    {
        get { return ArchKey == "8.6" || ArchKey == "8.9" || ArchKey == "12.0"; }
    }

    public bool DriverOk { get { return DriverMajor >= 580; } }

    public string EngineExe
    {
        get
        {
            switch (ArchKey)
            {
                case "8.6": return "ninfer-serve-86.exe";
                case "8.9": return "ninfer-serve-89.exe";
                case "12.0": return "ninfer-serve-120a.exe";
                default: return "";
            }
        }
    }
}

// ---------------- 池规划 ----------------
internal sealed class KvPlan
{
    public bool Feasible = true;
    public string Note = "";
    public int KvCapacity = 4032;
    public int HostKvMiB = 8192;
    public int GraphAllowanceMiB = 64;
    public int FreeMiB;
}

internal static class Hardware
{
    public static GpuInfo Detect()
    {
        var g = new GpuInfo();
        string o = Run("nvidia-smi",
            "--query-gpu=name,compute_cap,memory.total,driver_version --format=csv,noheader");
        if (o.Trim().Length == 0)
        {
            g.ProbeOk = false;
            g.Error = "未检测到 NVIDIA 显卡（nvidia-smi 无输出）。";
            return g;
        }
        string line = o.Trim().Split('\n')[0];
        string[] p = line.Split(',');
        if (p.Length < 4)
        {
            g.ProbeOk = false;
            g.Error = "无法解析 nvidia-smi 输出：" + line;
            return g;
        }
        g.Name = p[0].Trim();
        g.ComputeCap = p[1].Trim();
        Match m = Regex.Match(p[2], @"(\d+)");
        if (m.Success) g.VramMiB = int.Parse(m.Groups[1].Value);
        g.DriverRaw = p[3].Trim();
        Match d = Regex.Match(g.DriverRaw, @"^(\d+)");
        if (d.Success) g.DriverMajor = int.Parse(d.Groups[1].Value);
        g.ProbeOk = true;
        return g;
    }

    public static KvPlan PlanKv(GpuInfo g)
    {
        var k = new KvPlan();
        if (g.VramMiB >= 13000)
        {
            k.KvCapacity = 17920; k.HostKvMiB = 16384; k.GraphAllowanceMiB = 0;
            k.Note = "大显存档：设备池充裕，CUDA 图全开。";
        }
        else if (g.VramMiB >= 10000)
        {
            k.KvCapacity = 8192; k.HostKvMiB = 8192; k.GraphAllowanceMiB = 128;
            k.Note = "中显存档。";
        }
        else
        {
            k.KvCapacity = 4032; k.HostKvMiB = 8192; k.GraphAllowanceMiB = 64;
            k.Note = "8 GB 档：实测配方（设备池取下限 4032，图态压到 64 MiB）。";
        }
        return k;
    }

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return o ?? "";
            }
        }
        catch { return ""; }
    }
}

// ---------------- 安装上下文 ----------------
internal sealed class InstallerContext
{
    public string TargetDir = "";
    public bool CreateShortcut = true;
    public bool CreateStartMenu = true;
    public GpuInfo Gpu;
    public KvPlan Plan;

    // ---- 引擎包（步骤 5）----
    /// 要下载的包名，如 infer-engine-sm120a-20261002.zip
    public string KitFile = "";
    /// 架构标签：sm86 / sm89 / sm120a
    public string KitArch = "";
    /// 用户选了「手动选择本地文件」
    public bool UseManualKit = false;
    /// 最终使用的本地包路径（下载完 / 手动选的）
    public string KitLocalPath = "";
    /// 版本号，用于拼 Release 下载地址
    public string Version = AppInfo.Version;
}

/// 一个最小的单行输入对话框。
/// WinForms 没有自带的 InputBox（那是 VB 的），自己搭一个。
internal static class Prompt
{
    public static string Show(string text, string title, string initial)
    {
        using (var f = new Form())
        {
            f.Text = title;
            f.ClientSize = new Size(460, 140);
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false;
            f.BackColor = Color.FromArgb(255, 32, 32, 32);
            f.ForeColor = Color.FromArgb(255, 243, 243, 243);
            f.Font = new Font("Microsoft YaHei UI", 10F);

            f.Controls.Add(new Label
            {
                Text = text, Left = 18, Top = 16, Width = 424, Height = 24,
                ForeColor = f.ForeColor, BackColor = f.BackColor
            });

            var tb = new TextBox
            {
                Text = initial ?? "", Left = 18, Top = 46, Width = 424, Height = 28,
                BackColor = Color.FromArgb(255, 45, 45, 45), ForeColor = f.ForeColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            f.Controls.Add(tb);

            var ok = new Button
            {
                Text = "确定", Left = 254, Top = 88, Width = 88, Height = 32,
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(79, 140, 255),
                ForeColor = Color.White, DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderSize = 0;
            var no = new Button
            {
                Text = "取消", Left = 354, Top = 88, Width = 88, Height = 32,
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = f.ForeColor, DialogResult = DialogResult.Cancel
            };
            no.FlatAppearance.BorderSize = 0;
            f.Controls.Add(ok); f.Controls.Add(no);
            f.AcceptButton = ok; f.CancelButton = no;

            return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }
}

// ---------------- 配色（沿用启动器那套，保持一致）----------------
internal sealed class Palette
{
    public bool Dark;
    public Color Bg, Header, Card, Line, Fg, Dim, Accent, Green, Amber, Red, Btn2, LogBg, LogFg;

    public static Palette For(bool dark)
    {
        if (dark) return new Palette
        {
            Dark = true,
            Bg = Color.FromArgb(255, 32, 32, 32),
            Header = Color.FromArgb(255, 41, 41, 41),
            Card = Color.FromArgb(255, 45, 45, 45),
            Line = Color.FromArgb(255, 62, 62, 62),
            Fg = Color.FromArgb(255, 243, 243, 243),
            Dim = Color.FromArgb(255, 160, 160, 160),
            Accent = Color.FromArgb(255, 76, 194, 255),
            Green = Color.FromArgb(255, 108, 203, 95),
            Amber = Color.FromArgb(255, 252, 225, 0),
            Red = Color.FromArgb(255, 255, 153, 164),
            Btn2 = Color.FromArgb(255, 58, 58, 58),
            LogBg = Color.FromArgb(255, 24, 24, 24),
            LogFg = Color.FromArgb(255, 150, 210, 175)
        };
        return new Palette
        {
            Dark = false,
            Bg = Color.FromArgb(255, 243, 243, 243),
            Header = Color.FromArgb(255, 251, 251, 251),
            Card = Color.FromArgb(255, 255, 255, 255),
            Line = Color.FromArgb(255, 225, 225, 225),
            Fg = Color.FromArgb(255, 27, 27, 27),
            Dim = Color.FromArgb(255, 96, 96, 96),
            Accent = Color.FromArgb(255, 0, 95, 184),
            Green = Color.FromArgb(255, 15, 123, 15),
            Amber = Color.FromArgb(255, 157, 93, 0),
            Red = Color.FromArgb(255, 196, 43, 28),
            Btn2 = Color.FromArgb(255, 238, 238, 238),
            LogBg = Color.FromArgb(255, 250, 250, 250),
            LogFg = Color.FromArgb(255, 20, 90, 60)
        };
    }
}

// ---------------- 安装器主窗体 ----------------
internal sealed class InstallerForm : Form
{
    private readonly InstallerContext Ctx = new InstallerContext();
    private Palette P = Palette.For(false);
    private readonly float Scale = 1f;

    private Label _status;
    private TextBox _log;
    private Button _next, _back, _cancel;
    private Panel _body;
    private int _step;
    private RadioButton _agree, _disagree;
    private TextBox _licText;
    private TextBox _dirBox;
    private CheckBox _shortcut, _startMenu;
    private Label _treeLabel;
    private bool _installing;
    private bool _closingDone;      // 完成页上点了「完成」/「完成并启动」→ 关窗口不再提示

    private static int S2(float v, float scale) { return (int)Math.Round(v * scale); }
    private int Sx(float v) { return S2(v, Scale); }

    private static Font FontOf(float sz, FontStyle st)
    {
        try { return new Font("Microsoft YaHei UI", sz, st, GraphicsUnit.Point); }
        catch { return new Font(FontFamily.GenericSansSerif, sz, st, GraphicsUnit.Point); }
    }

    public InstallerForm()
    {
        P = Palette.For(ThemeIsDark());
        try
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
            {
                // Scale 是 readonly，用属性替代
            }
        }
        catch { }

        // 标题栏和页头都带上版本号 —— 版本号唯一来源是 Version.cs
        Text = "Local Models 引擎包 · 安装程序 " + AppInfo.Version;
        ClientSize = new Size(Sx(760), Sx(560));
        MinimumSize = new Size(Sx(700), Sx(520));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = P.Bg;
        ForeColor = P.Fg;
        Font = FontOf(10f, FontStyle.Regular);
        Icon = LoadIco();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        // 默认装到 64 位的 Program Files。
        // 本程序是 64 位应用（进程经实测跑在 64 位，引擎是原生 x64 的
        // ninfer-serve-*.exe），按 Windows 惯例就该放 Program Files，
        // 而不是 Program Files (x86)。
        Ctx.TargetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Local Models");

        // 顶部标题
        var head = new Panel { Dock = DockStyle.Top, Height = Sx(86), BackColor = P.Header };
        head.Controls.Add(new Label
        {
            Text = "Local Models 引擎包  " + AppInfo.Version,
            Font = FontOf(16f, FontStyle.Bold), ForeColor = P.Fg, AutoSize = true,
            Location = new Point(Sx(24), Sx(16)), BackColor = P.Header
        });
        head.Controls.Add(new Label
        {
            Text = "NInfer 本地推理引擎 · 按显卡架构自动适配",
            Font = FontOf(9.5f, FontStyle.Regular), ForeColor = P.Dim, AutoSize = true,
            Location = new Point(Sx(26), Sx(50)), BackColor = P.Header
        });

        // 底部按钮栏
        // ★ 修正"下一步只显示一个边"：原来三个按钮用绝对坐标 + bar.Resize 里定位，
        //   而 Resize 在窗口首次显示时不一定触发，按钮就停在 (0,0) 互相重叠。
        //   改成右侧停靠的 FlowLayoutPanel —— 布局由容器负责，任何时候都正确。
        var bar = new Panel { Dock = DockStyle.Bottom, Height = Sx(66), BackColor = P.Header };
        var btnFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, Sx(15), Sx(24), 0),
            BackColor = P.Header
        };
        _cancel = MkBtn("取消", (s, e) => Close());
        _back = MkBtn("上一步", (s, e) => NavBack());
        _next = MkBtn("下一步", (s, e) => NavNext());
        // 顺序：从右往左 = 取消、上一步、下一步 → 显示为 [下一步][上一步][取消]
        btnFlow.Controls.Add(_cancel);
        btnFlow.Controls.Add(_back);
        btnFlow.Controls.Add(_next);
        bar.Controls.Add(btnFlow);

        _status = new Label
        {
            Dock = DockStyle.Bottom, Height = Sx(30), ForeColor = P.Dim,
            BackColor = P.Bg, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(Sx(24), 0, 0, 0), Text = "第 1 步 · 欢迎"
        };

        _body = new Panel { Dock = DockStyle.Fill, BackColor = P.Bg, AutoScroll = true };

        Controls.Add(_body);
        Controls.Add(_status);
        Controls.Add(bar);
        Controls.Add(head);

        Load += (s, e) =>
        {
            BuildWelcome();
            Theme.ApplyWin11(Handle, P.Dark);
        };
    }

    private static bool ThemeIsDark()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (k == null) return false;
                object v = k.GetValue("AppsUseLightTheme");
                return v == null || Convert.ToInt32(v) == 0;
            }
        }
        catch { return false; }
    }

    private static Icon LoadIco()
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

    private Button MkBtn(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(Sx(100), Sx(36)),
            FlatStyle = FlatStyle.Flat,
            BackColor = IsPrimary(text) ? P.Accent : P.Btn2,
            ForeColor = IsPrimary(text) ? Color.White : P.Fg,
            Font = FontOf(10f, FontStyle.Regular),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }

    /// 蓝色主按钮（下一步 / 安装 / 完成并启动），其余走灰色次要样式
    private static bool IsPrimary(string text)
    {
        return text == "下一步" || text == "安装" || text == "完成并启动";
    }

    private Label Head(string t)
    {
        return new Label
        {
            Text = t, Dock = DockStyle.Top, Height = Sx(38),
            Font = FontOf(13f, FontStyle.Bold), ForeColor = P.Fg,
            BackColor = P.Bg, Padding = new Padding(Sx(24), Sx(10), 0, 0)
        };
    }

    private Label Para(string t)
    {
        return new Label
        {
            Text = t, Dock = DockStyle.Top, Height = Sx(96), AutoSize = false,
            Font = FontOf(10f, FontStyle.Regular), ForeColor = P.Fg,
            BackColor = P.Bg, Padding = new Padding(Sx(24), Sx(8), Sx(24), 0)
        };
    }

    private void ClearBody()
    {
        _body.Controls.Clear();
    }

    private void Log(string s)
    {
        if (_log == null || _log.IsDisposed) return;
        Action a = () => _log.AppendText(s + "\r\n");
        if (_log.InvokeRequired) _log.BeginInvoke(a); else a();
    }

    // ---------- 步骤切换 ----------
    // 0 欢迎 → 1 许可 → 2 硬件 → 3 选项 → 4 选择安装包 → 5 安装中 → 6 完成
    private const int StepCount = 7;

    private void Go(int dir)
    {
        if (_installing) return;
        int next = _step + dir;
        if (next < 0 || next >= StepCount) return;
        _step = next;
        switch (_step)
        {
            case 0: BuildWelcome(); break;
            case 1: BuildLicense(); break;
            case 2: BuildHardware(); break;
            case 3: BuildOptions(); break;
            case 4: BuildPackagePick(); break;
            case 5: BuildInstall(); break;
            case 6: BuildDone(); break;
        }
    }

    /// 左侧按钮。
    /// 完成页上它是「完成」—— 直接关掉安装器（不是回到上一步）。
    private void NavBack()
    {
        if (_step == 6) { CloseInstaller(); return; }
        Go(-1);
    }

    /// 右侧蓝色按钮。
    /// 完成页上它是「完成并启动」—— 关掉安装器并拉起程序。
    private void NavNext()
    {
        if (_step == 6) { FinishAndLaunch(); return; }
        Go(1);
    }

    /// 完成页左边那个「完成」：只关窗口
    private void CloseInstaller()
    {
        _closingDone = true;
        try { Close(); } catch { }
    }

    /// 完成页右边那个「完成并启动」：先拉起程序，再关安装器
    private void FinishAndLaunch()
    {
        _closingDone = true;
        try
        {
            string exe = Path.Combine(Ctx.TargetDir, "Local Models.exe");
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Ctx.TargetDir,
                    UseShellExecute = true
                });
            else
                Process.Start(new ProcessStartInfo(Ctx.TargetDir) { UseShellExecute = true });
        }
        catch { }
        try { Close(); } catch { }
    }

    /// 步骤名（和 Go() 里的 case 一一对应）
    private static string StepName(int step)
    {
        switch (step)
        {
            case 0: return "欢迎";
            case 1: return "许可";
            case 2: return "硬件";
            case 3: return "选项";
            case 4: return "引擎包";
            case 5: return "安装中";
            default: return "完成";
        }
    }

    private void UpdateNav(int step, bool canNext = true, string nextText = "下一步")
    {
        // ★ 步数判断一律用 StepCount / LastStep，不要写死数字 ——
        //   上次加「引擎包」页时就是漏改了这里的 `step < 5`，
        //   结果第 5 步被判成"完成页"，点「下一步」直接跳去完成页。
        const int LastStep = 6;

        _status.Text = "第 " + (step + 1) + " 步 · " + StepName(step);
        _back.Enabled = step > 0 && step < LastStep;
        _next.Enabled = canNext && step < LastStep;
        _next.Text = nextText;
        // 完成页上左边已经是「完成」（就是关窗口），再放一个「关闭」是重复的，
        // 两个按钮干同一件事只会让人犹豫。所以完成页只留「完成」+「完成并启动」。
        _cancel.Visible = step < LastStep;
        _cancel.Text = "取消";
    }

    // ---------- 步骤 1 ----------
    private void BuildWelcome()
    {
        ClearBody();
        _body.Controls.Add(Para(
            "这个安装包把 NInfer 推理引擎装到你的电脑上，装完即可在本机离线运行大模型。\r\n\r\n" +
            "· 安装程序会探测你的显卡，只释放**匹配你显卡架构**的那一个引擎\r\n" +
            "（另外两档仍留在安装包里，不会占用你的磁盘）。\r\n\r\n" +
            "· 安装目录下会包含启动器、网页界面、引擎、许可与说明文件。\r\n\r\n" +
            "· 模型文件（.ninfer）需要你自己准备并放进安装目录的 models\\ 文件夹。"));
        _body.Controls.Add(Head("欢迎使用"));
        UpdateNav(0);
    }

    // ---------- 步骤 2 ----------
    private void BuildLicense()
    {
        ClearBody();
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Top, Height = Sx(300),
            BackColor = P.Card, ForeColor = P.Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = FontOf(9f, FontStyle.Regular), Text = LicenseText()
        };
        var wrap = new Panel { Dock = DockStyle.Top, Height = Sx(316), Padding = new Padding(Sx(24), Sx(6), Sx(24), 0), BackColor = P.Bg };
        wrap.Controls.Add(box);

        var rbWrap = new Panel { Dock = DockStyle.Top, Height = Sx(70), BackColor = P.Bg };
        _agree = new RadioButton
        {
            Text = "我接受以上条款", AutoSize = true, ForeColor = P.Fg, BackColor = P.Bg,
            Font = FontOf(10f, FontStyle.Regular), Location = new Point(Sx(26), Sx(10)),
            Checked = false
        };
        _disagree = new RadioButton
        {
            Text = "我不接受", AutoSize = true, ForeColor = P.Fg, BackColor = P.Bg,
            Font = FontOf(10f, FontStyle.Regular), Location = new Point(Sx(26), Sx(38)),
            Checked = true
        };
        _agree.CheckedChanged += (s, e) => _next.Enabled = _agree.Checked;
        rbWrap.Controls.Add(_agree);
        rbWrap.Controls.Add(_disagree);

        _body.Controls.Add(rbWrap);
        _body.Controls.Add(wrap);
        _body.Controls.Add(Head("许可与署名声明"));
        UpdateNav(1, _agree.Checked);
    }

    private static string LicenseText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("NInfer 推理引擎 —— 许可证与署名");
        sb.AppendLine();
        sb.AppendLine("【引擎本体】");
        sb.AppendLine("Apache License 2.0。");
        sb.AppendLine("上游项目：https://github.com/1314521gjy/ninfer-fusion-kvmem");
        sb.AppendLine();
        sb.AppendLine("【署名】");
        sb.AppendLine("本引擎为上游 NInfer 的下游衍生，包含以下项目的成果（均为 Apache-2.0）：");
        sb.AppendLine("  · Neroued/ninfer");
        sb.AppendLine("  · ashalliants/ninfer-3090");
        sb.AppendLine("  · iamwavecut/ninfer-3090");
        sb.AppendLine("  · tancau/ninfer-kvmem-ring");
        sb.AppendLine("  · kvmem/kvmem-qw3（作者 Di Chai）");
        sb.AppendLine();
        sb.AppendLine("【编入引擎的第三方】");
        sb.AppendLine("  · cpp-httplib / ggml-quants / llama-jinja / nlohmann json /");
        sb.AppendLine("    spdlog + {fmt} / utf8proc —— MIT");
        sb.AppendLine("  · xgrammar + dlpack —— Apache-2.0");
        sb.AppendLine("  · picojson —— BSD-2-Clause");
        sb.AppendLine("  · Qwen chat 模板 —— Apache-2.0（© Alibaba Cloud）");
        sb.AppendLine();
        sb.AppendLine("【随包运行库】");
        sb.AppendLine("  · NVIDIA CUDA —— NVIDIA EULA 可再分发条款");
        sb.AppendLine("  · FFmpeg —— GNU GPL v2 or later");
        sb.AppendLine("  · curl —— MIT 类许可证");
        sb.AppendLine();
        sb.AppendLine("完整许可证文本随包安装到安装目录的 licenses\\ 文件夹，请勿删除。");
        sb.AppendLine();
        sb.AppendLine("【免责】");
        sb.AppendLine("本安装包基于上游 Apache-2.0 项目打包，上游未对其背书。");
        return sb.ToString();
    }

    // ---------- 步骤 3 ----------
    private void BuildHardware()
    {
        ClearBody();
        Ctx.Gpu = Hardware.Detect();
        Ctx.Plan = Hardware.PlanKv(Ctx.Gpu);

        var sb = new StringBuilder();
        bool ok = true;
        string fail = "";

        if (!Ctx.Gpu.ProbeOk)
        {
            ok = false; fail = Ctx.Gpu.Error;
        }
        else
        {
            sb.AppendLine("显卡：" + Ctx.Gpu.Name);
            sb.AppendLine("架构：" + Ctx.Gpu.ArchLabel + (Ctx.Gpu.ArchSupported ? "" : "   ← 不受支持"));
            sb.AppendLine("显存：" + Ctx.Gpu.VramMiB + " MiB");
            sb.AppendLine("驱动：" + Ctx.Gpu.DriverRaw + (Ctx.Gpu.DriverOk ? "" : "   ← 需要 580 或更高"));
            sb.AppendLine();
            if (!Ctx.Gpu.ArchSupported)
            {
                ok = false;
                fail = "显卡架构 " + Ctx.Gpu.ComputeCap + " 不受支持。\r\n" +
                       "本包只提供 sm_86（RTX 30 系）、sm_89（RTX 40 系）、sm_120a（RTX 50 系）三档引擎。";
            }
            else if (!Ctx.Gpu.DriverOk)
            {
                ok = false;
                fail = "驱动版本过低（" + Ctx.Gpu.DriverRaw + "）。\r\n" +
                       "该引擎基于 CUDA 13.x 构建，需要驱动 580 或更高版本。";
            }
            else
            {
                sb.AppendLine("将安装：" + Ctx.Gpu.EngineExe);
                sb.AppendLine("设备池：" + Ctx.Plan.KvCapacity + " token");
                sb.AppendLine("主机池：" + Ctx.Plan.HostKvMiB + " MiB");
                sb.AppendLine("图态预留：" + Ctx.Plan.GraphAllowanceMiB + " MiB");
                sb.AppendLine();
                sb.AppendLine("（" + Ctx.Plan.Note + "）");
            }
        }

        var lbl = Para(ok ? sb.ToString() : "无法安装：\r\n\r\n" + fail);
        lbl.Height = Sx(250);
        lbl.ForeColor = ok ? P.Fg : P.Red;

        _body.Controls.Add(lbl);
        _body.Controls.Add(Head(ok ? "硬件检查 · 可以安装" : "硬件检查 · 无法安装"));
        UpdateNav(2, ok, ok ? "下一步" : "下一步");
        _next.Enabled = false;   // 硬件页由上面 UpdateNav 决定，这里显式设置
        _next.Enabled = ok;
        if (!ok)
        {
            _back.Enabled = true;
        }
    }

    // ---------- 步骤 4 ----------
    private void BuildOptions()
    {
        ClearBody();
        var lbl = Para("目标目录：安装程序会把引擎、启动器、网页界面、许可与说明文件全部释放到这里。");
        lbl.Height = Sx(50);

        _dirBox = new TextBox
        {
            Text = Ctx.TargetDir,
            Dock = DockStyle.Fill,
            // ★ AutoSize 必须关掉。
            //   单行 TextBox 默认 AutoSize=true，高度由字体决定（约 23px），
            //   Dock=Fill 也不会把它拉高 —— 结果就是输入框比「浏览…」按钮矮一截。
            //   关掉之后 Dock=Fill 才会真正填满整行，两者等高。
            AutoSize = false,
            BackColor = P.Card, ForeColor = P.Fg, BorderStyle = BorderStyle.FixedSingle,
            Font = FontOf(10f, FontStyle.Regular)
        };
        var browse = MkBtn("浏览…", (s, e) =>
        {
            using (var dlg = new FolderBrowserDialogEx())
            {
                dlg.Description = "选择安装目录";
                dlg.SelectedPath = Directory.Exists(_dirBox.Text) ? _dirBox.Text : Ctx.TargetDir;
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPath.Length > 0)
                    _dirBox.Text = InstallPaths.EnsureAppFolder(dlg.SelectedPath);   // 自动补上 \Local Models
            }
            UpdateTreePreview();
        });
        browse.Width = Sx(80);
        browse.Dock = DockStyle.Right;
        browse.Margin = new Padding(Sx(8), 0, 0, 0);

        // 一行 = 文本框（填充） + 浏览按钮（右侧）
        var dirRow = new Panel { Dock = DockStyle.Top, Height = Sx(34), BackColor = P.Bg, Padding = new Padding(Sx(24), 0, Sx(24), 0) };
        dirRow.Controls.Add(_dirBox);
        dirRow.Controls.Add(browse);

        var dirWrap = new Panel { Dock = DockStyle.Top, Height = Sx(44), Padding = new Padding(Sx(24), Sx(6), Sx(24), 0), BackColor = P.Bg };
        dirWrap.Controls.Add(dirRow);

        // 目录内容说明（随目录变化实时更新）
        var tree = Para("");
        tree.Height = Sx(200);
        _treeLabel = tree;

        // 两行选项：桌面快捷方式 + 开始界面显示
        _shortcut = new CheckBox
        {
            Text = "创建桌面快捷方式", Checked = true, AutoSize = true,
            Location = new Point(0, 0),
            ForeColor = P.Fg, BackColor = P.Bg, Font = FontOf(10f, FontStyle.Regular)
        };
        _startMenu = new CheckBox
        {
            Text = "在开始界面中显示", Checked = true, AutoSize = true,
            Location = new Point(0, Sx(28)),
            ForeColor = P.Fg, BackColor = P.Bg, Font = FontOf(10f, FontStyle.Regular)
        };
        var scWrap = new Panel
        {
            Dock = DockStyle.Top, Height = Sx(72),
            Padding = new Padding(Sx(24), Sx(8), 0, 0), BackColor = P.Bg
        };
        scWrap.Controls.Add(_shortcut);
        scWrap.Controls.Add(_startMenu);

        _body.Controls.Add(scWrap);
        _body.Controls.Add(tree);
        _body.Controls.Add(dirWrap);
        _body.Controls.Add(lbl);
        _body.Controls.Add(Head("安装选项"));
        UpdateTreePreview();
        // 选项页后面还有「引擎包」一页，所以这里是「下一步」而不是「安装」
        UpdateNav(3, true, "下一步");
    }

    /// 根据当前目录框内容刷新"安装后目录结构"预览
    private void UpdateTreePreview()
    {
        if (_treeLabel == null || _treeLabel.IsDisposed) return;
        string d = _dirBox != null ? _dirBox.Text.Trim() : Ctx.TargetDir;
        if (d.Length == 0) d = Ctx.TargetDir;
        _treeLabel.Text =
            "安装后目录结构：\r\n" +
            "  " + d + "\\\r\n" +
            "    ├─ Local Models.exe     启动器（双击即用）\r\n" +
            "    ├─ engine\\             引擎（只含本机匹配的那一档）\r\n" +
            "    ├─ models\\             放你的 .ninfer 模型\r\n" +
            "    ├─ webui\\              网页界面\r\n" +
            "    ├─ licenses\\           许可证与署名\r\n" +
            "    ├─ launch-config.json   本机适配配置\r\n" +
            "    └─ 说明与教程";
    }

    /// 文件夹选择框。
    /// .NET Framework 4.8 自带 FolderBrowserDialog 是老式树形对话框（不好用），
    /// 这里优先用 Vista+ 的 IFileDialog（有地址栏、可新建文件夹）；
    /// 若系统不支持则回退到自带对话框。
    private sealed class FolderBrowserDialogEx : IDisposable
    {
        public string Description = "";
        public string SelectedPath = "";
        private IWin32Window _owner;

        public DialogResult ShowDialog(IWin32Window owner)
        {
            _owner = owner;
            try
            {
                var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                // C# 5：out 变量必须先声明，不能内联
                uint opts;
                dlg.GetOptions(out opts);
                opts |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
                dlg.SetOptions(opts);
                if (Description.Length > 0) dlg.SetTitle(Description);
                IntPtr hwnd = owner != null ? owner.Handle : IntPtr.Zero;
                int hr = dlg.Show(hwnd);
                if (hr != 0) return DialogResult.Cancel;   // 用户取消
                IShellItem item;
                dlg.GetResult(out item);
                if (item == null) return DialogResult.Cancel;
                IntPtr psz;
                item.GetDisplayName(SIGDN_FILESYSPATH, out psz);
                SelectedPath = System.Runtime.InteropServices.Marshal.PtrToStringUni(psz);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(psz);
                return DialogResult.OK;
            }
            catch
            {
                // 回退：系统自带对话框
                using (var fb = new FolderBrowserDialog())
                {
                    fb.Description = Description;
                    fb.SelectedPath = SelectedPath;
                    DialogResult r = fb.ShowDialog(owner);
                    if (r == DialogResult.OK) SelectedPath = fb.SelectedPath;
                    return r;
                }
            }
        }

        public void Dispose() { }

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [System.Runtime.InteropServices.PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
            void GetFileName(out IntPtr pszName);
            void SetTitle([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }

    // ---------- 步骤 5：引擎包（识别显卡 → 自动/手动获取）----------
    //
    // 为什么单独一页：安装器本体只有 1.5 MB，引擎包 1.3~1.5 GB 单独下载。
    //   好处是用户只下自己显卡需要的那一个，而不是把三个架构（4.2 GB）全下来。
    private ComboBox _srcBox;
    private RadioButton _rbAuto, _rbManual;
    private TextBox _kitPath;
    private Button _kitBrowse;
    private ProgressBar _dlBar;
    private Label _dlInfo, _dlSpeed, _srcNote;
    private Button _dlStart;
    private ManualResetEvent _dlCancel;
    private bool _downloading;
    private string _kitLocal = "";
    private List<EngineKit.Source> _sources;

    private void BuildPackagePick()
    {
        ClearBody();

        string arch = Ctx.Gpu != null ? Ctx.Gpu.ArchKey : "";
        string tag = EngineKit.ArchTag(arch);

        if (tag.Length == 0)
        {
            var bad = Para("无法确定本机显卡架构，请回到上一步检查硬件信息。");
            bad.ForeColor = P.Red;
            _body.Controls.Add(bad);
            _body.Controls.Add(Head("引擎包 · 无法自动选择"));
            UpdateNav(4, true, "下一步");
            return;
        }

        string kitName = EngineKit.KitFileName(arch);
        Ctx.KitFile = kitName;
        Ctx.KitArch = tag;

        string sizeHint = (arch == "12.0") ? "1.26" : ((arch == "8.9") ? "1.44" : "1.46");
        var info = Para(
            "检测到显卡：" + Ctx.Gpu.Name + "\r\n" +
            "架构：" + EngineKit.ArchName(arch) + "\r\n" +
            "需要下载：" + kitName + "（约 " + sizeHint + " GB）\r\n" +
            "安装器只带程序本体，引擎按显卡单独下载 —— 不用下另外两个架构的 2.8 GB。");
        info.Height = Sx(96);
        info.Dock = DockStyle.None;
        info.Top = Sx(46);

        _body.Controls.Add(info);
        _body.Controls.Add(Head("引擎包 · " + EngineKit.ArchName(arch)));

        string searched;
        _kitLocal = EngineKit.FindLocalKit(arch, out searched);

        int y = Sx(150);
        _rbAuto = new RadioButton
        {
            Text = "自动下载", Left = Sx(24), Top = y, Width = Sx(140),
            Checked = (_kitLocal.Length == 0), Font = FontOf(10f, FontStyle.Regular),
            ForeColor = P.Fg, BackColor = P.Bg
        };
        _rbManual = new RadioButton
        {
            Text = "手动选择本地文件", Left = Sx(180), Top = y, Width = Sx(200),
            Checked = (_kitLocal.Length > 0), Font = FontOf(10f, FontStyle.Regular),
            ForeColor = P.Fg, BackColor = P.Bg
        };
        _rbAuto.CheckedChanged += delegate { SyncKitMode(); };
        _rbManual.CheckedChanged += delegate { SyncKitMode(); };
        _body.Controls.Add(_rbAuto);
        _body.Controls.Add(_rbManual);
        y += Sx(34);

        _body.Controls.Add(new Label
        {
            Text = "下载源", Left = Sx(24), Top = y, Width = Sx(70), Height = Sx(24),
            Font = FontOf(9.5f, FontStyle.Regular), ForeColor = P.Dim, BackColor = P.Bg
        });

        _sources = EngineKit.DefaultSources(Ctx.Version);
        _srcBox = new ComboBox
        {
            Left = Sx(96), Top = y - Sx(2), Width = Sx(280),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = FontOf(10f, FontStyle.Regular),
            BackColor = P.Card, ForeColor = P.Fg, FlatStyle = FlatStyle.Flat
        };
        foreach (var s in _sources) _srcBox.Items.Add(s.Name);
        _srcBox.SelectedIndex = 0;
        _srcBox.SelectedIndexChanged += delegate
        {
            if (_srcNote != null && _srcBox.SelectedIndex >= 0 && _srcBox.SelectedIndex < _sources.Count)
                _srcNote.Text = _sources[_srcBox.SelectedIndex].Note;
        };
        _body.Controls.Add(_srcBox);

        _srcNote = new Label
        {
            Text = _sources[0].Note, Left = Sx(386), Top = y, Width = Sx(220), Height = Sx(24),
            Font = FontOf(9f, FontStyle.Regular), ForeColor = P.Dim, BackColor = P.Bg
        };
        _body.Controls.Add(_srcNote);

        var btnProbe = MkBtn("测速", delegate { ProbeSources(); });
        btnProbe.Left = Sx(612); btnProbe.Top = y - Sx(6);
        btnProbe.Width = Sx(76);
        _body.Controls.Add(btnProbe);
        y += Sx(38);

        _kitPath = new TextBox
        {
            Text = _kitLocal, Left = Sx(24), Top = y, Width = Sx(560), Height = Sx(28),
            Font = FontOf(10f, FontStyle.Regular),
            BackColor = P.Card, ForeColor = P.Fg, BorderStyle = BorderStyle.FixedSingle
        };
        _body.Controls.Add(_kitPath);

        _kitBrowse = MkBtn("浏览…", delegate { PickKitFile(); });
        _kitBrowse.Left = Sx(592); _kitBrowse.Top = y - Sx(4);
        _kitBrowse.Width = Sx(96);
        _body.Controls.Add(_kitBrowse);
        y += Sx(40);

        _dlBar = new ProgressBar
        {
            Left = Sx(24), Top = y, Width = Sx(664), Height = Sx(20),
            Style = ProgressBarStyle.Continuous
        };
        _body.Controls.Add(_dlBar);
        y += Sx(26);

        _dlInfo = new Label
        {
            Text = "", Left = Sx(24), Top = y, Width = Sx(430), Height = Sx(24),
            Font = FontOf(9.5f, FontStyle.Regular), ForeColor = P.Dim, BackColor = P.Bg
        };
        _body.Controls.Add(_dlInfo);

        _dlSpeed = new Label
        {
            Text = "", Left = Sx(460), Top = y, Width = Sx(228), Height = Sx(24),
            Font = FontOf(9.5f, FontStyle.Regular), ForeColor = P.Accent, BackColor = P.Bg,
            TextAlign = ContentAlignment.MiddleRight
        };
        _body.Controls.Add(_dlSpeed);

        _dlStart = MkBtn("下载", delegate { StartDownload(); });
        _dlStart.Left = Sx(592); _dlStart.Top = y + Sx(30); _dlStart.Width = Sx(96);
        _body.Controls.Add(_dlStart);

        SyncKitMode();
        UpdateNav(4, true, "下一步");

        if (_kitLocal.Length > 0)
            _dlInfo.Text = "已在本机找到：" + Path.GetFileName(_kitLocal);
        else if (searched.Length > 0)
            _dlInfo.Text = "本机未找到引擎包（找过：" + searched.TrimEnd(' ', ';') + "）";
    }

    private void SyncKitMode()
    {
        bool manual = (_rbManual != null && _rbManual.Checked);
        if (_srcBox != null) _srcBox.Enabled = !manual;
        if (_kitPath != null) _kitPath.Enabled = manual;
        if (_kitBrowse != null) _kitBrowse.Enabled = manual;
        if (_dlBar != null) _dlBar.Enabled = !manual;
        if (_dlStart != null)
        {
            _dlStart.Enabled = !manual && !_downloading;
            _dlStart.Text = manual ? "已选文件" : (_downloading ? "取消" : "下载");
        }
        Ctx.UseManualKit = manual;
        if (manual && _kitPath != null)
            Ctx.KitLocalPath = _kitPath.Text.Trim();

        if (_dlInfo != null && manual && _kitPath != null && _kitPath.Text.Trim().Length > 0)
        {
            long b;
            if (EngineKit.LooksLikeKit(_kitPath.Text.Trim(), out b))
                _dlInfo.Text = "文件大小 " + EngineDownload.Human(b) + "，可以开始安装";
            else
                _dlInfo.Text = "这个文件看起来不对（应该是一个 1 GB 以上的 zip）";
        }
    }

    /// 测速：并行探测所有源，最快的排前面并自动选中。
    /// GitHub 官方源在国内经常几十 KB/s 甚至连不上，
    /// 与其让用户干等几分钟才发现，不如几秒钟先测出来。
    private void ProbeSources()
    {
        if (_sources == null || _srcBox == null) return;
        _srcBox.Enabled = false;
        _dlInfo.Text = "正在测速…";

        string file = Ctx.KitFile;
        var th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
        {
            try
            {
                var threads = new List<System.Threading.Thread>();
                foreach (var s in _sources)
                {
                    if (s.Prefix.Length == 0) continue;
                    var src = s;
                    var t = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
                    {
                        EngineKit.Probe(src, file);
                    }));
                    t.IsBackground = true;
                    t.Start();
                    threads.Add(t);
                }
                foreach (var t in threads) t.Join(5000);

                var ok = new List<EngineKit.Source>();
                var bad = new List<EngineKit.Source>();
                foreach (var s in _sources)
                {
                    if (s.Prefix.Length == 0) { bad.Add(s); continue; }
                    if (s.Ok) ok.Add(s); else bad.Add(s);
                }
                ok.Sort(delegate(EngineKit.Source a, EngineKit.Source b) { return a.Ms.CompareTo(b.Ms); });
                var sorted = new List<EngineKit.Source>();
                sorted.AddRange(ok); sorted.AddRange(bad);

                BeginInvoke(new Action(delegate
                {
                    _sources = sorted;
                    _srcBox.Items.Clear();
                    foreach (var s in _sources)
                    {
                        string label = s.Name;
                        if (s.Ok) label += "（" + s.Ms + " ms）";
                        else if (s.Prefix.Length > 0) label += "（不可用）";
                        _srcBox.Items.Add(label);
                    }
                    _srcBox.SelectedIndex = 0;
                    _srcBox.Enabled = true;

                    if (ok.Count > 0)
                    {
                        _dlInfo.Text = "测速完成，已自动选中最快的源：" + ok[0].Name;
                        _srcNote.Text = "延迟 " + ok[0].Ms + " ms";
                    }
                    else
                    {
                        _dlInfo.Text = "所有在线源都连不上。可以换「手动选择本地文件」，或选「自定义」自己填地址。";
                    }
                }));
            }
            catch { }
        }));
        th.IsBackground = true;
        th.Start();
    }

    private void PickKitFile()
    {
        using (var dlg = new OpenFileDialog())
        {
            dlg.Title = "选择引擎包（" + Ctx.KitFile + "）";
            dlg.Filter = "引擎包 (*.zip)|*.zip|所有文件 (*.*)|*.*";
            if (_kitLocal.Length > 0)
            {
                try
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(_kitLocal);
                    dlg.FileName = Path.GetFileName(_kitLocal);
                }
                catch { }
            }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _kitLocal = dlg.FileName;
            _kitPath.Text = _kitLocal;
            Ctx.KitLocalPath = _kitLocal;
            SyncKitMode();
        }
    }

    private void StartDownload()
    {
        if (_downloading)
        {
            if (_dlCancel != null) _dlCancel.Set();
            _dlInfo.Text = "正在取消…";
            return;
        }

        if (_srcBox.SelectedIndex < 0) return;
        var src = _sources[_srcBox.SelectedIndex];
        string url = src.UrlFor(Ctx.KitFile);

        if (src.Prefix.Length == 0)
        {
            string v = Prompt.Show("请输入引擎包的完整下载地址：", "自定义下载源",
                                   "https://example.com/" + Ctx.KitFile);
            if (string.IsNullOrEmpty(v)) return;
            url = v.Trim();
        }
        if (url.Length == 0) return;

        string destDir;
        try { destDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads"; }
        catch { destDir = Path.GetTempPath(); }
        string dest = Path.Combine(destDir, Ctx.KitFile);
        Ctx.KitLocalPath = dest;

        _downloading = true;
        _dlCancel = new ManualResetEvent(false);
        _dlStart.Text = "取消";
        _dlBar.Value = 0;
        _dlInfo.ForeColor = P.Dim;
        _dlInfo.Text = "正在下载：" + Path.GetFileName(dest);
        _dlSpeed.Text = "";

        var th = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
        {
            var r = EngineDownload.Download(url, dest,
                delegate(long done, long total, double mbps)
                {
                    BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            if (total > 0)
                            {
                                int pct = (int)(done * 100 / total);
                                if (pct > 100) pct = 100;
                                _dlBar.Value = pct;
                                _dlInfo.Text = "已下载 " + EngineDownload.Human(done) + " / "
                                             + EngineDownload.Human(total) + "  (" + pct + "%)";
                            }
                            else _dlInfo.Text = "已下载 " + EngineDownload.Human(done);
                            if (mbps > 0) _dlSpeed.Text = mbps.ToString("0.0") + " MB/s";
                        }
                        catch { }
                    }));
                }, _dlCancel);

            BeginInvoke(new Action(delegate
            {
                _downloading = false;
                _dlStart.Text = "下载";
                _dlStart.Enabled = true;
                _srcBox.Enabled = true;

                if (r.Ok)
                {
                    _kitLocal = dest;
                    Ctx.KitLocalPath = dest;
                    _dlBar.Value = 100;
                    _dlInfo.ForeColor = P.Fg;
                    _dlInfo.Text = "下载完成：" + Path.GetFileName(dest);
                    _dlSpeed.Text = EngineDownload.Human(r.Bytes);
                    Log("引擎包下载完成：" + dest);
                }
                else if (r.Cancelled)
                {
                    _dlInfo.Text = "已取消。已下载的部分保留着，再点「下载」会接着下。";
                }
                else
                {
                    _dlInfo.ForeColor = P.Red;
                    _dlInfo.Text = "下载失败：" + r.Error;
                    _dlSpeed.Text = "可换源或手动下载";
                    Log("引擎包下载失败：" + r.Error);
                    // 给出去哪儿手动下的提示 —— 全自动失败时用户至少知道下一步干什么
                    Log("  手动下载地址：" + EngineKit.KitReleaseUrl());
                    Log("  下好后用上面的「手动选择本地文件」指过去即可。");
                }
            }));
        }));
        th.IsBackground = true;
        th.Start();
    }

    // ---------- 步骤 6 ----------
    private void BuildInstall()
    {
        ClearBody();
        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = P.LogBg, ForeColor = P.LogFg,
            BorderStyle = BorderStyle.None, Font = FontOf(9f, FontStyle.Regular), WordWrap = false
        };
        var wrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Sx(24), Sx(6), Sx(24), Sx(6)), BackColor = P.Bg };
        wrap.Controls.Add(_log);
        _body.Controls.Add(wrap);
        _body.Controls.Add(Head("正在安装 · 释放程序本体并解开引擎包"));
        UpdateNav(5, false, "安装");
        _next.Enabled = false;
        _installing = true;
        StartInstall();
    }

    private void StartInstall()
    {
        var t = new Thread(() =>
        {
            bool ok = true;
            try
            {
                // 手输的路径也统一补上 \Local Models（已带后缀的不会重复加）
                Ctx.TargetDir = InstallPaths.EnsureAppFolder(_dirBox.Text);

                // ★ 硬校验：目标必须是**绝对路径**。
                //   相对路径会相对安装器的工作目录解析 —— 那样文件会落到
                //   程序解压目录旁边，而不是用户指定的位置。宁可报错也不装错地方。
                try { Ctx.TargetDir = Path.GetFullPath(Ctx.TargetDir); } catch { }
                if (Ctx.TargetDir.Length == 0 || !Path.IsPathRooted(Ctx.TargetDir))
                {
                    Log("❌ 安装路径无效：" + (_dirBox.Text ?? "(空)"));
                    Log("   必须是绝对路径，例如 D:\\Apps\\Local Models");
                    ok = false;
                    _installing = false;
                    BeginInvoke(new Action(() => MessageBox.Show(
                        "安装路径无效：\r\n" + (_dirBox.Text ?? "(空)") +
                        "\r\n\r\n请填一个绝对路径，例如 D:\\Apps",
                        "Local Models 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                    return;
                }

                Log("将安装到：" + Ctx.TargetDir);
                Ctx.CreateShortcut = _shortcut.Checked;
                Ctx.CreateStartMenu = _startMenu.Checked;
                ok = DoInstall();
            }
            catch (Exception e)
            {
                ok = false;
                Log("安装出错：" + e.Message);
            }
            _installing = false;
            BeginInvoke(new Action(() =>
            {
                if (ok) { _step = 6; BuildDone(); }
                else
                {
                    _status.Text = "第 6 步 · 安装失败";
                    _next.Enabled = false;
                    _back.Enabled = false;
                }
            }));
        });
        t.IsBackground = true;
        t.Start();
    }
    /// 安装：内嵌 payload（程序本体）+ 引擎包 zip（大块头）。
    ///
    /// 两种情况：
    ///   · 老的「一体化」包：exe 旁边有 payload\ 目录，里面连着引擎 —— 照旧走 Run
    ///   · 新的「分离」包：exe 旁边只有程序文件，引擎在用户下载的 zip 里 —— 走 RunWithKit
    ///
    /// 这样同一个安装器两种发布方式都能用，不用维护两份代码。
    private bool DoInstall()
    {
        string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
        string src = Path.Combine(exeDir, "payload");

        // 有引擎包就用分离模式；没有就退回老的整包模式
        bool hasKit = !string.IsNullOrEmpty(Ctx.KitLocalPath) && File.Exists(Ctx.KitLocalPath);
        if (hasKit)
            return InstallCore.RunWithKit(src, Ctx, new Action<string>(Log));
        return InstallCore.Run(src, Ctx, new Action<string>(Log));
    }

    // ---------- 步骤 6 ----------
    private void BuildDone()
    {
        ClearBody();
        var lbl = Para(
            "安装完成。\r\n\r\n" +
            "安装目录：" + Ctx.TargetDir + "\r\n\r\n" +
            "启动方式：双击桌面的 Local Models 快捷方式，或从开始菜单打开。\r\n\r\n" +
            "首次使用：把 .ninfer 模型文件放进安装目录的 models\\ 文件夹，" +
            "然后点启动器窗口里的「载入模型」。\r\n\r\n" +
            "卸载：可从「设置 → 应用」里找到 Local Models 点击卸载，" +
            "或运行安装目录里的 Uninstall.exe。");
        lbl.Height = Sx(260);

        var open = MkBtn("打开安装目录", (s, e) =>
        {
            try { Process.Start(new ProcessStartInfo(Ctx.TargetDir) { UseShellExecute = true }); }
            catch { }
        });
        open.Width = Sx(140);
        var openWrap = new Panel { Dock = DockStyle.Top, Height = Sx(50), Padding = new Padding(Sx(24), Sx(8), 0, 0), BackColor = P.Bg };
        openWrap.Controls.Add(open);

        _body.Controls.Add(openWrap);
        _body.Controls.Add(lbl);
        _body.Controls.Add(Head("安装完成"));
        _status.Text = "第 7 步 · " + StepName(6);

        // 完成页的按钮语义变了：
        //   左边  「上一步」→「完成」      （只关掉安装器）
        //   右边蓝「下一步」→「完成并启动」（关掉并拉起程序）
        //   最右的「关闭」隐藏 —— 它和「完成」做的事完全一样，留着是重复。
        _back.Text = "完成";
        _back.Enabled = true;
        _next.Text = "完成并启动";
        _next.Enabled = true;
        _cancel.Visible = false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing && !_closingDone)
        {
            var r = MessageBox.Show("安装正在进行，确定要退出吗？", "Local Models 安装程序",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
        }
        base.OnFormClosing(e);
    }
}

// ---------------- 路径处理 ----------------
/// 顶层静态类：界面模式与静默模式都要用，不能放在窗口类里面。
internal static class InstallPaths
{
    /// 用户选/填的目录，统一在末尾再带一层 Local Models。
    ///
    /// 选 `D:\Apps` → 实际装到 `D:\Apps\Local Models`；
    /// 已经以 `Local Models` 结尾的就不重复追加，
    /// 避免出现 `...\Local Models\Local Models`。
    ///
    /// ★ 坑：盘根必须单独处理。
    ///   `C:\` TrimEnd 之后是 `C:`，而 `Path.Combine("C:", "Local Models")`
    ///   得到的是 **`C:Local Models` —— 一条相对路径**（相对该盘当前目录）。
    ///   相对路径会相对安装器的当前工作目录解析（通常就是程序解压目录），
    ///   结果文件全落到安装器旁边了。
    public static string EnsureAppFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return dir;
        string t = dir.Trim();
        if (t.Length == 0) return dir;

        string trimmed = t.TrimEnd('\\', '/');
        string leaf = trimmed.Length > 0 ? System.IO.Path.GetFileName(trimmed) : "";

        // 盘根（C: / C:\）或纯分隔符：GetFileName 返回空，必须走拼接而不是 Combine
        if (leaf.Length == 0)
        {
            if (trimmed.Length == 0) return t;
            return trimmed + "\\Local Models";
        }

        if (string.Equals(leaf, "Local Models", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return System.IO.Path.Combine(trimmed, "Local Models");
    }
}

// ---------------- DWM 辅助 ----------------
internal static class Theme
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static int TrySet(IntPtr hwnd, int attr, int value)
    {
        try { int v = value; return DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int)); }
        catch { return -1; }
    }

    public static void ApplyWin11(IntPtr hwnd, bool dark)
    {
        TrySet(hwnd, 33, 2);
        TrySet(hwnd, 20, dark ? 1 : 0);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 静默安装：Bonsai2-Setup.exe /silent [/dir "目标目录"] [/kit "引擎包.zip"]
        // 用于批量部署与自动验证，不弹任何界面。
        //
        // /kit 指向已经下好的引擎包。新的分发方式里安装器只带程序本体，
        // 引擎要另外拿 —— 静默模式下没法弹下载界面，所以必须指定。
        bool silent = false;
        string target = "";
        string kitPath = "";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "/silent" || args[i] == "--silent") silent = true;
            else if (args[i] == "/dir" && i + 1 < args.Length) { target = args[i + 1]; i++; }
            else if (args[i] == "/kit" && i + 1 < args.Length) { kitPath = args[i + 1]; i++; }
        }

        if (silent)
        {
            var gpu = Hardware.Detect();
            if (!gpu.ProbeOk)
            {
                Console.WriteLine("无法安装：探测失败 - " + gpu.Error);
                Environment.ExitCode = 2;
                return;
            }
            if (!gpu.ArchSupported)
            {
                Console.WriteLine("无法安装：显卡架构 " + gpu.ComputeCap + " 不受支持");
                Environment.ExitCode = 3;
                return;
            }
            if (!gpu.DriverOk)
            {
                Console.WriteLine("无法安装：驱动版本过低（需 580+），当前 " + gpu.DriverRaw);
                Environment.ExitCode = 4;
                return;
            }

            var ctx = new InstallerContext();
            ctx.Gpu = gpu;
            ctx.Plan = Hardware.PlanKv(gpu);
            ctx.CreateShortcut = true;
            ctx.CreateStartMenu = true;
            ctx.TargetDir = target.Length > 0
                ? InstallPaths.EnsureAppFolder(target)
                : Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles), "Local Models");

            string exeDir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            string payload = Path.Combine(exeDir, "payload");

            // 指定了引擎包就走「分离」模式，否则退回老的整体 payload
            if (kitPath.Length > 0)
            {
                if (!File.Exists(kitPath))
                {
                    Console.WriteLine("找不到引擎包：" + kitPath);
                    Environment.ExitCode = 5;
                    return;
                }
                ctx.KitLocalPath = kitPath;
                ctx.KitArch = EngineKit.ArchTag(gpu.ArchKey);
                ctx.KitFile = EngineKit.KitFileName(gpu.ArchKey);
            }

            Action<string> log = new Action<string>(Console.WriteLine);
            bool ok = kitPath.Length > 0
                ? InstallCore.RunWithKit(payload, ctx, log)
                : InstallCore.Run(payload, ctx, log);
            Environment.ExitCode = ok ? 0 : 1;
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerForm());
    }
}
