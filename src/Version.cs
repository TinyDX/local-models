// Version.cs -- 版本号与产品名的**唯一来源**
//
// 这个文件被三个 exe 一起编译进去：
//   · Local Models.exe  启动器
//   · Bonsai2-Setup.exe 安装程序
//   · Uninstall.exe     卸载程序
//
// 改版本号只改这里一处，三边自动一致 —— 包括写进注册表的 DisplayVersion。
// 编译命令里必须带上这个文件，漏了会报"找不到 AppInfo"。
using System;

internal static class AppInfo
{
    /// 程序版本。安装器会把它写进注册表的 DisplayVersion，
    /// 启动器会把它显示在「设置 → 关于 → 版本号」。
    public const string Version = "0.3.5beta";

    /// 产品名（注册表项名、窗口标题都用它）
    public const string Product = "Local Models";

    /// 注册表里卸载项的子键名
    public const string UninstallKey = "Local Models";
}
