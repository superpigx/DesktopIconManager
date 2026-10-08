using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>收纳盒内的一个条目。</summary>
    public sealed class GroupItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }

        public GroupItem() { }

        public GroupItem(string name, string fullPath, bool isDirectory)
        {
            Name = name;
            FullPath = fullPath;
            IsDirectory = isDirectory;
        }
    }

    /// <summary>
    /// 一个分类在被组合进别的收纳盒之前，作为独立收纳盒时的几何与配置。
    ///
    /// 存在的意义只有一个：**让「拆分」能按原边界还原**。
    /// 组合时把每个分类当时的位置、尺寸、布局模式、锁定 / 折叠状态记下来，
    /// 拆开时照单还原；没有记录的分类（老存档、或建盒后从未参与组合的）
    /// 退化为自动排布 —— 见 DesktopOverlayForm.DissolveGroup。
    ///
    /// 这是纯元数据，不参与渲染，也不影响命中测试。
    /// </summary>
    public sealed class TabOrigin
    {
        public Rectangle Bounds;
        public BoxViewMode ViewMode;
        public bool Locked;
        public bool Collapsed;

        public TabOrigin() { }

        public TabOrigin(Rectangle bounds, BoxViewMode viewMode, bool locked, bool collapsed)
        {
            Bounds = bounds;
            ViewMode = viewMode;
            Locked = locked;
            Collapsed = collapsed;
        }

        /// <summary>
        /// 边界是否可用于还原。
        ///
        /// 存档是文本、可被手改，源头也可能是极端分辨率下的旧数据；
        /// 宽高为负或荒谬大时宁可放弃还原，也不让面板跑到屏幕外。
        /// </summary>
        public bool IsUsable
        {
            get
            {
                if (Bounds.Width < GroupLayout.MinPanelWidth) return false;
                if (Bounds.Height < 24) return false;
                if (Bounds.Width > 20000 || Bounds.Height > 20000) return false;
                return true;
            }
        }

        public TabOrigin Clone()
        {
            return new TabOrigin(Bounds, ViewMode, Locked, Collapsed);
        }
    }

    /// <summary>
    /// 收纳盒内的一个「分类」—— 也就是面板顶部的一个标签页。
    ///
    /// 每个分类各自持有一份条目列表，互不干扰；
    /// 切换标签只是换一份要显示的列表，不会移动任何文件。
    /// </summary>
    public sealed class FenceTab
    {
        public string Title { get; set; }
        public List<GroupItem> Items { get; private set; }

        /// <summary>
        /// 该分类作为独立收纳盒时的原始几何与配置（用于组合后按原边界拆分）。
        /// null = 无记录（拆开时改用自动排布）。
        /// </summary>
        public TabOrigin Origin { get; set; }

        public FenceTab()
        {
            Title = "新分类";
            Items = new List<GroupItem>();
        }

        public FenceTab(string title)
        {
            Title = title;
            Items = new List<GroupItem>();
        }
    }

    /// <summary>
    /// 收纳盒的显示布局。
    ///
    /// 参考实现里对应「Use icon or list layouts ... per-widget icon size override」，
    /// 也就是同一个盒子可以按需切换成网格大图标、网格小图标或逐行列表。
    /// </summary>
    public enum BoxViewMode
    {
        /// <summary>大图标网格（默认）。</summary>
        LargeIcons = 0,
        /// <summary>小图标网格 —— 一屏能放下更多。</summary>
        SmallIcons = 1,
        /// <summary>列表 —— 每行一项，适合名字长的文件。</summary>
        List = 2
    }

    /// <summary>
    /// 一个收纳盒（面板）。内含 1..N 个分类（标签页）。
    ///
    /// 只有 1 个分类时外观与单一分组无异；
    /// 把另一个盒子拖到它上面就变成多分类盒子，顶部出现标签页。
    /// 二者可以随时拆开 —— 见 DesktopOverlayForm 的「拆出为独立收纳盒」。
    /// </summary>
    public sealed class FenceBox
    {
        /// <summary>
        /// 一个收纳盒最多容纳的分类数。
        /// 取自原程序的约束：「无法添加，收纳盒最多支持15个分类！」
        /// </summary>
        public const int MaxTabs = 15;

        public Rectangle Bounds { get; set; }
        public bool Collapsed { get; set; }

        /// <summary>
        /// 单盒锁定（标题栏的锁按钮）。锁定后该盒不可拖动、缩放，
        /// 条目也不可拖出；双击打开与右键菜单照常。
        /// 与窗体的全局锁定叠加 —— 任一为真即视为锁定。
        /// </summary>
        public bool Locked { get; set; }

        /// <summary>显示布局（大图标 / 小图标 / 列表），逐盒独立。</summary>
        public BoxViewMode ViewMode { get; set; }

        public List<FenceTab> Tabs { get; private set; }

        /// <summary>当前显示的标签页索引。越界会自动收敛到合法范围。</summary>
        public int ActiveTab { get; set; }

        public FenceBox()
        {
            Tabs = new List<FenceTab>();
            Tabs.Add(new FenceTab());
            ActiveTab = 0;
            ViewMode = BoxViewMode.LargeIcons;
            Bounds = new Rectangle(24, 24, 320, 240);
            Collapsed = false;
        }

        /// <summary>
        /// 为盒内还没有来源记录的分类记下**当前**的几何与配置。
        ///
        /// 必须在「把别的盒组合进来」或「本盒被组合进别的盒」之前调用：
        /// 一旦组合完成，原来各自独立时的位置就没处可查了。
        ///
        /// 只补空缺、不覆盖已有记录 —— 同一个盒被连续组合多次时，
        /// 第一次记下的才是它最初的独立形态。
        /// </summary>
        public int CaptureOrigin()
        {
            int captured = 0;
            Rectangle b = Bounds;

            foreach (FenceTab t in Tabs)
            {
                if (t.Origin != null) continue;
                t.Origin = new TabOrigin(b, ViewMode, Locked, Collapsed);
                captured++;
            }

            return captured;
        }

        /// <summary>是否存在可用于「按原边界拆分」的来源记录。</summary>
        public bool HasUsableOrigin
        {
            get
            {
                foreach (FenceTab t in Tabs)
                    if (t.Origin != null && t.Origin.IsUsable) return true;
                return false;
            }
        }

        /// <summary>
        /// 按来源记录构造一个独立收纳盒（用于拆分）。
        ///
        /// 没有记录或记录不可用时返回 null，由调用方退化为自动排布。
        /// </summary>
        public static FenceBox DetachToBox(FenceTab tab)
        {
            if (tab == null) return null;
            if (tab.Origin == null || !tab.Origin.IsUsable) return null;

            var box = new FenceBox();
            box.Tabs.Clear();
            box.Tabs.Add(tab);
            box.Bounds = tab.Origin.Bounds;
            box.ViewMode = tab.Origin.ViewMode;
            box.Locked = tab.Origin.Locked;
            box.Collapsed = tab.Origin.Collapsed;
            box.ActiveTab = 0;

            // 已还原成独立盒，来源记录不再有意义 —— 清掉，
            // 否则下次再组合时会拿这份旧记录覆盖掉它真正的新位置。
            tab.Origin = null;

            return box;
        }

        /// <summary>
        /// 按分类规则把盒内条目在**盒内各分类之间**重新分配。
        ///
        /// 修复「exe 落进脚本配置 tab」这类错位：历史遗留或手动挪动的
        /// 条目按规则归回自己的分类 tab；没有对应分类时自动新建
        /// （不超 MaxTabs，放不下的并入最后一个 tab，绝不丢条目），
        /// 重排后变空的分类随之删除。
        /// 只调整 tab 归属，不移动任何文件 —— 组合结构保持不变。
        /// 返回被挪动的条目数。
        /// </summary>
        public int ReclassifyInPlace(Classifier classifier)
        {
            if (classifier == null) return 0;

            // 1. 收集全部条目
            var all = new List<GroupItem>();
            foreach (FenceTab t in Tabs) all.AddRange(t.Items);
            if (all.Count == 0) return 0;

            // 2. 按分类名分桶（规则外的 → 「未归类」桶）
            var buckets = new Dictionary<string, List<GroupItem>>();
            foreach (GroupItem it in all)
            {
                string name = Classifier.GetDisplayName(
                    classifier.Classify(it.FullPath, it.IsDirectory));

                List<GroupItem> bucket;
                if (!buckets.TryGetValue(name, out bucket))
                {
                    bucket = new List<GroupItem>();
                    buckets[name] = bucket;
                }
                bucket.Add(it);
            }

            // 3. 原有 tab 顺序优先：分类还在的复用原 tab（保持手动排序），
            //    消失的分类连 tab 一起删除
            var newTabs = new List<FenceTab>();
            int moved = 0;

            foreach (FenceTab old in Tabs)
            {
                List<GroupItem> bucket;
                if (!buckets.TryGetValue(old.Title, out bucket)) continue;

                // 原 tab 里属于该分类的条目保持原有相对顺序
                var keep = new List<GroupItem>();
                foreach (GroupItem it in old.Items)
                    if (bucket.Contains(it)) keep.Add(it);

                foreach (GroupItem it in bucket)
                    if (!keep.Contains(it)) { keep.Add(it); moved++; }

                old.Items.Clear();
                old.Items.AddRange(keep);
                newTabs.Add(old);
                buckets.Remove(old.Title);
            }

            // 4. 缺失的分类新建 tab；超上限时并入最后一个 tab（不丢条目）
            foreach (KeyValuePair<string, List<GroupItem>> kv in buckets)
            {
                if (newTabs.Count < MaxTabs)
                {
                    var tab = new FenceTab(kv.Key);
                    tab.Items.AddRange(kv.Value);
                    moved += kv.Value.Count;
                    newTabs.Add(tab);
                }
                else
                {
                    var last = newTabs[newTabs.Count - 1];
                    last.Items.AddRange(kv.Value);
                    moved += kv.Value.Count;
                }
            }

            Tabs = newTabs;
            ActiveTab = 0;
            return moved;
        }

        /// <summary>当前分类。始终返回一个合法对象，永不返回 null。</summary>
        public FenceTab Current
        {
            get
            {
                if (Tabs.Count == 0) Tabs.Add(new FenceTab());
                if (ActiveTab < 0) ActiveTab = 0;
                if (ActiveTab >= Tabs.Count) ActiveTab = Tabs.Count - 1;
                return Tabs[ActiveTab];
            }
        }

        /// <summary>当前分类的标题。等价于原程序的「分类名」。</summary>
        public string Title
        {
            get { return Current.Title; }
            set { Current.Title = value; }
        }

        /// <summary>当前分类的条目。切换标签即切换这里指向的列表。</summary>
        public List<GroupItem> Items
        {
            get { return Current.Items; }
        }

        /// <summary>是否已组合成多分类（决定要不要画标签页）。</summary>
        public bool IsGrouped
        {
            get { return Tabs.Count > 1; }
        }

        /// <summary>循环切到下一个分类。返回是否切换成功。</summary>
        public bool NextTab(int step)
        {
            if (Tabs.Count < 2) return false;

            int n = Tabs.Count;
            int next = ((ActiveTab + step) % n + n) % n;
            if (next == ActiveTab) return false;

            ActiveTab = next;
            return true;
        }
    }

    /// <summary>
    /// 分组布局模型 + 持久化。
    ///
    /// 文件格式 v2（制表符分隔，UTF-8，顺序解析）：
    ///   # DIM-GROUPS v2
    ///   G &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;collapsed&gt; &lt;activeTab&gt; &lt;viewMode&gt;
    ///   T &lt;tabTitle&gt;                    ← 当前盒子新增一个分类
    ///   I &lt;isDir&gt; &lt;name&gt; &lt;fullPath&gt;     ← 追加到当前分类
    ///
    /// v1 格式（每盒单分类）仍可读取，会自动升级为「单分类盒子」：
    ///   G &lt;title&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;collapsed&gt;
    ///   I &lt;boxIndex&gt; &lt;isDir&gt; &lt;name&gt; &lt;fullPath&gt;
    ///
    /// 选择自研文本格式而不用 JSON，是为了零依赖（.NET Framework 无内置 JSON）
    /// 且便于人工查看与手改。
    /// </summary>
    public sealed class GroupLayout
    {
        private const string MarkerV1 = "# DIM-GROUPS v1";
        private const string MarkerV2 = "# DIM-GROUPS v2";

        /// <summary>
        /// 面板最小尺寸。
        ///
        /// 必须让「创建布局」「载入布局」「自动收紧」三处用同一套下限，
        /// 否则会出现尺寸不一致 —— 曾因此导致相邻面板重叠、
        /// 半透明面板叠色，看起来像「标题栏颜色不统一」。
        /// </summary>
        public const int MinPanelWidth = 120;
        public const int MinPanelHeight = 80;

        public List<FenceBox> Groups { get; private set; }

        public GroupLayout()
        {
            Groups = new List<FenceBox>();
        }

        /// <summary>
        /// 布局文件位置。
        ///
        /// 已从「exe 同目录」迁到 %LOCALAPPDATA%\DesktopIconManager
        /// （旧位置的文件会在首次运行时自动迁移过去）。
        /// </summary>
        public static string DefaultPath()
        {
            return AppPaths.ResolveDataFile("groups.txt");
        }

        /// <summary>上次载入时因路径不合法而被丢弃的条目数（0 表示存档干净）。</summary>
        public static int LastLoadSkipped { get; private set; }

        // ============================================================
        //  保存 / 载入
        // ============================================================

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(MarkerV2);
            sb.AppendLine("# time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            foreach (FenceBox g in Groups)
            {
                sb.Append("G\t")
                  .Append(g.Bounds.X).Append('\t')
                  .Append(g.Bounds.Y).Append('\t')
                  .Append(g.Bounds.Width).Append('\t')
                  .Append(g.Bounds.Height).Append('\t')
                  .Append(g.Collapsed ? "1" : "0").Append('\t')
                  .Append(Math.Max(0, Math.Min(g.ActiveTab, g.Tabs.Count - 1)))
                  .Append('\t').Append((int)g.ViewMode)
                  .Append('\t').Append(g.Locked ? "1" : "0")
                  .Append("\r\n");

                foreach (FenceTab tab in g.Tabs)
                {
                    sb.Append("T\t").Append(Escape(tab.Title));

                    // 尾部来源是**可选**的：没有记录时这行与旧版本逐字节一致，
                    // 老 exe 也能照常读（多出的字段被忽略）。
                    TabOrigin o = tab.Origin;
                    if (o != null && o.IsUsable)
                    {
                        sb.Append('\t').Append(o.Bounds.X)
                          .Append('\t').Append(o.Bounds.Y)
                          .Append('\t').Append(o.Bounds.Width)
                          .Append('\t').Append(o.Bounds.Height)
                          .Append('\t').Append((int)o.ViewMode)
                          .Append('\t').Append(o.Locked ? "1" : "0")
                          .Append('\t').Append(o.Collapsed ? "1" : "0");
                    }

                    sb.Append("\r\n");

                    foreach (GroupItem it in tab.Items)
                    {
                        sb.Append("I\t")
                          .Append(it.IsDirectory ? "1" : "0").Append('\t')
                          .Append(Escape(it.Name)).Append('\t')
                          .Append(Escape(it.FullPath))
                          .Append("\r\n");
                    }
                }
            }

            // 原子写 + 保留上一版 .bak。
            // 布局是用户唯一的资产，一次覆盖写写一半就可能把它毁掉。
            AtomicFile.WriteAllText(path, sb.ToString());
        }

        public static GroupLayout Load(string path)
        {
            LastLoadSkipped = 0;

            var layout = new GroupLayout();
            if (!File.Exists(path)) return layout;

            // 安全读：过大 / 读失败 → 返回空布局（由调用方重建），
            // 绝不把几百 MB 的损坏文件全读进内存
            string[] lines;
            if (!AtomicFile.TryReadLines(path, out lines) || lines == null)
                return layout;

            // 版本判定：宁可当作 v1 多算一遍，也不能把 v2 当 v1 解析
            bool v2 = false;
            foreach (string l in lines)
            {
                if (string.IsNullOrEmpty(l) || l[0] != '#') continue;
                if (l.Trim() == MarkerV2) { v2 = true; break; }
                if (l.Trim() == MarkerV1) { v2 = false; break; }
            }

            return v2 ? ParseV2(lines) : ParseV1(lines);
        }

        /// <summary>v2：G 起新盒 → T 起新分类 → I 追加条目，顺序解析。</summary>
        private static GroupLayout ParseV2(string[] lines)
        {
            var layout = new GroupLayout();
            FenceBox box = null;

            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw[0] == '#') continue;

                string[] p = raw.Split('\t');

                if (p[0] == "G" && p.Length >= 7)
                {
                    int x, y, w, h, active, view;
                    if (!int.TryParse(p[1], out x)) continue;
                    if (!int.TryParse(p[2], out y)) continue;
                    if (!int.TryParse(p[3], out w)) continue;
                    if (!int.TryParse(p[4], out h)) continue;
                    if (!int.TryParse(p[6], out active)) active = 0;
                    // 第 8 个字段（布局）是可选的：缺省视为大图标
                    if (p.Length < 8 || !int.TryParse(p[7], out view)) view = 0;
                    // 第 9 个字段（单盒锁定）可选：缺省不锁
                    bool locked = p.Length >= 9 && p[8] == "1";

                    box = new FenceBox();
                    box.Tabs.Clear();          // 分类全部由 T 行建立
                    box.Bounds = new Rectangle(x, y, Math.Max(40, w), Math.Max(40, h));
                    box.Collapsed = p[5] == "1";
                    box.ActiveTab = active;
                    box.Locked = locked;
                    box.ViewMode = (view == 1) ? BoxViewMode.SmallIcons
                                 : (view == 2) ? BoxViewMode.List
                                 : BoxViewMode.LargeIcons;

                    layout.Groups.Add(box);
                }
                else if (p[0] == "T" && p.Length >= 2 && box != null)
                {
                    if (box.Tabs.Count >= FenceBox.MaxTabs) continue;

                    FenceTab tab = new FenceTab(Unescape(p[1]));

                    // 可选字段：本分类被组合前的原始几何与配置。
                    // 老存档没有这段 → 保持 null，拆开时退化为自动排布。
                    // 只要有完整边界（6 列）就先认下来，后三列各自缺省，
                    // 这样手改过或只写了一半的存档也不会整段失效。
                    if (p.Length >= 6)
                    {
                        int ox, oy, ow, oh, ov;
                        if (int.TryParse(p[2], out ox) &&
                            int.TryParse(p[3], out oy) &&
                            int.TryParse(p[4], out ow) &&
                            int.TryParse(p[5], out oh))
                        {
                            // 字段序号（从 0 起）：
                            //   0=T  1=标题  2..5=边界  6=布局  7=锁定  8=折叠
                            // 判定一律用「该位是否存在」= index + 1。
                            // 写成 >=10 / >=11 这类偏大的值会让后两位永远读不到 ——
                            // 表现是「拆回去后锁定/折叠状态丢了」，很难查。
                            ov = 0;
                            if (p.Length >= 7) int.TryParse(p[6], out ov);

                            BoxViewMode vm = (ov == 1) ? BoxViewMode.SmallIcons
                                           : (ov == 2) ? BoxViewMode.List
                                           : BoxViewMode.LargeIcons;

                            bool oLock = p.Length >= 8 && p[7] == "1";
                            bool oCol = p.Length >= 9 && p[8] == "1";

                            var o = new TabOrigin(new Rectangle(ox, oy, ow, oh), vm, oLock, oCol);
                            if (o.IsUsable) tab.Origin = o;
                        }
                    }

                    box.Tabs.Add(tab);
                }
                else if (p[0] == "I" && p.Length >= 4 && box != null)
                {
                    // 存档里的路径来自外部数据 —— 打开前必须校验。
                    // 这是复盘点名的「存档投毒」面：groups.txt 被篡改后
                    // 未校验的 FullPath 会被直接交给 ShellExecute。
                    string full = Unescape(p[3]);
                    if (!PathSafety.IsPlausible(full))
                    {
                        LastLoadSkipped++;
                        continue;
                    }

                    FenceTab tab = box.Current;
                    tab.Items.Add(new GroupItem(Unescape(p[2]), full, p[1] == "1"));
                }
            }

            // 兜底：G 行后没有任何 T 行的盒子补一个空分类
            foreach (FenceBox b in layout.Groups)
                if (b.Tabs.Count == 0) b.Tabs.Add(new FenceTab("新分类"));

            return layout;
        }

        /// <summary>v1：每盒只有一个分类，G 行里带标题。</summary>
        private static GroupLayout ParseV1(string[] lines)
        {
            var layout = new GroupLayout();

            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw[0] == '#') continue;

                string[] p = raw.Split('\t');

                if (p[0] == "G" && p.Length >= 7)
                {
                    int x, y, w, h;
                    if (!int.TryParse(p[2], out x)) continue;
                    if (!int.TryParse(p[3], out y)) continue;
                    if (!int.TryParse(p[4], out w)) continue;
                    if (!int.TryParse(p[5], out h)) continue;

                    // 不要把尺寸「放大到最小宽度」：
                    // 存档里的尺寸就是当初的排版依据，放大反而会压到相邻面板。
                    // 这里只留一个防损坏数据的下限，远小于任何正常尺寸。
                    var box = new FenceBox();
                    box.Tabs.Clear();
                    box.Tabs.Add(new FenceTab(Unescape(p[1])));
                    box.Bounds = new Rectangle(x, y, Math.Max(40, w), Math.Max(40, h));
                    box.Collapsed = p[6] == "1";

                    layout.Groups.Add(box);
                }
                else if (p[0] == "I" && p.Length >= 5)
                {
                    int gi;
                    if (!int.TryParse(p[1], out gi)) continue;
                    if (gi < 0 || gi >= layout.Groups.Count) continue;

                    string full = Unescape(p[4]);
                    if (!PathSafety.IsPlausible(full))
                    {
                        LastLoadSkipped++;
                        continue;
                    }

                    layout.Groups[gi].Items.Add(new GroupItem(
                        Unescape(p[3]), full, p[2] == "1"));
                }
            }

            return layout;
        }

        public static bool IsLayoutFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var r = new StreamReader(path, Encoding.UTF8))
                {
                    string first = r.ReadLine();
                    if (first == null) return false;

                    string t = first.Trim();
                    return t == MarkerV1 || t == MarkerV2;
                }
            }
            catch { return false; }
        }

        // ============================================================
        //  由桌面内容构建默认分组
        // ============================================================

        /// <summary>
        /// 首次运行时，按分类把桌面图标自动分好组。
        /// 只读取文件系统，不触碰桌面。
        /// </summary>
        public static GroupLayout BuildDefault()
        {
            var classifier = new Classifier();
            var reader = new DesktopIconReader(classifier);

            IList<DesktopIcon> icons;
            try { icons = reader.ReadIcons(); }
            catch { icons = new List<DesktopIcon>(); }

            var order = new[]
            {
                IconCategory.Folder, IconCategory.Document, IconCategory.Archive,
                IconCategory.Image, IconCategory.Video, IconCategory.Audio,
                IconCategory.Executable, IconCategory.Script, IconCategory.Shortcut,
                IconCategory.WebLink, IconCategory.Other
            };

            var layout = new GroupLayout();
            int x = 24, y = 24, rowH = 0;

            foreach (IconCategory cat in order)
            {
                var members = new List<DesktopIcon>();
                foreach (DesktopIcon ic in icons)
                    if (ic.Category == cat) members.Add(ic);

                if (members.Count == 0) continue;

                members.Sort(delegate (DesktopIcon a, DesktopIcon b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                var box = new FenceBox();
                box.Title = Classifier.GetDisplayName(cat);

                // 每行最多放几列，由设置决定（默认 5）
                int perRow = AppSettings.Current.MaxColumns;
                if (perRow < 1) perRow = 5;
                const int itemW = 76, itemH = 82, pad = 6, titleH = 28;
                int cols = Math.Min(members.Count, perRow);
                int rows = (members.Count + perRow - 1) / perRow;

                int w = cols * itemW + pad * 2;
                int h = rows * itemH + titleH + pad * 2;

                // 先按最小尺寸收敛，再据此定位。
                // 若不定，定位间隔用原始宽度（可能只有 88px），
                // 而载入时尺寸会被放到最小宽度 120px —— 结果相邻面板重叠，
                // 半透明面板一叠色就看起来像「标题栏颜色不统一」。
                if (w < MinPanelWidth) w = MinPanelWidth;
                if (h < MinPanelHeight) h = MinPanelHeight;

                // 简单的流式摆放，超出屏宽则换行
                if (x + w > 1880 && x > 24)
                {
                    x = 24;
                    y += rowH + 12;
                    rowH = 0;
                }

                box.Bounds = new Rectangle(x, y, w, h);

                foreach (DesktopIcon ic in members)
                    box.Items.Add(new GroupItem(ic.Name, ic.FullPath, ic.IsDirectory));

                layout.Groups.Add(box);

                x += w + 12;
                if (h > rowH) rowH = h;
            }

            return layout;
        }

        /// <summary>对每个盒执行盒内重新归类（保持组合结构）。返回挪动条目数。</summary>
        public int ReclassifyAll(Classifier classifier)
        {
            int moved = 0;
            foreach (FenceBox g in Groups)
                moved += g.ReclassifyInPlace(classifier);
            return moved;
        }

        /// <summary>把布局里已不存在的条目剔除（文件被删/改名后清理）。</summary>
        public int PruneMissing()
        {
            int removed = 0;

            foreach (FenceBox g in Groups)
            {
                // 所有分类都要清理，不能只看当前分类
                foreach (FenceTab tab in g.Tabs)
                {
                    for (int i = tab.Items.Count - 1; i >= 0; i--)
                    {
                        string p = tab.Items[i].FullPath;
                        bool exists = false;
                        try
                        {
                            exists = Directory.Exists(p) || File.Exists(p);
                        }
                        catch { exists = false; }

                        if (!exists)
                        {
                            tab.Items.RemoveAt(i);
                            removed++;
                        }
                    }
                }
            }

            return removed;
        }

        // ============================================================
        //  重叠消除
        // ============================================================

        /// <summary>
        /// 消除面板之间的重叠。
        ///
        /// 为什么需要：早期版本存在尺寸口径不一致的缺陷，会把重叠坐标写进
        /// groups.txt。重叠有两个害处 ——
        ///   1. 半透明面板叠在一起会混色，观感上像「标题栏颜色不统一」
        ///   2. 被压住的面板抓不到标题栏，无法拖动
        ///
        /// 做法很保守：只把「后来者」向右推，右边放不下就换行，不改变已有面板。
        /// </summary>
        /// <returns>被移动过的次数</returns>
        public int ResolveOverlaps(int screenWidth)
        {
            if (screenWidth <= 0) screenWidth = 1920;
            if (Groups.Count < 2) return 0;

            int moved = 0;

            for (int i = 1; i < Groups.Count; i++)
            {
                FenceBox g = Groups[i];
                int guard = 0;

                while (guard++ < 200)
                {
                    bool hit = false;
                    for (int j = 0; j < i; j++)
                    {
                        if (Groups[j].Bounds.IntersectsWith(g.Bounds)) { hit = true; break; }
                    }
                    if (!hit) break;

                    Rectangle r = g.Bounds;
                    int nx = r.X + 16;

                    if (nx + r.Width > screenWidth)
                    {
                        // 右侧放不下 → 换到下一行
                        g.Bounds = new Rectangle(8, r.Y + r.Height + 8, r.Width, r.Height);
                    }
                    else
                    {
                        g.Bounds = new Rectangle(nx, r.Y, r.Width, r.Height);
                    }

                    moved++;
                }
            }

            return moved;
        }

        // ============================================================
        //  转义
        // ============================================================

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace("\t", "\\t")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
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
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == 'r') { sb.Append('\r'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
