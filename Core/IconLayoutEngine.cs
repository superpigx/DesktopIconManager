using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DesktopIconManager.Core
{
    /// <summary>参与布局的一个图标（携带其在 ListView 中的序号）。</summary>
    public sealed class LayoutItem
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public IconCategory Category { get; set; }
    }

    /// <summary>布局方案：包含目标坐标与容量校验结果。</summary>
    public sealed class LayoutPlan
    {
        public IDictionary<int, Point> Targets { get; private set; }
        public int ColumnsNeeded { get; set; }
        public int ColumnsAvailable { get; set; }
        public int RowsPerColumn { get; set; }
        public bool WillOverflow { get; set; }
        public IList<string> Warnings { get; private set; }

        public LayoutPlan()
        {
            Targets = new Dictionary<int, Point>();
            Warnings = new List<string>();
        }

        public int MoveCount { get { return Targets.Count; } }
    }

    /// <summary>
    /// 网格布局引擎。
    ///
    /// 布局策略：
    ///   1. 按分类排序，同类图标聚在一起；
    ///   2. 每类占用若干「列」，列内自上而下填满再换下一列（与 Windows 默认一致）；
    ///   3. 分类之间留出空隙列，视觉上形成区块；
    ///   4. 若需要的列数超过屏幕可容纳列数，标记溢出并告警。
    ///
    /// 本类为纯计算，不涉及任何 I/O 或窗口消息。
    /// </summary>
    public static class IconLayoutEngine
    {
        /// <summary>分类的显示顺序：文件夹在最前，未归类压尾。</summary>
        private static readonly IconCategory[] CategoryOrder =
        {
            IconCategory.Folder,
            IconCategory.Document,
            IconCategory.Archive,
            IconCategory.Image,
            IconCategory.Video,
            IconCategory.Audio,
            IconCategory.Executable,
            IconCategory.Script,
            IconCategory.Shortcut,
            IconCategory.WebLink,
            IconCategory.Other
        };

        public static LayoutPlan Compute(
            IList<LayoutItem> items,
            Size clientSize,
            Size spacing,
            int gapColumns)
        {
            if (items == null) throw new ArgumentNullException("items");

            var plan = new LayoutPlan();

            if (spacing.Width <= 0) spacing.Width = 75;
            if (spacing.Height <= 0) spacing.Height = 75;
            if (gapColumns < 0) gapColumns = 0;

            plan.ColumnsAvailable = clientSize.Width / spacing.Width;
            plan.RowsPerColumn = clientSize.Height / spacing.Height;

            if (plan.RowsPerColumn < 1) plan.RowsPerColumn = 1;

            // 按既定分类顺序 + 名称排序，保证每次布局结果稳定
            var ordered = items
                .OrderBy(i => OrderOf(i.Category))
                .ThenBy(i => i.Name, StringComparer.CurrentCulture)
                .ToList();

            var groups = ordered.GroupBy(i => i.Category)
                                .OrderBy(g => OrderOf(g.Key))
                                .ToList();

            int currentColumn = 0;

            foreach (var group in groups)
            {
                var members = group.ToList();
                int columnsForThisGroup = CeilDiv(members.Count, plan.RowsPerColumn);

                for (int i = 0; i < members.Count; i++)
                {
                    int col = currentColumn + (i / plan.RowsPerColumn);
                    int row = i % plan.RowsPerColumn;

                    int x = col * spacing.Width;
                    int y = row * spacing.Height;

                    plan.Targets[members[i].Index] = new Point(x, y);
                }

                currentColumn += columnsForThisGroup + gapColumns;
            }

            plan.ColumnsNeeded = currentColumn - (groups.Count > 0 ? gapColumns : 0);
            plan.WillOverflow = plan.ColumnsNeeded > plan.ColumnsAvailable;

            if (plan.WillOverflow)
            {
                plan.Warnings.Add(string.Format(
                    "列数不足：需要 {0} 列，屏幕仅能容纳 {1} 列，部分图标将超出可视区域。",
                    plan.ColumnsNeeded, plan.ColumnsAvailable));
            }

            if (items.Count > plan.ColumnsAvailable * plan.RowsPerColumn)
            {
                plan.Warnings.Add(string.Format(
                    "图标总数 {0} 超过单屏容量 {1}（{2} 列 × {3} 行）。",
                    items.Count,
                    plan.ColumnsAvailable * plan.RowsPerColumn,
                    plan.ColumnsAvailable,
                    plan.RowsPerColumn));
            }

            return plan;
        }

        private static int OrderOf(IconCategory c)
        {
            for (int i = 0; i < CategoryOrder.Length; i++)
            {
                if (CategoryOrder[i] == c) return i;
            }
            return CategoryOrder.Length;
        }

        private static int CeilDiv(int a, int b)
        {
            if (b <= 0) return a;
            return (a + b - 1) / b;
        }

        /// <summary>生成人类可读的布局摘要。</summary>
        public static string Describe(LayoutPlan plan, IEnumerable<LayoutItem> items)
        {
            var byCat = items.GroupBy(i => i.Category)
                             .OrderBy(g => OrderOf(g.Key))
                             .Select(g => string.Format("{0}×{1}",
                                        Classifier.GetDisplayName(g.Key), g.Count()));

            return string.Format(
                "  分类分布: {0}\r\n" +
                "  网格: {1} 列 × {2} 行  需要 {3} 列{4}",
                string.Join("  ", byCat.ToArray()),
                plan.ColumnsAvailable, plan.RowsPerColumn,
                plan.ColumnsNeeded,
                plan.WillOverflow ? "  [溢出]" : "  [容量充足]");
        }
    }
}