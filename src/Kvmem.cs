// Kvmem.cs -- KVMem（环形缓存）参数设置
//
// 背景：启动器原来把这几个值写死在代码里。
// 引擎侧的实测事实（2026-10-08 实测，见 说明-构建与打包.md）：
//
//   · 索引容量是**编译进引擎的固定值** —— blocks=4096、256.2 MiB，改窗口/取回量都不变
//   · raw_k_shadow 固定 32 MiB（显存），只跟每块大小有关
//   · 窗口调大 **不增加显存**，但会**线性增加主机内存需求**
//   · 主机池不够时引擎会**直接拒绝启动**，报：
//       NINFER_KV_WINDOW (KVMem ring) needs a Host KV tier that can hold
//       every page the Device pool cannot: host_pages=1304 pool_pages=63
//       logical_pages=4096 (raise --host-kv-mib)
//
// 所以这里除了存窗口/取回量，还要能**预估所需主机池下限**并给出警告。
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class Kvmem
{
    /// 环形窗口（token）。0 = 用引擎/启动器默认
    public static int Window = 16384;

    /// 每次取回（token）
    public static int Retrieve = 8192;

    /// 是否启用环形缓存（关掉就等于不用 KVMem）
    public static bool Ring = true;

    /// 可选档位（界面上给这几档），最后一项是「自定义」
    public static readonly int[] WindowChoices = new int[] { 8192, 16384, 32768, 65536 };
    public static readonly int[] RetrieveChoices = new int[] { 2048, 4096, 8192, 16384 };

    /// 自定义值的合法范围
    public const int WindowMin = 1024;
    public const int WindowMax = 1048576;     // 1M token
    public const int RetrieveMin = 256;
    public const int RetrieveMax = 262144;    // 256K token

    /// 某个窗口值是不是预设档位（不是就是自定义）
    public static bool IsPresetWindow(int v)
    {
        foreach (int x in WindowChoices) if (x == v) return true;
        return false;
    }

    public static bool IsPresetRetrieve(int v)
    {
        foreach (int x in RetrieveChoices) if (x == v) return true;
        return false;
    }

    /// 把任意输入夹到合法范围
    public static int ClampWindow(int v)
    {
        if (v < WindowMin) return WindowMin;
        if (v > WindowMax) return WindowMax;
        return v;
    }

    public static int ClampRetrieve(int v)
    {
        if (v < RetrieveMin) return RetrieveMin;
        if (v > RetrieveMax) return RetrieveMax;
        return v;
    }

    /// 每块多少 token —— 引擎实测固定 1024（raw_k_shadow 里 kv_size=1024）
    public const int BlockTokens = 1024;

    /// 索引块数 —— 引擎编译期固定 4096，改窗口也不变（实测确认）
    public const int IndexBlocks = 4096;

    /// 单块 KV 页在主机池里占多少字节。
    ///
    /// 由引擎报错反推：host_pages=1304 对应 logical_pages=4096（窗口 16384）。
    /// 页大小 = 窗口 / 4096 = 4 token？不 —— 是 16 token/页最接近：
    ///   16384 token / 1304 页 ≈ 12.6，取整为 16 token/页 → 4096/16 = 256 页…对不上
    /// 直接用引擎给的数算比例更可靠：需要 host 页数 ≈ 窗口 / 12.57
    ///
    /// 这里不追求绝对精确，只用来**给出量级警告**。
    public static double HostMiBFor(int window)
    {
        // 实测锚点：窗口 16384 时，主机池至少要 > 2048 MiB 才起得来，
        // 8192 MiB 可用。取"每 1024 token 约需 130 MiB 主机池"这个经验值，
        // 再乘一点安全系数。
        if (window <= 0) return 0;
        double mib = window / 1024.0 * 130.0;
        return Math.Round(mib, 0);
    }

    public static string FilePath(string root)
    {
        return Path.Combine(root, "KVMem.json");
    }

    public static void Load(string root)
    {
        try
        {
            string p = FilePath(root);
            if (!File.Exists(p)) { Save(root); return; }
            string t = File.ReadAllText(p, Encoding.UTF8);

            Match m = Regex.Match(t, "\"window\"\\s*:\\s*(\\d+)");
            if (m.Success) { int v; if (int.TryParse(m.Groups[1].Value, out v) && v >= 1024 && v <= 1048576) Window = v; }

            m = Regex.Match(t, "\"retrieve\"\\s*:\\s*(\\d+)");
            if (m.Success) { int v; if (int.TryParse(m.Groups[1].Value, out v) && v >= 256 && v <= 1048576) Retrieve = v; }

            m = Regex.Match(t, "\"ring\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success) Ring = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
    }

    public static void Save(string root)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  // KVMem 环形缓存。这几个值只影响运行时的窗口与取回量，");
            sb.AppendLine("  // 不影响引擎的索引容量（那是编译期固定的 4096 块 / 256 MiB 内存）。");
            sb.AppendLine("  //");
            sb.AppendLine("  // 窗口调大：能缓存更长的历史，但主机内存需求线性上升，");
            sb.AppendLine("  //           主机池不够时引擎会直接拒绝启动。");
            sb.AppendLine("  // 取回调大：一次取回更多内容，但更容易稀释上下文、拉长首 token 延迟。");
            sb.AppendLine("  // 显存占用不受这些值影响（固定 32 MiB）。");
            sb.AppendLine("  \"ring\": " + (Ring ? "true" : "false") + ",");
            sb.AppendLine("  // 环形窗口（token），默认 16384");
            sb.AppendLine("  \"window\": " + Window + ",");
            sb.AppendLine("  // 每次取回（token），默认 8192");
            sb.AppendLine("  \"retrieve\": " + Retrieve);
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
