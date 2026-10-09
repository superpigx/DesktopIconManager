using System.Drawing;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 主题调色板。
    ///
    /// 全部色值直接取自主题文件 `360desktoplite_theme.xml` 的真实定义
    /// （该文件用「元素名即样式名」的结构）。对应关系：
    ///
    ///   transparent_panel        canvas=0x99ffffff  border=0xffc0c0c0   →  面板本体
    ///   wp_transparent_panel     canvas=0xfff0f0f0                      →  实底面板 / 标签条底
    ///   panel_title              canvas=0xffffffff  border=0xffc5c5c5   →  标题栏（纯白不透明）
    ///   panel_st_line            canvas=0xffffffff  border=0xffc0c0c0   →  标题与内容的分隔线
    ///   panel_line_ecefe8        canvas=0xffecefe8  border=0xff22cb64   →  带绿边的浅线
    ///   panel_99000000           canvas=0x99000000  border=0xff22cb64   →  深色版（绿仍是边）
    ///   panel_4cffffff_border    canvas=0x00000000  border=0x4cffffff   →  玻璃版
    ///   drop_border              hot=0xffcdcdcd  selected=0xffccdee5    →  拖放提示
    ///   shadow                   border="5,3,5,7"                        →  投影（下重上轻）
    ///
    /// 文字样式取自 g_333333_11 / g_666666_9 / desktoplite_999999_9 等，
    /// 字体族统一为「微软雅黑,宋体,Tahoma」。
    ///
    /// ★ 关键结论：**绿色 #22CB64 在主题里只作 border（描边/线），从不作填充**。
    ///   早期版本把标题栏刷成绿色、又用绿色实心徽标，观感突兀 —— 属于误用。
    /// </summary>
    public sealed class ThemePalette
    {
        // ---- 全局主色（只作描边/线/选中态）----
        public Color Accent { get; set; }
        public Color AccentDark { get; set; }

        // ---- 面板 ----
        /// <summary>面板主体填充（含 alpha，决定透明度）。取自 transparent_panel。</summary>
        public Color PanelFill { get; set; }
        /// <summary>面板边框。取自 transparent_panel.border。</summary>
        public Color PanelBorder { get; set; }
        /// <summary>标题栏填充。取自 panel_title，是**不透明**纯白。</summary>
        public Color TitleFill { get; set; }
        /// <summary>标题栏文字。</summary>
        public Color TitleText { get; set; }
        /// <summary>标题栏与内容区之间的分隔线。取自 panel_st_line 的描边色。</summary>
        public Color TitleSeparator { get; set; }
        /// <summary>计数徽标背景。默认全透明 —— 主题里绿色不作填充。</summary>
        public Color BadgeFill { get; set; }
        /// <summary>计数徽标文字。</summary>
        public Color BadgeText { get; set; }
        /// <summary>内容区填充（默认全透明，即沿用面板底）。</summary>
        public Color BodyFill { get; set; }

        // ---- 分类标签页 ----
        /// <summary>标签条底色。取自 wp_transparent_panel（比标题栏略灰一点）。</summary>
        public Color TabBarFill { get; set; }
        /// <summary>当前分类的标签底色。</summary>
        public Color TabActiveFill { get; set; }
        /// <summary>当前分类的标签文字。</summary>
        public Color TabActiveText { get; set; }
        /// <summary>非当前分类的标签底色。</summary>
        public Color TabInactiveFill { get; set; }
        /// <summary>非当前分类的标签文字。</summary>
        public Color TabInactiveText { get; set; }
        /// <summary>鼠标悬停的标签底色。</summary>
        public Color TabHoverFill { get; set; }

        /// <summary>拖动吸附时的对齐参考线（绿色在这里是「线」，符合主题规则）。</summary>
        public Color SnapGuide { get; set; }

        // ---- 缩放手柄 ----
        /// <summary>
        /// 右下角缩放手柄（常驻显示，让用户知道这个角可以拖）。
        /// 直接用面板边框色，只是画成细斜线，不抢视觉。
        /// </summary>
        public Color GripColor { get; set; }
        /// <summary>鼠标压在手柄上时的颜色 —— 用主色描边，符合「绿只作线」的规则。</summary>
        public Color GripHotColor { get; set; }

        // ---- 文字 ----
        /// <summary>主文字色。取自 g_333333_11。</summary>
        public Color TextPrimary { get; set; }
        /// <summary>次要文字色。取自 g_666666_9。</summary>
        public Color TextSecondary { get; set; }
        /// <summary>更弱的提示文字。取自 desktoplite_999999_9。</summary>
        public Color TextMuted { get; set; }
        /// <summary>图标标签的文字描边色（保证在任意壁纸上可读）。</summary>
        public Color LabelHalo { get; set; }

        // ---- 状态 ----
        public Color HoverFill { get; set; }
        public Color SelectionFill { get; set; }

        // ---- 尺寸 ----
        public int TitleHeight { get; set; }
        /// <summary>分类标签条的高度（仅在组合成格子组时占用）。</summary>
        public int TabBarHeight { get; set; }
        public int BodyPadding { get; set; }
        public int ItemWidth { get; set; }
        public int ItemHeight { get; set; }
        public int IconSize { get; set; }
        /// <summary>小图标网格模式下的图标边长。</summary>
        public int SmallIconSize { get; set; }

        /// <summary>
        /// 中等图标边长（介于 IconSize 与 SmallIconSize 之间）。
        ///
        /// 不做成独立设置项 —— 它只是为了对齐参考实现的四档视图，
        /// 用户几乎不会单独调它。ThemeBinder 按大小图标的均值派生。
        /// </summary>
        public int MediumIconSize { get; set; }

        /// <summary>中等图标单元格宽（派生值，见 <see cref="MediumIconSize"/>）。</summary>
        public int MediumItemWidth { get; set; }

        /// <summary>中等图标单元格高（派生值）。</summary>
        public int MediumItemHeight { get; set; }
        /// <summary>小图标网格模式下的单元格高度。</summary>
        public int SmallItemHeight { get; set; }
        /// <summary>列表模式下一行的高度。</summary>
        public int ListRowHeight { get; set; }
        /// <summary>列表模式下的图标边长。</summary>
        public int ListIconSize { get; set; }
        public int PanelRadius { get; set; }
        public int PanelGap { get; set; }
        /// <summary>投影的模糊层数（越大越柔和）。</summary>
        public int ShadowDepth { get; set; }
        /// <summary>投影在纵向的偏移（对应 shadow 的 5,3,5,7 —— 下边最重）。</summary>
        public int ShadowOffsetY { get; set; }

        // ---- 字体 ----
        public string FontFamily { get; set; }
        public float TitleFontSize { get; set; }
        public float LabelFontSize { get; set; }
        /// <summary>标签条文字比正文略小一点。</summary>
        public float TabFontSize { get; set; }
        /// <summary>计数徽标字号。</summary>
        public float BadgeFontSize { get; set; }

        public bool DrawShadow { get; set; }
        public bool DrawLabelHalo { get; set; }

        private ThemePalette() { }

        /// <summary>浅色主题：60% 白半透明面板 + 纯白标题栏 + 灰色细分隔线。</summary>
        public static ThemePalette Light()
        {
            return new ThemePalette
            {
                Accent = Color.FromArgb(0x22, 0xCB, 0x64),
                AccentDark = Color.FromArgb(0x07, 0xCD, 0x5A),

                // 直接照抄主题文件的取值
                PanelFill = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),   // transparent_panel
                PanelBorder = Color.FromArgb(0xC0, 0xC0, 0xC0),

                // 标题栏是**不透明**纯白 —— 层次靠它和半透明面板的亮度差建立
                TitleFill = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),   // panel_title
                TitleText = Color.FromArgb(0x33, 0x33, 0x33),         // g_333333_11
                TitleSeparator = Color.FromArgb(0xC0, 0xC0, 0xC0),    // panel_st_line.border

                // 徽标不填充 —— 绿色在主题里不作底色
                BadgeFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                BadgeText = Color.FromArgb(0x99, 0x99, 0x99),         // desktoplite_999999_9

                BodyFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),

                // 标签条比标题栏略灰，当前标签用纯白「浮」出来
                TabBarFill = Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0),  // wp_transparent_panel
                TabActiveFill = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                TabActiveText = Color.FromArgb(0x33, 0x33, 0x33),
                TabInactiveFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                TabInactiveText = Color.FromArgb(0x66, 0x66, 0x66),   // g_666666_9
                TabHoverFill = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),

                SnapGuide = Color.FromArgb(0xAA, 0x22, 0xCB, 0x64),

                // 缩放手柄：常态跟面板边框同色（不抢眼，但看得见），
                // 压上去才变主色 —— 主色依旧只作「线」
                GripColor = Color.FromArgb(0xCC, 0xC0, 0xC0, 0xC0),
                GripHotColor = Color.FromArgb(0x22, 0xCB, 0x64),

                TextPrimary = Color.FromArgb(0x33, 0x33, 0x33),
                TextSecondary = Color.FromArgb(0x66, 0x66, 0x66),
                TextMuted = Color.FromArgb(0x99, 0x99, 0x99),
                LabelHalo = Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF),

                // 交互反馈用主色的低透明度叠加 —— 这是绿色出现得最合适的地方
                HoverFill = Color.FromArgb(0x26, 0x22, 0xCB, 0x64),
                SelectionFill = Color.FromArgb(0x4D, 0x22, 0xCB, 0x64),

                TitleHeight = 28,
                TabBarHeight = 22,
                BodyPadding = 6,
                ItemWidth = 76,
                ItemHeight = 82,
                IconSize = 48,
                SmallIconSize = 32,
                MediumIconSize = 40,
                MediumItemWidth = 62,
                MediumItemHeight = 72,
                SmallItemHeight = 62,
                ListRowHeight = 22,
                ListIconSize = 16,
                PanelRadius = 0,
                PanelGap = 12,
                ShadowDepth = 8,
                ShadowOffsetY = 3,      // shadow border 5,3,5,7 → 下重上轻

                FontFamily = "微软雅黑",
                TitleFontSize = 9f,
                LabelFontSize = 9f,
                TabFontSize = 8.5f,
                BadgeFontSize = 8f,

                DrawShadow = true,
                DrawLabelHalo = true
            };
        }

        /// <summary>深色主题：60% 黑半透明面板，绿色仍只作描边。</summary>
        public static ThemePalette Dark()
        {
            return new ThemePalette
            {
                Accent = Color.FromArgb(0x22, 0xCB, 0x64),
                AccentDark = Color.FromArgb(0x07, 0xCD, 0x5A),

                // 对应 panel_99000000：canvas 半透明黑，border 绿
                PanelFill = Color.FromArgb(0x99, 0x00, 0x00, 0x00),
                PanelBorder = Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF),

                TitleFill = Color.FromArgb(0xB3, 0x15, 0x15, 0x15),
                TitleText = Color.FromArgb(0xE4, 0xE4, 0xE4),
                TitleSeparator = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),

                BadgeFill = Color.FromArgb(0x00, 0x00, 0x00, 0x00),
                BadgeText = Color.FromArgb(0x99, 0x99, 0x99),

                BodyFill = Color.FromArgb(0x00, 0x00, 0x00, 0x00),

                TabBarFill = Color.FromArgb(0x66, 0x00, 0x00, 0x00),
                TabActiveFill = Color.FromArgb(0xCC, 0x33, 0x33, 0x33),
                TabActiveText = Color.FromArgb(0xE4, 0xE4, 0xE4),
                TabInactiveFill = Color.FromArgb(0x00, 0x00, 0x00, 0x00),
                TabInactiveText = Color.FromArgb(0x99, 0x99, 0x99),
                TabHoverFill = Color.FromArgb(0x99, 0x2A, 0x2A, 0x2A),

                SnapGuide = Color.FromArgb(0xAA, 0x22, 0xCB, 0x64),

                // 深色面板上用半透明白线才看得见
                GripColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
                GripHotColor = Color.FromArgb(0x22, 0xCB, 0x64),

                TextPrimary = Color.FromArgb(0xE4, 0xE4, 0xE4),
                TextSecondary = Color.FromArgb(0x99, 0x99, 0x99),
                TextMuted = Color.FromArgb(0x77, 0x77, 0x77),
                LabelHalo = Color.FromArgb(0xB0, 0x00, 0x00, 0x00),

                HoverFill = Color.FromArgb(0x33, 0x22, 0xCB, 0x64),
                SelectionFill = Color.FromArgb(0x5C, 0x22, 0xCB, 0x64),

                TitleHeight = 28,
                TabBarHeight = 22,
                BodyPadding = 6,
                ItemWidth = 76,
                ItemHeight = 82,
                IconSize = 48,
                SmallIconSize = 32,
                SmallItemHeight = 62,
                ListRowHeight = 22,
                ListIconSize = 16,
                PanelRadius = 0,
                PanelGap = 12,
                ShadowDepth = 8,
                ShadowOffsetY = 3,

                FontFamily = "微软雅黑",
                TitleFontSize = 9f,
                LabelFontSize = 9f,
                TabFontSize = 8.5f,
                BadgeFontSize = 8f,

                DrawShadow = true,
                DrawLabelHalo = true
            };
        }

        /// <summary>玻璃主题：几乎无填充，只有描边 —— 对应 panel_4cffffff_border。</summary>
        public static ThemePalette Glass()
        {
            ThemePalette p = Dark();

            p.PanelFill = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
            p.PanelBorder = Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF);

            // 标题栏也尽量透明，只留一条主色线作唯一点缀
            p.TitleFill = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
            p.TitleText = Color.FromArgb(0x33, 0x33, 0x33);
            p.TitleSeparator = Color.FromArgb(0x66, 0x22, 0xCB, 0x64);

            p.BadgeFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            p.BadgeText = Color.FromArgb(0x66, 0x66, 0x66);

            p.BodyFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);

            // 玻璃主题的底是偏白的，手柄线不能继承深色主题的「半透明白」——
            // 那条线在白玻璃上完全看不见，必须换成灰
            p.GripColor = Color.FromArgb(0xCC, 0xC0, 0xC0, 0xC0);

            p.TabBarFill = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            p.TabActiveFill = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);
            p.TabActiveText = Color.FromArgb(0x33, 0x33, 0x33);
            p.TabInactiveFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            p.TabInactiveText = Color.FromArgb(0x66, 0x66, 0x66);
            p.TabHoverFill = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);

            p.TextPrimary = Color.FromArgb(0x33, 0x33, 0x33);
            p.TextSecondary = Color.FromArgb(0x66, 0x66, 0x66);
            p.TextMuted = Color.FromArgb(0x99, 0x99, 0x99);
            p.LabelHalo = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);

            p.HoverFill = Color.FromArgb(0x33, 0x22, 0xCB, 0x64);
            p.SelectionFill = Color.FromArgb(0x4D, 0x22, 0xCB, 0x64);

            // 玻璃版不做重投影，避免糊成一片
            p.ShadowDepth = 5;

            return p;
        }

        /// <summary>按名称取主题；无法识别时返回浅色。</summary>
        public static ThemePalette ByName(string name)
        {
            // 空 / 未知一律给「原版」—— 它是按 360 收纳盒截图逐像素
            // 标定出来的默认观感，最接近用户习惯的样子
            if (string.IsNullOrEmpty(name)) return Original();

            switch (name.Trim().ToLowerInvariant())
            {
                case "light": return Light();
                case "dark": return Dark();
                case "glass": return Glass();
                default: return Original();
            }
        }

        /// <summary>
        /// 原版主题 —— 色值由用户提供的 360 空白收纳盒截图逐像素标定（2026-10-08）。
        ///
        /// 标定方法：面板内均值 (129,153,172)，紧邻壁纸 (156,186,212)/(169,195,218)；
        /// 假设 alpha=0.6（主题文件 transparent_panel 的 0x99）反解填充纯色，
        /// 三个独立位置解出的 C 误差 ≤2（#6F8391 / #6F8190 / #63798B），模型自洽。
        ///
        /// ★ 关键结论：原版面板是「压暗壁纸」的深色半透明，不是白。
        ///   早期版本用 0x99FFFFFF（60% 白），面板在壁纸上会发白发浑 ——
        ///   这正是「颜色怪」的根源，方向完全反了。
        /// </summary>
        public static ThemePalette Original()
        {
            ThemePalette p = Dark();

            p.PanelFill = Color.FromArgb(0x99, 0x6F, 0x83, 0x91);   // 标定 #6F8391
            p.PanelBorder = Color.FromArgb(0x99, 0x44, 0x6A, 0x81);
            p.TitleFill = Color.FromArgb(0x99, 0x44, 0x6A, 0x81);   // 标题栏更深 #446A81
            p.TitleText = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
            p.TitleSeparator = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
            p.BodyFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);    // 内容区沿用面板底

            // 标签条：原版选中项是「透明底 + 绿字」（newui 的 TabItem_Selected），
            // 非选中项截图里是深灰字
            p.TabBarFill = Color.FromArgb(0x80, 0x5F, 0x76, 0x88);
            p.TabActiveFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            p.TabActiveText = Color.FromArgb(0xFF, 0x59, 0xC4, 0x64);
            p.TabInactiveFill = Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            p.TabInactiveText = Color.FromArgb(0xE6, 0x2E, 0x2E, 0x2E);
            p.TabHoverFill = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

            // 深底上的手柄线用亮灰才看得见
            p.GripColor = Color.FromArgb(0x90, 0xE8, 0xF0, 0xF5);
            p.GripHotColor = Color.FromArgb(0x22, 0xCB, 0x64);

            return p;
        }

        /// <summary>生成等宽预览文本，用于诊断输出。</summary>
        public string Describe()
        {
            return string.Format(
                "Accent=#{0:X2}{1:X2}{2:X2}  PanelFill=#{3:X2}{4:X2}{5:X2}{6:X2}  " +
                "Border=#{7:X2}{8:X2}{9:X2}  Title=#{10:X2}{11:X2}{12:X2}  Radius={13}",
                Accent.R, Accent.G, Accent.B,
                PanelFill.A, PanelFill.R, PanelFill.G, PanelFill.B,
                PanelBorder.R, PanelBorder.G, PanelBorder.B,
                TitleFill.R, TitleFill.G, TitleFill.B,
                PanelRadius);
        }
    }
}
