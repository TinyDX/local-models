// EngineKit.cs -- 引擎包的识别与下载源选择
//
// 背景：以前一个安装包 4.54 GB，把三个架构的引擎（86 / 89 / 120a）全塞进去，
//   用户下完只用得上其中一个，白下 2.8 GB。
//
// 现在改成：安装器只带程序本体（约 1.5 MB），引擎单独从 Release 下载。
//   安装器认出本机显卡属于哪个架构，只下那一个包。
//
// 包名规则（与上游发布的一致）：
//   infer-engine-sm86-<日期>.zip
//   infer-engine-sm89-<日期>.zip
//   infer-engine-sm120a-<日期>.zip
//
// 下载源：GitHub 官方在国内经常连不上或极慢。
//   所以除了默认源，还内置几个镜像/代理，并且**启动时自动探测**哪个快。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal static class EngineKit
{
    /// 三个架构，和 GpuInfo.ArchKey 对应
    public static readonly string[] ArchKeys = new string[] { "8.6", "8.9", "12.0" };

    /// ArchKey → 包里的架构标签
    public static string ArchTag(string archKey)
    {
        if (archKey == "8.6") return "sm86";
        if (archKey == "8.9") return "sm89";
        if (archKey == "12.0") return "sm120a";
        return "";
    }

    /// ArchKey → 人类可读的卡系
    public static string ArchName(string archKey)
    {
        if (archKey == "8.6") return "RTX 30 系（sm_86）";
        if (archKey == "8.9") return "RTX 40 系（sm_89）";
        if (archKey == "12.0") return "RTX 50 系（sm_120a）";
        return "未知架构";
    }

    /// 引擎包文件名。日期部分与上游发布保持一致。
    /// 找不到精确匹配时，还会用通配去找（见 FindLocalKit）。
    ///
    /// ★★ 引擎更新时改这里 ★★
    ///   把日期改成新引擎包的日期，重新编译安装器即可。
    ///   对应的包要传到 KitTag 那个 Release 上。
    public const string KitDate = "20261013";

    public static string KitFileName(string archKey)
    {
        string tag = ArchTag(archKey);
        if (tag.Length == 0) return "";
        return "infer-engine-" + tag + "-" + KitDate + ".zip";
    }

    /// 引擎包所在 Release 的网页地址（给用户手动去下时用）
    public static string KitReleaseUrl()
    {
        return "https://github.com/TinyDX/local-models/releases/tag/" + KitTag;
    }

    // ---------------- 下载源 ----------------

    /// 一个候选下载源
    internal sealed class Source
    {
        public string Name;
        public string Prefix;        // 拼在包名前面的部分
        public string Note;
        public long Ms;              // 探测得到的延迟（-1 = 未探测/失败）
        public bool Ok;              // 探测是否可用

        public Source(string name, string prefix, string note)
        {
            Name = name; Prefix = prefix; Note = note; Ms = -1; Ok = false;
        }

        public string UrlFor(string file)
        {
            if (Prefix.Length == 0) return "";
            if (Prefix.EndsWith("/")) return Prefix + file;
            return Prefix + "/" + file;
        }
    }

    /// 引擎包所放的 Release tag。
    ///
    /// ★ 这里**故意不用程序版本号**。
    ///
    /// 原因：引擎包是大文件（三个加起来 4.2 GB）。如果跟着程序版本走，
    /// 每次发新版都得把 4.2 GB 重传一遍 —— 哪怕这一版只是改了个界面。
    /// 而且旧版的安装器会去旧 tag 找，一旦那个 Release 被删就全废了。
    ///
    /// 所以引擎包固定挂在 "engine-kit" 这个 tag 上，
    /// 只在**引擎本身更新**时才重传。程序怎么发版都不影响它。
    ///
    /// 对应的 Release 地址：
    ///   https://github.com/TinyDX/local-models/releases/tag/engine-kit
    public const string KitTag = "engine-kit";

    /// 用户可选的下载源。第一个是官方，其余是常用的 GitHub 加速。
    ///
    /// 之所以内置这些：GitHub Release 的下载在国内经常几十 KB/s 甚至断流。
    /// 这类加速站是公开服务，随时可能失效，所以：
    ///   · 全部列出来让用户自己挑
    ///   · 启动时自动测速，把能用的排前面并默认选中
    ///   · 失败会明确报错，不会静默卡住
    public static List<Source> DefaultSources(string version)
    {
        var l = new List<Source>();
        // 固定用 KitTag，不用传入的 version —— 见上面注释
        string gh = "https://github.com/TinyDX/local-models/releases/download/" + KitTag;

        l.Add(new Source("GitHub 官方", gh, "直连 GitHub，国内可能较慢"));
        l.Add(new Source("ghproxy 镜像", "https://ghproxy.net/" + gh, "第三方加速"));
        l.Add(new Source("gh-proxy 镜像", "https://gh-proxy.com/" + gh, "第三方加速"));
        l.Add(new Source("moeyy 镜像", "https://github.moeyy.xyz/" + gh, "第三方加速"));
        l.Add(new Source("自定义", "", "自己填地址（可指向网盘/内网服务器）"));
        return l;
    }

    /// 探测一个源是否可用、大概多快。
    /// 用 HEAD 请求拿包大小 —— 不下载内容，所以很快。
    public static void Probe(Source s, string file)
    {
        s.Ms = -1; s.Ok = false;
        if (s.Prefix.Length == 0) return;      // 「自定义」不探测
        EngineDownload.EnsureTls();            // 不设 TLS 会握手失败
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(s.UrlFor(file));
            req.Method = "HEAD";
            req.Timeout = 4000;
            req.ReadWriteTimeout = 4000;
            req.AllowAutoRedirect = true;
            req.UserAgent = "LocalModels-Setup";

            var sw = Stopwatch.StartNew();
            using (var r = (HttpWebResponse)req.GetResponse())
            {
                sw.Stop();
                s.Ms = sw.ElapsedMilliseconds;
                s.Ok = (r.StatusCode == HttpStatusCode.OK);
            }
        }
        catch
        {
            s.Ok = false;
        }
    }

    // ---------------- 本地已有的包 ----------------

    /// 在若干目录里找本机架构对应的引擎包。
    /// 找不到精确文件名时，用通配再找一遍 —— 上游换日期后不用改代码。
    public static string FindLocalKit(string archKey, out string searched)
    {
        string exact = KitFileName(archKey);
        string tag = ArchTag(archKey);
        var dirs = new List<string>();

        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads"); } catch { }
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { dirs.Add(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath)); } catch { }
        try { dirs.Add(Environment.CurrentDirectory); } catch { }

        var sb = new StringBuilder();
        foreach (string d in dirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            sb.Append(d).Append("; ");

            try
            {
                if (exact.Length > 0)
                {
                    string p = Path.Combine(d, exact);
                    if (File.Exists(p)) { searched = sb.ToString(); return p; }
                }
                if (tag.Length > 0)
                {
                    string[] hits = Directory.GetFiles(d, "infer-engine-" + tag + "-*.zip");
                    if (hits.Length > 0) { searched = sb.ToString(); return hits[0]; }
                }
            }
            catch { }
        }

        searched = sb.ToString();
        return "";
    }

    /// 包名是否与本机架构匹配（防止用户选错文件）
    public static bool Matches(string path, string archKey)
    {
        string tag = ArchTag(archKey);
        if (tag.Length == 0 || string.IsNullOrEmpty(path)) return false;
        string n = Path.GetFileName(path).ToLowerInvariant();
        return n.Contains("infer-engine-" + tag);
    }

    /// 包是不是一个像样的引擎包（粗略判断：大小至少 100 MB）
    public static bool LooksLikeKit(string path, out long bytes)
    {
        bytes = 0;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            bytes = new FileInfo(path).Length;
            return bytes > 100L * 1024 * 1024;
        }
        catch { return false; }
    }
}
