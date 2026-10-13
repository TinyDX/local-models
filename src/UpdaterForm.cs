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

    // ---- 引擎更新 ----
    private CheckBox _updateEngine;          // 「同时更新引擎」
    private string _engineArch = "";         // 本机架构（空 = 没装引擎，不更新）
    private System.Threading.ManualResetEvent _engineCancel;

    private float Sc = 1f;
    private int S(float v) { return (int)Math.Round(v * Sc); }

    public UpdaterForm(string dir, string oldVer, string newVer)
    {
        _dir = dir; _oldVer = oldVer; _newVer = newVer;

        Sc = 1f;
        try { using (var g = CreateGraphics()) Sc = g.DpiX / 96f; } catch { }

        // 看安装目录里装了哪个架构的引擎 —— 决定要不要更新引擎
        _engineArch = DetectInstalledArch();

        Text = "Local Models 更新程序 " + newVer;
        ClientSize = new Size(S(600), S(450));
        MinimumSize = new Size(S(560), S(430));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 10F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Build();
    }

    /// 看安装目录的 engine\ 里是哪个架构的引擎。
    /// 没有引擎就返回空串 —— 那种情况更新器不管引擎。
    ///
    /// ★ 不用 Directory.GetFiles(eng, "ninfer-serve-*.exe")：
    ///   Windows 的搜索模式在通配符 + 连字符组合下行为不稳。
    ///   直接列全部 .exe 再自己判断，最稳。
    private string DetectInstalledArch()
    {
        try
        {
            string eng = Path.Combine(_dir, "engine");
            if (!Directory.Exists(eng))
            {
                _engineProbe = "engine 目录不存在：" + eng;
                return "";
            }

            string[] all = Directory.GetFiles(eng, "*.exe");
            if (all.Length == 0)
            {
                _engineProbe = "engine 目录里没有 .exe 文件";
                return "";
            }

            foreach (string f in all)
            {
                string name = Path.GetFileName(f);
                if (name.IndexOf("ninfer-serve", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string a = EngineKit.ArchFromEngineExe(name);
                if (a.Length > 0)
                {
                    _engineProbe = "检测到 " + name;
                    return a;
                }
            }

            _engineProbe = "engine 里有 exe 但认不出架构";
            return "";
        }
        catch (Exception ex)
        {
            _engineProbe = "检测出错：" + ex.Message;
            return "";
        }
    }

    /// 引擎检测的说明，显示在界面上方便排查
    private string _engineProbe = "";

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
            // 程序版本一样，但引擎可能是旧的 —— 下面的勾选框会说明
            verTxt = "已安装版本：" + _oldVer + "\r\n最新版本：" + _newVer;
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
            Location = new Point(0, S(142)), Size = new Size(S(552), S(130)),
            BackColor = Color.FromArgb(255, 17, 20, 27), ForeColor = Dim,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        body.Controls.Add(_box);

        // ---- 「同时更新引擎」勾选框 ----
        // 只在检测到已装引擎时显示 —— 没装引擎的用户不需要这个选项
        if (_engineArch.Length > 0)
        {
            string have = UpdaterCore.InstalledEngineVersion(_dir);
            bool needUpdate = (have != EngineKit.KitDate);

            _updateEngine = new CheckBox
            {
                Text = needUpdate
                    ? "同时更新推理引擎（" + EngineKit.ArchName(_engineArch)
                      + "，约 " + (EngineKit.KitDate == "20261013" ? "834" : "800") + " MB）"
                    : "推理引擎已是最新（" + have + "）",
                Left = 0, Top = S(280), Width = S(552), Height = S(26),
                Checked = needUpdate,
                Enabled = needUpdate,
                ForeColor = needUpdate ? Fg : Dim,
                BackColor = Bg,
                Font = new Font("Microsoft YaHei UI", 10F)
            };
            body.Controls.Add(_updateEngine);

            if (needUpdate)
            {
                body.Controls.Add(new Label
                {
                    Text = "已装引擎：" + (have.Length > 0 ? have : "未知（旧版装的）")
                         + "　→　新引擎：" + EngineKit.KitDate
                         + (_engineProbe.Length > 0 ? "　（" + _engineProbe + "）" : ""),
                    Left = 0, Top = S(306), Width = S(552), Height = S(22),
                    ForeColor = Dim, BackColor = Bg,
                    Font = new Font("Microsoft YaHei UI", 9F)
                });
            }
        }

        Controls.Add(body);
        body.BringToFront();

        // ---- 判断要不要更新 ----
        //
        // ★ 不能只看程序版本：
        //   程序版本可能已经是最新，但**引擎还是旧的**
        //   （比如上次只更新了程序、没勾引擎；或者引擎单独发过新版）。
        //   所以只要「程序要更新」**或**「引擎要更新」，就允许点开始。
        bool progNewer = (_oldVer.Length == 0) || UpdaterCore.IsNewer(_newVer, _oldVer);
        bool engineNewer = (_engineArch.Length > 0)
                        && (UpdaterCore.InstalledEngineVersion(_dir) != EngineKit.KitDate);

        if (!progNewer && !engineNewer)
        {
            _go.Enabled = false;
            _go.Text = "已是最新";
            Log("程序与引擎均已是最新，无需更新。");
        }
        else if (!progNewer && engineNewer)
        {
            // 程序不用换，只更新引擎
            _go.Text = "更新引擎";
            Log("程序已是最新（" + _newVer + "），但引擎还是旧的 —— 点「更新引擎」单独更新。");
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

                // ---- 引擎更新 ----
                // 程序换完后，看引擎要不要跟着换。
                // 引擎是大文件（800 MB~1 GB），单独下载、单独解压。
                if (ok && _engineArch.Length > 0)
                {
                    try
                    {
                        ok = UpdateEngine();
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Log("✗ 引擎更新失败：" + ex.Message);
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

    /// 更新引擎：下载新版引擎包 → 解压覆盖安装目录的 engine\。
    ///
    /// 只在「已装引擎」且「版本标记对不上」时做。
    /// 返回 false 表示失败（程序部分还是更新成功了，只是引擎没换成）。
    private bool UpdateEngine()
    {
        // 已经是最新的就不用下 800 MB
        string have = UpdaterCore.InstalledEngineVersion(_dir);
        if (have == EngineKit.KitDate)
        {
            Log("");
            Log("引擎已是最新（" + have + "），跳过。");
            return true;
        }

        Log("");
        Log("── 更新引擎 ──");
        Log("已装引擎版本：" + (have.Length > 0 ? have : "未知（旧版装的）"));
        Log("新引擎版本：  " + EngineKit.KitDate);
        Log("架构：" + EngineKit.ArchName(_engineArch));
        Log("");

        if (!_updateEngine.Checked)
        {
            Log("（你取消了「同时更新引擎」，只更新了程序）");
            return true;
        }

        string file = EngineKit.KitFileName(_engineArch);
        string url = "";
        var sources = EngineKit.DefaultSources(AppInfo.Version);
        if (sources.Count == 0) { Log("✗ 没有可用的下载源"); return false; }
        url = sources[0].UrlFor(file);

        Log("下载：" + url);

        // 下到临时目录 —— 解压完就删，不占用户磁盘
        string tmp = Path.Combine(Path.GetTempPath(), "LocalModelsEngineUpdate");
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        Directory.CreateDirectory(tmp);
        string zip = Path.Combine(tmp, file);

        var cancel = new System.Threading.ManualResetEvent(false);
        _engineCancel = cancel;

        var r = EngineDownload.Download(url, zip,
            delegate(long done, long total, double mbps)
            {
                if (done < 0)
                {
                    SetStatus("引擎下载中断，正在自动重试…", Warn);
                    return;
                }
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    SetStatus("下载引擎 " + pct + "%  " + EngineDownload.Human(done)
                              + " / " + EngineDownload.Human(total)
                              + (mbps > 0 ? "  " + mbps.ToString("0.0") + " MB/s" : ""), Warn);
                }
            }, cancel);

        if (!r.Ok)
        {
            Log("✗ 引擎下载失败：" + r.Error);
            Log("  你可以换个网络再运行一次本更新程序 —— 已下载的部分会保留。");
            Log("  或者手动下载：" + EngineKit.KitReleaseUrl());
            try { Directory.Delete(tmp, true); } catch { }
            return false;
        }
        Log("✓ 下载完成：" + EngineDownload.Human(r.Bytes));

        // 解压到安装目录（覆盖 engine\）
        SetStatus("正在解压引擎…", Warn);
        Log("解压到 " + Path.Combine(_dir, "engine"));
        try
        {
            // cleanUnlisted: true —— 清掉 engine\ 里不在新包清单中的旧文件
            // （新版引擎包去掉了 pick-engine.bat 之类的辅助脚本，旧的要跟着走）
            int n = KitExtract.Run(zip, _dir,
                delegate(string s) { if (s.StartsWith("  + ")) return; Log(s); }, true);
            Log("✓ 已释放 " + n + " 个文件");
        }
        catch (Exception ex)
        {
            Log("✗ 解压失败：" + ex.Message);
            try { Directory.Delete(tmp, true); } catch { }
            return false;
        }

        // 记下版本，下次不再重复下
        UpdaterCore.MarkEngineVersion(_dir, EngineKit.KitDate);
        Log("✓ 引擎已更新到 " + EngineKit.KitDate);

        try { Directory.Delete(tmp, true); } catch { }
        return true;
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
