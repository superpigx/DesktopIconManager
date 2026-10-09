using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 托盘图标与菜单。
    ///
    /// 桌面整理工具应当常驻后台，不该一直挂着控制台窗口。
    /// 本类提供托盘入口，所有操作走事件回传给宿主窗体。
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private readonly Icon _icon;
        private bool _disposed;

        /// <summary>请求显示 / 隐藏分组面板。</summary>
        public event EventHandler TogglePanels;

        /// <summary>请求按文件类型重新分组。</summary>
        public event EventHandler Relayout;

        /// <summary>请求保存布局。</summary>
        public event EventHandler SaveLayout;

        /// <summary>请求退出。</summary>
        public event EventHandler ExitRequested;

        /// <summary>请求恢复系统桌面图标。</summary>
        public event EventHandler RestoreDesktopIcons;

        /// <summary>请求切换图标锁定（防误拖）。</summary>
        public event EventHandler ToggleLock;

        /// <summary>请求整理对齐（面板收紧到内容尺寸）。</summary>
        public event EventHandler TidyAll;

        /// <summary>请求切换拖动吸附。</summary>
        public event EventHandler ToggleSnap;

        /// <summary>请求打开设置窗口。</summary>
        public event EventHandler OpenSettings;

        /// <summary>托盘「桌面助手 ▸ 双击隐藏桌面图标」：立即切换一次。</summary>
        public event EventHandler ToggleDesktopIcons;

        /// <summary>托盘「桌面助手 ▸ 从快捷隐藏中排除」勾选变化。
        /// 参数为空表示「清空所有排除」。</summary>
        public event EventHandler ExcludeChanged;

        private ToolStripMenuItem _lockItem;
        private ToolStripMenuItem _snapItem;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public TrayHost(string themeName)
        {
            _icon = BuildIcon();

            _menu = new ContextMenuStrip();
            _menu.Font = new Font("微软雅黑", 9f);

            _menu.Items.Add("显示 / 隐藏面板", null, delegate { Raise(TogglePanels); });

            _lockItem = new ToolStripMenuItem("锁定图标位置");
            _lockItem.CheckOnClick = true;
            _lockItem.CheckedChanged += delegate { Raise(ToggleLock); };
            _menu.Items.Add(_lockItem);

            _snapItem = new ToolStripMenuItem("拖动吸附对齐");
            _snapItem.CheckOnClick = true;
            _snapItem.Checked = true;      // 与 SnapEngine 默认值一致
            _snapItem.CheckedChanged += delegate { Raise(ToggleSnap); };
            _menu.Items.Add(_snapItem);

            _menu.Items.Add("整理对齐", null, delegate { Raise(TidyAll); });

            // ---- 桌面助手 ▸（对齐参考实现的托盘菜单）----
            var assistant = new ToolStripMenuItem("桌面助手");
            assistant.DropDownItems.Add("双击隐藏桌面图标", null,
                delegate { Raise(ToggleDesktopIcons); });
            var miExclude = new ToolStripMenuItem("从快捷隐藏中排除");
            miExclude.DropDownOpening += RebuildExcludeMenu;
            assistant.DropDownItems.Add(miExclude);
            assistant.DropDownItems.Add(new ToolStripSeparator());
            assistant.DropDownItems.Add("恢复初始桌面", null, delegate { Raise(Relayout); });
            _menu.Items.Add(assistant);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("设置…", null, delegate { Raise(OpenSettings); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("按分类重新分组", null, delegate { Raise(Relayout); });
            _menu.Items.Add("保存布局", null, delegate { Raise(SaveLayout); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("恢复系统桌面图标", null, delegate { Raise(RestoreDesktopIcons); });
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("退出并还原桌面", null, delegate { Raise(ExitRequested); });

            _notify = new NotifyIcon();
            _notify.Icon = _icon;
            _notify.Text = "桌面图标管理器 · " + themeName + " 主题";
            _notify.ContextMenuStrip = _menu;
            _notify.Visible = true;
            _notify.DoubleClick += delegate { Raise(TogglePanels); };
        }

        /// <summary>
        /// 打开「从快捷隐藏中排除」时重建菜单项并同步勾选态。
        ///
        /// 勾选态必须每次打开都重读 —— 收纳盒可能被增删，
        /// 缓存的菜单项会显示过时的内容。
        /// </summary>
        private void RebuildExcludeMenu(object sender, EventArgs e)
        {
            var item = sender as ToolStripMenuItem;
            if (item == null) return;

            string[] titles = QueryBoxTitles();
            bool[] states = QueryExcludeStates();

            item.DropDownItems.Clear();

            if (titles == null || titles.Length == 0)
            {
                var none = new ToolStripMenuItem("（暂无收纳盒）");
                none.Enabled = false;
                item.DropDownItems.Add(none);
                return;
            }

            for (int i = 0; i < titles.Length; i++)
            {
                int index = i;
                var mi = new ToolStripMenuItem(titles[i]);
                mi.Checked = (states != null && i < states.Length && states[i]);
                mi.Click += delegate
                {
                    RaiseWithIndex(ExcludeChanged, index, mi.Checked);
                };
                item.DropDownItems.Add(mi);
            }
        }

        /// <summary>由宿主回填当前收纳盒清单与排除状态。</summary>
        public Func<string[]> QueryBoxTitles;
        public Func<bool[]> QueryExcludeStates;

        private static void RaiseWithIndex(EventHandler handler, int index, bool value)
        {
            if (handler == null) return;

            // 复用 EventHandler 的签名不便传参，这里用闭包 +
            // 一个静态的 Action 字段桥接：直接在调用点 Invoke 更清晰，
            // 所以退化为「无参事件 + 静态当前值」由宿主读取。
            PendingExcludeIndex = index;
            PendingExcludeValue = value;
            handler(null, EventArgs.Empty);
        }

        /// <summary>最近一次「从快捷隐藏中排除」操作的盒子下标（宿主读取）。</summary>
        public static int PendingExcludeIndex = -1;

        /// <summary>最近一次「从快捷隐藏中排除」操作的目标值（宿主读取）。</summary>
        public static bool PendingExcludeValue;

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>弹一个气泡提示。</summary>
        public void Notify(string title, string text)
        {
            try
            {
                _notify.BalloonTipTitle = title;
                _notify.BalloonTipText = text;
                _notify.ShowBalloonTip(3000);
            }
            catch
            {
                // 某些系统禁用了气泡，忽略
            }
        }

        public void SetTooltip(string text)
        {
            try
            {
                // NotifyIcon.Text 上限 63 字符
                _notify.Text = text != null && text.Length > 62
                    ? text.Substring(0, 62)
                    : text;
            }
            catch { }
        }

        /// <summary>
        /// 程序化生成托盘图标：绿色圆角方块 + 白色网格。
        /// 这样不依赖外部 .ico 文件，配色也与主题一致。
        /// </summary>
        private static Icon BuildIcon()
        {
            const int size = 32;

            using (var bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    // 圆角底板
                    using (var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7))
                    using (var brush = new SolidBrush(Color.FromArgb(0x22, 0xCB, 0x64)))
                    {
                        g.FillPath(brush, path);
                    }

                    // 2×2 白色网格，呼应「分组」
                    using (var pen = new Pen(Color.White, 2.6f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;

                        int a = 9, b = 23;
                        g.DrawLine(pen, a, a, b, a);
                        g.DrawLine(pen, a, b, b, b);
                        g.DrawLine(pen, a, a, a, b);
                        g.DrawLine(pen, b, a, b, b);
                        g.DrawLine(pen, 16, 9, 16, 23);
                        g.DrawLine(pen, 9, 16, 23, 16);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _notify.Visible = false;
                _notify.Dispose();
                _menu.Dispose();
                if (_icon != null) _icon.Dispose();
            }
            catch { }
        }
    }
}