// Uninstall.cs -- Local Models 卸载程序
//
// 设计要点：
//   1) 安装器把它释放到安装目录，并在「设置 → 应用」注册卸载项。
//   2) 卸载器**先把自己复制到 %TEMP% 再运行** —— 否则它正被安装目录占用，
//      没法把安装目录整个删掉。这是 Windows 卸载器的标准做法。
//   3) models\ 里是使用者自己的模型文件（可能好几 GB），
//      **默认不删**，需要显式勾选才会一起删除。
//   4) 清理范围：程序文件、引擎、网页、许可、桌面/开始菜单快捷方式、
//      注册表卸载项、%LOCALAPPDATA% 日志；并尽力回收网页端口的 URL 预留。
//
// 卸载动作集中在 Uninstaller.Run() 里，界面（UninstallForm）和静默模式
// （--quiet）都调它，避免两套逻辑分叉。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ---------------- 卸载动作 ----------------
internal static class Uninstaller
{
    /// 执行卸载；返回未能删除的条目数
    public static int Run(string dir, bool delModels, Action<string> log)
    {
        int fails = 0;

        log("=== 开始卸载 ===");
        log("安装目录：" + dir);
        log("");

        log("[1/7] 结束正在运行的进程");
        KillProcesses();

        log("[2/7] 删除快捷方式");
        DeleteShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Local Models.lnk", log);
        DeleteShortcut(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            "Local Models.lnk", log);
        try
        {
            string smDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Local Models");
            if (Directory.Exists(smDir))
            {
                Directory.Delete(smDir, true);
                log("      已删除开始菜单文件夹");
            }
        }
        catch (Exception e) { log("      ⚠ " + e.Message); }

        log("[3/7] 移除「设置 → 应用」中的条目");
        RemoveRegistry(log);

        log("[4/7] 删除程序文件" + (delModels ? "（含 models）" : "（保留 models）"));
        fails += DeleteInstallDir(dir, delModels, log);

        log("[5/7] 删除日志目录");
        try
        {
            string data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalModels");
            if (Directory.Exists(data)) { Directory.Delete(data, true); log("      已删除 " + data); }
            else log("      无需删除");
        }
        catch (Exception e) { log("      ⚠ " + e.Message); fails++; }

        log("[6/7] 回收网页端口预留");
        RemoveUrlAcl(log);

        log("");
        log("[7/7] 完成");
        if (fails > 0)
            log("⚠ 有 " + fails + " 处未能删除（可能仍被占用），可稍后手动清理");
        else
            log("✅ 卸载完成");

        return fails;
    }

    private static void KillProcesses()
    {
        string[] names = new string[]
        {
            "Local Models", "ninfer-serve-86", "ninfer-serve-89", "ninfer-serve-120a"
        };
        foreach (string n in names)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                }
            }
            catch { }
        }
        Thread.Sleep(800);
    }

    private static void DeleteShortcut(string folder, string name, Action<string> log)
    {
        try
        {
            if (string.IsNullOrEmpty(folder)) return;
            string p = Path.Combine(folder, name);
            if (File.Exists(p)) { File.Delete(p); log("      已删除 " + p); }
        }
        catch (Exception e) { log("      ⚠ " + e.Message); }
    }

    private static void RemoveRegistry(Action<string> log)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
            {
                if (k == null) return;
                if (k.OpenSubKey("Local Models") != null)
                {
                    k.DeleteSubKeyTree("Local Models", false);
                    log("      已移除卸载项");
                }
                else log("      无需移除");
            }
        }
        catch (Exception e) { log("      ⚠ " + e.Message); }
    }

    private static int DeleteInstallDir(string dir, bool delModels, Action<string> log)
    {
        int fails = 0;
        if (!Directory.Exists(dir)) { log("      目录不存在，跳过"); return 0; }

        if (!delModels)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    if (Path.GetFileName(sub).Equals("models", StringComparison.OrdinalIgnoreCase))
                    {
                        log("      保留 " + sub);
                        continue;
                    }
                    if (!TryDeleteDir(sub, log)) fails++;
                }
            }
            catch (Exception e) { log("      ⚠ " + e.Message); fails++; }

            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    if (!TryDeleteFile(f, log)) fails++;
            }
            catch (Exception e) { log("      ⚠ " + e.Message); fails++; }
        }
        else
        {
            if (!TryDeleteDir(dir, log)) fails++;
        }
        return fails;
    }

    private static bool TryDeleteDir(string path, Action<string> log)
    {
        try
        {
            Directory.Delete(path, true);
            log("      已删除 " + path);
            return true;
        }
        catch (Exception e)
        {
            log("      ⚠ 无法删除 " + path + "  (" + e.Message + ")");
            return false;
        }
    }

    private static bool TryDeleteFile(string path, Action<string> log)
    {
        try
        {
            File.Delete(path);
            log("      已删除 " + Path.GetFileName(path));
            return true;
        }
        catch (Exception e)
        {
            log("      ⚠ 无法删除 " + Path.GetFileName(path) + "  (" + e.Message + ")");
            return false;
        }
    }

    /// 尽力回收 URL 预留（需要管理员；失败不影响卸载）
    private static void RemoveUrlAcl(Action<string> log)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "http delete urlacl url=http://+:8096/")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(15000);
                log("      已回收");
            }
        }
        catch { log("      跳过（未提权或本就没有预留）"); }
    }
}

// ---------------- 卸载界面 ----------------
internal sealed class UninstallForm : Form
{
    private readonly string _dir;
    private Palette P = Palette.For(false);
    private CheckBox _chkModels;
    private TextBox _log;
    private Button _btnOk, _btnCancel;
    private Label _status;
    private bool _working;
    private bool _done;

    private static int S2(float v) { return (int)Math.Round(v); }

    private static Font FontOf(float sz, FontStyle st)
    {
        try { return new Font("Microsoft YaHei UI", sz, st, GraphicsUnit.Point); }
        catch { return new Font(FontFamily.GenericSansSerif, sz, st, GraphicsUnit.Point); }
    }

    public UninstallForm(string dir)
    {
        _dir = dir;
        P = Palette.For(ThemeIsDark());

        Text = "Local Models 卸载程序 " + AppInfo.Version;
        ClientSize = new Size(S2(680), S2(500));
        MinimumSize = new Size(S2(640), S2(460));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = P.Bg;
        ForeColor = P.Fg;
        Font = FontOf(10f, FontStyle.Regular);
        Icon = LoadIco();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var head = new Panel { Dock = DockStyle.Top, Height = S2(78), BackColor = P.Header };
        head.Controls.Add(new Label
        {
            Text = "卸载 Local Models",
            Font = FontOf(15f, FontStyle.Bold), ForeColor = P.Fg, AutoSize = true,
            Location = new Point(S2(22), S2(14)), BackColor = P.Header
        });
        head.Controls.Add(new Label
        {
            Text = "安装目录：" + _dir,
            Font = FontOf(9f, FontStyle.Regular), ForeColor = P.Dim, AutoSize = true,
            Location = new Point(S2(24), S2(46)), BackColor = P.Header
        });

        var bar = new Panel { Dock = DockStyle.Bottom, Height = S2(62), BackColor = P.Header };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, S2(14), S2(22), 0), BackColor = P.Header
        };
        _btnCancel = MkBtn("取消", P.Btn2, (s, e) => { if (!_working) Close(); });
        _btnOk = MkBtn("卸载", Color.FromArgb(197, 15, 31), (s, e) => StartWork());
        flow.Controls.Add(_btnCancel);
        flow.Controls.Add(_btnOk);
        bar.Controls.Add(flow);

        _status = new Label
        {
            Dock = DockStyle.Bottom, Height = S2(28), ForeColor = P.Dim, BackColor = P.Bg,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S2(22), 0, 0, 0),
            Text = "点击「卸载」开始。"
        };

        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BackColor = P.LogBg, ForeColor = P.LogFg,
            BorderStyle = BorderStyle.None, Font = FontOf(9f, FontStyle.Regular), WordWrap = false
        };
        var logWrap = new Panel
        {
            Dock = DockStyle.Fill, Padding = new Padding(S2(22), S2(6), S2(22), S2(6)),
            BackColor = P.Bg
        };
        logWrap.Controls.Add(_log);

        _chkModels = new CheckBox
        {
            Text = "同时删除 models 文件夹（里面是你自己的模型文件，删掉后需要重新准备）",
            Checked = false, AutoSize = true, ForeColor = P.Fg, BackColor = P.Bg,
            Font = FontOf(9.5f, FontStyle.Regular)
        };
        var chkWrap = new Panel
        {
            Dock = DockStyle.Bottom, Height = S2(38),
            Padding = new Padding(S2(22), S2(8), 0, 0), BackColor = P.Bg
        };
        chkWrap.Controls.Add(_chkModels);

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = S2(34), ForeColor = P.Dim, BackColor = P.Bg,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S2(22), 0, 0, 0),
            Font = FontOf(9f, FontStyle.Regular),
            Text = "将删除：程序文件 · 引擎 · 网页 · 许可 · 桌面与开始菜单快捷方式 · 应用列表条目"
        };

        Controls.Add(logWrap);
        Controls.Add(hint);
        Controls.Add(chkWrap);
        Controls.Add(_status);
        Controls.Add(bar);
        Controls.Add(head);

        Load += (s, e) => Theme.ApplyWin11(Handle, P.Dark, false);
    }

    private static bool ThemeIsDark()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(
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

    private Button MkBtn(string text, Color back, EventHandler h)
    {
        var b = new Button
        {
            Text = text, Size = new Size(S2(96), S2(34)), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, Font = FontOf(10f, FontStyle.Regular),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            Margin = new Padding(S2(8), 0, 0, 0)
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += h;
        return b;
    }

    private void Log(string s)
    {
        if (_log == null || _log.IsDisposed) return;
        Action a = () => _log.AppendText(s + "\r\n");
        if (_log.InvokeRequired) _log.BeginInvoke(a); else a();
    }

    private void StartWork()
    {
        if (_working) return;
        _working = true;
        _btnOk.Enabled = false;
        _chkModels.Enabled = false;
        _status.Text = "正在卸载…";
        bool delModels = _chkModels.Checked;

        var t = new Thread(() =>
        {
            int fails = 0;
            try { fails = Uninstaller.Run(_dir, delModels, Log); }
            catch (Exception e) { Log("卸载出错：" + e.Message); fails = -1; }
            _working = false;
            _done = true;
            BeginInvoke(new Action(() =>
            {
                _status.Text = fails == 0 ? "卸载完成。" : "卸载完成（有 " + fails + " 处未删除）。";
                _btnOk.Enabled = false;
                _btnOk.Text = "已完成";
                _btnCancel.Text = "关闭";
            }));
        });
        t.IsBackground = true;
        t.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working)
        {
            var r = MessageBox.Show("卸载正在进行，确定要退出吗？", "Local Models 卸载程序",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
        }
        base.OnFormClosing(e);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool quiet = false;
        string dir = "";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--run" && i + 1 < args.Length) { dir = args[i + 1]; i++; }
            else if (args[i] == "--quiet" || args[i] == "/quiet") quiet = true;
        }

        bool bootstrap = dir.Length == 0;

        if (bootstrap)
        {
            string self = Application.ExecutablePath;
            string selfDir = Path.GetDirectoryName(self);
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "LocalModels-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(self, tmp, true);
                var psi = new ProcessStartInfo(tmp,
                    "--run \"" + selfDir + "\"" + (quiet ? " --quiet" : ""))
                {
                    UseShellExecute = false
                };
                Process.Start(psi);
                return;
            }
            catch
            {
                // 复制失败就原地运行（保留 models 时仍能删掉其余文件）
                dir = selfDir;
            }
        }

        if (quiet)
        {
            // 静默卸载：不弹界面，保留 models
            Console.WriteLine("Local Models 静默卸载：" + dir);
            int fails = Uninstaller.Run(dir, false, delegate(string s) { Console.WriteLine(s); });
            Environment.ExitCode = fails;
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new UninstallForm(dir));
    }
}
