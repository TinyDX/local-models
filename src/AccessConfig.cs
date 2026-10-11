// AccessConfig.cs -- 网页端局域网访问控制（公开 / 指定 IP）
//
// 为什么需要它：网页端本身不带密码，一旦放行到局域网，
// 同一网段里任何人都能用。这里给一个白名单，只允许列出的设备访问。
//
// 配置存在程序目录的「访问控制.json」，界面上直接编辑，保存后立即生效（无需重开）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class AccessConfig
{
    /// true = 公开（任意设备都能访问）；false = 只允许白名单内的 IP
    public static bool Public = true;

    /// 白名单。每项可以是：
    ///   单个 IP      192.168.1.7
    ///   网段         192.168.1.0/24
    ///   通配         192.168.1.*
    public static List<string> Allow = new List<string>();

    public static string FilePath(string root)
    {
        return Path.Combine(root, "访问控制.json");
    }

    public static void Load(string root)
    {
        try
        {
            string p = FilePath(root);
            if (!File.Exists(p))
            {
                Save(root);          // 首次运行把默认配置写出来，方便查看和手改
                return;
            }
            string txt = File.ReadAllText(p, Encoding.UTF8);

            Match m = Regex.Match(txt, "\"public\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            if (m.Success) Public = m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

            Allow.Clear();
            Match am = Regex.Match(txt, "\"allow\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (am.Success)
                foreach (Match x in Regex.Matches(am.Groups[1].Value, "\"([^\"]*)\""))
                {
                    string s = x.Groups[1].Value.Trim();
                    if (s.Length > 0) Allow.Add(s);
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
            sb.AppendLine("  // true = 公开（任意设备都能访问）；false = 只允许 allow 里列出的 IP");
            sb.AppendLine("  // allow 每项可以写：单个 IP（192.168.1.7）、网段（192.168.1.0/24）、通配（192.168.1.*）");
            sb.AppendLine("  \"public\": " + (Public ? "true" : "false") + ",");
            sb.AppendLine("  \"allow\": [");
            for (int i = 0; i < Allow.Count; i++)
                sb.AppendLine("    \"" + Allow[i] + "\"" + (i < Allow.Count - 1 ? "," : ""));
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// 这个来源 IP 允许访问吗
    public static bool IsAllowed(string ip)
    {
        if (Public) return true;
        if (string.IsNullOrEmpty(ip)) return false;
        ip = Normalize(ip);
        foreach (string raw in Allow)
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;
            if (t == "*") return true;
            if (t.Equals(ip, StringComparison.OrdinalIgnoreCase)) return true;
            if (t.IndexOf('/') > 0 && InCidr(ip, t)) return true;
            if (t.EndsWith("*"))
            {
                string pre = t.Substring(0, t.Length - 1);
                if (ip.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// 把 IPv6 映射地址（::ffff:192.168.1.7）还原成 IPv4，便于和白名单比对
    public static string Normalize(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return ip;
        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) return ip.Substring(7);
        return ip;
    }

    /// 合法写法校验（用于界面提示），空串表示删除
    public static bool LooksValid(string entry)
    {
        string t = (entry ?? "").Trim();
        if (t.Length == 0) return true;
        if (t == "*") return true;
        if (t.EndsWith("*")) t = t.Substring(0, t.Length - 1) + "0";
        int slash = t.IndexOf('/');
        if (slash > 0)
        {
            int bits;
            if (!int.TryParse(t.Substring(slash + 1), out bits)) return false;
            if (bits < 0 || bits > 32) return false;
            t = t.Substring(0, slash);
        }
        if (t.EndsWith(".")) t = t.Substring(0, t.Length - 1) + "0";
        System.Net.IPAddress a;
        return System.Net.IPAddress.TryParse(t, out a) && a.AddressFamily ==
               System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private static bool InCidr(string ip, string cidr)
    {
        try
        {
            string[] parts = cidr.Split('/');
            if (parts.Length != 2) return false;
            uint a = ToUint(ip), b = ToUint(parts[0].Trim());
            if (a == 0 || b == 0) return false;
            int bits;
            if (!int.TryParse(parts[1].Trim(), out bits)) return false;
            if (bits <= 0) return true;
            if (bits > 32) bits = 32;
            uint mask = 0xFFFFFFFFu << (32 - bits);
            return (a & mask) == (b & mask);
        }
        catch { return false; }
    }

    private static uint ToUint(string ip)
    {
        System.Net.IPAddress a;
        if (!System.Net.IPAddress.TryParse(Normalize(ip), out a)) return 0;
        byte[] b = a.GetAddressBytes();
        if (b.Length != 4) return 0;
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
