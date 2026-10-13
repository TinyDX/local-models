// InstallCore.cs -- 安装动作的核心实现
//
// 抽出来的目的：
//   1) 界面（InstallerForm）和静默模式（Bonsai2-Setup.exe /silent）共用同一套逻辑，
//      避免两处实现分叉。
//   2) 可以脱离 GUI 自动验证。
//
// 关键行为（修复的旧 bug）：**释放 payload 整棵目录树**到安装目录，
// 只对 engine\ 下的引擎 exe 做架构过滤。旧版只复制 payload\engine\，
// 导致安装完目录里没有启动器、网页、许可。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

internal static class InstallCore
{
    /// exe 里编进了几个程序本体文件（0 = 没编，走外部 payload 目录的老路子）
    public static int EmbeddedCount()
    {
        try
        {
            int n = 0;
            foreach (string s in System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (s.StartsWith("p.", StringComparison.Ordinal)) n++;
            return n;
        }
        catch { return 0; }
    }

    /// 把内嵌在 exe 里的程序本体释放到临时目录，返回该目录。
    ///
    /// 为什么要内嵌：新的分发方式里，用户从 GitHub 只下载一个安装器 exe。
    ///   程序本体（启动器、网页、许可，约 0.4 MB）如果放在 exe 旁边的 payload\ 目录，
    ///   用户根本拿不到 —— 他只下了一个文件。
    ///   所以直接编进 exe，运行时解到临时目录再用。
    ///
    /// 引擎不内嵌（4.2 GB），按显卡单独下载。
    public static string ExtractEmbeddedPayload(Action<string> log)
    {
        string tmp = Path.Combine(Path.GetTempPath(),
            "LocalModelsSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string[] names = asm.GetManifestResourceNames();
        int n = 0;

        foreach (string res in names)
        {
            // 资源名形如 "p.<扁平化路径>" —— 见打包脚本里的命名规则。
            // 我们只取以 "p." 开头的（其余是图标、网页等其它资源）。
            if (!res.StartsWith("p.", StringComparison.Ordinal)) continue;

            string rel = ResourceToPath(res.Substring(2));
            if (rel.Length == 0) continue;

            string dst = Path.Combine(tmp, rel);
            string dd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);

            using (Stream s = asm.GetManifestResourceStream(res))
            {
                if (s == null) continue;
                using (var o = File.Create(dst)) s.CopyTo(o);
            }
            n++;
        }

        if (log != null) log("  已从安装程序释放 " + n + " 个文件");
        return tmp;
    }

    /// 把扁平化的资源名还原成相对路径。
    ///
    /// 打包时用 "__" 代替路径分隔符（资源名里不能带反斜杠），
    /// 文件名本身保持原样（含空格和中文，.NET 资源名支持）。
    ///   webui__index.html              → webui\index.html
    ///   licenses__MIT-fmt-bundled.txt  → licenses\MIT-fmt-bundled.txt
    ///   Local Models.exe               → Local Models.exe
    private static string ResourceToPath(string flat)
    {
        if (string.IsNullOrEmpty(flat)) return "";
        // 从后往前找第一个 "__"，它就是最后一层目录的分隔
        int i = flat.LastIndexOf("__", StringComparison.Ordinal);
        if (i > 0)
            return flat.Substring(0, i).Replace("__", "\\") + "\\" + flat.Substring(i + 2);
        return flat;
    }

    /// 用完就删掉临时目录。删不掉也不影响 —— 下次装会建新的。
    public static void CleanupTemp(string dir)
    {
        try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { }
    }

    /// 从「内嵌程序文件 + 下载的引擎包 zip」安装。
    ///
    /// 为什么分成两个来源：
    ///   安装器本体只有 1.5 MB（程序、网页、许可都在里面），
    ///   引擎包 1.3~1.5 GB 是用户按自己显卡单独下的 ——
    ///   这样一个包不用把三个架构的 4.2 GB 全塞进去。
    ///
    /// srcPayload  内嵌 payload 解压出来的临时目录（小文件）
    /// ctx.KitLocalPath  用户下好 / 选好的引擎包 zip
    public static bool RunWithKit(string srcPayload, InstallerContext ctx, Action<string> log)
    {
        // 先把小文件装好（引擎目录里的引擎 exe 这次跳过 —— 由 zip 提供）
        if (!Run(srcPayload, ctx, log)) return false;

        string kit = ctx.KitLocalPath;
        if (string.IsNullOrEmpty(kit) || !File.Exists(kit))
        {
            log("");
            log("❌ 找不到引擎包：" + (kit ?? "(空)"));
            log("   请回到上一步下载，或用「手动选择本地文件」指定。");
            return false;
        }

        log("");
        log("正在解压引擎包：" + Path.GetFileName(kit));

        try
        {
            long bytes = new FileInfo(kit).Length;
            log("  包大小：" + Math.Round(bytes / 1048576.0, 1) + " MB");

            int n = ExtractKit(kit, ctx.TargetDir, log);
            if (n <= 0)
            {
                log("❌ 引擎包里没有找到可用的文件。");
                return false;
            }
            log("  已释放 " + n + " 个文件");
            return true;
        }
        catch (Exception ex)
        {
            log("❌ 解压失败：" + ex.Message);
            log("   包可能下载不完整。删掉它重新下载试试。");
            return false;
        }
    }

    /// 解开引擎包 zip。
    ///
    /// 包里通常有一层顶层目录（如 infer-engine-sm120a-20261002/），
    /// 要去掉这一层，让 engine\ 直接落在安装目录下 ——
    /// 否则会变成 <安装目录>\infer-engine-sm120a-20261002\engine\，程序找不到引擎。
    ///
    /// 用 .NET 自带的 ZipArchive，不引第三方库。
    ///
    /// 安装器与更新器共用 —— 更新器也要解引擎包，逻辑不能分叉。
    /// 但更新器不想拖进整个 Installer.cs（那里面有界面代码），
    /// 所以真正的实现放在 KitExtract.cs —— 那个文件不依赖 InstallerContext。
    public static int ExtractKitTo(string zipPath, string targetDir, Action<string> log)
    {
        return KitExtract.Run(zipPath, targetDir, log);
    }

    private static int ExtractKit(string zipPath, string targetDir, Action<string> log)
    {
        return KitExtract.Run(zipPath, targetDir, log);
    }

    private static int ExtractKitOld(string zipPath, string targetDir, Action<string> log)
    {
        using (var fs = File.OpenRead(zipPath))
        using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read))
        {
            // 先判断要不要剥掉顶层目录：
            // 所有条目都在同一个顶级目录下时才剥，避免误伤本来就在根的文件。
            string strip = DetectCommonRoot(za);

            int n = 0;
            long total = 0;
            foreach (var e in za.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;      // 目录项

                string rel = e.FullName.Replace('/', '\\');
                if (strip.Length > 0 && rel.StartsWith(strip, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(strip.Length);

                if (rel.Length == 0) continue;
                // 防目录穿越
                if (rel.Contains("..")) continue;

                string dst = Path.Combine(targetDir, rel);
                string dd = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);

                using (var src = e.Open())
                using (var outS = File.Create(dst))
                    src.CopyTo(outS, 1 << 20);

                long len = new FileInfo(dst).Length;
                total += len; n++;
                log("  + " + rel + "   (" + Math.Round(len / 1048576.0, 1) + " MB)");
            }
            log("  合计 " + Math.Round(total / 1048576.0, 1) + " MB");
            return n;
        }
    }

    /// 所有条目是否同属一个顶级目录？是就返回 "该目录\"（含结尾反斜杠），否则空串。
    private static string DetectCommonRoot(System.IO.Compression.ZipArchive za)
    {
        string root = null;
        foreach (var e in za.Entries)
        {
            string name = e.FullName.Replace('/', '\\');
            int slash = name.IndexOf('\\');
            if (slash <= 0) return "";               // 有文件直接在根 → 不剥
            string top = name.Substring(0, slash + 1);
            if (root == null) root = top;
            else if (!root.Equals(top, StringComparison.OrdinalIgnoreCase)) return "";
        }
        return root ?? "";
    }

    /// 执行安装；返回是否成功
    public static bool Run(string srcPayload, InstallerContext ctx, Action<string> log)
    {
        if (!Directory.Exists(srcPayload))
        {
            log("❌ 找不到 payload 目录：" + srcPayload);
            log("   安装包可能不完整。");
            return false;
        }

        log("源目录：" + srcPayload);
        log("目标目录：" + ctx.TargetDir);
        log("");
        Directory.CreateDirectory(ctx.TargetDir);

        string wantEngine = ctx.Gpu != null ? ctx.Gpu.EngineExe : "";
        log("本机将保留引擎：" + (wantEngine.Length > 0 ? wantEngine : "(按架构自动)"));
        log("");

        long total = 0;
        int copied = 0, skipped = 0;
        var files = new List<string>(Directory.GetFiles(srcPayload, "*", SearchOption.AllDirectories));
        files.Sort();

        foreach (string f in files)
        {
            string rel = f.Substring(srcPayload.Length).TrimStart('\\');
            string name = Path.GetFileName(f);
            string relDir = Path.GetDirectoryName(rel);

            // engine\ 下跳过不匹配的引擎 exe
            if (relDir != null && relDir.Equals("engine", StringComparison.OrdinalIgnoreCase)
                && name.StartsWith("ninfer-serve-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!name.Equals(wantEngine, StringComparison.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(f);
                    log("  - 跳过（非本机架构）: " + name + "   (" + (fi.Length / 1048576) + " MB)");
                    skipped++;
                    continue;
                }
            }

            string dst = Path.Combine(ctx.TargetDir, rel);
            string dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

            CopyFile(f, dst);
            long len = new FileInfo(dst).Length;
            total += len;
            copied++;
            log("  + " + rel + "   (" + Math.Round(len / 1048576.0, 2) + " MB)");
        }

        log("");
        log("已复制 " + copied + " 个文件，跳过 " + skipped + " 个");
        log("已释放 " + Math.Round(total / 1073741824.0, 2) + " GB");
        log("");

        WriteConfig(ctx, log);
        // ★ 两个快捷方式选项是独立的 —— 只要有一个要建就得进这个方法。
        //   原来只判断 CreateShortcut，用户取消桌面但留着开始菜单时会两个都不建。
        if (ctx.CreateShortcut || ctx.CreateStartMenu) CreateShortcuts(ctx, log);
        else log("已跳过全部快捷方式（按你的选择）");
        RegisterUninstall(ctx, log);
        ReserveUrlAcl(log);

        log("");
        log("全部完成");
        return true;
    }

    private static void CopyFile(string src, string dst)
    {
        using (var fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var fd = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] buf = new byte[4194304];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) fd.Write(buf, 0, n);
        }
    }

    private static void WriteConfig(InstallerContext ctx, Action<string> log)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"engineExe\": \"" + ctx.Gpu.EngineExe + "\",");
        sb.AppendLine("  \"arch\": \"" + ctx.Gpu.ArchKey + "\",");
        sb.AppendLine("  \"gpu\": \"" + Esc(ctx.Gpu.Name) + "\",");
        sb.AppendLine("  \"vramMiB\": " + ctx.Gpu.VramMiB + ",");
        sb.AppendLine("  \"driver\": \"" + Esc(ctx.Gpu.DriverRaw) + "\",");
        sb.AppendLine("  \"kvCapacity\": " + ctx.Plan.KvCapacity + ",");
        sb.AppendLine("  \"hostKvMiB\": " + ctx.Plan.HostKvMiB + ",");
        sb.AppendLine("  \"graphAllowanceMiB\": " + ctx.Plan.GraphAllowanceMiB + ",");
        sb.AppendLine("  \"note\": \"" + Esc(ctx.Plan.Note) + "\"");
        sb.AppendLine("}");
        File.WriteAllText(Path.Combine(ctx.TargetDir, "launch-config.json"),
            sb.ToString(), new UTF8Encoding(false));
        log("已写出本机适配配置：launch-config.json");
    }

    private static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ---------- 快捷方式 ----------
    private static void CreateShortcuts(InstallerContext ctx, Action<string> log)
    {
        try
        {
            string launcher = FindLauncher(ctx.TargetDir);
            if (launcher == null) { log("⚠ 未找到启动器，跳过快捷方式"); return; }

            // 桌面快捷方式 —— 按用户勾选
            if (ctx.CreateShortcut)
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                MakeLnk(Path.Combine(desk, "Local Models.lnk"), launcher, ctx.TargetDir);
                log("已创建桌面快捷方式：Local Models.lnk");
            }
            else log("已跳过桌面快捷方式（按你的选择）");

            // 开始界面（开始菜单）—— 按用户勾选
            //   只放启动快捷方式。**不放卸载快捷方式**：Windows 会在快捷方式
            //   右键菜单里自动提供「卸载」，点它直接跳到「设置 → 应用」，
            //   而那条路径靠的是卸载注册项（下面 RegisterUninstall 写的）。
            //   再塞一个卸载 lnk 进开始菜单是重复的，还容易被误点。
            if (ctx.CreateStartMenu)
            {
                try
                {
                    string smDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Local Models");
                    Directory.CreateDirectory(smDir);
                    MakeLnk(Path.Combine(smDir, "Local Models.lnk"), launcher, ctx.TargetDir);
                    log("已创建开始菜单快捷方式（卸载走「设置 → 应用」）");
                }
                catch (Exception e) { log("⚠ 开始菜单快捷方式失败：" + e.Message); }
            }
            else log("已跳过开始菜单快捷方式（按你的选择）");
        }
        catch (Exception e)
        {
            log("⚠ 创建快捷方式失败：" + e.Message);
        }
    }

    private static void MakeLnk(string lnk, string target, string workDir)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
            null, sh, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { workDir });
        st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
        st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "Local Models" });
        st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
    }

    public static string FindLauncher(string dir)
    {
        string preferred = Path.Combine(dir, "Local Models.exe");
        if (File.Exists(preferred)) return preferred;

        foreach (string f in Directory.GetFiles(dir, "*.exe"))
        {
            string n = Path.GetFileName(f);
            if (n.Equals("Bonsai2-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.StartsWith("ninfer-serve-", StringComparison.OrdinalIgnoreCase)) continue;
            return f;
        }
        return null;
    }

    // ---------- 注册表卸载项 ----------
    private static void RegisterUninstall(InstallerContext ctx, Action<string> log)
    {
        try
        {
            string uninst = Path.Combine(ctx.TargetDir, "Uninstall.exe");
            using (var k = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.UninstallKey))
            {
                if (k == null) { log("⚠ 无法写入注册表，跳过「应用」列表注册"); return; }
                k.SetValue("DisplayName", "Local Models 本地推理");
                k.SetValue("UninstallString", "\"" + uninst + "\"");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" --quiet");
                string launcher = FindLauncher(ctx.TargetDir);
                if (launcher != null) k.SetValue("DisplayIcon", launcher + ",0");
                k.SetValue("InstallLocation", ctx.TargetDir);
                k.SetValue("Publisher", "Local Models");
                // ★ 版本号取自 Version.cs，和启动器「关于」页显示的完全一致
                //   （原来这里写死 "1.0"，界面上却是 0.2.0Beta，对不上）
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try
                {
                    long bytes = 0;
                    foreach (string f in Directory.GetFiles(ctx.TargetDir, "*", SearchOption.AllDirectories))
                        bytes += new FileInfo(f).Length;
                    k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                }
                catch { }
            }
            log("✅ 已在「设置 → 应用」注册，可直接卸载");
        }
        catch (Exception e)
        {
            log("⚠ 注册卸载项失败：" + e.Message);
        }
    }

    // ---------- URL 预留 ----------
    private static void ReserveUrlAcl(Action<string> log)
    {
        const string url = "http://+:8096/";
        try
        {
            if (HasUrlAcl(url))
            {
                log("网页端口已预留（" + url + "）");
                return;
            }
            log("正在预留网页端口 " + url + "（需要管理员权限，可能弹出 UAC 提示）");
            var psi = new ProcessStartInfo("netsh", "http add urlacl url=" + url + " user=Everyone")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(20000);
                if (p.ExitCode == 0) log("✅ 网页端口预留成功，手机/局域网可访问");
                else log("⚠ 预留返回码 " + p.ExitCode + "；局域网访问可能受限");
            }
        }
        catch (Exception e)
        {
            log("⚠ 未预留网页端口（" + e.Message + "）。");
            log("   影响：仅本机可用；如需手机访问，请以管理员身份运行一次本安装程序，");
            log("   或手动执行：netsh http add urlacl url=" + url + " user=Everyone");
        }
    }

    public static bool HasUrlAcl(string url)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "http show urlacl")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return o != null && o.IndexOf(url, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }
}
