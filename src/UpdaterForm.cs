// UpdaterForm.cs -- 轻量更新器的界面
//
// 流程：找安装目录 → 显示"旧版 → 新版" → 点更新 → 关程序 → 换文件 → 完成
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal sealed class UpdaterForm : Form
{
    private static readonly Color Bg      = Color.FromArgb(255, 15, 17, 23);
    private static readonly Color Panel2  = Color.FromArgb(255, 23, 26, 35);
    private static readonly Color Line    = Color.FromArgb(255, 42, 47, 61);
    private static readonly Color Fg      = Color.FromArgb(255, 230, 232, 238);
    private static readonly Color Dim     = Color.FromArgb(255, 152, 160, 179);
    private static readonly Color Accent  = Color.FromArgb(255, 79, 140, 255);
    private static readonly Color Ok      = Color.FromArgb(255, 56, 211, 159);
    private static readonly Color Warn    = Color.FromArgb(255, 255, 192, 97);
    private static readonly Color Err     = Color.FromArgb(255, 255, 107, 107);

    private readonly string _dir;
    private readonly string _oldVer;
    private readonly string _newVer;

    private Label _status;
    private Button _go, _close;
    private TextBox _box;
    private bool _busy;

    private float Sc = 1f;
    private int S(float v) { return (int)Math.Round(v * Sc); }

    public UpdaterForm(string dir, string oldVer, string newVer)
    {
        _dir = dir; _oldVer = oldVer; _newVer = newVer;

        Sc = 1f;
        try { using (var g = CreateGraphics()) Sc = g.DpiX / 96f; } catch { }

        Text = "Local Models 更新程序 " + newVer;
        ClientSize = new Size(S(600), S(400));
        MinimumSize = new Size(S(560), S(380));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 10F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Build();
    }

    private void Build()
    {
        // ---- 顶部 ----
        var head = new Panel { Dock = DockStyle.Top, Height = S(96), BackColor = Panel2 };
        head.Controls.Add(new Label
        {
            Text = "Local Models 更新程序",
            Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold),
            ForeColor = Fg, AutoSize = true, BackColor = Panel2,
            Location = new Point(S(26), S(18))
        });
        head.Controls.Add(new Label
        {
            Text = "只更新程序本身，不会动你的模型和设置",
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = Dim, AutoSize = true, BackColor = Panel2,
            Location = new Point(S(28), S(56))
        });
        Controls.Add(head);

        // ---- 底栏 ----
        var bar = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = Panel2 };
        _close = MkBtn("取消", false);
        _close.Click += delegate { if (!_busy) Close(); };
        _go = MkBtn("开始更新", true);
        _go.Click += delegate { DoUpdate(); };

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, S(14), S(24), 0), BackColor = Panel2
        };
        flow.Controls.Add(_close);     // 右起第一个
        flow.Controls.Add(_go);        // 右起第二个（更靠左）
        bar.Controls.Add(flow);

        _status = new Label
        {
            Dock = DockStyle.Bottom, Height = S(28), ForeColor = Dim, BackColor = Bg,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(S(24), 0, 0, 0),
            Text = "准备就绪"
        };
        Controls.Add(_status);
        Controls.Add(bar);

        // ---- 正文 ----
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(S(24), S(14), S(24), 0) };

        body.Controls.Add(new Label
        {
            Text = "安装目录",
            Font = new Font("Microsoft YaHei UI", 9.5F), ForeColor = Dim,
            AutoSize = true, Location = new Point(0, 0), BackColor = Bg
        });
        body.Controls.Add(new Label
        {
            Text = _dir,
            Font = new Font("Microsoft YaHei UI", 10F), ForeColor = Fg,
            AutoSize = false, Size = new Size(S(552), S(24)),
            Location = new Point(0, S(24)), BackColor = Bg
        });

        string verTxt;
        if (_oldVer.Length == 0)
            verTxt = "检测到的版本：无法识别（仍可更新）\r\n将更新到：" + _newVer;
        else if (!UpdaterCore.IsNewer(_newVer, _oldVer))
            verTxt = "已安装版本：" + _oldVer + "\r\n最新版本：" + _newVer + "\r\n\r\n已经是最新版，无需更新。";
        else
            verTxt = "已安装版本：" + _oldVer + "\r\n将更新到：" + _newVer;
        body.Controls.Add(new Label
        {
            Text = verTxt,
            Font = new Font("Microsoft YaHei UI", 10.5F), ForeColor = Fg,
            AutoSize = false, Size = new Size(S(552), S(76)),
            Location = new Point(0, S(58)), BackColor = Bg
        });

        _box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Location = new Point(0, S(142)), Size = new Size(S(552), S(140)),
            BackColor = Color.FromArgb(255, 17, 20, 27), ForeColor = Dim,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        body.Controls.Add(_box);

        Controls.Add(body);
        body.BringToFront();

        if (_oldVer.Length > 0 && !UpdaterCore.IsNewer(_newVer, _oldVer))
        {
            _go.Enabled = false;
            _go.Text = "已是最新";
            Log("无需更新。");
        }
    }

    private Button MkBtn(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, Size = new Size(S(110), S(36)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.FromArgb(255, 38, 43, 56),
            ForeColor = primary ? Color.White : Fg,
            Font = new Font("Microsoft YaHei UI", 10F),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private void Log(string s)
    {
        if (_box == null || _box.IsDisposed) return;
        Action a = delegate { _box.AppendText(s + "\r\n"); };
        if (_box.InvokeRequired) _box.BeginInvoke(a); else a();
    }

    private void SetStatus(string s, Color c)
    {
        if (_status == null || _status.IsDisposed) return;
        Action a = delegate { _status.Text = s; _status.ForeColor = c; };
        if (_status.InvokeRequired) _status.BeginInvoke(a); else a();
    }

    private void DoUpdate()
    {
        if (_busy) return;
        _busy = true;
        _go.Enabled = false; _close.Enabled = false;

        var t = new System.Threading.Thread(new System.Threading.ThreadStart(delegate
        {
            bool ok = true;
            try
            {
                SetStatus("正在关闭正在运行的程序…", Warn);
                Log("关闭安装目录里正在运行的程序…");
                UpdaterCore.KillRunning(_dir);

                // 全部文件先读进内存再写 —— 万一某个资源缺失，
                // 不会出现"替换了一半"的半残状态。
                var plan = new System.Collections.Generic.List<object[]>();
                foreach (var pair in UpdaterCore.Files)
                {
                    string rel = pair[0], res = pair[1];
                    byte[] data = UpdaterCore.Embedded(res);
                    if (data == null || data.Length == 0)
                    {
                        Log("✗ 更新包损坏：缺少 " + res);
                        ok = false; break;
                    }
                    plan.Add(new object[] { rel, data });
                }
                foreach (var pair in UpdaterCore.ExtraFiles)
                {
                    byte[] data = UpdaterCore.Embedded(pair[1]);
                    if (data != null && data.Length > 0)
                        plan.Add(new object[] { pair[0], data });
                }

                if (ok)
                {
                    SetStatus("正在替换文件…", Warn);
                    foreach (var item in plan)
                    {
                        string rel = (string)item[0];
                        byte[] data = (byte[])item[1];
                        string dst = Path.Combine(_dir, rel);
                        string dd = Path.GetDirectoryName(dst);
                        if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);

                        // 目标被占用时先改名，再写新的 —— 比直接覆盖更稳
                        try
                        {
                            if (File.Exists(dst)) File.Delete(dst);
                        }
                        catch
                        {
                            string bak = dst + ".old-" + DateTime.Now.ToString("HHmmss");
                            try { File.Move(dst, bak); } catch { }
                        }
                        File.WriteAllBytes(dst, data);
                        Log("✓ " + rel + "  (" + (data.Length / 1024) + " KB)");
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Log("✗ 出错：" + ex.Message);
            }

            bool done = ok;
            BeginInvoke(new Action(delegate
            {
                if (done)
                {
                    SetStatus("更新完成", Ok);
                    Log("");
                    Log("更新完成。你的模型、对话记录和设置都没有被改动。");
                    _go.Text = "完成";
                    _go.Enabled = true;
                    _close.Text = "启动程序";
                    _close.Enabled = true;
                    // 点「完成」就打开程序
                    _go.Click += delegate { Launch(); };
                }
                else
                {
                    SetStatus("更新失败", Err);
                    Log("");
                    Log("更新没有完成。程序文件可能被占用，或没有写入权限。");
                    Log("可以试试：关掉 Local Models 后重试，或以管理员身份运行本更新程序。");
                    _go.Enabled = true;
                    _go.Text = "重试";
                    _close.Enabled = true;
                }
                _busy = false;
            }));
        }));
        t.IsBackground = true;
        t.Start();
    }

    private void Launch()
    {
        try
        {
            string exe = Path.Combine(_dir, "Local Models.exe");
            if (File.Exists(exe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    WorkingDirectory = _dir, UseShellExecute = true
                });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_dir)
                { UseShellExecute = true });
        }
        catch { }
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            // Win11 圆角 + 深色标题栏（和主程序一致）
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
