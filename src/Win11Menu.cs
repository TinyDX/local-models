// Win11Menu.cs -- Win11 风格菜单（结构照搬原版：Win11ColorTable / Win11Renderer / Win11Menu）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class Win11ColorTable : ProfessionalColorTable
{
    private readonly Palette _p;
    public Win11ColorTable(Palette p) { _p = p; }
    public override Color ToolStripDropDownBackground { get { return _p.Card; } }
    public override Color ImageMarginGradientBegin { get { return _p.Card; } }
    public override Color ImageMarginGradientMiddle { get { return _p.Card; } }
    public override Color ImageMarginGradientEnd { get { return _p.Card; } }
    public override Color MenuBorder { get { return _p.Line; } }
    public override Color MenuItemBorder { get { return Color.Transparent; } }
    public override Color MenuItemSelected { get { return _p.PillBg; } }
    public override Color MenuItemSelectedGradientBegin { get { return _p.PillBg; } }
    public override Color MenuItemSelectedGradientEnd { get { return _p.PillBg; } }
    public override Color MenuItemPressedGradientBegin { get { return _p.PillBg; } }
    public override Color MenuItemPressedGradientEnd { get { return _p.PillBg; } }
    public override Color SeparatorDark { get { return _p.Line; } }
    public override Color SeparatorLight { get { return _p.Line; } }
}

internal sealed class Win11Renderer : ToolStripProfessionalRenderer
{
    private readonly Palette _p;
    public Win11Renderer(Palette p) : base(new Win11ColorTable(p)) { _p = p; RoundedEdges = true; }

    /// 当前项不用对勾 —— 改为底色比卡片略深一点。
    /// （Windows 11 的菜单也不用对勾，靠底色区分。）
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

    /// Checked 只在 ToolStripMenuItem 上，ToolStripItem 没有
    private static bool IsChecked(ToolStripItem it)
    {
        ToolStripMenuItem mi = it as ToolStripMenuItem;
        return mi != null && mi.Checked;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ToolStripItem it = e.Item;
        Rectangle r = new Rectangle(4, 1, it.Width - 8, it.Height - 2);
        SmoothingMode old = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (it.Selected || it.Pressed)
        {
            using (GraphicsPath path = Rounded(r, 5))
            using (SolidBrush b = new SolidBrush(it.Pressed ? _p.Line : _p.PillBg))
                e.Graphics.FillPath(b, path);
        }
        else if (IsChecked(it))
        {
            // 当前选中项：底色加深一点。
            // 深色主题下"加深"会糊成一团，所以那边改成往亮里带一点点，
            // 效果一样是"和别的项不一样"。
            Color cur = _p.Dark
                ? W11.Mix(_p.Card, Color.White, 0.08f)
                : W11.Mix(_p.Card, Color.Black, 0.13f);
            using (GraphicsPath path = Rounded(r, 5))
            using (SolidBrush b = new SolidBrush(cur))
                e.Graphics.FillPath(b, path);
        }
        e.Graphics.SmoothingMode = old;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle r = new Rectangle(12, e.Item.Height / 2, e.Item.Width - 24, 1);
        using (SolidBrush b = new SolidBrush(_p.Line)) e.Graphics.FillRectangle(b, r);
    }

    /// 菜单文字：强制用主题色。
    ///
    /// ToolStripProfessionalRenderer 默认取 SystemColors.ControlText ——
    /// 深色主题下那就是**黑字压在深色底上**，几乎看不见。
    /// 注意子菜单（「外观」展开的那一层）也是走这里，所以一并修好。
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _p.Fg : _p.Dim;
        base.OnRenderItemText(e);
    }

    /// 子菜单箭头：系统默认是黑色实心三角，深色下同样看不清。
    /// 换成 Win11 那种细的「>」chevron。
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        Rectangle r = e.ArrowRectangle;
        Graphics g = e.Graphics;
        SmoothingMode old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cx = r.Left + r.Width / 2 + 1;
        int cy = r.Top + r.Height / 2;
        using (Pen pen = new Pen(_p.Dim, 1.5f))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLines(pen, new Point[]
            {
                new Point(cx - 2, cy - 4),
                new Point(cx + 2, cy),
                new Point(cx - 2, cy + 4)
            });
        }
        g.SmoothingMode = old;
    }

    /// 子菜单左侧那条灰色「图标栏」也要去掉 —— 我们没有图标，
    /// 留着就是一条难看的灰带，不像 Win11。
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

    public static GraphicsPath Rounded(Rectangle b, int radius)
    {
        int d = radius * 2;
        GraphicsPath p = new GraphicsPath();
        if (b.Width < d || b.Height < d) { p.AddRectangle(b); return p; }
        p.AddArc(b.X, b.Y, d, d, 180, 90);
        p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

internal sealed class Win11Menu : ContextMenuStrip
{
    private readonly Palette _p;

    public Win11Menu(Palette p)
    {
        _p = p;
        Renderer = new Win11Renderer(p);
        BackColor = p.Card;
        ForeColor = p.Fg;
        Font = new Font("Microsoft YaHei UI", 10F);
        ShowImageMargin = false;
        Padding = new Padding(4, 6, 4, 6);
        DropShadowEnabled = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.TrySet(Handle, 33, 2);              // 主菜单圆角
        RoundSubMenus(Items);                     // 子菜单是**另一个窗口**，也得单独设
    }

    /// 递归给所有子菜单设圆角。
    /// ToolStripDropDownItem.DropDown 是一个独立的顶层窗口（ToolStripDropDownMenu），
    /// 不给它设 DWMWA_WINDOW_CORNER_PREFERENCE 的话，展开那一层还是方角。
    private static void RoundSubMenus(ToolStripItemCollection items)
    {
        foreach (ToolStripItem it in items)
        {
            ToolStripDropDownItem di = it as ToolStripDropDownItem;
            if (di == null) continue;
            ToolStripDropDown dd = di.DropDown;
            if (dd == null) continue;
            ToolStripDropDown captured = dd;
            dd.HandleCreated += delegate { Theme.TrySet(captured.Handle, 33, 2); };
            if (dd.IsHandleCreated) Theme.TrySet(dd.Handle, 33, 2);
            RoundSubMenus(di.DropDownItems);
        }
    }
}
