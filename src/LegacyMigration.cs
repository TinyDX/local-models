// LegacyMigration.cs -- 从旧版 "Local Models" 迁移到 "LLLM Studio"
//
// 背景：
//   0.4.1beta 之前产品叫 "Local Models"，安装目录、注册表键、快捷方式、exe 文件名
//   全是那个名字。改名之后，老用户升级会面对：
//     · 新程序装在 C:\Program Files\LLLM Studio
//     · 旧的还在 C:\Program Files\Local Models（模型、配置、对话记录都在里面）
//     · 控制面板里两个卸载项
//
// 这个类负责：
//   1. 检测旧安装（目录 + 注册表）
//   2. 把用户数据（模型、配置、对话记录）**移动**到新目录
//   3. 清掉旧目录、旧注册表、旧快捷方式
//
// 设计原则：
//   · **移动而不是复制** —— 模型 6 GB，复制占双倍空间
//   · **先移动数据，成功后再删旧目录** —— 失败时旧数据还在
//   · **模型目录优先用 MoveFileEx（同盘秒移）**，跨盘才退回复制+删除
//   · 每一步都记日志，出问题能查
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

internal static class LegacyMigration
{
    /// 检测结果
    internal sealed class DetectResult
    {
        public bool Found;              // 找到旧安装
        public string Dir = "";         // 旧安装目录
        public bool HasRegistry;        // 旧的卸载注册表项还在
        public long DataBytes;          // 用户数据总大小（模型+配置+对话）
        public int ModelCount;          // 模型文件数
    }

    /// 卸载注册表根
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// 检测旧版安装。返回 null 表示没装过旧版。
    public static DetectResult Detect()
    {
        var r = new DetectResult();
        try
        {
            // ① 注册表里登记过？
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallRoot + AppInfo.LegacyProduct))
            {
                if (k != null)
                {
                    r.HasRegistry = true;
                    object v = k.GetValue("InstallLocation");
                    if (v != null && Directory.Exists(v.ToString()))
                    {
                        r.Dir = v.ToString();
                        r.Found = true;
                    }
                }
            }

            // ② 注册表没登记（比如手动删过），退回到默认路径找
            if (r.Dir.Length == 0)
            {
                string def = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    AppInfo.LegacyInstallFolder);
                if (Directory.Exists(def) && File.Exists(Path.Combine(def, AppInfo.LegacyLauncherExe)))
                {
                    r.Dir = def;
                    r.Found = true;
                }
            }

            // ③ 再看一眼旧目录里有没有可迁移的东西
            if (r.Found)
            {
                try
                {
                    string md = Path.Combine(r.Dir, "models");
                    if (Directory.Exists(md))
                    {
                        foreach (string f in Directory.GetFiles(md, "*.ninfer"))
                        {
                            r.ModelCount++;
                            try { r.DataBytes += new FileInfo(f).Length; } catch { }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return r;
    }

    /// 用户数据清单 —— 这些要从旧目录搬到新目录。
    /// 程序文件（exe、webui、engine）**不搬** —— 新安装会重新装一份。
    private static readonly string[] DataFiles = new string[]
    {
        "KVMem.json",
        "磁盘KV.json",
        "推理等级.json",
        "访问控制.json",
        "API密钥.json",
        "引擎密钥.json",
        "对话历史.json",
        "launch-config.json",
        "引擎版本.txt",
        "更新日志.txt"
    };

    /// 迁移数据。返回 (成功, 消息)。
    ///
    /// 顺序很重要：
    ///   1. 先确保新目录存在
    ///   2. 移动 model 目录（大件，先做，失败就整体放弃）
    ///   3. 逐个移动配置文件（小件）
    ///   4. 全部成功后，删旧目录 + 旧注册表 + 旧快捷方式
    public static bool Migrate(string oldDir, string newDir, Action<string> log)
    {
        try
        {
            if (!Directory.Exists(oldDir)) { log("旧目录不存在，跳过迁移"); return true; }
            if (string.Equals(
                    Path.GetFullPath(oldDir).TrimEnd('\\'),
                    Path.GetFullPath(newDir).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                log("新旧目录相同，无需迁移");
                return true;
            }

            Directory.CreateDirectory(newDir);

            // ---- 1. 模型目录（最重要，先搬）----
            string oldModels = Path.Combine(oldDir, "models");
            string newModels = Path.Combine(newDir, "models");
            if (Directory.Exists(oldModels))
            {
                int moved = 0;
                Directory.CreateDirectory(newModels);
                foreach (string f in Directory.GetFiles(oldModels))
                {
                    string dst = Path.Combine(newModels, Path.GetFileName(f));
                    // 新目录已存在同名文件就不覆盖（可能是新装时释放的 README）
                    if (File.Exists(dst) && !f.EndsWith(".ninfer", StringComparison.OrdinalIgnoreCase))
                    {
                        log("  跳过（新版已有）：" + Path.GetFileName(f));
                        continue;
                    }
                    if (MoveFileAcross(f, dst, log)) moved++;
                }
                log("  已迁移模型 " + moved + " 个");
            }

            // ---- 2. 配置文件 ----
            int cfgMoved = 0;
            foreach (string name in DataFiles)
            {
                string src = Path.Combine(oldDir, name);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(newDir, name);
                if (MoveFileAcross(src, dst, log)) cfgMoved++;
            }
            log("  已迁移配置 " + cfgMoved + " 个");
            return true;
        }
        catch (Exception ex)
        {
            log("迁移失败：" + ex.Message);
            return false;
        }
    }

    /// 移动单个文件。
    /// 同盘用 File.Move（瞬间完成）；跨盘会抛异常，退回复制+删除。
    /// 目标已存在时先删目标（新装的默认文件让位于用户数据）。
    private static bool MoveFileAcross(string src, string dst, Action<string> log)
    {
        try
        {
            string dd = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dd)) Directory.CreateDirectory(dd);

            if (File.Exists(dst))
            {
                // 用户的 .ninfer 模型同名 —— 说明重复下载过，保留旧的（大文件优先）
                try { File.Delete(dst); } catch { return false; }
            }

            try
            {
                File.Move(src, dst);       // 同盘，瞬间
            }
            catch (IOException)
            {
                File.Copy(src, dst, true);  // 跨盘
                File.Delete(src);
            }
            return true;
        }
        catch (Exception ex)
        {
            log("  移动失败 " + Path.GetFileName(src) + "：" + ex.Message);
            return false;
        }
    }

    /// 清理旧版残留：旧目录、旧注册表项、旧快捷方式。
    /// **只在迁移成功后才调用** —— 否则用户数据还在旧目录里。
    public static void Cleanup(string oldDir, Action<string> log)
    {
        // ① 旧注册表项
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallRoot + AppInfo.LegacyProduct, true))
            {
                if (k != null)
                {
                    k.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallRoot + AppInfo.LegacyProduct, false);
                    log("  已删除旧注册表项");
                }
            }
        }
        catch (Exception ex) { log("  删注册表失败：" + ex.Message); }

        // ② 旧快捷方式
        try
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string oldLnk = Path.Combine(desk, AppInfo.LegacyProduct + ".lnk");
            if (File.Exists(oldLnk)) { File.Delete(oldLnk); log("  已删除旧桌面快捷方式"); }

            string sm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.LegacyProduct);
            if (Directory.Exists(sm)) { Directory.Delete(sm, true); log("  已删除旧开始菜单项"); }
        }
        catch (Exception ex) { log("  删快捷方式失败：" + ex.Message); }

        // ③ 旧安装目录 —— 最后删，且删之前再确认 models 已经空
        try
        {
            if (!Directory.Exists(oldDir)) return;

            string oldModels = Path.Combine(oldDir, "models");
            if (Directory.Exists(oldModels))
            {
                string[] left = Directory.GetFiles(oldModels, "*.ninfer");
                if (left.Length > 0)
                {
                    log("  ⚠ 旧目录里还有 " + left.Length + " 个模型未迁移，保留旧目录");
                    return;
                }
            }

            Directory.Delete(oldDir, true);
            log("  已删除旧安装目录");
        }
        catch (Exception ex)
        {
            log("  删旧目录失败（可能需要管理员权限）：" + ex.Message);
            log("  可以手动删除：" + oldDir);
        }
    }
}
