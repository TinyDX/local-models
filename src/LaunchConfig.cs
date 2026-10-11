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
            c.VramMiB = GrabInt(t, "vramMiB");
            int v;
            v = GrabInt(t, "kvCapacity"); if (v > 0) c.KvCapacity = v;
            v = GrabInt(t, "hostKvMiB"); if (v > 0) c.HostKvMiB = v;
            v = GrabInt(t, "graphAllowanceMiB"); if (v >= 0) c.GraphAllowanceMiB = v;
        }
        catch { }
        return c;
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
}
