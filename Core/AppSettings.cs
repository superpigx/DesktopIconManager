using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 应用设置。
    ///
    /// 用「当前生效的设置」这个进程内单例（<see cref="Current"/>）来承载，
    /// 因为分类引擎、桌面图标枚举、渲染绑定等六处都要读它 ——
    /// 逐层传参代价太大且极容易漏掉某一条路径。
    ///
    /// 设置窗口改的是一份**副本**，点「确定」之后才写回 Current 并落盘；
    /// 点「取消」则丢弃副本，宿主重新按 Current 应用一遍即可回滚。
    ///
    /// 存储格式（制表符无关，纯 key=value，UTF-8）：
    ///   # DIM-SETTINGS v1
    ///   theme=light
    ///   iconSize=48
    ///   ...
    ///   extraRules=文档=.md,.tex\n图片=.heic
    ///
    /// 解析对缺失/损坏的条目一律回退到默认值，绝不因为一行坏数据让程序起不来。
    /// </summary>
    /// <summary>
    /// 启动时的自动整理方式（对应设置里「程序启动时自动整理」下面的两个单选）。
    /// </summary>
    public enum AutoOrganizeMode
    {
        /// <summary>按整理规则，把文件实时归到各个同名分区。</summary>
        ByRules = 0,
        /// <summary>不管规则，所有文件都丢到同一个固定分区。</summary>
        FixedPartition = 1
    }

    /// <summary>分区标签的切换方式。</summary>
    public enum TabSwitchMode
    {
        /// <summary>点击标签切换（默认，误触少）。</summary>
        Click = 0,
        /// <summary>鼠标悬停即切换。</summary>
        Hover = 1
    }

    public sealed class AppSettings
    {
        private const string Marker = "# DIM-SETTINGS v1";

        // ---- 外观 ----
        public string Theme { get; set; }
        /// <summary>
        /// 面板底透明度 0..255；**-1 表示跟随主题**（三套主题各自的默认值不同，
        /// 玻璃主题本身就是很透明，统一用绝对值会把它变得不像玻璃）。
        /// 153 = 主题文件里的 0x99。
        /// </summary>
        public int PanelAlpha { get; set; }
        public int PanelRadius { get; set; }
        /// <summary>投影模糊层数，0 = 不投影。</summary>
        public int ShadowDepth { get; set; }
        /// <summary>投影纵向偏移（主题 shadow 的 5,3,5,7 → 下重上轻）。</summary>
        public int ShadowOffsetY { get; set; }
        public float TitleFontSize { get; set; }
        public float LabelFontSize { get; set; }

        // ---- 布局 ----
        public int IconSize { get; set; }
        public int ItemWidth { get; set; }
        public int ItemHeight { get; set; }
        public int SmallIconSize { get; set; }
        public int SmallItemHeight { get; set; }
        public int ListRowHeight { get; set; }
        public int ListIconSize { get; set; }
        public int BodyPadding { get; set; }
        public int TitleHeight { get; set; }
        public int TabBarHeight { get; set; }
        public int PanelGap { get; set; }
        /// <summary>自动分组时每行最多放几列。</summary>
        public int MaxColumns { get; set; }

        // ---- 交互 ----
        public bool SnapEnabled { get; set; }
        public int SnapGap { get; set; }
        public int SnapThreshold { get; set; }
        /// <summary>条目增删后面板自动收紧尺寸（对应原程序的 auto_vacuum）。</summary>
        public bool AutoFit { get; set; }
        /// <summary>启动时锁定图标位置（防误拖）。</summary>
        public bool LockIcons { get; set; }
        /// <summary>是否把公用桌面（C:\Users\Public\Desktop）的图标也算进来。</summary>
        public bool IncludePublicDesktop { get; set; }
        /// <summary>
        /// 是否把「在桌面上但不在任何收纳盒里」的图标收进兜底盒。
        ///
        /// 默认开 —— 接管桌面后系统图标被隐藏，没进面板的图标会变得
        /// 既看不到也点不到，那是比多一个面板严重得多的故障。
        /// </summary>
        public bool AdoptOrphanIcons { get; set; }

        // ---- 分类规则 ----
        /// <summary>
        /// 附加分类规则，每行一条：<c>文档=.md,.tex</c>
        /// 扩展名可用逗号或竖线分隔，会**追加**到内置规则上（内置规则不可删）。
        /// </summary>
        public string ExtraRuleText { get; set; }

        /// <summary>
        /// 被禁用的分类（显示名，如「网址」「视频」）。
        /// 命中禁用分类的文件会落进「未归类」—— 对应原版「整理规则」表格里的启用开关。
        /// </summary>
        public List<string> DisabledCategories { get; private set; }

        /// <summary>
        /// 确保禁用表已初始化（供设置界面写入前调用）。
        ///
        /// 属性是 private set —— 外部只能增删**内容**（Remove/Add），
        /// 不能整个换引用；需要重新赋值时走这个方法。
        /// </summary>
        public void EnsureDisabledCategories()
        {
            if (DisabledCategories == null)
                DisabledCategories = new List<string>();
        }

        /// <summary>开机自动启动覆盖层（写 HKCU Run，不需要管理员权限）。</summary>
        public bool AutoStart { get; set; }

        // ---- 自动整理（设置「整理规则」页顶部）----

        /// <summary>程序启动时自动整理桌面图标。</summary>
        public bool AutoOrganizeOnStartup { get; set; }

        /// <summary>自动整理的方式：按规则 / 全部丢到固定分区。</summary>
        public AutoOrganizeMode OrganizeMode { get; set; }

        /// <summary>「整理到固定分区」模式下的目标分区名。</summary>
        public string FixedPartition { get; set; }

        /// <summary>
        /// 分类 → 分区的映射（表格「选择桌面分区」列）。
        ///
        /// 每项形如 <c>文档=我的文档</c>；没有映射的分类归到同名分区。
        /// 存成 List 是为了在设置表格里逐行编辑。
        /// </summary>
        public List<string> PartitionMap { get; private set; }

        /// <summary>确保分区映射表已初始化（设置界面写入前调用）。</summary>
        public void EnsurePartitionMap()
        {
            if (PartitionMap == null) PartitionMap = new List<string>();
        }

        /// <summary>取某分类的映射分区；没有映射时返回 null（表示同名分区）。</summary>
        public string GetPartitionOf(string category)
        {
            if (PartitionMap == null || string.IsNullOrEmpty(category)) return null;

            for (int i = 0; i < PartitionMap.Count; i++)
            {
                string line = PartitionMap[i];
                if (string.IsNullOrEmpty(line)) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                if (string.Equals(line.Substring(0, eq).Trim(), category,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    string v = line.Substring(eq + 1).Trim();
                    return v.Length == 0 ? null : v;
                }
            }
            return null;
        }

        // ---- 外观补充（设置「外观设置」页）----

        /// <summary>主题色（强调色），格式 <c>#RRGGBB</c>；空 = 跟随主题。</summary>
        public string AccentColor { get; set; }

        /// <summary>面板背景图路径；空 = 不使用。</summary>
        public string BackgroundImage { get; set; }

        /// <summary>桌面字体名；空 / "默认字体" = 跟随主题。</summary>
        public string DesktopFont { get; set; }

        /// <summary>分区标签切换方式：点击 / 悬停。</summary>
        public TabSwitchMode TabSwitch { get; set; }

        /// <summary>分区标签始终显示（否则仅悬停显示）。</summary>
        public bool TabLabelAlways { get; set; }

        /// <summary>盒子显示边框。</summary>
        public bool PanelBorder { get; set; }

        /// <summary>盒子使用圆角。</summary>
        public bool PanelRounded { get; set; }

        /// <summary>在快捷方式图标上显示箭头标记。</summary>
        public bool ShortcutArrow { get; set; }

        /// <summary>标签栏收起后，鼠标移到标题上自动展开。</summary>
        public bool AutoExpandOnHover { get; set; }

        // ---- 性能与高级（复盘 v0.9.2 新增）----
        /// <summary>
        /// 用 Shell 官方 COM 接口（IFolderView）读桌面图标文本，
        /// 从而不再对 explorer 申请 PROCESS_VM_WRITE。
        /// 默认关闭 —— 该路径依赖 COM vtable，需真机验证后才建议开启。
        /// 关闭时文本读取回退到 LVM_GETITEMTEXTW（需要 VM_WRITE）。
        /// </summary>
        public bool UseFolderView { get; set; }

        /// <summary>拖动/悬停只重绘变化区域（脏矩形）。关闭则始终整屏重绘。</summary>
        public bool PerfDirtyRect { get; set; }

        /// <summary>
        /// 强制整屏重绘 —— **回退开关**。
        /// 脏矩形一旦算错就是残影（0.9.0 出过同类事故），
        /// 这里保留一键回到"安全网"状态的能力。
        /// </summary>
        public bool PerfFullRedraw { get; set; }

        /// <summary>预乘 alpha 走指针版（需 /unsafe 编译）；关闭则用托管版。</summary>
        public bool PerfFastBlit { get; set; }

        /// <summary>缓存面板投影位图（每盒每帧 8 层圆角描边 → 1 次贴图）。</summary>
        public bool PerfCacheShadow { get; set; }

        /// <summary>缓存图标标签的文字路径（8 次 DrawString 光晕 → 8 次 FillPath）。</summary>
        public bool PerfCacheLabelHalo { get; set; }

        /// <summary>图标缓存条目上限（LRU 淘汰）。</summary>
        public int IconCacheLimit { get; set; }

        /// <summary>悬停重绘节流（毫秒）。0 = 不节流。</summary>
        public int HoverThrottleMs { get; set; }

        /// <summary>布局自动落盘去抖（毫秒）。0 = 改动即写。</summary>
        public int SaveDebounceMs { get; set; }

        public AppSettings()
        {
            ResetToDefaults();
        }

        /// <summary>恢复出厂值。数值取自主题文件的真实定义。</summary>
        public void ResetToDefaults()
        {
            Theme = "original";

            // 外观：-1 = 跟随主题（0x99 = 153 即主题里 transparent_panel 的 60% 白）
            PanelAlpha = -1;
            PanelRadius = 0;
            ShadowDepth = 8;
            ShadowOffsetY = 3;
            TitleFontSize = 9f;
            LabelFontSize = 9f;

            // 布局
            IconSize = 48;
            ItemWidth = 76;
            ItemHeight = 82;
            SmallIconSize = 32;
            SmallItemHeight = 62;
            ListRowHeight = 22;
            ListIconSize = 16;
            BodyPadding = 6;
            TitleHeight = 28;
            TabBarHeight = 22;
            PanelGap = 12;
            MaxColumns = 5;

            // 交互
            SnapEnabled = true;
            SnapGap = 8;
            SnapThreshold = 12;
            AutoFit = true;
            LockIcons = false;
            IncludePublicDesktop = true;
            AdoptOrphanIcons = true;

            ExtraRuleText = string.Empty;
            DisabledCategories = new List<string>();
            AutoStart = false;

            // 自动整理：**默认关闭**。
            // 开启后每次启动都会按规则重排条目，等于「用户没做任何操作，
            // 布局却被程序改了」—— 这类默认行为必须由用户显式选择。
            AutoOrganizeOnStartup = false;
            OrganizeMode = AutoOrganizeMode.ByRules;
            FixedPartition = "其它";
            PartitionMap = new List<string>();

            // 外观补充
            AccentColor = string.Empty;
            BackgroundImage = string.Empty;
            DesktopFont = string.Empty;
            TabSwitch = TabSwitchMode.Click;
            TabLabelAlways = true;
            PanelBorder = true;
            PanelRounded = true;
            ShortcutArrow = false;
            AutoExpandOnHover = true;

            // 性能与高级
            UseFolderView = false;
            PerfDirtyRect = true;
            PerfFullRedraw = false;
            PerfFastBlit = true;
            PerfCacheShadow = true;
            PerfCacheLabelHalo = true;
            IconCacheLimit = 200;
            HoverThrottleMs = 16;
            SaveDebounceMs = 3000;
        }

        public AppSettings Clone()
        {
            var c = new AppSettings();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(AppSettings o)
        {
            if (o == null) return;

            Theme = o.Theme;

            // 自动整理
            AutoOrganizeOnStartup = o.AutoOrganizeOnStartup;
            OrganizeMode = o.OrganizeMode;
            FixedPartition = o.FixedPartition;
            EnsurePartitionMap();
            PartitionMap.Clear();
            if (o.PartitionMap != null) PartitionMap.AddRange(o.PartitionMap);

            // 外观补充
            AccentColor = o.AccentColor;
            BackgroundImage = o.BackgroundImage;
            DesktopFont = o.DesktopFont;
            TabSwitch = o.TabSwitch;
            TabLabelAlways = o.TabLabelAlways;
            PanelBorder = o.PanelBorder;
            PanelRounded = o.PanelRounded;
            ShortcutArrow = o.ShortcutArrow;
            AutoExpandOnHover = o.AutoExpandOnHover;

            PanelAlpha = o.PanelAlpha;
            PanelRadius = o.PanelRadius;
            ShadowDepth = o.ShadowDepth;
            ShadowOffsetY = o.ShadowOffsetY;
            TitleFontSize = o.TitleFontSize;
            LabelFontSize = o.LabelFontSize;

            IconSize = o.IconSize;
            ItemWidth = o.ItemWidth;
            ItemHeight = o.ItemHeight;
            SmallIconSize = o.SmallIconSize;
            SmallItemHeight = o.SmallItemHeight;
            ListRowHeight = o.ListRowHeight;
            ListIconSize = o.ListIconSize;
            BodyPadding = o.BodyPadding;
            TitleHeight = o.TitleHeight;
            TabBarHeight = o.TabBarHeight;
            PanelGap = o.PanelGap;
            MaxColumns = o.MaxColumns;

            SnapEnabled = o.SnapEnabled;
            SnapGap = o.SnapGap;
            SnapThreshold = o.SnapThreshold;
            AutoFit = o.AutoFit;
            LockIcons = o.LockIcons;
            IncludePublicDesktop = o.IncludePublicDesktop;
            AdoptOrphanIcons = o.AdoptOrphanIcons;

            ExtraRuleText = o.ExtraRuleText;
            DisabledCategories = new List<string>(
                o.DisabledCategories ?? new List<string>());
            AutoStart = o.AutoStart;

            UseFolderView = o.UseFolderView;
            PerfDirtyRect = o.PerfDirtyRect;
            PerfFullRedraw = o.PerfFullRedraw;
            PerfFastBlit = o.PerfFastBlit;
            PerfCacheShadow = o.PerfCacheShadow;
            PerfCacheLabelHalo = o.PerfCacheLabelHalo;
            IconCacheLimit = o.IconCacheLimit;
            HoverThrottleMs = o.HoverThrottleMs;
            SaveDebounceMs = o.SaveDebounceMs;
        }

        // ============================================================
        //  当前生效的设置
        // ============================================================

        private static AppSettings _current;

        /// <summary>
        /// 当前生效的设置。首次访问时从磁盘载入，载不到就用默认值。
        /// </summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null)
                    _current = Load(DefaultPath());
                return _current;
            }
        }

        /// <summary>替换当前生效的设置（设置窗口确定后调用）。</summary>
        public static void SetCurrent(AppSettings s)
        {
            if (s == null) return;
            _current = s;
        }

        /// <summary>
        /// 设置文件位置。
        ///
        /// 已从「exe 同目录」迁到 %LOCALAPPDATA%\DesktopIconManager
        /// （旧位置的文件会在首次运行时被自动迁移）。
        /// 原因：程序被放进 Program Files 时，写自身目录会失败或触发 UAC 提示。
        /// </summary>
        public static string DefaultPath()
        {
            return AppPaths.ResolveDataFile("settings.txt");
        }

        // ============================================================
        //  载入 / 保存
        // ============================================================

        public static AppSettings Load(string path)
        {
            var s = new AppSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;

            // 安全读：文件过大 / 损坏 → 直接用默认值，绝不把垃圾全读进内存
            string[] lines;
            if (!AtomicFile.TryReadLines(path, out lines) || lines == null) return s;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw[0] == '#') continue;

                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;

                string k = raw.Substring(0, eq).Trim();
                string v = raw.Substring(eq + 1);
                if (k.Length > 0) map[k] = v;
            }

            s.Theme = GetStr(map, "theme", s.Theme);

            // 自动整理
            s.AutoOrganizeOnStartup = GetBool(map, "autoOrganize", s.AutoOrganizeOnStartup);
            s.OrganizeMode = (AutoOrganizeMode)Clamp(
                GetInt(map, "organizeMode", (int)s.OrganizeMode), 0, 1);
            s.FixedPartition = GetStr(map, "fixedPartition", s.FixedPartition);

            s.EnsurePartitionMap();
            string pm = GetStr(map, "partitionMap", null);
            if (!string.IsNullOrEmpty(pm))
            {
                s.PartitionMap.Clear();
                foreach (string one in pm.Split(';'))
                {
                    string t2 = Unescape(one).Trim();
                    if (t2.Length > 0) s.PartitionMap.Add(t2);
                }
            }

            // 外观补充
            s.AccentColor = GetStr(map, "accentColor", s.AccentColor);
            s.BackgroundImage = GetStr(map, "backgroundImage", s.BackgroundImage);
            s.DesktopFont = GetStr(map, "desktopFont", s.DesktopFont);
            s.TabSwitch = (TabSwitchMode)Clamp(
                GetInt(map, "tabSwitch", (int)s.TabSwitch), 0, 1);
            s.TabLabelAlways = GetBool(map, "tabLabelAlways", s.TabLabelAlways);
            s.PanelBorder = GetBool(map, "panelBorder", s.PanelBorder);
            s.PanelRounded = GetBool(map, "panelRounded", s.PanelRounded);
            s.ShortcutArrow = GetBool(map, "shortcutArrow", s.ShortcutArrow);
            s.AutoExpandOnHover = GetBool(map, "autoExpandOnHover", s.AutoExpandOnHover);

            s.PanelAlpha = Clamp(GetInt(map, "panelAlpha", s.PanelAlpha), -1, 255);
            s.PanelRadius = Clamp(GetInt(map, "panelRadius", s.PanelRadius), 0, 24);
            s.ShadowDepth = Clamp(GetInt(map, "shadowDepth", s.ShadowDepth), 0, 24);
            s.ShadowOffsetY = Clamp(GetInt(map, "shadowOffsetY", s.ShadowOffsetY), 0, 24);
            s.TitleFontSize = Clamp(GetFloat(map, "titleFontSize", s.TitleFontSize), 6f, 24f);
            s.LabelFontSize = Clamp(GetFloat(map, "labelFontSize", s.LabelFontSize), 6f, 24f);

            s.IconSize = Clamp(GetInt(map, "iconSize", s.IconSize), 16, 128);
            s.ItemWidth = Clamp(GetInt(map, "itemWidth", s.ItemWidth), 40, 200);
            s.ItemHeight = Clamp(GetInt(map, "itemHeight", s.ItemHeight), 40, 220);
            s.SmallIconSize = Clamp(GetInt(map, "smallIconSize", s.SmallIconSize), 16, 96);
            s.SmallItemHeight = Clamp(GetInt(map, "smallItemHeight", s.SmallItemHeight), 28, 160);
            s.ListRowHeight = Clamp(GetInt(map, "listRowHeight", s.ListRowHeight), 14, 60);
            s.ListIconSize = Clamp(GetInt(map, "listIconSize", s.ListIconSize), 8, 48);
            s.BodyPadding = Clamp(GetInt(map, "bodyPadding", s.BodyPadding), 0, 32);
            s.TitleHeight = Clamp(GetInt(map, "titleHeight", s.TitleHeight), 16, 64);
            s.TabBarHeight = Clamp(GetInt(map, "tabBarHeight", s.TabBarHeight), 12, 48);
            s.PanelGap = Clamp(GetInt(map, "panelGap", s.PanelGap), 0, 64);
            s.MaxColumns = Clamp(GetInt(map, "maxColumns", s.MaxColumns), 1, 16);

            s.SnapEnabled = GetBool(map, "snapEnabled", s.SnapEnabled);
            s.SnapGap = Clamp(GetInt(map, "snapGap", s.SnapGap), 0, 64);
            s.SnapThreshold = Clamp(GetInt(map, "snapThreshold", s.SnapThreshold), 2, 64);
            s.AutoFit = GetBool(map, "autoFit", s.AutoFit);
            s.LockIcons = GetBool(map, "lockIcons", s.LockIcons);
            s.IncludePublicDesktop = GetBool(map, "includePublicDesktop", s.IncludePublicDesktop);
            s.AdoptOrphanIcons = GetBool(map, "adoptOrphanIcons", s.AdoptOrphanIcons);

            s.ExtraRuleText = Unescape(GetStr(map, "extraRules", s.ExtraRuleText));

            s.DisabledCategories = ParseNames(
                GetStr(map, "disabledCategories", string.Empty));
            s.AutoStart = GetBool(map, "autoStart", s.AutoStart);

            s.UseFolderView = GetBool(map, "useFolderView", s.UseFolderView);
            s.PerfDirtyRect = GetBool(map, "perfDirtyRect", s.PerfDirtyRect);
            s.PerfFullRedraw = GetBool(map, "perfFullRedraw", s.PerfFullRedraw);
            s.PerfFastBlit = GetBool(map, "perfFastBlit", s.PerfFastBlit);
            s.PerfCacheShadow = GetBool(map, "perfCacheShadow", s.PerfCacheShadow);
            s.PerfCacheLabelHalo = GetBool(map, "perfCacheLabelHalo", s.PerfCacheLabelHalo);
            s.IconCacheLimit = Clamp(GetInt(map, "iconCacheLimit", s.IconCacheLimit), 32, 2000);
            s.HoverThrottleMs = Clamp(GetInt(map, "hoverThrottleMs", s.HoverThrottleMs), 0, 100);
            s.SaveDebounceMs = Clamp(GetInt(map, "saveDebounceMs", s.SaveDebounceMs), 0, 60000);

            return s;
        }

        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine(Marker);
            sb.AppendLine("# time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            sb.AppendLine("theme=" + (Theme ?? "original"));
            sb.AppendLine("panelAlpha=" + PanelAlpha);
            sb.AppendLine("panelRadius=" + PanelRadius);
            sb.AppendLine("shadowDepth=" + ShadowDepth);
            sb.AppendLine("shadowOffsetY=" + ShadowOffsetY);
            sb.AppendLine("titleFontSize=" + Fmt(TitleFontSize));
            sb.AppendLine("labelFontSize=" + Fmt(LabelFontSize));

            sb.AppendLine("iconSize=" + IconSize);
            sb.AppendLine("itemWidth=" + ItemWidth);
            sb.AppendLine("itemHeight=" + ItemHeight);
            sb.AppendLine("smallIconSize=" + SmallIconSize);
            sb.AppendLine("smallItemHeight=" + SmallItemHeight);
            sb.AppendLine("listRowHeight=" + ListRowHeight);
            sb.AppendLine("listIconSize=" + ListIconSize);
            sb.AppendLine("bodyPadding=" + BodyPadding);
            sb.AppendLine("titleHeight=" + TitleHeight);
            sb.AppendLine("tabBarHeight=" + TabBarHeight);
            sb.AppendLine("panelGap=" + PanelGap);
            sb.AppendLine("maxColumns=" + MaxColumns);

            sb.AppendLine("snapEnabled=" + (SnapEnabled ? "1" : "0"));
            sb.AppendLine("snapGap=" + SnapGap);
            sb.AppendLine("snapThreshold=" + SnapThreshold);
            sb.AppendLine("autoFit=" + (AutoFit ? "1" : "0"));
            sb.AppendLine("lockIcons=" + (LockIcons ? "1" : "0"));
            sb.AppendLine("includePublicDesktop=" + (IncludePublicDesktop ? "1" : "0"));
            sb.AppendLine("adoptOrphanIcons=" + (AdoptOrphanIcons ? "1" : "0"));

            sb.AppendLine("extraRules=" + Escape(ExtraRuleText ?? string.Empty));

            if (DisabledCategories != null && DisabledCategories.Count > 0)
                sb.AppendLine("disabledCategories=" + string.Join(",", DisabledCategories.ToArray()));
            sb.AppendLine("autoStart=" + (AutoStart ? "1" : "0"));

            sb.AppendLine("# ---- 自动整理 ----");
            sb.AppendLine("autoOrganize=" + (AutoOrganizeOnStartup ? "1" : "0"));
            sb.AppendLine("organizeMode=" + (int)OrganizeMode);
            sb.AppendLine("fixedPartition=" + Escape(FixedPartition ?? string.Empty));
            if (PartitionMap != null && PartitionMap.Count > 0)
                sb.AppendLine("partitionMap=" + Escape(string.Join(";", PartitionMap.ToArray())));

            sb.AppendLine("# ---- 外观补充 ----");
            sb.AppendLine("accentColor=" + Escape(AccentColor ?? string.Empty));
            sb.AppendLine("backgroundImage=" + Escape(BackgroundImage ?? string.Empty));
            sb.AppendLine("desktopFont=" + Escape(DesktopFont ?? string.Empty));
            sb.AppendLine("tabSwitch=" + (int)TabSwitch);
            sb.AppendLine("tabLabelAlways=" + (TabLabelAlways ? "1" : "0"));
            sb.AppendLine("panelBorder=" + (PanelBorder ? "1" : "0"));
            sb.AppendLine("panelRounded=" + (PanelRounded ? "1" : "0"));
            sb.AppendLine("shortcutArrow=" + (ShortcutArrow ? "1" : "0"));
            sb.AppendLine("autoExpandOnHover=" + (AutoExpandOnHover ? "1" : "0"));

            sb.AppendLine("# ---- 性能与高级（改完重启覆盖层生效）----");
            sb.AppendLine("useFolderView=" + (UseFolderView ? "1" : "0"));
            sb.AppendLine("perfDirtyRect=" + (PerfDirtyRect ? "1" : "0"));
            sb.AppendLine("perfFullRedraw=" + (PerfFullRedraw ? "1" : "0"));
            sb.AppendLine("perfFastBlit=" + (PerfFastBlit ? "1" : "0"));
            sb.AppendLine("perfCacheShadow=" + (PerfCacheShadow ? "1" : "0"));
            sb.AppendLine("perfCacheLabelHalo=" + (PerfCacheLabelHalo ? "1" : "0"));
            sb.AppendLine("iconCacheLimit=" + IconCacheLimit);
            sb.AppendLine("hoverThrottleMs=" + HoverThrottleMs);
            sb.AppendLine("saveDebounceMs=" + SaveDebounceMs);

            // 原子写 + 保留上一版 .bak：断电/写一半不会损坏设置文件
            AtomicFile.WriteAllText(path, sb.ToString());
        }

        public static bool IsSettingsFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var r = new StreamReader(path, Encoding.UTF8))
                {
                    string first = r.ReadLine();
                    return first != null && first.Trim() == Marker;
                }
            }
            catch { return false; }
        }

        // ============================================================
        //  解析辅助
        // ============================================================

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string GetStr(Dictionary<string, string> m, string k, string def)
        {
            string v;
            if (m.TryGetValue(k, out v) && v != null) return v.Trim();
            return def;
        }

        private static int GetInt(Dictionary<string, string> m, string k, int def)
        {
            string v;
            int parsed;
            if (m.TryGetValue(k, out v)
                && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return def;
        }

        private static float GetFloat(Dictionary<string, string> m, string k, float def)
        {
            string v;
            float parsed;
            if (m.TryGetValue(k, out v)
                && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return def;
        }

        private static bool GetBool(Dictionary<string, string> m, string k, bool def)
        {
            string v;
            if (!m.TryGetValue(k, out v)) return def;

            v = v.Trim();
            if (v == "1") return true;
            if (v == "0") return false;

            bool parsed;
            if (bool.TryParse(v, out parsed)) return parsed;
            return def;
        }

        /// <summary>解析逗号分隔的分类名列表，空白项丢弃。</summary>
        private static List<string> ParseNames(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;

            foreach (string part in s.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0 && !list.Contains(name))
                    list.Add(name);
            }
            return list;
        }

        /// <summary>把换行等转义进单行，保证 key=value 一行一条。</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace("\r\n", "\\n")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
