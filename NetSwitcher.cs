// NetSwitcher v3 —— 手机热点一键切换 + 内网识别 + 默认出口选择
// 编译: 双击 build.bat（自动定位 .NET Framework 4.x 的 csc.exe）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace NetSwitcher
{
    // ==================== 设计令牌 ====================
    // 浅灰蓝画布 + 纯白圆角卡片 + 极淡投影，主色一块蓝做顶栏；
    // 语义色只出现在状态标签上（淡底 + 同色字，不描边）。
    public static class T
    {
        // ---- 面：画布 / 白卡 / 悬停 / 选中 / 输入底 ----
        public static readonly Color Page = Color.FromArgb(0xF5, 0xF7, 0xFB);    // 画布底
        public static readonly Color Card = Color.FromArgb(0xFF, 0xFF, 0xFF);    // 卡片面
        public static readonly Color Raised = Color.FromArgb(0xE4, 0xEE, 0xFF);  // 选中（淡蓝，比原来略深一档）
        public static readonly Color Hover = Color.FromArgb(0xFA, 0xFB, 0xFE);   // 普通悬停
        public static readonly Color Field = Color.FromArgb(0xF0, 0xF3, 0xF8);   // 输入底 / 页码底
        // 选中态描边：比普通发丝线更蓝、更明确，配合淡蓝底让「这张被选中」一眼可见
        public static readonly Color SelectedBorder = Color.FromArgb(0x9C, 0xBE, 0xF5);
        // ---- 顶栏 hero：一块蓝，和小程序首页 .hero 同款渐变 ----
        // 克制化：把两端色拉近，去掉高饱和的亮蓝头，观感更沉静、不发飘。
        public static readonly Color HeroTop = Color.FromArgb(0x46, 0x7D, 0xE9);
        public static readonly Color HeroBottom = Color.FromArgb(0x35, 0x66, 0xD8);
        public static readonly Color OnHero = Color.FromArgb(0xFF, 0xFF, 0xFF);
        public static readonly Color OnHeroMuted = Color.FromArgb(220, 0xFF, 0xFF, 0xFF);
        public static readonly Color OnHeroFaint = Color.FromArgb(168, 0xFF, 0xFF, 0xFF);
        // hero 上的警示字：从突兀的黄(#FFE2A0)收敛成 hero 蓝同族的高亮浅蓝，冷调统一
        public static readonly Color OnHeroSoft = Color.FromArgb(0xFF, 0xC9, 0xDC);

        // ---- 发丝线：浅灰实线，和 .card / .field 的边框同色 ----
        public static readonly Color Hairline = Color.FromArgb(0xED, 0xF0, 0xF5);
        public static readonly Color HairlineStrong = Color.FromArgb(0xD6, 0xE4, 0xFF);

        // ---- 四级文字 ----
        public static readonly Color TextPrimary = Color.FromArgb(0x1F, 0x24, 0x30);
        public static readonly Color TextSecondary = Color.FromArgb(0x5A, 0x64, 0x74);
        public static readonly Color TextMuted = Color.FromArgb(0x8A, 0x94, 0xA6);
        public static readonly Color TextFaint = Color.FromArgb(0xA0, 0xA8, 0xB8);

        // ---- 语义色：收敛到同一饱和度，冷调为主，绿/橙/红只作点缀 ----
        public static readonly Color AccentNet = Color.FromArgb(0x35, 0x66, 0xD8);
        public static readonly Color AccentInner = Color.FromArgb(0x2E, 0xA8, 0x7F);
        public static readonly Color Warn = Color.FromArgb(0xE8, 0x92, 0x3C);        // 琥珀橙

        // ---- 标签淡底（.tag-* 的 background）----
        public static readonly Color AccentNetSoft = Color.FromArgb(0xEA, 0xEF, 0xFC);
        public static readonly Color AccentInnerSoft = Color.FromArgb(0xE9, 0xF6, 0xF0);

        // ---- 字体：Segoe UI 为主，等宽只给数字；中文自动回退微软雅黑 ----
        public const string Family = "Segoe UI";
        public const string FamilyCn = "Microsoft YaHei UI";
        public const string NumFamily = "Consolas";
        public static readonly Font FNum = new Font(NumFamily, 9.5f);
        public static readonly Font FNumSm = new Font(NumFamily, 8.5f);

        public static Color WithAlpha(Color c, int a) { return Color.FromArgb(a, c); }

        /// <summary>
        /// 按字间距绘制文本。GDI+ 没有原生字距，这里逐字符绘制，每个字符后
        /// 加固定的 tracking 像素。小字号标签加 0.8px，标题加 0.4px，
        public static void DrawSpaced(Graphics g, string text, Font font, Brush brush,
                                      float x, float y, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return;
            float cx = x;
            foreach (char ch in text)
            {
                g.DrawString(ch.ToString(), font, brush, cx, y);
                cx += g.MeasureString(ch.ToString(), font, int.MaxValue, StringFormat.GenericTypographic).Width + tracking;
            }
        }

        /// <summary>测量带字间距文本的总宽（和 DrawSpaced 完全同参）</summary>
        public static float MeasureSpaced(Graphics g, string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            float w = 0f;
            foreach (char ch in text)
                w += g.MeasureString(ch.ToString(), font, int.MaxValue, StringFormat.GenericTypographic).Width + tracking;
            return w > 0 ? w - tracking : 0f;
        }

        /// <summary>圆角：按尺寸分级，内圆角 = 外圆角减内边距</summary>
        public const int RBadge = 6, RCard = 12, RPanel = 16;

        // ---- 动效节奏：一套时长，全局复用（对应设计规范里的 motion tokens）----
        // 交互反馈用 fast（悬停/按下），悬停这类高频动作必须够快，慢了会显得粘手。
        public const int DurFast = 120;

        /// <summary>
        /// 缓动：ease-out-cubic。键盘/鼠标操作需要「立刻响应、缓缓收住」，
        /// 所以入场比出场快，不要用线性或 ease-in。
        /// </summary>
        public static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        /// <summary>线性插值，压到 0..1 防止过冲</summary>
        public static float Lerp(float a, float b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return a + (b - a) * t;
        }

        /// <summary>按进度混合两个颜色（用于悬停底色平滑过渡）</summary>
        public static Color Blend(Color from, Color to, float t)
        {
            t = t <= 0f ? 0f : (t >= 1f ? 1f : t);
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * t),
                (int)Math.Round(from.R + (to.R - from.R) * t),
                (int)Math.Round(from.G + (to.G - from.G) * t),
                (int)Math.Round(from.B + (to.B - from.B) * t));
        }

        /// <summary>
        /// 把 0..1 的进度换算成动画曲线值。
        /// 所有控件都走这个函数，动效手感才会统一，不会这个快那个慢。
        public static float Animate(float startMs, int durationMs)
        {
            float elapsed = Environment.TickCount - startMs;
            if (durationMs <= 0) return 1f;
            return EaseOut(elapsed / durationMs);
        }

        /// <summary>
        /// 极淡投影：卡片浮在浅灰画布上，靠一层很淡的蓝灰影拉开层次。
        /// 层数多一层、每层更淡，边缘就不会出现生硬的色阶。
        /// </summary>
        public static void DropShadow(Graphics g, Rectangle r, int rad)
        {
            // 从外到内：越靠外越淡、越往下偏移，模拟光从上方来
            int[] alphas = { 5, 8, 12 };
            for (int i = alphas.Length; i >= 1; i--)
            {
                var rr = new Rectangle(r.X + 1, r.Y + i, r.Width - 2, r.Height - 2);
                using (var p = RoundedRect(rr, rad))
                using (var b = new SolidBrush(Color.FromArgb(alphas[i - 1], 0x1F, 0x24, 0x30)))
                    g.FillPath(b, p);
            }
        }

        /// <summary>统一画圆角矩形路径（所有卡片/胶囊共用一个实现，圆角才会一致）</summary>
        public static GraphicsPath RoundedRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(2, rad * 2);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ==================== 弹出菜单 ====================
    /// <summary>
    /// 托盘菜单专用配色（托盘图标的 ContextMenuStrip 必须是 ToolStrip 系，
    /// 不能换成自绘的 FlatMenu，所以这里只把颜色对齐面板，几何沿用系统）。
    /// 面板内的右键菜单一律走 FlatMenu。
    public class LightMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return T.Field; } }
        public override Color MenuItemSelectedGradientBegin { get { return T.Field; } }
        public override Color MenuItemSelectedGradientEnd { get { return T.Field; } }
        public override Color MenuItemBorder { get { return T.Field; } }
        public override Color MenuBorder { get { return T.Hairline; } }
        public override Color MenuItemPressedGradientBegin { get { return T.Field; } }
        public override Color MenuItemPressedGradientEnd { get { return T.Field; } }
        public override Color ToolStripDropDownBackground { get { return T.Card; } }
        public override Color ImageMarginGradientBegin { get { return T.Card; } }
        public override Color ImageMarginGradientMiddle { get { return T.Card; } }
        public override Color ImageMarginGradientEnd { get { return T.Card; } }
        public override Color SeparatorDark { get { return T.Hairline; } }
        public override Color SeparatorLight { get { return T.Card; } }
        public override Color CheckBackground { get { return T.AccentNetSoft; } }
        public override Color CheckSelectedBackground { get { return T.AccentNetSoft; } }
    }

    /// <summary>
    /// 自绘圆角菜单。
    /// 为什么不用 ContextMenuStrip：它是直角窗、行高 22、分隔线通栏、悬停是一条
    /// 生硬的灰底，和这套「白卡 + 12 圆角 + 极淡影」的界面摆在一起像两个时代的产物；
    /// 强行改 ProfessionalColorTable 也只能换颜色，改不了那套几何。
    /// 这里用一张无边框窗自己画：12 圆角、38 行高、左侧系统字形图标、
    /// 悬停淡蓝胶囊、主操作用主蓝加粗，和卡片是同一套语言。
    /// 键盘（↑↓ Enter Esc）、点菜单外关闭、超出屏幕自动翻转，都按系统菜单的行为补上了。
    /// </summary>
    public class FlatMenu : Form
    {
        class Item
        {
            public string Text = "";
            public string Hint = "";     // 右侧浅色补充信息（网段 / IP / 状态）
            public char Glyph;           // 系统图标字形，0 = 不画
            public bool Sep;             // 分隔线
            public bool Bold;
            public bool Accent;          // 主操作：主蓝加粗
            public bool Enabled = true;
            public Action Act;
            public Rectangle Rect;
        }

        const int RowH = 38, IconBox = 18, IconGap = 8, PadX = 9, PadY = 7, SepH = 11;
        const int MinW = 208, MaxW = 460;

        static readonly Font FText = new Font(T.FamilyCn, 9.5f);
        static readonly Font FTextBold = new Font(T.FamilyCn, 9.5f, FontStyle.Bold);
        static readonly Font FHint = new Font(T.Family, 8.5f);

        readonly List<Item> items = new List<Item>();
        int hover = -1;
        bool closing;

        public FlatMenu()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = T.Card;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        /// <summary>系统投影。窗口是圆角 Region，投影由系统画在窗外，不会被裁掉。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW：不进 Alt+Tab
                return cp;
            }
        }

        // ---------- 组装 ----------
        public void Sep() { items.Add(new Item { Sep = true }); }

        public void Add(string text, char glyph, Action act, string hint = null,
                        bool bold = false, bool accent = false, bool enabled = true)
        {
            items.Add(new Item
            {
                Text = text, Glyph = glyph, Act = act, Hint = hint ?? "",
                Bold = bold, Accent = accent, Enabled = enabled
            });
        }

        /// <summary>按内容量一次尺寸，并给每行分配矩形。显示前一定要调。</summary>
        void Relayout()
        {
            int w = 0;
            foreach (var it in items)
            {
                if (it.Sep) continue;
                int tw = TextRenderer.MeasureText(it.Text, it.Bold ? FTextBold : FText).Width;
                int row = IconBox + IconGap + tw;
                if (it.Hint.Length > 0)
                    row += 14 + TextRenderer.MeasureText(it.Hint, FHint).Width;
                if (row > w) w = row;
            }
            w = Math.Max(MinW, Math.Min(MaxW, w + PadX * 2 + 6));

            int y = PadY;
            foreach (var it in items)
            {
                int h = it.Sep ? SepH : RowH;
                it.Rect = new Rectangle(0, y, w, h);
                y += h;
            }
            ClientSize = new Size(w, y + PadY);
            ApplyRegion();
        }

        void ApplyRegion()
        {
            try
            {
                using (var p = T.RoundedRect(new Rectangle(0, 0, Width, Height), T.RCard))
                    Region = new Region(p);
            }
            catch { }
        }

        /// <summary>离屏排版：只按当前条目算尺寸并排好行，不显示窗口。给视觉核对/截图用。</summary>
        public void LayoutOffscreen() { Relayout(); }

        /// <summary>离屏绘制：把整张菜单画到已有 Graphics 的指定位置。</summary>
        public void PaintOffscreen(Graphics g, Point at)
        {
            using (var bmp = new Bitmap(Width, Height))
            {
                using (var bg = Graphics.FromImage(bmp))
                {
                    bg.Clear(Color.Transparent);
                    using (var pe = new PaintEventArgs(bg, new Rectangle(0, 0, Width, Height)))
                        OnPaint(pe);
                }
                g.DrawImage(bmp, at);
            }
        }

        // ---------- 显示 ----------
        public void ShowAt(Control anchor, Point p)
        {
            if (anchor == null) { ShowAtScreen(Cursor.Position); return; }
            ShowAtScreen(anchor.PointToScreen(p));
        }

        public void ShowAtScreen(Point pt)
        {
            Relayout();
            var wa = Screen.FromPoint(pt).WorkingArea;
            int x = pt.X, y = pt.Y;
            // 右下放不下就朝反方向翻，和系统菜单一致（不要直接顶到屏幕边缘之外）
            if (x + Width > wa.Right) x = Math.Max(wa.Left, pt.X - Width);
            if (y + Height > wa.Bottom) y = Math.Max(wa.Top, pt.Y - Height);
            Location = new Point(x, y);

            Show();
            Activate();
            hover = -1;
            Invalidate();
        }

        // ---------- 交互 ----------
        int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Sep || !items[i].Enabled) continue;
                if (items[i].Rect.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != -1) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) { CloseMenu(); return; }
            int h = HitTest(e.Location);
            if (h < 0) return;
            var act = items[h].Act;
            CloseMenu();
            // 菜单先关干净，再执行动作：动作里可能弹 UAC / MessageBox，
            // 顺序反过来的话菜单会盖在对话框上面，看起来像卡死。
            if (act != null)
            {
                try { act(); } catch { }
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // 点到别处（另一个卡片 / 桌面）就收起，和系统菜单的行为一样
            if (!closing) CloseMenu();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { CloseMenu(); return; }
            if (e.KeyCode == Keys.Enter) { e.Handled = true; ActivateHover(); return; }
            int dir = 0;
            if (e.KeyCode == Keys.Down) dir = 1;
            else if (e.KeyCode == Keys.Up) dir = -1;
            else return;
            e.Handled = true;
            if (items.Count == 0) return;

            int i = hover;
            for (int n = 0; n < items.Count; n++)
            {
                i += dir;
                if (i < 0) i = items.Count - 1;
                if (i >= items.Count) i = 0;
                if (!items[i].Sep && items[i].Enabled) break;
            }
            hover = i;
            Invalidate();
        }

        void ActivateHover()
        {
            if (hover < 0 || hover >= items.Count || items[hover].Sep || !items[hover].Enabled) return;
            var act = items[hover].Act;
            CloseMenu();
            if (act != null) { try { act(); } catch { } }
        }

        void CloseMenu()
        {
            if (closing) return;
            closing = true;
            try { Close(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            e.Cancel = false;
            base.OnFormClosing(e);
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var body = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = T.RoundedRect(body, T.RCard))
            {
                using (var bg = new SolidBrush(T.Card)) g.FillPath(bg, path);
                using (var pen = new Pen(T.Hairline, 1f)) g.DrawPath(pen, path);
            }

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it.Sep)
                {
                    int y = it.Rect.Y + it.Rect.Height / 2;
                    using (var pen = new Pen(T.Hairline, 1f))
                        g.DrawLine(pen, PadX + 4, y, Width - PadX - 4, y);
                    continue;
                }

                var r = it.Rect;
                bool hot = (i == hover) && it.Enabled;

                // 悬停：淡蓝胶囊，只覆盖行内，不铺满整宽，圆角让它是「一块」而不是「一条」
                if (hot)
                {
                    var hb = new Rectangle(r.X + 4, r.Y + 2, r.Width - 8, r.Height - 4);
                    using (var hp = T.RoundedRect(hb, T.RBadge + 2))
                    using (var brush = new SolidBrush(T.Field))
                        g.FillPath(brush, hp);
                }

                Color fg = !it.Enabled ? T.TextFaint
                         : it.Accent ? T.AccentNet
                         : (hot ? T.TextPrimary : T.TextSecondary);
                Color iconFg = !it.Enabled ? T.TextFaint
                             : it.Accent ? T.AccentNet
                             : (hot ? T.AccentNet : T.TextMuted);

                float iconX = r.X + PadX;
                float iconY = r.Y + (RowH - IconBox) / 2f;
                if (it.Glyph != '\0')
                    Glyph.Draw(g, it.Glyph, new RectangleF(iconX, iconY, IconBox, IconBox), iconFg, 1f);

                float textX = iconX + IconBox + IconGap;
                float textW = r.Width - PadX - textX;
                if (it.Hint.Length > 0)
                    textW -= TextRenderer.MeasureText(g, it.Hint, FHint).Width + 14;

                using (var b = new SolidBrush(fg))
                using (var fmt = new StringFormat
                {
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                    g.DrawString(it.Text, it.Bold ? FTextBold : FText, b,
                        new RectangleF(textX, r.Y, Math.Max(20, textW), RowH), fmt);

                if (it.Hint.Length > 0)
                {
                    using (var b = new SolidBrush(it.Enabled ? T.TextMuted : T.TextFaint))
                    using (var fmt = new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                        g.DrawString(it.Hint, FHint, b,
                            new RectangleF(r.X, r.Y, r.Width - PadX, RowH), fmt);
                }
            }
        }
    }

    // ==================== 数据模型 ====================
    public class Hotspot
    {
        public string SSID = "";
        public bool Visible;
        public bool Saved;
        public int Signal = -1;
        public string Kind = "other";
        public bool Connected;
    }

    public class LanIf
    {
        public string Alias = "";
        public string Desc = "";
        public string IP = "";
        public int Prefix = 0;
        public string Mask = "";
        public string Gateway = "";
        public bool GwReachable;        // 网关有回应
        public string ReachHow = "";    // 靠什么确认的：ping / ARP / TCP80
        public bool InArp;              // ARP 表里有它 = 同一广播域
        public bool IsDefaultEgress;
        public long SpeedMbps;
        public bool IsVirtual;

        public bool CanInternet;        // 走这块网卡能不能上外网（源地址绑定实测）
        public List<string> Subnets = new List<string>();  // 它直连的网段
        public bool HasPinnedRoute;     // 已经挂着一条永久路由
        public List<string> PinnedSubnets = new List<string>(); // 它上面真实存在的持久路由网段
        public List<string> IntranetRoutes = new List<string>();  // 走它的非默认路由（内网专线）
        public int IfIndex;
        public int Metric;
        public int RouteMetric;         // 默认路由自带的路由度量
        public int TotalMetric;         // 总度量 = 路由度量 + 接口度量，Windows 就是按这个挑出口
    }

    public class Snapshot
    {
        public List<Hotspot> Hotspots = new List<Hotspot>();
        public List<LanIf> Lans = new List<LanIf>();
        public string WifiSSID = "";
        public int WifiSignal = -1;
        public string BestAlias = "";
        public string BestIP = "";
        public List<string> PinnedRoutes = new List<string>();  // 已固定的内网网段
        public List<string> IntranetRoutes = new List<string>(); // 全局：当前生效的内网专线网段
        public string IntranetAlias = "";                        // 内网资料实际走哪块网卡
        public string IntranetIP = "";
        public DateTime At = DateTime.Now;
    }

    // ==================== 配置 ====================
    public static class Cfg
    {
        public static string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetSwitcher");
        public static string SlotsFile = Path.Combine(Dir, "slots.txt");
        public static string UiFile = Path.Combine(Dir, "ui.txt");
        public static string PrefFile = Path.Combine(Dir, "pref.txt");
        public static string NotesFile = Path.Combine(Dir, "notes.txt");

        // ==================== 网络备注 ====================
        // 用户自己给某个网络起的名字，比如「宿舍热点」「实验室专线」。
        // 键是「wifi:SSID」或「lan:网卡名」，这样热点和内网共用一份文件也互不串。
        // 缓存在内存里：卡片每画一次都要读它，不能每次都摸硬盘。
        public static Dictionary<string, string> Notes = LoadNotes();
        public static string WifiKey(string ssid) { return "wifi:" + (ssid ?? ""); }
        public static string LanKey(string alias) { return "lan:" + (alias ?? ""); }

        public static Dictionary<string, string> LoadNotes()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!System.IO.File.Exists(NotesFile)) return d;
                // 格式：键<TAB>备注。用制表符分隔，SSID / 网卡名里出现 = 号也不会解析错。
                foreach (var raw in System.IO.File.ReadAllLines(NotesFile, Encoding.UTF8))
                {
                    var line = raw;
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string k = line.Substring(0, tab).Trim();
                    string v = line.Substring(tab + 1).Trim();
                    if (k.Length == 0 || v.Length == 0) continue;
                    d[k] = v;
                }
            }
            catch { }
            return d;
        }

        /// <summary>读一条备注；没有就返回空串</summary>
        public static string Note(string key)
        {
            string v;
            return key != null && Notes.TryGetValue(key, out v) ? v : "";
        }

        /// <summary>写一条备注；传空串等于删除。写完立刻落盘，并同步内存缓存。</summary>
        public static void SetNote(string key, string text)
        {
            if (string.IsNullOrEmpty(key)) return;
            text = (text ?? "").Trim();
            if (text.Length == 0) Notes.Remove(key);
            else Notes[key] = text;

            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.AppendLine("# 网络备注（键<TAB>备注），程序自己写，手改也行");
                foreach (var kv in Notes) sb.AppendLine(kv.Key + "\t" + kv.Value);
                System.IO.File.WriteAllText(NotesFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }

        public static Point LoadUi()
        {
            try
            {
                if (System.IO.File.Exists(UiFile))
                {
                    var p = System.IO.File.ReadAllText(UiFile).Split(',');
                    if (p.Length >= 2) return new Point(int.Parse(p[0]), int.Parse(p[1]));
                }
            }
            catch { }
            return new Point(-1, -1);
        }

        public static void SaveUi(Point p)
        {
            try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(UiFile, p.X + "," + p.Y); }
            catch { }
        }

        public static List<string> LoadPinned()
        {
            try
            {
                if (!System.IO.File.Exists(SlotsFile)) return new List<string>();
                return System.IO.File.ReadAllLines(SlotsFile, Encoding.UTF8)
                    .Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#")).ToList();
            }
            catch { return new List<string>(); }
        }

        public static void SavePinned(IEnumerable<string> list)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllText(SlotsFile,
                    "# pinned hotspots\r\n" + string.Join("\r\n", list.ToArray()), new UTF8Encoding(true));
            }
            catch { }
        }

        public static string LoadPreferredIface()
        {
            try
            {
                if (System.IO.File.Exists(PrefFile)) return System.IO.File.ReadAllText(PrefFile, Encoding.UTF8).Trim();
            }
            catch { }
            return "";
        }

        public static void SavePreferredIface(string name)
        {
            try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(PrefFile, name ?? "", Encoding.UTF8); }
            catch { }
        }
    }

    // ==================== Win32 ====================
    public static class Native
    {
        public const int WM_SHOWPANEL = 0x8001;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }
        [DllImport("iphlpapi.dll")] public static extern int GetBestInterfaceEx(byte[] dest, out int bestIfIndex);
    }

    // ==================== Wi-Fi ====================
    public static class Wifi
    {
        public static string Run(string args, int timeout = 20000)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try
                {
                    var enc = Encoding.Default;
                    psi.StandardOutputEncoding = enc;
                    psi.StandardErrorEncoding = enc;
                }
                catch { }
                using (var p = Process.Start(psi))
                {
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } }
                    try { p.WaitForExit(); } catch { }
                    Thread.Sleep(15);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static string Pick(string text, params string[] keys)
        {
            foreach (var line in text.Split('\n'))
            {
                int i = line.IndexOf(':');
                if (i < 0) continue;
                var k = line.Substring(0, i).Trim();
                foreach (var want in keys)
                    if (string.Equals(k, want, StringComparison.OrdinalIgnoreCase)) return line.Substring(i + 1).Trim();
            }
            return "";
        }

        public static string CurrentSSID()
        {
            return Pick(Run("wlan show interfaces", 10000), "SSID");
        }

        public static int CurrentSignal()
        {
            var s = Pick(Run("wlan show interfaces", 10000), "Signal").Replace("%", "").Trim();
            int v;
            return int.TryParse(s, out v) ? v : -1;
        }

        public static List<string> SavedProfiles()
        {
            var res = new List<string>();
            foreach (var line in Run("wlan show profiles", 12000).Split('\n'))
            {
                int i = line.IndexOf(':');
                if (i < 0) continue;
                var v = line.Substring(i + 1).Trim();
                if (v.Length > 0 && !v.StartsWith("<")) res.Add(v);
            }
            return res;
        }

        /// <summary>扫描附近热点，SSID -> 最强信号</summary>
        public static Dictionary<string, int> Scan()
        {
            var res = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var o = Run("wlan show networks mode=bssid", 25000);
            string cur = null;
            foreach (var raw in o.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase))
                {
                    int i = line.IndexOf(':');
                    if (i < 0) { cur = null; continue; }
                    var v = line.Substring(i + 1).Trim();
                    cur = v.Length > 0 ? v : null;
                    if (cur != null && !res.ContainsKey(cur)) res[cur] = -1;
                    continue;
                }
                if (cur == null) continue;
                int j = line.IndexOf(':');
                if (j < 0) continue;
                var key = line.Substring(0, j).Trim();
                if (key.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) != 0) continue;
                var val = line.Substring(j + 1).Replace("%", "").Trim();
                int sig;
                if (int.TryParse(val, out sig) && sig > res[cur]) res[cur] = sig;
            }
            return res;
        }

        /// <summary>识别是不是手机热点、是苹果还是安卓</summary>
        public static string GuessKind(string ssid)
        {
            if (string.IsNullOrEmpty(ssid)) return "other";
            var s = ssid.ToLowerInvariant();
            string[] apple = { "iphone", "ipad", "apple", "苹果" };
            string[] android = { "redmi", "honor", "荣耀", "mate", "nova", "xiaomi", "小米", "红米",
                                 "oppo", "vivo", "realme", "oneplus", "一加", "huawei", "华为",
                                 "galaxy", "pixel", "android", "安卓", "samsung", "meizu", "魅族",
                                 "iqoo", "nubia", "moto", "poco" };
            foreach (var a in apple) if (s.Contains(a)) return "apple";
            foreach (var a in android) if (s.Contains(a)) return "android";
            if (Regex.IsMatch(ssid, @"的\s*(iPhone|iPad|手机)", RegexOptions.IgnoreCase)) return "apple";
            if (Regex.IsMatch(ssid, @"^\s*(iPhone|iPad)", RegexOptions.IgnoreCase)) return "apple";
            return "other";
        }

        public static bool HasProfile(string ssid)
        {
            foreach (var p in SavedProfiles())
                if (string.Equals(p, ssid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Esc(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string Hex(string s)
        {
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s)) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        static string ProfileXml(string ssid, string pass)
        {
            string sec = string.IsNullOrEmpty(pass)
                ? "<authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>"
                : "<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>" +
                  "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + Esc(pass) + "</keyMaterial></sharedKey>";
            return "<?xml version=\"1.0\"?>" +
                   "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                   "<name>" + Esc(ssid) + "</name>" +
                   "<SSIDConfig><SSID><name>" + Esc(ssid) + "</name><hex>" + Hex(ssid) + "</hex></SSID></SSIDConfig>" +
                   "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                   "<MSM><security>" + sec + "</security></MSM></WLANProfile>";
        }

        static bool AddProfile(string ssid, string pass)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "ns_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                System.IO.File.WriteAllText(tmp, ProfileXml(ssid, pass), new UTF8Encoding(false));
                Run("wlan add profile filename=\"" + tmp + "\" user=all", 15000);
            }
            catch { }
            finally { try { System.IO.File.Delete(tmp); } catch { } }
            return HasProfile(ssid);
        }

        static bool WaitConnected(string ssid, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (string.Equals(CurrentSSID(), ssid, StringComparison.OrdinalIgnoreCase)) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        public static string Connect(string ssid, Func<string> askPassword)
        {
            if (string.IsNullOrEmpty(ssid)) return "没有选择热点";
            if (string.Equals(CurrentSSID(), ssid, StringComparison.OrdinalIgnoreCase)) return null;

            if (HasProfile(ssid))
            {
                Run("wlan connect name=\"" + ssid + "\" ssid=\"" + ssid + "\"", 25000);
                if (WaitConnected(ssid, 14000)) return null;
            }

            string pass = askPassword != null ? askPassword() : null;
            if (pass == null) return "已取消";
            pass = pass.Trim();
            if (pass.Length == 0) return "密码不能为空";

            Run("wlan delete profile name=\"" + ssid + "\"", 10000);
            if (!AddProfile(ssid, pass)) return "写入 Wi-Fi 配置失败，SSID 可能不对";
            Run("wlan connect name=\"" + ssid + "\" ssid=\"" + ssid + "\"", 25000);
            if (WaitConnected(ssid, 20000)) return null;

            var cur = CurrentSSID();
            return "没能连上 " + ssid + "。" + (cur.Length > 0 ? "，现在还连着 " + cur : "") + "。确认手机热点开着、密码没改过。";
        }

        public static void Disconnect() { Run("wlan disconnect", 8000); Thread.Sleep(500); }
    }

    // ==================== 网卡 / 内网 ====================
    public static class Lan
    {
        static bool IsVirtual(string name, string desc)
        {
            var s = (name + " " + desc).ToLowerInvariant();
            string[] keys = { "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "tap-", "tun",
                              "vpn", "wireguard", "openvpn", "zerotier", "bluetooth", "蓝牙", "docker", "wsl",
                              "teredo", "isatap", "pseudo" };
            foreach (var k in keys) if (s.Contains(k)) return true;
            return false;
        }

        static string MaskFromPrefix(int p)
        {
            if (p <= 0) return "0.0.0.0";
            uint m = p >= 32 ? uint.MaxValue : ~(uint.MaxValue >> p);
            return string.Format("{0}.{1}.{2}.{3}",
                (m >> 24) & 0xFF, (m >> 16) & 0xFF, (m >> 8) & 0xFF, m & 0xFF);
        }

        public static int BestInterfaceIndex()
        {
            try
            {
                var buf = new byte[16];
                buf[0] = 2; buf[1] = 0;
                buf[2] = 0; buf[3] = 80;
                buf[4] = 8; buf[5] = 8; buf[6] = 8; buf[7] = 8;
                int idx;
                if (Native.GetBestInterfaceEx(buf, out idx) == 0) return idx;
            }
            catch { }
            return -1;
        }

        public static List<LanIf> Collect(out int bestIdx)
        {
            bestIdx = BestInterfaceIndex();
            var list = new List<LanIf>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                var addr = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (addr == null || IPAddress.IsLoopback(addr.Address)) continue;

                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw == null) continue;

                var li = new LanIf
                {
                    Alias = ni.Name,
                    Desc = ni.Description,
                    IP = addr.Address.ToString(),
                    Prefix = addr.PrefixLength,
                    Mask = MaskFromPrefix(addr.PrefixLength),
                    Gateway = gw.Address.ToString(),
                    IsVirtual = IsVirtual(ni.Name, ni.Description),
                    SpeedMbps = ni.Speed > 0 ? ni.Speed / 1000000 : 0
                };
                try
                {
                    var v4 = props.GetIPv4Properties();
                    li.IsDefaultEgress = (v4 != null && v4.Index == bestIdx);
                    if (v4 != null) li.IfIndex = v4.Index;
                }
                catch { }

                // 这块网卡直连的网段（用来做「内网路由」）
                try
                {
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var net = SubnetOf(ua.Address.ToString(), ua.PrefixLength);
                        if (net != null && !li.Subnets.Contains(net)) li.Subnets.Add(net);
                    }
                }
                catch { }

                list.Add(li);
            }

            try
            {
                var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                if (root != null)
                {
                    foreach (var li in list)
                    {
                        foreach (var sub in root.GetSubKeyNames())
                        {
                            using (var sk = root.OpenSubKey(sub))
                            {
                                if (sk == null) continue;
                                var ipArr = sk.GetValue("IPAddress") as string[];
                                var dhcp = sk.GetValue("DhcpIPAddress") as string;
                                var cand = (ipArr != null && ipArr.Length > 0) ? ipArr[0] : dhcp;
                                if (cand != li.IP) continue;
                                var m = sk.GetValue("InterfaceMetric");
                                int mv;
                                if (m != null && int.TryParse(m.ToString(), out mv)) li.Metric = mv;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 从路由表补上「默认路由度量」和「总度量」。
            // route print 默认路由的最后一列就是「总度量」本身。
            // （接口度量 + 路由度量，例如 200+1=201）。
            // Windows 挑出口只看这个数，光把接口度量调小是不够的。
            try
            {
                var psi = new ProcessStartInfo("route.exe", "print -4")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                string txt;
                using (var p = Process.Start(psi))
                {
                    txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                }
                foreach (var raw in txt.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    var m = Regex.Match(line,
                        @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\S+)\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+)\s*$");
                    if (!m.Success) continue;
                    int total;
                    if (!int.TryParse(m.Groups[3].Value, out total)) continue;
                    var gwIp = m.Groups[2].Value;
                    foreach (var li in list)
                    {
                        if (li.Gateway != gwIp) continue;
                        li.TotalMetric = total;
                        // 反推路由度量：总度量 - 接口度量
                        li.RouteMetric = total - li.Metric;
                        if (li.RouteMetric < 0) li.RouteMetric = 0;
                        // 接口度量本身没读到时，也用它兜底
                        if (li.Metric <= 0) li.Metric = 0;
                    }
                }
                foreach (var li in list)
                    if (li.TotalMetric <= 0) li.TotalMetric = li.RouteMetric + li.Metric;
            }
            catch { }

            return list;
        }

        [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

        [StructLayout(LayoutKind.Sequential)]
        struct IpNetRow
        {
            public int Index;
            public int PhysAddrLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PhysAddr;
            public uint Addr;
            public int Type;
        }

        /// <summary>ARP 表里有没有这个 IP。有 = 同一广播域，实打实看得见。</summary>
        public static bool InArpTable(string ip)
        {
            try
            {
                int size = 0;
                GetIpNetTable(IntPtr.Zero, ref size, false);
                if (size <= 0) return false;
                IntPtr buf = Marshal.AllocHGlobal(size + 8192);
                try
                {
                    if (GetIpNetTable(buf, ref size, false) != 0) return false;
                    int count = Marshal.ReadInt32(buf);
                    int rowSize = Marshal.SizeOf(typeof(IpNetRow));
                    uint w = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
                    for (int i = 0; i < count && i < 5000; i++)
                    {
                        var r = (IpNetRow)Marshal.PtrToStructure((IntPtr)((long)buf + 4 + i * rowSize), typeof(IpNetRow));
                        if (r.Addr == w && (r.Type == 3 || r.Type == 4)) return true;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return false;
        }

        static bool TcpProbe(string host, int port, int ms)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(host, port);
                    if (t.Wait(ms)) return c.Connected;
                    return false;
                }
            }
            catch { return false; }
        }

        /// <summary>综合判断网关是否活着。ping 不行就退到 ARP / TCP。</summary>
        public static bool ProbeGateway(string gw, int pingMs, out bool inArp, out string how)
        {
            inArp = false;
            how = "";
            try
            {
                using (var p = new Ping())
                {
                    var r = p.Send(gw, pingMs);
                    if (r != null && r.Status == IPStatus.Success) { how = "ping " + r.RoundtripTime + "ms"; return true; }
                }
            }
            catch { }

            if (InArpTable(gw)) { inArp = true; how = "ARP"; }

            foreach (var port in new int[] { 80, 443, 53, 22 })
            {
                if (TcpProbe(gw, port, 350))
                {
                    how = (how.Length > 0 ? how + " + " : "") + "TCP" + port;
                    return true;
                }
            }
            return inArp;
        }

        /// <summary>
        /// 跑一个需要管理员的命令，靠退出码判定成败（不看输出文字，中英文都不可靠）。    /// 成功返回 null，失败返回错误说明。
        /// </summary>
        static string RunElevated(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    if (p.ExitCode != 0)
            return "退出码 " + p.ExitCode + "：" + o.Trim();
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>读某个网卡当前真正生效的接口度量（null = 读不到）</summary>
        public static int? GetMetric(string alias)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "int ipv4 show interfaces");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                    // 本机实际输出。MTU 列：  Idx  Met  MTU  State  Name
                    // 老版本可能没有 MTU 列，所以 MTU 段用可选匹配，别写死列宽。
                    foreach (var line in o.Split('\n'))
                    {
                        var t = line.TrimEnd('\r');
                        var m = Regex.Match(t, @"^\s*(\d+)\s+(\d+)\s+(?:\d+\s+)?\S+\s+(.+?)\s*$");
                        if (!m.Success) continue;
                        if (!string.Equals(m.Groups[3].Value.Trim(), alias.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                        int v;
                        if (int.TryParse(m.Groups[2].Value, out v)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 设优先出口。成功返回 null。
        /// 失败必须返回错误，不能假装成功——原来只看关键词，
        /// "requires elevation" 里没有 failed 字样，被误判成成功。
        /// </summary>
        public static string ApplyPriority(string alias, List<string> allAliases, int timeoutMs)
        {
            try
            {
                var names = new List<string>(allAliases);
                if (!names.Contains(alias)) names.Add(alias);

                // 先把目标网卡降到 1。
                string err = RunElevated("netsh.exe",
                    "int ipv4 set interface \"" + alias + "\" metric=1", timeoutMs);
                if (err != null) return err;

                // 关键：Windows 比的是「总度量 = 路由度量 + 接口度量」，
                // 只把对手设成 200 是不够的——对手路由度量可能是 1，
                // 200+1=201 会和目标 1+200=201 打平，打平时系统仍走物理网卡。
                // 于是界面提示成功、出口却纹丝不动。
                // 这里按实际总度量算「确保赢」的最小值，留 50 余量即可。
                // 别用 9999 那种夸张数字，免得把用户的手动设置全冲掉。
                int targetTotal = 1 + RouteMetricOf(alias);
                int others = targetTotal + 50;
                if (others < 200) others = 200;

                foreach (var a in names)
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    if (string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)) continue;
                    // 其他网卡失败不致命（比如正在断开的网卡），继续压后面几块
                    RunElevated("netsh.exe",
                        "int ipv4 set interface \"" + a + "\" metric=" + others, timeoutMs);
                }

                // 回读校验：目标网卡必须真的是 1
                int? got = GetMetric(alias);
                if (got.HasValue && got.Value != 1)
                    return "设置后度量是 " + got.Value + "，没生效（可能需要管理员权限）。";

                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>读某块网卡默认路由自带的路由度量（读不到。 算）</summary>
        public static int RouteMetricOf(string alias)
        {
            try
            {
                var psi = new ProcessStartInfo("route.exe", "print -4");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                string txt;
                using (var p = Process.Start(psi))
                {
                    txt = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                }
                // 用该网卡。IP 定位它那条默认路由，最后一列是路由度量
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
                    var ip = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) return 0;
                    var ipStr = ip.Address.ToString();
                    foreach (var raw in txt.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (!line.Contains(ipStr)) continue;
                        var m = Regex.Match(line,
                            @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+\S+\s+" + Regex.Escape(ipStr) + @"\s+(\d+)\s*$");
                        if (!m.Success) continue;
                        int v;
                        if (int.TryParse(m.Groups[1].Value, out v)) return v;
                    }
                }
            }
            catch { }
            return 0;
        }

        // ---------- 内网固定路由 ----------
        static uint ToUInt(IPAddress a) { return BitConverter.ToUInt32(a.GetAddressBytes(), 0); }

        static IPAddress FromUInt(uint v)
        {
            var b = BitConverter.GetBytes(v);
            return new IPAddress(b);
        }

        /// <summary>IP/前缀 -> 网段地址（如 192.168.1.10/24 -> 192.168.1.0/24）</summary>
        public static string SubnetOf(string ip, int prefix)
        {
            try
            {
                if (prefix <= 0 || prefix > 32) return null;
                byte[] b = IPAddress.Parse(ip).GetAddressBytes();
                if (b.Length != 4) return null;
                // 按网络字节序（大端）拼，别用 BitConverter，小端机器上会把地址倒过来
                uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
                uint mask = prefix >= 32 ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - prefix));
                uint net = addr & mask;
                return string.Format("{0}.{1}.{2}.{3}/{4}",
                    (net >> 24) & 0xFF, (net >> 16) & 0xFF, (net >> 8) & 0xFF, net & 0xFF, prefix);
            }
            catch { return null; }
        }

        /// <summary>本机已有的永久路由（只读注册表，不需要管理员）</summary>
        public static List<string> PinnedRoutes()
        {
            var res = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes"))
                {
                    if (k == null) return res;
                    foreach (var n in k.GetValueNames())
                        res.Add(n.Replace(',', '/'));
                }
            }
            catch { }
            return res;
        }

        /// <summary>
        /// 读系统里真实存在的持久路由，返回「网。-> 网关」。
        /// 。netsh 读而不是注册表：netsh 加的持久路由。Win10/11 上不落进
        /// 注册表那。PersistentRoutes 键，照着注册表判断就会把
        /// 「已经固定了」误当成「没固定」，点取消时反而又去添加一遍。        /// 输出是定宽表格，按位置切列，不依赖界面语言。
        /// </summary>
        public static Dictionary<string, string> PersistentRoutes()
        {
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string text;
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "interface ipv4 show route store=persistent");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                using (var p = Process.Start(psi))
                {
                    text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                }
            }
            catch { return res; }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                // netsh 的表头和数据都是本地化的（中文系统上是「发布 类型/跃点数 前缀…」）。
                // 所以不能按英文关键字匹配，只能按「哪一列是网段」来认：
                // 整行里唯一一个带斜杠的 ip/prefix 就是前缀列。
                var m = Regex.Match(line, @"(\d+\.\d+\.\d+\.\d+/\d+)");
                if (!m.Success) continue;
                string prefix = m.Groups[1].Value;
                // 网关在前缀列之后：先跳过一个数字（接口索引），再取那个 ip。
                string after = line.Substring(m.Index + m.Length).Trim();
                var gm = Regex.Match(after, @"^(\d+)\s+(\d+\.\d+\.\d+\.\d+)");
                string gw = gm.Success ? gm.Groups[2].Value : "";
                if (!res.ContainsKey(prefix)) res[prefix] = gw;
            }

            // 兜底：netsh 偶尔读不到自己写的持久路由，
            // 那就读 route print 里的「永久路由」段落（那一段是英文标题 + 四列数字）。
            if (res.Count == 0)
            {
                try
                {
                    var psi2 = new ProcessStartInfo("route.exe", "print -4");
                    psi2.UseShellExecute = false;
                    psi2.CreateNoWindow = true;
                    psi2.RedirectStandardOutput = true;
                    try { psi2.StandardOutputEncoding = Encoding.Default; } catch { }
                    string txt;
                    using (var p = Process.Start(psi2))
                    {
                        txt = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(10000);
                    }
                    foreach (var raw in txt.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (line.Trim().Length == 0) continue;
                        // 标题行也是本地化的，这里靠「这一段里每行都是 4 个数字」来认数据行。
                        // 注意只认「恰好 4 列」的行：活动路由表那几行有 5 列（多一个接口 IP），
                        // 不会被误当成持久路由。
                        var cells = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (cells.Length == 4 &&
                            cells[0].Split('.').Length == 4 && cells[1].Split('.').Length == 4 &&
                            cells[0].All(c => c >= '0' && c <= '9') && cells[1].All(c => c >= '0' && c <= '9') &&
                            cells[2].Split('.').Length == 4 && cells[3].All(c => c >= '0' && c <= '9'))
                        {
                            string net = cells[0], mask = cells[1], gw = cells[2];
                            int prefix = 0;
                            foreach (var part in mask.Split('.'))
                            {
                                int b = int.Parse(part);
                                for (int i = 7; i >= 0; i--)
                                {
                                    if ((b & (1 << i)) != 0) prefix++;
                                    else goto done;
                                }
                            }
                        done:
                            if (prefix < 8 || prefix >= 32) continue;
                            string cidr = net + "/" + prefix;
                            if (!res.ContainsKey(cidr)) res[cidr] = gw;
                        }
                    }
                }
                catch { }
            }
            return res;
        }

        /// <summary>
        /// 读出每块网卡上「不是默认路由」的专线路由（按网卡 IP 归类）。    /// 这些就是内网专线。2.16.0.0/12 走有线，上网照旧。Wi-Fi。    /// 。route print 解析，比 IpForwardTable 的结构体对齐可靠得多。
        /// </summary>
        public static Dictionary<string, List<string>> IntranetRoutesByIp()
        {
            var res = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string text;
            try
            {
                var psi = new ProcessStartInfo("route.exe", "print -4");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                try { psi.StandardOutputEncoding = Encoding.Default; } catch { }
                using (var p = Process.Start(psi))
                {
                    text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                }
            }
            catch { return res; }

            var row = new Regex(@"^\s*(\d+\.\d+\.\d+\.\d+)\s+(\d+\.\d+\.\d+\.\d+)\s+(\S+)\s+(\d+\.\d+\.\d+\.\d+)\s+(\d+)\s*$");
            foreach (var raw in text.Split('\n'))
            {
                var m = row.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;

                var dest = m.Groups[1].Value;
                var mask = m.Groups[2].Value;
                var gw = m.Groups[3].Value;
                var ifIp = m.Groups[4].Value;
                int met;
                if (!int.TryParse(m.Groups[5].Value, out met)) continue;

                // 默认路由不算内网专线
                if (dest == "0.0.0.0" && mask == "0.0.0.0") continue;
                // 多播 / 广播 / 回环
                if (dest.StartsWith("224.") || dest.StartsWith("239.")) continue;
                if (dest == "255.255.255.255") continue;
                if (dest.StartsWith("127.")) continue;
                // 直接相连（On-link）不是专线，跳过
                if (gw.Equals("On-link", StringComparison.OrdinalIgnoreCase)) continue;

                int prefix = 0;
                try
                {
                    foreach (var part in mask.Split('.'))
                    {
                        int b = int.Parse(part);
                        for (int i = 7; i >= 0; i--)
                        {
                            if ((b & (1 << i)) != 0) prefix++;
                            else goto done;
                        }
                    }
                }
                catch { }
            done:
                if (prefix < 8 || prefix >= 32) continue;   // 太宽泛或 /32 主机路由都不算专线
                string cidr = dest + "/" + prefix;
                if (!res.ContainsKey(ifIp)) res[ifIp] = new List<string>();
                if (!res[ifIp].Contains(cidr)) res[ifIp].Add(cidr);
            }
            return res;
        }

        /// <summary>
        /// 给某个网段挂一条永久路由，让这个网段的流量固定走某块网卡。
        /// 这样上网照旧走默认出口，内网走这块网卡，两边互不打扰。
        /// </summary>
        public static string PinRoute(string subnet, string gateway, string ifAlias, int timeoutMs)
        {
            var parts = subnet.Split('/');
            if (parts.Length != 2) return "网段格式不对：" + subnet;
            // netsh 的 add route 语法必须写成 prefix= / nexthop= / interface=。
            // 少一个关键字它会直接报「语法无效」，路由根本不会写进去。
            // interface= 也必须给：只给 nexthop 它 netsh 认不出是哪块网卡。
            string err = RunElevated("netsh.exe",
                "interface ipv4 add route prefix=" + parts[0] + "/" + parts[1] +
                " nexthop=" + gateway +
                (string.IsNullOrEmpty(ifAlias) ? "" : " interface=\"" + ifAlias + "\"") +
                " metric=1 store=persistent", timeoutMs);
            if (err != null) return err;
            // 回读校验：持久路由表里必须真的出现这条。
            if (!PersistentRoutes().ContainsKey(subnet))
                return "路由没写进去：" + subnet;
            return null;
        }

        public static string UnpinRoute(string subnet, string gateway, string ifAlias, int timeoutMs)
        {
            var parts = subnet.Split('/');
            if (parts.Length != 2) return "网段格式不对：" + subnet;
            // 同样的坑：delete route 也认 prefix=/nexthop=/interface= 这套写法。
            string err = RunElevated("netsh.exe",
                "interface ipv4 delete route prefix=" + parts[0] + "/" + parts[1] +
                (string.IsNullOrEmpty(gateway) ? "" : " nexthop=" + gateway) +
                (string.IsNullOrEmpty(ifAlias) ? "" : " interface=\"" + ifAlias + "\"") +
                " store=persistent", timeoutMs);
            if (err != null) return err;
            if (PersistentRoutes().ContainsKey(subnet)) return "路由没删掉：" + subnet;
            return null;
        }

        /// <summary>这块网卡自己能不能上网（绑定源地址实测，最可靠）</summary>
        public static bool CanReachInternet(string localIp)
        {
            if (string.IsNullOrEmpty(localIp)) return false;
            // 探测目标：默认一组全球通用公共 DNS；可用 %APPDATA%\NetSwitcher\probe.txt
            // 每行一个 "主机:端口" 覆盖（例如内网环境换成自己的探测点）。
            string[] hosts = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };
            int[] ports = { 443, 53, 80 };
            try
            {
                string pf = Path.Combine(Cfg.Dir, "probe.txt");
                if (System.IO.File.Exists(pf))
                {
                    var custom = new List<string>();
                    foreach (var raw in System.IO.File.ReadAllLines(pf, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        custom.Add(line);
                    }
                    if (custom.Count > 0) hosts = custom.ToArray();
                }
            }
            catch { }
            for (int i = 0; i < hosts.Length; i++)
            {
                string hp = hosts[i];
                string host = hp;
                int port = ports[i % ports.Length];
                int colon = hp.LastIndexOf(':');
                if (colon > 0)
                {
                    host = hp.Substring(0, colon);
                    int p;
                    if (int.TryParse(hp.Substring(colon + 1), out p) && p > 0 && p <= 65535) port = p;
                }
                try
                {
                    using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        s.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                        var t = s.ConnectAsync(host, port);
                        if (t.Wait(1200) && s.Connected) return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }

    // ==================== 探测。===================
    public class Detector
    {
        public Snapshot Build()
        {
            var s = new Snapshot();

            try { s.WifiSSID = Wifi.CurrentSSID(); s.WifiSignal = Wifi.CurrentSignal(); }
            catch { }

            List<string> saved = new List<string>();
            try { saved = Wifi.SavedProfiles(); } catch { }

            Dictionary<string, int> scanned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try { scanned = Wifi.Scan(); } catch { }

            var pinned = Cfg.LoadPinned();
            var map = new Dictionary<string, Hotspot>(StringComparer.OrdinalIgnoreCase);

            Action<string> touch = k =>
            {
                if (string.IsNullOrEmpty(k)) return;
                if (!map.ContainsKey(k)) map[k] = new Hotspot { SSID = k };
                var h = map[k];
                h.Kind = Wifi.GuessKind(k);
                h.Saved = saved.Any(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
                h.Visible = scanned.ContainsKey(k);
                h.Signal = h.Visible ? scanned[k] : -1;
                h.Connected = string.Equals(k, s.WifiSSID, StringComparison.OrdinalIgnoreCase);
            };

            foreach (var k in saved) touch(k);
            foreach (var k in scanned.Keys) touch(k);
            foreach (var k in pinned) touch(k);

            // 只列现在扫得到（或正连着）的热点。
            // 扫不到的 = 连不上，就不占位置了；手里存过但没开热点的也不显示。
            var list = map.Values
                .Where(h => h.Connected || h.Visible)
                .ToList();

            list.Sort((a, b) =>
            {
                if (a.Connected != b.Connected) return a.Connected ? -1 : 1;
                int k = b.Signal.CompareTo(a.Signal);
                if (k != 0) return k;
                return string.Compare(a.SSID, b.SSID, StringComparison.OrdinalIgnoreCase);
            });
            s.Hotspots = list;

            try
            {
                int best;
                var lans = Lan.Collect(out best);
                // 「有没有固定过内网」必须问 netsh，不能只读注册表。
                // netsh 写的持久路由在 Win10/11 上不进那个注册表键，
                // 只看注册表会永远显示「没固定」，于是菜单写的是「固定内网网段」，
                // 而实际那条内网专线还在生效，谁也不知道。
                var persistent = Lan.PersistentRoutes();
                var pinnedRoutes = persistent.Keys.ToList();
                var intranet = Lan.IntranetRoutesByIp();
                foreach (var li in lans)
                {
                    bool inArp; string how;
                    li.GwReachable = Lan.ProbeGateway(li.Gateway, 900, out inArp, out how);
                    li.InArp = inArp;
                    li.ReachHow = how;

                    // 这块网卡上真实挂着哪些持久路由（按下一跳是不是它的网关来判断归属）
                    li.PinnedSubnets.Clear();
                    foreach (var kv in persistent)
                    {
                        if (string.Equals(kv.Value, li.Gateway, StringComparison.OrdinalIgnoreCase))
                            li.PinnedSubnets.Add(kv.Key);
                    }
                    li.HasPinnedRoute = li.PinnedSubnets.Count > 0;

                    // 这块网卡上跑着哪些内网专线（除了它自己的直连网段）
                    List<string> routes;
                    if (intranet.TryGetValue(li.IP, out routes))
                    {
                        foreach (var r in routes)
                            if (!li.Subnets.Contains(r) && !li.IntranetRoutes.Contains(r))
                                li.IntranetRoutes.Add(r);
                        if (li.IntranetRoutes.Count > 0) li.HasPinnedRoute = true;
                    }
                    li.CanInternet = li.IsDefaultEgress || Lan.CanReachInternet(li.IP);
                }
                s.Lans = lans;
                s.PinnedRoutes = pinnedRoutes;
                var b = lans.FirstOrDefault(x => x.IsDefaultEgress);
                if (b != null) { s.BestAlias = b.Alias; s.BestIP = b.IP; }

                // 内网资料实际走哪块网卡：找挂着内网专线的那块
                var inner = lans.FirstOrDefault(x => x.IntranetRoutes.Count > 0);
                if (inner != null)
                {
                    s.IntranetAlias = inner.Alias;
                    s.IntranetIP = inner.IP;
                    foreach (var r in inner.IntranetRoutes) s.IntranetRoutes.Add(r);
                }
                else if (intranet.Count > 0)
                {
                    // 兜底：按 IP 反查网卡。
                    foreach (var kv in intranet)
                    {
                        foreach (var r in kv.Value) if (!s.IntranetRoutes.Contains(r)) s.IntranetRoutes.Add(r);
                        var owner = lans.FirstOrDefault(x => x.IP == kv.Key);
                        if (owner != null && s.IntranetAlias.Length == 0)
                        {
                            s.IntranetAlias = owner.Alias;
                            s.IntranetIP = owner.IP;
                        }
                    }
                }
            }
            catch { }

            s.At = DateTime.Now;
            return s;
        }
    }

    // ==================== 系统图标字形 ====================
    /// <summary>
    /// 全部图标统一。Windows 自带的图标字体，不再。Graphics 手绘线条。
    /// 手绘的问题：弧线粗细、端点、坐标全靠手感，换一个尺寸就歪；
    /// 系统字形是矢量轮廓，同一套笔触、同一套网格，任何大小都对齐。    /// 优先 Segoe Fluent Icons（Win11），老机器退。Segoe MDL2 Assets（Win10 自带）。
    /// </summary>
    static class Glyph
    {
        public static readonly string Family = PickFamily();

        // 只保留仍在用的两个字形：Wi-Fi（卡片图标、空状态）和信号格（信号强度）。
        // 齿轮/刷新/最小化/关闭四个键已改为。RoundButton 里手绘矢量线条，
        // 不再依赖系统图标字体，原来那几个码位常量一并删掉，免得误以为还在用。
        public const char Wifi = '\uE701';        // 四道。+ 圆点
        public const char SignalBars = '\uE86F';  // 四格信号

        // ---- 菜单图标 ----
        // 菜单里的图标一律取系统图标字体里的现成字形，线宽、网格、圆角都。        // Windows 自己的菜单一致，不用手绘、也不引入第二套图标。        // 每个码位都在 Segoe Fluent Icons（回退 MDL2）上验过存在，不会出现豆腐块。
        public const char MenuEdit = '\uE70F';       // 铅笔
        public const char MenuNote = '\uE90A';       // 便签
        public const char MenuDelete = '\uE74D';     // 垃圾。
        public const char MenuCopy = '\uE8C8';       // 复制
        public const char MenuPin = '\uE718';        // 图钉
        public const char MenuUnpin = '\uE77A';      // 拔掉的图。
        public const char MenuRoute = '\uE787';      // 路由
        public const char MenuSync = '\uE895';       // 同步（恢复自动）
        public const char MenuSend = '\uE724';       // 发送（Ping）
        public const char MenuInfo = '\uE946';       // 信息
        public const char MenuRefresh = '\uE72C';    // 刷新
        public const char MenuPower = '\uE7E8';      // 电源（开机自启 / 退出）
        public const char MenuFolder = '\uE8B7';     // 文件夹
        public const char MenuHelp = '\uE897';       // 问号
        public const char MenuConnect = '\uE703';    // 已连接的链
        public const char MenuDisconnect = '\uE8CD'; // 断开的链
        public const char MenuExit = '\uE711';       // 叉
        public const char MenuSearch = '\uE721';     // 放大镜
        public const char MenuCheck = '\uE73E';      // 对勾

        static string PickFamily()
        {
            try
            {
                foreach (FontFamily ff in new InstalledFontCollection().Families)
                {
                    if (ff.Name == "Segoe Fluent Icons") return "Segoe Fluent Icons";
                }
                foreach (FontFamily ff in new InstalledFontCollection().Families)
                {
                    if (ff.Name == "Segoe MDL2 Assets") return "Segoe MDL2 Assets";
                }
            }
            catch { }
            return "Segoe MDL2 Assets";
        }

        /// <summary>
        /// 。box 里居中画一个字形。        /// 不直。DrawString：字形的墨迹。em 框里是偏的，字号一改就跑出边界或偏上偏下。        /// 这里把字形取成轮廓路径，量出真实墨迹外接框，再等比缩放居中到 box。        /// 于是同一套代码在 22px 的小图标。44px 的大图标上视觉大小完全一致。
        /// </summary>
        public static void Draw(Graphics g, char code, RectangleF box, Color color, float scale = 1f)
        {
            try
            {
                using (var fam = new FontFamily(Family))
                using (var path = new GraphicsPath())
                {
                    path.AddString(code.ToString(), fam, (int)FontStyle.Regular, 100f,
                        PointF.Empty, StringFormat.GenericTypographic);
                    var b = path.GetBounds();
                    if (b.Width <= 0f || b.Height <= 0f) return;

                    float target = Math.Min(box.Width, box.Height) * scale;
                    float k = Math.Min(target / b.Width, target / b.Height);
                    // 用单个矩阵一次算好：x' = k*x + tx。不要链式 Translate/Scale——
                    // Matrix 的变换是前置累乘，写下来的顺序和实际生效的顺序相反，很容易算歪。
                    float tx = box.X + (box.Width - b.Width * k) / 2f - b.X * k;
                    float ty = box.Y + (box.Height - b.Height * k) / 2f - b.Y * k;
                    using (var m = new Matrix(k, 0f, 0f, k, tx, ty))
                        path.Transform(m);

                    var old = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var br = new SolidBrush(color)) g.FillPath(br, path);
                    g.SmoothingMode = old;
                }
            }
            catch { }
        }

        public static void Draw(Graphics g, char code, Rectangle box, Color color, float scale = 1f)        {
            Draw(g, code, new RectangleF(box.X, box.Y, box.Width, box.Height), color, scale);
        }
    }

    // ==================== 动效基类 ====================
    /// <summary>
    /// 把「悬。按下的瞬时切换」换成插值过渡。    /// 原来控件。Over=true 然后立刻 Invalidate，画面一帧就跳到底色。    /// 手感很硬。这里用 TickCount 记起点，每帧重算 0..1 的进度再重绘。    /// 到时间就停表，不会常驻占 CPU。
    /// </summary>
    public class MotionControl : Control
    {
        float hoverTarget;        // 悬停进度目标。 。1
        float hoverProgress;      // 当前悬停进度
        float pressTarget;
        float pressProgress;
        float hoverStart, pressStart;
        const int FrameMs = 15;

        protected readonly System.Windows.Forms.Timer anim;

        public MotionControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            anim = new System.Windows.Forms.Timer { Interval = FrameMs };
            anim.Tick += (s, e) => StepAnimation();
        }

        /// <summary>悬停进度 0..1（已缓动），控件直接拿去混色/位移</summary>
        protected float Hover { get { return hoverProgress; } }
        /// <summary>按下进度 0..1（已缓动）</summary>
        protected float Press { get { return pressProgress; } }
        /// <summary>是否正处于过渡中</summary>
        protected bool Animating { get { return anim.Enabled; } }

        protected void SetHover(bool on)
        {
            hoverTarget = on ? 1f : 0f;
            hoverStart = Environment.TickCount;
            EnsureAnimating();
        }

        protected void SetPress(bool on)
        {
            pressTarget = on ? 1f : 0f;
            pressStart = Environment.TickCount;
            EnsureAnimating();
        }

        void EnsureAnimating()
        {
            if (!anim.Enabled && (Math.Abs(hoverProgress - hoverTarget) > 0.001f ||
                                  Math.Abs(pressProgress - pressTarget) > 0.001f))
            {
                anim.Start();
                StepAnimation();
            }
        }

        void StepAnimation()
        {
            hoverProgress = T.Lerp(hoverProgress, hoverTarget, T.Animate(hoverStart, T.DurFast));
            pressProgress = T.Lerp(pressProgress, pressTarget, T.Animate(pressStart, T.DurFast));
            // 靠近目标就直接吸附，避免无限逼近导致永远在重。
            if (Math.Abs(hoverProgress - hoverTarget) < 0.01f) hoverProgress = hoverTarget;
            if (Math.Abs(pressProgress - pressTarget) < 0.01f) pressProgress = pressTarget;

            Invalidate();
            if (hoverProgress == hoverTarget && pressProgress == pressTarget) anim.Stop();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(true); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHover(false); SetPress(false); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); SetPress(true); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); SetPress(false); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { anim.Stop(); anim.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ==================== 圆形按钮 ====================
    public class RoundButton : MotionControl
    {
        /// <summary>按钮种类：决定用哪个系统字形</summary>
        public enum Kind { Gear, Refresh, Minimize, Close }
        public Kind Icon = Kind.Gear;
        public Action ClickAction;

        /// <summary>转圈进度 0..360。扫描时刷新按钮转起来，给「正在干活」的反馈</summary>
        float spinAngle;
        bool spinning;
        readonly System.Windows.Forms.Timer spin;

        public RoundButton()
        {
            BackColor = Color.Transparent;
            // 光标用系统默认箭头：Windows 自己的按钮、列表项都是箭头。            // 这里没必要再塞一个手形，看起来像网页控件，和系统不一致。
            Cursor = Cursors.Default;
            Size = new Size(30, 30);
            // 转一。= 360°/每帧 18°，约 1.2 秒一圈（60fps 下）。            // 之前是每。12°。0ms 一。一圈要 0.9 秒，扫描 3 秒能。3 圈多。            // 视觉上像「转不停」。放慢到。1.4 秒一圈，一次扫描刚好走一两圈，不闹。
            spin = new System.Windows.Forms.Timer { Interval = 30 };
            spin.Tick += (s, e) =>
            {
                spinAngle = (spinAngle + 8f) % 360f;
                Invalidate();
            };
        }

        /// <summary>正在扫描就转圈。停下来时归位到 0，不要停在一个斜着的位）</summary>
        public bool Spinning
        {
            get { return spinning; }
            set
            {
                if (spinning == value) return;
                spinning = value;
                if (spinning) spin.Start();
                else { spin.Stop(); spinAngle = 0f; Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location) && ClickAction != null) ClickAction();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 按钮底：默认不画。只有鼠标压在上面时才浮出一个半透明圆，
            // 这样静止状态下四个键就是四枚干净的线条图标，不再是
            // 「两个实心圆盘 + 两个线条图标」拼在一起。
            float a = Math.Max(Hover, Press);
            if (a > 0.01f)
            {
                int alpha = (int)Math.Round(T.Lerp(0f, 60f, a));
                using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            }

            Color fg = T.Blend(T.OnHeroMuted, T.OnHero, Hover);
            // 不再用系统图标字体：MDL2 的字形粗细、网格、圆角都和这套「蓝底白线」
            // 气质不合（齿轮齿太密、刷新箭头两端不对称）。这里全部改为手绘细线，
            // 统一 1.6px 线宽 + 圆头端点，四枚视觉重量才真正一致。
            float cx = Width / 2f, cy = Height / 2f;
            float r = 8.5f;                      // 图标外接半径
            using (var pen = new Pen(fg, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                var state = g.Save();
                if (spinning)
                {
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(spinAngle);
                    g.TranslateTransform(-cx, -cy);
                }
                switch (Icon)
                {
                    case Kind.Minimize:
                        g.DrawLine(pen, cx - 7f, cy, cx + 7f, cy);
                        break;
                    case Kind.Close:
                        g.DrawLine(pen, cx - 6.5f, cy - 6.5f, cx + 6.5f, cy + 6.5f);
                        g.DrawLine(pen, cx + 6.5f, cy - 6.5f, cx - 6.5f, cy + 6.5f);
                        break;
                    case Kind.Refresh:
                        {
                            // 双向刷新符号：两段圆环 + 两个箭头头，经典且任何尺寸下都能读出来。
                            // 之前那版是「一段圆弧 + 起点小箭头」，箭头只有 5px、压在 1.6px 线上，
                            // 缩小后整个字形糊成一个缺口圆环，看不出「刷新」。
                            // 这里改成上下两个方向相反的钩形箭头（顺时针语义），
                            // 箭头头部放大到 4.8px、独立的三角，缩到 20px 也认得出。
                            float rr = r * 1.02f;
                            var circle = new RectangleF(cx - rr, cy - rr, rr * 2, rr * 2);

                            // 上半圈：从左中扫到右上（顺时针）
                            g.DrawArc(pen, circle, 185f, 120f);
                            // 下半圈：从右上对面继续，绕过右侧（顺时针）
                            g.DrawArc(pen, circle, 5f, 120f);

                            // 箭头 1：落在 185°+120°=305° 处（右上），指向切线顺方向
                            DrawSpinHead(g, pen, cx, cy, rr, 305f, 4.9f);
                            // 箭头 2：落在 5°+120°=125° 处（左下）
                            DrawSpinHead(g, pen, cx, cy, rr, 125f, 4.9f);
                        }
                        break;
                    default: // Gear
                        {
                            // 齿轮：一圈「齿形轮廓」+ 中心孔。
                            // 关键是齿要落在轮廓上（齿顶凸出、齿根凹回），
                            // 只画中心圆加一圈小短线会读成太阳/转盘。
                            float rRoot = r * 0.76f;
                            float rTip = r * 1.06f;
                            float rHole = r * 0.30f;
                            const int teeth = 8;
                            double step = 2 * Math.PI / teeth;
                            double halfTooth = step * 0.21;   // 齿顶半角
                            double halfRoot = step * 0.34;    // 齿根半角

                            using (var gear = new GraphicsPath())
                            {
                                var pts = new PointF[teeth * 4];
                                int n = 0;
                                for (int k = 0; k < teeth; k++)
                                {
                                    double c = step * k;
                                    pts[n++] = new PointF(
                                        (float)(cx + rRoot * Math.Cos(c - halfRoot)),
                                        (float)(cy + rRoot * Math.Sin(c - halfRoot)));
                                    pts[n++] = new PointF(
                                        (float)(cx + rTip * Math.Cos(c - halfTooth)),
                                        (float)(cy + rTip * Math.Sin(c - halfTooth)));
                                    pts[n++] = new PointF(
                                        (float)(cx + rTip * Math.Cos(c + halfTooth)),
                                        (float)(cy + rTip * Math.Sin(c + halfTooth)));
                                    pts[n++] = new PointF(
                                        (float)(cx + rRoot * Math.Cos(c + halfRoot)),
                                        (float)(cy + rRoot * Math.Sin(c + halfRoot)));
                                }
                                gear.AddPolygon(pts);
                                gear.CloseFigure();
                                g.DrawPath(pen, gear);
                            }
                            // 中心。
                            g.DrawEllipse(pen, cx - rHole, cy - rHole, rHole * 2, rHole * 2);
                        }
                        break;
                }
                g.Restore(state);
            }
        }

        /// <summary>
        /// 在圆环的指定角度处画一个箭头头（实心小三角），指向顺时针切线方向。
        /// 刷新图标全靠这两个三角把「旋转」语义定住，所以它要比线粗一档、独立成型，
        /// 不能拿两条短线凑——缩到 20px 时短线会糊进圆弧里。
        /// </summary>
        static void DrawSpinHead(Graphics g, Pen pen, float cx, float cy, float radius, float deg, float size)
        {
            // GDI+ 的 0° 指向右、角度顺时针增长（y 轴向下）
            double rad = deg * Math.PI / 180.0;
            // 箭头基部落在圆弧上
            float bx = (float)(cx + radius * Math.Cos(rad));
            float by = (float)(cy + radius * Math.Sin(rad));
            // 顺时针切线方向
            float tx = (float)(-Math.Sin(rad));
            float ty = (float)(Math.Cos(rad));
            // 径向（用于撑开三角的两个后角）
            float nx = (float)Math.Cos(rad);
            float ny = (float)Math.Sin(rad);

            // 三角：尖端沿切线向前，两个后角向后张开
            var p1 = new PointF(bx + tx * size, by + ty * size);                       // 尖
            var p2 = new PointF(bx - tx * size * 0.55f + nx * size * 0.62f,
                                by - ty * size * 0.55f + ny * size * 0.62f);           // 后角 1
            var p3 = new PointF(bx - tx * size * 0.55f - nx * size * 0.62f,
                                by - ty * size * 0.55f - ny * size * 0.62f);           // 后角 2

            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[] { p1, p2, p3 });
                // 用同色实心填充，视觉上比 1.6px 线更实，远看能立住
                using (var br = new SolidBrush(pen.Color)) g.FillPath(br, path);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { spin.Stop(); spin.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ==================== 热点卡片 ====================
    public class HotspotCard : MotionControl
    {
        public Hotspot H;
        public bool Busy;
        public string BusyText = "连接中…";
        public Action<HotspotCard> ClickAction, RightAction;

        static readonly Font FTitle = new Font(T.Family, 12f, FontStyle.Bold);
        static readonly Font FSub = new Font(T.Family, 9f);
        static readonly Font FBadge = new Font(T.Family, 8.5f);
        static readonly Font FTiny = new Font(T.Family, 8f);
        static readonly Font FHint = new Font(T.Family, 8.5f);
        static readonly Font FNote = new Font(T.FamilyCn, 8.5f);

        public HotspotCard()
        {
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            // 用系统默认箭头：Windows 的列表项、按钮都是箭头，
            // 手形是网页里的东西，出现在桌面面板上既不合系统也不好看。
            Cursor = Cursors.Default;
            Size = new Size(300, 78);
        }

        static void Round(GraphicsPath p, Rectangle r, int rad)
        {
            var np = T.RoundedRect(r, rad);
            p.Reset();
            p.AddPath(np, false);
            np.Dispose();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Right) { if (RightAction != null) RightAction(this); }
            else if (e.Button == MouseButtons.Left) { if (ClickAction != null) ClickAction(this); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (H == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var body = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = T.RoundedRect(body, T.RCard))
            {
                // 白卡 + 极淡投影；已连接换成淡蓝底 + 蓝描边（小程序选中态）
                // 悬停用缓动进度混色，不再是「一帧跳到底色」
                Color baseFace = H.Connected ? T.Raised : T.Card;
                Color face = T.Blend(baseFace, H.Connected ? T.Raised : T.Hover, Hover);
                T.DropShadow(g, body, T.RCard);
                using (var bg = new SolidBrush(face))
                    g.FillPath(bg, path);
                // 已连接（使用中）：2px 主蓝系描边 + 左侧一小段品牌蓝指示条
                if (H.Connected)
                {
                    using (var sp = new Pen(T.SelectedBorder, 2f)) g.DrawPath(sp, path);
                    using (var ib = new SolidBrush(T.AccentNet))
                    using (var ip = T.RoundedRect(new Rectangle(2, 16, 3, Height - 34), 2))
                        g.FillPath(ib, ip);
                }
                else
                {
                    using (var pen = new Pen(T.Hairline, 1f)) g.DrawPath(pen, path);
                }
            }

            // 让「图标圆」与右侧两行文字共用同一条视觉中线：
            // 标题在 15~37、副行在 40~60，两行整体中线约 37.5；
            // 圆取 44px 高、中心落在 37.5（而不是整卡 46），图标和文字才真正对齐。
            var iconR = new Rectangle(16, 16, 44, 44);
            using (var b = new SolidBrush(H.Connected ? T.AccentInnerSoft : T.Field))
                g.FillEllipse(b, iconR);
            // Wi-Fi 图标走系统字形（E701），和 Windows 自己的网络图标同一套轮廓
            Glyph.Draw(g, Glyph.Wifi,
                new RectangleF(iconR.X + 10, iconR.Y + 10, iconR.Width - 20, iconR.Height - 20),
                H.Connected ? T.AccentInner : T.TextMuted, 1.05f);

            int left = iconR.Right + 14;
            int tw = Width - 16 - left;

            // 标题行：名字 + 右侧状态（同一行，不打架）。徽章统一用 RBadge 圆角。
            string tag = Busy ? "…" : (H.Connected ? "使用中" : (H.Visible ? "可用" : "离线"));
            var szTag = g.MeasureString(tag, FBadge);
            int tagW = (int)szTag.Width + 18;

            // 标题加 0.4px 字间距，名字更透气；按「标签左侧」的剩余宽度裁剪，不让长名字压标签
            var br = new Rectangle(Width - tagW - 14, 16, tagW, 20);
            int titleRight = br.X - 10;
            using (var b = new SolidBrush(T.TextPrimary))
            {
                var ts = g.MeasureString(H.SSID, FTitle);
                if (left + ts.Width <= titleRight)
                {
                    T.DrawSpaced(g, H.SSID, FTitle, b, left, 15, 0.4f);
                }
                else
                {
                    using (var fmt = new StringFormat
                    {
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                        g.DrawString(H.SSID, FTitle, b,
                            new RectangleF(left, 13, Math.Max(20, titleRight - left), 22), fmt);
                }
            }
            // 标签：淡底 + 同色字，不描边（对齐小程序 .tag-*）
            Color tagBg = H.Connected ? T.AccentInnerSoft : (H.Visible ? T.AccentNetSoft : T.Field);
            Color tagFg = H.Connected ? T.AccentInner : (H.Visible ? T.AccentNet : T.TextMuted);
            using (var bp = T.RoundedRect(br, T.RBadge))
            using (var bb = new SolidBrush(tagBg)) g.FillPath(bb, bp);
            using (var b = new SolidBrush(tagFg))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(tag, FBadge, b, br, fmt);

            // 副行：信号强度或密码提示
            string sub;
            if (Busy) sub = BusyText;
            else if (H.Connected) sub = "已连接" + (H.Signal >= 0 ? "  ·  信号 " + H.Signal + "%" : "");
            else if (H.Visible) sub = "在附近" + (H.Signal >= 0 ? "  ·  信号 " + H.Signal + "%" : "");
            else sub = "当前扫不到";

            using (var b = new SolidBrush(T.Blend(T.TextMuted, T.TextSecondary, Hover)))
            using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(sub, FSub, b, new RectangleF(left, 40, tw, 20), fmt);

            // 信号格：直接用系统字。E86F，四格随强度填充，不再手工摆小方。
            if (H.Visible && H.Signal >= 0)
            {
                int bars = H.Signal >= 80 ? 4 : H.Signal >= 60 ? 3 : H.Signal >= 35 ? 2 : 1;
                Color barColor = bars >= 4 ? (H.Connected ? T.AccentInner : T.TextSecondary)
                              : bars >= 2 ? T.TextMuted
                              : T.TextFaint;
                Glyph.Draw(g, Glyph.SignalBars,
                    new RectangleF(Width - 14 - 22, 44, 22, 18), barColor, 0.85f);
            }

            // 底行（y=62）：左边备注胶囊，右边「首次连接需密码」。            // 两者同一行、一左一右，宽度各自让位，永远不会互相盖住。
            bool needPwd = !H.Saved && H.Visible && !Busy;
                int pwdW = needPwd ? (int)g.MeasureString("首次连接需密码", FHint).Width : 0;
            if (needPwd)
            {
                using (var b = new SolidBrush(T.Warn))
                using (var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Far,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                    g.DrawString("首次连接需密码", FHint, b,
                        new RectangleF(left, 62, Math.Max(30, tw), 18), fmt);
            }

            // 备注：用户自己起的名字（比如「宿舍热点」），画成卡片底部的小胶囊。            // 这是「一眼认出是哪个网络」的信息，所以用主蓝淡底 + 同色字，固定左下。
            string note = Cfg.Note(Cfg.WifiKey(H.SSID));
            if (note.Length > 0)
            {
                int noteMax = tw - (pwdW > 0 ? pwdW + 20 : 0);
                if (noteMax > 46)
                {
                    // 。TextRenderer 量宽：它。GDI+ 。DrawString 在雅黑小字号上差 6~8px。                    // 。MeasureString 会量窄，导致明明放得下也被裁成省略号。
                    int txtW = TextRenderer.MeasureText(note, FNote).Width;
                    var nr = new Rectangle(left, 62, Math.Min(noteMax, txtW + 40), 19);
                    using (var np = T.RoundedRect(nr, T.RBadge))
                    using (var nb = new SolidBrush(T.AccentNetSoft)) g.FillPath(nb, np);
                    Glyph.Draw(g, Glyph.MenuNote, new RectangleF(nr.X + 5, nr.Y + 4, 11, 11), T.AccentNet, 1f);
                    using (var b = new SolidBrush(T.AccentNet))
                    using (var fmt = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                        g.DrawString(note, FNote, b,
                            new RectangleF(nr.X + 18, nr.Y, Math.Max(10, nr.Width - 20), nr.Height), fmt);
                }
            }
        }
    }

    // ==================== 内网卡片 ====================
    public class LanCard : MotionControl
    {
        public LanIf L;
        public bool Preferred;

        static readonly Font FTitle = new Font(T.FamilyCn, 12f, FontStyle.Bold);
        static readonly Font FVal = new Font(T.NumFamily, 9.5f);
        static readonly Font FBadge = new Font(T.FamilyCn, 8.5f);
        static readonly Font FHint = new Font(T.FamilyCn, 8.5f);
        static readonly Font FNote = new Font(T.FamilyCn, 8.5f);

        public LanCard()
        {
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            // 和热点卡片一致：系统默认箭头，不用手。
            Cursor = Cursors.Default;
            Size = new Size(340, 104);
        }

        static void Round(GraphicsPath p, Rectangle r, int rad)
        {
            var np = T.RoundedRect(r, rad);
            p.Reset();
            p.AddPath(np, false);
            np.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (L == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var body = new Rectangle(1, 1, Width - 3, Height - 3);
            bool active = L.IsDefaultEgress;
            using (var path = T.RoundedRect(body, T.RCard))
            {
                // 白卡 + 极淡投影；选中/优先换淡蓝底 + 蓝描。
                Color baseFace = (Preferred || active) ? T.Raised : T.Card;
                Color face = T.Blend(baseFace, (Preferred || active) ? T.Raised : T.Hover, Hover);
                T.DropShadow(g, body, T.RCard);
                using (var bg = new SolidBrush(face))
                    g.FillPath(bg, path);
                // 选中/优先：2px 主蓝系描边 + 左侧一小段品牌蓝指示条，明确但不刺眼
                if (Preferred || active)
                {
                    using (var sp = new Pen(T.SelectedBorder, 2f)) g.DrawPath(sp, path);
                    using (var ib = new SolidBrush(T.AccentNet))
                    using (var ip = T.RoundedRect(new Rectangle(2, 22, 3, Height - 46), 2))
                        g.FillPath(ib, ip);
                }
                else
                {
                    using (var pen = new Pen(T.Hairline, 1f)) g.DrawPath(pen, path);
                }
            }

            // 状态点：网关是否可达。左侧彩色竖条已去掉。            // 用状态点 + 徽章表达同样信息，比在卡片边上贴一条色条干净。
            Color led = L.GwReachable ? T.AccentInner : T.Warn;
            using (var b = new SolidBrush(Color.FromArgb(44, led)))
                g.FillEllipse(b, new Rectangle(16, 21, 14, 14));
            using (var b = new SolidBrush(led))
                g.FillEllipse(b, new Rectangle(19, 24, 8, 8));

            // 标题（标题加 0.4px 字间距，和热点卡片一致）
            float titleW;
            using (var b = new SolidBrush(T.TextPrimary))
            {
                T.DrawSpaced(g, L.Alias, FTitle, b, 40, 18, 0.4f);
                titleW = T.MeasureSpaced(g, L.Alias, FTitle, 0.4f);
            }

            // 右上角状态徽章。            // 「外网。 系统实际出口（问内核得到的，是真的）。
            // 「内网。 挂着内网专线，资料走它；
            // 「优先。 你点过指定的（可能因为度量打平而没真正生效）；
            // 「备用。 既不出口也没专线。            // Linear 规范：胶囊用半透明。+ 语义色文字，不用高饱和实心块。
            bool innerOnly = L.IntranetRoutes.Count > 0;
            string tag;
            Color bgc;
            Color fgc;
            if (active) { tag = "外网"; bgc = T.AccentNetSoft; fgc = T.AccentNet; }
            else if (innerOnly) { tag = "内网"; bgc = T.AccentInnerSoft; fgc = T.AccentInner; }
            else if (Preferred) { tag = "优先"; bgc = T.Field; fgc = T.TextSecondary; }
            else { tag = "备用"; bgc = T.Field; fgc = T.TextMuted; }
            var sz = g.MeasureString(tag, FBadge);
            int bw = (int)sz.Width + 18;
            var br = new Rectangle(Width - bw - 14, 17, bw, 20);
            using (var bp = T.RoundedRect(br, T.RBadge))
            using (var bb = new SolidBrush(bgc)) g.FillPath(bb, bp);
            using (var b = new SolidBrush(fgc))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(tag, FBadge, b, br, sf);

            // 备注：用户自己起的名字，紧跟在网卡名右边的一个小胶囊里。            // 位置在标题行、状态徽章的左边；宽度不够就省略号，绝不压到徽章。
            string note = Cfg.Note(Cfg.LanKey(L.Alias));
            if (note.Length > 0)
            {
                int nx = (int)Math.Round(40 + titleW + 8);
                int nMax = br.X - 8 - nx;
                if (nMax > 70)
                {
                    var szNote = g.MeasureString(note, FNote);
                    // 宽度留出图标 + 左右内边。+ 一点余量：GDI+ 。DrawString 。MeasureString
                    // 略宽，不留余量的话明明放得下也会被裁成省略号。
                    var nr = new Rectangle(nx, 18,
                        Math.Min(nMax, Math.Max((int)szNote.Width, TextRenderer.MeasureText(note, FNote).Width) + 34), 19);
                    using (var np = T.RoundedRect(nr, T.RBadge))
                    using (var nb = new SolidBrush(T.AccentNetSoft)) g.FillPath(nb, np);
                    Glyph.Draw(g, Glyph.MenuNote, new RectangleF(nr.X + 4, nr.Y + 4, 11, 11), T.AccentNet, 1f);
                    using (var nb = new SolidBrush(T.AccentNet))
                    using (var fmt = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                        g.DrawString(note, FNote, nb,
                            new RectangleF(nr.X + 18, nr.Y, Math.Max(10, nr.Width - 22), nr.Height), fmt);
                }
            }

            // 第二行：IP（数据，等宽字体。
            using (var b = new SolidBrush(T.TextSecondary))
                g.DrawString(L.IP + "/" + L.Prefix, FVal, b, new PointF(40, 44));

            // 第三行：网关
            string gwLine = "网关 " + L.Gateway;
            using (var b = new SolidBrush(T.TextMuted))
                g.DrawString(gwLine, FHint, b, new PointF(40, 64));

            // 可达性用文字表达，不。✓✗ 符号（那。emoji 的替代品。
            string reach = L.GwReachable
                ? (L.ReachHow.Length > 0 ? L.ReachHow : "可达")
                : "无响应";
            using (var b = new SolidBrush(L.GwReachable ? T.AccentInner : T.Warn))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(reach, FHint, b, new RectangleF(40, 62, Width - 56, 22), fmt);

            // 第四行：内网专线 / 掩码 + 总度。    // 度量写「总度量」：Windows 就是。路由度。接口度量 挑出口的。    // 只显示接口度量会让人误以为「我明明。 了怎么不生效」。
            string left4, right4;
            if (L.IntranetRoutes.Count > 0)
            {
                left4 = "内网专线 " + L.IntranetRoutes[0] +
                        (L.IntranetRoutes.Count > 1 ? " +" + (L.IntranetRoutes.Count - 1) : "");
            }
            else
            {
                left4 = "掩码 " + L.Mask;
            }
            if (L.TotalMetric > 0) right4 = "总度量 " + L.TotalMetric;
            else right4 = "度量 " + (L.Metric > 0 ? L.Metric.ToString() : "自动");

            using (var b = new SolidBrush(L.IntranetRoutes.Count > 0 ? T.WithAlpha(T.AccentInner, 200) : T.TextMuted))
                g.DrawString(left4, FHint, b, new PointF(40, 88));
            using (var b = new SolidBrush(T.TextMuted))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Far, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(right4, FHint, b, new RectangleF(40, 86, Width - 56, 22), fmt);
        }
    }

    // ==================== 主面。===================
    public class NetPanel : Form
    {
        const string BRAND = "NetSwitcher";
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 0xBEEF;
        const int PAD = 28, GAP = 16, HEADER = 268, SECTION = 40, BOTTOM = 28;
        const int CARDW = 340, CARDH = 92, LANH = 110, COLS = 2;

        Snapshot snap = new Snapshot();
        bool scanning, closingToTray, hotkeyOk;
        string preferred = "";
        NotifyIcon tray;
        System.Windows.Forms.Timer poll;
        List<Control> dyn = new List<Control>();
        RoundButton btnHide, btnExit, btnRefresh, btnGear;
        int hsY = HEADER, lanY = HEADER + 120;
        bool dragArmed, dragging, layoutPending;

        // 内容视口：顶栏 hero 固定，下面的分区放进这个面板里滚动。
        // 原来内容直接铺在窗体上，热点一多窗口被屏幕高度截断，内网分区就永远够不着。
        Panel viewport;
        int scrollY, maxScroll;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public NetPanel()
        {
            preferred = Cfg.LoadPreferredIface();
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            Text = BRAND;
            Icon = BuildIcon();
            BackColor = T.Page;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            ApplyWindowRegion();

            btnGear = new RoundButton { Icon = RoundButton.Kind.Gear };
            btnGear.ClickAction = () => ShowMainMenu();
            btnRefresh = new RoundButton { Icon = RoundButton.Kind.Refresh };
            btnRefresh.ClickAction = () => Scan(true);
            btnHide = new RoundButton { Icon = RoundButton.Kind.Minimize };
            btnHide.ClickAction = () => HidePanel();
            btnExit = new RoundButton { Icon = RoundButton.Kind.Close };
            btnExit.ClickAction = () => ExitApp();

            // 内容视口：盖住 hero 以下的整片区域，自己滚动。
            // 注意：不用 AutoScroll。AutoScroll 会自己把系统原生滚动条装回来，
            // ShowScrollBar 藏掉后它下一次布局又给显示出来——于是屏幕上会出现
            // 「自绘细条 + 系统灰条」两条滚动条。这里干脆自己管偏移：
            // 卡片按内容坐标摆放，滚动时整体上移，右侧只画我们自己的细条。
            viewport = new Panel
            {
                BackColor = T.Page,
                AutoScroll = false
            };
            // 双缓冲：内容区整块重绘时不闪
            var dbProp = typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (dbProp != null) dbProp.SetValue(viewport, true, null);
            // 看板空白处也要能拖动窗口（卡片自己会吃掉鼠标事件，这是预期的。
            viewport.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
            viewport.MouseMove += ViewportMouseMove;
            viewport.MouseLeave += (s, e) => { if (sbHover) { sbHover = false; viewport.Invalidate(); } };
            Controls.Add(viewport);

            // 四个圆形按钮属于固定顶栏，挂在窗体上（不随内容滚动）
            Controls.Add(btnGear);
            Controls.Add(btnRefresh);
            Controls.Add(btnHide);
            Controls.Add(btnExit);

            // 分区标题 / 空状态画在视口内容坐标系里，和卡片同一套坐。
            viewport.Paint += ViewportPaint;
            BuildTray();
            LayoutPanel(true);
            Scan(true);

            poll = new System.Windows.Forms.Timer { Interval = 10000 };
            poll.Tick += (s, e) => Scan(false);
            poll.Start();
        }

        /// <summary>窗口四角是否交给 DWM 画（Win11 22000+）。
        /// 用注册表读 build 号，避免 app.manifest 没声明 Win11 时 Environment.OSVersion 被「版本谎言」降级成 6.2。</summary>
        internal static readonly bool UseDwmRound = CheckWin11();
        static bool CheckWin11()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var s = k != null ? k.GetValue("CurrentBuildNumber") as string : null;
                    int b;
                    return s != null && int.TryParse(s, out b) && b >= 22000;
                }
            }
            catch { return false; }
        }

        // 无边框窗体本身没有 WS_THICKFRAME，这里不要再加：加了会带出系统的
        // 不可见调整边框，DWM 会把四周约 8px 削掉，客户区看着是整窗、实际绘制被裁，
        // 底部和右侧就会露出桌面。DWMWCP_ROUND 对无边框窗体同样生效，圆角由合成器裁。

        /// <summary>
        /// 窗口圆角。Win11 交给 DWM（GPU 合成、边缘有抗锯齿，不再有 Region 的 1-bit 阶梯）；
        /// 老系统没有 DWM 圆角属性，退回 GDI+ Region 裁剪。
        /// 尺寸变化（LayoutPanel 改 ClientSize）后必须重裁，否则圆角是旧尺寸的。
        /// </summary>
        void ApplyWindowRegion()
        {
            if (UseDwmRound) return;   // 不设 Region，让 DWM 画
            try
            {
                using (var path = T.RoundedRect(new Rectangle(0, 0, Width, Height), T.RPanel))
                    Region = new Region(path);
            }
            catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyWindowRegion();
        }

        // ---------- 采集 ----------
        void Scan(bool force)
        {
            if (scanning || IsDisposed) return;
            scanning = true;
            // 只有用户手动触发的扫描（点刷。/ F5 / 菜单）才转圈。            // 后台。10 秒的自动轮询。force=false，不转——否则用户什么都没点。            // 按钮自己开始转，看起来像出了问题。
            if (force && btnRefresh != null) btnRefresh.Spinning = true;
            new Thread(() =>
            {
                Snapshot s = null;
                try { s = new Detector().Build(); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        scanning = false;
                        if (force && btnRefresh != null) btnRefresh.Spinning = false;
                        if (s == null || IsDisposed) return;
                        bool changed = SignatureDiffers(snap, s);
                        snap = s;
                        // 拖动期间完全不动画面，松手后再统一刷新
                        if (dragging) { layoutPending = true; return; }
                        if (force || changed) LayoutPanel(false);
                        else RefreshCards();
                        Invalidate();
                    }));
                }
                catch { scanning = false; }
            }) { IsBackground = true }.Start();
        }

        static bool SignatureDiffers(Snapshot a, Snapshot b)
        {
            if (a.Hotspots == null || b.Hotspots == null) return true;
            if (a.Hotspots.Count != b.Hotspots.Count) return true;
            for (int i = 0; i < a.Hotspots.Count; i++)
                if (!string.Equals(a.Hotspots[i].SSID, b.Hotspots[i].SSID, StringComparison.Ordinal)) return true;
            if (a.Lans == null || b.Lans == null) return true;
            if (a.Lans.Count != b.Lans.Count) return true;
            for (int i = 0; i < a.Lans.Count; i++)
            {
                if (!string.Equals(a.Lans[i].Alias, b.Lans[i].Alias, StringComparison.Ordinal)) return true;
                if (!string.Equals(a.Lans[i].IP, b.Lans[i].IP, StringComparison.Ordinal)) return true;
            }
            if (a.PinnedRoutes.Count != b.PinnedRoutes.Count) return true;
            // 出口/内网路径变了必须重排重绘，否则顶栏会一直显示旧出口
            if (!string.Equals(a.BestAlias, b.BestAlias, StringComparison.Ordinal)) return true;
            if (!string.Equals(a.IntranetAlias, b.IntranetAlias, StringComparison.Ordinal)) return true;
            return false;
        }

        void RefreshCards()
        {
            // 拖动时重绘子控件会让窗口画面抖，等松手再。
            if (dragging) { layoutPending = true; return; }

            foreach (var c in dyn)
            {
                var hc = c as HotspotCard;
                if (hc != null && hc.H != null)
                {
                    var h = snap.Hotspots.FirstOrDefault(x => string.Equals(x.SSID, hc.H.SSID, StringComparison.OrdinalIgnoreCase));
                    if (h != null) { hc.H = h; hc.Invalidate(); }
                    continue;
                }
                var lc = c as LanCard;
                if (lc != null && lc.L != null)
                {
                    var l = snap.Lans.FirstOrDefault(x => x.Alias == lc.L.Alias);
                    if (l != null)
                    {
                        lc.L = l;
                        lc.Preferred = string.Equals(preferred, l.Alias, StringComparison.OrdinalIgnoreCase);
                        lc.Invalidate();
                    }
                }
            }
        }

        // ---------- 布局 ----------
        void LayoutPanel(bool initial)
        {
            // 拖动过程中重建子控件会让窗口抖，推迟到松开鼠标再做
            if (dragging) { layoutPending = true; return; }

            Point old = Location;
            bool keep = !initial && Visible && old.X > -1000;

            foreach (var c in dyn) c.Dispose();
            dyn.Clear();

            var hs = snap.Hotspots ?? new List<Hotspot>();
            var ls = snap.Lans ?? new List<LanIf>();

            int rowsH = Math.Max(hs.Count > 0 ? 1 : 0, (hs.Count + COLS - 1) / COLS);
            int rowsL = Math.Max(0, (ls.Count + COLS - 1) / COLS);

            // 热点为空时留出空状态卡片的高度。4），不要撑出一整块卡片。            // 这个 72 必须。DrawEmptyState 里画的块同高，否则下面的分区会压上来。
            const int EMPTY_H = 72;
            int hsBlockH = hs.Count > 0
                ? Math.Max(1, rowsH) * CARDH + Math.Max(0, rowsH - 1) * GAP
                : EMPTY_H;

            // 内容坐标：从视口顶部 0 开始排（hero 在窗体上固定绘制，不参与滚动）。            // 顶部。10px：分区标题不贴着 hero 下缘，视觉上更透气。
            int contentTop = 10;
            hsY = contentTop;
            lanY = hsY + SECTION + hsBlockH + 36;
            int contentBottom = lanY + SECTION + Math.Max(1, rowsL) * LANH + Math.Max(0, rowsL - 1) * GAP;
            int contentH = contentBottom + BOTTOM;

            int wantW = PAD * 2 + COLS * CARDW + GAP;

            var wa = Screen.PrimaryScreen.WorkingArea;
            // 视口顶部落在 hero 下边缘（hero 14 + 208 + 16 间距。
            int viewTop = 14 + 208 + 16;
            // 先按「没有滚动条」算一次视口高度，判断到底需不需要滚。
            int viewHAvailable = Math.Max(0, (wa.Height - 40) - viewTop - 8);
            bool needScroll = contentH + 8 > viewHAvailable;
            // 系统滚动条已。ShowScrollBar 隐藏，不再占宽度，窗口也不用加宽。
            // needScroll 只用来决定自绘细条画不画。
            // 内容总高 + 视口顶部不能超过屏幕，超出部分交给滚。
            int idealH = viewTop + contentH;
            int wantH = Math.Min(idealH, wa.Height - 40);

            int x, y;
            if (keep) { x = old.X; y = old.Y; }
            else
            {
                var saved = Cfg.LoadUi();
                if (saved.X > -1 && saved.Y > -1) { x = saved.X; y = saved.Y; }
                else { x = wa.Right - wantW - 24; y = wa.Top + 26; }
            }
            x = Math.Max(wa.Left + 6, Math.Min(x, wa.Right - wantW - 6));
            y = Math.Max(wa.Top + 6, Math.Min(y, wa.Bottom - wantH - 6));

            ClientSize = new Size(wantW, wantH);
            Location = new Point(x, y);
            ApplyWindowRegion(); // 尺寸变了，窗口圆角区域要跟着重裁

            // 视口铺满 hero 以下到窗体底部，内容在里面滚。            // 。8px 底部内边距，避免最后一张卡片贴着窗口圆角。            // 不用 AutoScroll，所以视口永远是全宽、绝不会有系统滚动条。
            int viewH = Math.Max(0, Height - viewTop - 8);
            viewport.SetBounds(0, viewTop, Width, viewH);

            for (int i = 0; i < hs.Count; i++)
            {
                int r = i / COLS, c = i % COLS;
                var card = new HotspotCard { H = hs[i] };
                card.ClickAction = OnHotspotClick;
                card.RightAction = OnHotspotRight;
                card.SetBounds(PAD + c * (CARDW + GAP), hsY + SECTION + r * (CARDH + GAP) - scrollY, CARDW, CARDH);
                viewport.Controls.Add(card);
                dyn.Add(card);
                card.Tag = "hs:" + i;   // 记住它在热点网格里的序号，滚动重排时不用再猜行号
            }

            for (int i = 0; i < ls.Count; i++)
            {
                int r = i / COLS, c = i % COLS;
                var li = ls[i];
                var card = new LanCard { L = li };
                card.Preferred = string.Equals(preferred, li.Alias, StringComparison.OrdinalIgnoreCase);
                // 两类卡片统一：右键 = 打开选项菜单（和热点卡一致）。
                // 热点卡左键仍是「连接」，这里左键不抢动作，避免误触设置。
                card.MouseClick += (s2, e2) => { if (e2.Button == MouseButtons.Right) AskUseIface(li); };
                card.SetBounds(PAD + c * (CARDW + GAP), lanY + SECTION + r * (LANH + GAP) - scrollY, CARDW, LANH);
                viewport.Controls.Add(card);
                dyn.Add(card);
                card.Tag = "lan:" + i;  // 内网卡片序号
            }

            // 滚动范围：内容总高超出视口高度的部分。不用 AutoScroll，自己算。
            maxScroll = Math.Max(0, contentH + 8 - viewH);
            if (scrollY > maxScroll) scrollY = maxScroll;
            if (scrollY < 0) scrollY = 0;

            // 四个按钮挂在窗体（固定顶栏）上，y 必须落在蓝色 hero 内。            // hero 。y=14 起、高 208；按。30px 高，放在 30 让中心与标题行齐平。            // 之前误用 4（那是视口内坐标），按钮上半截压到窗口外，看起来「一半一半」。            // 顺序。Windows 惯例：设。刷新 。最小化 。关闭（关闭永远贴最右）。
            const int BTN_Y = 30;
            int bx = Width - PAD - 30;          // 最右按钮（关闭）的位置
            btnExit.Location = new Point(bx, BTN_Y);
            btnHide.Location = new Point(bx - 38, BTN_Y);
            btnRefresh.Location = new Point(bx - 76, BTN_Y);
            btnGear.Location = new Point(bx - 114, BTN_Y);

            foreach (var c in dyn) c.BringToFront();
            btnGear.BringToFront();
            btnRefresh.BringToFront();
            btnHide.BringToFront();
            btnExit.BringToFront();
            viewport.Invalidate();
            Invalidate(true);
        }

        /// <summary>
        /// 按像素滚动内容，并夹在合法范围内。滚。/ 键盘 / 拖动滚动条都走这里。
        /// 不用 AutoScroll：滚。= 把卡片整体上。scrollY，分区标题在绘制时同样偏移，
        /// 于是内容和标题永远同步，也不会冒出第二条系统滚动条。
        /// </summary>
        void ScrollBy(int dy)
        {
            if (viewport == null) return;
            int target = Math.Max(0, Math.Min(scrollY + dy, maxScroll));
            if (target == scrollY) return;
            ApplyScroll(target);
        }

        /// <summary>把滚动位置落到界面上：卡片整体上。+ 整块重绘</summary>
        void ApplyScroll(int target)
        {
            scrollY = target;
            foreach (var c in dyn)
            {
                string tag = c.Tag as string;
                if (tag == null) continue;
                int colon = tag.IndexOf(':');
                if (colon < 0) continue;
                string kind = tag.Substring(0, colon);
                int idx;
                if (!int.TryParse(tag.Substring(colon + 1), out idx)) continue;

                int r = idx / COLS, col = idx % COLS;
                if (kind == "hs")
                    c.Top = hsY + SECTION + r * (CARDH + GAP) - scrollY;
                else
                    c.Top = lanY + SECTION + r * (LANH + GAP) - scrollY;
                c.Left = PAD + col * (CARDW + GAP);
            }
            viewport.Invalidate();
        }

        // ---------- 绘制 ----------
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width, Height);
            // 画布。F5F7FB 浅灰蓝，和小程序 page 底色一。
            using (var b = new SolidBrush(T.Page))
                g.FillRectangle(b, r);
            // 顶栏：一块蓝渐变，就是小程序首页那张 .hero
            // hero：四周留白、圆。16 的蓝色卡片，浮在 #F5F7FB 上（小程。.hero 的观感）
            var hero = new Rectangle(16, 14, Width - 32, 208);
            using (var path = T.RoundedRect(hero, T.RPanel))
            {
                // 落影：hero 浮在画布上，靠一层很淡的蓝灰影拉出层。
                using (var sh = new SolidBrush(Color.FromArgb(22, 0x2B, 0x4C, 0xA8)))
                    using (var sp = T.RoundedRect(new Rectangle(hero.X, hero.Y + 5, hero.Width, hero.Height), T.RPanel))
                        g.FillPath(sh, sp);
                // 主渐变：从左上到右下，顶部略亮，收口在深蓝，避免整块死板
                using (var b = new LinearGradientBrush(
                    new Rectangle(hero.X, hero.Y, hero.Width, hero.Height),
                    T.HeroTop, T.HeroBottom, 62f))
                    g.FillPath(b, path);

                // 右上一抹柔光：让蓝色不是一整片纯色，有光源方向。
                g.SetClip(path, CombineMode.Intersect);
                using (var glow = new GraphicsPath())
                {
                    // 一个很大的椭圆放在右上角外侧，用低透明度白做高。
                    glow.AddEllipse(hero.Right - hero.Width * 0.62f, hero.Y - hero.Height * 0.55f,
                                    hero.Width * 0.9f, hero.Height * 1.5f);
                    using (var pat = new PathGradientBrush(glow))
                    {
                        pat.CenterColor = Color.FromArgb(30, 255, 255, 255);
                        pat.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillPath(pat, glow);
                    }
                }
                g.ResetClip();
            }
            // 窗口外边框：一圈极淡的灰，替代系统边框。
            // Win11 走 DWM 圆角时由系统画边框/阴影，自绘直线会在圆角外露出直角，
            // 所以只在退回 Region 裁剪的老系统上画。
            if (!UseDwmRound)
            {
                using (var pen = new Pen(T.Hairline, 1f))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 标题：白字压在蓝 hero 上，下方只留白，不再画分隔线
            using (var f = new Font(T.Family, 13.5f, FontStyle.Bold))
            using (var b = new SolidBrush(T.OnHero))
                g.DrawString("网络切换", f, b, new PointF(36, 32));

            // ===== 核心信息区：外网走谁 / 内网走谁，分两行大字写明 =====
            // 原来是「出。+ Wi-Fi」小胶囊，只说了出口，看不出内网走哪条，
            // 用户还得自己去卡片里猜。现在两条路径各自一行，直说。
            // 两行路径直接压在蓝色 hero 上，靠留白分层，不再套一个高度写死的白块
            // （之前那个块。96、内容要 112，第二行会漏到框外）
            string wifiVal = snap.WifiSSID.Length > 0
                ? snap.WifiSSID + (snap.WifiSignal >= 0 ? "  " + snap.WifiSignal + "%" : "")
                : "";

            using (var fLab = new Font(T.Family, 8f))
            using (var fVal = new Font(T.Family, 13f, FontStyle.Bold))
            using (var fSub = new Font(T.Family, 8.5f))
            using (var bLab = new SolidBrush(T.OnHeroFaint))
            using (var bSub = new SolidBrush(T.OnHeroMuted))
            {
                // 。1 行：外网（默认出口）
                bool hasNet = !string.IsNullOrEmpty(snap.BestAlias);
            string netName = hasNet ? snap.BestAlias : "未检测到";
                string netIp = hasNet ? snap.BestIP : "";
                var netColor = hasNet ? T.OnHero : T.OnHeroSoft;
                float y1 = 72f;
                T.DrawSpaced(g, "外网 · 上网", fLab, bLab, 36, y1, 0.8f);
                using (var bv = new SolidBrush(netColor))
                    g.DrawString(netName, fVal, bv, new PointF(36, y1 + 16));
                if (netIp.Length > 0)
                {
                    var w = g.MeasureString(netName, fVal).Width;
                    g.DrawString(netIp, T.FNumSm, bSub, new PointF(40 + w, y1 + 24));
                }

                // 。2 行：内网（专线路由）
                bool hasInner = !string.IsNullOrEmpty(snap.IntranetAlias) && snap.IntranetRoutes.Count > 0;
                string innerName = hasInner ? snap.IntranetAlias : "未配置内网专线";
                string innerSub = "";
                if (hasInner)
                {
                innerSub = string.Join("、", snap.IntranetRoutes.Take(2).ToArray());
                    if (snap.IntranetRoutes.Count > 2) innerSub += " +" + (snap.IntranetRoutes.Count - 2);
                }
                var innerColor = hasInner ? T.OnHero : T.OnHeroFaint;
                float y2 = y1 + 56;
                T.DrawSpaced(g, "内网 · 资料", fLab, bLab, 36, y2, 0.8f);
                using (var bv = new SolidBrush(innerColor))
                    g.DrawString(innerName, fVal, bv, new PointF(36, y2 + 16));
                if (innerSub.Length > 0)
                {
                    var w = g.MeasureString(innerName, fVal).Width;
                    g.DrawString(innerSub, T.FNumSm, bSub, new PointF(40 + w, y2 + 24));
                }

                // 一句话结论，放在两行路径下方单独一条，别和热点标题。
                string note;
                Color noteColor;
                if (hasNet && hasInner)
                {
                    note = "两条线并存：上网走 " + snap.BestAlias + "，内网资料走 " + snap.IntranetAlias;
                    noteColor = T.OnHeroMuted;
                }
                else if (hasNet)
                {
                    note = "当前只有外网出口，没有内网专线";
                    noteColor = T.OnHeroFaint;
                }
                else
                {
                    note = "没有可用的默认出口";
                    noteColor = T.OnHeroSoft;
                }

                // 结论行与 Wi-Fi 胶囊同排：左边结论，右边胶囊，互不重。
                float noteY = y2 + 56;
                float noteRight = Width - 36;
                using (var fLab2 = new Font(T.Family, 8.5f))
                using (var fVal2 = new Font(T.Family, 9f))
                {
                    if (wifiVal.Length > 0)
                    {
                        float chipW = g.MeasureString("Wi-Fi", fLab2).Width
                                    + g.MeasureString(wifiVal, fVal2).Width + 26;
                        float chipX = noteRight - chipW;
                        noteRight = chipX - 14;
                        using (var bChipVal = new SolidBrush(T.OnHero))
                            DrawChip(g, chipX, noteY - 4, "Wi-Fi", wifiVal, fLab2, fVal2,
                                     bLab, bChipVal, T.AccentInner);
                    }
                }
                using (var fn = new Font(T.Family, 8.5f))
                using (var bn = new SolidBrush(noteColor))
                using (var fmt = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                    g.DrawString(note, fn, bn,
                        new RectangleF(36, noteY, Math.Max(40, noteRight - 36), 18), fmt);
            }
            // 分区标题与空状态已移到 ViewportPaint，跟着内容一起滚动
        }

        /// <summary>
        /// 视口内容绘制：分区标题 + 空状态。坐标是「内容坐标」（从视口顶部 0 计），
        /// 由 Panel 的滚动偏移自动平移，所以滚到哪画到哪，不会和卡片错位。
        /// </summary>

        /// <summary>
        /// 视口内容绘制：分区标题 + 空状态。坐标是「内容坐标」（从视口顶部 0 计），
        /// 由 Panel 的滚动偏移自动平移，所以滚到哪画到哪，不会和卡片错位。
        /// </summary>
        void ViewportPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            // 自己管的滚动：把内容坐标整体上移 scrollY 就直接映射到视口客户区
            g.TranslateTransform(0, -scrollY);

            // 分区标题的右对齐按视口宽度算（视口比窗体窄时不会画到外面）
            int contentW = viewport.ClientSize.Width;
            SectionHeader(g, "手机热点", hsY, snap.Hotspots != null ? snap.Hotspots.Count : 0, "左键连接 / 右键选项", contentW);
            SectionHeader(g, "内网 / 有线", lanY, snap.Lans != null ? snap.Lans.Count : 0, "右键打开选项", contentW);

            // 空状态：不是干巴巴一行灰字，而是「图标 + 原因 + 下一步怎么做」。
            // 空状态是用户唯一会盯着看很久的界面，必须告诉他现在是什么情况、该点哪里。
            if (snap.Hotspots == null || snap.Hotspots.Count == 0)
            {
                var box = new Rectangle(PAD, hsY + SECTION, contentW - PAD * 2, 72);
                DrawEmptyState(g, box, Glyph.Wifi,
                    scanning ? "正在扫描附近的 Wi-Fi…" : "附近没扫到任何 Wi-Fi",
                    scanning ? "稍等一下，扫描完会自动列出来" : "确认无线已打开，或点右上角的刷新按钮重试");
            }

            // 滚动条画在「客户区坐标」上，不能跟着内容一起平移，所以先撤销位移
            g.ResetTransform();
            DrawScrollBar(g);
        }

        // 自绘细滚动条：系统那条灰色带箭头的老式滚动条在这个面板里非常出戏。        // 这里只画一。6px 的圆角滑块，浮在内容右侧，悬停时才加深。
        const int SB_W = 6;          // 滑块宽度
        const int SB_GUTTER = 8;     // 距右边缘留白（贴住但不出窗）
        bool sbHover;                // 指针是否停在滚动条热区上

        void DrawScrollBar(Graphics g)
        {
            if (viewport == null || maxScroll <= 0) return;
            float trackH = viewport.ClientSize.Height;
            float trackW = viewport.ClientSize.Width;
            if (trackH <= 0) return;

            // 滑块长度 = 可视比例，太短也要给个可抓的最小长。
            float ratio = (float)viewport.ClientSize.Height / (maxScroll + viewport.ClientSize.Height);
            float thumbH = Math.Max(36f, trackH * ratio);
            float travel = Math.Max(0f, trackH - thumbH);
            float prog = maxScroll <= 0 ? 0f : (float)scrollY / maxScroll;
            float thumbY = travel * Math.Min(1f, Math.Max(0f, prog));

            float x = trackW - SB_GUTTER;
            var thumb = new RectangleF(x, thumbY, SB_W, thumbH);
            int alpha = (int)Math.Round(255 * (sbHover ? 0.6f : 0.34f));
            using (var p = T.RoundedRect(new Rectangle(
                (int)Math.Round(thumb.X), (int)Math.Round(thumb.Y),
                (int)thumb.Width, (int)Math.Round(thumb.Height)), SB_W / 2 + 1))
            using (var b = new SolidBrush(Color.FromArgb(alpha, 0x5A, 0x64, 0x74)))
                g.FillPath(b, p);
        }

        /// <summary>
        /// 统一的空状态：左侧软圆。Wi-Fi 图标 + 右侧「主文案 / 次文案」两行，
        /// 和热点卡片同一套横向语言，垂直整体居中。原来的居中堆叠。        /// 图标小、文字贴底被裁掉，又宽又空，属于典型的「空状态没人设计」。
        /// </summary>
        void DrawEmptyState(Graphics g, Rectangle box, char glyph, string title, string hint)
        {
            using (var path = T.RoundedRect(box, T.RCard))
            {
                using (var bb = new SolidBrush(T.Card)) g.FillPath(bb, path);
                using (var pen = new Pen(T.Hairline, 1f)) g.DrawPath(pen, path);
            }

            // 左侧 44px 软圆 + 居中字形，和热点卡片的图标位完全同参
            var iconR = new Rectangle(box.X + 16, box.Y + (box.Height - 44) / 2, 44, 44);
            using (var b = new SolidBrush(T.Field))
                g.FillEllipse(b, iconR);
            Glyph.Draw(g, glyph,
                new RectangleF(iconR.X + 10, iconR.Y + 10, iconR.Width - 20, iconR.Height - 20),
                T.TextMuted, 1.05f);

            // 右侧两行文字，在剩余空间里垂直居。
            float textX = iconR.Right + 14;
            float textW = box.Right - 16 - textX;
            float lineH = 20f;
            float blockH = lineH * 2 + 4;
            float textY = box.Y + (box.Height - blockH) / 2f;

            using (var ft = new Font(T.FamilyCn, 9.5f, FontStyle.Bold))
            using (var fh = new Font(T.FamilyCn, 8.5f))
            using (var bt = new SolidBrush(T.TextSecondary))
            using (var bh = new SolidBrush(T.TextFaint))
            {
                g.DrawString(title, ft, bt, new RectangleF(textX, textY, textW, lineH));
                g.DrawString(hint, fh, bh, new RectangleF(textX, textY + lineH + 4, textW, lineH));
            }
        }

        /// <summary>画一个「标。+ 值」的小胶囊，返回下一。x</summary>
        float DrawChip(Graphics g, float x, float y, string label, string val,
                       Font fLab, Font fVal, Brush bLab, Brush bVal, Color accent)
        {
            var szLab = g.MeasureString(label, fLab);
            var szVal = g.MeasureString(val, fVal);
            float w = szLab.Width + szVal.Width + 26;
            var rr = new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Ceiling(w), 24);

            // hero 上的胶囊：半透明白底，和 hero-add 同一。
            using (var path = T.RoundedRect(rr, T.RBadge))
            {
                using (var bb = new SolidBrush(Color.FromArgb(38, 255, 255, 255))) g.FillPath(bb, path);
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f)) g.DrawPath(pen, path);
            }
            g.DrawString(label, fLab, bLab, new PointF(x + 9, y + 5));
            g.DrawString(val, fVal, bVal, new PointF(x + 9 + szLab.Width + 6, y + 4));
            return x + w + 10;
        }

        void SectionHeader(Graphics g, string title, int y, int count, string hint)
        {
            SectionHeader(g, title, y, count, hint, Width);
        }

        /// <summary>
        /// 分区标题。contentW 是内容可用宽度（视口宽度），右侧提示文字按它右对齐，
        /// 不能直接用窗体宽度——视口扣掉滚动条后比窗体窄，用窗体宽度会把提示画到外面。
        /// </summary>
        void SectionHeader(Graphics g, string title, int y, int count, string hint, int contentW)
        {
            Font fb = null, fs = null, fh = null;
            try
            {
                fb = new Font(T.Family, 10.5f, FontStyle.Bold);
                // 区块标题。0.4px 字间距，与卡片标题一。
                using (var b = new SolidBrush(T.TextPrimary))
                    T.DrawSpaced(g, title, fb, b, PAD + 2, y + 1, 0.4f);
                var sz = T.MeasureSpaced(g, title, fb, 0.4f);

                fs = new Font(T.Family, 8.5f);
                // 空列表用灰色就够了——下面已经画了带解释的空状态卡片，
                // 橙色警示在这里是双重报警，只会显得刺眼。
                using (var b = new SolidBrush(count > 0 ? T.TextMuted : T.TextFaint))
                    g.DrawString("  " + (count > 0 ? count + " 个" : "未检测到"), fs, b, new PointF(PAD + 4 + sz, y + 4));

                fh = new Font(T.Family, 8.5f);
                using (var b = new SolidBrush(T.TextFaint))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Far })
                    g.DrawString(hint, fh, b, new RectangleF(PAD, y + 4, contentW - PAD * 2 - 2, 16), fmt);
            }
            finally
            {
                if (fb != null) fb.Dispose();
                if (fs != null) fs.Dispose();
                if (fh != null) fh.Dispose();
            }
        }

        // ---------- 交互 ----------
        void OnHotspotClick(HotspotCard c)
        {
            if (c.Busy || c.H == null) return;
            var h = c.H;
            if (h.Connected) { Notify("已经在用这个了", h.SSID, ToolTipIcon.Info); return; }
            if (!h.Saved && !h.Visible)
            {
                Notify("这个热点现在扫不到", h.SSID, ToolTipIcon.Warning);
                return;
            }
            ConnectHotspot(c, h.SSID);
        }

        void ConnectHotspot(HotspotCard card, string ssid)
        {
            card.Busy = true;
            card.BusyText = "正在连接 " + ssid + " …";
            card.Invalidate();

            new Thread(() =>
            {
                string err = Wifi.Connect(ssid, () =>
                {
                    string result = null;
                    try
                    {
                        Invoke((Action)(() =>
                        {
                            card.Busy = false; card.Invalidate();
                            using (var f = MakePrompt("需要密码", "连接「" + ssid + "」，输入热点密码：", true))
                                if (f.ShowDialog(this) == DialogResult.OK) result = f.Tag as string;
                            card.Busy = true; card.BusyText = "正在连接 " + ssid + " …"; card.Invalidate();
                        }));
                    }
                    catch { }
                    return result;
                });

                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) return;
                        card.Busy = false;
                        if (err == null) Notify("已切换", "现在连的是 " + ssid, ToolTipIcon.Info);
                        else if (err != "已取消") Notify("没切成", err, ToolTipIcon.Warning);
                        Scan(true);
                    }));
                }
                catch { }
            }) { IsBackground = true }.Start();
        }

        void OnHotspotRight(HotspotCard c)
        {
            if (c.H == null) return;
            var h = c.H;
            string key = Cfg.WifiKey(h.SSID);
            var menu = new FlatMenu();
            menu.Add("复制 SSID", Glyph.MenuCopy, () => { try { Clipboard.SetText(h.SSID); } catch { } }, h.SSID);
            menu.Add("连接", Glyph.MenuConnect, () => { if (!h.Connected) ConnectHotspot(c, h.SSID); },
                     h.Connected ? "使用中" : "", bold: true, accent: true, enabled: !h.Connected);
            menu.Add("断开当前 Wi-Fi", Glyph.MenuDisconnect, () =>
            {
                new Thread(() => { Wifi.Disconnect(); try { BeginInvoke((Action)(() => Scan(true))); } catch { } }) { IsBackground = true }.Start();
            });
            menu.Sep();
            menu.Add(Cfg.Note(key).Length > 0 ? "修改备注…" : "添加备注…", Glyph.MenuNote,
                     () => EditNote(key, "给这个热点起个名字", h.SSID), Cfg.Note(key));
            bool pinned = Cfg.LoadPinned().Contains(h.SSID, StringComparer.OrdinalIgnoreCase);
            menu.Add(pinned ? "从面板移除" : "固定到面板", pinned ? Glyph.MenuUnpin : Glyph.MenuPin, () =>
            {
                var p = Cfg.LoadPinned();
                if (pinned) p = p.Where(x => !string.Equals(x, h.SSID, StringComparison.OrdinalIgnoreCase)).ToList();
                else if (!p.Contains(h.SSID, StringComparer.OrdinalIgnoreCase)) p.Add(h.SSID);
                Cfg.SavePinned(p);
                Scan(true);
            });
            menu.Sep();
            menu.Add("手动输入 SSID 并连接…", Glyph.MenuSearch, () =>
            {
                string v = Input("手动连接", "热点名称（SSID）", "");
                if (string.IsNullOrEmpty(v)) return;
                ConnectHotspot(c, v.Trim());
            });
            menu.ShowAtScreen(Cursor.Position);   // 在鼠标右键的位置弹出
        }

        void AskUseIface(LanIf li)
        {
            var all = snap.Lans.Select(x => x.Alias).ToList();
            string key = Cfg.LanKey(li.Alias);
            var menu = new FlatMenu();

            // 内网固定路由：上网照旧走默认出口，只有这个网段走这块网卡。
            // 「取消」必须针对系统里真实存在的那条持久路由，
            // 也就。li.PinnedSubnets（netsh 读出来的），而不是网卡自己的直连网段。
            // 之前这里取的是 Subnets[0]（网卡自己的直连网段），
            // 而真正固定的是另一条内网专线网段，点下去删的是另一条路由，
            // 结果就是「点了没反应」还误以为生效了。
            if (li.PinnedSubnets.Count > 0)
            {
                foreach (var pinned in li.PinnedSubnets)
                {
                    string target = pinned;
                    menu.Add("取消内网固定", Glyph.MenuUnpin, () => TogglePin(li, target, true),
                             target, bold: true, accent: true);
                }
                menu.Sep();
            }
            else if (li.Subnets.Count > 0)
            {
                // 还没固定过时，给一个「固定这条直连网段」的动作。
                string subnet = li.Subnets[0];
                menu.Add("固定内网网段", Glyph.MenuPin, () => TogglePin(li, subnet, false),
                         subnet, bold: true, accent: true);
                menu.Sep();
            }

            // 整个改走它（原来的优先出口功能，保留）。
            menu.Add("所有上网流量改走这块网卡", Glyph.MenuRoute, () => ApplyIfacePriority(li.Alias, all),
                     li.Alias);
            if (preferred.Length > 0 || li.IsDefaultEgress)
                menu.Add("恢复系统自动选择出口", Glyph.MenuSync, () => ResetPriority());

            menu.Sep();
            menu.Add(Cfg.Note(key).Length > 0 ? "修改备注…" : "添加备注…", Glyph.MenuNote,
                     () => EditNote(key, "给这个网络起个名字", li.Alias + "  " + li.IP), Cfg.Note(key));
            menu.Add("复制 IP", Glyph.MenuCopy, () => { try { Clipboard.SetText(li.IP); } catch { } }, li.IP);
            menu.Add("Ping 网关", Glyph.MenuSend, () =>
            {
                new Thread(() =>
                {
                    bool inArp; string how;
                    bool ok = Lan.ProbeGateway(li.Gateway, 1500, out inArp, out how);
                    try
                    {
                        BeginInvoke((Action)(() => Info(
                            (ok ? "网关有回应" : "网关没回应") + "\r\n\r\n" +
                            "网关：" + li.Gateway + "\r\n本机：" + li.IP + "/" + li.Prefix +
                            "\r\n依据：" + (how.Length > 0 ? how : "无") +
                            "\r\nARP：" + (inArp ? "在表里（同一广播域）" : "不在表里"),
                            li.Alias, !ok)));
                    }
                    catch { }
                }) { IsBackground = true }.Start();
            });
            menu.Add("详情", Glyph.MenuInfo, () => Info("网卡详情 · " + li.Alias,
                "网卡：" + li.Alias + "\r\n型号：" + li.Desc +
                (Cfg.Note(key).Length > 0 ? "\r\n备注：" + Cfg.Note(key) : "") +
                "\r\nIP：" + li.IP + "/" + li.Prefix + "   (" + li.Mask + ")" +
                "\r\n网关：" + li.Gateway + (li.GwReachable ? "   ✓ " + li.ReachHow : "   ✗ 无响应") +
                "\r\nARP：" + (li.InArp ? "在表里（同一广播域）" : "不在表里") +
                "\r\n直连网段：" + string.Join("、", li.Subnets.ToArray()) +
                "\r\n内网专线：" + (li.IntranetRoutes.Count > 0 ? string.Join("、", li.IntranetRoutes.ToArray()) : "无") +
                "\r\n能否上外网：" + (li.CanInternet ? "能" : "不能（只能访问内网）") +
                "\r\n已固定路由：" + (li.HasPinnedRoute ? "是" : "否") +
                "\r\n链路：" + li.SpeedMbps + " Mbps" +
                "\r\n度量：" + li.Metric +
                "\r\n默认出口：" + (li.IsDefaultEgress ? "是" : "否") +
                "\r\n虚拟网卡：" + (li.IsVirtual ? "是" : "否")));
            menu.ShowAtScreen(Cursor.Position);
        }

        /// <summary>
        /// 编辑一条备注。留空就是删掉这条备注。        /// 输完立刻刷新卡片，卡片上马上就能看到，不用重新扫描。
        /// </summary>
        void EditNote(string key, string label, string who)
        {
            string cur = Cfg.Note(key);
            string v = Input("备注 · " + who, label + "（留空 = 清除备注）", cur);
            if (v == null) return;              // 点了取消
            Cfg.SetNote(key, v);
            RefreshCards();
            viewport.Invalidate();
        }

        /// <summary>
        /// 给内网网段挂一条永久路由，或者把已有的一条摘掉。
        /// 效果：上网继续走 Wi-Fi/主路由，只有这个内网网段走这块有线网卡，两者并存。
        /// </summary>
        void TogglePin(LanIf li, string subnet, bool currentlyPinned)
        {
            // 明确告诉提权子进程是「加」还是「删」，不要让它自己判断。
            // 自己判断就会踩到注册表读不到持久路由的坑，把「删」做成「加」。
            string action = currentlyPinned ? "unpin" : "pin";
            string res = ElevateAndWait("--" + action + " \"" + subnet + "\" \"" + li.Gateway +
                                        "\" \"" + li.Alias + "\"", 25000);
            if (res == null)
            {
                Notify("已生效", (currentlyPinned ? "已取消固定 " : "已固定 ") + subnet +
                        " ，经 " + li.Gateway, ToolTipIcon.Info);
                WaitThenScan(900);
            }
            else if (res == "CANCELLED")
            {
                Notify("已取消", "UAC 被拒绝，路由没有改动。", ToolTipIcon.Warning);
                Scan(true);
            }
            else
            {
                Notify("操作失败", res, ToolTipIcon.Error);
                Scan(true);
            }
        }

        void ApplyIfacePriority(string alias, List<string> all)
        {
            RefreshCards();

            // 先自己试（已经是管理员就直接成）
            string err = Lan.ApplyPriority(alias, all, 6000);
            if (err == null)
            {
                preferred = alias;
                Cfg.SavePreferredIface(alias);
                RefreshCards();
                Notify("已切换", "外网流量优先走 " + alias + "（内网专线不受影响）", ToolTipIcon.Info);
                // 等内核路由重新收敛后再检测，否则界面还是旧的出口
                WaitThenScan(1200);
                return;
            }

            Notify("需要管理员权限", "正在请求提权（会弹 UAC，点「是」）…", ToolTipIcon.Info);
            string res = ElevateAndWait("--priority \"" + alias + "\" " +
                                        string.Join("|", all.Select(a => "\"" + a + "\"").ToArray()), 25000);
            if (res == null)
            {
                preferred = alias;
                Cfg.SavePreferredIface(alias);
                Notify("已切换", "所有流量现在优先走 " + alias, ToolTipIcon.Info);
                WaitThenScan(1200);
            }
            else if (res == "CANCELLED")
            {
                Notify("已取消", "UAC 被拒绝，出口没有改动。", ToolTipIcon.Warning);
                Scan(true);
            }
            else
            {
                Notify("切换失败", res, ToolTipIcon.Error);
                Scan(true);
            }
        }

        /// <summary>等系统把路由表刷新完再重新检）</summary>
        void WaitThenScan(int ms)
        {
            var t = new System.Windows.Forms.Timer { Interval = ms };
            t.Tick += (s2, e2) =>
            {
                t.Stop();
                t.Dispose();
                Scan(true);
            };
            t.Start();
        }

        void ResetPriority()
        {
            string res = ElevateAndWait("--reset-priority", 25000);
            if (res == null)
            {
                preferred = "";
                Cfg.SavePreferredIface("");
                Notify("已重置", "优先出口交回系统自动选择", ToolTipIcon.Info);
                WaitThenScan(900);
            }
            else if (res == "CANCELLED")
            {
                Notify("已取消", "UAC 被拒绝，没有改动。", ToolTipIcon.Warning);
                Scan(true);
            }
            else
            {
                Notify("重置失败", res, ToolTipIcon.Error);
                Scan(true);
            }
        }

        static string ResultFile
        {
            get { return Path.Combine(Path.GetTempPath(), "NetSwitcher_priv.txt"); }
        }

        /// <summary>
        /// 提权执行子命令并等结果。返回 null = 成功。
        /// "CANCELLED" = 用户拒绝 UAC；其它 = 错误文本。
        /// 原来这里用 catch{} 把失败全吞了，界面就假装成功。
        /// </summary>
        static string ElevateAndWait(string args, int timeoutMs)
        {
            try { if (System.IO.File.Exists(ResultFile)) System.IO.File.Delete(ResultFile); } catch { }
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                // 用户点了「否」，Win32Exception 1223
                if (ex is System.ComponentModel.Win32Exception) return "CANCELLED";
                return ex.Message;
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(250);
                try
                {
                    if (System.IO.File.Exists(ResultFile))
                    {
                        string txt = System.IO.File.ReadAllText(ResultFile, Encoding.UTF8).Trim();
                        System.IO.File.Delete(ResultFile);
                        return txt.Length == 0 ? null : txt;
                    }
                }
                catch { }
            }

            return "提权进程没有回音（可能一直被 UAC 拦着）。";
        }

        // ---------- 提权子命。---------
        /// <summary>把提权结果写给主进程看。空内容 = 成功）</summary>
        static void WriteResult(string msg)
        {
            try
            {
                System.IO.File.WriteAllText(ResultFile, msg ?? "", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>跑一个命令，靠退出码判定成败</summary>
        static string RunCmd(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(timeoutMs);
            if (p.ExitCode != 0) return "退出码 " + p.ExitCode + "：" + o.Trim();
                    return null;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static int RunElevatedTask(string[] args)
        {
            try
            {
                if (args.Length >= 1 && args[0] == "--reset-priority")
                {
                    var all = new List<string>();
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        all.Add(ni.Name);
                    }
                    string firstErr = null;
                    foreach (var a in all)
                    {
                        // 注意：metric=automatic 会清掉手动度量，恢复系统自动挑。
                        string err = RunCmd("netsh.exe",
                            "int ipv4 set interface \"" + a + "\" metric=automatic", 6000);
                        if (err != null && firstErr == null) firstErr = a + ": " + err;
                    }
                    WriteResult(firstErr);
                    return firstErr == null ? 0 : 1;
                }
                if (args.Length >= 2 && args[0] == "--priority")
                {
                    var alias = args[1];
                    var all = new List<string>();
                    if (args.Length >= 3)
                        all.AddRange(args[2].Split('|').Select(x => x.Trim('"')).Where(x => x.Length > 0));
                    string err = Lan.ApplyPriority(alias, all, 8000);
                    WriteResult(err);
                    return err == null ? 0 : 1;
                }
                if (args.Length >= 3 && (args[0] == "--pin" || args[0] == "--unpin"))
                {
                    var subnet = args[1].Trim('"');
                    var gw = args[2].Trim('"');
                    var alias = args.Length >= 4 ? args[3].Trim('"') : "";
                    // 动作由主进程明确指定。-pin 是加。-unpin 是删。
                    // 以前这里靠「注册表里有没有这条」自己判断要不要删，
                    // 。netsh 写的持久路由不在那个注册表键里，
                    // 于是「取消」被判成「没固定」，转头又加了一遍——点了没反应。
                    string err = args[0] == "--unpin"
                        ? Lan.UnpinRoute(subnet, gw, alias, 8000)
                        : Lan.PinRoute(subnet, gw, alias, 8000);
                    WriteResult(err);
                    return err == null ? 0 : 1;
                }
            }
            catch { }
            return 2;
        }

        // ---------- 菜单 ----------
        /// <summary>托盘菜单用的 ToolStrip 配色。托盘必须用 ContextMenuStrip，面板内。FlatMenu）</summary>
        ContextMenuStrip MkMenu()
        {
            return new ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Font(T.FamilyCn, 9.5f),
                BackColor = T.Card,
                ForeColor = T.TextPrimary,
                Renderer = new ToolStripProfessionalRenderer(new LightMenuColors())
            };
        }

        void ShowMainMenu()
        {
            var menu = new FlatMenu();
            menu.Add("立即重新检测", Glyph.MenuRefresh, () => Scan(true), "F5");
            menu.Add("手动输入 SSID 连接…", Glyph.MenuSearch, () =>
            {
                string v = Input("手动连接", "热点名称（SSID）", "");
                if (string.IsNullOrEmpty(v)) return;
                var card = dyn.OfType<HotspotCard>().FirstOrDefault();
                if (card != null) ConnectHotspot(card, v.Trim());
            });
            menu.Add("断开当前 Wi-Fi", Glyph.MenuDisconnect, () =>
            {
                new Thread(() => { Wifi.Disconnect(); try { BeginInvoke((Action)(() => Scan(true))); } catch { } }) { IsBackground = true }.Start();
            });
            menu.Sep();

            bool startup = IsStartup();
            menu.Add("开机自启", startup ? Glyph.MenuCheck : Glyph.MenuPower, () =>
            {
                SetStartup(!IsStartup());
                Scan(false);
            }, startup ? "已开启" : "已关闭");

            menu.Add("打开配置文件夹", Glyph.MenuFolder, () =>
            {
                try { Directory.CreateDirectory(Cfg.Dir); Process.Start("explorer.exe", "\"" + Cfg.Dir + "\""); } catch { }
            });
            menu.Add("使用说明", Glyph.MenuHelp, () => Info("使用说明 · " + BRAND,
                BRAND + " " + AppVer.Text + "\r\n\r\n" +
                "手机热点：自动扫描并按「连接中 > 固定 > 在附近 > 离线」排序，点一下就切过去。\r\n" +
                "内网 / 有线：列出所有带网关的网卡，点卡片选走哪条线。\r\n" +
                "备注：右键点卡片 -> 添加备注，给网络起个自己的名字，卡片上直接显示。\r\n\r\n" +
                "Ctrl+Alt+N 呼出 / F5 重新检测 / Esc 收起\r\n\r\n" +
                "配置目录：" + Cfg.Dir));
            menu.Sep();
            menu.Add("退出 " + BRAND, Glyph.MenuExit, () => ExitApp());
            menu.ShowAt(btnGear, new Point(0, btnGear.Height + 6));
        }

        // ---------- 托盘 ----------
        void Notify(string title, string text, ToolTipIcon icon)
        {
            try { if (tray != null && tray.Visible) tray.ShowBalloonTip(2500, title, text, icon); } catch { }
        }

        void BuildTray()
        {
            var cm = MkMenu();
            cm.Items.Add("显示面板  (Ctrl+Alt+N)", null, (s, e) => ShowPanel());
            cm.Items.Add("隐藏面板", null, (s, e) => HidePanel());
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("立即重新检测", null, (s, e) => { ShowPanel(); Scan(true); });
            var miStartup = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = IsStartup() };
            miStartup.CheckedChanged += (s, e) => { if (miStartup.Checked != IsStartup()) SetStartup(miStartup.Checked); };
            cm.Items.Add(miStartup);
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("退出", null, (s, e) => ExitApp());

            tray = new NotifyIcon
            {
                Icon = Icon,
                Text = "NetSwitcher 热点切换 / 内网识别",
                ContextMenuStrip = cm,
                Visible = true
            };
            tray.DoubleClick += (s, e) => { if (Visible) HidePanel(); else ShowPanel(); };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (Visible) HidePanel(); else ShowPanel(); } };
        }

        static string StartupLnk()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NetSwitcher.lnk");
        }

        static bool IsStartup()
        {
            try { return System.IO.File.Exists(StartupLnk()); } catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                if (on)
                {
                    if (System.IO.File.Exists(StartupLnk())) return;
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t == null) return;
                    object sh = Activator.CreateInstance(t);
                    object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { StartupLnk() });
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
                    lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                    lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
                    Marshal.ReleaseComObject(lnk);
                    Marshal.ReleaseComObject(sh);
                }
                else if (System.IO.File.Exists(StartupLnk()))
                {
                    System.IO.File.Delete(StartupLnk());
                }
            }
            catch (Exception ex) { Info("设置开机自启失败", ex.Message, true); }
        }

        // ---------- 对话框 ----------
        // 面板用的是「蓝色 hero 圆角卡 + 白卡」这套语言，系统 MessageBox / 默认 Form
        // 是直角灰框 + 系统按钮，摆进来非常突兀。这里统一用自绘的 FlatDialog：
        // 无边框圆角、蓝色标题条、白底内容、淡蓝圆角输入框、主蓝圆角按钮。
        public class FlatButton : Control
        {
            bool hover;
            public bool Primary;
            public FlatButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Default;
                Height = 34;
                Font = new Font(T.FamilyCn, 9.5f);
            }
            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                Color face = Primary
                    ? (hover ? Color.FromArgb(0x2A, 0x55, 0xBE) : T.AccentNet)
                    : (hover ? Color.FromArgb(0xE2, 0xE8, 0xF2) : T.Field);
                using (var p = T.RoundedRect(r, T.RBadge + 2))
                using (var b = new SolidBrush(face)) g.FillPath(b, p);
                using (var b = new SolidBrush(Primary ? Color.White : T.TextPrimary))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Text, Font, b, r, sf);
            }
        }

        public class FlatDialog : Form
        {
            public const int HeaderH = 50;
            static readonly Font FTitle = new Font(T.FamilyCn, 11f, FontStyle.Bold);
            static readonly Font FLabel = new Font(T.FamilyCn, 9.5f);
            static readonly Font FInput = new Font(T.FamilyCn, 10.5f);

            public string Value { get { return tb == null ? "" : tb.Text; } }
            public bool Warning;

            readonly string dlgTitle, labelText;
            readonly bool inputMode;
            TextBox tb;
            int bodyTop, fieldY, buttonsY;
            Rectangle fieldRect;
            bool dragging, closeHover;
            Point dragAt;

            public FlatDialog(string title, string label, string def, bool input, bool password)
            {
                dlgTitle = title; labelText = label; inputMode = input;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterParent;
                ShowInTaskbar = false;
                MaximizeBox = false; MinimizeBox = false;
                BackColor = T.Card;
                DoubleBuffered = true;
                KeyPreview = true;
                AutoScaleMode = AutoScaleMode.None;
                Font = FLabel;

                const int W = 420, PADX = 22;
                int cw = W - PADX * 2;
                using (var g = CreateGraphics())
                {
                    if (input)
                    {
                        int labelH = (int)Math.Ceiling(g.MeasureString(label, FLabel, cw).Height) + 2;
                        bodyTop = HeaderH + 20;
                        fieldY = bodyTop + labelH + 10;
                        buttonsY = fieldY + 36 + 20;
                    }
                    else
                    {
                        int msgH = (int)Math.Ceiling(g.MeasureString(label, FLabel, cw).Height) + 4;
                        bodyTop = HeaderH + 20;
                        buttonsY = bodyTop + msgH + 20;
                    }
                }
                ClientSize = new Size(W, buttonsY + 34 + 20);
                fieldRect = new Rectangle(PADX, fieldY, cw, 36);
                ApplyRegion();

                if (input)
                {
                    tb = new TextBox
                    {
                        Left = PADX + 10, Top = fieldY + 4, Width = cw - 20, Height = 28,
                        BorderStyle = BorderStyle.None,
                        BackColor = T.Field,
                        ForeColor = T.TextPrimary,
                        Font = FInput,
                        Text = def ?? "",
                        UseSystemPasswordChar = password
                    };
                    tb.GotFocus += (s, e) => Invalidate();
                    tb.LostFocus += (s, e) => Invalidate();
                    Controls.Add(tb);
                }

                const int bw = 92, bh = 34, gap = 10;
                int right = W - PADX;
                var ok = new FlatButton
                {
                    Text = "确定", Primary = true, Width = bw, Height = bh,
                    Left = right - bw - (input ? bw + gap : 0), Top = buttonsY
                };
                ok.Click += (s, e) => { Tag = Value; DialogResult = DialogResult.OK; Close(); };
                Controls.Add(ok);
                if (input)
                {
                    var cancel = new FlatButton { Text = "取消", Width = bw, Height = bh, Left = right - bw, Top = buttonsY };
                    cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
                    Controls.Add(cancel);
                }
                Shown += (s, e) => { if (tb != null) { tb.Focus(); tb.SelectAll(); } };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                    cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (!NetPanel.UseDwmRound) return;
                try
                {
                    int v = 2;   // DWMWCP_ROUND
                    Native.DwmSetWindowAttribute(Handle, 33, ref v, 4);
                }
                catch { }
            }

            void ApplyRegion()
            {
                if (NetPanel.UseDwmRound) return;   // DWM 圆角，不用 1-bit Region
                try { using (var p = T.RoundedRect(new Rectangle(0, 0, Width, Height), T.RPanel)) Region = new Region(p); }
                catch { }
            }

            // 和主面板一致：不加 WS_THICKFRAME，DWM 圆角由合成器裁，
            // 不会引入系统边框占位把标题条/白卡的绘制宽度挤偏。

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                // 白底圆角卡
                using (var p = T.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), T.RPanel))
                using (var b = new SolidBrush(T.Card)) g.FillPath(b, p);

                // 蓝色标题条：顶部圆角与卡片对齐，下半被裁掉贴住白卡
                g.SetClip(new Rectangle(0, 0, Width, HeaderH), CombineMode.Intersect);
                using (var hp = T.RoundedRect(new Rectangle(0, 0, Width, HeaderH + 16), T.RPanel))
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, HeaderH),
                           T.HeroTop, T.HeroBottom, 62f))
                    g.FillPath(b, hp);
                g.ResetClip();

                using (var b = new SolidBrush(T.OnHero))
                    g.DrawString(dlgTitle, FTitle, b, new PointF(20, 15));
                float cx = Width - 26, cy = HeaderH / 2f;
                using (var pen = new Pen(closeHover ? T.OnHero : T.OnHeroMuted, 1.6f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
                    g.DrawLine(pen, cx + 6, cy - 6, cx - 6, cy + 6);
                }

                if (inputMode)
                {
                    using (var b = new SolidBrush(T.TextSecondary))
                        g.DrawString(labelText, FLabel, b,
                            new RectangleF(22, bodyTop, Width - 44, fieldY - bodyTop - 8));
                    using (var fp = T.RoundedRect(fieldRect, T.RBadge + 2))
                    using (var b = new SolidBrush(T.Field)) g.FillPath(b, fp);
                    using (var fp2 = T.RoundedRect(fieldRect, T.RBadge + 2))
                    using (var pen = new Pen(tb != null && tb.Focused ? T.SelectedBorder : T.Hairline,
                                             tb != null && tb.Focused ? 1.6f : 1f))
                        g.DrawPath(pen, fp2);
                }
                else
                {
                    using (var b = new SolidBrush(Warning ? T.Warn : T.TextSecondary))
                        g.DrawString(labelText, FLabel, b,
                            new RectangleF(22, bodyTop, Width - 44, buttonsY - bodyTop - 16));
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || e.Y >= HeaderH) return;
                if (e.X >= Width - 40) { DialogResult = DialogResult.Cancel; Close(); return; }
                dragging = true;
                dragAt = new Point(e.X, e.Y);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragging)
                {
                    Location = new Point(Location.X + e.X - dragAt.X, Location.Y + e.Y - dragAt.Y);
                    return;
                }
                bool ch = e.Y < HeaderH && e.X >= Width - 40;
                if (ch != closeHover) { closeHover = ch; Invalidate(); }
            }

            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter) { Tag = Value; DialogResult = DialogResult.OK; Close(); }
            }
        }

        public static Form MakePrompt(string title, string label, bool password)
        {
            return new FlatDialog(title, label, "", true, password);
        }

        public static string Input(string title, string label, string def)
        {
            using (var d = new FlatDialog(title, label, def, true, false))
            {
                var owner = Form.ActiveForm;
                var r = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
                return r == DialogResult.OK ? d.Value : null;
            }
        }

        /// <summary>和面板同一套语言的提示框，替代系统 MessageBox。</summary>
        public static DialogResult Info(string title, string message, bool warning = false)
        {
            using (var d = new FlatDialog(title, message, "", false, false) { Warning = warning })
            {
                var owner = Form.ActiveForm;
                return owner != null ? d.ShowDialog(owner) : d.ShowDialog();
            }
        }

        // ---------- 窗口 ----------
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                // DWMWINDOWATTRIBUTE.WINDOW_CORNER_PREFERENCE = 33
                // 1 = DONOTROUND（原来的值，强制直角），2 = ROUND（Win11 22000+）
                // 用 DWM 圆角时四角由 GPU 合成、自带抗锯齿，不再有 Region 的 1-bit 阶梯。
                // 只设这个属性，不做 DwmExtendFrameIntoClientArea(1,1,1,1)：
                // 那个会让 DWM 在客户区内侧再画一圈系统边框，圆角外多出一条灰线。
                int v = UseDwmRound ? 2 : 1;
                Native.DwmSetWindowAttribute(Handle, 33, ref v, 4);
            }
            catch { }
            try { hotkeyOk = RegisterHotKey(Handle, HOTKEY_ID, 0x0002 | 0x0001, 0x4E); } catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyOk) { try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { } hotkeyOk = false; }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_SHOWPANEL) { ShowPanel(); return; }
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { TogglePanel(); return; }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (closingToTray) { base.OnFormClosing(e); return; }
            e.Cancel = true;
            HidePanel();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Scan(true);
        }

        void ShowPanel()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Show();
                Native.ShowWindow(Handle, 5);
                BringToFront();
                Activate();
                Native.SetForegroundWindow(Handle);
            }
            catch { }
            Scan(false);
        }

        void HidePanel()
        {
            closingToTray = true;
            Cfg.SaveUi(Location);
            Hide();
            closingToTray = false;
        }

        void TogglePanel() { if (Visible) HidePanel(); else ShowPanel(); }

        void ExitApp()
        {
            closingToTray = true;
            Cfg.SaveUi(Location);
            if (tray != null) tray.Visible = false;
            Application.Exit();
        }

        // 拖拽用「屏幕坐标锚点」：
        // 按下时记下光标相对窗口左上角的偏移，之后每次把窗口摆。光标屏幕位。- 偏移。    // 绝不能用客户区坐。e.X/e.Y 来算位移——窗口一动，光标相对窗口的位置就变了。    // 下一帧会算出反向位移把窗口拽回去，表现就是来回抽动、弹跳。
        Point dragAnchor;
        Point dragOrigin;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            BeginDrag();
        }

        /// <summary>开始一次窗口拖动（窗体与视口空白处都走这里）</summary>
        void BeginDrag()
        {
            Point screen = Cursor.Position;
            dragOrigin = screen;
            dragAnchor = new Point(screen.X - Location.X, screen.Y - Location.Y);
            dragArmed = true;
            dragging = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ContinueDrag();
        }

        /// <summary>视口空白处的拖动，和窗体走同一套位移逻辑</summary>
        void ViewportMouseMove(object sender, MouseEventArgs e)
        {
            // 自绘滚动条的热区：指针靠近右边缘时加深滑块，给「这里能拖」的暗示
            bool hot = maxScroll > 0 && e.X >= viewport.ClientSize.Width - 20;
            if (hot != sbHover) { sbHover = hot; viewport.Invalidate(); }
            ContinueDrag();
        }

        void ContinueDrag()
        {
            if (!dragArmed) return;
            if (Control.MouseButtons != MouseButtons.Left)
            {
                dragArmed = false;
                if (dragging) { dragging = false; if (Capture) Capture = false; }
                return;
            }

            Point screen = Cursor.Position;

            if (!dragging)
            {
                // 超过系统拖拽阈值才算真拖，避免点击时手抖把窗口挪走
                Size dz = SystemInformation.DragSize;
                if (Math.Abs(screen.X - dragOrigin.X) < dz.Width &&
                    Math.Abs(screen.Y - dragOrigin.Y) < dz.Height) return;

                dragging = true;
                Capture = true;
            }

            // 直接用屏幕坐标定位，窗口移动不会反过来影响这个。
            Location = new Point(screen.X - dragAnchor.X, screen.Y - dragAnchor.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool was = dragging;
            dragArmed = false;
            dragging = false;
            if (Capture) Capture = false;

            if (was)
            {
                Cfg.SaveUi(Location);
                // 拖动期间被推迟的布局，现在补。
                if (layoutPending)
                {
                    layoutPending = false;
                    LayoutPanel(false);
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) HidePanel();
            if (e.KeyCode == Keys.F5) Scan(true);
            if (e.KeyCode == Keys.Down) ScrollBy(68);
            if (e.KeyCode == Keys.Up) ScrollBy(-68);
            if (e.KeyCode == Keys.PageDown) ScrollBy(viewport == null ? 0 : viewport.Height - 80);
            if (e.KeyCode == Keys.PageUp) ScrollBy(viewport == null ? 0 : -(viewport.Height - 80));
        }

        /// <summary>
        /// 滚轮：无论指针在窗体上还是视口里都滚内容。
        /// 视口。AutoScroll 。Panel，它自己会处理滚轮；这里兜住
        /// 「指针在 hero 顶栏上」的情况，避免那里滚不动。
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-e.Delta / 120 * 68);
        }

        /// <summary>
        /// 窗口 / 托盘图标一律用 exe 自己内嵌。app.ico（编译时 /win32icon 打进去的那份）。
        /// 以前这里。Graphics 现画一个圆 + 两道弧，和资源管理器里显示的 exe 图标不是同一张，
        /// 托盘、任务栏、桌面三处对不上。现在只有一份图标，处处一致。
        /// </summary>
        static Icon BuildIcon()
        {
            try
            {
                var ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch { }
            try
            {
                // exe 没带资源时，退回编译目录里。app.ico
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string icoPath = Path.Combine(dir, "app.ico");
                if (System.IO.File.Exists(icoPath)) return new Icon(icoPath);
            }
            catch { }
            return SystemIcons.Application;
        }
    }

    // ==================== 入口 ====================
    static class Program
    {
        [STAThread]
        static void Main(string[] rawArgs)
        {
            if (rawArgs != null && rawArgs.Length > 0 && rawArgs[0].StartsWith("--"))
            {
                Environment.Exit(NetPanel.RunElevatedTask(rawArgs));
                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool created;
                using (var mtx = new Mutex(true, "NetSwitcher_SingleInstance_v3", out created))
                {
                    if (!created)
                    {
                        IntPtr exist = Native.FindWindow(null, "NetSwitcher");
                        if (exist != IntPtr.Zero)
                        {
                            Native.ShowWindow(exist, 9);
                            Native.PostMessage(exist, Native.WM_SHOWPANEL, IntPtr.Zero, IntPtr.Zero);
                            Native.SetForegroundWindow(exist);
                        }
                        return;
                    }
                    Application.Run(new NetPanel());
                    GC.KeepAlive(mtx);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.WriteAllText(Path.Combine(Path.GetTempPath(), "NetSwitcher_crash.txt"), ex.ToString());
                    MessageBox.Show("启动失败：\r\n" + ex.Message, "NetSwitcher");
                }
                catch { }
            }
        }
    }
}
