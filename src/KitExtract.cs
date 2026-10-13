// KitExtract.cs -- 解开引擎包 zip
//
// 抽成独立文件的原因：
//   安装器（Installer.cs）和更新器（UpdaterForm.cs）都要解引擎包。
//   但 Installer.cs 里有整套安装界面，更新器不该把它拖进来。
//   这里只依赖 System.IO.Compression，谁都能用。
//
// 包里通常有一层顶层目录（infer-engine-sm120a-20261013/），
// 要剥掉，让 engine\ 直接落在目标目录下 ——
// 否则会变成 <安装目录>\infer-engine-sm120a-20261013\engine\，程序找不到引擎。
using System;
using System.IO;
using System.IO.Compression;

internal static class KitExtract
{
    /// 把 zip 解到 targetDir，返回释放的文件数。
    /// log 可以为 null。
    ///
    /// cleanUnlisted 为 true 时，解压完会**删掉 engine\ 里不在包清单中的文件** ——
    /// 新版引擎包去掉了某个文件（比如 pick-engine.bat）时，旧文件不会赖着不走。
    /// 只清 engine\ 一个目录：docs\、配置、模型、用户文档一律不碰。
    public static int Run(string zipPath, string targetDir, Action<string> log, bool cleanUnlisted)
    {
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
            throw new FileNotFoundException("找不到引擎包：" + zipPath);

        Directory.CreateDirectory(targetDir);

        int n;
        var kept = new System.Collections.Generic.List<string>();

        using (var fs = File.OpenRead(zipPath))
        using (var za = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            // 先判断要不要剥掉顶层目录：
            // 所有条目都在同一个顶级目录下时才剥，避免误伤本来就在根的文件。
            string strip = DetectCommonRoot(za);

            n = 0;
            long total = 0;
            foreach (var e in za.Entries)
            {
                if (e.FullName.EndsWith("/")) continue;      // 目录项

                string rel = e.FullName.Replace('/', '\\');
                if (strip.Length > 0 && rel.StartsWith(strip, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(strip.Length);

                if (rel.Length == 0) continue;
                if (rel.Contains("..")) continue;            // 防目录穿越

                string dst = Path.Combine(targetDir, rel);
                string dd = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);

                using (var src = e.Open())
                using (var outS = File.Create(dst))
                    src.CopyTo(outS, 1 << 20);

                long len = new FileInfo(dst).Length;
                total += len; n++;
                kept.Add(rel.ToLowerInvariant());
                if (log != null)
                    log("  + " + rel + "   (" + Math.Round(len / 1048576.0, 1) + " MB)");
            }
            if (log != null)
                log("  合计 " + Math.Round(total / 1048576.0, 1) + " MB");
        }

        if (cleanUnlisted) CleanEngineDir(targetDir, kept, log);
        return n;
    }

    /// 兼容旧签名
    public static int Run(string zipPath, string targetDir, Action<string> log)
    {
        return Run(zipPath, targetDir, log, false);
    }

    /// 删掉 engine\ 下不在新包清单里的文件。
    ///
    /// 为什么只清 engine\：
    ///   那个目录**完全属于引擎包**，里面不该有用户的东西。
    ///   而 docs\、models\、*json 配置、更新日志都可能被用户改过或属于程序本体，
    ///   一概不碰。
    ///
    /// 清空子目录也一并删掉（引擎包换版本时可能有废弃的子目录）。
    private static void CleanEngineDir(string targetDir, System.Collections.Generic.List<string> kept, Action<string> log)
    {
        try
        {
            string eng = Path.Combine(targetDir, "engine");
            if (!Directory.Exists(eng)) return;

            int removed = 0;
            foreach (string f in Directory.GetFiles(eng, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(targetDir.Length).TrimStart('\\').ToLowerInvariant();
                if (kept.Contains(rel)) continue;

                try
                {
                    File.Delete(f);
                    removed++;
                    if (log != null)
                        log("  - 删除旧文件 " + f.Substring(targetDir.Length).TrimStart('\\'));
                }
                catch { }
            }

            // 清掉空目录（新包没有的子目录）
            try
            {
                foreach (string d in Directory.GetDirectories(eng, "*", SearchOption.AllDirectories))
                {
                    if (Directory.GetFiles(d).Length == 0 && Directory.GetDirectories(d).Length == 0)
                        Directory.Delete(d);
                }
            }
            catch { }

            if (removed > 0 && log != null)
                log("  已清理 " + removed + " 个旧文件");
        }
        catch { }
    }

    /// 所有条目是否同属一个顶级目录？是就返回 "该目录\"（含结尾反斜杠），否则空串。
    private static string DetectCommonRoot(ZipArchive za)
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
}
