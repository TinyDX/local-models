// Version.cs -- 版本号与产品名的**唯一来源**
//
// 这个文件被四个 exe 一起编译进去：
//   · LLLM Studio.exe   启动器
//   · LLLM-Studio-Setup.exe 安装程序
//   · Uninstall.exe     卸载程序
//   · LLLM-Studio-Update.exe 更新程序
//
// 改版本号/产品名只改这里一处，几边自动一致 —— 包括写进注册表的 DisplayVersion。
// 编译命令里必须带上这个文件，漏了会报"找不到 AppInfo"。
using System;

internal static class AppInfo
{
    /// 程序版本。安装器会把它写进注册表的 DisplayVersion，
    /// 启动器会把它显示在「设置 → 关于 → 版本号」。
    public const string Version = "0.4.5.beta";

    /// 产品名 —— 窗口标题、注册表键、快捷方式、安装目录都用它。
    public const string Product = "LLLM Studio";

    /// 产品全称（安装器欢迎页、关于页显示）
    public const string ProductFull = "Local Large Language Models";

    /// 启动器 exe 的文件名（不带路径）
    public const string LauncherExe = "LLLM Studio.exe";

    /// 安装程序的文件名（发布时用这个名）
    public const string SetupExe = "LLLM-Studio-Setup.exe";

    /// 更新程序的文件名（发布时用这个名）
    public const string UpdateExe = "LLLM-Studio-Update.exe";

    /// 旧版的安装/更新程序名 —— 排除列表里要一并认，
    /// 否则老用户的目录里留着旧 exe，会被当成启动器。
    public const string LegacySetupExe = "Bonsai2-Setup.exe";
    public const string LegacyUpdateExe = "LocalModels-Update.exe";

    /// 注册表里卸载项的子键名
    public const string UninstallKey = "LLLM Studio";

    // ---------------- 旧版（兼容用） ----------------
    //
    // 0.4.1beta 之前的版本叫 "Local Models"，目录/注册表/快捷方式全是那个名字。
    // 下面的常量**永远不要删** —— 老用户升级时要靠它们认出来做迁移。
    //
    // 迁移策略（见 LegacyMigration.cs）：
    //   · 装了新版：检测旧目录/旧注册表，把用户数据搬过来
    //   · 卸载新版：新旧两个键都查，两处文件都清

    /// 旧版产品名
    public const string LegacyProduct = "Local Models";

    /// 旧版启动器 exe 名
    public const string LegacyLauncherExe = "Local Models.exe";

    /// 旧版默认安装目录（Program Files 下）
    public const string LegacyInstallFolder = "Local Models";
}
