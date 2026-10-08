using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DesktopIconManager.Core;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 桌面覆盖层窗体 —— 复刻「隐藏系统图标 + 自绘分组面板」的实现方式。
    ///
    /// 工作流程：
    ///   1. 隐藏系统桌面图标（DesktopToggler）
    ///   2. 建立一个逐像素 alpha 的分层窗口铺满屏幕
    ///   3. 用 GDI+ 把分组面板与图标画进 32bpp 位图
    ///   4. 预乘 alpha 后经 UpdateLayeredWindow 提交
    ///
    /// 交互：
    ///   · 拖动标题栏 → 移动收纳盒（与其它盒 / 屏幕边缘自动吸附对齐）
    ///   · 拖右下角 → 缩放（同样吸附）
    ///   · 把一个盒拖到另一个上 → 组合成格子组（分类变成标签页）
    ///   · 点标签 / 滚轮 / Ctrl+Tab → 切换分类
    ///   · 拖动图标 → 移到别的收纳盒；拖到图标上 → 建新盒
    ///   · 双击标题栏 → 折叠/展开
    ///   · 右键 → 菜单（布局 / 新建分类 / 拆分 / 组合 / 重命名 / 解散）
    ///   · ESC 退出 · Ctrl+S 保存
    ///
    /// 安全：任何退出路径（正常关闭、异常、进程结束）都会恢复系统桌面图标。
    /// </summary>
    internal sealed class DesktopOverlayForm : Form
    {
        private enum DragMode { None, Panel, Item, Resize }

        /// <summary>
        /// 右下角缩放手柄的命中边长。
        /// 与 GroupPanelRenderer.DrawResizeGrip 画出来的 13×13 视觉区对齐 ——
        /// 命中区略大于视觉区一点点（14 vs 13），手感更「好点」。
        /// </summary>
        private const int ResizeGrip = 14;

        private ThemePalette _theme;
        private GroupPanelRenderer _renderer;
        private readonly IconExtractor _icons;
        private readonly GroupLayout _layout;
        private readonly string _layoutPath;

        private DragMode _drag = DragMode.None;
        private int _dragGroup = -1;
        private int _dragItem = -1;
        private Point _dragStart;
        private Rectangle _dragOriginRect;

        private readonly SnapEngine _snap = new SnapEngine();

        private int _hoverGroup = -1;
        private int _hoverItem = -1;
        private int _hoverTab = -1;                 // 悬停的分类标签，-1 表示无
        private int _hoverGrip = -1;                // 鼠标压在哪个盒的缩放手柄上，-1 表示无
        /// <summary>拖着面板时，落点会触发组合的目标盒；-1 表示无。</summary>
        private int _combineTarget = -1;
        private readonly List<SnapGuide> _dragGuides = new List<SnapGuide>();

        private bool _dirty;
        private bool _closing;
        private bool _panelsVisible = true;
        private bool _locked;            // 图标锁定：锁定后禁止拖动，防误操作
        private bool _autoFit = true;    // 改动后自动收紧面板尺寸（自动补位）

        // ---- 渲染链路复用（复盘 P0-1 / P1-8 / P1-9）----
        /// <summary>预乘用的临时字节缓冲。复用，尺寸不变不重建。</summary>
        private byte[] _blitBuffer;
        /// <summary>复用的 GDI 内存 DC / DIBSection。每帧建销会有大量内核态往返。</summary>
        private IntPtr _blitMemDc = IntPtr.Zero;
        private IntPtr _blitBmp = IntPtr.Zero;
        private IntPtr _blitBits = IntPtr.Zero;
        private IntPtr _blitOld = IntPtr.Zero;
        private int _blitW;
        private int _blitH;
        /// <summary>缓冲区刚重建 → 内容不可信，必须整屏重绘一次。</summary>
        private bool _bufferDirty = true;
        /// <summary>首帧是否已成功提交（用于合并 OnHandleCreated / OnShown 的重复渲染）。</summary>
        private bool _firstRenderDone;

        // ---- 脏矩形（P1-8）----
        private readonly List<SnapGuide> _lastGuides = new List<SnapGuide>();
        private int _lastCombineTarget = -1;
        private Rectangle _pendingDirty;
        private bool _hoverPending;
        private Timer _hoverTimer;
        private Timer _saveTimer;

        public DesktopOverlayForm(ThemePalette theme, GroupLayout layout, string layoutPath)
        {
            _theme = theme;
            _layout = layout;
            _layoutPath = layoutPath;

            _renderer = new GroupPanelRenderer(theme);
            _icons = new IconExtractor();

            // 从设置初始化交互行为（设置窗口改完会通过 ApplySettings 再刷一遍）
            AppSettings cfg = AppSettings.Current;
            _autoFit = cfg.AutoFit;
            _locked = cfg.LockIcons;
            _snap.Enabled = cfg.SnapEnabled;
            _snap.Gap = cfg.SnapGap;
            _snap.Threshold = cfg.SnapThreshold;

            // 性能开关（都可在设置文件里关掉，随时回退到保守路径）
            _icons.MaxCache = cfg.IconCacheLimit;
            _renderer.UseShadowCache = cfg.PerfCacheShadow;
            _renderer.UseHaloCache = cfg.PerfCacheLabelHalo;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = false;
            BackColor = Color.Black;

            Rectangle vs = SystemInformation.VirtualScreen;
            Bounds = vs;
            TopMost = false;

            // 无边框窗体默认不参与焦点，导致收不到 WM_MOUSEWHEEL。
            // 显式声明可选中，这样鼠标按下后能拿到焦点，滚轮切换分类才有效。
            SetStyle(ControlStyles.Selectable, true);

            // AllowDrop 不在这里设。
            // 句柄创建前设置的话，WinForms 会在 OnHandleCreated 内部调用
            // OLE 初始化，那时抛异常无法被我们的代码捕获（会直接弹崩溃框）。
            // 改到 OnHandleCreated 里设置，可以捕获并降级。
        }

        /// <summary>启用 WS_EX_LAYERED（逐像素 alpha）与 WS_EX_TOOLWINDOW（不进任务栏）。</summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        // ============================================================
        //  生命周期
        // ============================================================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 启用外部拖放。必须放在 base 之后并捕获异常：
            // OLE 初始化依赖 STA 线程，若线程单元模式不对会抛异常。
            // 这里降级为「不支持外部拖入」，不影响其余功能。
            try
            {
                AllowDrop = true;
            }
            catch (Exception ex)
            {
                WriteLog("启用拖放失败，外部拖入功能不可用: " + ex.Message);
            }

            // 悬停重绘节流定时器（P1-12）。只悬停时启用，拖拽不走这里。
            try
            {
                int ms = AppSettings.Current.HoverThrottleMs;
                if (ms > 0)
                {
                    _hoverTimer = new Timer();
                    _hoverTimer.Interval = Math.Max(1, ms);
                    _hoverTimer.Tick += OnHoverTimerTick;
                }
            }
            catch
            {
                _hoverTimer = null;
            }

            // 布局落盘去抖定时器（P2-18）。间隔按设置，0 = 关闭去抖。
            try
            {
                _saveTimer = new Timer();
                _saveTimer.Tick += OnSaveTimerTick;
            }
            catch
            {
                _saveTimer = null;
            }

            // 接管桌面：先隐藏系统图标，避免与自绘图标重叠
            DesktopToggler.HideIcons();

            Render();
        }

        /// <summary>统一走 AppLog（带级别、轮转、环境头）。</summary>
        private static void WriteLog(string text)
        {
            AppLog.Warn(text);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 冷启动省一整帧：OnHandleCreated 已经画过一次全屏
            // （1080p 约 8MB 分配 + 207 万像素预乘，10~20ms）。
            // 只有那次因为尺寸还没定而被跳过时，才在这里补一帧 ——
            // 这样既省了一帧，又不会退化成"首屏空白"。
            if (!_firstRenderDone) Render();
        }

        private void OnHoverTimerTick(object sender, EventArgs e)
        {
            if (_hoverTimer != null) _hoverTimer.Stop();
            if (!_hoverPending) return;

            _hoverPending = false;
            Rectangle dirty = _pendingDirty;
            _pendingDirty = Rectangle.Empty;

            if (dirty.IsEmpty) Render();
            else RenderRegion(dirty);
        }

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            if (_saveTimer != null) _saveTimer.Stop();
            SaveIfDirty();
        }

        /// <summary>标记布局已改动，并按去抖设置安排落盘。</summary>
        private void MarkDirty()
        {
            _dirty = true;
            if (_saveTimer == null) return;

            int ms = 0;
            try { ms = AppSettings.Current.SaveDebounceMs; }
            catch { }

            if (ms <= 0) return;

            try
            {
                _saveTimer.Stop();
                _saveTimer.Interval = ms;
                _saveTimer.Start();
            }
            catch
            {
                // 定时器起不来不影响功能，退出时仍会落盘
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 分层窗口的内容完全由 UpdateLayeredWindow 决定，这里不做任何绘制
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 同上，避免与分层内容冲突
        }

        protected override void WndProc(ref Message m)
        {
            // Shell 原生菜单带自绘项，会向宿主窗口发 WM_DRAWITEM / WM_MEASUREITEM。
            // 必须先转交给 IContextMenu2/3，否则那些菜单项会渲染成空白。
            if (ShellContextMenu.TryHandleMessage(ref m)) return;

            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _closing = true;

            // 退出前必须把待落盘的改动写掉 —— 去抖定时器可能还有未触发的一轮
            if (_saveTimer != null)
            {
                try { _saveTimer.Stop(); }
                catch { }
            }
            SaveIfDirty();

            // 关键安全步骤：无论如何都要还回系统桌面图标
            try { DesktopToggler.ShowIcons(); }
            catch { }

            ReleaseBlitResources();

            if (_backBuffer != null)
            {
                _backBuffer.Dispose();
                _backBuffer = null;
            }

            base.OnFormClosing(e);
        }

        private void SaveIfDirty()
        {
            if (!_dirty) return;
            try
            {
                _layout.Save(_layoutPath);
                _dirty = false;
            }
            catch
            {
                // 保存失败不影响退出流程
            }
        }

        // ============================================================
        //  对外控制（供托盘调用）
        // ============================================================

        /// <summary>面板当前是否可见。</summary>
        public bool PanelsVisible { get { return _panelsVisible; } }

        /// <summary>
        /// 显示 / 隐藏分组面板。
        ///
        /// 隐藏时会把系统桌面图标还回去 —— 因为面板一藏，
        /// 若不还原图标就会剩下一片空白桌面。
        /// </summary>
        public void TogglePanels()
        {
            _panelsVisible = !_panelsVisible;

            if (_panelsVisible)
            {
                DesktopToggler.HideIcons();
            }
            else
            {
                DesktopToggler.ShowIcons();
            }

            Render();
        }

        /// <summary>
        /// 处理 explorer 重启。
        ///
        /// 新的 explorer 会把系统图标重新显示出来，所以这里要
        /// 重新隐藏，并重绘覆盖层。
        /// </summary>
        public void HandleExplorerRestart()
        {
            if (_closing) return;

            if (_panelsVisible)
            {
                DesktopToggler.HideIcons();
            }

            Render();
        }

        /// <summary>立即保存布局（供托盘菜单调用）。</summary>
        public void SaveLayoutNow()
        {
            MarkDirty();   // 强制落盘一次，避免用户以为点了没反应
            SaveIfDirty();
        }

        /// <summary>按文件类型重新分组（供托盘菜单调用）。</summary>
        public void RequestRelayout()
        {
            RebuildFromDesktop();
        }

        /// <summary>图标是否被锁定（锁定后禁止拖动，防止误操作）。</summary>
        public bool Locked
        {
            get { return _locked; }
            set
            {
                if (_locked == value) return;
                _locked = value;
                Render();
            }
        }

        /// <summary>拖动/缩放时的吸附对齐开关。</summary>
        public bool SnapEnabled
        {
            get { return _snap.Enabled; }
            set { _snap.Enabled = value; }
        }

        // ============================================================
        //  应用设置（设置窗口边改边调，所见即所得）
        // ============================================================

        /// <summary>
        /// 应用一份设置。
        ///
        /// 主题变了就**重建渲染器** —— GroupPanelRenderer 的字体是按主题字号建的，
        /// 复用旧对象会导致字号跟不上（这是最容易漏的一处）。
        /// </summary>
        public void ApplySettings(AppSettings s)
        {
            if (s == null) return;

            ThemePalette next = ThemeBinder.Build(s);

            bool sizesChanged = !SameSizes(_theme, next);
            bool lookChanged = sizesChanged || !SameLook(_theme, next);

            if (lookChanged)
            {
                if (_renderer != null) _renderer.Dispose();
                _theme = next;
                _renderer = new GroupPanelRenderer(_theme);
            }

            _snap.Enabled = s.SnapEnabled;
            _snap.Gap = s.SnapGap;
            _snap.Threshold = s.SnapThreshold;
            _autoFit = s.AutoFit;

            // 节流/去抖间隔随设置刷新；图标缓存上限同理
            if (_hoverTimer != null)
            {
                int ms = s.HoverThrottleMs;
                _hoverTimer.Interval = Math.Max(1, ms > 0 ? ms : 1);
            }
            if (_icons != null) _icons.MaxCache = s.IconCacheLimit;
            if (_renderer != null) _renderer.UseShadowCache = s.PerfCacheShadow;
            if (_renderer != null) _renderer.UseHaloCache = s.PerfCacheLabelHalo;

            // 尺寸变了要把现有面板重新收紧，否则图标会溢出或被裁
            if (sizesChanged) ResizeAllToContent();

            MarkDirty();
            Render();
        }

        /// <summary>把全部面板收到刚好容纳内容（忽略 autoFit 开关）。</summary>
        private void ResizeAllToContent()
        {
            bool saved = _autoFit;
            _autoFit = true;

            for (int i = 0; i < _layout.Groups.Count; i++)
                AutoFitPanel(i);

            _autoFit = saved;
        }

        /// <summary>只比较影响排版的尺寸，用于判断要不要重新收紧面板。</summary>
        private static bool SameSizes(ThemePalette a, ThemePalette b)
        {
            if (a == null || b == null) return false;

            return a.TitleHeight == b.TitleHeight
                && a.TabBarHeight == b.TabBarHeight
                && a.IconSize == b.IconSize
                && a.ItemWidth == b.ItemWidth
                && a.ItemHeight == b.ItemHeight
                && a.SmallIconSize == b.SmallIconSize
                && a.SmallItemHeight == b.SmallItemHeight
                && a.ListRowHeight == b.ListRowHeight
                && a.ListIconSize == b.ListIconSize
                && a.BodyPadding == b.BodyPadding;
        }

        /// <summary>比较配色与字体，用于判断要不要重建渲染器。</summary>
        private static bool SameLook(ThemePalette a, ThemePalette b)
        {
            if (a == null || b == null) return false;

            return a.PanelFill.ToArgb() == b.PanelFill.ToArgb()
                && a.PanelBorder.ToArgb() == b.PanelBorder.ToArgb()
                && a.TitleFill.ToArgb() == b.TitleFill.ToArgb()
                && a.TitleText.ToArgb() == b.TitleText.ToArgb()
                && a.TitleSeparator.ToArgb() == b.TitleSeparator.ToArgb()
                && a.TabBarFill.ToArgb() == b.TabBarFill.ToArgb()
                && a.PanelRadius == b.PanelRadius
                && a.ShadowDepth == b.ShadowDepth
                && a.ShadowOffsetY == b.ShadowOffsetY
                && a.TitleFontSize == b.TitleFontSize
                && a.LabelFontSize == b.LabelFontSize;
        }

        // ============================================================
        //  兜底：把没进任何收纳盒的图标收起来
        // ============================================================

        /// <summary>
        /// 把「在桌面上、但不在任何收纳盒里」的图标收进一个兜底盒。
        ///
        /// 为什么必须有：接管桌面后系统图标是隐藏的，某个图标若不在任何面板里，
        /// 用户就**既看不到也点不到**它 —— 这比多出一个面板糟糕得多。
        ///
        /// 现实触发场景：
        ///   · 后来新放到桌面的文件
        ///   · 公用桌面读取修正后多出来的图标
        ///   · 布局文件是从别处拷来的
        /// </summary>
        /// <returns>收进来的图标数量</returns>
        public int AdoptOrphanIcons()
        {
            if (!AppSettings.Current.AdoptOrphanIcons) return 0;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FenceBox b in _layout.Groups)
            {
                foreach (FenceTab t in b.Tabs)
                {
                    foreach (GroupItem it in t.Items)
                    {
                        if (!string.IsNullOrEmpty(it.FullPath)) known.Add(it.FullPath);
                    }
                }
            }

            IList<DesktopIcon> icons;
            try { icons = new DesktopIconReader(new Classifier()).ReadIcons(); }
            catch { return 0; }

            var orphans = new List<DesktopIcon>();
            foreach (DesktopIcon ic in icons)
            {
                if (string.IsNullOrEmpty(ic.FullPath)) continue;
                if (known.Contains(ic.FullPath)) continue;
                orphans.Add(ic);
            }

            if (orphans.Count == 0) return 0;

            orphans.Sort(delegate (DesktopIcon a, DesktopIcon b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });

            var box = new FenceBox();
            box.Title = "未收纳";
            foreach (DesktopIcon ic in orphans)
                box.Items.Add(new GroupItem(ic.Name, ic.FullPath, ic.IsDirectory));

            int maxCols = AppSettings.Current.MaxColumns;
            if (maxCols < 1) maxCols = 5;

            Size sz = _renderer.MeasurePanelSize(
                orphans.Count, maxCols, _renderer.HeaderHeight(1), BoxViewMode.LargeIcons);

            Rectangle screen = SystemInformation.VirtualScreen;

            // 放左下角 —— 那里通常是空的，不会压住用户已有的排列
            int y = screen.Bottom - sz.Height - 24;
            if (y < 24) y = 24;

            box.Bounds = new Rectangle(24, y, sz.Width, sz.Height);
            _layout.Groups.Add(box);
            _layout.ResolveOverlaps(screen.Width);

            MarkDirty();
            return orphans.Count;
        }

        // ============================================================
        //  分类（标签页）辅助
        // ============================================================

        /// <summary>
        /// 头部高度 —— 标题栏，若已组合成格子组则再加一行分类标签。
        ///
        /// 计算容量、条目位置、命中测试三处必须用同一个值，
        /// 否则图标会压到标签行上。
        /// </summary>
        private int HeaderHeightOf(FenceBox box)
        {
            return _renderer.HeaderHeight(box.Tabs.Count);
        }

        /// <summary>某个条目在窗体坐标下的矩形。</summary>
        private Rectangle ItemBoundsOf(int groupIndex, int itemIndex)
        {
            FenceBox box = _layout.Groups[groupIndex];
            return _renderer.GetItemBounds(
                ToLocal(box.Bounds), HeaderHeightOf(box), box.ViewMode, itemIndex);
        }

        /// <summary>收集分类标题，供渲染使用；未组合时返回 null。</summary>
        private static string[] TabTitlesOf(FenceBox box)
        {
            if (box == null || box.Tabs.Count < 2) return null;

            var a = new string[box.Tabs.Count];
            for (int i = 0; i < box.Tabs.Count; i++)
            {
                a[i] = box.Tabs[i].Title;
                if (string.IsNullOrEmpty(a[i])) a[i] = "(未命名)";
            }
            return a;
        }

        /// <summary>把所有面板收紧到刚好容纳内容（自动补位）。</summary>
        public void TidyAll()
        {
            for (int i = 0; i < _layout.Groups.Count; i++)
                AutoFitPanel(i);

            MarkDirty();
            Render();
        }

        /// <summary>
        /// 面板尺寸自适应内容。
        /// 条目增删后调用，避免留下大片空白 —— 对应原程序的 auto_vacuum。
        /// </summary>
        private void AutoFitPanel(int groupIndex)
        {
            if (!_autoFit) return;
            if (groupIndex < 0 || groupIndex >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[groupIndex];
            int count = Math.Max(1, box.Items.Count);
            Size cell = _renderer.CellSize(box.ViewMode);

            int w, h;

            if (box.ViewMode == BoxViewMode.List)
            {
                // 列表模式：宽度由用户拖拽决定（只保一个最小可读宽度），仅收紧高度
                w = Math.Max(_theme.ItemWidth * 3, box.Bounds.Width);
                h = count * cell.Height + HeaderHeightOf(box) + _theme.BodyPadding * 2;
            }
            else
            {
                // 网格模式：沿用当前宽度能容纳的列数，只调整高度（避免拖动时宽度跳变）
                int columns = Math.Max(1,
                    (box.Bounds.Width - _theme.BodyPadding * 2) / cell.Width);
                int rows = (count + columns - 1) / columns;

                w = columns * cell.Width + _theme.BodyPadding * 2;
                h = rows * cell.Height + HeaderHeightOf(box) + _theme.BodyPadding * 2;
            }

            // 与「创建布局」「载入布局」共用同一套下限，
            // 三处口径不一致会导致相邻面板重叠
            if (w < GroupLayout.MinPanelWidth) w = GroupLayout.MinPanelWidth;
            if (h < GroupLayout.MinPanelHeight) h = GroupLayout.MinPanelHeight;

            box.Bounds = new Rectangle(box.Bounds.X, box.Bounds.Y, w, h);
        }

        /// <summary>
        /// 拖一个图标到另一个图标上 → 自动创建新分组。
        ///
        /// 这是原程序最具标志性的交互（内部代号 AVCDragCreateBoxMenu）。
        /// 新分组落在鼠标位置，包含被拖的图标与目标图标。
        /// </summary>
        private void CreateGroupWithItem(int srcGroup, int srcItem,
                                         int dstGroup, int dstItem,
                                         Point localPoint)
        {
            GroupItem dragged = GetItem(srcGroup, srcItem);
            GroupItem target = GetItem(dstGroup, dstItem);
            if (dragged == null || target == null) return;

            var box = new FenceBox();
            box.Title = "新建分组";
            box.Items.Add(dragged);
            box.Items.Add(target);

            // 先移除源条目（注意索引顺序：先删大索引，避免错位）
            RemoveItemAt(srcGroup, srcItem);
            RemoveItemAt(dstGroup, dstItem);

            // 落点即新分组左上角
            box.Bounds = new Rectangle(
                ToScreenPoint(localPoint).X,
                ToScreenPoint(localPoint).Y,
                220, 120);

            _layout.Groups.Add(box);
            AutoFitPanel(_layout.Groups.Count - 1);

            MarkDirty();
            Render();
        }

        /// <summary>按索引安全移除条目。</summary>
        private void RemoveItemAt(int groupIndex, int itemIndex)
        {
            if (groupIndex < 0 || groupIndex >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[groupIndex];
            if (itemIndex < 0 || itemIndex >= box.Items.Count) return;

            box.Items.RemoveAt(itemIndex);
        }

        /// <summary>
        /// 落点会不会触发「组合」。
        ///
        /// **必须是落在对方的头部**（标题栏，或组合后的标签条）才算 ——
        /// 早先只判断「是否落在对方矩形内」，而面板通常排得比较密，
        /// 拖一个面板经过另一个时一松手就组合了，极易误触；
        /// 用户曾因此把 7 个面板全组合成了一个。
        /// </summary>
        private int HitCombineTarget(int excludeIndex, Point localPoint)
        {
            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                if (gi == excludeIndex) continue;

                FenceBox box = _layout.Groups[gi];
                Rectangle b = ToLocal(box.Bounds);
                var header = new Rectangle(b.X, b.Y, b.Width, HeaderHeightOf(box));

                if (header.Contains(localPoint)) return gi;
            }
            return -1;
        }

        /// <summary>
        /// 把一个分组并入另一个分组：条目全部转移，源分组删除。
        ///
        /// 只调整分组归属，**不会删除任何磁盘文件**。
        /// 重复条目（同一路径已在目标分组里）会自动跳过。
        /// </summary>
        private bool MergeGroups(int srcIndex, int dstIndex)
        {
            if (srcIndex < 0 || dstIndex < 0) return false;
            if (srcIndex >= _layout.Groups.Count || dstIndex >= _layout.Groups.Count) return false;
            if (srcIndex == dstIndex) return false;

            FenceBox src = _layout.Groups[srcIndex];
            FenceBox dst = _layout.Groups[dstIndex];

            // 先把要移入的条目挑出来（去重），再询问 ——
            // 这样提示里能给出准确的数字
            var incoming = new List<GroupItem>();
            foreach (GroupItem it in src.Items)
            {
                if (!ContainsPath(dstIndex, it.FullPath))
                    incoming.Add(it);
            }

            var answer = MessageBox.Show(this,
                "把「" + src.Title + "」合并进「" + dst.Title + "」？\n\n" +
                "「" + src.Title + "」共 " + src.Items.Count + " 个条目，\n" +
                "其中 " + incoming.Count + " 个会被移入目标分组" +
                (src.Items.Count - incoming.Count > 0
                    ? "（" + (src.Items.Count - incoming.Count) + " 个重复，将跳过）"
                    : "") + "。\n\n" +
                "只调整分组，不会删除任何文件。",
                "合并分组", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return false;

            foreach (GroupItem it in incoming)
                dst.Items.Add(it);

            // 删除源分组。注意：若目标在源之后，其索引会前移一位。
            int dstAfterRemove = dstIndex > srcIndex ? dstIndex - 1 : dstIndex;

            _layout.Groups.RemoveAt(srcIndex);

            if (dstAfterRemove >= 0 && dstAfterRemove < _layout.Groups.Count)
                AutoFitPanel(dstAfterRemove);

            MarkDirty();
            Render();
            return true;
        }

        // ============================================================
        //  分类组合（对应参考实现的 widget groups）
        // ============================================================

        /// <summary>
        /// 把一个收纳盒「组合」进另一个 —— 变成多分类格子组。
        ///
        /// 与「合并」的区别正在于此：
        ///   · 组合：源盒的每个分类各自成为一个新标签，条目各归各的，不混在一起
        ///   · 合并：所有条目塞进同一个分类（见 MergeGroups）
        ///
        /// 组合不复制也不移动任何文件，只是换了归属的盒子；随时可以再拆开。
        /// </summary>
        private bool CombineAsGroup(int srcIndex, int dstIndex)
        {
            if (srcIndex < 0 || dstIndex < 0) return false;
            if (srcIndex >= _layout.Groups.Count || dstIndex >= _layout.Groups.Count) return false;
            if (srcIndex == dstIndex) return false;

            FenceBox src = _layout.Groups[srcIndex];
            FenceBox dst = _layout.Groups[dstIndex];

            int free = FenceBox.MaxTabs - dst.Tabs.Count;
            if (free <= 0)
            {
                MessageBox.Show(this,
                    "「" + dst.Title + "」已经有 " + dst.Tabs.Count + " 个分类，" +
                    "已达上限 " + FenceBox.MaxTabs + " 个，无法继续添加。",
                    "无法组合", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            int moving = Math.Min(src.Tabs.Count, free);

            var answer = MessageBox.Show(this,
                "把「" + src.Title + "」组合进「" + dst.Title + "」？\n\n" +
                (moving < src.Tabs.Count ? "（上限 " + FenceBox.MaxTabs + " 个分类，" +
                                           "超出的留在原处）\n\n" : "") +
                "组合后点标签就能切换查看，随时可以再拆开。\n" +
                "不会移动或删除任何文件。",
                "组合成格子组", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return false;

            // 记录第一个新分类的下标，稍后切过去，让用户立刻看到结果
            int firstNew = dst.Tabs.Count;

            for (int i = 0; i < moving; i++)
                dst.Tabs.Add(src.Tabs[i]);

            src.Tabs.RemoveRange(0, moving);

            if (src.Tabs.Count == 0)
            {
                // 源盒被搬空 → 整个盒子消失，目标索引可能前移一位
                _layout.Groups.RemoveAt(srcIndex);

                int dstAfter = dstIndex > srcIndex ? dstIndex - 1 : dstIndex;
                if (dstAfter >= 0 && dstAfter < _layout.Groups.Count)
                {
                    _layout.Groups[dstAfter].ActiveTab = firstNew;
                    AutoFitPanel(dstAfter);
                }
            }
            else
            {
                // 源盒还有余下的分类 → **两个盒都要按各自当前分类重新收紧**。
                // 早先这里只收紧了源盒，目标盒明明多了分类、换了当前分类却没重排，
                // 尺寸与实际内容对不上。
                dst.ActiveTab = firstNew;

                AutoFitPanel(srcIndex);

                if (dstIndex >= 0 && dstIndex < _layout.Groups.Count)
                    AutoFitPanel(dstIndex);
            }

            MarkDirty();
            Render();
            return true;
        }

        /// <summary>新增一个空分类并切过去。</summary>
        private void AddTab(int gi)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (box.Tabs.Count >= FenceBox.MaxTabs)
            {
                MessageBox.Show(this,
                    "无法添加，收纳盒最多支持 " + FenceBox.MaxTabs + " 个分类！",
                    "已达上限", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            box.Tabs.Add(new FenceTab("新分类 " + (box.Tabs.Count + 1)));
            box.ActiveTab = box.Tabs.Count - 1;

            AutoFitPanel(gi);
            MarkDirty();
            Render();
        }

        /// <summary>切换到指定分类。</summary>
        private void SwitchTab(int gi, int tabIndex)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (tabIndex < 0 || tabIndex >= box.Tabs.Count) return;
            if (box.ActiveTab == tabIndex) return;

            box.ActiveTab = tabIndex;
            EnsureTabFits(gi);          // 新分类条目更多时先把面板撑开
            MarkDirty();
            Render();
        }

        /// <summary>循环切换分类（鼠标滚轮 / Ctrl+Tab）。</summary>
        private void CycleTab(int gi, int step)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (box.Tabs.Count < 2) return;
            if (!box.NextTab(step)) return;

            EnsureTabFits(gi);          // 新分类条目更多时先把面板撑开
            MarkDirty();
            Render();
        }

        /// <summary>把一个分类拆出来，成为独立的收纳盒。</summary>
        private void DetachTab(int gi, int tabIndex)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (box.Tabs.Count < 2) return;
            if (tabIndex < 0 || tabIndex >= box.Tabs.Count) return;

            FenceTab tab = box.Tabs[tabIndex];

            var nb = new FenceBox();
            nb.Tabs.Clear();
            nb.Tabs.Add(tab);
            // 放在源盒右侧并留出间距 —— 正好是吸附会认的贴合位置
            nb.Bounds = new Rectangle(
                box.Bounds.Right + _theme.PanelGap,
                box.Bounds.Y,
                box.Bounds.Width,
                box.Bounds.Height);

            box.Tabs.RemoveAt(tabIndex);
            if (box.ActiveTab >= box.Tabs.Count) box.ActiveTab = box.Tabs.Count - 1;

            _layout.Groups.Add(nb);

            AutoFitPanel(gi);
            AutoFitPanel(_layout.Groups.Count - 1);

            MarkDirty();
            Render();
        }

        /// <summary>解散一个分类；若它是最后一个，等同于解散整个收纳盒。</summary>
        private void RemoveTab(int gi, int tabIndex)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (tabIndex < 0 || tabIndex >= box.Tabs.Count) return;

            if (box.Tabs.Count == 1)
            {
                RemoveGroup(gi);
                return;
            }

            FenceTab tab = box.Tabs[tabIndex];

            var answer = MessageBox.Show(this,
                "解散分类「" + tab.Title + "」？\n\n" +
                "其中的 " + tab.Items.Count + " 个图标不会被删除，" +
                "只是不再显示在该收纳盒中。",
                "解散当前分类", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            box.Tabs.RemoveAt(tabIndex);
            if (box.ActiveTab >= box.Tabs.Count) box.ActiveTab = box.Tabs.Count - 1;

            AutoFitPanel(gi);
            MarkDirty();
            Render();
        }

        /// <summary>除指定面板外的所有面板矩形（屏幕坐标），供吸附计算使用。</summary>
        private List<Rectangle> OtherRects(int excludeIndex)
        {
            var list = new List<Rectangle>(_layout.Groups.Count);
            for (int i = 0; i < _layout.Groups.Count; i++)
            {
                if (i == excludeIndex) continue;
                list.Add(_layout.Groups[i].Bounds);
            }
            return list;
        }

        // ============================================================
        //  解散格子组 / 尺寸保证
        // ============================================================

        /// <summary>
        /// 解散格子组：把该盒的每个分类还原成独立的收纳盒。
        ///
        /// 这是「组合到…」的反操作。之前只能一个分类一个分类地拆，
        /// 一旦误把多个面板组合起来就很难还原 —— 用户正是这么被坑的。
        /// </summary>
        private void DissolveGroup(int gi)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox src = _layout.Groups[gi];

            if (src.Tabs.Count < 2)
            {
                MessageBox.Show(this, "这个收纳盒只有一个分类，不需要解散。",
                    "解散格子组", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int total = 0;
            foreach (FenceTab t in src.Tabs) total += t.Items.Count;

            var answer = MessageBox.Show(this,
                "把「" + src.Title + "」的 " + src.Tabs.Count + " 个分类拆成 " +
                src.Tabs.Count + " 个独立收纳盒？\n\n" +
                "共 " + total + " 个条目会各自回到对应收纳盒。\n" +
                "只调整面板归属，不会移动或删除任何文件。",
                "解散格子组", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            // 先把分类与现有几何取出来，再删原盒
            var tabs = new List<FenceTab>(src.Tabs);
            int baseX = src.Bounds.X;
            int baseY = src.Bounds.Y;
            BoxViewMode view = src.ViewMode;

            int maxCols = AppSettings.Current.MaxColumns;
            if (maxCols < 1) maxCols = 5;

            Rectangle screen = SystemInformation.VirtualScreen;
            int singleHeader = _renderer.HeaderHeight(1);

            _layout.Groups.RemoveAt(gi);

            int x = baseX, y = baseY, rowH = 0;

            for (int i = 0; i < tabs.Count; i++)
            {
                FenceTab tab = tabs[i];

                Size sz = _renderer.MeasurePanelSize(
                    Math.Max(1, tab.Items.Count), maxCols, singleHeader, view);

                if (x + sz.Width > screen.Right - 8 && x > baseX)
                {
                    x = baseX;
                    y += rowH + _theme.PanelGap;
                    rowH = 0;
                }

                var nb = new FenceBox();
                nb.Tabs.Clear();
                nb.Tabs.Add(tab);
                nb.ViewMode = view;
                nb.ActiveTab = 0;
                nb.Bounds = new Rectangle(x, y, sz.Width, sz.Height);

                _layout.Groups.Add(nb);

                x += sz.Width + _theme.PanelGap;
                if (sz.Height > rowH) rowH = sz.Height;
            }

            // 摆不稳就交给既有的重叠消除兜底
            _layout.ResolveOverlaps(screen.Width);

            MarkDirty();
            Render();

            MessageBox.Show(this,
                "已拆成 " + tabs.Count + " 个收纳盒。\n可直接拖标题栏重新摆放。",
                "解散完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 切换分类后保证当前分类的条目装得下。
        ///
        /// 各分类的条目数差别很大（1 项到 36 项都有），切到一个更满的分类时
        /// 若不扩高，超出的图标就会被裁掉甚至画到面板外。
        /// **只增不减** —— 避免来回切标签时面板忽大忽小。
        /// </summary>
        private void EnsureTabFits(int gi)
        {
            if (!_autoFit) return;
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            Rectangle b = box.Bounds;
            int headerH = HeaderHeightOf(box);

            int columns, rows;
            _renderer.MeasureCapacity(b, headerH, box.ViewMode, out columns, out rows);

            int need = box.Items.Count;
            if (need <= columns * rows) return;

            int needRows = (need + columns - 1) / columns;
            Size cell = _renderer.CellSize(box.ViewMode);
            int h = needRows * cell.Height + headerH + _theme.BodyPadding * 2;

            if (h < GroupLayout.MinPanelHeight) h = GroupLayout.MinPanelHeight;
            if (h <= b.Height) return;

            // 不能顶出屏幕底部
            Rectangle screen = SystemInformation.VirtualScreen;
            if (b.Y + h > screen.Bottom) h = Math.Max(b.Height, screen.Bottom - b.Y);

            box.Bounds = new Rectangle(b.X, b.Y, b.Width, h);
        }

        // ============================================================
        //  渲染
        // ============================================================

        /// <summary>复用的绘图缓冲区。拖拽时每帧都渲染，反复 new 1080p ARGB（≈8MB）
        /// 会把 GC 大对象堆打爆 —— 只在尺寸变化时重建。</summary>
        private Bitmap _backBuffer;

        /// <summary>整屏重绘。任何「不知道具体哪里变了」的场景都走这里 —— 它是安全网。</summary>
        private void Render()
        {
            if (!IsHandleCreated || _closing) return;

            int w = Width;
            int h = Height;
            if (w <= 0 || h <= 0) return;

            PerfStats.BeginFrame();

            _origin = Location;      // 脏矩形计算依赖它，顺手保证不漂移
            EnsureBackBuffer(w, h);

            using (Graphics g = Graphics.FromImage(_backBuffer))
            {
                // ★ 复用位图必须先清成全透明 —— 之前每帧 new 的位图天然是
                // 干净的透明画布，改成复用后上帧内容会残留：
                // 切换分类/移动面板后旧画面叠在新画面上，就是「重影」
                g.Clear(Color.Transparent);

                DrawContent(g, w, h);
            }

            Blit(_backBuffer, new Rectangle(0, 0, w, h));

            _bufferDirty = false;
            _firstRenderDone = true;

            UpdateSceneStats();
            PerfStats.EndFrame(false);
        }

        /// <summary>
        /// 局部重绘 —— 只重画 dirty 区域并提交该区域（P1-8）。
        ///
        /// 拖动一个面板时，整屏 207 万像素里真正变化的只有面板前后两块，
        /// 走局部可以省掉绝大部分拷贝 + 预乘 + 内核态往返。
        ///
        /// ★ 安全约束：
        ///   1. dirty 必须覆盖「变化前」与「变化后」两块的并集，少一块就是残影
        ///      （0.9.0 的重影事故就是这个族的问题）
        ///   2. 缓冲区刚重建时内容不可信 → 强制退化为整屏
        ///   3. 设置里 perfFullRedraw=1 或 perfDirtyRect=0 → 一律整屏
        /// </summary>
        private void RenderRegion(Rectangle dirty)
        {
            if (!IsHandleCreated || _closing) return;

            int w = Width;
            int h = Height;
            if (w <= 0 || h <= 0) return;

            EnsureBackBuffer(w, h);
            _origin = Location;

            bool partial = CanUseDirtyRect() && !_bufferDirty;

            dirty.Intersect(new Rectangle(0, 0, w, h));
            if (partial && (dirty.Width <= 0 || dirty.Height <= 0)) return;

            // 脏区是「两个矩形的包围盒」：鼠标从左上角盒子划到右下角盒子时，
            // 包围盒几乎等于整屏。这时走局部不但没有收益（清屏+重绘范围一样大），
            // 还多一次判断开销 —— 直接整屏更稳。
            if (partial && (long)dirty.Width * dirty.Height * 2 > (long)w * h)
                partial = false;

            if (!partial)
            {
                Render();
                return;
            }

            PerfStats.BeginFrame();

            try
            {
            using (Graphics g = Graphics.FromImage(_backBuffer))
            {
                // 局部清屏必须用 SourceCopy：默认 SourceOver 拿透明色去"叠"，
                // 等于什么都没做 —— 这正是局部更新最容易踩的坑
                CompositingMode oldMode = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                {
                    g.FillRectangle(clear, dirty);
                }
                g.CompositingMode = oldMode;

                Region oldClip = g.Clip;
                g.SetClip(dirty, CombineMode.Intersect);
                try
                {
                    DrawContent(g, w, h);
                }
                finally
                {
                    g.SetClip(oldClip, CombineMode.Replace);
                    oldClip.Dispose();
                }
            }

                Blit(_backBuffer, dirty);
                _firstRenderDone = true;
            }
            catch (Exception ex)
            {
                // 局部更新一旦出任何问题，**本次会话**直接降级为整屏重绘，
                // 并记日志 —— 宁可慢一点，也不能让界面出错
                _dirtyRectFailed = true;
                AppLog.Error("局部重绘失败，本次会话降级为整屏重绘", ex);

                _bufferDirty = true;
                Render();
                return;
            }

            PerfStats.EndFrame(true);
        }

        /// <summary>局部重绘出过错 → 本次会话不再尝试（进程级自愈，不用改设置）。</summary>
        private bool _dirtyRectFailed;

        /// <summary>是否允许走脏矩形。设置里保留一键回到整屏的开关。</summary>
        private bool CanUseDirtyRect()
        {
            if (_dirtyRectFailed) return false;

            try
            {
                AppSettings s = AppSettings.Current;
                if (s == null) return false;
                return s.PerfDirtyRect && !s.PerfFullRedraw;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureBackBuffer(int w, int h)
        {
            if (_backBuffer != null && _backBuffer.Width == w && _backBuffer.Height == h)
                return;

            if (_backBuffer != null)
            {
                _backBuffer.Dispose();
                _backBuffer = null;
            }

            _backBuffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            // 新缓冲区是脏的，且 GDI 资源尺寸也要跟着变
            _bufferDirty = true;
            ReleaseBlitResources();
        }

        /// <summary>记录场景规模，供 --stats 使用。</summary>
        private void UpdateSceneStats()
        {
            try
            {
                int items = 0;
                for (int i = 0; i < _layout.Groups.Count; i++)
                    items += _layout.Groups[i].Items.Count;

                PerfStats.GroupCount = _layout.Groups.Count;
                PerfStats.ItemCount = items;
                PerfStats.IconCacheCount = (_icons != null) ? _icons.CacheCount : 0;
            }
            catch
            {
                // 统计失败不影响渲染
            }
        }

        // ============================================================
        //  脏矩形计算
        // ============================================================

        /// <summary>投影 / 光晕会画到面板外侧，脏区必须外扩这么多才不会留残影。</summary>
        private int ShadowMargin()
        {
            int m = 0;
            if (_theme != null && _theme.DrawShadow)
                m = Math.Max(_theme.ShadowDepth, _theme.ShadowOffsetY);
            return m + 3;
        }

        private Rectangle InflatedLocal(Rectangle screenRect)
        {
            Rectangle r = ToLocal(screenRect);
            r.Inflate(ShadowMargin(), ShadowMargin());
            return r;
        }

        private static Rectangle Union(Rectangle a, Rectangle b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return Rectangle.Union(a, b);
        }

        /// <summary>参考线在窗体坐标下的包围盒。</summary>
        private Rectangle GuidesBounds(IList<SnapGuide> guides)
        {
            Rectangle r = Rectangle.Empty;
            if (guides == null || guides.Count == 0) return r;

            for (int i = 0; i < guides.Count; i++)
            {
                SnapGuide sg = guides[i];

                if (sg.Vertical)
                {
                    int x = sg.Position - _origin.X;
                    int y0 = sg.From - _origin.Y;
                    int y1 = sg.To - _origin.Y;
                    r = Union(r, new Rectangle(x - 2, Math.Min(y0, y1) - 2, 5,
                                               Math.Abs(y1 - y0) + 5));
                }
                else
                {
                    int y = sg.Position - _origin.Y;
                    int x0 = sg.From - _origin.X;
                    int x1 = sg.To - _origin.X;
                    r = Union(r, new Rectangle(Math.Min(x0, x1) - 2, y - 2,
                                               Math.Abs(x1 - x0) + 5, 5));
                }
            }

            return r;
        }

        /// <summary>
        /// 拖动/缩放过程中按「上一帧位置 ∪ 本帧位置 ∪ 参考线 ∪ 组合高亮」更新。
        /// 少算任何一块都会留残影，所以这里宁可多算。
        /// </summary>
        private void RenderDragFrame(Rectangle oldBounds, Rectangle newBounds)
        {
            Rectangle dirty = Union(InflatedLocal(oldBounds), InflatedLocal(newBounds));

            dirty = Union(dirty, GuidesBounds(_lastGuides));
            dirty = Union(dirty, GuidesBounds(_dragGuides));

            if (_lastCombineTarget >= 0 && _lastCombineTarget < _layout.Groups.Count)
                dirty = Union(dirty, InflatedLocal(_layout.Groups[_lastCombineTarget].Bounds));

            if (_combineTarget >= 0 && _combineTarget < _layout.Groups.Count)
                dirty = Union(dirty, InflatedLocal(_layout.Groups[_combineTarget].Bounds));

            _lastGuides.Clear();
            _lastGuides.AddRange(_dragGuides);
            _lastCombineTarget = _combineTarget;

            RenderRegion(dirty);
        }

        /// <summary>悬停变化：只有所在收纳盒内部会变，脏区就是那两个盒子。</summary>
        private void RenderHoverFrame(int oldGroup, int newGroup)
        {
            Rectangle dirty = Rectangle.Empty;

            if (oldGroup >= 0 && oldGroup < _layout.Groups.Count)
                dirty = Union(dirty, InflatedLocal(_layout.Groups[oldGroup].Bounds));

            if (newGroup >= 0 && newGroup < _layout.Groups.Count)
                dirty = Union(dirty, InflatedLocal(_layout.Groups[newGroup].Bounds));

            if (dirty.IsEmpty)
            {
                Render();
                return;
            }

            // 节流窗口内又发生了新变化 → 把脏区累加起来，避免漏掉中间态
            if (_hoverPending)
            {
                _pendingDirty = Union(_pendingDirty, dirty);
                return;
            }

            if (_hoverTimer == null)
            {
                RenderRegion(dirty);
                return;
            }

            _hoverPending = true;
            _pendingDirty = dirty;
            try
            {
                _hoverTimer.Stop();
                _hoverTimer.Start();
            }
            catch
            {
                RenderRegion(dirty);
            }
        }

        private void DrawContent(Graphics g, int w, int h)
        {
            if (!_panelsVisible) return;

            var view = new Rectangle(0, 0, w, h);

            for (int gi = 0; gi < _layout.Groups.Count; gi++)
            {
                FenceBox box = _layout.Groups[gi];
                Rectangle bounds = ToLocal(box.Bounds);

                if (!bounds.IntersectsWith(view)) continue;

                int headerH = HeaderHeightOf(box);
                int hoverTab = (gi == _hoverGroup) ? _hoverTab : -1;

                _renderer.DrawPanel(g, bounds, box.Title, box.Items.Count,
                                    TabTitlesOf(box), box.ActiveTab, hoverTab);

                // 标题栏按钮（锁 / 视图切换）—— 折叠时也画，标题栏还在
                _renderer.DrawTitleButtons(g, bounds, box.Locked);

                if (box.Collapsed) continue;

                bool listMode = (box.ViewMode == BoxViewMode.List);

                // 把条目裁剪在面板内。
                //
                // 面板被拖小、或当前分类条目多于装得下的数量时，
                // GetItemBounds 照样会算出面板外的坐标 —— 不裁剪就会画到
                // 桌面别处，看上去就是「图标乱七八糟」。
                Region oldClip = g.Clip;
                var body = new Rectangle(
                    bounds.X + 1,
                    bounds.Y + headerH,
                    Math.Max(1, bounds.Width - 2),
                    Math.Max(1, bounds.Height - headerH - 1));

                g.SetClip(body, CombineMode.Intersect);

                try
                {
                    for (int i = 0; i < box.Items.Count; i++)
                    {
                        Rectangle itemRect = _renderer.GetItemBounds(
                            bounds, headerH, box.ViewMode, i);

                        if (!itemRect.IntersectsWith(body)) continue;

                        GroupItem item = box.Items[i];

                        Icon icon = null;
                        try { icon = _icons.GetIcon(item.FullPath, item.IsDirectory); }
                        catch { }

                        ItemState state = ItemState.Normal;
                        if (gi == _hoverGroup && i == _hoverItem) state = ItemState.Hover;

                        if (listMode)
                        {
                            // 列表模式行宽足够，不必截到 14 个字符；
                            // 右侧补一段「所在位置」作为次要信息
                            string detail = item.FullPath;
                            try { detail = Path.GetDirectoryName(item.FullPath); }
                            catch { }
                            if (string.IsNullOrEmpty(detail)) detail = item.IsDirectory ? "文件夹" : "";

                            _renderer.DrawListItem(g, itemRect, icon,
                                GroupPanelRenderer.Ellipsize(item.Name, 40), detail, state);
                        }
                        else
                        {
                            _renderer.DrawItem(g, itemRect, icon,
                                GroupPanelRenderer.Ellipsize(item.Name, 14), state, box.ViewMode);
                        }
                    }
                }
                finally
                {
                    g.SetClip(oldClip, CombineMode.Replace);
                    oldClip.Dispose();
                }
            }

            // 拖着面板时高亮落点目标 —— 松手就会组合，必须先给明确反馈
            if (_combineTarget >= 0 && _combineTarget < _layout.Groups.Count)
            {
                FenceBox tb = _layout.Groups[_combineTarget];
                Rectangle tbBounds = ToLocal(tb.Bounds);
                if (tbBounds.IntersectsWith(view))
                {
                    _renderer.DrawDropTarget(g, tbBounds, HeaderHeightOf(tb),
                        "松手即组合成格子组");
                }
            }

            // 缩放手柄：常驻画在每个盒的右下角。
            //
            // 缩放功能早就能用，但之前一个像素的提示都没画 ——
            // 用户根本不知道这个角可以拖，自然以为不能改尺寸。
            // 锁定时不画：那时缩放是禁用的，画了等于骗人。
            if (!_locked)
            {
                for (int gi = 0; gi < _layout.Groups.Count; gi++)
                {
                    Rectangle gripBounds = ToLocal(_layout.Groups[gi].Bounds);
                    if (!gripBounds.IntersectsWith(view)) continue;

                    // 只有真正悬停、且没在拖拽时才高亮
                    bool hot = (_drag == DragMode.None && gi == _hoverGrip);

                    _renderer.DrawResizeGrip(g, gripBounds, hot);
                }
            }

            // 缩放中：在右下角显示当前尺寸。
            // 吸附是「跳变」，只盯边缘不容易察觉，把数字显示出来手感才明确。
            if (_drag == DragMode.Resize && _dragGroup >= 0
                && _dragGroup < _layout.Groups.Count)
            {
                FenceBox rb = _layout.Groups[_dragGroup];
                Rectangle rbBounds = ToLocal(rb.Bounds);
                if (rbBounds.IntersectsWith(view))
                    _renderer.DrawSizeBadge(g, rbBounds, rb.Bounds.Size);
            }

            // 参考线画在最上层 —— 拖动时能看见正在跟谁对齐
            if (_dragGuides.Count > 0)
                _renderer.DrawSnapGuides(g, _dragGuides, _origin);
        }

        /// <summary>
        /// 把位图的指定区域按预乘 alpha 提交到分层窗口。
        ///
        /// 三项复用（复盘 P0-1 + P1-9）：
        ///   1. 临时 byte[] 复用 —— 原来是每帧 new，1080p 就是 8.3MB/帧进 LOH
        ///   2. 内存 DC / DIBSection 复用 —— 原来是每帧 Create/Delete，
        ///      GDI 对象churn 加内核态往返
        ///   3. stride == w*4 时一次 Marshal.Copy 拷完，省掉上千次小拷贝
        ///   4. 预乘走指针版（设置 perfFastBlit 可切回托管版做对照）
        ///   5. 局部更新时只处理 dirty 区域 —— 拷贝量与预乘量都随区域缩小
        /// </summary>
        private void Blit(Bitmap bmp, Rectangle rect)
        {
            if (bmp == null || rect.Width <= 0 || rect.Height <= 0) return;

            IntPtr screenDc = IntPtr.Zero;

            try
            {
                screenDc = OverlayNative.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return;

                // ★ 关键：GDI 资源**始终按整屏尺寸**分配。
                // 若按脏区尺寸分配，脏区每帧大小都在变，DIBSection 就会被反复
                // 重建 —— 反而比整屏重绘更糟。整屏分配一次，局部更新只填子区域。

                // 资源是本次才新建的话，DIB 里 dirty 之外还是一片 0，
                // 这时绝不能走局部 —— 否则未更新区域会变成透明空洞。
                bool fresh = (_blitBmp == IntPtr.Zero || _blitBuffer == null
                              || _blitW != bmp.Width || _blitH != bmp.Height);

                if (!EnsureBlitResources(screenDc, bmp.Width, bmp.Height)) return;

                if (fresh)
                    rect = new Rectangle(0, 0, _blitW, _blitH);

                int fullStride = _blitW * 4;
                int rowBytes = rect.Width * 4;
                bool wholeScreen = (rect.X == 0 && rect.Y == 0
                                    && rect.Width == _blitW && rect.Height == _blitH);

                BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly,
                                             PixelFormat.Format32bppArgb);
                try
                {
                    if (wholeScreen && bd.Stride == rowBytes)
                    {
                        // 整屏：一次拷完 + 一次预乘，最快路径
                        Marshal.Copy(bd.Scan0, _blitBuffer, 0, rowBytes * rect.Height);
                        PremultiplyWhole(rowBytes * rect.Height);
                    }
                    else
                    {
                        // 局部：把脏区逐行填进整屏缓冲的对应位置
                        for (int y = 0; y < rect.Height; y++)
                        {
                            IntPtr rowPtr = (IntPtr)((long)bd.Scan0 + (long)y * bd.Stride);
                            int dest = (rect.Y + y) * fullStride + rect.X * 4;

                            Marshal.Copy(rowPtr, _blitBuffer, dest, rowBytes);

                            if (UseFastBlit())
                                OverlayNative.PremultiplyFast(_blitBuffer, dest, rowBytes);
                            else
                                OverlayNative.Premultiply(_blitBuffer, dest, rowBytes);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                // 提交：整屏一次拷完；局部只拷脏区在整屏缓冲里的那一段
                if (wholeScreen)
                {
                    Marshal.Copy(_blitBuffer, 0, _blitBits, rowBytes * rect.Height);
                }
                else
                {
                    for (int y = 0; y < rect.Height; y++)
                    {
                        int src = (rect.Y + y) * fullStride + rect.X * 4;
                        IntPtr dest = (IntPtr)((long)_blitBits + src);
                        Marshal.Copy(_blitBuffer, src, dest, rowBytes);
                    }
                }

                var blend = new OverlayNative.BLENDFUNCTION();
                blend.BlendOp = OverlayNative.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = OverlayNative.AC_SRC_ALPHA;

                // ★★ 提交必须**始终按整屏尺寸** —— 这是分层窗口最容易踩的坑：
                //
                // UpdateLayeredWindow 的 pptDst / psize 定义的是**整个窗口的新位置与新尺寸**，
                // 不是"要更新的子矩形"。若传局部矩形（dst=Left+rect.X、size=rect 尺寸），
                // 系统会把覆盖层窗口整个**挪到子区域的位置并缩放到子区域的尺寸**：
                //   · 画面表现 = 每次重绘所有面板都在跳（观感就是「满屏栅格闪烁」）
                //   · 更隐蔽的后果 = 窗口 Location/Size 被改 → ToLocal 的 _origin 漂移
                //     → 命中测试整体错位 → 点分类标签、点图标全部落空
                //
                // 所以：绘制与拷贝可以只做 dirty 区域（这才是有意义的那部分优化），
                // 最后一步提交必须是整屏。源 DC 里 dirty 之外的像素保留上一次的值，
                // 与整屏重绘的结果逐像素一致。
                var srcPt = new OverlayNative.POINT { X = 0, Y = 0 };
                var size = new OverlayNative.SIZE { cx = _blitW, cy = _blitH };
                var dstPt = new OverlayNative.POINT { X = Left, Y = Top };

                OverlayNative.UpdateLayeredWindow(Handle, screenDc, ref dstPt, ref size,
                    _blitMemDc, ref srcPt, 0, ref blend, OverlayNative.ULW_ALPHA);
            }
            catch (Exception ex)
            {
                AppLog.Error("提交分层窗口失败", ex);
            }
            finally
            {
                if (screenDc != IntPtr.Zero) OverlayNative.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /// <summary>整屏预乘（一次性处理整块）。</summary>
        private void PremultiplyWhole(int length)
        {
            if (UseFastBlit())
                OverlayNative.PremultiplyFast(_blitBuffer, 0, length);
            else
                OverlayNative.Premultiply(_blitBuffer, 0, length);
        }

        private static bool UseFastBlit()
        {
            try
            {
                AppSettings s = AppSettings.Current;
                return s == null || s.PerfFastBlit;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>准备好与尺寸匹配的 GDI 资源；尺寸不变则直接复用。</summary>
        private bool EnsureBlitResources(IntPtr screenDc, int w, int h)
        {
            if (_blitMemDc != IntPtr.Zero && _blitBmp != IntPtr.Zero
                && _blitW == w && _blitH == h && _blitBuffer != null)
                return true;

            ReleaseBlitResources();

            _blitMemDc = OverlayNative.CreateCompatibleDC(screenDc);
            if (_blitMemDc == IntPtr.Zero) return false;

            var bmi = new OverlayNative.BITMAPINFO();
            // 颜色表数组必须显式分配，否则 P/Invoke 编组会因空引用失败
            bmi.bmiColors = new OverlayNative.RGBQUAD[256];
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(OverlayNative.BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;   // 负数 = 自上而下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = OverlayNative.BI_RGB;

            IntPtr bits;
            _blitBmp = OverlayNative.CreateDIBSection(_blitMemDc, ref bmi,
                OverlayNative.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);

            if (_blitBmp == IntPtr.Zero || bits == IntPtr.Zero)
            {
                ReleaseBlitResources();
                return false;
            }

            _blitBits = bits;
            _blitOld = OverlayNative.SelectObject(_blitMemDc, _blitBmp);

            _blitBuffer = new byte[w * 4 * h];
            _blitW = w;
            _blitH = h;
            return true;
        }

        private void ReleaseBlitResources()
        {
            if (_blitMemDc != IntPtr.Zero)
            {
                if (_blitOld != IntPtr.Zero)
                {
                    OverlayNative.SelectObject(_blitMemDc, _blitOld);
                    _blitOld = IntPtr.Zero;
                }
                if (_blitBmp != IntPtr.Zero)
                {
                    OverlayNative.DeleteObject(_blitBmp);
                    _blitBmp = IntPtr.Zero;
                }
                OverlayNative.DeleteDC(_blitMemDc);
                _blitMemDc = IntPtr.Zero;
            }

            _blitBits = IntPtr.Zero;
            _blitBuffer = null;
            _blitW = 0;
            _blitH = 0;
        }

        private Point _origin;

        private Rectangle ToLocal(Rectangle r)
        {
            return new Rectangle(r.X - _origin.X, r.Y - _origin.Y, r.Width, r.Height);
        }

        private Point ToScreenPoint(Point p)
        {
            return new Point(p.X + _origin.X, p.Y + _origin.Y);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            _origin = Location;
        }

        // ============================================================
        //  命中测试
        // ============================================================

        private int HitGroupTitle(Point p)
        {
            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                Rectangle b = ToLocal(_layout.Groups[gi].Bounds);
                var title = new Rectangle(b.X, b.Y, b.Width, _theme.TitleHeight);
                if (title.Contains(p)) return gi;
            }
            return -1;
        }

        /// <summary>
        /// 命中标题栏按钮（0=锁，1=视图切换）。几何与
        /// GroupPanelRenderer.TitleButtonRect 共用，不可能漂移。
        /// </summary>
        private bool HitTitleButton(Point p, out int groupIndex, out int button)
        {
            groupIndex = -1;
            button = -1;

            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                Rectangle b = ToLocal(_layout.Groups[gi].Bounds);
                if (!b.Contains(p)) continue;

                for (int i = 0; i < 2; i++)
                {
                    Rectangle r = GroupPanelRenderer.TitleButtonRect(
                        b, _theme.TitleHeight, i);
                    if (r.Contains(p))
                    {
                        groupIndex = gi;
                        button = i;
                        return true;
                    }
                }
                return false;   // 在这个盒里但没点中按钮
            }
            return false;
        }

        private bool HitResizeGrip(Point p, out int groupIndex)
        {
            groupIndex = -1;
            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                Rectangle b = ToLocal(_layout.Groups[gi].Bounds);
                var grip = new Rectangle(b.Right - ResizeGrip, b.Bottom - ResizeGrip,
                                         ResizeGrip, ResizeGrip);
                if (grip.Contains(p)) { groupIndex = gi; return true; }
            }
            return false;
        }

        private bool HitItem(Point p, out int groupIndex, out int itemIndex)
        {
            groupIndex = -1;
            itemIndex = -1;

            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                FenceBox box = _layout.Groups[gi];
                if (box.Collapsed) continue;

                Rectangle b = ToLocal(box.Bounds);
                if (!b.Contains(p)) continue;

                for (int i = 0; i < box.Items.Count; i++)
                {
                    if (ItemBoundsOf(gi, i).Contains(p))
                    {
                        groupIndex = gi;
                        itemIndex = i;
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// 命中分类标签行。
        ///
        /// tabIndex 为 -1 且返回 true 时表示「紧凑模式」——
        /// 分类太多无法并排显示，整条退化成一块，需要弹出列表让用户选。
        /// </summary>
        private bool HitTab(Point p, out int groupIndex, out int tabIndex)
        {
            groupIndex = -1;
            tabIndex = -1;

            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                FenceBox box = _layout.Groups[gi];
                if (box.Tabs.Count < 2) continue;

                Rectangle[] rects = _renderer.GetTabRects(ToLocal(box.Bounds), box.Tabs.Count);
                if (rects == null) continue;

                for (int t = 0; t < rects.Length; t++)
                {
                    if (!rects[t].Contains(p)) continue;

                    groupIndex = gi;
                    // 并排模式直接给出分类下标；紧凑模式给 -1，由调用方弹列表
                    tabIndex = (rects.Length == box.Tabs.Count) ? t : -1;
                    return true;
                }
            }
            return false;
        }

        private int HitGroupBody(Point p)
        {
            for (int gi = _layout.Groups.Count - 1; gi >= 0; gi--)
            {
                if (ToLocal(_layout.Groups[gi].Bounds).Contains(p)) return gi;
            }
            return -1;
        }

        // ============================================================
        //  鼠标交互
        // ============================================================

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            // 无边框窗体默认不参与焦点，滚轮消息会收不到。
            // 点一下就能拿到焦点，之后滚轮切换分类才有效。
            if (!Focused)
            {
                try { Focus(); } catch { }
            }

            // 分类标签：左键切换 / 右键出菜单
            int tg, tt;
            if (HitTab(e.Location, out tg, out tt))
            {
                if (e.Button == MouseButtons.Right)
                {
                    ShowTabMenu(tg, tt, e.Location);
                    return;
                }
                if (e.Button == MouseButtons.Left)
                {
                    if (tt >= 0) SwitchTab(tg, tt);
                    else ShowTabList(tg, e.Location);   // 紧凑模式：弹列表
                    return;
                }
            }

            if (e.Button == MouseButtons.Right)
            {
                // 右键图标 → 系统原生菜单；右键面板或空白 → 本工具菜单
                int rg, ri;
                if (HitItem(e.Location, out rg, out ri))
                {
                    if (!ShowShellMenu(rg, ri, e.Location))
                        ShowItemFallbackMenu(rg, ri, e.Location);
                }
                else
                {
                    ShowContextMenu(e.Location);
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // 锁定状态下只响应双击打开与右键菜单，不做任何拖动
            if (_locked) return;

            _snap.ClearGuides();
            _dragGuides.Clear();
            _combineTarget = -1;

            // ---- 标题栏按钮（锁 / 视图切换）：优先级最高，点了就不当拖动处理 ----
            int bgi, bbtn;
            if (HitTitleButton(e.Location, out bgi, out bbtn))
            {
                FenceBox bb = _layout.Groups[bgi];

                if (bbtn == 0)
                {
                    bb.Locked = !bb.Locked;
                }
                else
                {
                    // 循环切换：大图标 → 小图标 → 列表 → 大图标
                    bb.ViewMode = (BoxViewMode)(((int)bb.ViewMode + 1) % 3);
                    AutoFitPanel(bgi);
                }

                MarkDirty();
                Render();
                return;
            }

            int gi;
            if (HitResizeGrip(e.Location, out gi))
            {
                if (_layout.Groups[gi].Locked) return;   // 单盒锁定：不能缩放

                _drag = DragMode.Resize;
                _dragGroup = gi;
                _dragStart = e.Location;
                _dragOriginRect = _layout.Groups[gi].Bounds;
                UpdateCursor();
                return;
            }

            int itemIndex;
            int groupIndex;
            if (HitItem(e.Location, out groupIndex, out itemIndex))
            {
                if (_layout.Groups[groupIndex].Locked) return;   // 锁定盒：条目拖不动（双击照常）

                _drag = DragMode.Item;
                _dragGroup = groupIndex;
                _dragItem = itemIndex;
                _dragStart = e.Location;
                UpdateCursor();
                return;
            }

            int titleGroup = HitGroupTitle(e.Location);
            if (titleGroup >= 0)
            {
                if (_layout.Groups[titleGroup].Locked) return;   // 锁定盒：面板拖不动

                _drag = DragMode.Panel;
                _dragGroup = titleGroup;
                _dragStart = e.Location;
                _dragOriginRect = _layout.Groups[titleGroup].Bounds;
                UpdateCursor();
                return;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            // 拖拽中不清理：鼠标滑出屏幕边缘再回来时，
            // 一清一建会让手柄高亮闪烁
            if (_drag != DragMode.None) return;

            if (_hoverGroup == -1 && _hoverItem == -1
                && _hoverTab == -1 && _hoverGrip == -1) return;

            _hoverGroup = -1;
            _hoverItem = -1;
            _hoverTab = -1;
            _hoverGrip = -1;

            UpdateCursor();
            Render();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖拽过程中不会走 UpdateHover，指针得在这里刷新 ——
            // 否则拖起来还是箭头，松手才会变
            UpdateCursor();

            if (_drag == DragMode.None)
            {
                UpdateHover(e.Location);
                return;
            }

            int dx = e.X - _dragStart.X;
            int dy = e.Y - _dragStart.Y;

            if (_drag == DragMode.Panel && _dragGroup >= 0)
            {
                var proposed = new Rectangle(
                    _dragOriginRect.X + dx, _dragOriginRect.Y + dy,
                    _dragOriginRect.Width, _dragOriginRect.Height);

                Rectangle screen = SystemInformation.VirtualScreen;
                FenceBox box = _layout.Groups[_dragGroup];

                // 先吸附（与其它面板或屏幕边缘对齐），再做屏幕边缘保护，
                // 顺序不能反 —— 反了会把吸附结果又推出屏幕外
                proposed = _snap.SnapMove(proposed, OtherRects(_dragGroup), screen);
                proposed = SnapEngine.ClampToScreen(proposed, screen,
                                                    120, HeaderHeightOf(box));

                Rectangle before = box.Bounds;
                box.Bounds = proposed;

                // 落点是否压在别的盒头部 —— 会高亮，松手就组合
                _combineTarget = HitCombineTarget(_dragGroup, e.Location);

                _dragGuides.Clear();
                _dragGuides.AddRange(_snap.Guides);

                MarkDirty();
                RenderDragFrame(before, proposed);
            }
            else if (_drag == DragMode.Resize && _dragGroup >= 0)
            {
                var proposed = new Rectangle(
                    _dragOriginRect.X, _dragOriginRect.Y,
                    Math.Max(140, _dragOriginRect.Width + dx),
                    Math.Max(80, _dragOriginRect.Height + dy));

                proposed = _snap.SnapResize(
                    proposed, OtherRects(_dragGroup), SystemInformation.VirtualScreen, 140, 80);

                Rectangle before = _layout.Groups[_dragGroup].Bounds;
                _layout.Groups[_dragGroup].Bounds = proposed;

                _dragGuides.Clear();
                _dragGuides.AddRange(_snap.Guides);

                MarkDirty();
                RenderDragFrame(before, proposed);
            }
            else if (_drag == DragMode.Item)
            {
                UpdateHover(e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 拖面板标题栏落到另一个面板的**头部**上 → 组合成格子组（不是合并条目）
            if (_drag == DragMode.Panel && _dragGroup >= 0)
            {
                // 用拖动过程中算好的落点，保证「看到高亮就一定组合」——
                // 若这里另算一遍条件，会出现高亮了却没组合的错位
                int target = _combineTarget;

                if (target >= 0 && target < _layout.Groups.Count && target != _dragGroup)
                {
                    bool combined = CombineAsGroup(_dragGroup, target);

                    // 用户在确认框里取消：把面板放回原位，
                    // 否则它会停在两个面板重叠的别扭位置上
                    if (!combined && _dragGroup < _layout.Groups.Count)
                    {
                        _layout.Groups[_dragGroup].Bounds = _dragOriginRect;
                        MarkDirty();
                        Render();
                    }
                }
            }

            if (_drag == DragMode.Item && _dragGroup >= 0 && _dragItem >= 0)
            {
                int tg, ti;

                // 优先判断：是否拖到了另一个图标上 → 建新分组
                if (HitItem(e.Location, out tg, out ti)
                    && !(tg == _dragGroup && ti == _dragItem))
                {
                    CreateGroupWithItem(_dragGroup, _dragItem, tg, ti, e.Location);
                }
                else
                {
                    int targetGroup = HitGroupBody(e.Location);

                    if (targetGroup >= 0 && targetGroup != _dragGroup)
                    {
                        GroupItem item = _layout.Groups[_dragGroup].Items[_dragItem];
                        RemoveItemAt(_dragGroup, _dragItem);
                        _layout.Groups[targetGroup].Items.Add(item);

                        AutoFitPanel(_dragGroup);
                        AutoFitPanel(targetGroup);

                        MarkDirty();
                        Render();
                    }
                }
            }

            _drag = DragMode.None;
            _dragGroup = -1;
            _dragItem = -1;

            // 拖动结束：参考线与组合高亮一并消失
            bool hadOverlay = (_dragGuides.Count > 0 || _combineTarget >= 0);
            if (hadOverlay)
            {
                _dragGuides.Clear();
                _combineTarget = -1;
                _snap.ClearGuides();
            }

            // 松手后按当前位置重算悬停态 —— 拖拽期间 hover 是被冻结的，
            // 不重算的话缩放手柄的高亮会停在上一次的位置
            UpdateHover(e.Location);

            // UpdateHover 只在悬停态真变了才重绘，
            // 「只清了参考线、悬停没变」这种情况得在这里补一次
            if (hadOverlay) Render();
        }

        /// <summary>
        /// 滚轮切换分类 —— 对应参考实现的
        /// "switch members from the title, mouse wheel, or cyclic Ctrl+Tab shortcut"。
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            int gi = HitGroupBody(e.Location);
            if (gi < 0) return;

            FenceBox box = _layout.Groups[gi];
            if (box.Tabs.Count < 2) return;

            CycleTab(gi, e.Delta > 0 ? -1 : 1);
        }

        // ============================================================
        //  外部拖放（从资源管理器拖文件进来）
        // ============================================================

        protected override void OnDragEnter(DragEventArgs e)
        {
            AcceptDrop(e);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            AcceptDrop(e);
        }

        private void AcceptDrop(DragEventArgs e)
        {
            if (_locked) { e.Effect = DragDropEffects.None; return; }

            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);

            if (_locked) return;
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] paths;
            try { paths = (string[])e.Data.GetData(DataFormats.FileDrop); }
            catch { return; }

            if (paths == null || paths.Length == 0) return;

            Point client = PointToClient(new Point(e.X, e.Y));
            int gi = HitGroupBody(client);

            if (gi < 0)
            {
                // 拖到空白处 → 新建分组收纳
                var box = new FenceBox();
                box.Title = "新建分组";
                box.Bounds = new Rectangle(
                    ToScreenPoint(client).X, ToScreenPoint(client).Y, 220, 140);
                _layout.Groups.Add(box);
                gi = _layout.Groups.Count - 1;
            }

            int added = 0;

            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (ContainsPath(gi, p)) continue;   // 已存在则不重复添加

                bool isDir;
                try { isDir = Directory.Exists(p); }
                catch { isDir = false; }

                _layout.Groups[gi].Items.Add(new GroupItem(
                    Path.GetFileName(p), p, isDir));
                added++;
            }

            if (added > 0)
            {
                AutoFitPanel(gi);
                MarkDirty();
                Render();
            }
        }

        private bool ContainsPath(int groupIndex, string path)
        {
            if (groupIndex < 0 || groupIndex >= _layout.Groups.Count) return false;

            foreach (GroupItem it in _layout.Groups[groupIndex].Items)
            {
                if (string.Equals(it.FullPath, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);

            // 双击图标 → 用系统默认程序打开
            if (e.Button == MouseButtons.Left)
            {
                int g, it;
                if (HitItem(e.Location, out g, out it))
                {
                    OpenItem(g, it);
                    return;
                }
            }

            int gi = HitGroupTitle(e.Location);
            if (gi >= 0)
            {
                _layout.Groups[gi].Collapsed = !_layout.Groups[gi].Collapsed;
                MarkDirty();
                Render();
            }
        }

        /// <summary>用系统默认程序打开一个图标项。</summary>
        private void OpenItem(int groupIndex, int itemIndex)
        {
            GroupItem item = GetItem(groupIndex, itemIndex);
            if (item == null) return;

            // 路径来自 groups.txt —— 属于外部数据。
            // 交给 Shell 之前先做合法性校验，挡住「存档投毒」这一攻击面。
            if (!PathSafety.IsPlausible(item.FullPath))
            {
                MessageBox.Show(this,
                    "条目路径不合法，已拒绝打开：\n" + item.FullPath +
                    "\n\n可能存档（groups.txt）已损坏或被修改。\n" +
                    "可右键桌面空白处 →「清理已失效条目」来修。",
                    "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ShellLauncher.Exists(item.FullPath))
            {
                MessageBox.Show(this,
                    "目标不存在：\n" + item.FullPath + "\n\n可能已被删除或移动。",
                    "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ShellLauncher.OpenWithFallback(item.FullPath))
            {
                MessageBox.Show(this,
                    "无法打开：\n" + item.FullPath,
                    "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>安全地取出一个条目，越界返回 null。</summary>
        private GroupItem GetItem(int groupIndex, int itemIndex)
        {
            if (groupIndex < 0 || groupIndex >= _layout.Groups.Count) return null;

            FenceBox box = _layout.Groups[groupIndex];
            if (itemIndex < 0 || itemIndex >= box.Items.Count) return null;

            return box.Items[itemIndex];
        }

        /// <summary>
        /// 弹出系统原生右键菜单。
        /// 返回 false 表示准备失败，调用方应回退到自绘菜单。
        /// </summary>
        private bool ShowShellMenu(int groupIndex, int itemIndex, Point localPoint)
        {
            GroupItem item = GetItem(groupIndex, itemIndex);
            if (item == null) return false;
            if (!PathSafety.IsPlausible(item.FullPath)) return false;

            ShellContextMenu menu = null;
            try
            {
                menu = ShellContextMenu.Prepare(Handle, item.FullPath);
                if (menu == null) return false;

                Point screen = PointToScreen(localPoint);
                return menu.Show(this, screen.X, screen.Y);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (menu != null) menu.Dispose();
            }
        }

        /// <summary>原生菜单不可用时的简易回退菜单。</summary>
        private void ShowItemFallbackMenu(int groupIndex, int itemIndex, Point localPoint)
        {
            GroupItem item = GetItem(groupIndex, itemIndex);
            if (item == null) return;

            var menu = new ContextMenuStrip();
            menu.Font = new Font("微软雅黑", 9f);

            menu.Items.Add("打开", null, delegate { OpenItem(groupIndex, itemIndex); });
            menu.Items.Add("打开所在位置", null,
                delegate { ShellLauncher.OpenContainingFolder(item.FullPath); });
            menu.Items.Add("属性", null, delegate { ShellLauncher.ShowProperties(item.FullPath); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("从分组中移除", null,
                delegate { RemoveItemFromGroup(groupIndex, itemIndex); });

            menu.Show(this, localPoint);
        }

        /// <summary>把条目从分组中移除（不动磁盘文件）。</summary>
        private void RemoveItemFromGroup(int groupIndex, int itemIndex)
        {
            GroupItem item = GetItem(groupIndex, itemIndex);
            if (item == null) return;

            _layout.Groups[groupIndex].Items.RemoveAt(itemIndex);
            MarkDirty();
            Render();
        }

        private void UpdateHover(Point p)
        {
            int newGroup = -1;
            int newItem = -1;
            int newTab = -1;
            int newGrip = -1;

            // 缩放手柄的优先级最高，必须与 OnMouseDown 的判断顺序一致 ——
            // 顺序不一致就会出现「高亮在图标上、按下去却开始缩放」
            int gripIndex;
            if (!_locked && HitResizeGrip(p, out gripIndex))
            {
                newGrip = gripIndex;
                newGroup = gripIndex;
            }
            else
            {
                int gi, ii;
                if (HitItem(p, out gi, out ii))
                {
                    newGroup = gi;
                    newItem = ii;
                }
                else
                {
                    // 没落在条目上时也记录所在收纳盒 ——
                    // 标签悬停高亮、滚轮与 Ctrl+Tab 切换都需要知道正对着哪个盒子
                    newGroup = HitGroupBody(p);
                }

                int tg, tt;
                if (HitTab(p, out tg, out tt))
                {
                    newGroup = tg;
                    newTab = tt;   // 紧凑模式为 -1（整条一块，无法逐个高亮）
                }
            }

            bool changed = (newGroup != _hoverGroup || newItem != _hoverItem
                            || newTab != _hoverTab || newGrip != _hoverGrip);

            int prevGroup = _hoverGroup;

            _hoverGroup = newGroup;
            _hoverItem = newItem;
            _hoverTab = newTab;
            _hoverGrip = newGrip;

            UpdateCursor();

            // 悬停变化只影响「离开的盒子」与「进入的盒子」——
            // 之前是整屏重绘，1080p 下每划过一次鼠标就是 8MB 拷贝 + 207 万像素预乘
            if (changed) RenderHoverFrame(prevGroup, newGroup);
        }

        /// <summary>
        /// 按当前位置切换鼠标指针。
        ///
        /// 之前从来不碰 Cursor，整个覆盖层永远是默认箭头 ——
        /// 「右下角能不能缩放」、「图标能不能拖」全靠用户猜。
        /// </summary>
        private void UpdateCursor()
        {
            Cursor wanted;

            if (_locked)
            {
                // 锁定时一切拖动都禁用，指针不该给出能拖的暗示
                wanted = Cursors.Default;
            }
            else if (_drag == DragMode.Resize)
            {
                wanted = Cursors.SizeNWSE;
            }
            else if (_drag == DragMode.Panel || _drag == DragMode.Item)
            {
                wanted = Cursors.SizeAll;
            }
            else if (_hoverGrip >= 0)
            {
                // 拖右下角缩放，Windows 的标准指针就是 NWSE（左上—右下斜双箭头）
                wanted = Cursors.SizeNWSE;
            }
            else if (_hoverItem >= 0)
            {
                wanted = Cursors.Hand;
            }
            else
            {
                wanted = Cursors.Default;
            }

            if (Cursor != wanted) Cursor = wanted;
        }

        // ============================================================
        //  右键菜单
        // ============================================================

        private void ShowContextMenu(Point location)
        {
            int gi = HitGroupBody(location);

            var menu = new ContextMenuStrip();

            if (gi >= 0)
            {
                FenceBox box = _layout.Groups[gi];

                menu.Items.Add("重命名当前分类", null, delegate { RenameGroup(gi); });
                menu.Items.Add("新建分类", null, delegate { AddTab(gi); });

                // 按分类规则把错位条目归回自己的 tab（exe 回「程序」这类），
                // 组合结构不动，文件也不动
                menu.Items.Add("重新归类此盒", null, delegate
                {
                    int moved = box.ReclassifyInPlace(new Classifier());
                    MarkDirty(); Render();
                    if (moved > 0) AutoFitPanel(gi);
                });

                // 布局：大图标 / 小图标 / 列表，逐盒独立设置
                AddViewMenu(menu, gi);

                menu.Items.Add("折叠 / 展开", null, delegate
                {
                    _layout.Groups[gi].Collapsed = !_layout.Groups[gi].Collapsed;
                    MarkDirty(); Render();
                });

                // 组合 / 合并到其他收纳盒（也可以直接把标题栏拖到目标盒上）
                if (_layout.Groups.Count > 1)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    AddCombineMenu(menu, gi);
                    AddMergeMenu(menu, gi);
                }

                if (box.Tabs.Count > 1)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("解散格子组（拆成 " + box.Tabs.Count + " 个独立收纳盒）",
                        null, delegate { DissolveGroup(gi); });
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("拆出当前分类为独立收纳盒",
                        null, delegate { DetachTab(gi, box.ActiveTab); });
                    menu.Items.Add("解散当前分类",
                        null, delegate { RemoveTab(gi, box.ActiveTab); });
                }

                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("解散收纳盒", null, delegate { RemoveGroup(gi); });
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add("新建收纳盒", null, delegate { AddGroup(location); });
            menu.Items.Add("重新归类全部盒子（保持组合）", null, delegate
            {
                if (_layout.ReclassifyAll(new Classifier()) > 0)
                {
                    MarkDirty();
                    ResizeAllToContent();
                    Render();
                }
            });
            menu.Items.Add("按分类重新分组", null, delegate { RebuildFromDesktop(); });
            menu.Items.Add("清理已失效条目", null, delegate { PruneMissing(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("保存布局  (Ctrl+S)", null, delegate { SaveIfDirty(); });

            menu.Show(this, location);
        }

        /// <summary>右键菜单里的「布局」子菜单：大图标 / 小图标 / 列表。</summary>
        private void AddViewMenu(ContextMenuStrip menu, int gi)
        {
            if (menu == null) return;
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            var sub = new ToolStripMenuItem("布局");

            AddViewItem(sub, box, gi, "大图标", BoxViewMode.LargeIcons);
            AddViewItem(sub, box, gi, "小图标", BoxViewMode.SmallIcons);
            AddViewItem(sub, box, gi, "列表", BoxViewMode.List);

            menu.Items.Add(sub);
        }

        private void AddViewItem(ToolStripMenuItem parent, FenceBox box, int gi,
                                 string text, BoxViewMode mode)
        {
            var item = new ToolStripMenuItem(text);
            item.Checked = (box.ViewMode == mode);
            item.Click += delegate
            {
                if (box.ViewMode == mode) return;

                box.ViewMode = mode;
                // 换布局后单元格尺寸变了，面板要重新收紧
                AutoFitPanel(gi);
                MarkDirty();
                Render();
            };
            parent.DropDownItems.Add(item);
        }

        /// <summary>
        /// 「组合到…」子菜单：把源盒整体并入目标，成为目标的一个新分类（标签页）。
        ///
        /// 这正是参考实现的行为 —— 组合成组，各分类各归各的，而不是把条目混在一起。
        /// </summary>
        private void AddCombineMenu(ContextMenuStrip menu, int gi)
        {
            if (menu == null) return;
            if (gi < 0 || gi >= _layout.Groups.Count) return;
            if (_layout.Groups.Count < 2) return;

            var sub = new ToolStripMenuItem("组合到…（按分类变标签页）");

            for (int i = 0; i < _layout.Groups.Count; i++)
            {
                if (i == gi) continue;

                // 闭包捕获：for 变量是共享的，必须复制一份
                int target = i;

                string label = _layout.Groups[i].Title;
                if (string.IsNullOrEmpty(label)) label = "(未命名)";
                label = label.Replace("&", "&&");

                sub.DropDownItems.Add(
                    label + "   (" + _layout.Groups[i].Tabs.Count + " 个分类)",
                    null, delegate { CombineAsGroup(gi, target); });
            }

            menu.Items.Add(sub);
        }

        /// <summary>
        /// 「全部并入一个分类」子菜单 —— 把源盒的条目并进目标的一个分类里。
        ///
        /// 与「组合」的区别：组合保留各分类成为标签页；这个则把条目混在一起。
        /// 只调整归属，不删除任何磁盘文件。
        /// </summary>
        private void AddMergeMenu(ContextMenuStrip menu, int gi)
        {
            if (menu == null) return;
            if (gi < 0 || gi >= _layout.Groups.Count) return;
            if (_layout.Groups.Count < 2) return;

            var sub = new ToolStripMenuItem("全部并入一个分类");

            for (int i = 0; i < _layout.Groups.Count; i++)
            {
                if (i == gi) continue;

                int target = i;

                string label = _layout.Groups[i].Title;
                if (string.IsNullOrEmpty(label)) label = "(未命名)";
                label = label.Replace("&", "&&");

                sub.DropDownItems.Add(
                    label + "   (" + _layout.Groups[i].Items.Count + " 项)",
                    null, delegate { MergeGroups(gi, target); });
            }

            menu.Items.Add(sub);
        }

        /// <summary>右键某个分类标签：切换 / 重命名 / 拆出 / 解散。</summary>
        private void ShowTabMenu(int gi, int tabIndex, Point location)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            int t = (tabIndex >= 0) ? tabIndex : box.ActiveTab;
            if (t < 0 || t >= box.Tabs.Count) return;

            var menu = new ContextMenuStrip();

            if (tabIndex >= 0 && tabIndex != box.ActiveTab)
            {
                menu.Items.Add("切换到此分类", null, delegate { SwitchTab(gi, t); });
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add("重命名此分类", null, delegate { RenameTab(gi, t); });
            menu.Items.Add("新建分类", null, delegate { AddTab(gi); });

            if (box.Tabs.Count > 1)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("拆出为独立收纳盒", null, delegate { DetachTab(gi, t); });
                menu.Items.Add("解散此分类", null, delegate { RemoveTab(gi, t); });
            }

            menu.Show(this, location);
        }

        /// <summary>分类太多、标签退化成紧凑块时，弹出完整列表让用户选。</summary>
        private void ShowTabList(int gi, Point location)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            var menu = new ContextMenuStrip();

            for (int i = 0; i < box.Tabs.Count; i++)
            {
                int t = i;

                string title = box.Tabs[i].Title;
                if (string.IsNullOrEmpty(title)) title = "(未命名)";
                title = title.Replace("&", "&&");

                var item = new ToolStripMenuItem((i + 1) + ". " + title);
                item.Checked = (i == box.ActiveTab);
                item.Click += delegate { SwitchTab(gi, t); };
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("新建分类", null, delegate { AddTab(gi); });

            menu.Show(this, location);
        }

        /// <summary>重命名一个分类。</summary>
        private void RenameTab(int gi, int tabIndex)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            FenceBox box = _layout.Groups[gi];
            if (tabIndex < 0 || tabIndex >= box.Tabs.Count) return;

            using (var dlg = new RenameDialog(box.Tabs[tabIndex].Title))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string t = dlg.ValueText;
                if (string.IsNullOrEmpty(t)) return;

                box.Tabs[tabIndex].Title = t;
                MarkDirty();
                Render();
            }
        }

        private void RenameGroup(int gi)
        {
            string current = _layout.Groups[gi].Title;

            using (var dlg = new RenameDialog(current))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string t = dlg.ValueText;
                    if (!string.IsNullOrEmpty(t))
                    {
                        _layout.Groups[gi].Title = t;
                        MarkDirty();
                        Render();
                    }
                }
            }
        }

        private void AddGroup(Point location)
        {
            var box = new FenceBox();
            box.Title = "新分组";
            box.Bounds = new Rectangle(
                ToScreenPoint(location).X,
                ToScreenPoint(location).Y,
                240, 140);

            _layout.Groups.Add(box);
            MarkDirty();
            Render();
        }

        private void RemoveGroup(int gi)
        {
            if (gi < 0 || gi >= _layout.Groups.Count) return;

            var answer = MessageBox.Show(
                "删除分组「" + _layout.Groups[gi].Title + "」？\n\n" +
                "组内图标不会被删除，只会从覆盖层移除（可在「按分类重新分组」中恢复）。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            _layout.Groups.RemoveAt(gi);
            MarkDirty();
            Render();
        }

        private void RebuildFromDesktop()
        {
            var answer = MessageBox.Show(
                "将按文件类型重新分组，当前的自定义分组与位置会被覆盖。\n\n继续？",
                "重新分组", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes) return;

            GroupLayout rebuilt = GroupLayout.BuildDefault();
            _layout.Groups.Clear();
            _layout.Groups.AddRange(rebuilt.Groups);

            MarkDirty();
            Render();
        }

        private void PruneMissing()
        {
            int n = _layout.PruneMissing();
            MarkDirty();
            Render();

            MessageBox.Show("已清理 " + n + " 个失效条目。", "完成");
        }

        // ============================================================
        //  键盘
        // ============================================================

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.Control && e.KeyCode == Keys.S) { SaveIfDirty(); return; }
            if (e.KeyCode == Keys.F5) { Render(); return; }

            // Ctrl+Tab / Ctrl+Shift+Tab：循环切换鼠标所在收纳盒的分类。
            // 对应参考实现的 "cyclic Ctrl+Tab shortcut"。
            if (e.Control && e.KeyCode == Keys.Tab)
            {
                int gi = _hoverGroup;
                if (gi >= 0 && gi < _layout.Groups.Count)
                {
                    CycleTab(gi, e.Shift ? -1 : 1);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hoverTimer != null) { _hoverTimer.Dispose(); _hoverTimer = null; }
                if (_saveTimer != null) { _saveTimer.Dispose(); _saveTimer = null; }
                if (_renderer != null) _renderer.Dispose();
                if (_icons != null) _icons.Dispose();

                ReleaseBlitResources();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>极简重命名对话框（不依赖任何外部 UI 库）。</summary>
    internal sealed class RenameDialog : Form
    {
        private readonly TextBox _text;

        public string ValueText { get { return _text.Text; } }

        public RenameDialog(string initial)
        {
            Text = "重命名分组";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(320, 108);
            Font = new Font("微软雅黑", 9f);

            var label = new Label();
            label.Text = "分组名称：";
            label.Location = new Point(14, 18);
            label.AutoSize = true;

            _text = new TextBox();
            _text.Text = initial ?? string.Empty;
            _text.Location = new Point(14, 42);
            _text.Width = 292;

            var ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(146, 74);
            ok.Width = 76;

            var cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = new Point(230, 74);
            cancel.Width = 76;

            Controls.Add(label);
            Controls.Add(_text);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}