// ContextGuard.cs -- 检测 prompt 超出 KV 池的情况并提醒
//
// 为什么需要它：
//   引擎在 prompt 超过设备池时会打一条 warning，但**返回 HTTP 200**，
//   而且引擎自己说：超出池子那部分的**中间内容会静默丢失**，
//   给出一个看起来正常、实际错的答案。
//   用户完全看不出来 —— 这是最危险的一类失败。
//
// 做法：定期扫 engine.log 里的那条 warning，把「最近发生过」的状态显示在界面上。
//   不阻止请求（用户可能明知在超限使用），但必须让他知道「这答案不可信」。
using System;
using System.IO;
using System.Text.RegularExpressions;

internal static class ContextGuard
{
    /// 最近一次超池的 prompt token 数（0 = 没发生过 / 已清空）
    public static long LastOverflowTokens;

    /// 最近一次超池时设备池有多大
    public static long LastPoolTokens;

    /// 超池发生的时间
    public static DateTime LastOverflowAt = DateTime.MinValue;

    /// 清空状态（卸载模型 / 重新载入时调）
    public static void Reset()
    {
        LastOverflowTokens = 0;
        LastPoolTokens = 0;
        LastOverflowAt = DateTime.MinValue;
    }

    /// 给人看的一句提醒
    public static string WarnText()
    {
        if (LastOverflowTokens <= 0) return "";
        return "⚠ 上次请求的上下文有 " + LastOverflowTokens.ToString("N0")
             + " token，超过设备池的 " + LastPoolTokens.ToString("N0")
             + " token —— 超出部分的中间内容会被丢弃，回答可能不可信。"
             + "建议缩短对话，或在设置里调大 KV 池。";
    }

    /// 引擎吐出一行日志时调用。只有超池那条 warning 会被传进来。
    /// 每秒可能来好几次，所以这里只做正则匹配，不碰磁盘。
    public static bool NoteLine(string line)
    {
        try
        {
            // 形如：prompt 19370 tokens (303 pages) > pool 17920 tokens (280 pages)
            Match m = Regex.Match(line, @"prompt\s+([\d,]+)\s+tokens.*?>\s*pool\s+([\d,]+)\s+tokens");
            if (!m.Success) return false;

            long ptok = 0, pool = 0;
            long.TryParse(m.Groups[1].Value.Replace(",", ""), out ptok);
            long.TryParse(m.Groups[2].Value.Replace(",", ""), out pool);
            if (ptok <= 0) return false;

            // 同一件事不重复报（引擎每 30 秒会重复打同一条）
            if (ptok == LastOverflowTokens) return false;

            LastOverflowTokens = ptok;
            LastPoolTokens = pool;
            LastOverflowAt = DateTime.Now;
            return true;
        }
        catch { return false; }
    }
}
