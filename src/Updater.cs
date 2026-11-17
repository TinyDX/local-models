// Updater.cs -- 轻量更新器（LLLM-Studio-Update.exe）
//
// 为什么要有它：完整包 4.54 GB，其中 4.2 GB 是三个引擎 exe。
// 但更新程序本身只需要替换几个小文件（≈300 KB）——
// 让已经装过的人为了 300 KB 去下 4.5 GB 是不合理的。
//
// 设计要点：
//
//   1) **跨版本**：不管对方是 0.1.0 还是 0.2.0 还是 0.3.0，
//      装完都是当前版本。所以内嵌的是「当前版本的全部程序文件」，
//      而不是「相对上一版的差异」—— 差异包只对特定版本有效，太脆。
//
//   2) **不动用户的東西**：只覆盖程序文件。
//      models\（模型）、对话历史.json、各种设置 json、licenses\、engine\ 一律不碰。
//
//   3) **自己找安装目录**：优先读注册表的 InstallLocation（安装器写的），
//      读不到再找自己旁边的 launch-config.json。都不行就让用户手动选。
//
//   4) **先关程序再替换**：正在运行的 exe 是锁住的，必须先杀掉。
//      只杀安装目录里的那个，不碰别处同名的进程。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal static class UpdaterCore
{
    /// 卸载注册表根
    private const string UninstallRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// 目录里有没有启动器（新旧两种文件名都认）。
    /// 老用户升级时目录里还叫 "Local Models.exe"；新版是 "LLLM Studio.exe"。
    public static bool HasLauncher(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, AppInfo.LauncherExe))
                || File.Exists(Path.Combine(dir, AppInfo.LegacyLauncherExe));
        }
        catch { return false; }
    }

    /// 找出该目录里的启动器路径（优先新版名字）。
    public static string LauncherPath(string dir)
    {
        string n = Path.Combine(dir, AppInfo.LauncherExe);
        if (File.Exists(n)) return n;
        string o = Path.Combine(dir, AppInfo.LegacyLauncherExe);
        if (File.Exists(o)) return o;
        return n;
    }

    /// 要替换的文件（相对安装目录的路径 → 内嵌资源名）
    /// 只列**程序文件**，用户数据和引擎不在其中。
    public static readonly string[][] Files = new string[][]
    {
        new string[] { AppInfo.LauncherExe,      "LLLM Studio.exe" },
        new string[] { "Uninstall.exe",         "Uninstall.exe" },
        new string[] { "webui\\index.html",     "index.html" }
    };

    /// 这些文件只在新版存在时才放，旧版没有也不影响
    public static readonly string[][] ExtraFiles = new string[][]
    {
        new string[] { "更新日志.txt", "更新日志.txt" }
    };

    /// 更新完成后要**删掉**的旧文件（改名遗留）。
    ///
    /// ★ 这是本版最要紧的修复：
    ///   0.4.1beta 把启动器从 "Local Models.exe" 改名成 "LLLM Studio.exe"，
    ///   但更新器只写入新文件、没删旧文件 —— 结果安装目录里同时躺着两个 exe。
    ///   用户双击的往往是旧的（桌面快捷方式还指着它），
    ///   于是更新后看到的还是老界面，以为更新没生效。
    ///
    /// 这些文件只在**新版对应文件已经就位**之后才删，避免把用户唯一能启动的程序删掉。
    public static readonly string[] ObsoleteFiles = new string[]
    {
        AppInfo.LegacyLauncherExe      // "Local Models.exe"
    };

    /// 修桌面/开始菜单的快捷方式：改名后旧 .lnk 指向的 exe 已经不存在了。
    /// 找不到旧 lnk 也没关系（用户可能是从安装目录直接启动的）。
    public static void FixShortcuts(string dir, Action<string> log)
    {
        try
        {
            string launcher = Path.Combine(dir, AppInfo.LauncherExe);
            if (!File.Exists(launcher)) return;

            var dirs = new List<string>();
            try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
            try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
            try
            {
                string sm = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.LegacyProduct);
                if (Directory.Exists(sm)) dirs.Add(sm);
            }
            catch { }

            // 旧 lnk 的名字是 "Local Models.lnk"
            foreach (string d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                string oldLnk = Path.Combine(d, AppInfo.LegacyProduct + ".lnk");
                if (!File.Exists(oldLnk)) continue;

                try
                {
                    // 删旧建新 —— 用 WScript.Shell 创建，和安装器一致
                    File.Delete(oldLnk);
                    string newLnk = Path.Combine(d, AppInfo.Product + ".lnk");
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t != null)
                    {
                        object sh = Activator.CreateInstance(t);
                        object sc = t.InvokeMember("CreateShortcut",
                            System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { newLnk });
                        Type st = sc.GetType();
                        st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { launcher });
                        st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { dir });
                        st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { launcher + ",0" });
                        st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { AppInfo.Product });
                        st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    }
                    log("✓ 已更新快捷方式：" + Path.GetFileName(newLnk));
                }
                catch (Exception ex)
                {
                    log("⚠ 快捷方式更新失败：" + ex.Message);
                }
            }
        }
        catch { }
    }

    // ---------------- 引擎更新 ----------------
    //
    // 为什么增量包也要管引擎：
    //   引擎更新时（比如这次 20261002 → 20261013），
    //   如果只能靠用户重下 800 MB 的安装包，那增量包就没意义了。
    //   所以增量包也带下载引擎的能力 —— 反正下载代码是现成的。
    //
    // 判断要不要更新：看安装目录里有没有 engine\infer-serve-<arch>.exe，
    //   有就说明引擎已装。至于版本新旧，靠一个记录文件（引擎版本.txt）。
    //   没记录文件 = 旧版装的 = 需要更新（除非用户自己跳过）。

    /// 引擎版本记录文件（写在安装目录）
    public const string EngineVersionFile = "引擎版本.txt";

    /// 读已装引擎的版本标记。没有就返回空串（= 旧版装的，需要更新）。
    public static string InstalledEngineVersion(string dir)
    {
        try
        {
            string p = Path.Combine(dir, EngineVersionFile);
            if (!File.Exists(p)) return "";
            return File.ReadAllText(p, System.Text.Encoding.UTF8).Trim();
        }
        catch { return ""; }
    }

    /// 安装目录里有没有引擎
    public static bool HasEngine(string dir, string archKey)
    {
        try
        {
            string tag = EngineKit.ArchTag(archKey);
            if (tag.Length == 0) return false;
            // 三个架构的文件名都试一遍（用户可能换过显卡）
            foreach (string arch in EngineKit.ArchKeys)
            {
                string exe = Path.Combine(dir, "engine", EngineKit.EngineExeFor(arch));
                if (File.Exists(exe)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// 写引擎版本标记
    public static void MarkEngineVersion(string dir, string version)
    {
        try { File.WriteAllText(Path.Combine(dir, EngineVersionFile), version, new System.Text.UTF8Encoding(false)); }
        catch { }
    }

    /// 从注册表读安装目录（安装器写的 InstallLocation）
    public static string RegistryDir()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                UninstallRoot + AppInfo.UninstallKey) ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallRoot + AppInfo.LegacyProduct))
            {
                if (k == null) return "";
                object v = k.GetValue("InstallLocation");
                if (v == null) return "";
                string s = v.ToString();
                return (s.Length > 0 && Directory.Exists(s)) ? s : "";
            }
        }
        catch { return ""; }
    }

    /// 从自己旁边的 launch-config.json 猜安装目录
    public static string SiblingDir()
    {
        try
        {
            string here = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(here)) return "";
            // 自己就放在安装目录里
            if (HasLauncher(here)) return here;
            // 自己旁边有个 payload\（跟完整包放一起了）
            string sub = Path.Combine(here, "payload");
            if (HasLauncher(sub)) return sub;
        }
        catch { }
        return "";
    }

    /// 判断一个目录像不像安装目录（新旧两种 exe 名都认）
    public static bool LooksInstalled(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            // 启动器在，就算装上过（引擎可能被用户删了，不该因此拒绝更新）
            return HasLauncher(dir);
        }
        catch { return false; }
    }

    /// 读某个目录里已安装程序的版本号。
    ///
    /// 从 exe 里扫 "x.y.zBeta" 这种字符串 —— .NET 的字符串常量在 PE 里是 UTF-16，
    /// 所以按 UTF-16 找。找不到就返回空串。
    public static string InstalledVersion(string dir)
    {
        try
        {
            string exe = LauncherPath(dir);
            if (!File.Exists(exe)) return "";
            byte[] b = File.ReadAllBytes(exe);

            // 匹配 数字.数字.数字Beta，先按 UTF-16 找
            for (int i = 0; i + 24 <= b.Length; i++)
            {
                // UTF-16 里每个字符是 [字符, 0]
                if (b[i] < '0' || b[i] > '9') continue;
                if (i + 1 >= b.Length || b[i + 1] != 0) continue;
                var sb = new StringBuilder();
                int k = i;
                while (k + 1 < b.Length && b[k + 1] == 0 && b[k] >= 32 && b[k] < 127)
                {
                    sb.Append((char)b[k]);
                    k += 2;
                    if (sb.Length > 24) break;
                }
                string s = sb.ToString();
                // ★ 大小写都要认 —— 版本号从 0.3.5Beta 改成了 0.4.0beta，
                //   只认大写 Beta 会导致「检测到的版本：无法识别」
                // ★ 要认这几种写法：
                //   · 0.3.5Beta          老版（大写 Beta）
                //   · 0.4.1beta          小写 beta
                //   · 4.0.2beta-canary   编号笔误时期的 canary 包
                //   · 0.4.5.beta         beta 前多一个点
                //   漏了任何一种都会显示「检测到的版本：无法识别」。
                if (Regex.IsMatch(s, @"^\d+\.\d+\.\d+\.?[Bb]eta(-canary)?$")) return s;
            }
        }
        catch { }
        return "";
    }

    /// 比较版本号，a 比 b 新返回 true。形如 0.3.1Beta / 0.4.5.beta / 4.0.2beta-canary
    ///
    /// canary 当作「同号里更旧的」处理：
    ///   0.4.2beta-canary < 0.4.2beta
    /// 这样正式版能把 canary 顶掉，反过来 canary 不会盖掉正式版。
    public static bool IsNewer(string a, string b)
    {
        try
        {
            if (string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a)) return false;
            string ra = Regex.Match(a, @"^\d+\.\d+\.\d+").Value;
            string rb = Regex.Match(b, @"^\d+\.\d+\.\d+").Value;
            if (ra.Length == 0 || rb.Length == 0) return a != b;
            var va = Normalize(ra);
            var vb = Normalize(rb);
            if (va != vb) return va > vb;

            // 主版本号相同 → 看是不是 canary
            bool ca = a.IndexOf("canary", StringComparison.OrdinalIgnoreCase) >= 0;
            bool cb = b.IndexOf("canary", StringComparison.OrdinalIgnoreCase) >= 0;
            if (ca == cb) return false;   // 同为 canary 或同为正式 → 不算更新
            return !ca;                    // a 是正式版、b 是 canary → a 更新
        }
        catch { return a != b; }
    }

    /// 版本号归一化。
    ///
    /// ★ 为什么要这一步：
    ///   中间有几版被误编号成 4.0.2 / 4.0.3 / 4.0.4（本意是 0.4.x），
    ///   已经发到测试群了。如果照字面比较，4.0.3 > 0.4.5 恒成立 ——
    ///   装了 4.0.3beta-canary 的人永远收不到 0.4.5 的更新。
    ///   把那批 4.0.x 折成 0.0.x 看待，它们就正确地落在 0.4.5 之前。
    private static Version Normalize(string baseVer)
    {
        var v = new Version(baseVer);
        if (v.Major == 4 && v.Minor == 0) return new Version(0, 0, v.Build);
        return v;
    }

    /// 杀掉安装目录里正在运行的程序（只杀这个目录里的，别处同名的不动）
    public static void KillRunning(string dir)
    {
        try
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (path != null && path.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch { }
            }
        }
        catch { }
        System.Threading.Thread.Sleep(800);
    }

    /// 读内嵌资源
    public static byte[] Embedded(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (res.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    res.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream s = asm.GetManifestResourceStream(res))
                    {
                        if (s == null) continue;
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
