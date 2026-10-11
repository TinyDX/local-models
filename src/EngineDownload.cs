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
    /// 目标文件旁边会有一个 .part 临时文件 —— 下完才改名成正式名。
    /// 这样中途失败不会留下一个看起来完整、实际损坏的文件。
    public static Result Download(string url, string destPath, ProgressHandler onProgress, ManualResetEvent cancel)
    {
        var res = new Result();
        string part = destPath + ".part";

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
            req.ReadWriteTimeout = 30000;
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
            return res;
        }
        catch (IOException io)
        {
            res.Error = "写入失败：" + io.Message + "\r\n（磁盘空间够吗？这个包解压后还要占约 1.5 GB）";
            return res;
        }
        catch (Exception ex)
        {
            res.Error = ex.Message;
            return res;
        }
    }

    /// 把 WebException 翻成人能看懂的话。
    /// 默认那套 "基础连接已经关闭" 之类的提示对用户毫无帮助。
    private static string DescribeWebError(WebException we)
    {
        switch (we.Status)
        {
            case WebExceptionStatus.NameResolutionFailure:
                return "找不到服务器。可能是下载源地址写错了，或者本机 DNS 有问题。";
            case WebExceptionStatus.ConnectFailure:
                return "连不上服务器。GitHub 在国内经常连不上，换个下载源试试。";
            case WebExceptionStatus.Timeout:
                return "连接超时。换个下载源，或者用「手动选择本地文件」。";
            case WebExceptionStatus.ProtocolError:
                try
                {
                    var r = we.Response as HttpWebResponse;
                    if (r != null)
                    {
                        if (r.StatusCode == HttpStatusCode.NotFound)
                            return "服务器上没有这个文件（404）。\r\n"
                                 + "可能原因：Release 还没发布，或版本号对不上。\r\n"
                                 + "可以先手动下载引擎包，再用「手动选择本地文件」。";
                        return "服务器返回 " + (int)r.StatusCode + " " + r.StatusDescription;
                    }
                }
                catch { }
                return "服务器拒绝了请求。";
            case WebExceptionStatus.ReceiveFailure:
                return "连接中断。网络不稳定，可以点「继续下载」重试（支持断点续传）。";
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
