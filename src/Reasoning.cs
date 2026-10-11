// Reasoning.cs -- 推理（思考）等级设置
//
// 背景：思考开关本来是网页端的一个勾选框，靠请求里的 reasoning_effort 控制。
// 但第三方聊天客户端大多没有「推理等级」这个设置，或者有也不生效 ——
// 于是在程序里放一个全局设置：客户端没指定时，由代理补上这一档。
//
// 优先级：客户端自己发了 reasoning_effort → 用客户端的
//         客户端没发                     → 用这里的全局设置
//         模型名带 -think 别名           → 用别名里的档位（最高优先级）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal static class Reasoning
{
    /// 全部可选档位。"none" 表示关闭思考。
    public static readonly string[] Levels =
        new string[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    /// 界面上的中文名
    public static string Label(string lv)
    {
        if (lv == "none") return "关闭（默认，最快）";
        if (lv == "minimal") return "minimal（最少）";
        if (lv == "low") return "low";
        if (lv == "medium") return "medium（中等）";
        if (lv == "high") return "high";
        if (lv == "xhigh") return "xhigh";
        if (lv == "max") return "max（想得最久最细，最慢）";
        return lv;
    }

    /// 当前档位。默认关闭 —— 与引擎的 --default-reasoning-effort none 保持一致。
    public static string Effort = "none";

    public static string FilePath(string root)
    {
        return Path.Combine(root, "推理等级.json");
    }

    public static void Load(string root)
    {
        try
        {
            string p = FilePath(root);
            if (!File.Exists(p)) { Save(root); return; }
            string txt = File.ReadAllText(p, Encoding.UTF8);
            Match m = Regex.Match(txt, "\"effort\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success)
            {
                string v = m.Groups[1].Value.Trim().ToLowerInvariant();
                foreach (string lv in Levels)
                    if (lv == v) { Effort = v; return; }
                Effort = "none";
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
            sb.AppendLine("  // 全局推理等级。客户端自己带了 reasoning_effort 时以客户端的为准。");
            sb.AppendLine("  // 可选：none（关闭）/ minimal / low / medium / high / xhigh / max");
            sb.AppendLine("  \"effort\": \"" + Effort + "\"");
            sb.AppendLine("}");
            File.WriteAllText(FilePath(root), sb.ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}
