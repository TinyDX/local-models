// W11Ui.cs -- Windows 11 风格控件（平面不透明版）
//
// WinForms 自带的 RadioButton / Panel 是 Win7 时代的样子：没有圆角、没有悬停反馈。
// 这里用 UserPaint 自己画，做出接近 Win11 设置的观感：
//   · 圆角卡片
//   · 悬停时背景轻微提亮
//   · 选中项：圆点填充强调色 + 左侧强调条
//   · 全程 DPI 缩放
//
// 每个控件都有一个 Bg 字段 = **父容器底色**，用来先把背景填平
// （这样圆角外面不会露出脏色）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class W11
{
    /// DPI 缩放系数，用之前先赋值
    public static float Dpi = 1f;

    public static int S(float v) { return (int)Math.Round(v * Dpi); }

    /// 圆角矩形路径
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// 两色按比例混合
    public static Color Mix(Color a, Color b, float t)
    {
        if (t < 0) t = 0; if (t > 1) t = 1;
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    /// 画一块圆角面板：先填空 Bg，再填圆角 Fill，最后描边
    public static void Panel(Graphics g, Rectangle r, int radius, Color bg, Color fill, Color border)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        g.SmoothingMode = SmoothingMode.None;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Round(r, radius))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border != Color.Empty)
                using (var p = new Pen(border)) g.DrawPath(p, path);
        }
    }

    // ---------- 只留竖向滚动条，去掉横向那条 ----------

    private const int GWL_STYLE = -16;
    private const int WS_HSCROLL = 0x00100000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr h, int idx, int val);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr h, int idx, IntPtr val);

    private static IntPtr GetStyleBits(IntPtr h)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_STYLE)
                                : (IntPtr)GetWindowLong32(h, GWL_STYLE);
    }

    private static void SetStyleBits(IntPtr h, IntPtr v)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_STYLE, v);
        else SetWindowLong32(h, GWL_STYLE, (int)v);
    }

    /// 去掉容器上「横向」滚动条，**竖向保留**。
    ///
    /// 为什么需要：Panel 的 AutoScroll 只要内容比容器宽一点点，
    /// 就会同时冒出横竖两条滚动条 —— 横向那条基本没用（内容本该换行），
    /// 却占掉一整条高度、也很难看。
    ///
    /// 做法是清掉窗口样式里的 WS_HSCROLL 位。这是 Win32 层面的开关，
    /// 清掉之后就算内容再宽也不会画横向滚动条，竖向不受影响。
    ///
    /// 注意：句柄必须已创建（AutoScroll 的滚动条是句柄建好后才挂上的），
    /// 所以要等窗口显示之后再调用。
    public static void NoHorizontalScroll(Control c)
    {
        if (c == null) return;
        try
        {
            if (c.IsHandleCreated)
            {
                long v = GetStyleBits(c.Handle).ToInt64();
                if ((v & WS_HSCROLL) != 0)
                {
                    SetStyleBits(c.Handle, (IntPtr)(v & ~WS_HSCROLL));
                    c.PerformLayout();   // 让布局重算，滚动条才会真的消失
                }
            }
        }
        catch { }
        try
        {
            foreach (Control ch in c.Controls) NoHorizontalScroll(ch);
        }
        catch { }
    }
}

/// 主窗口用的 Form。
///
/// ★ 开 WS_EX_COMPOSITED：整个窗口先在离屏缓冲里画完，再一次性合成到屏幕。
///
///   为什么要这个：切主题是「销毁旧窗口 → 重建新窗口」。不开这个扩展样式时，
///   新窗口是**一边画一边显示**的 —— 还没画到的区域会短暂露出**旧窗口的残留像素**，
///   表现出来就是设置页左侧菜单那一块闪出「详细信息」页的键名列。
///   离屏合成之后，看到的就是一整帧完整画面，不会有中间态。
internal sealed class W11Form : Form
{
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;      // WS_EX_COMPOSITED
            return cp;
        }
    }
}

/// 圆角卡片容器
internal sealed class W11Card : Panel
{
    public Color Bg = Color.Empty;       // 父容器底色
    public Color Fill = Color.Empty;     // 卡片底色
    public Color Border = Color.Empty;   // 描边
    public int Radius = 10;

    public W11Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color bg = Bg == Color.Empty ? BackColor : Bg;
        Color fill = Fill == Color.Empty ? BackColor : Fill;
        W11.Panel(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius, bg, fill, Border);
    }
}

/// 左侧导航项
internal sealed class W11NavItem : Panel
{
    public bool Selected;
    public Color Bg = Color.Empty, Card = Color.Empty, CardAlt = Color.Empty;
    public Color Accent = Color.Empty, Fg = Color.Empty, Dim = Color.Empty;
    private bool _hover;

    public event EventHandler Picked;

    public W11NavItem()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Picked != null) Picked(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color fill = Selected ? W11.Mix(Card, Accent, 0.16f) : (_hover ? Card : Bg);
        Color border = Selected ? W11.Mix(Card, Accent, 0.45f) : Color.Empty;
        W11.Panel(g, new Rectangle(0, 0, Width - 1, Height - 1), W11.S(6), Bg, fill, border);

        if (Selected)
        {
            var bar = new Rectangle(W11.S(2), W11.S(9), W11.S(3), Height - W11.S(18));
            using (var bp = W11.Round(bar, W11.S(2)))
            using (var bb = new SolidBrush(Accent))
                g.FillPath(bb, bp);
        }

        Color tc = Selected ? Fg : (_hover ? Fg : Dim);
        var textRect = new Rectangle(W11.S(16), 0, Width - W11.S(16) - W11.S(8), Height);
        TextRenderer.DrawText(g, Text, Font, textRect, tc,
            TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// 单选项行（Win11 设置那种：左边圆点，右边标题 + 副标题）
internal sealed class W11OptionRow : Panel
{
    public bool Selected;
    public string Title = "";
    public string Sub = "";
    public Color Bg = Color.Empty, Card = Color.Empty, CardAlt = Color.Empty;
    public Color Accent = Color.Empty, Fg = Color.Empty, Dim = Color.Empty;
    public int Radius = 8;
    private bool _hover;

    public event EventHandler Picked;

    public W11OptionRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Picked != null) Picked(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color fill = Selected
            ? W11.Mix(Card, Accent, 0.16f)
            : (_hover ? W11.Mix(Card, Fg, 0.06f) : Card);
        Color border = Selected ? W11.Mix(Card, Accent, 0.5f) : Color.Empty;
        W11.Panel(g, new Rectangle(0, 0, Width - 1, Height - 1), Radius, Bg, fill, border);

        // 左侧圆点
        int d = W11.S(18);
        int cy = Height / 2;
        var dot = new Rectangle(W11.S(16), cy - d / 2, d, d);
        if (Selected)
        {
            using (var b = new SolidBrush(Accent)) g.FillEllipse(b, dot);
            int id = W11.S(7);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, new Rectangle(dot.X + (d - id) / 2, dot.Y + (d - id) / 2, id, id));
        }
        else
        {
            using (var p = new Pen(W11.Mix(Card, Dim, 0.85f), W11.S(1.4f)))
                g.DrawEllipse(p, dot);
        }

        int tx = W11.S(46);
        int tw = Width - tx - W11.S(12);
        if (tw < W11.S(20)) tw = W11.S(20);

        if (Sub.Length > 0)
        {
            TextRenderer.DrawText(g, Title, Font,
                new Rectangle(tx, cy - W11.S(20), tw, W11.S(20)),
                Fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            using (Font sf = new Font(Font.FontFamily, Font.Size - 1.2f))
                TextRenderer.DrawText(g, Sub, sf,
                    new Rectangle(tx, cy + W11.S(1), tw, W11.S(18)),
                    Dim, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
        else
        {
            TextRenderer.DrawText(g, Title, Font,
                new Rectangle(tx, 0, tw, Height),
                Fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
    }
}

/// 圆角列表行（详细信息页用）
internal sealed class W11Row : Panel
{
    public Color Bg = Color.Empty;        // 父容器底色（填圆角外的部分）
    public Color Fill = Color.Empty;      // 行底色
    public Color Border = Color.Empty;
    public int Radius = 8;

    public W11Row()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color bg = Bg == Color.Empty ? BackColor : Bg;
        Color fill = Fill == Color.Empty ? BackColor : Fill;
        W11.Panel(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius, bg, fill, Border);
    }
}

/// Win11 风格的下拉选择框。
///
/// 为什么不用系统的 ComboBox：它的外观是 Win7 那一套（方角、灰边、
/// 老式箭头），放在这堆圆角卡片里很突兀。自己画一个。
///
/// 用法：
///   var dd = new W11DropDown { Titles = {...}, Subs = {...}, Selected = 1 };
///   dd.Picked += delegate(int i) { ... };
internal sealed class W11DropDown : Panel
{
    public string[] Titles = new string[0];
    public string[] Subs = new string[0];
    public string Placeholder = "请选择…";

    public int Selected = -1;

    public Color Bg = Color.Empty;         // 父容器底色
    public Color Fill = Color.Empty;       // 收起时的底色
    public Color Card = Color.Empty;       // 下拉面板底色
    public Color Line = Color.Empty;
    public Color Fg = Color.Empty, Dim = Color.Empty, Accent = Color.Empty;

    /// 选中项变化时回调（参数是新索引）
    public event Action<int> Picked;

    private bool _hover;
    private W11DropList _list;

    public W11DropDown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = W11.S(44);
    }

    public string SelectedTitle
    {
        get
        {
            if (Selected >= 0 && Selected < Titles.Length) return Titles[Selected];
            return Placeholder;
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 只标记按下，真正开关放在 MouseUp ——
        // ToolStripDropDown 会自动在"点到外面"时收起，而那条路径发生在 MouseDown 之后，
        // 在 MouseDown 里开/关会跟它打架，表现出来就是"瞬间收回又展开"。
        _pressed = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_pressed && ClientRectangle.Contains(e.Location)) OpenList();
        _pressed = false;
        base.OnMouseUp(e);
    }

    private bool _pressed;

    private void OpenList()
    {
        if (Titles.Length == 0) return;

        // ★ 防止「点一次开、再点一次瞬间收回又展开」：
        //   点击时下拉刚被关掉（收起动作发生在 MouseDown 之前），
        //   如果这时还认为"已经开着"，就会被当成"再点一次=关闭"。
        //   所以用一个时间戳：刚关掉后 250ms 内的点击一律当成"要打开"。
        if (_list != null && !_list.IsDisposed)
        {
            if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;
            try { _list.Close(); } catch { }
            _list = null;
            return;
        }
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;

        try
        {
            _list = new W11DropList(this);
            _list.Closed += delegate
            {
                _closedAt = DateTime.UtcNow;
                _list = null;
            };
            _list.Show(this, new Point(0, Height + W11.S(4)));
        }
        catch (Exception ex)
        {
            // 下拉打不开不该让整个程序崩
            _list = null;
            System.Diagnostics.Debug.WriteLine("下拉打开失败: " + ex.Message);
        }
    }

    /// 上次收起的时间 —— 用来避免"收起瞬间又被当成要打开"
    private DateTime _closedAt = DateTime.MinValue;

    internal void Choose(int idx)
    {
        if (idx < 0 || idx >= Titles.Length) return;
        Selected = idx;
        Invalidate();
        if (Picked != null) Picked(idx);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 2 || Height <= 2) return;
        var g = e.Graphics;
        Color bg = Bg == Color.Empty ? BackColor : Bg;
        Color fill = _hover ? W11.Mix(Fill, Fg, 0.10f) : Fill;
        W11.Panel(g, new Rectangle(0, 0, Width - 1, Height - 1), W11.S(7), bg, fill,
                  _hover ? W11.Mix(Line, Accent, 0.5f) : Line);

        int padL = W11.S(14), padR = W11.S(42);
        string sub = (Selected >= 0 && Selected < Subs.Length) ? Subs[Selected] : "";
        if (sub.Length > 0)
        {
            TextRenderer.DrawText(g, SelectedTitle, Font,
                new Rectangle(padL, W11.S(6), Width - padL - padR, W11.S(19)),
                Fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Font sf = new Font(Font.FontFamily, Font.Size - 1.2f))
                TextRenderer.DrawText(g, sub, sf,
                    new Rectangle(padL, W11.S(24), Width - padL - padR, W11.S(16)),
                    Dim, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        else
        {
            TextRenderer.DrawText(g, SelectedTitle, Font,
                new Rectangle(padL, 0, Width - padL - padR, Height),
                Fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // 右边细 chevron，和托盘菜单保持一致的风格
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = Width - W11.S(21), cy = Height / 2;
        using (var pen = new Pen(_hover ? Fg : Dim, W11.S(1.6f)))
        {
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
            g.DrawLines(pen, new Point[]
            {
                new Point(cx - W11.S(4), cy - W11.S(2)),
                new Point(cx,            cy + W11.S(3)),
                new Point(cx + W11.S(4), cy - W11.S(2))
            });
        }
        g.SmoothingMode = SmoothingMode.None;
    }
}

/// 下拉展开后的列表窗口。
/// 用 ToolStripDropDown 承载 —— 这样能盖住下面的内容，
/// 也不会被父面板的边界裁掉（普通 Panel 做不到）。
///
/// ★ 坑：ToolStripDropDown.Controls 是**只读**集合，
///   直接 Controls.Add() 会抛 NotSupportedException（"集合为只读"）。
///   正确做法是把 Panel 塞进 ToolStripControlHost，再 Items.Add()。
internal sealed class W11DropList : ToolStripDropDown
{
    private readonly W11DropDown _owner;

    public W11DropList(W11DropDown owner)
    {
        _owner = owner;
        AutoSize = false;
        Padding = new Padding(0);
        Margin = new Padding(0);
        DropShadowEnabled = true;
        BackColor = (owner.Card == Color.Empty) ? Color.FromArgb(30, 34, 45) : owner.Card;

        var host = new Panel { BackColor = BackColor, Padding = new Padding(0) };
        int h = 0;
        for (int i = 0; i < owner.Titles.Length; i++)
        {
            int idx = i;
            string sub = (i < owner.Subs.Length) ? owner.Subs[i] : "";
            var row = new W11DropRow
            {
                Owner = owner, Index = i,
                Title = owner.Titles[i], Sub = sub,
                Selected = (i == owner.Selected),
                Left = 0, Top = h, Width = owner.Width - 2,
                Height = W11.S(sub.Length > 0 ? 50 : 38),
                Font = owner.Font
            };
            row.Picked += delegate { owner.Choose(idx); Close(); };
            host.Controls.Add(row);
            h += row.Height + W11.S(2);
        }
        host.Height = h + W11.S(6);
        host.Width = owner.Width - 2;

        // ★ 不能用 Controls.Add —— 只能透过 ToolStripControlHost 放进 Items
        var item = new ToolStripControlHost(host);
        item.Margin = new Padding(0);
        item.Padding = new Padding(0);
        Items.Add(item);

        Size = new Size(owner.Width, h + W11.S(8));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TrySet(Handle, 33, 2);   // 圆角
    }
}

/// 下拉列表里的一行
internal sealed class W11DropRow : Panel
{
    public W11DropDown Owner;
    public int Index;
    public string Title = "", Sub = "";
    public bool Selected;

    public event EventHandler Picked;

    private bool _hover;

    public W11DropRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Picked != null) Picked(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color card   = (Owner != null && Owner.Card != Color.Empty) ? Owner.Card : BackColor;
        Color accent = (Owner != null && Owner.Accent != Color.Empty) ? Owner.Accent : Color.FromArgb(79, 140, 255);
        Color fg     = (Owner != null && Owner.Fg != Color.Empty) ? Owner.Fg : Color.White;
        Color dim    = (Owner != null && Owner.Dim != Color.Empty) ? Owner.Dim : Color.Gray;

        Color fill = Selected ? W11.Mix(card, accent, 0.22f)
                   : (_hover ? W11.Mix(card, fg, 0.10f) : card);
        W11.Panel(g, new Rectangle(0, 0, Width - 1, Height - 1), W11.S(6), card, fill, Color.Empty);

        int tx = W11.S(14);
        if (Selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(accent, W11.S(2f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new Point[]
                {
                    new Point(W11.S(11), Height / 2),
                    new Point(W11.S(14), Height / 2 + W11.S(3)),
                    new Point(W11.S(19), Height / 2 - W11.S(4))
                });
            }
            g.SmoothingMode = SmoothingMode.None;
            tx = W11.S(28);
        }

        int tw = Width - tx - W11.S(10);
        if (tw < W11.S(20)) tw = W11.S(20);
        if (Sub.Length > 0)
        {
            TextRenderer.DrawText(g, Title, Font,
                new Rectangle(tx, W11.S(5), tw, W11.S(20)),
                fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            using (Font sf = new Font(Font.FontFamily, Font.Size - 1.2f))
                TextRenderer.DrawText(g, Sub, sf,
                    new Rectangle(tx, W11.S(25), tw, W11.S(18)),
                    dim, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
        else
        {
            TextRenderer.DrawText(g, Title, Font,
                new Rectangle(tx, 0, tw, Height),
                fg, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
    }
}

/// 圆角小按钮
internal sealed class W11Button : Panel
{
    public Color Bg = Color.Empty;
    public Color Fill = Color.Empty, FillHover = Color.Empty, FillDown = Color.Empty;
    public Color TextColor = Color.Empty, Border = Color.Empty;
    public int Radius = 6;
    private bool _hover, _down;

    public event EventHandler Clicked;

    public W11Button()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _down = false; Invalidate();
        if (_hover && Clicked != null) Clicked(this, EventArgs.Empty);
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 2 || Height <= 2) return;
        Color bg = Bg == Color.Empty ? BackColor : Bg;
        Color fill = _down ? FillDown : (_hover ? FillHover : Fill);
        Color tc = TextColor;
        if (!Enabled)
        {
            // 禁用态：往底色方向褪掉，字也变淡
            fill = W11.Mix(fill, bg, 0.62f);
            tc = W11.Mix(TextColor, bg, 0.55f);
        }
        W11.Panel(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius, bg, fill, Border);
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(0, 0, Width, Height), tc,
            TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
