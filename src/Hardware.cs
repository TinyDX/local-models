// Hardware.cs -- 显卡探测（启动器侧的最小版本）
//
// 逻辑与安装器的 Hardware 一致：调 nvidia-smi 查 计算能力/显存/驱动版本。
// 启动器只需要一项：计算能力 —— 用来在 launch-config.json 缺失时判断该用哪个引擎。
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

internal static class Hardware
{
    /// 返回计算能力字符串，如 "8.6" / "8.9" / "12.0"；探测失败返回空串
    public static string ComputeCap()
    {
        string o = Run("nvidia-smi",
            "--query-gpu=compute_cap --format=csv,noheader");
        o = o.Trim();
        if (o.Length == 0) return "";
        // 可能多卡，取第一行
        string first = o.Split('\n')[0].Trim();
        Match m = Regex.Match(first, @"(\d+)\.(\d+)");
        return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : "";
    }

    /// 返回显存 MiB；失败返回 0
    public static int VramMiB()
    {
        string o = Run("nvidia-smi",
            "--query-gpu=memory.total --format=csv,noheader").Trim();
        if (o.Length == 0) return 0;
        Match m = Regex.Match(o.Split('\n')[0], @"(\d+)");
        int v;
        return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : 0;
    }

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return o ?? "";
            }
        }
        catch { return ""; }
    }
}
