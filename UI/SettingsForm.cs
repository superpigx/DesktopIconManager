using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using DesktopIconManager.Core;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 设置窗口。
    ///
    /// 操作的是传入设置的**副本**：
    ///   · 拖动任何控件 → 立即回调 <see cref="Edit"/> 的 preview，宿主实时应用，所见即所得
    ///   · 点「确定」→ 把副本写回目标对象，由宿主落盘
    ///   · 点「取消」→ 丢弃副本，宿主按原值重新应用一遍即可回滚
    ///
    /// 不依赖任何外部 UI 库，控件全部程序化创建，配色沿用主题的灰白系。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        /// <summary>预览回调：把工作副本交给宿主即时应用。</summary>
        public delegate void PreviewHandler(AppSettings working);

        private const int RowH = 30;
        private const int LabelW = 148;
        private const int CtrlX = 170;
        private const int CtrlW = 150;

        private readonly AppSettings _work;
        private readonly PreviewHandler _preview;

        /// <summary>左侧导航项与对应内容页的映射。</summary>
        private readonly ListBox _nav;
        private readonly Panel _content;
        private readonly List<TabPage> _pages = new List<TabPage>();

        private readonly TabPage _pgLook;
        private readonly TabPage _pgLayout;
        private readonly TabPage _pgBehave;
        private readonly TabPage _pgRules;

        /// <summary>可选主题色（对齐参考实现那一排色块）。</summary>
        private static readonly string[] Palette =
        {
            "#2E7D32", "#1976D2", "#C62828", "#AD1457", "#7B1FA2", "#00838F",
            "#F9A825", "#EF6C00", "#5D4037", "#455A64", "#00BFA5", "#7CB342",
            "#D81B60", "#5E35B1", "#00897B", "#F4511E"
        };

        private RadioButton _fixedRad;
        private ComboBox _fixedCmb;

        private CheckBox _alphaCustom;
        private TrackBar _alpha;
        private Label _alphaText;

        private TextBox _extraBox;
        private TextBox _ruleView;

        private bool _loading = true;
        private int _y;

        // ---- 设置窗口自身配色（跟随所选主题的深浅）----
        // 之前是固定灰白，深色主题下开设置窗口一片刺眼的白
        private Color _cBg;      // 窗体底
        private Color _cPage;    // 页签底
        private Color _cField;   // 输入框 / 只读框底
        private Color _cFg;      // 主文字
        private Color _cFgSub;   // 次要文字
        private Color _cFgHint;  // 提示文字

        private void InitUiColors(ThemePalette ui)
        {
            bool dark = ui != null && ui.TextPrimary.GetBrightness() > 0.5f;

            if (dark)
            {
                _cBg     = Color.FromArgb(0x23, 0x26, 0x2B);
                _cPage   = Color.FromArgb(0x2A, 0x2D, 0x33);
                _cField  = Color.FromArgb(0x31, 0x35, 0x3C);
                _cFg     = Color.FromArgb(0xE4, 0xE4, 0xE4);
                _cFgSub  = Color.FromArgb(0xA8, 0xAD, 0xB5);
                _cFgHint = Color.FromArgb(0x7A, 0x80, 0x88);
            }
            else
            {
                _cBg     = Color.FromArgb(0xF4, 0xF6, 0xF8);
                _cPage   = Color.FromArgb(0xFF, 0xFF, 0xFF);
                _cField  = Color.FromArgb(0xFF, 0xFF, 0xFF);
                _cFg     = Color.FromArgb(0x20, 0x24, 0x28);
                _cFgSub  = Color.FromArgb(0x55, 0x5C, 0x63);
                _cFgHint = Color.FromArgb(0x88, 0x8F, 0x96);
            }
        }

        private SettingsForm(AppSettings work, PreviewHandler preview, ThemePalette uiTheme)
        {
            _work = work ?? new AppSettings();
            _preview = preview;
            InitUiColors(uiTheme);

            Text = "桌面图标管理器 · 设置";
            Font = new Font("微软雅黑", 9f);
            BackColor = _cBg;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 620);

            // ---- 左侧导航（对齐参考实现的「设置中心」）----
            _nav = new ListBox();
            _nav.Location = new Point(10, 10);
            _nav.Size = new Size(132, 542);
            _nav.BorderStyle = BorderStyle.FixedSingle;
            _nav.BackColor = _cBg;
            _nav.ForeColor = _cFg;
            _nav.Font = new Font("微软雅黑", 9.5f);
            _nav.Items.AddRange(new object[]
            {
                "外观设置", "桌面管理", "常规设置", "整理规则"
            });
            Controls.Add(_nav);

            // ---- 右侧内容区 ----
            _content = new Panel();
            _content.Location = new Point(150, 10);
            _content.Size = new Size(596, 542);
            _content.BackColor = _cBg;
            _content.AutoScroll = true;
            Controls.Add(_content);

            _pgLook = new TabPage("外观");
            _pgLayout = new TabPage("布局");
            _pgBehave = new TabPage("交互");
            _pgRules = new TabPage("整理规则");

            foreach (TabPage p in new[] { _pgLook, _pgLayout, _pgBehave, _pgRules })
            {
                p.BackColor = _cBg;
                p.AutoScroll = true;
                p.Dock = DockStyle.None;
                p.Location = new Point(0, 0);
                p.Size = new Size(596, 542);
                p.Visible = false;
            }

            _pages.AddRange(new[] { _pgLook, _pgLayout, _pgBehave, _pgRules });
            _content.Controls.AddRange(new Control[] { _pgLook, _pgLayout, _pgBehave, _pgRules });

            _nav.SelectedIndexChanged += delegate
            {
                int i = _nav.SelectedIndex;
                if (i < 0 || i >= _pages.Count) return;

                for (int k = 0; k < _pages.Count; k++)
                    _pages[k].Visible = (k == i);
            };
            _nav.SelectedIndex = 0;
            _pgLook.Visible = true;

            // ---- 底部按钮 ----
            var btnReset = new Button();
            btnReset.Text = "恢复默认";
            btnReset.Location = new Point(10, 566);
            btnReset.Size = new Size(88, 30);
            btnReset.Click += delegate
            {
                if (MessageBox.Show(this, "把所有设置恢复为默认值？", "恢复默认",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                _work.ResetToDefaults();
                BuildAll();
                FirePreview();
            };
            Controls.Add(btnReset);

            var btnPreview = new Button();
            btnPreview.Text = "样式预览";
            btnPreview.Location = new Point(106, 566);
            btnPreview.Size = new Size(88, 30);
            btnPreview.Click += delegate
            {
                try
                {
                    using (var pf = new PreviewForm(ThemeBinder.Build(_work)))
                    {
                        pf.ShowDialog(this);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "预览打不开：" + ex.Message, "预览",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(btnPreview);

            var btnOk = new Button();
            btnOk.Text = "确定";
            btnOk.Location = new Point(572, 566);
            btnOk.Size = new Size(84, 30);
            btnOk.DialogResult = DialogResult.OK;
            Controls.Add(btnOk);

            var btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(664, 566);
            btnCancel.Size = new Size(84, 30);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            BuildAll();
            _loading = false;
        }

        /// <summary>
        /// 弹出设置窗口。
        ///
        /// 确定时把工作副本写回 <paramref name="target"/> 并返回 true；
        /// 取消返回 false，调用方需按 target 的原值重新应用一次以回滚预览。
        /// </summary>
        public static bool Edit(IWin32Window owner, AppSettings target, PreviewHandler preview,
                                ThemePalette uiTheme)
        {
            return EditAt(owner, target, preview, uiTheme, -1);
        }

        /// <summary>
        /// 打开设置窗口并定位到指定导航页。
        ///
        /// 菜单里的「改变外观」「整理规则设定」就是靠它直达对应页 ——
        /// 让用户从任意界面一步跳到他真正想改的地方。
        /// </summary>
        /// <param name="pageIndex">导航下标；-1 表示用默认页。</param>
        public static bool EditAt(IWin32Window owner, AppSettings target, PreviewHandler preview,
                                  ThemePalette uiTheme, int pageIndex)
        {
            if (target == null) return false;

            AppSettings work = target.Clone();

            using (var f = new SettingsForm(work, preview, uiTheme))
            {
                if (pageIndex >= 0 && pageIndex < f._nav.Items.Count)
                    f._nav.SelectedIndex = pageIndex;

                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                target.CopyFrom(work);

                // 自启是系统副作用，放在「确定」而不是勾选时 ——
                // 用户点取消的话，注册表不能已经被改掉
                if (!AutoStartManager.Apply(target.AutoStart))
                {
                    MessageBox.Show(owner,
                        "开机自启写入注册表失败（可能被权限或安全软件拦截）。\r\n" +
                        "程序本体不受影响，仍可手动启动。",
                        "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return true;
            }
        }

        private void FirePreview()
        {
            if (_loading) return;
            if (_preview != null) _preview(_work);
        }

        // ============================================================
        //  页面构建
        // ============================================================

        private void BuildAll()
        {
            bool saved = _loading;
            _loading = true;

            _pgLook.Controls.Clear();
            _pgLayout.Controls.Clear();
            _pgBehave.Controls.Clear();
            _pgRules.Controls.Clear();

            BuildLookPage();
            BuildLayoutPage();
            BuildBehavePage();
            BuildRulesPage();

            _loading = saved;
        }

        private void BuildLookPage()
        {
            _y = 14;
            TabPage pg = _pgLook;

            Cmb(pg, "主题",
                new[] { "原版", "浅色", "深色", "玻璃" },
                ThemeIndex(_work.Theme),
                delegate (int i) { _work.Theme = ThemeName(i); });

            _alphaCustom = Chk(pg, "自定义面板底透明度", _work.PanelAlpha >= 0,
                delegate (bool v)
                {
                    _work.PanelAlpha = v ? _alpha.Value : -1;
                    if (_alpha != null) _alpha.Enabled = v;
                    UpdateAlphaText();
                },
                "不勾选时跟随主题：原版/浅色/深色约 60%，玻璃主题更透");

            _alpha = Trk(pg, "面板底透明度", 0, 255,
                _work.PanelAlpha >= 0 ? _work.PanelAlpha : 0x99,
                delegate (int v)
                {
                    if (_alphaCustom.Checked) _work.PanelAlpha = v;
                    UpdateAlphaText();
                });

            _alphaText = new Label();
            _alphaText.AutoSize = true;
            _alphaText.ForeColor = _cFgSub;
            _alphaText.Location = new Point(CtrlX + CtrlW + 8, _y - 22);
            pg.Controls.Add(_alphaText);

            Num(pg, "标题栏高度", 16, 64, _work.TitleHeight,
                delegate (int v) { _work.TitleHeight = v; });
            Num(pg, "标签条高度", 12, 48, _work.TabBarHeight,
                delegate (int v) { _work.TabBarHeight = v; });
            Num(pg, "面板圆角", 0, 24, _work.PanelRadius,
                delegate (int v) { _work.PanelRadius = v; },
                "主题文件里是 0（直角 + 投影浮起）");
            Num(pg, "投影深度", 0, 24, _work.ShadowDepth,
                delegate (int v) { _work.ShadowDepth = v; },
                "0 = 不投影；越大越柔和");
            Num(pg, "投影下偏移", 0, 24, _work.ShadowOffsetY,
                delegate (int v) { _work.ShadowOffsetY = v; },
                "主题的 shadow 是 5,3,5,7 —— 下边最重");
            NumF(pg, "标题字号", 6f, 24f, _work.TitleFontSize,
                delegate (float v) { _work.TitleFontSize = v; });
            NumF(pg, "标签字号", 6f, 24f, _work.LabelFontSize,
                delegate (float v) { _work.LabelFontSize = v; },
                "标签与计数徽标会按此自动略小");

            SyncAlphaEnabled();

            // ============================================================
            //  以下分组对齐参考实现「外观设置」页
            // ============================================================

            Gap(pg, "颜色");

            var clr = new ListBox();
            clr.Location = new Point(20, _y);
            clr.Size = new Size(556, 46);
            clr.DrawMode = DrawMode.OwnerDrawFixed;
            clr.ItemHeight = 22;
            clr.BorderStyle = BorderStyle.FixedSingle;
            clr.BackColor = _cField;
            clr.ForeColor = _cFg;
            clr.Items.AddRange(new object[Palette.Length]);
            _y += 56;

            int curIdx = PaletteIndexOf(_work.AccentColor);
            if (curIdx < 0) curIdx = 0;
            clr.SelectedIndex = curIdx;

            clr.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                e.DrawBackground();

                Color c;
                try { c = ColorTranslator.FromHtml(Palette[e.Index]); }
                catch { c = Color.Gray; }

                var box = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 3, 26, 16);
                using (var b = new SolidBrush(c)) e.Graphics.FillRectangle(b, box);
                using (var p = new Pen(_cFgSub)) e.Graphics.DrawRectangle(p, box);

                bool sel = (e.Index == clr.SelectedIndex);
                using (var b = new SolidBrush(sel ? _cFg : _cFgSub))
                {
                    e.Graphics.DrawString(Palette[e.Index], Font, b,
                        e.Bounds.X + 38, e.Bounds.Y + 3);
                }

                // 选中打勾 —— 与参考实现一致，光靠高亮底色不够明确
                if (sel)
                {
                    using (var p = new Pen(_cFg, 2f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        e.Graphics.DrawLines(p, new[]
                        {
                            new Point(e.Bounds.X + 544, e.Bounds.Y + 11),
                            new Point(e.Bounds.X + 548, e.Bounds.Y + 15),
                            new Point(e.Bounds.X + 556, e.Bounds.Y + 6)
                        });
                    }
                }
            };

            clr.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                _work.AccentColor = Palette[clr.SelectedIndex];
                FirePreview();
            };

            Gap(pg, "桌面字体设置");
            var fonts = new List<string>();
            fonts.Add("默认字体");
            try
            {
                foreach (FontFamily fn in FontFamily.Families)
                    fonts.Add(fn.Name);
            }
            catch { }

            Cmb(pg, "字体", fonts.ToArray(), fonts.IndexOf(
                    string.IsNullOrEmpty(_work.DesktopFont) ? "默认字体" : _work.DesktopFont),
                delegate (int i)
                {
                    _work.DesktopFont = (i <= 0) ? string.Empty : fonts[i];
                },
                "选「默认字体」表示跟随主题；改这里会同时作用于标题与图标标签");

            Gap(pg, "背景图片");
            var bgBox = new TextBox();
            bgBox.Location = new Point(CtrlX, _y - 1);
            bgBox.Size = new Size(CtrlW + 210, 22);
            bgBox.ReadOnly = true;
            bgBox.BackColor = _cField;
            bgBox.ForeColor = _cFg;
            bgBox.Text = _work.BackgroundImage ?? string.Empty;
            pg.Controls.Add(bgBox);

            var bgBtn = new Button();
            bgBtn.Text = "选择图片";
            bgBtn.Location = new Point(CtrlX + CtrlW + 216, _y - 2);
            bgBtn.Size = new Size(88, 24);
            bgBtn.Click += delegate
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择桌面背景图";
                    dlg.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;

                    _work.BackgroundImage = dlg.FileName;
                    bgBox.Text = dlg.FileName;
                    FirePreview();
                }
            };
            pg.Controls.Add(bgBtn);
            _y += RowH;

            Gap(pg, "分区标签切换");

            var grpTab = new GroupBox();
            grpTab.Text = "";
            grpTab.ForeColor = _cFg;
            grpTab.BackColor = Color.Transparent;
            grpTab.Location = new Point(20, _y - 4);
            grpTab.Size = new Size(560, 34);
            pg.Controls.Add(grpTab);

            var radClick = new RadioButton();
            radClick.AutoSize = true;
            radClick.Text = "点击切换";
            radClick.ForeColor = _cFg;
            radClick.BackColor = Color.Transparent;
            radClick.Location = new Point(16, 6);
            radClick.Checked = (_work.TabSwitch == TabSwitchMode.Click);
            radClick.CheckedChanged += delegate
            {
                if (_loading) return;
                if (!radClick.Checked) return;
                _work.TabSwitch = TabSwitchMode.Click;
                FirePreview();
            };
            grpTab.Controls.Add(radClick);

            var radHover = new RadioButton();
            radHover.AutoSize = true;
            radHover.Text = "悬停切换";
            radHover.ForeColor = _cFg;
            radHover.BackColor = Color.Transparent;
            radHover.Location = new Point(140, 6);
            radHover.Checked = (_work.TabSwitch == TabSwitchMode.Hover);
            radHover.CheckedChanged += delegate
            {
                if (_loading) return;
                if (!radHover.Checked) return;
                _work.TabSwitch = TabSwitchMode.Hover;
                FirePreview();
            };
            grpTab.Controls.Add(radHover);
            _y += 40;

            Gap(pg, "分区菜单标签显示");

            var grpLabel = new GroupBox();
            grpLabel.Text = "";
            grpLabel.ForeColor = _cFg;
            grpLabel.BackColor = Color.Transparent;
            grpLabel.Location = new Point(20, _y - 4);
            grpLabel.Size = new Size(560, 34);
            pg.Controls.Add(grpLabel);

            var radAlways = new RadioButton();
            radAlways.AutoSize = true;
            radAlways.Text = "始终显示";
            radAlways.ForeColor = _cFg;
            radAlways.BackColor = Color.Transparent;
            radAlways.Location = new Point(16, 6);
            radAlways.Checked = _work.TabLabelAlways;
            radAlways.CheckedChanged += delegate
            {
                if (_loading || !radAlways.Checked) return;
                _work.TabLabelAlways = true;
                FirePreview();
            };
            grpLabel.Controls.Add(radAlways);

            var radOnHover = new RadioButton();
            radOnHover.AutoSize = true;
            radOnHover.Text = "悬停显示";
            radOnHover.ForeColor = _cFg;
            radOnHover.BackColor = Color.Transparent;
            radOnHover.Location = new Point(140, 6);
            radOnHover.Checked = !_work.TabLabelAlways;
            radOnHover.CheckedChanged += delegate
            {
                if (_loading || !radOnHover.Checked) return;
                _work.TabLabelAlways = false;
                FirePreview();
            };
            grpLabel.Controls.Add(radOnHover);
            _y += 40;

            Gap(pg, "其他");

            Chk(pg, "盒子显示边框", _work.PanelBorder,
                delegate (bool v) { _work.PanelBorder = v; });

            Chk(pg, "盒子使用圆角", _work.PanelRounded,
                delegate (bool v)
                {
                    _work.PanelRounded = v;
                    _work.PanelRadius = v ? Math.Max(6, _work.PanelRadius) : 0;
                });

            Chk(pg, "在快捷方式图标上显示箭头", _work.ShortcutArrow,
                delegate (bool v) { _work.ShortcutArrow = v; });

            Chk(pg, "收起后，鼠标移动到标题自动展开", _work.AutoExpandOnHover,
                delegate (bool v) { _work.AutoExpandOnHover = v; });
        }

        /// <summary>可用的分区名（= 全部分类名 + 几个常用自定义名）。</summary>
        private static string[] PartitionNames()
        {
            var names = new List<string>();
            try
            {
                var probe = new Classifier(null, null);
                foreach (CategoryRule r in probe.Rules) names.Add(r.DisplayName);
            }
            catch { }

            names.Add("未归类");
            names.Add("新建分类");
            return names.ToArray();
        }

        /// <summary>把 #RRGGBB 找回调色板下标；找不到（跟随主题）返回 -1。</summary>
        private static int PaletteIndexOf(string html)
        {
            if (string.IsNullOrEmpty(html)) return -1;

            for (int i = 0; i < Palette.Length; i++)
                if (string.Equals(Palette[i], html, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private void BuildLayoutPage()
        {
            _y = 14;
            TabPage pg = _pgLayout;

            Gap(pg, "大图标");
            Num(pg, "图标边长", 16, 128, _work.IconSize,
                delegate (int v) { _work.IconSize = v; });
            Num(pg, "单元格宽", 40, 200, _work.ItemWidth,
                delegate (int v) { _work.ItemWidth = v; });
            Num(pg, "单元格高", 40, 220, _work.ItemHeight,
                delegate (int v) { _work.ItemHeight = v; });

            Gap(pg, "小图标");
            Num(pg, "图标边长", 16, 96, _work.SmallIconSize,
                delegate (int v) { _work.SmallIconSize = v; });
            Num(pg, "单元格高", 28, 160, _work.SmallItemHeight,
                delegate (int v) { _work.SmallItemHeight = v; });

            Gap(pg, "列表");
            Num(pg, "行高", 14, 60, _work.ListRowHeight,
                delegate (int v) { _work.ListRowHeight = v; });
            Num(pg, "图标边长", 8, 48, _work.ListIconSize,
                delegate (int v) { _work.ListIconSize = v; });

            Gap(pg, "通用");
            Num(pg, "内容边距", 0, 32, _work.BodyPadding,
                delegate (int v) { _work.BodyPadding = v; });
            Num(pg, "面板间距", 0, 64, _work.PanelGap,
                delegate (int v) { _work.PanelGap = v; },
                "左侧多出的空档会显示对齐参考线");
            Num(pg, "自动分组每行列数", 1, 16, _work.MaxColumns,
                delegate (int v) { _work.MaxColumns = v; },
                "仅影响「按分类重新分组」的初始排版");
        }

        private void BuildBehavePage()
        {
            _y = 14;
            TabPage pg = _pgBehave;

            Chk(pg, "拖动吸附对齐", _work.SnapEnabled,
                delegate (bool v) { _work.SnapEnabled = v; },
                "移动与缩放都会吸附；吸住后要多拖一段才松开");

            Num(pg, "吸附间距", 0, 64, _work.SnapGap,
                delegate (int v) { _work.SnapGap = v; },
                "两个面板紧贴时留的空隙");

            Num(pg, "吸附阈值", 2, 64, _work.SnapThreshold,
                delegate (int v) { _work.SnapThreshold = v; },
                "距离多近才吸住。太大容易「甩不掉」");

            Gap(pg, "行为");
            Chk(pg, "自动收紧面板", _work.AutoFit,
                delegate (bool v) { _work.AutoFit = v; },
                "条目增删后把面板收到刚好容纳内容");

            Chk(pg, "启动时锁定图标位置", _work.LockIcons,
                delegate (bool v) { _work.LockIcons = v; },
                "锁定后禁止拖动，双击打开与右键菜单照常");

            Chk(pg, "纳入公用桌面图标", _work.IncludePublicDesktop,
                delegate (bool v) { _work.IncludePublicDesktop = v; },
                "C:\\Users\\Public\\Desktop —— 关机后重进这个选项才生效");

            Chk(pg, "收纳未归类的桌面图标", _work.AdoptOrphanIcons,
                delegate (bool v) { _work.AdoptOrphanIcons = v; },
                "把不在任何收纳盒里的图标收进「未收纳」盒。建议开启 ——\r\n" +
                "接管桌面后系统图标是隐藏的，漏掉的图标会变得既看不到也点不到");

            Gap(pg, "系统");
            Chk(pg, "开机自动启动覆盖层", _work.AutoStart,
                delegate (bool v) { _work.AutoStart = v; },
                "写注册表 HKCU Run（不需要管理员），点「确定」后生效；\r\n" +
                "开机后直接进桌面整理模式，无控制台窗口");
        }

        private void BuildRulesPage()
        {
            TabPage pg = _pgRules;
            _y = 14;

            // ---- 顶部：程序启动时自动整理 ----
            Chk(pg, "程序启动时自动整理", _work.AutoOrganizeOnStartup,
                delegate (bool v)
                {
                    _work.AutoOrganizeOnStartup = v;
                    if (_fixedRad != null) _fixedRad.Enabled = v;
                });

            RadioButton radRules = null;
            radRules = Rad(pg, "按照【整理规则】，实时整理到各个分区",
                _work.OrganizeMode == AutoOrganizeMode.ByRules,
                delegate
                {
                    if (_loading || radRules == null || !radRules.Checked) return;
                    _work.OrganizeMode = AutoOrganizeMode.ByRules;
                    if (_fixedCmb != null) _fixedCmb.Enabled = false;
                    FirePreview();
                });

            // 「整理到固定分区 + 下拉」：截图里这两个控件同行
            int saveY = _y;
            _y = saveY - 26;

            var fixedBox = new Panel();
            fixedBox.Location = new Point(20, _y);
            fixedBox.Size = new Size(566, 26);
            fixedBox.BackColor = Color.Transparent;
            pg.Controls.Add(fixedBox);

            var radFixed = new RadioButton();
            radFixed.AutoSize = true;
            radFixed.Text = "整理到固定分区";
            radFixed.ForeColor = _cFg;
            radFixed.BackColor = Color.Transparent;
            radFixed.Location = new Point(0, 4);
            radFixed.Checked = (_work.OrganizeMode == AutoOrganizeMode.FixedPartition);
            radFixed.Enabled = _work.AutoOrganizeOnStartup;
            radFixed.CheckedChanged += delegate
            {
                if (_loading || !radFixed.Checked) return;
                _work.OrganizeMode = AutoOrganizeMode.FixedPartition;
                if (_fixedCmb != null) _fixedCmb.Enabled = true;
                FirePreview();
            };
            fixedBox.Controls.Add(radFixed);
            _fixedRad = radFixed;

            var cmbFixed = new ComboBox();
            cmbFixed.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFixed.Location = new Point(110, 2);
            cmbFixed.Size = new Size(180, 22);
            cmbFixed.ForeColor = _cFg;
            cmbFixed.BackColor = _cField;
            string[] parts = PartitionNames();
            cmbFixed.Items.AddRange(parts);
            int fi = Array.IndexOf(parts, _work.FixedPartition ?? string.Empty);
            cmbFixed.SelectedIndex = fi >= 0 ? fi : 0;
            cmbFixed.Enabled = (_work.OrganizeMode == AutoOrganizeMode.FixedPartition);
            cmbFixed.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                _work.FixedPartition = cmbFixed.SelectedItem as string;
                FirePreview();
            };
            fixedBox.Controls.Add(cmbFixed);
            _fixedCmb = cmbFixed;

            _y = saveY;
            Gap(pg, "整理规则");

            // ---- 规则表格（仿原版「整理规则」：类型 / 后缀 / 启用开关）----
            // 勾选 = 启用；取消勾选后该类文件落「未归类」
            var grid = new ListView();
            grid.Location = new Point(14, _y);
            grid.Size = new Size(568, 190);
            grid.View = View.Details;
            grid.FullRowSelect = true;
            grid.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            grid.CheckBoxes = true;
            grid.HideSelection = true;
            grid.BackColor = _cField;
            grid.ForeColor = _cFg;
            grid.BorderStyle = BorderStyle.FixedSingle;

            // 列顺序对齐参考实现：类型 / 包含后缀 / 选择桌面分区 /（勾选=启用）
            grid.Columns.Add("类型", 88);
            grid.Columns.Add("包含后缀", 268);
            grid.Columns.Add("选择桌面分区", 120);
            pg.Controls.Add(grid);

            _y += 198;

            var probe = new Classifier(_work.ExtraRuleText, null);
            foreach (CategoryRule r in probe.Rules)
            {
                var exts = new List<string>(r.Extensions);
                exts.Sort(StringComparer.OrdinalIgnoreCase);

                // 先设状态再入列 —— 入列后 ItemChecked 才会因用户点击触发
                var it = new ListViewItem(r.DisplayName);

                // 后缀列只展示前若干个 —— 全列出来会把行撑得没法看
                string all = string.Join(" ", exts.ToArray());
                it.SubItems.Add(all.Length > 46 ? all.Substring(0, 45) + "…" : all);
                it.SubItems.Add(_work.GetPartitionOf(r.DisplayName) ?? r.DisplayName);
                it.Checked = _work.DisabledCategories == null
                    || !_work.DisabledCategories.Contains(r.DisplayName);
                it.Tag = r.DisplayName;
                grid.Items.Add(it);
            }

            // 「选择桌面分区」列：双击（或点击）弹出下拉，改完立刻生效
            grid.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;

                ListViewItem item = grid.GetItemAt(e.X, e.Y);
                if (item == null) return;

                // 第 3 列（索引 2）才是分区列
                int col = -1;
                for (int i = 0; i < item.SubItems.Count; i++)
                {
                    if (e.X >= item.SubItems[i].Bounds.Left &&
                        e.X < item.SubItems[i].Bounds.Right)
                    {
                        col = i;
                        break;
                    }
                }
                if (col != 2) return;

                string category = item.Tag as string;
                if (string.IsNullOrEmpty(category)) return;

                var menu = new ContextMenuStrip();
                foreach (string pn in PartitionNames())
                {
                    string captured = pn;
                    var mi = new ToolStripMenuItem(pn);
                    mi.Checked = (string.Equals(
                        _work.GetPartitionOf(category) ?? category, pn,
                        StringComparison.OrdinalIgnoreCase));
                    mi.Click += delegate
                    {
                        _work.EnsurePartitionMap();

                        // 覆盖同分类的旧映射
                        for (int i = _work.PartitionMap.Count - 1; i >= 0; i--)
                        {
                            string line = _work.PartitionMap[i];
                            if (string.IsNullOrEmpty(line)) continue;

                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;

                            if (string.Equals(line.Substring(0, eq).Trim(), category,
                                              StringComparison.OrdinalIgnoreCase))
                                _work.PartitionMap.RemoveAt(i);
                        }

                        _work.PartitionMap.Add(category + "=" + captured);
                        item.SubItems[2].Text = captured;
                        FirePreview();
                    };
                    menu.Items.Add(mi);
                }
                menu.Show(grid, new Point(e.X, e.Y + 1));
            };

            grid.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (_loading) return;   // 填充阶段不是用户操作

                string name = e.Item.Tag as string;
                if (string.IsNullOrEmpty(name)) return;

                _work.EnsureDisabledCategories();

                // 勾上 = 启用 = 移出禁用表
                if (e.Item.Checked) _work.DisabledCategories.Remove(name);
                else if (!_work.DisabledCategories.Contains(name))
                    _work.DisabledCategories.Add(name);

                RefreshRuleView();
                FirePreview();
            };

            var tip = new Label();
            tip.AutoSize = false;
            tip.Location = new Point(14, _y + 4);
            tip.Size = new Size(568, 34);
            tip.ForeColor = _cFgHint;
            tip.Text = "内置规则不可删（避免误操作把基础分类搞坏），但可以往下追加扩展名。\r\n"
                     + "每行一条，格式：分类名=.扩展名,.扩展名（也可以只写 md 不带点）";
            pg.Controls.Add(tip);

            _extraBox = new TextBox();
            _extraBox.Multiline = true;
            _extraBox.ScrollBars = ScrollBars.Vertical;
            _extraBox.Location = new Point(14, _y + 42);
            _extraBox.Size = new Size(568, 110);
            _extraBox.BackColor = _cField;
            _extraBox.ForeColor = _cFg;
            _extraBox.Font = new Font("Consolas", 9f);
            _extraBox.Text = ToCrlf(_work.ExtraRuleText);
            _extraBox.TextChanged += delegate
            {
                _work.ExtraRuleText = _extraBox.Text;
                RefreshRuleView();
                FirePreview();
            };
            pg.Controls.Add(_extraBox);

            var lbl = new Label();
            lbl.AutoSize = true;
            lbl.Location = new Point(14, _y + 164);
            lbl.ForeColor = _cFg;
            lbl.Text = "当前生效的完整规则（已禁用的分类会标注）：";
            pg.Controls.Add(lbl);

            _ruleView = new TextBox();
            _ruleView.Multiline = true;
            _ruleView.ReadOnly = true;
            _ruleView.ScrollBars = ScrollBars.Vertical;
            _ruleView.BackColor = _cField;
            _ruleView.Location = new Point(14, _y + 186);
            _ruleView.Size = new Size(568, 126);
            _ruleView.Font = new Font("Consolas", 8.5f);
            pg.Controls.Add(_ruleView);

            RefreshRuleView();
        }

        private void RefreshRuleView()
        {
            if (_ruleView == null) return;

            var sb = new StringBuilder();
            try
            {
                var classifier = new Classifier(_work.ExtraRuleText, _work.DisabledCategories);

                foreach (CategoryRule r in classifier.Rules)
                {
                    var exts = new List<string>(r.Extensions);
                    exts.Sort(StringComparer.OrdinalIgnoreCase);

                    sb.Append(r.DisplayName)
                      .Append("（").Append(exts.Count).Append(" 个）");
                    if (classifier.IsDisabled(r.DisplayName))
                        sb.Append("   【已禁用 → 落「未归类」】");
                    sb.AppendLine();
                    sb.Append("    ")
                      .Append(string.Join(" ", exts.ToArray()))
                      .AppendLine()
                      .AppendLine();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("规则读取失败：" + ex.Message);
            }

            _ruleView.Text = ToCrlf(sb.ToString());
        }

        /// <summary>
        /// 把任意换行统一成 CRLF。
        ///
        /// 不能直接 Replace("\n", "\r\n") —— 若是已有的 CRLF 会被变成 CRCRLF，
        /// 多行文本框里就会显示成空行。
        /// </summary>
        private static string ToCrlf(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }

        // ============================================================
        //  行控件构造
        // ============================================================

        private Label RowLabel(TabPage pg, string text)
        {
            var l = new Label();
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Location = new Point(14, _y);
            l.Size = new Size(LabelW, 22);
            l.Text = text;
            pg.Controls.Add(l);
            return l;
        }

        private void Gap(TabPage pg, string title)
        {
            _y += 8;
            var l = new Label();
            l.AutoSize = true;
            l.Location = new Point(14, _y);
            l.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            l.ForeColor = _cFg;
            l.Text = title;
            pg.Controls.Add(l);
            _y += 24;
        }

        private void Hint(TabPage pg, string text)
        {
            // 按实际行数给高度 —— 固定 18px 会把第二行裁掉
            int lines = 1;
            if (!string.IsNullOrEmpty(text))
                lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;

            int h = 16 * lines + 2;

            var l = new Label();
            l.AutoSize = false;
            l.Location = new Point(14, _y);
            l.Size = new Size(592, h);
            l.ForeColor = _cFgHint;
            l.Text = text;
            pg.Controls.Add(l);
            _y += h;
        }

        private ComboBox Cmb(TabPage pg, string label, string[] items, int index,
                             Action<int> set, string hint = null)
        {
            RowLabel(pg, label);

            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Location = new Point(CtrlX, _y - 1);
            c.Size = new Size(CtrlW, 22);
            c.Items.AddRange(items);
            c.SelectedIndex = (index >= 0 && index < items.Length) ? index : 0;
            c.SelectedIndexChanged += delegate
            {
                if (_loading) return;
                set(c.SelectedIndex);
                FirePreview();
            };
            pg.Controls.Add(c);

            _y += RowH;
            if (hint != null) Hint(pg, hint);
            return c;
        }

        /// <summary>
        /// 一行单选圆点（截图里的 ◉ / ○ 那种）。
        /// 用 RadioButton 控件而不是自绘，才能吃到系统的键盘操作与焦点行为。
        /// </summary>
        private RadioButton Rad(TabPage pg, string text, bool value, Action onClick)
        {
            var r = new RadioButton();
            r.AutoSize = true;
            r.Text = text;
            r.Checked = value;
            r.ForeColor = _cFg;
            r.BackColor = Color.Transparent;
            r.Location = new Point(20, _y);

            if (onClick != null) r.CheckedChanged += delegate { onClick(); };

            pg.Controls.Add(r);
            _y += 26;
            return r;
        }

        private CheckBox Chk(TabPage pg, string label, bool value,
                             Action<bool> set, string hint = null)
        {
            var c = new CheckBox();
            c.AutoSize = true;
            c.Location = new Point(14, _y + 2);
            c.Text = label;
            c.Checked = value;
            c.CheckedChanged += delegate
            {
                if (_loading) return;
                set(c.Checked);
                FirePreview();
            };
            pg.Controls.Add(c);

            _y += RowH - 4;
            if (hint != null) Hint(pg, hint);
            return c;
        }

        private NumericUpDown Num(TabPage pg, string label, int min, int max, int value,
                                  Action<int> set, string hint = null)
        {
            RowLabel(pg, label);

            var n = new NumericUpDown();
            n.Location = new Point(CtrlX, _y - 1);
            n.Size = new Size(72, 22);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = ClampInt(value, min, max);
            n.ValueChanged += delegate
            {
                if (_loading) return;
                set((int)n.Value);
                FirePreview();
            };
            pg.Controls.Add(n);

            _y += RowH;
            if (hint != null) Hint(pg, hint);
            return n;
        }

        private NumericUpDown NumF(TabPage pg, string label, float min, float max, float value,
                                   Action<float> set, string hint = null)
        {
            RowLabel(pg, label);

            var n = new NumericUpDown();
            n.Location = new Point(CtrlX, _y - 1);
            n.Size = new Size(72, 22);
            n.DecimalPlaces = 1;
            n.Increment = 0.5m;
            n.Minimum = (decimal)min;
            n.Maximum = (decimal)max;
            n.Value = (decimal)ClampFloat(value, min, max);
            n.ValueChanged += delegate
            {
                if (_loading) return;
                set((float)n.Value);
                FirePreview();
            };
            pg.Controls.Add(n);

            _y += RowH;
            if (hint != null) Hint(pg, hint);
            return n;
        }

        private TrackBar Trk(TabPage pg, string label, int min, int max, int value,
                             Action<int> set)
        {
            RowLabel(pg, label);

            var t = new TrackBar();
            t.Minimum = min;
            t.Maximum = max;
            t.TickFrequency = (max - min) / 8;
            t.Location = new Point(CtrlX - 4, _y - 6);
            t.Size = new Size(CtrlW + 40, 30);
            t.Value = ClampInt(value, min, max);
            t.ValueChanged += delegate
            {
                if (_loading) return;
                set(t.Value);
                FirePreview();
            };
            pg.Controls.Add(t);

            _y += RowH + 4;
            return t;
        }

        // ============================================================
        //  辅助
        // ============================================================

        private void SyncAlphaEnabled()
        {
            if (_alpha != null && _alphaCustom != null)
                _alpha.Enabled = _alphaCustom.Checked;
            UpdateAlphaText();
        }

        private void UpdateAlphaText()
        {
            if (_alphaText == null) return;

            if (_alphaCustom != null && !_alphaCustom.Checked)
            {
                _alphaText.Text = "跟随主题";
                return;
            }

            int v = (_alpha != null) ? _alpha.Value : 0x99;
            int pct = (int)Math.Round(v * 100.0 / 255.0);
            _alphaText.Text = v + " / 255（" + pct + "%）";
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static float ClampFloat(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static string ThemeName(int index)
        {
            switch (index)
            {
                case 1: return "light";
                case 2: return "dark";
                case 3: return "glass";
                default: return "original";
            }
        }

        private static int ThemeIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;

            switch (name.Trim().ToLowerInvariant())
            {
                case "light": return 1;
                case "dark": return 2;
                case "glass": return 3;
                default: return 0;
            }
        }
    }
}
