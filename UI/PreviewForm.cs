using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DesktopIconManager.Core;

namespace DesktopIconManager.UI
{
    /// <summary>一个分组的数据。</summary>
    internal sealed class GroupData
    {
        public string Title;
        public IconCategory Category;
        public List<DesktopIcon> Items = new List<DesktopIcon>();
    }

    /// <summary>
    /// 样式预览窗体。
    ///
    /// 做法：先截取当前桌面作为背景，再把分组面板叠加渲染在上面。
    /// 好处是——
    ///   · 不需要接管桌面图层，零风险，不碰你任何图标
    ///   · 又能在真实壁纸上看清半透明面板的实际观感
    ///
    /// 看到满意后，同样的渲染器可直接复用到实时覆盖层。
    ///
    /// 操作：ESC 或单击任意处退出。
    /// </summary>
    internal sealed class PreviewForm : Form
    {
        private readonly ThemePalette _theme;
        private readonly Bitmap _background;
        private readonly GroupPanelRenderer _renderer;
        private readonly IconExtractor _iconExtractor;
        private readonly List<GroupData> _groups;
        private readonly List<PanelPlacement> _placements = new List<PanelPlacement>();

        private int _hoverIndex = -1;
        private Point _mouse = new Point(-1, -1);

        private sealed class PanelPlacement
        {
            public Rectangle Bounds;
            public GroupData Group;
        }

        private static readonly IconCategory[] Order =
        {
            IconCategory.Folder, IconCategory.Document, IconCategory.Archive,
            IconCategory.Image, IconCategory.Video, IconCategory.Audio,
            IconCategory.Executable, IconCategory.Script, IconCategory.Shortcut,
            IconCategory.WebLink, IconCategory.Other
        };

        public PreviewForm(ThemePalette theme)
        {
            _theme = theme;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.Black;

            // 覆盖整个虚拟桌面
            Rectangle vs = SystemInformation.VirtualScreen;
            Bounds = vs;

            _background = CaptureDesktop(vs);
            _renderer = new GroupPanelRenderer(_theme);
            _iconExtractor = new IconExtractor();

            _groups = BuildGroups();
            LayoutPanels();
        }

        /// <summary>截取桌面作为背景。</summary>
        private static Bitmap CaptureDesktop(Rectangle bounds)
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        /// <summary>读取桌面图标并按分类分组。</summary>
        private static List<GroupData> BuildGroups()
        {
            var classifier = new Classifier();
            var reader = new DesktopIconReader(classifier);

            var groups = new List<GroupData>();

            IList<DesktopIcon> icons;
            try { icons = reader.ReadIcons(); }
            catch { icons = new List<DesktopIcon>(); }

            foreach (IconCategory cat in Order)
            {
                var items = icons
                    .Where(i => i.Category == cat)
                    .OrderBy(i => i.Name, StringComparer.CurrentCulture)
                    .ToList();

                if (items.Count == 0) continue;

                groups.Add(new GroupData
                {
                    Title = Classifier.GetDisplayName(cat),
                    Category = cat,
                    Items = items
                });
            }

            return groups;
        }

        /// <summary>
        /// 摆放面板：从左到右排列，超出宽度换行。
        /// 面板宽度按项目数自适应，但有上限，避免单个面板过宽。
        /// </summary>
        private void LayoutPanels()
        {
            _placements.Clear();

            const int margin = 24;
            // 用已捕获的背景尺寸，而不是 ClientSize —— 构造阶段 ClientSize 未必已生效
            int canvasW = _background != null ? _background.Width : SystemInformation.VirtualScreen.Width;

            int maxPanelWidth = Math.Max(300, (canvasW - margin * 3) / 2);
            int maxColumns = Math.Max(1, (maxPanelWidth - _theme.BodyPadding * 2) / _theme.ItemWidth);

            int x = margin;
            int y = margin;
            int rowHeight = 0;

            foreach (GroupData g in _groups)
            {
                Size size = _renderer.MeasurePanelSize(g.Items.Count, maxColumns);

                if (x + size.Width + margin > canvasW && x > margin)
                {
                    x = margin;
                    y += rowHeight + _theme.PanelGap;
                    rowHeight = 0;
                }

                _placements.Add(new PanelPlacement
                {
                    Bounds = new Rectangle(x, y, size.Width, size.Height),
                    Group = g
                });

                x += size.Width + _theme.PanelGap;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
        }

        // ============================================================
        //  绘制
        // ============================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // 背景 = 桌面截图
            if (_background != null)
            {
                g.DrawImageUnscaled(_background, 0, 0);
            }
            else
            {
                g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
            }

            // 分组面板
            foreach (PanelPlacement p in _placements)
            {
                _renderer.DrawPanel(g, p.Bounds, p.Group.Title, p.Group.Items.Count);

                for (int i = 0; i < p.Group.Items.Count; i++)
                {
                    Rectangle itemRect = _renderer.GetItemBounds(p.Bounds, i);
                    DesktopIcon icon = p.Group.Items[i];

                    Icon shellIcon = null;
                    try { shellIcon = _iconExtractor.GetIcon(icon.FullPath, icon.IsDirectory); }
                    catch { }

                    ItemState state = ItemState.Normal;
                    if (_hoverIndex >= 0 && _hoverIndex == GlobalIndex(p, i))
                        state = ItemState.Hover;

                    _renderer.DrawItem(g, itemRect, shellIcon,
                        GroupPanelRenderer.Ellipsize(icon.Name, 14), state);
                }
            }

            DrawHud(g);
        }

        private int GlobalIndex(PanelPlacement p, int localIndex)
        {
            int baseIndex = 0;
            foreach (PanelPlacement other in _placements)
            {
                if (other == p) break;
                baseIndex += other.Group.Items.Count;
            }
            return baseIndex + localIndex;
        }

        /// <summary>右下角提示条。</summary>
        private void DrawHud(Graphics g)
        {
            string tip = string.Format(
                "样式预览 · {0} 主题 · {1} 个分组 · {2} 个图标    按 ESC 或单击退出",
                _themeName, _placements.Count,
                _placements.Sum(p => p.Group.Items.Count));

            g.SmoothingMode = SmoothingMode.AntiAlias;

            int canvasW = _background != null ? _background.Width : Width;
            int canvasH = _background != null ? _background.Height : Height;

            using (var font = new Font(_theme.FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point))
            {
                SizeF sz = g.MeasureString(tip, font);
                var rect = new RectangleF(
                    canvasW - sz.Width - 34,
                    canvasH - sz.Height - 26,
                    sz.Width + 20, sz.Height + 10);

                using (var brush = new SolidBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)))
                {
                    g.FillRectangle(brush, rect);
                }
                using (var brush = new SolidBrush(Color.White))
                {
                    g.DrawString(tip, font, brush, rect.X + 10, rect.Y + 5);
                }
            }
        }

        private string _themeName = "light";

        public void SetThemeName(string name)
        {
            _themeName = name;
        }

        // ============================================================
        //  交互
        // ============================================================

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.Location;

            int hit = -1;
            foreach (PanelPlacement p in _placements)
            {
                if (!p.Bounds.Contains(e.Location)) continue;

                for (int i = 0; i < p.Group.Items.Count; i++)
                {
                    if (_renderer.GetItemBounds(p.Bounds, i).Contains(e.Location))
                    {
                        hit = GlobalIndex(p, i);
                        break;
                    }
                }
                break;
            }

            if (hit != _hoverIndex)
            {
                _hoverIndex = hit;
                Invalidate();
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            Close();
            base.OnMouseClick(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_renderer != null) _renderer.Dispose();
                if (_iconExtractor != null) _iconExtractor.Dispose();
                if (_background != null) _background.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}