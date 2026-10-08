using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using DesktopIconManager.Core;

namespace DesktopIconManager.UI
{
    /// <summary>图标项的状态。</summary>
    public enum ItemState
    {
        Normal = 0,
        Hover = 1,
        Selected = 2
    }

    /// <summary>
    /// 分组面板渲染器。
    ///
    /// 视觉构成（自下而上）：
    ///   1. 投影（可选，让面板从壁纸上「浮」起来）
    ///   2. 面板底 —— 半透明填充（alpha 决定透明度）
    ///   3. 内容区 —— 可再叠一层更浅的填充
    ///   4. 标题栏 —— 与面板同色系，靠 1px 分隔线区分层次
    ///   4b. 分类标签行 —— 仅当收纳盒里有多于一个分类时出现
    ///   5. 描边 —— 1px 细线
    ///
    /// 所有尺寸与配色均来自主题调色板，改配色只需换 ThemePalette。
    /// </summary>
    public sealed class GroupPanelRenderer : IDisposable
    {
        /// <summary>标签能显示文字的最小宽度。低于它就只能退化成「当前分类 + n/N」。</summary>
        public const int MinTabWidth = 46;

        private readonly ThemePalette _theme;
        private readonly Font _titleFont;
        private readonly Font _tabFont;
        private readonly Font _labelFont;
        private readonly Font _badgeFont;
        private readonly StringFormat _centerFormat;

        /// <summary>
        /// MeasureString 结果缓存（key=文本）。
        ///
        /// GDI+ 的 MeasureString 很慢，而这里测的都是**反复出现的短串**
        /// （计数徽标 0~999、尺寸提示「数字 × 数字」）—— 拖拽时每帧重测
        /// 纯属浪费。renderer 重建时缓存随实例一起丢弃，不会串字体。
        /// </summary>
        private readonly Dictionary<string, SizeF> _measureCache =
            new Dictionary<string, SizeF>(64);

        // ============================================================
        //  位图缓存（复盘 P1-11）
        // ============================================================

        /// <summary>
        /// 投影位图缓存。
        /// 投影是「8 层递减 alpha 的圆角描边」，每盒每帧 8 次 DrawPath + 抗锯齿；
        /// 面板在拖动过程中尺寸不变，完全可以预渲染一次后贴图。
        /// </summary>
        private readonly Dictionary<string, Bitmap> _shadowCache =
            new Dictionary<string, Bitmap>(16);
        private readonly LinkedList<string> _shadowLru = new LinkedList<string>();
        private const int MaxShadowCache = 12;

        /// <summary>
        /// 图标标签光晕位图缓存。
        /// 光晕是「8 个方向各画一遍文字」，每个标签每帧 8 次 DrawString ——
        /// 光晕只取决于文本与矩形尺寸，同样可以预渲染。
        /// </summary>
        private readonly Dictionary<string, Bitmap> _haloCache =
            new Dictionary<string, Bitmap>(64);
        private readonly LinkedList<string> _haloLru = new LinkedList<string>();
        private const int MaxHaloCache = 64;

        private bool _useShadowCache = true;
        private bool _useHaloCache = true;

        /// <summary>投影位图缓存开关（关闭 = 回到每次直接绘制，观感一致）。</summary>
        public bool UseShadowCache
        {
            get { return _useShadowCache; }
            set
            {
                if (_useShadowCache == value) return;
                _useShadowCache = value;
                ClearShadowCache();
            }
        }

        /// <summary>光晕位图缓存开关（关闭 = 回到每次直接绘制，观感一致）。</summary>
        public bool UseHaloCache
        {
            get { return _useHaloCache; }
            set
            {
                if (_useHaloCache == value) return;
                _useHaloCache = value;
                ClearHaloCache();
            }
        }

        private static void TouchLru(LinkedList<string> lru, string key)
        {
            LinkedListNode<string> first = lru.First;
            if (first != null && string.Equals(first.Value, key, StringComparison.Ordinal))
                return;

            LinkedListNode<string> found = lru.Find(key);
            if (found != null) lru.Remove(found);

            lru.AddFirst(key);
        }

        private void ClearShadowCache()
        {
            foreach (Bitmap b in _shadowCache.Values)
            {
                if (b != null) { try { b.Dispose(); } catch { } }
            }
            _shadowCache.Clear();
            _shadowLru.Clear();
        }

        private void ClearHaloCache()
        {
            foreach (Bitmap b in _haloCache.Values)
            {
                if (b != null) { try { b.Dispose(); } catch { } }
            }
            _haloCache.Clear();
            _haloLru.Clear();
        }

        /// <summary>带缓存的文本测量（限定用 _badgeFont 绘制的短串）。</summary>
        private SizeF MeasureCached(Graphics g, string text)
        {
            SizeF sz;
            if (_measureCache.TryGetValue(text, out sz)) return sz;

            sz = g.MeasureString(text, _badgeFont);

            // 上限防膨胀：徽标与提示串的组合有限，正常到不了
            if (_measureCache.Count < 512) _measureCache[text] = sz;
            return sz;
        }
        private readonly StringFormat _nearFormat;
        private readonly StringFormat _farFormat;
        private readonly StringFormat _labelFormat;

        /// <summary>
        /// 已释放标记。
        ///
        /// GDI+ 对象一旦 Dispose，其 native 句柄即归零，
        /// 再用它绘制会让 GDI+ 内部解引用空指针，抛 NullReferenceException
        /// （堆栈指向 DrawPanel，却看不出哪里为空 —— 就是这个问题）。
        /// 因此释放后必须彻底拒绝绘制。
        /// </summary>
        private bool _disposed;

        public ThemePalette Theme { get { return _theme; } }

        public GroupPanelRenderer(ThemePalette theme)
        {
            if (theme == null) throw new ArgumentNullException("theme");
            _theme = theme;

            _titleFont = new Font(_theme.FontFamily, _theme.TitleFontSize, FontStyle.Bold, GraphicsUnit.Point);
            _tabFont = new Font(_theme.FontFamily, _theme.TabFontSize, FontStyle.Regular, GraphicsUnit.Point);
            _labelFont = new Font(_theme.FontFamily, _theme.LabelFontSize, FontStyle.Regular, GraphicsUnit.Point);
            _badgeFont = new Font(_theme.FontFamily, _theme.BadgeFontSize, FontStyle.Regular, GraphicsUnit.Point);

            _centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            _nearFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            _farFormat = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            _labelFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.LineLimit
            };
        }

        // ============================================================
        //  头部尺寸与标签布局
        // ============================================================

        /// <summary>
        /// 头部（标题栏 + 分类标签行）的总高度。
        ///
        /// 组合成格子组后多出一行标签，内容区要相应下移 ——
        /// 计算容量与条目位置时必须用同一个值，否则图标会压到标签上。
        /// </summary>
        public int HeaderHeight(int tabCount)
        {
            return _theme.TitleHeight + (tabCount > 1 ? _theme.TabBarHeight : 0);
        }

        /// <summary>标签数量多到无法并排显示时，退化为「当前分类 + n/N」单块。</summary>
        public bool TabsFitAsChips(Rectangle panelBounds, int tabCount)
        {
            if (tabCount < 2) return false;
            int available = panelBounds.Width - 4;
            return tabCount * MinTabWidth <= available;
        }

        /// <summary>
        /// 计算每个标签的矩形，用于绘制与命中测试。
        ///
        /// 返回长度等于 tabCount（并排模式），或长度为 1（紧凑模式）。
        /// 未组合（tabCount &lt; 2）时返回 null。
        /// </summary>
        public Rectangle[] GetTabRects(Rectangle panelBounds, int tabCount)
        {
            if (tabCount < 2) return null;

            int rowY = panelBounds.Y + _theme.TitleHeight;
            int rowH = _theme.TabBarHeight;
            int avail = panelBounds.Width - 4;

            if (!TabsFitAsChips(panelBounds, tabCount))
            {
                return new[] { new Rectangle(panelBounds.X + 2, rowY, avail, rowH) };
            }

            // 均分整个标签条宽度 —— 不设上限。
            // 之前被 MaxTabWidth 钳制，盒子较宽时标签挤在左边、
            // 右侧留一截空白，看起来既不均分也不自适应
            int w = avail / tabCount;
            var rects = new Rectangle[tabCount];
            int x = panelBounds.X + 2;

            for (int i = 0; i < tabCount; i++)
            {
                rects[i] = new Rectangle(x, rowY, w, rowH);
                x += w;
            }

            return rects;
        }

        // ============================================================
        //  面板
        // ============================================================

        /// <summary>绘制面板背景（单分类，无标签行）。</summary>
        public void DrawPanel(Graphics g, Rectangle bounds, string title, int itemCount)
        {
            DrawPanel(g, bounds, title, itemCount, null, -1, -1);
        }

        /// <summary>
        /// 绘制面板背景（含投影、填充、标题栏、分类标签行、描边）。
        /// </summary>
        /// <param name="tabTitles">分类标题；null 或长度 &lt; 2 时不画标签行。</param>
        /// <param name="activeTab">当前分类索引。</param>
        /// <param name="hoverTab">鼠标悬停的分类索引，-1 表示无。</param>
        public void DrawPanel(Graphics g, Rectangle bounds, string title, int itemCount,
                              string[] tabTitles, int activeTab, int hoverTab)
        {
            if (_disposed || g == null) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            // title 为 null 时 DrawString 会崩，统一兜底
            if (title == null) title = string.Empty;

            int tabCount = (tabTitles == null) ? 0 : tabTitles.Length;
            int headerH = HeaderHeight(Math.Max(tabCount, 1));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 分层窗口是 ARGB 表面，ClearType 需要不透明背景，
            // 在透明表面上会产生彩色描边 —— 改用灰度抗锯齿
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            var titleRect = new Rectangle(bounds.X, bounds.Y, bounds.Width, _theme.TitleHeight);

            // ---- 1. 投影 ----
            // 主题文件的 shadow 用 9 宫格位图（border="5,3,5,7"），下边最重 ——
            // 这里用多层递减 alpha 的描边近似：越外层越淡，整体向下偏移，
            // 比原来固定 4 层 alpha=10 的写法柔和得多
            if (_theme.DrawShadow && _theme.ShadowDepth > 0)
            {
                int pad = _theme.ShadowDepth + 1;

                if (_useShadowCache)
                {
                    Bitmap shadow = GetShadowBitmap(bounds.Width, bounds.Height);
                    if (shadow != null)
                    {
                        // 1:1 贴图，不缩放 —— 与直接绘制逐像素一致
                        g.DrawImage(shadow, bounds.X - pad, bounds.Y - pad);
                    }
                    else
                    {
                        DrawShadowInto(g, bounds);
                    }
                }
                else
                {
                    DrawShadowInto(g, bounds);
                }
            }

            // ---- 2. 面板底 ----
            using (var brush = new SolidBrush(_theme.PanelFill))
            {
                FillRoundedRect(g, brush, bounds);
            }

            // ---- 3. 内容区 ----
            // 改为「完全透明则不填充」：主题里内容区与面板同色，
            // 多刷一层只会让颜色变浑
            if (_theme.BodyFill.A > 0)
            {
                Rectangle body = new Rectangle(
                    bounds.X + 1,
                    bounds.Y + headerH,
                    bounds.Width - 2,
                    bounds.Height - headerH - 1);

                if (body.Width > 0 && body.Height > 0)
                {
                    using (var brush = new SolidBrush(_theme.BodyFill))
                    {
                        g.FillRectangle(brush, body);
                    }
                }
            }

            // ---- 4. 标题栏 ----
            // 与面板同色时不重复填充，只靠下方分隔线区分层次
            if (_theme.TitleFill.ToArgb() != _theme.PanelFill.ToArgb())
            {
                using (var brush = new SolidBrush(_theme.TitleFill))
                {
                    g.FillRectangle(brush, titleRect);
                }
            }

            // ---- 4c. 标题文字 ----
            Rectangle titleTextRect = new Rectangle(
                titleRect.X + 10, titleRect.Y,
                Math.Max(1, titleRect.Width - 52), titleRect.Height);

            using (var brush = new SolidBrush(_theme.TitleText))
            {
                g.DrawString(title, _titleFont, brush, titleTextRect, _centerFormat);
            }

            // ---- 4d. 计数徽标 ----
            // 主题里绿色只作描边，所以默认不给徽标填充，只用弱化文字色标出数量。
            // 早期版本刷了一块饱和绿底 + 白字，在白色标题栏上非常突兀。
            if (itemCount > 0)
            {
                string badge = itemCount.ToString();
                SizeF sz = MeasureCached(g, badge);
                float bw = Math.Max(14f, sz.Width + 4f);
                float bh = 16f;

                var badgeRect = new RectangleF(
                    titleRect.Right - bw - 8f,
                    titleRect.Y + (titleRect.Height - bh) / 2f,
                    bw, bh);

                if (_theme.BadgeFill.A > 0)
                {
                    using (var brush = new SolidBrush(_theme.BadgeFill))
                    {
                        FillRoundedRect(g, brush,
                            Rectangle.Round(badgeRect), 3);
                    }
                }
                using (var brush = new SolidBrush(_theme.BadgeText))
                {
                    g.DrawString(badge, _badgeFont, brush, badgeRect, _centerFormat);
                }
            }

            // ---- 4e. 分类标签行 ----
            if (tabCount > 1)
            {
                DrawTabBar(g, bounds, tabTitles, activeTab, hoverTab);
            }

            // ---- 4f. 头部与内容区之间的分隔线 ----
            // 这是原程序 panel_line_* 的做法：用一条细线划分标题与内容，
            // 而不是把标题栏刷成色块
            if (_theme.TitleSeparator.A > 0)
            {
                int lineY = bounds.Y + headerH - 1;
                if (lineY > bounds.Y && lineY < bounds.Bottom)
                {
                    using (var pen = new Pen(_theme.TitleSeparator, 1f))
                    {
                        g.DrawLine(pen, bounds.X + 1, lineY, bounds.Right - 2, lineY);
                    }
                }
            }

            // ---- 5. 描边 ----
            using (var pen = new Pen(_theme.PanelBorder, 1f))
            {
                DrawRoundedRect(g, pen, bounds);
            }
        }

        /// <summary>绘制分类标签行。并排放不下时退化为「当前分类 + n/N」。</summary>
        private void DrawTabBar(Graphics g, Rectangle bounds, string[] tabs, int activeTab, int hoverTab)
        {
            Rectangle[] rects = GetTabRects(bounds, tabs.Length);
            if (rects == null) return;

            // 标签条底色：比标题栏略灰的一条，让当前标签的纯白能「浮」出来
            if (_theme.TabBarFill.A > 0)
            {
                var strip = new Rectangle(
                    bounds.X + 1,
                    bounds.Y + _theme.TitleHeight,
                    bounds.Width - 2,
                    _theme.TabBarHeight);
                if (strip.Width > 0 && strip.Height > 0)
                {
                    using (var brush = new SolidBrush(_theme.TabBarFill))
                        g.FillRectangle(brush, strip);
                }
            }

            bool chips = rects.Length == tabs.Length;

            if (!chips)
            {
                // 紧凑模式：只画一个宽条，显示当前分类名与序号
                Rectangle r = rects[0];
                using (var brush = new SolidBrush(_theme.TabActiveFill))
                    g.FillRectangle(brush, r);

                string left = (activeTab >= 0 && activeTab < tabs.Length) ? tabs[activeTab] : "分类";
                string right = (activeTab + 1) + "/" + tabs.Length;

                var leftRect = new Rectangle(r.X + 8, r.Y, Math.Max(1, r.Width - 60), r.Height);
                var rightRect = new Rectangle(r.Right - 52, r.Y, 44, r.Height);

                using (var brush = new SolidBrush(_theme.TabActiveText))
                {
                    g.DrawString(left, _tabFont, brush, leftRect, _nearFormat);
                    g.DrawString(right, _tabFont, brush, rightRect, _farFormat);
                }

                // 主色细线作为点缀，贴合主题的设计规则
                using (var pen = new Pen(_theme.Accent, 2f))
                    g.DrawLine(pen, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);

                return;
            }

            for (int i = 0; i < tabs.Length; i++)
            {
                Rectangle r = rects[i];
                bool active = (i == activeTab);

                Color fill = active
                    ? _theme.TabActiveFill
                    : (i == hoverTab ? _theme.TabHoverFill : _theme.TabInactiveFill);

                if (fill.A > 0)
                {
                    using (var brush = new SolidBrush(fill))
                        FillRoundedRect(g, brush, r, 3);
                }

                string label = tabs[i];
                if (string.IsNullOrEmpty(label)) label = "(未命名)";

                var textRect = new Rectangle(r.X + 4, r.Y, Math.Max(1, r.Width - 8), r.Height);
                using (var brush = new SolidBrush(active ? _theme.TabActiveText : _theme.TabInactiveText))
                {
                    g.DrawString(label, _tabFont, brush, textRect, _centerFormat);
                }

                // 当前标签底部一条主色细线 —— 绿色依旧只作线
                if (active)
                {
                    using (var pen = new Pen(_theme.Accent, 2f))
                        g.DrawLine(pen, r.X + 2, r.Bottom - 1, r.Right - 2, r.Bottom - 1);
                }
            }
        }

        // ============================================================
        //  图标项
        // ============================================================

        /// <summary>绘制一个图标项（图标 + 标签）。</summary>
        public void DrawItem(Graphics g, Rectangle bounds, Icon icon, string label, ItemState state)
        {
            DrawItem(g, bounds, icon, label, state, BoxViewMode.LargeIcons);
        }

        /// <summary>绘制一个图标项（图标 + 标签），图标边长随布局变化。</summary>
        public void DrawItem(Graphics g, Rectangle bounds, Icon icon, string label,
                             ItemState state, BoxViewMode view)
        {
            if (_disposed || g == null) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // 背景状态
            if (state == ItemState.Selected)
            {
                using (var brush = new SolidBrush(_theme.SelectionFill))
                {
                    FillRoundedRect(g, brush, bounds, 4);
                }
            }
            else if (state == ItemState.Hover)
            {
                using (var brush = new SolidBrush(_theme.HoverFill))
                {
                    FillRoundedRect(g, brush, bounds, 4);
                }
            }

            // 图标尺寸随布局变化，但不超过单元格
            int iconSize = (view == BoxViewMode.SmallIcons) ? _theme.SmallIconSize : _theme.IconSize;
            if (iconSize > bounds.Width - 4) iconSize = Math.Max(16, bounds.Width - 4);

            // 图标居中偏上
            int iconTop = bounds.Y + 4;
            var iconRect = new Rectangle(
                bounds.X + (bounds.Width - iconSize) / 2,
                iconTop,
                iconSize,
                iconSize);

            if (icon != null)
            {
                try { g.DrawIcon(icon, iconRect); }
                catch { /* 图标资源异常时跳过 */ }
            }
            else
            {
                // 无图标时画一个占位方块
                using (var brush = new SolidBrush(Color.FromArgb(40, _theme.TextSecondary)))
                {
                    g.FillRectangle(brush, iconRect);
                }
                using (var pen = new Pen(Color.FromArgb(80, _theme.TextSecondary)))
                {
                    g.DrawRectangle(pen, iconRect);
                }
            }

            // 标签（最多两行）
            var labelRect = new Rectangle(
                bounds.X + 2,
                iconRect.Bottom + 3,
                bounds.Width - 4,
                bounds.Bottom - iconRect.Bottom - 4);

            if (labelRect.Height > 0)
            {
                DrawLabel(g, labelRect, label, state);
            }
        }

        /// <summary>
        /// 列表模式的一行：小图标 + 名称，右侧在宽度允许时补一段次要信息。
        ///
        /// 对应参考实现的 list layout —— 文件名长的场景比网格好读得多。
        /// </summary>
        public void DrawListItem(Graphics g, Rectangle bounds, Icon icon,
                                 string label, string detail, ItemState state)
        {
            if (_disposed || g == null) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            if (state == ItemState.Selected)
            {
                using (var brush = new SolidBrush(_theme.SelectionFill))
                    FillRoundedRect(g, brush, bounds, 3);
            }
            else if (state == ItemState.Hover)
            {
                using (var brush = new SolidBrush(_theme.HoverFill))
                    FillRoundedRect(g, brush, bounds, 3);
            }

            int pad = 4;
            int iconSize = Math.Min(_theme.ListIconSize, bounds.Height - 4);
            if (iconSize < 8) iconSize = Math.Max(8, bounds.Height - 2);

            var iconRect = new Rectangle(
                bounds.X + pad,
                bounds.Y + (bounds.Height - iconSize) / 2,
                iconSize, iconSize);

            if (icon != null)
            {
                try { g.DrawIcon(icon, iconRect); }
                catch { }
            }
            else
            {
                using (var brush = new SolidBrush(Color.FromArgb(40, _theme.TextSecondary)))
                    g.FillRectangle(brush, iconRect);
            }

            int textX = iconRect.Right + 6;
            int avail = bounds.Right - textX - pad;
            if (avail <= 8) return;

            bool showDetail = !string.IsNullOrEmpty(detail) && avail > 200;
            int nameW = showDetail ? (int)(avail * 0.52f) : avail;
            int rowY = bounds.Y;

            var nameRect = new Rectangle(textX, rowY, Math.Max(1, nameW), bounds.Height);
            using (var brush = new SolidBrush(_theme.TextPrimary))
            {
                g.DrawString(label, _labelFont, brush, nameRect, _nearFormat);
            }

            if (showDetail)
            {
                var detailRect = new Rectangle(
                    textX + nameW + 4, rowY,
                    Math.Max(1, avail - nameW - 4), bounds.Height);

                using (var brush = new SolidBrush(_theme.TextMuted))
                {
                    g.DrawString(detail, _badgeFont, brush, detailRect, _nearFormat);
                }
            }
        }

        /// <summary>
        /// 绘制标签。桌面背景千变万化，所以先描一圈半透明光晕，
        /// 保证白字在任何壁纸上都能看清。
        /// </summary>
        private void DrawLabel(Graphics g, Rectangle rect, string text, ItemState state)
        {
            if (string.IsNullOrEmpty(text)) return;

            Color fore = _theme.TextPrimary;

            if (_theme.DrawLabelHalo)
            {
                if (_useHaloCache)
                {
                    Bitmap halo = GetHaloBitmap(text, rect.Width, rect.Height);
                    if (halo != null)
                    {
                        // 1:1 贴图（位图比矩形外扩 1px，故偏移 -1）
                        g.DrawImage(halo, rect.X - 1, rect.Y - 1);
                    }
                    else
                    {
                        DrawHaloDirect(g, rect, text);
                    }
                }
                else
                {
                    DrawHaloDirect(g, rect, text);
                }
            }

            using (var brush = new SolidBrush(fore))
            {
                g.DrawString(text, _labelFont, brush, rect, _labelFormat);
            }
        }

        /// <summary>直接画光晕（缓存关闭或缓存生成失败时的路径）。</summary>
        private void DrawHaloDirect(Graphics g, Rectangle rect, string text)
        {
            using (var halo = new SolidBrush(_theme.LabelHalo))
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var r = new Rectangle(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
                        g.DrawString(text, _labelFont, halo, r, _labelFormat);
                    }
                }
            }
        }

        // ============================================================
        //  吸附参考线
        // ============================================================

        /// <summary>
        /// 绘制拖动/缩放时的对齐参考线。
        ///
        /// 注意这里必须是 internal 而非 public —— SnapGuide 是本程序集内部类型，
        /// public 成员暴露 internal 类型会触发 CS0051（可访问性倒挂）。
        /// </summary>
        internal void DrawSnapGuides(Graphics g, IList<SnapGuide> guides, Point origin)
        {
            if (_disposed || g == null) return;
            if (guides == null || guides.Count == 0) return;
            if (_theme.SnapGuide.A == 0) return;

            g.SmoothingMode = SmoothingMode.None;

            using (var pen = new Pen(_theme.SnapGuide, 1f))
            {
                for (int i = 0; i < guides.Count; i++)
                {
                    SnapGuide sg = guides[i];

                    if (sg.Vertical)
                    {
                        int x = sg.Position - origin.X;
                        g.DrawLine(pen, x, sg.From - origin.Y, x, sg.To - origin.Y);
                    }
                    else
                    {
                        int y = sg.Position - origin.Y;
                        g.DrawLine(pen, sg.From - origin.X, y, sg.To - origin.X, y);
                    }
                }
            }
        }

        /// <summary>
        /// 画右下角的缩放手柄（三条 45° 细斜线）。
        ///
        /// 缩放功能本身一直是好的，但之前**完全没有画过任何提示** ——
        /// 用户看不到这个角能拖，就以为不能改尺寸。
        ///
        /// 常态用面板边框色（看得见但不抢眼），鼠标压上去换成主色加粗。
        /// 视觉占用 13×13，与窗体侧的命中区（14×14）对齐，避免
        /// 「看得见却点不到」。
        /// </summary>
        public void DrawResizeGrip(Graphics g, Rectangle panelBounds, bool hot)
        {
            if (_disposed || g == null) return;

            // 折叠后面板很矮，硬画会压到标题文字
            if (panelBounds.Width < 44 || panelBounds.Height < 24) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color color = hot ? _theme.GripHotColor : _theme.GripColor;

            const int inset = 4;
            int[] offsets = { 3, 6, 9 };

            using (var pen = new Pen(color, hot ? 2f : 1f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                for (int i = 0; i < offsets.Length; i++)
                {
                    int off = offsets[i];

                    g.DrawLine(pen,
                        panelBounds.Right - inset - off, panelBounds.Bottom - inset,
                        panelBounds.Right - inset, panelBounds.Bottom - inset - off);
                }
            }
        }

        /// <summary>
        /// 标题栏按钮几何（渲染与命中**共用** —— 两处口径一旦不一致，
        /// 就会出现「画在这、点不动」）。index：0=锁，1=列表切换。
        /// </summary>
        public static Rectangle TitleButtonRect(Rectangle bounds, int titleHeight, int index)
        {
            const int badge = 24;   // 计数徽标占位
            const int gap = 4;
            const int size = 18;

            int y = bounds.Y + Math.Max(0, (titleHeight - 16) / 2);
            int right = bounds.Right - 8 - badge - gap;

            // 从右往左：徽标 → 锁 → 列表
            if (index == 0) return new Rectangle(right - size, y, size, 16);
            return new Rectangle(right - gap - size * 2, y, size, 16);
        }

        /// <summary>
        /// 画标题栏右侧的两个按钮：锁定（挂锁）与视图循环切换（三横线）。
        /// 对应原版收纳盒标题栏的 🔒 / ☰。
        /// 锁定中的锁用主色 —— 绿依旧只作描边/强调。
        /// </summary>
        public void DrawTitleButtons(Graphics g, Rectangle bounds, bool locked)
        {
            if (_disposed || g == null) return;
            if (bounds.Width < 130) return;   // 太窄放不下，硬画会压到标题文字

            g.SmoothingMode = SmoothingMode.AntiAlias;

            for (int i = 0; i < 2; i++)
            {
                Rectangle r = TitleButtonRect(bounds, _theme.TitleHeight, i);
                if (r.IsEmpty) continue;

                Color c = (i == 0 && locked) ? _theme.Accent : _theme.TitleText;

                using (var pen = new Pen(Color.FromArgb(0xB4, c), 1.6f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;

                    if (i == 0)
                    {
                        // 挂锁：锁体 + 锁梁
                        int bw = 8;
                        int bx = r.X + (r.Width - bw) / 2;
                        int by = r.Y + 7;
                        g.DrawRectangle(pen, bx, by, bw, 6);
                        g.DrawArc(pen, bx + 2, by - 5, 4, 6, 180, 180);
                    }
                    else
                    {
                        // 视图切换：三条横线
                        for (int k = 0; k < 3; k++)
                        {
                            int ly = r.Y + 3 + k * 5;
                            g.DrawLine(pen, r.X + 3, ly, r.X + r.Width - 3, ly);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 缩放时在面板右下角显示当前尺寸。
        ///
        /// 吸附是「跳变」，光盯面板边缘不容易察觉到底有没有吸住；
        /// 把尺寸数字显示出来，配合参考线，手感才明确。
        /// </summary>
        public void DrawSizeBadge(Graphics g, Rectangle panelBounds, Size size)
        {
            if (_disposed || g == null) return;
            if (panelBounds.Width <= 0 || panelBounds.Height <= 0) return;

            string text = size.Width + " × " + size.Height;
            SizeF sz = MeasureCached(g, text);

            var rect = new RectangleF(
                panelBounds.Right - sz.Width - 16f,
                panelBounds.Bottom - sz.Height - 10f,
                sz.Width + 12f,
                sz.Height + 4f);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            using (var brush = new SolidBrush(Color.FromArgb(0xD9, 0x1F, 0x1F, 0x1F)))
            {
                FillRoundedRect(g, brush, Rectangle.Round(rect), 3);
            }
            using (var brush = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)))
            {
                g.DrawString(text, _badgeFont, brush, rect, _centerFormat);
            }
        }

        /// <summary>
        /// 高亮「松手就会组合到这里」的目标面板。
        ///
        /// 组合是个破坏性的结构变更，必须先给明确的可视反馈 ——
        /// 之前没有任何提示，拖过去松手就弹确认框，很容易误触。
        /// </summary>
        public void DrawDropTarget(Graphics g, Rectangle panelBounds, int headerHeight, string hint)
        {
            if (_disposed || g == null) return;
            if (panelBounds.Width <= 0 || panelBounds.Height <= 0) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            var header = new Rectangle(
                panelBounds.X, panelBounds.Y, panelBounds.Width,
                Math.Max(1, headerHeight));

            // 头部描一圈主色，标示落点
            using (var brush = new SolidBrush(Color.FromArgb(0x33, _theme.Accent)))
            {
                g.FillRectangle(brush, header);
            }
            using (var pen = new Pen(_theme.Accent, 2f))
            {
                g.DrawRectangle(pen, header.X + 1, header.Y + 1,
                                header.Width - 3, header.Height - 3);
            }

            if (string.IsNullOrEmpty(hint)) return;

            SizeF sz = MeasureCached(g, hint);
            var chip = new RectangleF(
                header.X + (header.Width - sz.Width - 12f) / 2f,
                header.Bottom + 4f,
                sz.Width + 12f,
                sz.Height + 4f);

            using (var brush = new SolidBrush(Color.FromArgb(0xD9, 0x1F, 0x1F, 0x1F)))
            {
                FillRoundedRect(g, brush, Rectangle.Round(chip), 3);
            }
            using (var brush = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)))
            {
                g.DrawString(hint, _badgeFont, brush, chip, _centerFormat);
            }
        }

        // ============================================================
        //  布局计算
        // ============================================================

        /// <summary>某种布局下的单元格尺寸。</summary>
        public Size CellSize(BoxViewMode view)
        {
            switch (view)
            {
                case BoxViewMode.List:
                    // 列表：宽度由面板宽度决定（见 GetItemBounds），这里给一个占位值
                    return new Size(Math.Max(80, _theme.ItemWidth * 3), _theme.ListRowHeight);

                case BoxViewMode.SmallIcons:
                    return new Size(_theme.ItemWidth, _theme.SmallItemHeight);

                default:
                    return new Size(_theme.ItemWidth, _theme.ItemHeight);
            }
        }

        /// <summary>计算面板内容区可容纳的列数与行数（默认布局）。</summary>
        public void MeasureCapacity(Rectangle panelBounds, out int columns, out int rows)
        {
            MeasureCapacity(panelBounds, _theme.TitleHeight, BoxViewMode.LargeIcons, out columns, out rows);
        }

        /// <summary>计算面板内容区可容纳的列数与行数。</summary>
        public void MeasureCapacity(Rectangle panelBounds, int headerHeight,
                                    out int columns, out int rows)
        {
            MeasureCapacity(panelBounds, headerHeight, BoxViewMode.LargeIcons, out columns, out rows);
        }

        /// <summary>计算面板内容区可容纳的列数与行数（按布局模式）。</summary>
        public void MeasureCapacity(Rectangle panelBounds, int headerHeight, BoxViewMode view,
                                    out int columns, out int rows)
        {
            Size cell = CellSize(view);

            int contentW = panelBounds.Width - _theme.BodyPadding * 2;
            int contentH = panelBounds.Height - headerHeight - _theme.BodyPadding * 2;

            if (view == BoxViewMode.List)
            {
                // 列表每行一项，只有一列
                columns = 1;
                rows = Math.Max(1, contentH / cell.Height);
                return;
            }

            columns = Math.Max(1, contentW / cell.Width);
            rows = Math.Max(1, contentH / cell.Height);
        }

        /// <summary>计算第 index 个图标项在面板内的矩形（默认布局）。</summary>
        public Rectangle GetItemBounds(Rectangle panelBounds, int index)
        {
            return GetItemBounds(panelBounds, _theme.TitleHeight, BoxViewMode.LargeIcons, index);
        }

        /// <summary>计算第 index 个图标项在面板内的矩形。</summary>
        public Rectangle GetItemBounds(Rectangle panelBounds, int headerHeight, int index)
        {
            return GetItemBounds(panelBounds, headerHeight, BoxViewMode.LargeIcons, index);
        }

        /// <summary>计算第 index 个图标项在面板内的矩形（按布局模式）。</summary>
        public Rectangle GetItemBounds(Rectangle panelBounds, int headerHeight, BoxViewMode view, int index)
        {
            Size cell = CellSize(view);

            if (view == BoxViewMode.List)
            {
                int listW = panelBounds.Width - _theme.BodyPadding * 2;
                return new Rectangle(
                    panelBounds.X + _theme.BodyPadding,
                    panelBounds.Y + headerHeight + _theme.BodyPadding + index * cell.Height,
                    Math.Max(1, listW),
                    cell.Height);
            }

            int columns, rows;
            MeasureCapacity(panelBounds, headerHeight, view, out columns, out rows);

            int col = index % columns;
            int row = index / columns;

            return new Rectangle(
                panelBounds.X + _theme.BodyPadding + col * cell.Width,
                panelBounds.Y + headerHeight + _theme.BodyPadding + row * cell.Height,
                cell.Width,
                cell.Height);
        }

        /// <summary>
        /// 给定项目数，算出面板需要多大。
        /// 用于在布局前预估尺寸。
        /// </summary>
        public Size MeasurePanelSize(int itemCount, int maxColumns)
        {
            return MeasurePanelSize(itemCount, maxColumns, _theme.TitleHeight, BoxViewMode.LargeIcons);
        }

        /// <summary>给定项目数与头部高度，算出面板需要多大。</summary>
        public Size MeasurePanelSize(int itemCount, int maxColumns, int headerHeight)
        {
            return MeasurePanelSize(itemCount, maxColumns, headerHeight, BoxViewMode.LargeIcons);
        }

        /// <summary>给定项目数、头部高度与布局模式，算出面板需要多大。</summary>
        public Size MeasurePanelSize(int itemCount, int maxColumns, int headerHeight, BoxViewMode view)
        {
            Size cell = CellSize(view);
            if (itemCount < 1) itemCount = 1;

            if (view == BoxViewMode.List)
            {
                // 列表模式宽度交给用户拖拽决定，这里只给出「够读名字」的最小宽度
                int listW = Math.Max(240, _theme.ItemWidth * 3);
                return new Size(listW, itemCount * cell.Height + headerHeight + _theme.BodyPadding * 2);
            }

            if (maxColumns < 1) maxColumns = 1;

            int columns = Math.Min(itemCount, maxColumns);
            if (columns < 1) columns = 1;

            int rows = (itemCount + columns - 1) / columns;
            if (rows < 1) rows = 1;

            return new Size(
                columns * cell.Width + _theme.BodyPadding * 2,
                rows * cell.Height + headerHeight + _theme.BodyPadding * 2);
        }

        // ============================================================
        //  绘制辅助
        // ============================================================

        private void FillRoundedRect(Graphics g, Brush brush, Rectangle r, int radius)
        {
            if (radius <= 0)
            {
                g.FillRectangle(brush, r);
                return;
            }
            using (GraphicsPath path = RoundedPath(r, radius))
            {
                g.FillPath(brush, path);
            }
        }

        private void FillRoundedRect(Graphics g, Brush brush, Rectangle r)
        {
            FillRoundedRect(g, brush, r, _theme.PanelRadius);
        }

        private void DrawRoundedRect(Graphics g, Pen pen, Rectangle r)
        {
            if (_theme.PanelRadius <= 0)
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                return;
            }
            using (GraphicsPath path = RoundedPath(r, _theme.PanelRadius))
            {
                g.DrawPath(pen, path);
            }
        }

        /// <summary>
        /// 把投影画进 g。bounds 是面板矩形（不是投影外扩后的矩形）。
        /// 抽出来是为了「直接画」与「预渲染到缓存位图」两条路径共用同一段代码，
        /// 保证缓存开关切换时观感完全一致。
        /// </summary>
        private void DrawShadowInto(Graphics g, Rectangle bounds)
        {
            if (_disposed || g == null) return;

            int depth = _theme.ShadowDepth;
            int offY = _theme.ShadowOffsetY;
            if (depth <= 0) return;

            SmoothingMode oldSmooth = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            try
            {
                for (int i = depth; i >= 1; i--)
                {
                    // 二次衰减：靠内浓、靠外淡
                    int a = 26 * i * i / (depth * depth);
                    if (a <= 0) continue;
                    if (a > 40) a = 40;

                    Rectangle shadow = new Rectangle(
                        bounds.X - i,
                        bounds.Y - i + offY,
                        bounds.Width + i * 2,
                        bounds.Height + i * 2);

                    using (var pen = new Pen(Color.FromArgb(a, 0, 0, 0), 1f))
                    {
                        DrawRoundedRect(g, pen, shadow);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = oldSmooth;
            }
        }

        /// <summary>
        /// 取（或生成）某尺寸的投影位图。
        /// 位图比面板大一圈（pad = depth+1），绘制时左上角对齐 bounds - pad。
        /// 生成失败返回 null —— 调用方会退回直接绘制，功能不受影响。
        /// </summary>
        private Bitmap GetShadowBitmap(int w, int h)
        {
            if (_disposed || w <= 0 || h <= 0) return null;
            if (w > 4000 || h > 4000) return null;      // 异常尺寸不预渲染，避免吃掉大内存

            string key = w + "x" + h;

            Bitmap cached;
            if (_shadowCache.TryGetValue(key, out cached))
            {
                TouchLru(_shadowLru, key);
                return cached;
            }

            Bitmap bmp = null;
            try
            {
                int depth = _theme.ShadowDepth;
                int offY = _theme.ShadowOffsetY;
                int pad = depth + 1;

                int bw = w + pad * 2;
                int bh = h + pad * 2 + offY;
                if (bw <= 0 || bh <= 0) return null;

                bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);

                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // 位图默认是全透明，正好当作干净画布
                    DrawShadowInto(g, new Rectangle(pad, pad, w, h));
                }

                _shadowCache[key] = bmp;
                TouchLru(_shadowLru, key);

                while (_shadowCache.Count > MaxShadowCache && _shadowLru.Count > 0)
                {
                    LinkedListNode<string> last = _shadowLru.Last;
                    if (last == null) break;

                    _shadowLru.RemoveLast();

                    Bitmap victim;
                    if (_shadowCache.TryGetValue(last.Value, out victim))
                    {
                        _shadowCache.Remove(last.Value);
                        if (victim != null) { try { victim.Dispose(); } catch { } }
                    }
                }

                return bmp;
            }
            catch
            {
                if (bmp != null) { try { bmp.Dispose(); } catch { } }
                return null;
            }
        }

        /// <summary>
        /// 取（或生成）标签光晕位图。
        /// 内容 = 8 个方向各画一遍文字；绘制时按 1:1 贴到标签矩形上。
        /// </summary>
        private Bitmap GetHaloBitmap(string text, int w, int h)
        {
            if (_disposed || string.IsNullOrEmpty(text)) return null;
            if (w <= 0 || h <= 0 || w > 1024 || h > 256) return null;

            string key = text + "|" + w + "x" + h;

            Bitmap cached;
            if (_haloCache.TryGetValue(key, out cached))
            {
                TouchLru(_haloLru, key);
                return cached;
            }

            Bitmap bmp = null;
            try
            {
                // 上下左右各留 1px：8 向偏移里 +1 那一笔会画到矩形外 1 像素，
                // 不留边就会被裁掉，和直接绘制差 1px
                bmp = new Bitmap(w + 2, h + 2, PixelFormat.Format32bppArgb);

                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;

                    using (var halo = new SolidBrush(_theme.LabelHalo))
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                var r = new Rectangle(1 + dx, 1 + dy, w, h);
                                g.DrawString(text, _labelFont, halo, r, _labelFormat);
                            }
                        }
                    }
                }

                _haloCache[key] = bmp;
                TouchLru(_haloLru, key);

                while (_haloCache.Count > MaxHaloCache && _haloLru.Count > 0)
                {
                    LinkedListNode<string> last = _haloLru.Last;
                    if (last == null) break;

                    _haloLru.RemoveLast();

                    Bitmap victim;
                    if (_haloCache.TryGetValue(last.Value, out victim))
                    {
                        _haloCache.Remove(last.Value);
                        if (victim != null) { try { victim.Dispose(); } catch { } }
                    }
                }

                return bmp;
            }
            catch
            {
                if (bmp != null) { try { bmp.Dispose(); } catch { } }
                return null;
            }
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();

            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }

        /// <summary>把长名字截断为适合显示的长度。</summary>
        public static string Ellipsize(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (text.Length <= maxChars) return text;
            return text.Substring(0, Math.Max(1, maxChars - 1)) + "…";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ClearShadowCache();
            ClearHaloCache();

            _titleFont.Dispose();
            _tabFont.Dispose();
            _labelFont.Dispose();
            _badgeFont.Dispose();
            _centerFormat.Dispose();
            _nearFormat.Dispose();
            _farFormat.Dispose();
            _labelFormat.Dispose();
        }
    }
}
