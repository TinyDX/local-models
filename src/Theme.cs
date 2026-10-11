// Theme.cs -- 主题配色
// 40 个颜色值**逐个提取自原版 Local Models.exe 的 IL**（Palette.For 里的 Color.FromArgb 常量），
// 按 Palette 字段声明顺序一一对应。没有一个是估的。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal enum ThemeMode { System = 0, Light = 1, Dark = 2 }

internal sealed class Palette
{
    public bool Dark;
    public Color Bg, Header, Card, CardAlt, Line, Fg, Dim, Accent, AccentHover,
                 Green, Amber, Red, PillBg, LogBg, LogFg, InputBg, TitleBar,
                 Btn2, PillOk, PillWarn;

    public static Palette For(bool dark)
    {
        if (dark) return new Palette
        {
            Dark = true,
            Bg = Color.FromArgb(255, 32, 32, 32),
            Header = Color.FromArgb(255, 41, 41, 41),
            Card = Color.FromArgb(255, 45, 45, 45),
            CardAlt = Color.FromArgb(255, 38, 38, 38),
            Line = Color.FromArgb(255, 62, 62, 62),
            Fg = Color.FromArgb(255, 243, 243, 243),
            Dim = Color.FromArgb(255, 160, 160, 160),
            Accent = Color.FromArgb(255, 76, 194, 255),
            AccentHover = Color.FromArgb(255, 120, 210, 255),
            Green = Color.FromArgb(255, 108, 203, 95),
            Amber = Color.FromArgb(255, 252, 225, 0),
            Red = Color.FromArgb(255, 255, 153, 164),
            PillBg = Color.FromArgb(255, 58, 58, 58),
            LogBg = Color.FromArgb(255, 24, 24, 24),
            LogFg = Color.FromArgb(255, 150, 210, 175),
            InputBg = Color.FromArgb(255, 45, 45, 45),
            TitleBar = Color.FromArgb(255, 32, 32, 32),
            Btn2 = Color.FromArgb(255, 58, 58, 58),
            PillOk = Color.FromArgb(255, 30, 58, 48),
            PillWarn = Color.FromArgb(255, 58, 48, 30)
        };
        return new Palette
        {
            Dark = false,
            Bg = Color.FromArgb(255, 243, 243, 243),
            Header = Color.FromArgb(255, 251, 251, 251),
            Card = Color.FromArgb(255, 255, 255, 255),
            CardAlt = Color.FromArgb(255, 249, 249, 249),
            Line = Color.FromArgb(255, 225, 225, 225),
            Fg = Color.FromArgb(255, 27, 27, 27),
            Dim = Color.FromArgb(255, 96, 96, 96),
            Accent = Color.FromArgb(255, 0, 95, 184),
            AccentHover = Color.FromArgb(255, 0, 120, 212),
            Green = Color.FromArgb(255, 15, 123, 15),
            Amber = Color.FromArgb(255, 157, 93, 0),
            Red = Color.FromArgb(255, 196, 43, 28),
            PillBg = Color.FromArgb(255, 238, 238, 238),
            LogBg = Color.FromArgb(255, 250, 250, 250),
            LogFg = Color.FromArgb(255, 20, 90, 60),
            InputBg = Color.FromArgb(255, 255, 255, 255),
            TitleBar = Color.FromArgb(255, 243, 243, 243),
            Btn2 = Color.FromArgb(255, 238, 238, 238),
            PillOk = Color.FromArgb(255, 223, 246, 221),
            PillWarn = Color.FromArgb(255, 255, 244, 206)
        };
    }
}

internal static class Theme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeMode Mode = ThemeMode.System;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    public static bool SystemIsLight()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
            {
                if (k == null) return false;
                object v = k.GetValue("AppsUseLightTheme");
                if (v == null) return false;
                return Convert.ToInt32(v) != 0;
            }
        }
        catch { return false; }
    }

    public static bool IsDark()
    {
        switch (Mode)
        {
            case ThemeMode.Light: return false;
            case ThemeMode.Dark: return true;
            default: return !SystemIsLight();
        }
    }

    public static Palette Current { get { return Palette.For(IsDark()); } }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ================= 深色滚动条 =================
    //
    // WinForms 的滚动条（TextBox 的 ScrollBars、Panel 的 AutoScroll）是
    // 系统主题画的，默认永远是浅色 —— 在深色界面里白晃晃一条，很扎眼。
    //
    // 系统给的办法是 SetWindowTheme(hwnd, "DarkMode_Explorer", null)：
    // 把那个控件的主题切成「深色资源管理器」那一套，滚动条就跟着变深色。
    // Win10 1809+ 就支持，Explorer / UWP 自己就是这么用的，不用额外依赖。
    //
    // 注意：必须作用在**真正带滚动条的那个窗口句柄**上，且句柄要先创建好
    // （构造函数里还没有），所以得等窗口显示之后再调。

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

    /// 把某个控件及其**所有子控件**的滚动条刷成深色。
    ///
    /// 之所以要递归：Panel 的 AutoScroll 滚动条归 Panel 自己，
    /// 而 MultiLine TextBox 的滚动条是它自己的子窗口，两者都得单独设。
    public static void DarkScrollbars(Control c)
    {
        if (c == null) return;
        try
        {
            if (!c.IsHandleCreated) return;
            SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        }
        catch { }
        try
        {
            foreach (Control ch in c.Controls) DarkScrollbars(ch);
        }
        catch { }
    }

    // ================= Mica 背景 =================
    //
    // Windows 11 的 Mica 是「取桌面壁纸 → 大幅模糊 → 叠主题色调」，铺在窗口底层。
    //
    // 为什么不用系统那个真 Mica：DwmSetWindowAttribute(38) 只能作用在**窗口本身**，
    // 而 WinForms 的控件是 GDI 画的、不透明，会把系统给的底层整个盖住 ——
    // 想让真 Mica 透出来，就得让窗体背景透明，那样控件位置会变成点击穿透，
    // 而且 GDI 的半透明也没法跟 DWM 底层混合。所以这里**自己按同样原理生成一张底图**：
    // 读壁纸 → 缩放 → 强模糊 → 叠主题色。视觉上就是 Mica，而且跟随你的壁纸变化。

    private static Bitmap _micaCache;
    private static int _micaW, _micaH;
    private static string _micaKey = "";

    /// 生成（或取缓存）Mica 底图
    public static Bitmap MicaBackground(int w, int h, Palette p, bool dark)
    {
        if (w <= 0 || h <= 0) return null;
        string key = w + "x" + h + "|" + (dark ? "d" : "l") + "|" + p.Bg.ToArgb() + "|" + WallpaperPath();
        if (_micaCache != null && _micaW == w && _micaH == h && _micaKey == key)
            return _micaCache;

        Bitmap src = null;
        try
        {
            string wp = WallpaperPath();
            if (wp.Length > 0 && File.Exists(wp))
                using (var tmp = new Bitmap(wp)) src = new Bitmap(tmp);
        }
        catch { src = null; }

        if (src == null)
        {
            // 没壁纸（纯色桌面）→ 用主题色当底
            src = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(src)) g.Clear(p.Bg);
        }

        try
        {
            // 1) 按「覆盖」方式缩放到窗口大小
            var scaled = new Bitmap(w, h);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float s = Math.Max((float)w / src.Width, (float)h / src.Height);
                int dw = (int)(src.Width * s), dh = (int)(src.Height * s);
                g.DrawImage(src, new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh));
            }

            // 2) 强模糊：缩小到 1/24 再放大回来，够糊且很快
            int sw = Math.Max(1, w / 24), sh = Math.Max(1, h / 24);
            var small = new Bitmap(sw, sh);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(scaled, 0, 0, sw, sh);
            }
            var blur = new Bitmap(w, h);
            using (var g = Graphics.FromImage(blur))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(small, 0, 0, w, h);
            }

            // 3) 叠主题色 —— Mica 会把壁纸压得很淡，越不透明越"素"
            using (var g = Graphics.FromImage(blur))
            using (var b = new SolidBrush(Color.FromArgb(dark ? 208 : 188, p.Bg)))
                g.FillRectangle(b, 0, 0, w, h);

            scaled.Dispose();
            small.Dispose();

            if (_micaCache != null) _micaCache.Dispose();
            _micaCache = blur;
            _micaW = w; _micaH = h; _micaKey = key;
            return _micaCache;
        }
        catch { }
        finally { if (src != null) src.Dispose(); }

        return null;
    }

    /// 当前桌面壁纸路径（纯色桌面返回空串）
    public static string WallpaperPath()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
            {
                if (k == null) return "";
                object v = k.GetValue("WallPaper");
                if (v == null) return "";
                string s = v.ToString();
                if (s.IndexOf('%') >= 0) s = Environment.ExpandEnvironmentVariables(s);
                return s;
            }
        }
        catch { return ""; }
    }

    public static void ApplyWin11(IntPtr hwnd, bool dark, bool mica)
    {
        TrySet(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        if (mica)
        {
            if (TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW) != 0)
                TrySet(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW);
        }
    }


    public static int TrySet(IntPtr hwnd, int attr, int value)
    {
        try { int v = value; return DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int)); }
        catch { return -1; }
    }
}
