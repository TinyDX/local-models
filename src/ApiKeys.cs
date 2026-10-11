// ApiKeys.cs -- 自定义 API 密钥（可多条）
//
// 为什么要这一层：引擎自己只认一个 --api-key（写死在启动参数里），
// 没法给不同设备发不同的 key。所以在前面的网页代理里加一层校验：
//   客户端拿任意一条登记过的 key → 代理解析通过 → 换成引擎的 key 转发
//   没登记过的 key → 直接 401，不打扰引擎
//
// 好处：可以给手机一条、电脑一条、朋友一条，随时删掉某一条就等于吊销。
//
// 配置存在程序目录的「API密钥.json」，设置页里直接编辑，保存后立即生效。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class ApiKeys
{
    /// 用户自己加的密钥（不含程序内置的那条）
    public static List<string> Keys = new List<string>();

    public static string FilePath(string root)
    {
        return Path.Combine(root, "API密钥.json");
    }

    public static void Load(string root)
    {
        try
        {
            string p = FilePath(root);
            if (!File.Exists(p))
            {
                Save(root);          // 首次运行把默认（空）配置写出来，方便查看
                return;
            }
            string txt = File.ReadAllText(p, Encoding.UTF8);
            Keys.Clear();
            Match m = Regex.Match(txt, "\"keys\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (m.Success)
                foreach (Match x in Regex.Matches(m.Groups[1].Value, "\"([^\"]*)\""))
                {
                    string s = x.Groups[1].Value.Trim();
                    if (s.Length > 0) Keys.Add(s);
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
            sb.AppendLine("  // 自定义 API 密钥，一条一个。可以给不同设备发不同的 key，删掉一条即吊销。");
            sb.AppendLine("  // 程序内置的 key 一直有效，不在这里显示。");
            sb.AppendLine("  \"keys\": [");
            for (int i = 0; i < Keys.Count; i++)
                sb.AppendLine("    \"" + Keys[i] + "\"" + (i < Keys.Count - 1 ? "," : ""));
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// 这条 key 是否已登记（内置那条由调用方另外判断）
    public static bool IsKnown(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (string k in Keys)
            if (string.Equals(k, key, StringComparison.Ordinal)) return true;
        return false;
    }

    /// 生成一条看起来像 API key 的随机串：sk-lm-<16位十六进制>
    public static string Generate()
    {
        var b = new byte[8];
        using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
            rng.GetBytes(b);
        var sb = new StringBuilder("sk-lm-");
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    /// 合法写法校验：非空、不含空白
    public static bool LooksValid(string key)
    {
        string t = (key ?? "").Trim();
        if (t.Length == 0) return false;
        foreach (char c in t)
            if (char.IsWhiteSpace(c)) return false;
        return true;
    }
}
