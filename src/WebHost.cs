// WebHost.cs -- 内置网页服务（替代 node + webui-server.js）
//
// 集成动机：原版靠外部的 node.exe + webui-server.js 提供网页。
//   其中 node.exe 的路径写死在 C:\Users\Administrator\.dsh\... ，
//   换一台电脑这个路径就不存在 —— 这是分发时最大的障碍。
//   这里用 .NET 自带的 HttpListener 完全替代，功能等价：
//     · 静态页（index.html 编译进 exe，作为嵌入资源）
//     · /v1/* 与 /anthropic/* 反代到引擎，SSE 流式透传
//
// 监听方式说明（踩过的坑）：
//   不能用 "http://+:port/" —— 绑 "+" 需要管理员权限，拿不到时会退回 localhost，
//   而 localhost 前缀只接受 Host 头 = localhost 的请求，
//   于是用 127.0.0.1 或局域网 IP 访问就会 400 "Invalid Hostname"。
//   正确做法：把本机每个具体地址都显式注册。
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal sealed class WebHost
{
    private readonly int _port, _eng;
    private readonly string _fallbackRoot;
    private readonly List<HttpListener> _listeners = new List<HttpListener>();
    private Thread _accept;
    public volatile bool On;
    public string Err = "";

    /// 有客户端连接（或被拒绝）时回调，供界面写日志。
    /// 用它就能判断"手机的请求到底有没有到达这台电脑" ——
    /// 有记录 = 网络通了、只是被拦；完全没记录 = 包根本没到（网络层隔离）。
    public Action<string> OnClient;

    /// 当前模型 ID（启动器按 .ninfer 文件名推导后填进来）。
    /// 用来识别「思考别名」：<模型ID>-think、<模型ID>-think-high …
    public string ModelId = "";

    /// 思考档位（与网页端下拉框一致）
    private static readonly string[] ThinkLevels =
        new string[] { "minimal", "low", "medium", "high", "xhigh", "max" };

    /// 解析「思考别名」模型名。
    ///
    /// 为什么做别名：思考开关在网页端是勾选框，但第三方聊天客户端
    /// 大多没有"自定义请求参数"的地方，没法直接塞 reasoning_effort。
    /// 于是把档位编进模型名里，客户端只要换个模型名就能控制思考：
    ///
    ///   bonsai2_27b_...            → 用户填的这个 → 思考关闭（引擎默认）
    ///   bonsai2_27b_...-think      → 开启思考，档位 medium
    ///   bonsai2_27b_...-think-high → 开启思考，档位 high
    ///   bonsai2_27b_...-think-max  → 开启思考，档位 max
    private bool ParseThinkAlias(string asked, out string effort)
    {
        effort = "";
        if (string.IsNullOrEmpty(asked) || string.IsNullOrEmpty(ModelId)) return false;
        string prefix = ModelId + "-think";
        if (!asked.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string rest = asked.Substring(prefix.Length).TrimStart('-').Trim();
        if (rest.Length == 0) { effort = "medium"; return true; }   // -think
        foreach (string lv in ThinkLevels)
            if (string.Equals(lv, rest, StringComparison.OrdinalIgnoreCase)) { effort = lv; return true; }

        // 档位写错了也按 -think 处理（默认 medium），免得客户端只看到一个 404
        effort = "medium";
        return true;
    }

    /// 从 JSON 请求体里取 model 字段的值（不引 JSON 库，够用即可）
    private static string ExtractModel(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        Match m = Regex.Match(json, "\"model\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// 把请求体里的模型名换成真名，必要时补上思考参数。
    /// 在 "model":"xxx" 后面直接插字段 —— 插在键值对之间永远是合法 JSON。
    private static string ApplyThinkAlias(string json, string realModel, string effort)
    {
        string outp = Regex.Replace(json,
            "(\"model\"\\s*:\\s*)\"[^\"]*\"",
            "$1\"" + realModel + "\"", RegexOptions.None);
        if (effort.Length == 0) return outp;
        if (outp.IndexOf("reasoning_effort", StringComparison.OrdinalIgnoreCase) >= 0) return outp;

        string inject = ",\"reasoning_effort\":\"" + effort + "\",\"preserve_thinking\":true";
        return Regex.Replace(outp,
            "(\"model\"\\s*:\\s*\"[^\"]*\")",
            "$1" + inject, RegexOptions.None);
    }

    /// 客户端没自己指定推理等级时，补上程序里设置的全局档位。
    /// 返回 null 表示不用改（客户端已指定，或全局档位是关闭）。
    private static string InjectGlobalEffort(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        if (Reasoning.Effort == "none") return null;                        // 关闭 → 不加字段
        if (json.IndexOf("reasoning_effort", StringComparison.OrdinalIgnoreCase) >= 0) return null;

        string inject = ",\"reasoning_effort\":\"" + Reasoning.Effort + "\",\"preserve_thinking\":true";
        string outp = Regex.Replace(json,
            "(\"model\"\\s*:\\s*\"[^\"]*\")", "$1" + inject, RegexOptions.None);
        return outp == json ? null : outp;
    }

    /// 是不是聊天类接口（只有这类才需要改写请求体）
    private static bool IsChatPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        return p.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/completions", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> Mime =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" },
        { ".js", "text/javascript; charset=utf-8" },
        { ".css", "text/css; charset=utf-8" },
        { ".json", "application/json; charset=utf-8" },
        { ".svg", "image/svg+xml" },
        { ".png", "image/png" },
        { ".ico", "image/x-icon" },
        { ".woff2", "font/woff2" }
    };

    public WebHost(int port, int enginePort, string fallbackRoot)
    {
        _port = port; _eng = enginePort; _fallbackRoot = fallbackRoot;
    }

    public bool Start()
    {
        // ★ 反 400 / 反"手机打不开"的关键：
        //   HttpListener 只接受 **Host 头与已注册前缀匹配** 的请求。
        //   之前只注册具体地址，于是手机用局域网 IP / 主机名访问就 400。
        //
        //   正解：注册通配前缀（接受任意 Host）。但它需要 URL 预留（urlacl）：
        //     netsh http add urlacl url=http://+:8096/ user=Everyone
        //   预留一次永久生效，之后普通权限也能绑通配前缀。
        //
        //   这里按顺序尝试：
        //     1) 通配前缀（已有预留 / 管理员权限 → 成功）
        //     2) 提权补预留后再试通配
        //     3) 退回逐地址注册（至少本机可用）
        //
        //   另外必须先把防火墙放行，否则在"公用网络"（校园 WiFi、酒店 WiFi
        //   都算）上入站会被直接丢掉 —— 本机能开，别的设备连不上。
        EnsureFirewall();

        if (TryWildcard()) return Started("已绑定通配前缀（任意 Host 可访问）");
        if (EnsureUrlAcl() && TryWildcard()) return Started("已自动预留并绑定通配前缀");
        if (TryFallback()) return Started(_fallbackCount + " 个地址可访问（未能绑定通配前缀）");
        Err = "无法监听任何地址";
        return false;
    }

    /// 本程序创建的防火墙规则名（用独立名字，不去动别人建的规则）
    private const string FwRuleName = "Local Models Web (all networks)";

    /// 放行状态，供界面/日志显示
    public string FirewallNote = "";

    /// 确保防火墙放行网页端口。
    ///
    /// ★ 两个容易踩的点：
    ///
    ///   1) **必须按"端口"放行，按"程序"放行没用。**
    ///      HttpListener 走的是 HTTP.sys 内核驱动，流量由 System 进程接手，
    ///      不归属本程序进程 —— 所以以程序为条件的入站规则对它不生效。
    ///
    ///   2) **规则必须覆盖所有网络类别（profile=any）。**
    ///      Windows 按网络的"类别"套用规则：家里 WiFi 常被判为"专用网络"，
    ///      而校园网、酒店网、公共 WiFi 会被判为"公用网络"。
    ///      只对"专用网络"生效的规则，一换到校园网就等于没开 ——
    ///      而且表面上看不出来，端口在监听、本机能开，别的设备就是连不上。
    ///
    ///   远程范围限定为本子网（只有同一网段的设备能访问），
    ///   避免连到公共网络时把服务暴露给整个网络。
    private void EnsureFirewall()
    {
        try
        {
            if (HasFirewallRule()) { FirewallNote = "已放行端口 " + _port; return; }

            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                "advfirewall firewall add rule name=\"" + FwRuleName + "\" " +
                "dir=in action=allow protocol=TCP localport=" + _port + " " +
                "profile=any remoteip=localsubnet")
            {
                UseShellExecute = true,
                Verb = "runas",              // 需要管理员，弹一次 UAC
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit(20000);
                FirewallNote = p.ExitCode == 0
                    ? "已新建放行规则（端口 " + _port + "，所有网络类别）"
                    : "放行规则创建失败（退出码 " + p.ExitCode + "）";
            }
        }
        catch
        {
            // 用户拒绝 UAC 或失败：不影响本机使用，只是别的设备连不上
            FirewallNote = "未放行端口（用户取消或失败）—— 局域网设备可能连不上";
        }
    }

    /// 检查本程序自己的放行规则是否已存在
    private bool HasFirewallRule()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                "advfirewall firewall show rule name=\"" + FwRuleName + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return p.ExitCode == 0;       // 找到规则时 netsh 返回 0
            }
        }
        catch { return false; }
    }

    private int _fallbackCount;

    private bool Started(string msg)
    {
        Err = msg;
        On = true;
        _accept = new Thread(AcceptLoop) { IsBackground = true };
        _accept.Start();
        return true;
    }

    /// 尝试绑定通配前缀
    private bool TryWildcard()
    {
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://+:" + _port + "/");
            l.Start();
            _listeners.Add(l);
            return true;
        }
        catch { return false; }
    }

    /// 用 netsh 补 URL 预留（会弹一次 UAC）
    private bool EnsureUrlAcl()
    {
        try
        {
            string url = "http://+:" + _port + "/";
            // 先查是否已存在
            if (HasUrlAcl(url)) return true;

            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                "http add urlacl url=" + url + " user=Everyone")
            {
                UseShellExecute = true,
                Verb = "runas",              // 触发 UAC 提权
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
        }
        catch { return false; }   // 用户拒绝 UAC 或失败
    }

    private bool HasUrlAcl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", "http show urlacl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                return o != null && o.IndexOf(url, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch { return false; }
    }

    /// 退回：逐个注册本机所有具体地址
    private bool TryFallback()
    {
        var prefixes = new List<string>();
        prefixes.Add("http://127.0.0.1:" + _port + "/");
        prefixes.Add("http://[::1]:" + _port + "/");
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    prefixes.Add("http://" + ip + ":" + _port + "/");
                else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                         && !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast)
                    prefixes.Add("http://[" + ip + "]:" + _port + "/");
            }
        }
        catch { }
        prefixes.Add("http://localhost:" + _port + "/");

        foreach (string p in prefixes)
        {
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add(p);
                l.Start();
                _listeners.Add(l);
                _fallbackCount++;
            }
            catch { }
        }
        return _listeners.Count > 0;
    }

    public void Stop()
    {
        On = false;
        foreach (var l in _listeners) { try { l.Stop(); l.Close(); } catch { } }
        _listeners.Clear();
    }

    private void AcceptLoop()
    {
        foreach (var l in _listeners)
        {
            var lst = l;
            var th = new Thread(() =>
            {
                while (On)
                {
                    HttpListenerContext c;
                    try { c = lst.GetContext(); } catch { if (!On) break; continue; }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { Handle(c); }
                        catch { try { c.Response.StatusCode = 500; c.Response.Close(); } catch { } }
                    });
                }
            }) { IsBackground = true };
            th.Start();
        }
    }

    private void Handle(HttpListenerContext c)
    {
        // ---- 来源 IP 检查（公开 / 指定 IP）----
        string clientIp = "";
        try
        {
            if (c.Request.RemoteEndPoint != null)
                clientIp = AccessConfig.Normalize(c.Request.RemoteEndPoint.Address.ToString());
        }
        catch { }

        if (!AccessConfig.IsAllowed(clientIp))
        {
            try
            {
                c.Response.StatusCode = 403;
                c.Response.ContentType = "text/html; charset=utf-8";
                byte[] body = Encoding.UTF8.GetBytes(
                    "<!doctype html><meta charset=\"utf-8\">" +
                    "<div style=\"font:15px/1.7 system-ui;padding:40px;color:#c0392b\">" +
                    "<b>访问被拒绝</b><br>本机开启了 IP 白名单，你的地址（" + clientIp +
                    "）不在允许列表内。<br>如需访问，请让电脑端把它加进「设置 → 局域网访问控制 → 指定 IP」。</div>");
                c.Response.OutputStream.Write(body, 0, body.Length);
                c.Response.Close();
            }
            catch { }
            if (OnClient != null) OnClient(clientIp + " 被拒绝（不在白名单）");
            return;
        }
        if (OnClient != null) OnClient(clientIp + " 已连接");

        string p = c.Request.Url.AbsolutePath;

        // ---- 对话历史存到安装目录（而不是浏览器 local storage）----
        //   GET  /chats  → 返回全部会话 JSON
        //   POST /chats  → 覆盖保存（body 就是那份 JSON）
        //   放在安装目录的 对话历史.json，卸载时不会被自动删（那是用户的东西）。
        if (p == "/chats" || p == "/chats/")
        {
            ServeChats(c);
            return;
        }

        if (p.StartsWith("/v1/") || p.StartsWith("/anthropic/"))
        {
            // 模型列表：补上「思考别名」，客户端下拉框里就能直接选。
            // ★ 这里也要校验密钥 —— 原来这个分支直接拿自己的 key 去问引擎，
            //   等于把 /v1/models 当公开接口，任何人带上乱七八糟的 key 都能拿到模型列表。
            //   和 Proxy() 保持一致：带了 key 但没登记过 → 401。
            if ((p == "/v1/models" || p == "/models") &&
                (c.Request.HttpMethod == "GET" || c.Request.HttpMethod == "HEAD"))
            {
                string mt = ClientToken(c.Request);
                if (mt.Length > 0 && !EngineKey.Accept(mt))
                {
                    RejectUnauthorized(c, clientIp);
                    return;
                }
                ServeModelsWithAliases(c);
                return;
            }
            Proxy(c, null);
            return;
        }

        // ★ 兼容「接口地址只填到主机端口」的客户端：
        //   这类客户端会把请求发到 /chat/completions、/messages 这种没有 /v1 前缀的路径，
        //   原来直接回一个光秃秃的 405，用户只能对着错误猜。
        //   这里自动补上 /v1 前缀再转发（引擎认不认由它决定）。
        if (IsApiPath(p))
        {
            if (OnClient != null) OnClient(clientIp + " " + c.Request.HttpMethod + " " + p + "（自动补 /v1 前缀）");
            Proxy(c, "/v1" + p);
            return;
        }

        if (c.Request.HttpMethod != "GET" && c.Request.HttpMethod != "HEAD")
        {
            // 不是认识的接口路径，回一个能看懂的错误，而不是空 405
            try
            {
                c.Response.StatusCode = 404;
                c.Response.ContentType = "application/json; charset=utf-8";
                byte[] eb = Encoding.UTF8.GetBytes(
                    "{\"error\":{\"message\":\"未知接口：" + c.Request.HttpMethod + " " + p +
                    "。本机的 OpenAI 兼容接口地址是 http://<本机IP>:8096/v1，" +
                    "例如 http://127.0.0.1:8096/v1/chat/completions\"," +
                    "\"type\":\"invalid_request_error\"}}");
                c.Response.OutputStream.Write(eb, 0, eb.Length);
                c.Response.Close();
            }
            catch { }
            if (OnClient != null) OnClient(clientIp + " " + c.Request.HttpMethod + " " + p + "（未知接口，已拒绝）");
            return;
        }

        if (p == "/" || p.Length == 0 || p == "/index.html")
        {
            byte[] html = Embedded("index.html", _fallbackRoot);
            if (html != null)
            {
                c.Response.StatusCode = 200;
                c.Response.ContentType = "text/html; charset=utf-8";
                c.Response.Headers["Cache-Control"] = "no-store";
                if (c.Request.HttpMethod != "HEAD") c.Response.OutputStream.Write(html, 0, html.Length);
                c.Response.Close();
                return;
            }
        }

        // 其它文件：先找嵌入资源，再找磁盘（webui 目录）
        string rel = Uri.UnescapeDataString(p).TrimStart('/', '\\');
        byte[] data = Embedded(rel, _fallbackRoot);
        if (data == null && _fallbackRoot != null)
        {
            try
            {
                string full = Path.GetFullPath(Path.Combine(_fallbackRoot,
                    rel.Replace('/', Path.DirectorySeparatorChar)));
                if (full.StartsWith(Path.GetFullPath(_fallbackRoot), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(full)) data = File.ReadAllBytes(full);
            }
            catch { }
        }

        if (data == null)
        {
            byte[] nf = Encoding.UTF8.GetBytes("404: " + p);
            c.Response.StatusCode = 404;
            c.Response.ContentType = "text/plain; charset=utf-8";
            c.Response.OutputStream.Write(nf, 0, nf.Length);
            c.Response.Close();
            return;
        }

        string ct;
        if (!Mime.TryGetValue(Path.GetExtension(rel), out ct)) ct = "application/octet-stream";
        c.Response.StatusCode = 200;
        c.Response.ContentType = ct;
        c.Response.Headers["Cache-Control"] = "no-store";
        if (c.Request.HttpMethod != "HEAD") c.Response.OutputStream.Write(data, 0, data.Length);
        c.Response.Close();
    }

    /// 从嵌入资源取文件（资源名形如 LocalModels.webui.index.html）
    private static byte[] Embedded(string name, string unused)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string want = name.Replace('/', '.').Replace('\\', '.');
            foreach (string res in asm.GetManifestResourceNames())
            {
                if (res.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream s = asm.GetManifestResourceStream(res))
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// 取引擎的模型列表，并在后面补上「思考别名」。
    ///
    /// 这样客户端点「获取模型列表」时，下拉框里会直接出现：
    ///   原模型名            → 思考关闭
    ///   原模型名-think      → 思考开启（medium）
    ///   原模型名-think-high → 思考 high
    ///   …各档位
    /// 对话历史的读写。
    ///
    /// 存到**安装目录**而不是浏览器，也不是 webui\ —— 好处：
    ///   · 换浏览器、换设备（局域网访问）都能看到同一份历史
    ///   · 清浏览器数据不会丢
    ///   · 直接备份 / 编辑这个文件就行
    ///   · 和 API密钥.json、推理等级.json 这些用户配置放在一起
    ///
    /// 文件：<安装目录>\对话历史.json
    ///
    /// 注意 _fallbackRoot 是 webui 目录，所以要往上退一级才是安装目录。
    private void ServeChats(HttpListenerContext c)
    {
        string baseDir = _fallbackRoot;
        try
        {
            // webui 的上一级就是安装目录
            if (!string.IsNullOrEmpty(baseDir))
            {
                string up = Path.GetDirectoryName(baseDir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(up) &&
                    string.Equals(Path.GetFileName(baseDir.TrimEnd('\\', '/')), "webui",
                                  StringComparison.OrdinalIgnoreCase))
                    baseDir = up;
            }
        }
        catch { }
        if (string.IsNullOrEmpty(baseDir)) baseDir = ".";

        string path = Path.Combine(baseDir, "对话历史.json");
        try
        {
            if (c.Request.HttpMethod == "GET" || c.Request.HttpMethod == "HEAD")
            {
                string json = "[]";
                if (File.Exists(path))
                {
                    try { json = File.ReadAllText(path, Encoding.UTF8); } catch { json = "[]"; }
                    if (json.Trim().Length == 0) json = "[]";
                }
                byte[] b = Encoding.UTF8.GetBytes(json);
                c.Response.StatusCode = 200;
                c.Response.ContentType = "application/json; charset=utf-8";
                c.Response.ContentEncoding = Encoding.UTF8;
                c.Response.ContentLength64 = b.Length;
                if (c.Request.HttpMethod != "HEAD") c.Response.OutputStream.Write(b, 0, b.Length);
                c.Response.Close();
                return;
            }

            if (c.Request.HttpMethod == "POST" || c.Request.HttpMethod == "PUT")
            {
                string body;
                using (var sr = new StreamReader(c.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();

                // 只接受 JSON 数组，避免把乱七八糟的东西写进文件
                string t = (body ?? "").Trim();
                bool ok = t.StartsWith("[") && t.EndsWith("]");
                if (ok)
                {
                    // 先写临时文件再替换 —— 写一半断电也不会把历史弄坏
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, t, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }

                byte[] rb = Encoding.UTF8.GetBytes(ok ? "{\"ok\":true}" : "{\"ok\":false}");
                c.Response.StatusCode = ok ? 200 : 400;
                c.Response.ContentType = "application/json; charset=utf-8";
                c.Response.ContentLength64 = rb.Length;
                c.Response.OutputStream.Write(rb, 0, rb.Length);
                c.Response.Close();
                if (OnClient != null) OnClient("对话历史已保存（" + (t.Length / 1024) + " KB）");
                return;
            }

            c.Response.StatusCode = 405;
            c.Response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                byte[] eb = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" +
                    ex.Message.Replace("\"", "'") + "\"}");
                c.Response.StatusCode = 500;
                c.Response.ContentType = "application/json; charset=utf-8";
                c.Response.OutputStream.Write(eb, 0, eb.Length);
                c.Response.Close();
            }
            catch { }
            if (OnClient != null) OnClient("对话历史读写失败：" + ex.Message);
        }
    }

    private void ServeModelsWithAliases(HttpListenerContext c)
    {
        string body = "";
        try
        {
            var up = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _eng + "/v1/models");
            up.Method = "GET";
            up.Timeout = 20000;
            up.ReadWriteTimeout = 20000;
            try { up.Headers["Authorization"] = "Bearer " + ApiKey; } catch { }
            using (var r = (HttpWebResponse)up.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                body = sr.ReadToEnd();
        }
        catch { }

        if (body.Length == 0 || ModelId.Length == 0)
        {
            Proxy(c, null);        // 拿不到就原样转发
            return;
        }

        string extra = BuildAliasEntries();
        int close = body.LastIndexOf(']');
        if (close >= 0 && extra.Length > 0)
            body = body.Substring(0, close) + "," + extra + body.Substring(close);

        try
        {
            byte[] outBytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/json; charset=utf-8";
            c.Response.Headers["Cache-Control"] = "no-store";
            if (c.Request.HttpMethod != "HEAD")
                c.Response.OutputStream.Write(outBytes, 0, outBytes.Length);
            c.Response.Close();
        }
        catch { }
    }

    /// 生成思考别名那几条 JSON
    private string BuildAliasEntries()
    {
        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        var sb = new StringBuilder();
        // 先来一条「-think」，等价于 medium
        sb.Append("{\"id\":\"").Append(ModelId).Append("-think\",\"object\":\"model\"")
          .Append(",\"owned_by\":\"ninfer\",\"created\":").Append(now)
          .Append(",\"meta\":{\"alias\":\"思考开启（默认档位 medium）\"}}");
        foreach (string lv in ThinkLevels)
        {
            sb.Append(",{\"id\":\"").Append(ModelId).Append("-think-").Append(lv)
              .Append("\",\"object\":\"model\",\"owned_by\":\"ninfer\",\"created\":").Append(now)
              .Append(",\"meta\":{\"alias\":\"思考档位 ").Append(lv).Append("\"}}");
        }
        return sb.ToString();
    }

    /// 是不是「缺了 /v1 前缀」的 OpenAI 兼容接口路径
    private static bool IsApiPath(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        string[] known = new string[]
        {
            "/chat/completions", "/completions", "/models", "/embeddings",
            "/messages", "/responses", "/audio/transcriptions", "/audio/speech",
            "/images/generations", "/rerank", "/tokenize"
        };
        foreach (string k in known)
            if (p.Equals(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Proxy(HttpListenerContext c, string pathOverride)
    {
        string clientIp = "";
        try
        {
            if (c.Request.RemoteEndPoint != null)
                clientIp = AccessConfig.Normalize(c.Request.RemoteEndPoint.Address.ToString());
        }
        catch { }

        string path = pathOverride != null ? pathOverride : c.Request.Url.PathAndQuery;
        var up = (HttpWebRequest)WebRequest.Create(
            "http://127.0.0.1:" + _eng + path);
        up.Method = c.Request.HttpMethod;
        up.Timeout = 600000;
        up.ReadWriteTimeout = 600000;
        up.AllowReadStreamBuffering = false;
        up.AllowWriteStreamBuffering = false;

        foreach (string k in c.Request.Headers.AllKeys)
        {
            if (k == null) continue;
            string lk = k.ToLowerInvariant();
            if (lk == "host" || lk == "connection" || lk == "content-length") continue;
            try { up.Headers[k] = c.Request.Headers[k]; } catch { }
        }
        // ---- API 密钥校验 ----
        // 引擎自己只认一个 key，所以「多条 key」在这一层做：
        //   没带 key        → 补上引擎的 key（网页免 key 就靠这个）
        //   带了且是登记过的 → 换成引擎的 key 转发
        //   带了但没登记过   → 直接 401，不打扰引擎
        string token = ClientToken(c.Request);
        if (token.Length == 0)
        {
            try { up.Headers["Authorization"] = "Bearer " + ApiKey; } catch { }
        }
        else if (EngineKey.Accept(token))
        {
            try { up.Headers["Authorization"] = "Bearer " + ApiKey; } catch { }
        }
        else
        {
            RejectUnauthorized(c, clientIp);
            return;
        }
        if (c.Request.ContentLength64 > 0) up.ContentLength = c.Request.ContentLength64;
        if (c.Request.HasEntityBody)
        {
            // 聊天请求体通常只有几 KB，先读出来判断要不要改写模型名/补思考参数
            byte[] raw;
            using (var ms = new MemoryStream())
            {
                c.Request.InputStream.CopyTo(ms);
                raw = ms.ToArray();
            }
            string path0 = c.Request.Url.AbsolutePath;
            string asked = Encoding.UTF8.GetString(raw);
            if (IsChatPath(path0))
            {
                string effort;
                if (ParseThinkAlias(ExtractModel(asked), out effort))
                {
                    // 模型名带了 -think 别名：以别名里的档位为准
                    string rewritten = ApplyThinkAlias(asked, ModelId, effort);
                    raw = Encoding.UTF8.GetBytes(rewritten);
                    if (OnClient != null)
                        OnClient(clientIp + " 用别名开启思考：档位 " + effort);
                }
                else
                {
                    // 客户端没自己带 reasoning_effort 时，补上程序里设置的全局档位
                    string injected = InjectGlobalEffort(asked);
                    if (injected != null)
                    {
                        raw = Encoding.UTF8.GetBytes(injected);
                        if (OnClient != null)
                            OnClient(clientIp + " 应用全局推理等级：" + Reasoning.Effort);
                    }
                }
                try { up.ContentLength = raw.Length; } catch { }
            }
            using (Stream d = up.GetRequestStream()) d.Write(raw, 0, raw.Length);
        }

        try
        {
            using (var r = (HttpWebResponse)up.GetResponse())
            {
                c.Response.StatusCode = (int)r.StatusCode;
                foreach (string k in r.Headers.AllKeys)
                {
                    string lk = k.ToLowerInvariant();
                    if (lk == "transfer-encoding" || lk == "content-length" || lk == "connection") continue;
                    try { c.Response.Headers[k] = r.Headers[k]; } catch { }
                }
                c.Response.SendChunked = true;
                using (Stream rs = r.GetResponseStream())
                {
                    byte[] b = new byte[16384];
                    int n;
                    while ((n = rs.Read(b, 0, b.Length)) > 0)
                    {
                        c.Response.OutputStream.Write(b, 0, n);
                        c.Response.OutputStream.Flush();
                    }
                }
                c.Response.Close();
            }
        }
        catch (WebException we)
        {
            var r = we.Response as HttpWebResponse;
            if (r != null)
            {
                c.Response.StatusCode = (int)r.StatusCode;
                using (Stream rs = r.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    rs.CopyTo(ms);
                    byte[] b = ms.ToArray();
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.OutputStream.Write(b, 0, b.Length);
                }
                c.Response.Close();
            }
            else
            {
                byte[] b = Encoding.UTF8.GetBytes(
                    "{\"error\":{\"message\":\"engine unreachable\"}}");
                c.Response.StatusCode = 502;
                c.Response.ContentType = "application/json; charset=utf-8";
                c.Response.OutputStream.Write(b, 0, b.Length);
                c.Response.Close();
            }
        }
    }

    /// 引擎与网页代理共用的 API 密钥。
    /// 不再是常量 —— 首次运行随机生成，存在「引擎密钥.json」里。
    internal static string ApiKey { get { return EngineKey.Current; } }

    /// 回一个 401（密钥无效）。抽出来是因为不止一个分支要用：
    /// 原来只有 Proxy() 里有一段内联的 401，/v1/models 那条路绕过了校验。
    private void RejectUnauthorized(HttpListenerContext c, string clientIp)
    {
        try
        {
            c.Response.StatusCode = 401;
            c.Response.ContentType = "application/json; charset=utf-8";
            byte[] body = Encoding.UTF8.GetBytes(
                "{\"error\":{\"message\":\"API key 无效。本机登记的有效 key 见程序「设置 → API 密钥」。\",\"type\":\"invalid_request_error\"}}");
            c.Response.OutputStream.Write(body, 0, body.Length);
            c.Response.Close();
        }
        catch { }
        if (OnClient != null) OnClient(clientIp + " 使用了无效的 API key");
    }

    /// 从请求里取出客户端带的 API key（去掉 "Bearer " 前缀）
    private static string ClientToken(HttpListenerRequest req)
    {
        try
        {
            string h = req.Headers["Authorization"];
            if (string.IsNullOrEmpty(h)) return "";
            h = h.Trim();
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) h = h.Substring(7);
            else if (h.StartsWith("bearer", StringComparison.OrdinalIgnoreCase)) h = h.Substring(6);
            return h.Trim();
        }
        catch { return ""; }
    }
}
