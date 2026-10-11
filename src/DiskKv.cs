// DiskKv.cs -- 磁盘 KV 缓存设置
//
// 背景：引擎的 KV 池有三个层级
//   · 设备池（显存）—— 最快，但 8 GB 卡只放得下 4032 token
//   · 主机池（内存）—— 慢一点，钉住 8192 MiB
//   · ★ 磁盘池 —— 最慢，但容量可以到几十 GB
//
// 开启磁盘层后，被换出的 KV 会写到磁盘，上下文长度改由**磁盘空间**决定，
// 于是能突破「显存 + 内存」的硬顶。代价是速度 —— 取回换出的 KV 要读盘。
//
// 对应的引擎参数：
//   --disk-kv-path DIR   换出的 KV 写到哪
//   --disk-kv-gib N      磁盘层预算
//   --disk-kv-restore    新请求匹配到前缀时从磁盘层恢复
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class DiskKv
{
    /// 是否启用磁盘 KV 层。默认关 —— 关着最快，只在需要超长上下文时开。
    public static bool Enabled = false;

    /// 缓存目录。空表示用安装目录下的 kv-cache\
    public static string Dir = "";

    /// 磁盘层预算（GB）。引擎默认 64。
    public static int Gib = 64;

    /// 容量可选档位
    public static readonly int[] SizeChoices = new int[] { 16, 32, 64, 128, 256 };

    public static string DefaultDir(string root)
    {
        return System.IO.Path.Combine(root, "kv-cache");
    }

    /// 实际要传给引擎的目录（空则取默认）
    public static string EffectiveDir(string root)
    {
        if (Dir != null && Dir.Trim().Length > 0) return Dir.Trim();
        return DefaultDir(root);
    }

    public static string FilePath(string root)
    {
        return System.IO.Path.Combine(root, "磁盘KV.json");
    }

    public static void Load(string root)
    {
        try
        {
            string p = FilePath(root);
            if (!File.Exists(p)) { Save(root); return; }
            string txt = File.ReadAllText(p, Encoding.UTF8);

            Match m = Regex.Match(txt, "\"enabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success) Enabled = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

            m = Regex.Match(txt, "\"dir\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) Dir = m.Groups[1].Value.Replace("\\\\", "\\");

            m = Regex.Match(txt, "\"gib\"\\s*:\\s*(\\d+)");
            if (m.Success)
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v) && v >= 1 && v <= 4096) Gib = v;
            }
        }
        catch { }
    }

    public static void Save(string root)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  // 磁盘 KV 缓存。开启后换出的 KV 写磁盘，上下文长度改由磁盘空间决定。");
            sb.AppendLine("  // 利：上下文可以远超显存/内存的容量，长对话不必反复重算前缀。");
            sb.AppendLine("  // 弊：速度明显下降（要读盘），占用磁盘空间且持续写入，建议放 SSD。");
            sb.AppendLine("  \"enabled\": " + (Enabled ? "true" : "false") + ",");
            sb.AppendLine("  // 缓存目录。留空表示用安装目录下的 kv-cache\\");
            sb.AppendLine("  \"dir\": \"" + (Dir == null ? "" : Dir.Replace("\\", "\\\\")) + "\",");
            sb.AppendLine("  // 磁盘层预算（GB），默认 64");
            sb.AppendLine("  \"gib\": " + Gib);
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
