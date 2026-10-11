// Win11Tabs.cs -- 自绘选项卡
//
// ★ 本文件按原版 Win11TabControl 的 IL **逐条还原**（IL 见 workspace\_还原依据\原版-控件-IL.txt）
//
// 原版 OnPaint 的真实结构（IL 偏移 -> 语义）：
//    26:  g.Clear(_p.Bg)                                    ← 清整块
//    32:  Rectangle disp = DisplayRectangle
//    76:  new Rectangle(disp.X-2, disp.Y-1, disp.Width+4, disp.Height+3)   ← 页面区外框
//   103:  Round(rect, 8)
//   149:  g.FillPath(Card 画刷, 外框路径)                    ← 填充
//   158:  g.DrawPath(Line 画笔, 外框路径)                    ← 描边
//   203:  for (i = 0; i < TabCount; i++)                    ← 遍历所有标签
//   214:    Rectangle r = GetTabRect(i)
//   227:    r.Inflate(-3, -4)                               ← 注意是 -3, -4
//   235:    取 SelectedIndex 判断 sel
//   247:    用 _hotIndex 判断 hot
//   269:    if (sel || hot) Round(r, 7) ; FillPath(sel ? Card : PillBg)
//   361:      选中时画底部指示条：宽 r.Width*0.22*2 居中，高 3，Round 2，Accent
//   497:    string text = TabPages[i].Text
//   546:    new Font("Microsoft YaHei UI", <size>, <style>)
//   657:    DrawString(text, font, brush, rect, StringFormat 居中)
//   727: 循环结束
//
// ⚠ 上一版我把 g.Clear 和页面区填充删掉了 —— 那是错的。原版两个都有。
//   之前"页面空白"的真因不在 OnPaint，而是我没实现 TabPage 的内容构建。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class Win11TabControl : TabControl
{
    private readonly Palette _p;
    private int _hotIndex = -1;

    // GDI 缓存（原版每次重绘都 new，这里做缓存只为性能，绘制结果不变）
    private Font _fSel, _fNorm;
    private SolidBrush _bCard, _bPill, _bAccent;
    private Pen _penLine;
    private StringFormat _sf;
    private readonly List<IDisposable> _disposables = new List<IDisposable>();

    public Win11TabControl(Palette p)
    {
        _p = p;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(150, 40);
        Padding = new Point(20, 6);
        BuildCache();
        UpdateStyles();
    }

    private void BuildCache()
    {
        DisposeCache();
        _fSel = NewFont(10.5f, FontStyle.Bold);
        _fNorm = NewFont(10f, FontStyle.Regular);
        _bCard = new SolidBrush(_p.Card);
        _bPill = new SolidBrush(_p.PillBg);
        _bAccent = new SolidBrush(_p.Accent);
        _penLine = new Pen(_p.Line);
        _sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        _disposables.AddRange(new IDisposable[] { _fSel, _fNorm, _bCard, _bPill, _bAccent, _penLine, _sf });
    }

    private static Font NewFont(float sz, FontStyle st)
    {
        try { return new Font("Microsoft YaHei UI", sz, st); }
        catch { return new Font(FontFamily.GenericSansSerif, sz, st); }
    }

    private void DisposeCache()
    {
        foreach (var d in _disposables) { try { d.Dispose(); } catch { } }
        _disposables.Clear();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hotIndex) { _hotIndex = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hotIndex != -1) { _hotIndex = -1; Invalidate(); }
    }

    private int HitTest(Point pt)
    {
        for (int i = 0; i < TabCount; i++)
            if (GetTabRect(i).Contains(pt)) return i;
        return -1;
    }

    /// 逐条对应原版 OnPaint
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // IL 26: Clear(Bg)
        g.Clear(_p.Bg);

        // IL 32-158: 页面区外框（DisplayRectangle 外扩，圆角 8，Card 填充 + Line 描边）
        Rectangle disp = DisplayRectangle;
        Rectangle page = new Rectangle(disp.X - 2, disp.Y - 1, disp.Width + 4, disp.Height + 3);
        if (page.Width > 8 && page.Height > 8)
        {
            using (GraphicsPath path = Round(page, 8))
            {
                g.FillPath(_bCard, path);
                g.DrawPath(_penLine, path);
            }
        }

        // IL 203-727: 逐个标签
        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            // IL 223-227: Inflate(-3, -4)
            r.Inflate(-3, -4);
            bool sel = (i == SelectedIndex);
            bool hot = (i == _hotIndex);

            if (sel || hot)
            {
                // IL 269-317: Round(r, 7) + FillPath(sel ? Card : PillBg)
                using (GraphicsPath path = Round(r, 7))
                    g.FillPath(sel ? _bCard : _bPill, path);

                if (sel)
                {
                    // IL 361-463: 底部指示条
                    //   宽 = r.Width * 0.22 * 2，居中；高 3；Round 2；Accent
                    int half = (int)(r.Width * 0.22);
                    Rectangle bar = new Rectangle(r.X + r.Width / 2 - half, r.Bottom - 3, half * 2, 3);
                    using (GraphicsPath bp = Round(bar, 2))
                        g.FillPath(_bAccent, bp);
                }
            }

            // IL 497-705: 标签文字，居中
            string text = TabPages[i].Text;
            using (SolidBrush tb = new SolidBrush(sel ? _p.Fg : _p.Dim))
            {
                Rectangle tr = new Rectangle(r.X, r.Y, r.Width, r.Height - (sel ? 3 : 0));
                g.DrawString(text, sel ? _fSel : _fNorm, tb, tr, _sf);
            }
        }
    }

    /// 对应原版 Win11TabControl.Round
    private static GraphicsPath Round(Rectangle b, int radius)
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeCache();
        base.Dispose(disposing);
    }
}
