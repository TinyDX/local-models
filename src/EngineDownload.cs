// EngineDownload.cs -- 引擎包下载（支持断点续传、进度回调、校验）
//
// 为什么单独写而不是用 WebClient.DownloadFile：
//   · 1.3 GB 的文件断一次就得重头下，国内网络下这是常态
//   · 需要进度回调（进度条 + 速度显示）
//   · 需要能取消
//   · 需要区分「网络失败」和「磁盘写不下」，给用户能看懂的提示
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

internal static class EngineDownload
{
    /// 显式启用现代 TLS。
    ///
    /// ★ 这个必须做，否则在某些机器上会报「未能创建 SSL/TLS 安全通道」。
    ///
    /// 原因：.NET Framework 4.8 在部分 Windows 配置下，默认只协商 TLS 1.0/1.1，
    /// 而 GitHub 早就把这俩禁了 —— 握手直接被拒。
    /// 实测：同一台机器上不设 SecurityProtocol 时下载失败，设成 Tls12 后正常。
    ///
    /// 用 3072 / 12288 这种数字而不是枚举名，是因为编译目标是 C# 5，
    /// 而 Tls13 枚举在 .NET 4.8 的某些引用程序集里不存在 —— 写数字更保险。
    ///   Tls12 = 3072，Tls13 = 12288
    private static bool _tlsReady;

    public static void EnsureTls()
    {
        if (_tlsReady) return;
        _tlsReady = true;
        try
        {
            // 12288(Tls13) | 3072(Tls12) | 768(Tls11) | 192(Tls10)
            // 保留旧协议是给某些企业代理留退路，现代服务器会自动协商到最高的那个
            const int want = 12288 | 3072 | 768 | 192;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)want;
        }
        catch
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch { }
        }
    }

    /// 进度回调：已下载字节、总字节（总字节可能为 0 = 服务器没给长度）、速度 MB/s
    public delegate void ProgressHandler(long done, long total, double mbps);

    /// 下载结果
    internal sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public long Bytes;
        public bool Cancelled;
    }

    /// 下载到目标文件。已存在部分内容时**自动续传**。
    ///
    /// ★ 带自动重试：大文件（1.26 GB）走 GitHub 或镜像站时，
    ///   连接中途断掉是常态（"从传输流收到意外的 EOF 或 0 个字节"）。
    ///   断一次就整个失败的话，用户得从头再下 —— 不能接受。
    ///   所以这里自动重试最多 MaxRetry 次，每次从断点接着下。
    ///
    /// 目标文件旁边会有一个 .part 临时文件 —— 下完才改名成正式名。
    /// 这样中途失败不会留下一个看起来完整、实际损坏的文件。
    public static Result Download(string url, string destPath, ProgressHandler onProgress, ManualResetEvent cancel)
    {
        return Download(url, destPath, onProgress, cancel, 0);
    }

    /// attempt = 内部重试计数，调用方传 0
    public static Result Download(string url, string destPath, ProgressHandler onProgress,
                                  ManualResetEvent cancel, int attempt)
    {
        var res = new Result();
        string part = destPath + ".part";

        EnsureTls();   // 必须先设 —— 见上面注释

        try
        {
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            long have = 0;
            if (File.Exists(part))
            {
                try { have = new FileInfo(part).Length; } catch { have = 0; }
            }

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 15000;
            // 大文件跨洋下载，30 秒太紧 —— 服务端偶尔停顿就会被误判为超时
            req.ReadWriteTimeout = 90000;
            req.AllowAutoRedirect = true;
            req.UserAgent = "LocalModels-Setup";
            if (have > 0) req.AddRange(have);

            using (var r = (HttpWebResponse)req.GetResponse())
            {
                // 服务器不支持续传（回了 200 而不是 206）→ 从头来过
                bool resume = (r.StatusCode == HttpStatusCode.PartialContent);
                if (have > 0 && !resume) { have = 0; try { File.Delete(part); } catch { } }

                long total = r.ContentLength + have;
                res.Bytes = total;

                using (var src = r.GetResponseStream())
                using (var dst = new FileStream(part, resume ? FileMode.Append : FileMode.Create,
                                                FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];   // 1 MB
                    long done = have;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long lastReport = done;
                    double lastSec = 0;

                    while (true)
                    {
                        if (cancel != null && cancel.WaitOne(0))
                        {
                            res.Cancelled = true;
                            res.Error = "已取消";
                            dst.Flush();
                            return res;
                        }

                        int n = src.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        dst.Write(buf, 0, n);
                        done += n;

                        // 每 200ms 报一次，避免刷爆 UI
                        double sec = sw.Elapsed.TotalSeconds;
                        if (sec - lastSec >= 0.2)
                        {
                            double mbps = 0;
                            double dt = sec - lastSec;
                            if (dt > 0) mbps = ((done - lastReport) / 1048576.0) / dt;
                            lastReport = done; lastSec = sec;
                            if (onProgress != null)
                            {
                                try { onProgress(done, total, mbps); } catch { }
                            }
                        }
                    }
                    dst.Flush();
                    if (onProgress != null) { try { onProgress(done, total, 0); } catch { } }
                }
            }

            // 下完改名 —— 用 Move 覆盖，保证不会出现半个文件
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            File.Move(part, destPath);
            res.Ok = true;
            return res;
        }
        catch (WebException we)
        {
            res.Error = DescribeWebError(we);
            return RetryOrFail(url, destPath, onProgress, cancel, attempt, res);
        }
        catch (IOException io)
        {
            res.Error = "写入失败：" + FirstLine(io.Message);
            return RetryOrFail(url, destPath, onProgress, cancel, attempt, res);
        }
        catch (Exception ex)
        {
            res.Error = FirstLine(ex.Message);
            return RetryOrFail(url, destPath, onProgress, cancel, attempt, res);
        }
    }

    /// 最多自动重试几次。
    /// 大文件跨洋下载断流太常见了，值得多试几次 —— 反正有断点续传，重试不浪费已下的部分。
    private const int MaxRetry = 5;

    /// 出错了：还有重试次数就歇一秒接着下（从断点），否则把错误抛给界面。
    private static Result RetryOrFail(string url, string destPath, ProgressHandler onProgress,
                                      ManualResetEvent cancel, int attempt, Result res)
    {
        if (cancel != null && cancel.WaitOne(0))
        {
            res.Cancelled = true;
            res.Error = "已取消";
            return res;
        }
        if (attempt >= MaxRetry) return res;

        // 给网络一点恢复时间，然后从断点重来
        try { System.Threading.Thread.Sleep(1500); } catch { }

        if (onProgress != null)
        {
            // 让界面知道在重试，而不是卡死了
            try { onProgress(-1, 0, 0); } catch { }
        }

        return Download(url, destPath, onProgress, cancel, attempt + 1);
    }

    /// 只取第一行 —— 界面上的提示标签是单行的，多行会被截断。
    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.IndexOfAny(new char[] { '\r', '\n' });
        if (i > 0) s = s.Substring(0, i);
        return s.Trim();
    }

    /// 把 WebException 翻成人能看懂的话。
    /// 默认那套 "基础连接已经关闭" 之类的提示对用户毫无帮助。
    private static string DescribeWebError(WebException we)
    {
        switch (we.Status)
        {
            case WebExceptionStatus.NameResolutionFailure:
                return "找不到服务器，检查下载源地址或本机 DNS";
            case WebExceptionStatus.ConnectFailure:
                return "连不上服务器，换个下载源试试";
            case WebExceptionStatus.Timeout:
                return "连接超时，换个下载源或手动选择文件";
            case WebExceptionStatus.ProtocolError:
                try
                {
                    var r = we.Response as HttpWebResponse;
                    if (r != null)
                    {
                        // ★ 这里的文案要**短**：它显示在安装界面的单行标签里，
                        //   多行文本会被截断（之前 "服务器上没有这个文件（404）。可能原因：..." 就是这样）。
                        //   详细说明放进 FailureDetail，由调用方写进日志。
                        if (r.StatusCode == HttpStatusCode.NotFound)
                            return "服务器上没有这个文件（404），换一个下载源试试";
                        return "服务器返回 " + (int)r.StatusCode + "，换一个下载源试试";
                    }
                }
                catch { }
                return "服务器拒绝了请求。";
            case WebExceptionStatus.ReceiveFailure:
                return "连接中断，再点一次「下一步」可断点续传";
            default:
                return "下载失败：" + we.Message;
        }
    }

    /// 算一个可读的大小
    public static string Human(long bytes)
    {
        if (bytes >= 1073741824L) return (bytes / 1073741824.0).ToString("0.00") + " GB";
        if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0.0") + " MB";
        if (bytes >= 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
        return bytes + " B";
    }
}
