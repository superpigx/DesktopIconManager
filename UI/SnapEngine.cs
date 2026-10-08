using System;
using System.Collections.Generic;
using System.Drawing;

namespace DesktopIconManager.UI
{
    /// <summary>拖动/缩放时显示的一条对齐参考线。</summary>
    internal struct SnapGuide
    {
        /// <summary>true = 竖线（吸附的是 X 轴）；false = 横线（吸附的是 Y 轴）。</summary>
        public bool Vertical;
        /// <summary>线的位置（屏幕坐标）。</summary>
        public int Position;
        /// <summary>线在另一轴上的起点。</summary>
        public int From;
        /// <summary>线在另一轴上的终点。</summary>
        public int To;
    }

    /// <summary>
    /// 面板吸附（对齐）引擎。
    ///
    /// 对应参考实现里的一句描述：
    ///   "Snapping works while moving as well as resizing,
    ///    with a configurable gap and screen-edge protection."
    ///
    /// 四件事：
    ///   1. 与其它面板的边对齐（左/右/上/下/居中）、按固定间距紧贴、同宽同高
    ///   2. 与屏幕边缘对齐
    ///   3. **迟滞**：吸住之后要多拖一段才松开，否则在阈值边界会一直抖
    ///   4. 屏幕边缘保护 —— 面板不会被拖到屏幕外而抓不回来
    ///
    /// 每条轴独立求解：取「距离当前值最近、且在阈值之内」的那个候选位置。
    /// </summary>
    internal sealed class SnapEngine
    {
        /// <summary>两个面板紧贴时留的空隙。参考实现把这个值做成可配置项。</summary>
        public const int DefaultGap = 8;

        /// <summary>磁吸生效的距离阈值 —— 超过这个距离就不吸，避免「甩不掉」。</summary>
        public const int DefaultThreshold = 12;

        /// <summary>
        /// 迟滞余量。
        ///
        /// 吸住之后再拖这么多像素才松开。没有它的话，鼠标正好停在阈值边界时
        /// 会一帧吸住、一帧弹开，看起来就是抖动 —— 少了「吸住」的踏实感。
        /// </summary>
        public const int Hysteresis = 6;

        // 候选参与吸附的是哪条边 —— 决定参考线画在哪里
        private const int EdgeStart = 0;    // 左 / 上
        private const int EdgeEnd = 1;      // 右 / 下
        private const int EdgeCenter = 2;   // 中心

        private readonly List<SnapGuide> _guides = new List<SnapGuide>();

        private bool _enabled = true;
        private int _gap = DefaultGap;
        private int _threshold = DefaultThreshold;

        // 上次吸住的候选（迟滞用）
        private bool _hasLockX, _hasLockY;
        private Cand _lockX, _lockY;

        public bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>紧贴时间距。</summary>
        public int Gap
        {
            get { return _gap; }
            set { _gap = Math.Max(0, value); }
        }

        /// <summary>吸附阈值。</summary>
        public int Threshold
        {
            get { return _threshold; }
            set { _threshold = Math.Max(1, value); }
        }

        /// <summary>当前应绘制的参考线。</summary>
        public IList<SnapGuide> Guides { get { return _guides; } }

        /// <summary>清空参考线并释放迟滞锁定（每次开始/结束拖拽时调用）。</summary>
        public void ClearGuides()
        {
            _guides.Clear();
            _hasLockX = false;
            _hasLockY = false;
        }

        // ============================================================
        //  候选位置
        // ============================================================

        private struct Cand
        {
            /// <summary>基准坐标：按 Edge 的含义，我的这条边/中心应落到的位置。</summary>
            public int Value;
            /// <summary>参与吸附的是哪条边（EdgeStart / EdgeEnd / EdgeCenter）。</summary>
            public int Edge;
            /// <summary>是否来自另一个面板（false 表示来自屏幕）。</summary>
            public bool HasRect;
            /// <summary>来源面板，用于决定参考线长度。</summary>
            public Rectangle Rect;
        }

        /// <summary>
        /// 在候选集里挑一个。
        ///
        /// 优先复用上次吸住的那个（迟滞区间 threshold + Hysteresis），
        /// 超出才重新按阈值搜索 —— 这是「吸附感」的关键。
        /// </summary>
        private bool PickAxis(List<Cand> cands, int current, bool vertical, out Cand picked)
        {
            bool hasLock = vertical ? _hasLockX : _hasLockY;
            Cand lockC = vertical ? _lockX : _lockY;

            if (hasLock && Math.Abs(lockC.Value - current) <= _threshold + Hysteresis)
            {
                picked = lockC;
                return true;
            }

            int bestD = int.MaxValue;
            Cand best = default(Cand);
            bool found = false;

            foreach (Cand c in cands)
            {
                int d = Math.Abs(c.Value - current);
                if (d > _threshold) continue;
                if (d < bestD) { bestD = d; best = c; found = true; }
            }

            if (vertical)
            {
                _hasLockX = found;
                _lockX = best;
            }
            else
            {
                _hasLockY = found;
                _lockY = best;
            }

            picked = best;
            return found;
        }

        /// <summary>移动吸附：四条边都可能被吸住。</summary>
        public Rectangle SnapMove(Rectangle proposed, IList<Rectangle> others, Rectangle screen)
        {
            _guides.Clear();
            if (!_enabled) return proposed;

            int w = proposed.Width;
            int h = proposed.Height;

            // ---------- X 轴 ----------
            var xs = new List<Cand>();
            if (others != null)
            {
                foreach (Rectangle o in others)
                {
                    // 边对齐
                    xs.Add(new Cand { Value = o.Left, Edge = EdgeStart, HasRect = true, Rect = o });
                    xs.Add(new Cand { Value = o.Right - w, Edge = EdgeEnd, HasRect = true, Rect = o });
                    // 按间距紧贴
                    xs.Add(new Cand { Value = o.Right + _gap, Edge = EdgeStart, HasRect = true, Rect = o });
                    xs.Add(new Cand { Value = o.Left - _gap - w, Edge = EdgeEnd, HasRect = true, Rect = o });
                    // 水平居中
                    xs.Add(new Cand { Value = o.Left + (o.Width - w) / 2, Edge = EdgeCenter, HasRect = true, Rect = o });
                }
            }
            xs.Add(new Cand { Value = screen.Left, Edge = EdgeStart });
            xs.Add(new Cand { Value = screen.Right - w, Edge = EdgeEnd });
            xs.Add(new Cand { Value = screen.Left + (screen.Width - w) / 2, Edge = EdgeCenter });

            int x = proposed.X;
            Cand cx;
            if (PickAxis(xs, x, true, out cx))
            {
                x = cx.Value;
                _guides.Add(new SnapGuide
                {
                    Vertical = true,
                    Position = GuideX(cx, w),
                    From = SpanFrom(cx, proposed.Top, screen.Top, true),
                    To = SpanTo(cx, proposed.Bottom, screen.Bottom, true)
                });
            }

            // ---------- Y 轴 ----------
            var ys = new List<Cand>();
            if (others != null)
            {
                foreach (Rectangle o in others)
                {
                    ys.Add(new Cand { Value = o.Top, Edge = EdgeStart, HasRect = true, Rect = o });
                    ys.Add(new Cand { Value = o.Bottom - h, Edge = EdgeEnd, HasRect = true, Rect = o });
                    ys.Add(new Cand { Value = o.Bottom + _gap, Edge = EdgeStart, HasRect = true, Rect = o });
                    ys.Add(new Cand { Value = o.Top - _gap - h, Edge = EdgeEnd, HasRect = true, Rect = o });
                    ys.Add(new Cand { Value = o.Top + (o.Height - h) / 2, Edge = EdgeCenter, HasRect = true, Rect = o });
                }
            }
            ys.Add(new Cand { Value = screen.Top, Edge = EdgeStart });
            ys.Add(new Cand { Value = screen.Bottom - h, Edge = EdgeEnd });
            ys.Add(new Cand { Value = screen.Top + (screen.Height - h) / 2, Edge = EdgeCenter });

            int y = proposed.Y;
            Cand cy;
            if (PickAxis(ys, y, false, out cy))
            {
                y = cy.Value;
                _guides.Add(new SnapGuide
                {
                    Vertical = false,
                    Position = GuideY(cy, h),
                    From = SpanFrom(cy, proposed.Left, screen.Left, false),
                    To = SpanTo(cy, proposed.Right, screen.Right, false)
                });
            }

            return new Rectangle(x, y, w, h);
        }

        /// <summary>
        /// 缩放吸附：只吸附正在被拖的右下角。
        ///
        /// 除了「边对齐 / 紧贴」，还支持**同宽同高** ——
        /// 拖出一个和隔壁面板一样大的格子，是很常用的整理动作。
        /// </summary>
        public Rectangle SnapResize(Rectangle proposed, IList<Rectangle> others, Rectangle screen,
                                    int minWidth, int minHeight)
        {
            _guides.Clear();
            if (!_enabled) return proposed;

            int x = proposed.X;
            int y = proposed.Y;
            int right = proposed.Right;
            int bottom = proposed.Bottom;

            // ---------- 右边缘 ----------
            var rs = new List<Cand>();
            if (others != null)
            {
                foreach (Rectangle o in others)
                {
                    rs.Add(new Cand { Value = o.Left - _gap, Edge = EdgeEnd, HasRect = true, Rect = o });
                    rs.Add(new Cand { Value = o.Right, Edge = EdgeEnd, HasRect = true, Rect = o });
                    rs.Add(new Cand { Value = o.Left, Edge = EdgeEnd, HasRect = true, Rect = o });
                    rs.Add(new Cand { Value = x + o.Width, Edge = EdgeEnd, HasRect = true, Rect = o });  // 同宽
                }
            }
            rs.Add(new Cand { Value = screen.Right, Edge = EdgeEnd });

            Cand cr;
            bool hasR = PickAxis(rs, right, true, out cr);
            if (hasR) right = cr.Value;

            // ---------- 下边缘 ----------
            var bs = new List<Cand>();
            if (others != null)
            {
                foreach (Rectangle o in others)
                {
                    bs.Add(new Cand { Value = o.Top - _gap, Edge = EdgeEnd, HasRect = true, Rect = o });
                    bs.Add(new Cand { Value = o.Bottom, Edge = EdgeEnd, HasRect = true, Rect = o });
                    bs.Add(new Cand { Value = o.Top, Edge = EdgeEnd, HasRect = true, Rect = o });
                    bs.Add(new Cand { Value = y + o.Height, Edge = EdgeEnd, HasRect = true, Rect = o });  // 同高
                }
            }
            bs.Add(new Cand { Value = screen.Bottom, Edge = EdgeEnd });

            Cand cb;
            bool hasB = PickAxis(bs, bottom, false, out cb);
            if (hasB) bottom = cb.Value;

            // 先按最小尺寸收敛，再决定要不要画参考线 ——
            // 若被最小尺寸卡住，实际边缘并不在吸附位置上，
            // 此时画线会和真实边缘错位，必须丢弃
            int w = Math.Max(minWidth, right - x);
            int h = Math.Max(minHeight, bottom - y);

            if (hasR && x + w == right)
            {
                _guides.Add(new SnapGuide
                {
                    Vertical = true,
                    Position = right,
                    From = SpanFrom(cr, y, screen.Top, true),
                    To = SpanTo(cr, bottom, screen.Bottom, true)
                });
            }

            if (hasB && y + h == bottom)
            {
                _guides.Add(new SnapGuide
                {
                    Vertical = false,
                    Position = bottom,
                    From = SpanFrom(cb, x, screen.Left, false),
                    To = SpanTo(cb, right, screen.Right, false)
                });
            }

            return new Rectangle(x, y, w, h);
        }

        // ============================================================
        //  参考线位置与长度
        // ============================================================

        /// <summary>
        /// 参考线画在「参与吸附的那条边」上。
        ///
        /// 这里必须区分是哪条边：早期版本一律按右边缘算，
        /// 结果对齐左边缘时线画到了右边缘上，跟实际对齐位置对不上。
        /// </summary>
        private static int GuideX(Cand c, int width)
        {
            if (c.Edge == EdgeStart) return c.Value;
            if (c.Edge == EdgeEnd) return c.Value + width;
            return c.Value + width / 2;
        }

        private static int GuideY(Cand c, int height)
        {
            if (c.Edge == EdgeStart) return c.Value;
            if (c.Edge == EdgeEnd) return c.Value + height;
            return c.Value + height / 2;
        }

        /// <summary>
        /// 参考线在另一轴上的起点。
        ///
        /// 与另一个面板对齐时，线覆盖「我方 + 对方」的并集，一眼能看出跟谁对齐；
        /// 与屏幕对齐时贯穿整屏，更像一条标尺。
        /// </summary>
        private static int SpanFrom(Cand c, int mineFrom, int screenFrom, bool forX)
        {
            if (!c.HasRect) return screenFrom;
            return Math.Min(mineFrom, forX ? c.Rect.Top : c.Rect.Left);
        }

        private static int SpanTo(Cand c, int mineTo, int screenTo, bool forX)
        {
            if (!c.HasRect) return screenTo;
            return Math.Max(mineTo, forX ? c.Rect.Bottom : c.Rect.Right);
        }

        // ============================================================
        //  屏幕边缘保护
        // ============================================================

        /// <summary>
        /// 把面板限制在屏幕内，保证它永远抓得回来。
        ///
        /// 对应参考实现的 "screen-edge protection"：
        /// 至少要留下 <paramref name="keepX"/> 宽的横向可抓区域，
        /// 以及完整的一条标题栏在纵向可见 —— 否则面板被拖出屏幕就没了。
        /// </summary>
        public static Rectangle ClampToScreen(Rectangle r, Rectangle screen, int keepX, int keepY)
        {
            if (keepX > r.Width) keepX = r.Width;
            if (keepY > r.Height) keepY = r.Height;
            if (keepX < 24) keepX = 24;
            if (keepY < 16) keepY = 16;

            int x = r.X;
            int y = r.Y;

            // 右侧：最多让 (Width - keepX) 消失在屏幕右缘之外
            int maxX = screen.Right - keepX;
            // 左侧：最多让 (Width - keepX) 消失在屏幕左缘之外
            int minX = screen.Left - (r.Width - keepX);
            if (x > maxX) x = maxX;
            if (x < minX) x = minX;

            // 纵向不允许把标题栏拖出屏幕
            int maxY = screen.Bottom - keepY;
            int minY = screen.Top;
            if (y > maxY) y = maxY;
            if (y < minY) y = minY;

            return new Rectangle(x, y, r.Width, r.Height);
        }
    }
}
