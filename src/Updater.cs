// Updater.cs -- 轻量更新器（LocalModels-Update.exe）
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
    /// 要替换的文件（相对安装目录的路径 → 内嵌资源名）
    /// 只列**程序文件**，用户数据和引擎不在其中。
    public static readonly string[][] Files = new string[][]
    {
        new string[] { "Local Models.exe",      "Local Models.exe" },
        new string[] { "Uninstall.exe",         "Uninstall.exe" },
        new string[] { "webui\\index.html",     "index.html" }
    };

    /// 这些文件只在新版存在时才放，旧版没有也不影响
    public static readonly string[][] ExtraFiles = new string[][]
    {
        new string[] { "更新日志.txt", "更新日志.txt" }
    };

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
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Local Models"))
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
            if (File.Exists(Path.Combine(here, "Local Models.exe"))) return here;
            // 自己旁边有个 payload\（跟完整包放一起了）
            string sub = Path.Combine(here, "payload");
            if (File.Exists(Path.Combine(sub, "Local Models.exe"))) return sub;
        }
        catch { }
        return "";
    }

    /// 判断一个目录像不像 Local Models 的安装目录
    public static bool LooksInstalled(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            // 启动器在，就算装上过（引擎可能被用户删了，不该因此拒绝更新）
            return File.Exists(Path.Combine(dir, "Local Models.exe"));
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
            string exe = Path.Combine(dir, "Local Models.exe");
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
                if (Regex.IsMatch(s, @"^\d+\.\d+\.\d+[Bb]eta$")) return s;
            }
        }
        catch { }
        return "";
    }

    /// 比较版本号，a 比 b 新返回 true。形如 0.3.1Beta
    public static bool IsNewer(string a, string b)
    {
        try
        {
            if (string.IsNullOrEmpty(b)) return true;
            if (string.IsNullOrEmpty(a)) return false;
            string ra = Regex.Match(a, @"^\d+\.\d+\.\d+").Value;
            string rb = Regex.Match(b, @"^\d+\.\d+\.\d+").Value;
            if (ra.Length == 0 || rb.Length == 0) return a != b;
            var va = new Version(ra);
            var vb = new Version(rb);
            return va > vb;
        }
        catch { return a != b; }
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
