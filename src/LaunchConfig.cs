// LaunchConfig.cs -- 读安装器写出的 launch-config.json
//
// 安装器在安装目录下生成这个文件，记录本机探测结果（引擎文件名、池参数等）。
// 启动器启动时读它，从而不必写死任何机器相关常量。
using System;
using System.IO;
using System.Text.RegularExpressions;

internal sealed class LaunchConfig
{
    public string EngineExe = "";
    public string Arch = "";
    public string Gpu = "";
    public int VramMiB;
    public string Driver = "";
    public int KvCapacity = 4032;
    public int HostKvMiB = 8192;
    public int GraphAllowanceMiB = 64;
    public string Note = "";

    /// 是否禁用 CUDA 图（对应引擎参数 --no-cuda-graph）。
    ///
    /// 为什么需要这个开关：
    ///   CUDA 图是启动时捕获一次、之后反复执行的加速手段。驱动升级后
    ///   内核参数布局可能变化，导致 cudaGraphExecUpdate 失败：
    ///     CUDA Graph executable update failed: cudaErrorGraphExecUpdateFailure (update result 5)
    ///   引擎随即 FATAL 退出，模型完全载不起来
    ///   （实测：NVIDIA 621.14 驱动 + sm120 引擎，2026-11-05）。
    ///   关掉图的推理速度代价实测约等于 0，所以引擎一失败就自动切到这个模式，
    ///   并把结果写回本文件 —— 下次启动直接生效，不必再白失败一次。
    public bool NoCudaGraph;

    public static LaunchConfig Load(string root)
    {
        var c = new LaunchConfig();
        try
        {
            string f = Path.Combine(root, "launch-config.json");
            if (!File.Exists(f)) return c;
            string t = File.ReadAllText(f);
            c.EngineExe = Grab(t, "engineExe");
            c.Arch = Grab(t, "arch");
            c.Gpu = Grab(t, "gpu");
            c.Driver = Grab(t, "driver");
            c.Note = Grab(t, "note");
            c.NoCudaGraph = GrabBool(t, "noCudaGraph");
            c.VramMiB = GrabInt(t, "vramMiB");
            int v;
            v = GrabInt(t, "kvCapacity"); if (v > 0) c.KvCapacity = v;
            v = GrabInt(t, "hostKvMiB"); if (v > 0) c.HostKvMiB = v;
            v = GrabInt(t, "graphAllowanceMiB"); if (v >= 0) c.GraphAllowanceMiB = v;
        }
        catch { }
        return c;
    }

    /// 把「禁用 CUDA 图」写回 launch-config.json。
    /// 只动这一个键，其它字段原样保留（安装器写的档位信息不能被覆盖）。
    public static bool SaveNoCudaGraph(string root, bool value)
    {
        try
        {
            string f = Path.Combine(root, "launch-config.json");
            if (!File.Exists(f)) return false;
            string t = File.ReadAllText(f);

            // 先删掉已有的键（可能带尾逗号）
            t = Regex.Replace(t, "\\s*\"noCudaGraph\"\\s*:\\s*(true|false)\\s*,?", "");

            int idx = t.LastIndexOf('}');
            if (idx < 0) return false;

            string head = t.Substring(0, idx).TrimEnd();
            string tail = t.Substring(idx);

            // 前一个字段没有尾逗号时要补上
            if (head.Length > 0 && head[head.Length - 1] != ',') head += ",";

            string rebuilt = head + Environment.NewLine
                           + "  \"noCudaGraph\": " + (value ? "true" : "false") + Environment.NewLine
                           + tail;
            File.WriteAllText(f, rebuilt, new System.Text.UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    private static string Grab(string t, string k)
    {
        Match m = Regex.Match(t, "\"" + k + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static int GrabInt(string t, string k)
    {
        Match m = Regex.Match(t, "\"" + k + "\"\\s*:\\s*(-?\\d+)");
        int v;
        return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : 0;
    }

    private static bool GrabBool(string t, string k)
    {
        Match m = Regex.Match(t, "\"" + k + "\"\\s*:\\s*(true|false)");
        return m.Success && string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}