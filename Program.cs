using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DesktopIconManager.Core;
using DesktopIconManager.UI;

namespace DesktopIconManager
{
    /// <summary>
    /// 桌面图标管理器。
    ///
    /// 命令（**不带参数时默认 --shell**，即双击 exe 就是启动桌面整理）：
    ///   --shell      桌面覆盖层（核心功能，等价于 --gui / 不带参数）
    ///   --dump-layout 只读诊断：打印存档解析后的布局全景 + 溢出/重复报告
    ///   --probe      只读扫描桌面文件，输出分类统计
    ///   --diagnose   诊断桌面图层结构，检测冲突程序
    ///   --positions  只读列出当前图标坐标
    ///   --preview    预览布局方案，显示每个图标将移动到哪里（不写入）
    ///   --apply      快照 + 应用布局（会真正移动图标）
    ///   --restore    从快照还原图标位置
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 程序入口。
        ///
        /// [STAThread] 是必需的，不能删：
        ///   · OLE 拖放（AllowDrop）要求线程处于单线程单元(STA)
        ///   · Shell 右键菜单的 COM 接口也要求 STA
        /// 缺了它会在创建窗口时抛「必须将当前线程设置为单线程单元模式」。
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            // ---- 异常模式必须在「任何控件创建之前」设好 ----
            // 文档明确约束：一旦线程上创建了任何控件，异常模式就不能再更改。
            // 之前把它放在窗体创建之后（RunShell 内），直接抛
            // InvalidOperationException: 只要在线程上创建了任何控件，
            // 则线程异常模式将不能再有任何更改。
            SetupExceptionHandling();

            // 必须在创建任何窗口之前调用
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // 不强行设成 UTF-8：中文 Windows 控制台默认是 GBK(936)，
                // 强行改代码页反而会让中文变乱码，交给系统处理最稳。
                // 若在 UTF-8 终端（如 Windows Terminal）下运行，可加 --utf8。
                if (Array.IndexOf(args, "--utf8") >= 0)
                    Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // 某些终端不支持改代码页，忽略
            }

            // 先把设置装载进来 —— 分类引擎、桌面枚举、主题绑定都要读它，
            // 必须早于任何基于设置的逻辑
            try { AppSettings.SetCurrent(AppSettings.Load(AppSettings.DefaultPath())); }
            catch { /* 设置文件损坏就用默认值，不能让程序起不来 */ }

            string mode;

            // 无参数 = 双击 exe / 快捷方式 / 某些自启脚本的启动方式。
            // 这种情况下最合理的默认行为是「启动桌面覆盖层」——
            // 以前默认跑 probe，用户双击后只看到一堆统计然后窗口消失，
            // 表现为「直接运行 exe 没有用」。
            // 需要只读探测必须显式写 --probe（脚本里也该写全，语义更清楚）。
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                mode = args[0].TrimStart('-').ToLowerInvariant();
            else
                mode = "shell";

            switch (mode)
            {
                case "probe":     return RunProbe();
                case "diagnose":  return RunDiagnose();
                case "positions": return RunPositions();
                case "preview":   return RunPreview();
                case "apply":     return RunApply(args);
                case "restore":   return RunRestore(args);
                case "ui":        return RunUi(args);
                case "shell":     return RunShell(args);
                case "settings":  return RunSettings();
                case "show-icons": return RunShowIcons();
                case "stats":     return RunStats();
                case "dump-layout": return RunDumpLayout();
                case "gui": case "start": case "run":
                    // --gui / --start / --run：给快捷方式用的显式别名
                    return RunShell(args);
                default:
                    PrintUsage();
                    return 1;
            }
        }

        // ============================================================
        //  阶段1：只读探测
        // ============================================================

        private static int RunProbe()
        {
            Header("阶段1 · 只读探测");
            Console.WriteLine("  [安全声明] 本模式全程只读，不会移动/删除/改写任何图标。");
            Console.WriteLine();

            var classifier = new Classifier();
            var reader = new DesktopIconReader(classifier);

            IList<DesktopIcon> icons;
            try { icons = reader.ReadIcons(); }
            catch (Exception ex)
            {
                Console.WriteLine("  读取失败: " + ex.Message);
                return 1;
            }

            if (icons.Count == 0)
            {
                Console.WriteLine("  未读取到任何桌面图标。");
                return 1;
            }

            var summary = reader.SummarizeByCategory(icons);
            Console.WriteLine("  【总览】");
            Console.WriteLine("  " + new string('-', 63));
            Console.WriteLine("  桌面图标总数: " + icons.Count);
            Console.WriteLine("  文件夹: " + CountOf(summary, IconCategory.Folder));
            Console.WriteLine("  文件:   " + (icons.Count - CountOf(summary, IconCategory.Folder)));
            Console.WriteLine();

            Console.WriteLine("  【分类分布】");
            Console.WriteLine("  " + new string('-', 63));

            var groups = icons.GroupBy(i => i.Category).OrderByDescending(g => g.Count()).ToList();
            int max = groups.Count > 0 ? groups.Max(g => g.Count()) : 1;

            foreach (var g in groups)
            {
                int n = g.Count();
                int bar = Math.Max(1, n * 30 / max);
                Console.WriteLine(string.Format("  {0,-8} {1,4} 个  {2}",
                    Classifier.GetDisplayName(g.Key), n, new string('█', bar)));
            }
            Console.WriteLine();

            var unknown = icons
                .Where(i => i.Category == IconCategory.Other && !i.IsDirectory)
                .Select(i => Path.GetExtension(i.Name))
                .Where(e => !string.IsNullOrEmpty(e))
                .GroupBy(e => e.ToLowerInvariant())
                .OrderByDescending(g => g.Count())
                .ToList();

            if (unknown.Count == 0)
            {
                Console.WriteLine("  【未归类】无 —— 所有文件均已归类");
            }
            else
            {
                Console.WriteLine("  【未归类扩展名】");
                Console.WriteLine("  " + new string('-', 63));
                foreach (var g in unknown)
                    Console.WriteLine(string.Format("  {0,-12} {1} 个", g.Key, g.Count()));
            }

            Footer("阶段1 完成 · 未对桌面做任何修改");
            return 0;
        }

        // ============================================================
        //  诊断
        // ============================================================

        private static int RunDiagnose()
        {
            Header("桌面图层诊断");
            Console.WriteLine("  [只读] 本模式不修改任何内容。");
            Console.WriteLine();

            Console.WriteLine("  【冲突检测】");
            Console.WriteLine("  " + new string('-', 63));
            var conflicts = DesktopLayer.DetectConflicts();
            if (conflicts.Count == 0)
            {
                Console.WriteLine("  未发现其他桌面管理程序，可以安全接管。");
            }
            else
            {
                Console.WriteLine("  [警告] 检测到以下程序可能同时接管桌面图层：");
                foreach (string c in conflicts)
                    Console.WriteLine("    · " + c);
                Console.WriteLine();
                Console.WriteLine("  这些程序与本工具同时运行可能导致图标行为异常。");
                Console.WriteLine("  建议先在它们的设置中关闭「桌面整理」，或退出后再使用本工具。");
            }
            Console.WriteLine();

            using (var layer = DesktopLayer.Attach())
            {
                if (layer == null)
                {
                    Console.WriteLine("  [失败] 未能附加到桌面图层。");
                    Console.WriteLine("  可能原因：桌面被第三方程序接管，或权限不足。");
                    return 1;
                }

                Console.WriteLine("  【图层结构】");
                Console.WriteLine("  " + new string('-', 63));
                Console.WriteLine(layer.Describe());

                Size spacing = layer.GetIconSpacing();
                Size client = layer.GetClientSize();

                Console.WriteLine("  【度量】");
                Console.WriteLine("  " + new string('-', 63));
                Console.WriteLine(string.Format("  图标数量: {0}", layer.Count));
                Console.WriteLine(string.Format("  图标间距: {0} × {1}", spacing.Width, spacing.Height));
                Console.WriteLine(string.Format("  客户区尺寸: {0} × {1}", client.Width, client.Height));
                Console.WriteLine(string.Format("  单屏容量: {0} 列 × {1} 行 = {2} 个图标",
                    client.Width / Math.Max(1, spacing.Width),
                    client.Height / Math.Max(1, spacing.Height),
                    (client.Width / Math.Max(1, spacing.Width)) * (client.Height / Math.Max(1, spacing.Height))));
            }

            Footer("诊断完成");
            return 0;
        }

        // ============================================================
        //  只读列出坐标
        // ============================================================

        private static int RunPositions()
        {
            Header("当前图标坐标（只读）");

            using (var layer = DesktopLayer.Attach())
            {
                if (layer == null) { Console.WriteLine("  [失败] 未能附加到桌面图层。"); return 1; }

                int n = layer.Count;
                Console.WriteLine(string.Format("  共 {0} 个图标", n));
                Console.WriteLine("  " + new string('-', 63));
                Console.WriteLine(string.Format("  {0,-5} {1,-42} {2}", "序号", "名称", "坐标"));
                Console.WriteLine("  " + new string('-', 63));

                for (int i = 0; i < n; i++)
                {
                    string name = layer.GetItemText(i);
                    Point p = layer.GetItemPosition(i);
                    Console.WriteLine(string.Format("  {0,-5} {1,-42} {2}",
                        i, Trim(Display(name), 42), p));
                }
            }

            Footer("读取完成 · 未做任何修改");
            return 0;
        }

        // ============================================================
        //  预览布局
        // ============================================================

        private static int RunPreview()
        {
            Header("布局预览（不写入）");
            Console.WriteLine("  [安全声明] 本模式只计算方案，不移动任何图标。");
            Console.WriteLine();

            using (var layer = DesktopLayer.Attach())
            {
                if (layer == null) { Console.WriteLine("  [失败] 未能附加到桌面图层。"); return 1; }

                var classifier = new Classifier();
                var reader = new DesktopIconReader(classifier);
                var nameIndex = BuildNameIndex(reader.ReadIcons());

                int virtualCount;
                var items = BuildLayoutItems(layer, nameIndex, out virtualCount);

                Size spacing = layer.GetIconSpacing();
                Size client = layer.GetClientSize();
                LayoutPlan plan = IconLayoutEngine.Compute(items, client, spacing, 1);

                Console.WriteLine("  【方案】");
                Console.WriteLine("  " + new string('-', 63));
                Console.WriteLine(IconLayoutEngine.Describe(plan, items));
                if (virtualCount > 0)
                    Console.WriteLine(string.Format("  其中 {0} 个为系统虚拟图标（未在文件系统中找到），归入「未归类」。", virtualCount));
                Console.WriteLine();

                foreach (string w in plan.Warnings)
                    Console.WriteLine("  [警告] " + w);
                if (plan.Warnings.Count > 0) Console.WriteLine();

                Console.WriteLine("  【逐项对照】");
                Console.WriteLine("  " + new string('-', 63));

                int changed = 0;
                foreach (var item in items.OrderBy(i => i.Category).ThenBy(i => i.Name))
                {
                    Point target = plan.Targets[item.Index];
                    Point current = layer.GetItemPosition(item.Index);

                    bool moved = (current.X != target.X || current.Y != target.Y);
                    if (moved) changed++;

                    Console.WriteLine(string.Format("  {0,-38} {1,-10} {2} -> {3} {4}",
                        Trim(Display(item.Name), 38),
                        Classifier.GetDisplayName(item.Category),
                        current.ToString(),
                        target.ToString(),
                        moved ? "" : "(无需移动)"));
                }

                Console.WriteLine();
                Console.WriteLine(string.Format("  合计: {0} 个图标，其中 {1} 个需要移动。", items.Count, changed));
            }

            Footer("预览完成 · 未做任何修改");
            return 0;
        }

        // ============================================================
        //  应用布局
        // ============================================================

        private static int RunApply(string[] args)
        {
            Header("应用布局");
            Console.WriteLine("  [注意] 本模式会真正移动桌面图标。");
            Console.WriteLine("  程序会在移动前自动生成位置快照，可随时用 --restore 还原。");
            Console.WriteLine();

            using (var layer = DesktopLayer.Attach())
            {
                if (layer == null) { Console.WriteLine("  [失败] 未能附加到桌面图层。"); return 1; }

                var conflicts = DesktopLayer.DetectConflicts();
                if (conflicts.Count > 0)
                {
                    Console.WriteLine("  [警告] 检测到其他桌面管理程序正在运行：");
                    foreach (string c in conflicts) Console.WriteLine("    · " + c);
                    Console.WriteLine("  继续操作可能导致双方互相覆盖位置。");
                    Console.WriteLine();
                }

                var classifier = new Classifier();
                var reader = new DesktopIconReader(classifier);
                var nameIndex = BuildNameIndex(reader.ReadIcons());

                int virtualCount;
                var items = BuildLayoutItems(layer, nameIndex, out virtualCount);

                Size spacing = layer.GetIconSpacing();
                Size client = layer.GetClientSize();
                LayoutPlan plan = IconLayoutEngine.Compute(items, client, spacing, 1);

                Console.WriteLine("  【方案】");
                Console.WriteLine("  " + new string('-', 63));
                Console.WriteLine(IconLayoutEngine.Describe(plan, items));
                Console.WriteLine();

                foreach (string w in plan.Warnings)
                    Console.WriteLine("  [警告] " + w);
                if (plan.Warnings.Count > 0) Console.WriteLine();

                // ---- 快照（关键安全步骤）----
                string snapPath = GetSnapshotPath(args);
                var snapshot = new List<SnapshotEntry>();
                for (int i = 0; i < items.Count; i++)
                {
                    Point p = layer.GetItemPosition(items[i].Index);
                    snapshot.Add(new SnapshotEntry
                    {
                        Index = items[i].Index,
                        Name = items[i].Name,
                        X = p.X,
                        Y = p.Y
                    });
                }

                try
                {
                    PositionStore.Save(snapPath, snapshot);
                    Console.WriteLine("  [快照] 已保存 " + snapshot.Count + " 条位置记录：");
                    Console.WriteLine("         " + snapPath);
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  [中止] 快照保存失败，为安全起见不再继续。");
                    Console.WriteLine("         原因: " + ex.Message);
                    return 1;
                }

                // ---- 确认 ----
                Console.Write("  确认应用该布局? 输入大写 YES 继续: ");
                string answer = Console.ReadLine();
                if (answer == null || answer.Trim() != "YES")
                {
                    Console.WriteLine("  已取消，未做任何修改。");
                    Footer("操作已取消");
                    return 0;
                }

                // ---- 应用 ----
                Console.WriteLine();
                Console.WriteLine("  正在移动图标...");
                int applied = 0;
                foreach (var item in items)
                {
                    Point target = plan.Targets[item.Index];
                    layer.SetItemPosition(item.Index, target.X, target.Y);
                    applied++;
                    if (applied % 20 == 0)
                        Console.WriteLine(string.Format("    进度 {0}/{1}", applied, items.Count));
                }

                if (items.Count > 0)
                    layer.Refresh(0, items.Count);

                Console.WriteLine(string.Format("  完成，已处理 {0} 个图标。", applied));
                Console.WriteLine();
                Console.WriteLine("  如需还原：DesktopIconManager.exe --restore");
            }

            Footer("布局已应用");
            return 0;
        }

        // ============================================================
        //  还原
        // ============================================================

        private static int RunRestore(string[] args)
        {
            Header("还原图标位置");

            string snapPath = GetSnapshotPath(args);
            if (!PositionStore.IsSnapshotFile(snapPath))
            {
                Console.WriteLine("  [失败] 找不到有效快照文件：");
                Console.WriteLine("         " + snapPath);
                Console.WriteLine();
                Console.WriteLine("  可用参数指定其他快照：--restore \"D:\\path\\snapshot.txt\"");
                return 1;
            }

            var entries = PositionStore.Load(snapPath);
            Console.WriteLine(string.Format("  快照包含 {0} 条记录：{1}", entries.Count, snapPath));
            Console.WriteLine();

            using (var layer = DesktopLayer.Attach())
            {
                if (layer == null) { Console.WriteLine("  [失败] 未能附加到桌面图层。"); return 1; }

                // 建立 名称 -> 当前序号 的映射（序号可能因增删图标而变化，故优先按名称匹配）
                var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int n = layer.Count;
                for (int i = 0; i < n; i++)
                {
                    string name = layer.GetItemText(i);
                    if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name))
                        byName[name] = i;
                }

                int restored = 0, missing = 0;
                foreach (var e in entries)
                {
                    int index;
                    if (!string.IsNullOrEmpty(e.Name) && byName.TryGetValue(e.Name, out index))
                    {
                        layer.SetItemPosition(index, e.X, e.Y);
                        restored++;
                    }
                    else if (e.Index >= 0 && e.Index < n)
                    {
                        layer.SetItemPosition(e.Index, e.X, e.Y);
                        restored++;
                    }
                    else
                    {
                        missing++;
                    }
                }

                if (n > 0) layer.Refresh(0, n);

                Console.WriteLine(string.Format("  已还原 {0} 个图标位置。", restored));
                if (missing > 0)
                    Console.WriteLine(string.Format("  {0} 条记录因图标已不存在而跳过。", missing));
            }

            Footer("还原完成");
            return 0;
        }

        // ============================================================
        //  样式预览界面
        // ============================================================

        private static int RunUi(string[] args)
        {
            // 默认跟随设置里的主题；命令行给一个非选项参数可临时覆盖
            string themeName = AppSettings.Current.Theme;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!string.IsNullOrEmpty(a) && a[0] != '-')
                {
                    themeName = a;
                    break;
                }
            }

            AppSettings cfg = AppSettings.Current.Clone();
            cfg.Theme = themeName;
            ThemePalette theme = ThemeBinder.Build(cfg);

            Header("样式预览 · " + themeName + " 主题");
            Console.WriteLine("  [安全声明] 本模式只截取桌面作为背景，不修改任何图标。");
            Console.WriteLine();
            Console.WriteLine("  " + theme.Describe());
            Console.WriteLine();
            Console.WriteLine("  按 ESC 或单击任意处退出预览。");
            Console.WriteLine();

            using (var form = new PreviewForm(theme))
            {
                form.SetThemeName(themeName);
                Application.Run(form);
            }

            Console.WriteLine("  预览已关闭。");
            Footer("样式预览结束 · 未对桌面做任何修改");
            return 0;
        }

        // ============================================================
        //  设置窗口
        // ============================================================

        /// <summary>
        /// 独立打开设置窗口（**不接管桌面**，可随时运行）。
        ///
        /// 与托盘里的「设置…」是同一个窗口，区别只是这里没有运行中的覆盖层，
        /// 所以预览回调为空 —— 改动在下次 --shell / --ui 时生效。
        /// </summary>
        private static int RunSettings()
        {
            AppSettings settings = AppSettings.Current;

            Header("设置 · " + settings.Theme + " 主题");
            Console.WriteLine("  设置文件: " + AppSettings.DefaultPath());
            Console.WriteLine("  当前: " + DescribeSettings(settings));
            Console.WriteLine();
            Console.WriteLine("  提示：改动即时写入设置文件；桌面覆盖层需重开才生效。");
            Console.WriteLine();

            bool ok = SettingsForm.Edit(null, settings, null, ThemeBinder.Build(settings));

            if (!ok)
            {
                Console.WriteLine("  已取消，设置未改动。");
                Footer("设置窗口已关闭");
                return 0;
            }

            try
            {
                settings.Save(AppSettings.DefaultPath());
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 保存设置出错: " + ex.Message);
                Footer("设置未保存");
                return 1;
            }

            Console.WriteLine("  已保存设置。");
            Console.WriteLine("  现在: " + DescribeSettings(settings));
            Footer("设置窗口已关闭");
            return 0;
        }

        /// <summary>一行摘要，便于在命令行确认当前设置。</summary>
        private static string DescribeSettings(AppSettings s)
        {
            if (s == null) return "(无设置)";

            string alpha = (s.PanelAlpha < 0) ? "跟随主题" : s.PanelAlpha.ToString();

            return string.Format(
                "主题={0}  面板透明度={1}  大图标={2}  小图标={3}  列表行高={4}  " +
                "吸附={5}(间距 {6} / 阈值 {7})",
                s.Theme, alpha, s.IconSize, s.SmallIconSize, s.ListRowHeight,
                s.SnapEnabled ? "开" : "关", s.SnapGap, s.SnapThreshold);
        }

        // ============================================================
        //  桌面覆盖层（核心模式）
        // ============================================================

        private static int RunShell(string[] args)
        {
            // ---- 单实例保护 ----
            // 两个覆盖层同时接管桌面图层会互相覆盖图标位置，必须拦住。
            // 命名用 "Local\" 前缀 = 会话级。
            // 这里用 using 显式释放：以前依赖终结器，语义上没错但不够明确，
            // 而且句柄要等到 GC 才归还。
            bool createdNew;
            using (var single = new System.Threading.Mutex(
                true, @"Local\DesktopIconManager.Shell", out createdNew))
            {
            if (!createdNew)
            {
                MessageBox.Show(
                    "桌面整理已经在运行了（托盘里有绿色图标）。\r\n" +
                    "如需重启，请先从托盘菜单退出。",
                    "桌面图标管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            // ---- 退出残留自检（P0-6）----
            // 上次如果是被强杀 / 断电结束的，ProcessExit 钩子根本不会执行，
            // 系统桌面图标就会一直处于隐藏状态。这里把它恢复回来 ——
            // 最坏情况从「用户手动跑 --show-icons」变成「自愈」。
            bool crashedLastTime = RunningMarker.Take();
            if (crashedLastTime)
            {
                AppLog.Warn("检测到上次运行未正常退出，已自动恢复系统桌面图标");
                try { DesktopToggler.ShowIcons(); }
                catch { }

                Console.WriteLine("  [自检] 上次运行没有正常退出，已自动恢复系统桌面图标。");
                Console.WriteLine();
            }

            // ---- 全程正常退出时删除运行标记 ----
            AppDomain.CurrentDomain.ProcessExit += delegate { RunningMarker.Release(); };

            // 默认跟随设置里的主题；命令行给一个非选项参数可临时覆盖
            string themeName = AppSettings.Current.Theme;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!string.IsNullOrEmpty(a) && a[0] != '-') { themeName = a; break; }
            }

            AppSettings cfg = AppSettings.Current.Clone();
            cfg.Theme = themeName;
            ThemePalette theme = ThemeBinder.Build(cfg);

            Header("桌面覆盖层 · " + themeName + " 主题");
            Console.WriteLine("  版本: " + AppVersion.Number);
            Console.WriteLine("  数据目录: " + AppPaths.DataDirectory);
            Console.WriteLine("  日志: " + AppPaths.LogPath);
            Console.WriteLine();
            AppLog.Info("启动覆盖层 · 主题 " + themeName);

            PerfStats.BeginPhase("冲突检测");
            // 冲突检测
            var conflicts = DesktopLayer.DetectConflicts();
            PerfStats.EndPhase();
            if (conflicts.Count > 0)
            {
                Console.WriteLine("  [警告] 检测到其他桌面管理程序正在运行：");
                foreach (string c in conflicts) Console.WriteLine("    · " + c);
                Console.WriteLine("  建议先退出它们，否则会互相覆盖图标。");
                Console.WriteLine();
            }

            if (DesktopToggler.FindDefView() == IntPtr.Zero)
            {
                Console.WriteLine("  [失败] 找不到桌面图层，无法接管。");
                return 1;
            }

            // 载入或初始化布局
            PerfStats.BeginPhase("载入布局");
            string layoutPath = GroupLayout.DefaultPath();
            GroupLayout layout;
            if (GroupLayout.IsLayoutFile(layoutPath))
            {
                layout = GroupLayout.Load(layoutPath);
                PerfStats.EndPhase();
                Console.WriteLine("  已载入布局: " + layoutPath);
                Console.WriteLine(DescribeLayout(layout));

                if (GroupLayout.LastLoadSkipped > 0)
                {
                    AppLog.Warn("载入布局时丢弃了 " + GroupLayout.LastLoadSkipped
                                + " 个路径不合法的条目");
                    Console.WriteLine("  [注意] 有 " + GroupLayout.LastLoadSkipped
                                      + " 个条目因路径不合法被忽略（详见日志）。");
                }

                // 修正历史数据里可能残留的面板重叠
                // （旧版存在尺寸口径不一致的缺陷，会把重叠坐标写进存档）
                int fixedCount = layout.ResolveOverlaps(SystemInformation.VirtualScreen.Width);
                if (fixedCount > 0)
                {
                    Console.WriteLine("  已消除 " + fixedCount + " 处面板重叠（旧版数据遗留）");
                    try { layout.Save(layoutPath); }
                    catch { }
                }
            }
            else
            {
                layout = GroupLayout.BuildDefault();
                PerfStats.EndPhase();
                Console.WriteLine("  首次运行，已按文件类型自动分组。");
                Console.WriteLine(DescribeLayout(layout));
                try { layout.Save(layoutPath); }
                catch { }
            }
            Console.WriteLine();

            Console.WriteLine("  操作说明：");
            Console.WriteLine("    · 拖标题栏移动面板（自动吸附对齐）· 拖右下角缩放");
            Console.WriteLine("    · 把一个盒子拖到另一个上 → 组合成格子组（标签页切换）");
            Console.WriteLine("    · 点标签 / 滚轮 / Ctrl+Tab 切换分类");
            Console.WriteLine("    · 右键菜单可切换布局（大图标 / 小图标 / 列表）");
            Console.WriteLine("    · 拖图标到别的收纳盒 · 双击标题折叠");
            Console.WriteLine("    · Ctrl+S 保存 · ESC 退出");
            Console.WriteLine();
            Console.WriteLine("  退出时会自动恢复系统桌面图标。");
            Console.WriteLine();

            // 兜底：任何未捕获异常 / 进程退出，都要恢复桌面图标
            EventHandler restore = delegate { try { DesktopToggler.ShowIcons(); } catch { } };
            AppDomain.CurrentDomain.ProcessExit += restore;
            AppDomain.CurrentDomain.UnhandledException += delegate { restore(null, null); };

            // ---- 准备运行环境 ----
            EnableDpiAwareness();

            bool keepConsole = Array.IndexOf(args, "--console") >= 0;
            if (!keepConsole)
            {
                // 后台常驻模式：分离控制台窗口，改用托盘图标操作
                try { NativeMethods.FreeConsole(); } catch { }
            }

            try
            {
                using (var tray = new TrayHost(themeName))
                using (var watcher = new ExplorerWatcher())
                {
                    using (var form = new DesktopOverlayForm(theme, layout, layoutPath))
                    {
                        // 兜底：把「在桌面上但不在任何收纳盒里」的图标收进「未收纳」盒。
                        // 接管桌面后系统图标是隐藏的，漏掉的图标会变得既看不到也点不到。
                        // 注意：此处控制台可能已被 FreeConsole 分离，改用气泡提示。
                        PerfStats.BeginPhase("收纳未归类图标");
                        int adopted = form.AdoptOrphanIcons();
                        PerfStats.EndPhase();
                        if (adopted > 0)
                        {
                            tray.Notify("已收纳未归类图标",
                                "把 " + adopted + " 个不在任何收纳盒里的图标收进了「未收纳」盒。");
                        }

                        // 托盘 → 窗体
                        tray.TogglePanels += delegate { form.TogglePanels(); };
                        tray.SaveLayout += delegate { form.SaveLayoutNow(); };
                        tray.Relayout += delegate { form.RequestRelayout(); };
                        tray.RestoreDesktopIcons += delegate { DesktopToggler.ShowIcons(); };
                        tray.ToggleLock += delegate { form.Locked = !form.Locked; };
                        tray.ToggleSnap += delegate { form.SnapEnabled = !form.SnapEnabled; };
                        tray.TidyAll += delegate { form.TidyAll(); };
                        tray.ExitRequested += delegate { form.Close(); };

                        // 设置：边改边预览；取消则按原值重新应用一遍以回滚
                        tray.OpenSettings += delegate
                        {
                            AppSettings working = AppSettings.Current;

                            bool saved = SettingsForm.Edit(form, working,
                                delegate (AppSettings w) { form.ApplySettings(w); },
                                theme);

                            if (saved)
                            {
                                try { working.Save(AppSettings.DefaultPath()); }
                                catch { }
                                tray.SetTooltip("桌面图标管理器 · " + working.Theme + " 主题");
                            }
                            else
                            {
                                // 取消：把预览过的改动撤回去
                                form.ApplySettings(working);
                            }
                        };

                        // explorer 重启 → 重新接管
                        watcher.ExplorerRestarted += delegate { form.HandleExplorerRestart(); };
                        watcher.Start();

                        form.Shown += delegate
                        {
                            tray.Notify("桌面整理已启动",
                                "双击托盘图标可显示/隐藏面板，右键查看更多。");
                        };

                        // 异常处理已在 Main 开头统一设好（必须在创建控件之前）
                        Application.Run(form);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex);

                // 关键：必须同时打到控制台。
                // 之前只写日志，导致窗口一闪而过、用户完全看不出原因。
                Console.WriteLine();
                Console.WriteLine("  [异常] " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                    Console.WriteLine("  [内层] " + ex.InnerException.Message);
                Console.WriteLine("  完整堆栈已记录到: " + AppPaths.LogPath);
                Console.WriteLine("  若桌面图标未恢复，请运行 --show-icons");
            }
            finally
            {
                restore(null, null);
                RunningMarker.Release();
            }

            }   // using (Mutex) —— 单实例保护的作用域到此结束

            Footer("覆盖层已关闭 · 系统桌面图标已恢复");
            return 0;
        }

        /// <summary>开启 Per-Monitor DPI 感知，保证高缩放下的坐标准确。</summary>
        private static void EnableDpiAwareness()
        {
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(
                        NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch
            {
                // 旧系统没有这个 API，走回退
            }

            try { NativeMethods.SetProcessDPIAware(); }
            catch { }
        }

        /// <summary>
        /// 设置 UI 线程异常处理：记录日志并给出轻提示，替代默认崩溃对话框。
        ///
        /// 必须在创建任何控件之前调用 —— 这是 WinForms 的硬性约束。
        /// 用 try/catch 兜底：即使设置失败，也只是退回默认行为，不影响功能。
        /// </summary>
        private static void SetupExceptionHandling()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.ThreadException +=
                    delegate(object s, System.Threading.ThreadExceptionEventArgs ea)
                {
                    WriteCrashLog(ea.Exception);

                    try
                    {
                        MessageBox.Show(
                            "发生了一个错误，已记录到：\n" + AppPaths.LogPath + "\n\n" +
                            ea.Exception.Message,
                            "桌面图标管理器",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch { }
                };
            }
            catch
            {
                // 极端情况下（如已提前创建控件）设置失败，忽略即可
            }
        }

        /// <summary>记录异常（统一走 AppLog：单一文件 + 轮转 + 环境头）。</summary>
        private static void WriteCrashLog(Exception ex)
        {
            if (ex == null) return;
            AppLog.Fatal("未捕获异常", ex);
        }

        /// <summary>
        /// 应急恢复：把系统桌面图标显示回来。
        /// 万一覆盖层异常退出导致图标不见了，运行这个即可。
        /// </summary>
        private static int RunShowIcons()
        {
            Header("恢复系统桌面图标");

            Console.WriteLine(DesktopToggler.Describe());

            if (DesktopToggler.AreIconsVisible())
            {
                Console.WriteLine();
                Console.WriteLine("  桌面图标已经是显示状态，无需恢复。");
                Footer("无需操作");
                return 0;
            }

            bool ok = DesktopToggler.ShowIcons();
            Console.WriteLine();
            Console.WriteLine(ok ? "  已发送恢复指令。" : "  恢复失败，请手动右键桌面 → 查看 → 显示桌面图标。");

            Footer(ok ? "桌面图标已恢复" : "恢复失败");
            return ok ? 0 : 1;
        }

        // ============================================================
        //  运行指标（--stats）
        // ============================================================

        /// <summary>
        /// 输出运行指标与环境信息。
        ///
        /// 这是复盘里「可观测性优先于进一步优化」的落点 ——
        /// 在没有渲染耗时 / 帧数 / 内存 / 句柄之前就动渲染链路，最容易引入回归。
        ///
        /// 注意：单独运行 --stats 拿到的是**这个命令行进程**的指标
        /// （没有覆盖层，帧数为 0）。要看真实渲染数据，请用
        /// `--shell --console` 跑一段时间后退出，或看数据目录下的日志。
        /// </summary>
        private static int RunStats()
        {
            Header("运行指标 · " + AppVersion.Display);

            AppSettings s = AppSettings.Current;

            Console.WriteLine("  【环境】");
            Console.WriteLine("  程序目录: " + AppPaths.BaseDirectory);
            Console.WriteLine("  数据目录: " + AppPaths.DataDirectory);
            Console.WriteLine("  日志文件: " + AppPaths.LogPath);
            Console.WriteLine("  进程: " + (Environment.Is64BitProcess ? "64 位" : "32 位")
                              + "   CLR " + Environment.Version);
            Console.WriteLine("  系统: " + Environment.OSVersion);
            Console.WriteLine("  显示器: " + Screen.AllScreens.Length + " 台   虚拟屏幕: "
                              + SystemInformation.VirtualScreen.Width + "×"
                              + SystemInformation.VirtualScreen.Height);

            try
            {
                IntPtr dv = DesktopToggler.FindDefView();
                Console.WriteLine("  桌面图层: " + (dv == IntPtr.Zero ? "未找到" : "0x" + dv.ToInt64().ToString("X8")));
                Console.WriteLine("  系统桌面图标: " + (DesktopToggler.AreIconsVisible() ? "显示" : "隐藏"));
            }
            catch { }
            Console.WriteLine();

            Console.WriteLine("  【布局】");
            string layoutPath = GroupLayout.DefaultPath();
            Console.WriteLine("  布局文件: " + layoutPath);
            try
            {
                var info = new FileInfo(layoutPath);
                Console.WriteLine("  文件大小: " + info.Length + " 字节   修改时间: " + info.LastWriteTime);
            }
            catch { }

            try
            {
                if (GroupLayout.IsLayoutFile(layoutPath))
                {
                    GroupLayout layout = GroupLayout.Load(layoutPath);
                    Console.WriteLine(DescribeLayout(layout));
                    if (GroupLayout.LastLoadSkipped > 0)
                        Console.WriteLine("  路径不合法被忽略: " + GroupLayout.LastLoadSkipped + " 条");
                }
                else
                {
                    Console.WriteLine("  （尚无布局文件）");
                }
            }
            catch { }
            Console.WriteLine();

            Console.WriteLine("  【性能开关】");
            Console.WriteLine("    脏矩形 perfDirtyRect      = " + (s.PerfDirtyRect ? "开" : "关"));
            Console.WriteLine("    强制整屏 perfFullRedraw   = " + (s.PerfFullRedraw ? "开（回退模式）" : "关"));
            Console.WriteLine("    指针预乘 perfFastBlit     = " + (s.PerfFastBlit ? "开" : "关"));
            Console.WriteLine("    投影缓存 perfCacheShadow  = " + (s.PerfCacheShadow ? "开" : "关"));
            Console.WriteLine("    光晕缓存 perfCacheLabelHalo = " + (s.PerfCacheLabelHalo ? "开" : "关"));
            Console.WriteLine("    COM 通道 useFolderView    = " + (s.UseFolderView ? "开" : "关（默认，LVM 回退）"));
            Console.WriteLine("    图标缓存上限              = " + s.IconCacheLimit);
            Console.WriteLine("    悬停节流                  = " + s.HoverThrottleMs + " ms");
            Console.WriteLine("    落盘去抖                  = " + s.SaveDebounceMs + " ms");
            Console.WriteLine();

            if (PerfStats.PhaseReport.Length > 0)
            {
                Console.WriteLine("  【启动阶段耗时】");
                Console.WriteLine(PerfStats.PhaseReport);
            }

            Console.WriteLine("  【本次进程指标】");
            Console.WriteLine(PerfStats.Report());

            Footer("指标输出完毕");
            return 0;
        }

        // ============================================================
        //  辅助
        // ============================================================

        /// <summary>
        /// 打印「存档解析后的布局全景」（只读，不改任何文件）。
        ///
        /// 用来回答一类很难猜的问题：图标到底是**存错了**、**读错了**，
        /// 还是**画错了**。逐项列出每个盒 / 每个分类的
        /// 条目数、来源记录、面板容量、以及条目是否装得下，
        /// 一眼就能定位到是哪一层出的问题。
        /// </summary>
        private static int RunDumpLayout()
        {
            Header("布局诊断 · 存档解析结果");

            string path = GroupLayout.DefaultPath();
            Console.WriteLine("  布局文件: " + path);
            Console.WriteLine("  [安全声明] 本模式只读，不写文件、不动桌面。");
            Console.WriteLine();

            if (!GroupLayout.IsLayoutFile(path))
            {
                Console.WriteLine("  （尚无布局文件）");
                Footer("无数据");
                return 0;
            }

            GroupLayout layout;
            try { layout = GroupLayout.Load(path); }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 解析异常: " + ex.Message);
                Footer("诊断失败");
                return 1;
            }

            AppSettings cfg = AppSettings.Current;
            ThemePalette theme = ThemeBinder.Build(cfg);

            int maxCols = Math.Max(1, cfg.MaxColumns);
            Console.WriteLine("  设置: 主题=" + cfg.Theme + "  最大列数=" + maxCols
                              + "  锁定图标=" + (cfg.LockIcons ? "是" : "否"));
            Console.WriteLine();

            int totalItems = 0;
            int totalCats = 0;
            int multiTabBoxes = 0;
            var problems = new List<string>();

            // 条目路径 → 出现次数（跨分类重复检测）
            var pathCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var pathWhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var renderer = new GroupPanelRenderer(theme))
            {
                for (int gi = 0; gi < layout.Groups.Count; gi++)
                {
                    FenceBox b = layout.Groups[gi];
                    totalCats += b.Tabs.Count;
                    if (b.Tabs.Count > 1) multiTabBoxes++;

                    Console.WriteLine("  ┌─ 盒 #" + gi + "  " + b.Bounds.X + "," + b.Bounds.Y
                        + "  " + b.Bounds.Width + "×" + b.Bounds.Height
                        + (b.Locked ? "  [锁定]" : "")
                        + (b.Collapsed ? "  [折叠]" : "")
                        + "  布局=" + ViewName(b.ViewMode)
                        + "  当前分类=" + (b.ActiveTab + 1) + "/" + b.Tabs.Count);

                    for (int ti = 0; ti < b.Tabs.Count; ti++)
                    {
                        FenceTab t = b.Tabs[ti];
                        totalItems += t.Items.Count;

                        // 容量按该分类自己的有效显示方式算（没单独设置就跟盒）
                        BoxViewMode vmode = b.ViewModeOf(t);
                        int headerH = renderer.HeaderHeight(b.Tabs.Count);
                        int cols, rows;
                        renderer.MeasureCapacity(b.Bounds, headerH, vmode,
                                                 out cols, out rows);
                        int capacity = cols * rows;

                        string title = string.IsNullOrEmpty(t.Title) ? "(未命名)" : t.Title;
                        string flag = (ti == b.ActiveTab) ? " ←当前" : "";

                        Console.WriteLine("  │  · " + (ti + 1) + " " + title
                            + "  条目 " + t.Items.Count
                            + "  容量 " + cols + "×" + rows + "=" + capacity
                            + "  显示=" + ViewName(vmode)
                            + (t.ViewMode.HasValue ? "(独立)" : "(跟盒)")
                            + flag);

                        if (t.Items.Count > capacity)
                        {
                            problems.Add("盒#" + gi + " 分类「" + title + "」有 " + t.Items.Count
                                + " 个条目，面板只装得下 " + capacity
                                + " 个 —— 会被裁掉 " + (t.Items.Count - capacity) + " 个");
                        }

                        if (t.Origin != null)
                            Console.WriteLine("  │      来源记录: " + t.Origin.Bounds.X + ","
                                + t.Origin.Bounds.Y + "  " + t.Origin.Bounds.Width + "×"
                                + t.Origin.Bounds.Height + "  布局=" + ViewName(t.Origin.ViewMode)
                                + (t.Origin.Locked ? " [锁]" : "")
                                + (t.Origin.Collapsed ? " [折叠]" : ""));
                        else
                            Console.WriteLine("  │      来源记录: 无（拆开时按自动排布）");

                        foreach (GroupItem it in t.Items)
                        {
                            if (string.IsNullOrEmpty(it.FullPath)) continue;
                            if (pathCount.ContainsKey(it.FullPath))
                            {
                                pathCount[it.FullPath]++;
                                problems.Add("重复条目: " + it.Name + " 同时在「"
                                    + pathWhere[it.FullPath] + "」与「盒#" + gi + "/" + title + "」");
                            }
                            else
                            {
                                pathCount[it.FullPath] = 1;
                                pathWhere[it.FullPath] = "盒#" + gi + "/" + title;
                            }
                        }
                    }

                    Console.WriteLine("  └" + new string('─', 58));
                    Console.WriteLine();
                }
            }

            Console.WriteLine("  【汇总】");
            Console.WriteLine("  " + new string('-', 63));
            Console.WriteLine("  收纳盒 " + layout.Groups.Count + " 个"
                + "（其中 " + multiTabBoxes + " 个已组合成格子组）");
            Console.WriteLine("  分类共 " + totalCats + " 个");
            Console.WriteLine("  条目共 " + totalItems + " 条，去重后 " + pathCount.Count + " 条");
            if (GroupLayout.LastLoadSkipped > 0)
                Console.WriteLine("  路径不合法被丢弃: " + GroupLayout.LastLoadSkipped + " 条");
            Console.WriteLine();

            if (problems.Count == 0)
            {
                Console.WriteLine("  [结论] 存档结构正常：无溢出、无重复条目。");
                Console.WriteLine("          若界面上仍显示异常，问题在渲染 / 尺寸而非存档。");
            }
            else
            {
                Console.WriteLine("  [发现 " + problems.Count + " 处问题]");
                for (int i = 0; i < problems.Count && i < 40; i++)
                    Console.WriteLine("    " + (i + 1) + ". " + problems[i]);
                if (problems.Count > 40)
                    Console.WriteLine("    … 其余 " + (problems.Count - 40) + " 处省略");
            }

            Footer("诊断完成（只读，未改动任何文件）");
            return 0;
        }

        private static string ViewName(BoxViewMode v)
        {
            switch (v)
            {
                case BoxViewMode.SmallIcons: return "小图标";
                case BoxViewMode.List: return "列表";
                default: return "大图标";
            }
        }

        /// <summary>
        /// 描述布局规模：收纳盒数 / 分类总数 / 已组合的盒子数。
        ///
        /// 一个收纳盒可以含多个分类（标签页），所以「盒子数」与「分类数」
        /// 是两个不同的量，输出时分清。
        /// </summary>
        private static string DescribeLayout(GroupLayout layout)
        {
            if (layout == null) return "  (无布局)";

            int cats = 0;
            int grouped = 0;

            foreach (FenceBox b in layout.Groups)
            {
                cats += b.Tabs.Count;
                if (b.Tabs.Count > 1) grouped++;
            }

            string text = "  收纳盒 " + layout.Groups.Count + " 个 · 分类共 " + cats + " 个";
            if (grouped > 0) text += "（其中 " + grouped + " 个已组合成格子组）";
            return text;
        }

        /// <summary>
        /// 把 ListView 中的图标与文件系统实体对应起来。
        ///
        /// 关键点：Windows 默认隐藏已知扩展名，ListView 显示「文档」，
        /// 而文件系统里是「文档.docx」。因此索引同时收录
        /// 「完整文件名」和「去扩展名」两个键。
        /// </summary>
        private static Dictionary<string, DesktopIcon> BuildNameIndex(IList<DesktopIcon> icons)
        {
            var map = new Dictionary<string, DesktopIcon>(StringComparer.OrdinalIgnoreCase);

            foreach (var icon in icons)
            {
                if (!map.ContainsKey(icon.Name))
                    map[icon.Name] = icon;

                string noExt = Path.GetFileNameWithoutExtension(icon.Name);
                if (!string.IsNullOrEmpty(noExt)
                    && !string.Equals(noExt, icon.Name, StringComparison.OrdinalIgnoreCase)
                    && !map.ContainsKey(noExt))
                {
                    map[noExt] = icon;
                }
            }

            return map;
        }

        private static List<LayoutItem> BuildLayoutItems(
            DesktopLayer layer,
            Dictionary<string, DesktopIcon> nameIndex,
            out int virtualCount)
        {
            var classifier = new Classifier();
            var items = new List<LayoutItem>();
            virtualCount = 0;

            int n = layer.Count;
            for (int i = 0; i < n; i++)
            {
                string text = layer.GetItemText(i);
                string display = Display(text);

                DesktopIcon match;
                IconCategory category;

                if (nameIndex.TryGetValue(display, out match))
                {
                    category = classifier.Classify(match.FullPath, match.IsDirectory);
                }
                else
                {
                    // 系统虚拟图标（回收站/此电脑等）不在文件系统中
                    category = IconCategory.Other;
                    virtualCount++;
                }

                items.Add(new LayoutItem
                {
                    Index = i,
                    Name = display,
                    Category = category
                });
            }

            return items;
        }

        private static string GetSnapshotPath(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!string.IsNullOrEmpty(a) && !a.StartsWith("-"))
                    return a;
            }
            return PositionStore.DefaultPath();
        }

        /// <summary>去掉 ListView 标题中的制表符（部分项目带隐藏后缀）。</summary>
        private static string Display(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int tab = name.IndexOf('\t');
            return tab >= 0 ? name.Substring(0, tab) : name;
        }

        private static int CountOf(IDictionary<IconCategory, int> map, IconCategory key)
        {
            int n;
            return map.TryGetValue(key, out n) ? n : 0;
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        private static void Header(string title)
        {
            Console.WriteLine("=====================================================");
            Console.WriteLine("  " + title);
            Console.WriteLine("=====================================================");
            Console.WriteLine();
        }

        private static void Footer(string text)
        {
            Console.WriteLine();
            Console.WriteLine("=====================================================");
            Console.WriteLine("  " + text);
            Console.WriteLine("=====================================================");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("桌面图标管理器");
            Console.WriteLine();
            Console.WriteLine("  ★ 直接双击 exe（不带任何参数）= 启动桌面覆盖层");
            Console.WriteLine("    只读探测请显式加 --probe");
            Console.WriteLine();
            Console.WriteLine("用法: DesktopIconManager.exe [命令]");
            Console.WriteLine();
            Console.WriteLine("  --probe      只读扫描桌面文件，输出分类统计（默认）");
            Console.WriteLine("  --diagnose   诊断桌面图层结构，检测冲突程序");
            Console.WriteLine("  --positions  只读列出当前图标坐标");
            Console.WriteLine("  --preview    预览布局方案（不写入）");
            Console.WriteLine("  --apply      快照 + 应用布局（会移动图标）");
            Console.WriteLine("  --restore    从快照还原图标位置");
            Console.WriteLine("  --ui         样式预览界面（不修改桌面）");
            Console.WriteLine("               可选主题: light(默认) / dark / glass");
            Console.WriteLine("  --shell      桌面覆盖层模式（核心功能）");
            Console.WriteLine("               接管桌面，显示可拖拽的收纳盒");
            Console.WriteLine("               拖动自动吸附对齐；盒子拖到一起组合成格子组");
            Console.WriteLine("               右键可切换布局：大图标 / 小图标 / 列表");
            Console.WriteLine("               默认分离控制台、常驻托盘");
            Console.WriteLine("  --shell --console  同上，但保留控制台窗口（便于看日志）");
            Console.WriteLine("  --settings   打开设置窗口（不接管桌面，可随时运行）");
            Console.WriteLine("               主题 / 尺寸 / 吸附 / 分类规则都在这里改");
Console.WriteLine("  --show-icons 应急：恢复系统桌面图标");
        Console.WriteLine("  --stats      输出版本 / 数据目录 / 布局 / 性能开关 / 运行指标");
        Console.WriteLine("  --dump-layout 只读诊断：打印存档解析后的每个盒 / 每个分类的");
        Console.WriteLine("               条目数、来源记录、面板容量，并报告溢出与重复条目");
            Console.WriteLine();
            Console.WriteLine("  --utf8       强制控制台 UTF-8 输出（仅 UTF-8 终端需要）");
            Console.WriteLine();
            Console.WriteLine("建议顺序: --diagnose  →  --preview  →  --apply");
            Console.WriteLine("          --ui    查看分组面板观感");
            Console.WriteLine("          --shell 启动完整桌面整理");
        }
    }
}