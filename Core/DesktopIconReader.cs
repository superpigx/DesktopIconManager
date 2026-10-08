using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 一个桌面图标项的信息（只读快照）。
    /// </summary>
    public sealed class DesktopIcon
    {
        public string FullPath { get; set; }
        public string Name { get; set; }
        public bool IsDirectory { get; set; }
        public IconCategory Category { get; set; }

        /// <summary>图标在桌面上的位置。X/Y 为 -1 表示暂不可用（只读模式下常见）。</summary>
        public int GridX { get; set; }
        public int GridY { get; set; }

        /// <summary>原始位置，用于一键还原。</summary>
        public int OriginalX { get; set; }
        public int OriginalY { get; set; }

        public long SizeBytes { get; set; }
        public DateTime Modified { get; set; }

        public DesktopIcon()
        {
            GridX = -1;
            GridY = -1;
            OriginalX = -1;
            OriginalY = -1;
            SizeBytes = -1;
        }
    }

    /// <summary>
    /// 桌面图标读取器 —— 阶段1 只读探测。
    ///
    /// 设计原则：
    ///   1. 本类只做「读取」，不移动、不删除、不写入任何图标位置。
    ///   2. 图标清单来自桌面目录枚举（用户桌面 + 公用桌面），这是 Shell 的权威数据源。
    ///   3. 图标坐标需要通过 WorkerW 图层的ListView 读取，属于阶段2 的工作，
    ///      当前阶段坐标恒为 -1（未知）。
    ///
    /// 这样即使程序崩溃，也不会损坏你的桌面布局。
    /// </summary>
    public sealed class DesktopIconReader
    {
        private readonly Classifier _classifier;

        public DesktopIconReader(Classifier classifier)
        {
            if (classifier == null) throw new ArgumentNullException("classifier");
            _classifier = classifier;
        }

        /// <summary>
        /// 枚举桌面所有可见图标（用户桌面 + 公用桌面）。
        /// 只读操作，不修改任何内容。
        /// </summary>
        public IList<DesktopIcon> ReadIcons()
        {
            var result = new List<DesktopIcon>();

            foreach (string root in GetDesktopRoots())
            {
                result.AddRange(EnumerateRoot(root));
            }

            return result;
        }

        /// <summary>获取桌面目录列表：用户桌面 + 公用桌面。</summary>
        private IEnumerable<string> GetDesktopRoots()
        {
            var roots = new List<string>();

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(userProfile) && Directory.Exists(userProfile))
                roots.Add(userProfile);

            if (AppSettings.Current.IncludePublicDesktop)
            {
                // 公用桌面 = C:\Users\Public\Desktop
                //
                // 注意：这里**不能**用 SpecialFolder.CommonApplicationData ——
                // 那是 C:\ProgramData，拼出来是 C:\ProgramData\Desktop（根本不存在）。
                // 之前用错枚举，导致公用桌面上的图标一直没被读到。
                string publicDesktop =
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

                if (string.IsNullOrEmpty(publicDesktop))
                {
                    string pub = Environment.GetEnvironmentVariable("PUBLIC");
                    if (!string.IsNullOrEmpty(pub))
                        publicDesktop = Path.Combine(pub, "Desktop");
                }

                if (!string.IsNullOrEmpty(publicDesktop) && Directory.Exists(publicDesktop))
                    roots.Add(publicDesktop);
            }

            return roots;
        }

        private IEnumerable<DesktopIcon> EnumerateRoot(string root)
        {
            FileSystemInfo[] entries;
            try
            {
                // 无参重载：返回该目录下全部文件与子目录（含隐藏项）
                entries = new DirectoryInfo(root).GetFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            foreach (var fi in entries)
            {
                // 排除永不显示在桌面上的系统文件
                if (IsNonDisplayedSystemFile(fi.Name))
                    continue;

                DesktopIcon icon;
                try
                {
                    icon = BuildIcon(fi);
                }
                catch
                {
                    continue; // 单个条目失败不影响整体
                }
                yield return icon;
            }
        }

        /// <summary>
        /// 判断是否为桌面不显示的系统文件。
        /// desktop.ini 与 thumbs.db 由 Shell 内部维护，不会出现在桌面上，
        /// 若不排除会虚增图标数量。
        /// </summary>
        private static bool IsNonDisplayedSystemFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;

            return name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase);
        }

        private DesktopIcon BuildIcon(FileSystemInfo fi)
        {
            bool isDir = (fi.Attributes & FileAttributes.Directory) == FileAttributes.Directory;

            var icon = new DesktopIcon
            {
                FullPath = fi.FullName,
                Name = fi.Name,
                IsDirectory = isDir,
                Category = _classifier.Classify(fi.FullName, isDir)
            };

            var fileInfo = fi as FileInfo;
            if (fileInfo != null)
            {
                try
                {
                    icon.SizeBytes = fileInfo.Length;
                    icon.Modified = fileInfo.LastWriteTime;
                }
                catch
                {
                    // 权限不足时保持默认值
                }
            }

            return icon;
        }

        /// <summary>
        /// 按分类分组统计。仅用于报告展示。
        /// </summary>
        public IDictionary<IconCategory, int> SummarizeByCategory(IEnumerable<DesktopIcon> icons)
        {
            var map = new Dictionary<IconCategory, int>();

            foreach (var icon in icons)
            {
                int n;
                map.TryGetValue(icon.Category, out n);
                map[icon.Category] = n + 1;
            }

            return map;
        }
    }
}