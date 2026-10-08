using System;
using System.Collections.Generic;
using System.Drawing;
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

        private readonly TabControl _tabs;
        private readonly TabPage _pgLook;
        private readonly TabPage _pgLayout;
        private readonly TabPage _pgBehave;
        private readonly TabPage _pgRules;

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
                _cBg     = _cBg;
                _cPage   = _cPage;
                _cField  = _cField;
                _cFg     = _cFg;
                _cFgSub  = _cFgSub;
                _cFgHint = _cFgHint;
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
            ClientSize = new Size(640, 580);

            _tabs = new TabControl();
            _tabs.Location = new Point(10, 10);
            _tabs.Size = new Size(620, 512);

            _pgLook = new TabPage("外观");
            _pgLayout = new TabPage("布局");
            _pgBehave = new TabPage("交互");
            _pgRules = new TabPage("分类规则");

            foreach (TabPage p in new[] { _pgLook, _pgLayout, _pgBehave, _pgRules })
            {
                p.BackColor = _cPage;
                p.AutoScroll = true;
            }

            _tabs.TabPages.AddRange(new[] { _pgLook, _pgLayout, _pgBehave, _pgRules });
            Controls.Add(_tabs);

            // ---- 底部按钮 ----
            var btnReset = new Button();
            btnReset.Text = "恢复默认";
            btnReset.Location = new Point(10, 536);
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
            btnPreview.Location = new Point(106, 536);
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
            btnOk.Location = new Point(452, 536);
            btnOk.Size = new Size(84, 30);
            btnOk.DialogResult = DialogResult.OK;
            Controls.Add(btnOk);

            var btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(546, 536);
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
            if (target == null) return false;

            AppSettings work = target.Clone();

            using (var f = new SettingsForm(work, preview, uiTheme))
            {
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

            // ---- 规则表格（仿原版「整理规则」：类型 / 后缀 / 启用开关）----
            // 勾选 = 启用；取消勾选后该类文件落「未归类」
            var grid = new ListView();
            grid.Location = new Point(14, 34);
            grid.Size = new Size(588, 118);
            grid.View = View.Details;
            grid.FullRowSelect = true;
            grid.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            grid.CheckBoxes = true;
            grid.HideSelection = true;
            grid.Columns.Add("分类", 120);
            grid.Columns.Add("后缀数", 66, HorizontalAlignment.Right);
            grid.Columns.Add("包含后缀", 370);
            pg.Controls.Add(grid);

            var probe = new Classifier(_work.ExtraRuleText, null);
            foreach (CategoryRule r in probe.Rules)
            {
                var exts = new List<string>(r.Extensions);
                exts.Sort(StringComparer.OrdinalIgnoreCase);

                // 先设状态再入列 —— 入列后 ItemChecked 才会因用户点击触发
                var it = new ListViewItem(r.DisplayName);
                it.SubItems.Add(exts.Count.ToString());
                it.SubItems.Add(string.Join(" ", exts.ToArray()));
                it.Checked = _work.DisabledCategories == null
                    || !_work.DisabledCategories.Contains(r.DisplayName);
                it.Tag = r.DisplayName;
                grid.Items.Add(it);
            }

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
            tip.Location = new Point(14, 162);
            tip.Size = new Size(590, 34);
            tip.ForeColor = _cFgHint;
            tip.Text = "内置规则不可删（避免误操作把基础分类搞坏），但可以往下追加扩展名。\r\n"
                     + "每行一条，格式：分类名=.扩展名,.扩展名（也可以只写 md 不带点）";
            pg.Controls.Add(tip);

            _extraBox = new TextBox();
            _extraBox.Multiline = true;
            _extraBox.ScrollBars = ScrollBars.Vertical;
            _extraBox.Location = new Point(14, 202);
            _extraBox.Size = new Size(588, 110);
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
            lbl.Location = new Point(14, 324);
            lbl.ForeColor = _cFg;
            lbl.Text = "当前生效的完整规则（已禁用的分类会标注）：";
            pg.Controls.Add(lbl);

            _ruleView = new TextBox();
            _ruleView.Multiline = true;
            _ruleView.ReadOnly = true;
            _ruleView.ScrollBars = ScrollBars.Vertical;
            _ruleView.BackColor = _cField;
            _ruleView.Location = new Point(14, 346);
            _ruleView.Size = new Size(588, 126);
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
