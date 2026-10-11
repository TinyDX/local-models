// EngineKey.cs -- 引擎 API 密钥（首次运行自动生成）
//
// 为什么要有它：
//   旧版把密钥硬编码成 "bonsai-local-7f3a9c21"。
//   自己用没问题，但开源之后所有人共用同一个默认值 ——
//   虽然是本机回环地址、不对外网暴露，但同一局域网里谁都能连上。
//
// 现在改成：首次运行生成一条随机密钥，存进安装目录的「引擎密钥.json」。
//   之后每次启动都读同一个文件，保证程序、引擎、网页三者用的是同一条。
//
// 兼容性：**旧的硬编码密钥继续被接受**。
//   老用户升级后，客户端（手机、别的电脑）里存着旧 key，
//   如果直接作废，他们全部会 401。所以旧 key 作为「兼容密钥」保留。
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class EngineKey
{
    /// 旧版硬编码的密钥 —— 保留用于兼容，不作为新装的默认值。
    internal const string LegacyKey = "bonsai-local-7f3a9c21";

    private static readonly object _lock = new object();
    private static string _key = "";

    /// 当前生效的密钥。首次取值时若还没初始化，会生成一条临时的
    /// （正常流程是 App 启动时调 Ensure(root)，这里只是兜底）。
    public static string Current
    {
        get
        {
            lock (_lock)
            {
                if (_key.Length == 0) _key = NewKey();
                return _key;
            }
        }
    }

    public static string FilePath(string root)
    {
        return Path.Combine(root, "引擎密钥.json");
    }

    /// 读取（不存在就生成）密钥。**程序启动时必须调一次**。
    /// root = 安装目录。
    public static string Ensure(string root)
    {
        lock (_lock)
        {
            if (_key.Length > 0) return _key;

            try
            {
                string p = FilePath(root);
                if (File.Exists(p))
                {
                    string txt = File.ReadAllText(p, Encoding.UTF8);
                    Match m = Regex.Match(txt, "\"key\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success)
                    {
                        string k = m.Groups[1].Value.Trim();
                        if (LooksValid(k)) { _key = k; return _key; }
                    }
                }

                // 没有文件 / 内容坏了 → 生成新的并落盘
                _key = NewKey();
                Save(root);
                return _key;
            }
            catch
            {
                // 读不了也不能让程序起不来：内存里留一份，这次运行照样能用
                if (_key.Length == 0) _key = NewKey();
                return _key;
            }
        }
    }

    public static void Save(string root)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  // 引擎与网页代理共用的 API 密钥。首次运行时自动生成，请勿随意修改。");
            sb.AppendLine("  // 改掉之后，已经用旧密钥连接的客户端需要重新填。");
            sb.AppendLine("  // 旧版内置密钥（" + LegacyKey + "）仍然兼容，不会被作废。");
            sb.AppendLine("  \"key\": \"" + _key + "\"");
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }

    /// 客户端传来的 key 是否放行。
    /// 当前密钥、旧版内置密钥、用户自己登记的（ApiKeys）都算通过。
    public static bool Accept(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (string.Equals(token, Current, StringComparison.Ordinal)) return true;
        if (string.Equals(token, LegacyKey, StringComparison.Ordinal)) return true;
        return ApiKeys.IsKnown(token);
    }

    /// 生成一条随机密钥，形如 sk-lm-<16位十六进制>
    public static string NewKey()
    {
        var b = new byte[8];
        try
        {
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(b);
        }
        catch
        {
            // 极端情况下退化成时间 + 进程号派生，至少不是固定值
            long t = DateTime.UtcNow.Ticks;
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            for (int i = 0; i < 8; i++)
                b[i] = (byte)((t >> (i * 8)) ^ (pid << (i % 4)));
        }

        var sb = new StringBuilder("sk-lm-");
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    /// 密钥写法的合法性：非空、无空白字符
    public static bool LooksValid(string key)
    {
        string t = (key ?? "").Trim();
        if (t.Length == 0) return false;
        foreach (char c in t)
            if (char.IsWhiteSpace(c)) return false;
        return true;
    }
}
