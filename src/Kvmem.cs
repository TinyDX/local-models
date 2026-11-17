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

    /// 每页多少 token。
    ///
    /// 引擎日志实测（2026-10-20，RTX 4070 Ti Super）：
    ///   capacity | KV 17,920 tokens, k8v4 | pages 280/4,096
    ///   → 17,920 / 280 = 64 token/页
    /// 上游 profiles.json 里也写着 "page_tokens": 64 —— 跨显卡固定。
    public const int TokensPerPage = 64;

    /// 环形窗口需要多少页
    public static int PagesFor(int windowTokens)
    {
        if (windowTokens <= 0) return 0;
        return (windowTokens + TokensPerPage - 1) / TokensPerPage;
    }

    /// 设备池有多少页
    public static int DevicePoolPages(int kvCapacityTokens)
    {
        if (kvCapacityTokens <= 0) return 0;
        return (kvCapacityTokens + TokensPerPage - 1) / TokensPerPage;
    }

    /// 检查「环绕窗口 vs 设备池」是否合理。
    ///
    /// ★ 先说清楚一个前提：
    ///   环形窗口**本来就该大于设备池** —— 它的作用就是把装不下的历史
    ///   换出到主机池、需要时再取回。窗口 ≤ 池子的话根本不需要环。
    ///   所以「窗口比池子大」是正常设计，不能当错误报。
    ///
    /// 真正会出事的情况：**窗口大得让引擎预留页数远超池子容量**，
    /// 长对话时换页周转不过来，报：
    ///   context cache exhausted: Paged KV pool has no free page for host-restore
    ///   usable 280 (capacity 280, lent 0), allocated 280, reserved 1971
    ///
    /// 实测（2026-10-20，RTX 4070 Ti Super，池子 17,920 token = 280 页）：
    ///   窗口 16384（256 页，0.9×池子）→ 引擎 reserved 692 页，**报过错**
    ///   窗口 65536（1024 页，3.7×池子）→ 引擎 reserved 1971 页，报过错
    ///   → 这个用户即使 16384 也踩了，因为他的 prompt 有 15 万 token。
    ///     窗口只是**次要因素**，主因是 prompt 长度。
    ///
    /// 所以判据用 **4 倍**：
    ///   只提醒"窗口相对池子大得离谱"的配置（本该配磁盘 KV 的场景），
    ///   默认档位（16384 / 8192）一律不打扰 —— 天天弹警告等于没警告。
    ///
    /// 返回给用户看的提示文字（空串 = 没问题）。
    public static string CheckWindow(int windowTokens, int kvCapacityTokens)
    {
        if (windowTokens <= 0 || kvCapacityTokens <= 0) return "";

        int winPages = PagesFor(windowTokens);
        int poolPages = DevicePoolPages(kvCapacityTokens);
        if (poolPages <= 0) return "";

        // 4 倍以内视为正常（环形缓存的设计本意）
        double ratio = (double)winPages / poolPages;
        if (ratio <= 4.0) return "";

        string s = "⚠ 环绕窗口 " + windowTokens.ToString("N0") + " token（约 "
                 + winPages.ToString("N0") + " 页）是设备池（"
                 + kvCapacityTokens.ToString("N0") + " token，"
                 + poolPages.ToString("N0") + " 页）的 "
                 + ratio.ToString("0.0") + " 倍，偏大。\r\n"
                 + "  长对话（尤其带工具的多轮对话）可能报「context cache exhausted」，\r\n"
                 + "  且超出设备池的部分内容会被静默丢弃。\r\n"
                 + "  建议：";

        if (poolPages >= 128)
        {
            int suggest = (kvCapacityTokens / 1024) * 1024;
            if (suggest < 1024) suggest = 1024;
            s += "降到 " + suggest.ToString("N0") + " token 以内，或开启「磁盘 KV 缓存」。";
        }
        else
        {
            s += "开启「磁盘 KV 缓存」（设置 → 磁盘 KV 缓存），或缩短对话、少挂 tools。";
        }
        return s;
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
