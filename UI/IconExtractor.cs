using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 从系统 Shell 提取文件/文件夹图标。
    ///
    /// 优先使用 SHGetImageList（48×48 额外大图标，清晰度好），
    /// 失败时回退到 SHGetFileInfo（32×32）。
    /// 所有 HICON 都会及时销毁，避免句柄泄漏。
    /// </summary>
    public sealed class IconExtractor : IDisposable
    {
        private const int SHIL_LARGE = 0;
        private const int SHIL_EXTRALARGE = 2;

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint SHGFI_SYSICONINDEX = 0x000004000;

        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        private const int ILD_TRANSPARENT = 0x00000001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                                   ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid,
                                                 out IImageList ppv);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        private IImageList _imageList;
        private bool _imageListTried;

        /// <summary>图标本体。按扩展名或完整路径缓存（见 BuildCacheKey）。</summary>
        private readonly Dictionary<string, Icon> _cache =
            new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 最近使用顺序（表头 = 最近使用）。
        /// 缓存此前**没有上限**：.lnk/.exe/.url 等按完整路径各存一份，
        /// 每个 48×48 图标 10~40KB，几百个快捷方式就是好几 MB，且只增不减。
        /// 现在按 LRU 封顶。
        /// </summary>
        private readonly LinkedList<string> _lru = new LinkedList<string>();

        private int _maxCache = 200;

        /// <summary>缓存条目上限（LRU 淘汰）。最小 16。</summary>
        public int MaxCache
        {
            get { return _maxCache; }
            set { _maxCache = Math.Max(16, value); TrimCache(); }
        }

        /// <summary>当前缓存了多少个图标（供 --stats 观察）。</summary>
        public int CacheCount { get { return _cache.Count; } }

        /// <summary>当前是否可用高清图标源。</summary>
        public bool HighQualityAvailable { get { return EnsureImageList() != null; } }

        /// <summary>把某个键标记为最近使用。</summary>
        private void TouchLru(string key)
        {
            if (_lru.Count > 0 && _lru.First != null && _lru.First.Value == key) return;

            // LinkedList.Remove 是 O(n)；缓存上限数百级，且只在 miss/命中时走一次，
            // 相比之前"只增不减"的内存增长，这点开销完全值得
            LinkedListNode<string> found = _lru.Find(key);
            if (found != null) _lru.Remove(found);

            _lru.AddFirst(key);
        }

        /// <summary>按上限淘汰最久未使用的条目。</summary>
        private void TrimCache()
        {
            while (_cache.Count > _maxCache && _lru.Count > 0)
            {
                LinkedListNode<string> last = _lru.Last;
                if (last == null) break;

                string victim = last.Value;
                _lru.RemoveLast();

                Icon icon;
                if (_cache.TryGetValue(victim, out icon))
                {
                    _cache.Remove(victim);
                    if (icon != null)
                    {
                        try { icon.Dispose(); }
                        catch { }
                    }
                }
            }
        }

        private IImageList EnsureImageList()
        {
            if (_imageListTried) return _imageList;
            _imageListTried = true;

            try
            {
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                IImageList list;
                // 先试 48×48（额外大）
                int hr = SHGetImageList(SHIL_EXTRALARGE, ref iid, out list);
                if (hr == 0 && list != null) { _imageList = list; return _imageList; }

                hr = SHGetImageList(SHIL_LARGE, ref iid, out list);
                if (hr == 0 && list != null) { _imageList = list; return _imageList; }
            }
            catch
            {
                // 忽略，走回退路径
            }
            return null;
        }

        /// <summary>
        /// 取图标。文件夹与文件都支持。
        /// 返回的 Icon 是副本，调用方负责 Dispose。
        /// </summary>
        public Icon GetIcon(string path, bool isDirectory)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string key = BuildCacheKey(path, isDirectory);

            Icon cached;
            if (_cache.TryGetValue(key, out cached))
            {
                TouchLru(key);
                return cached;
            }

            Icon icon = LoadIcon(path, isDirectory);
            if (icon != null)
            {
                _cache[key] = icon;
                TouchLru(key);
                TrimCache();
            }

            return icon;
        }

        /// <summary>
        /// 决定缓存键。
        ///
        /// 普通文件按「扩展名」缓存即可（同类文件图标相同）；
        /// 但下列类型的图标**因文件而异**，必须按完整路径缓存，
        /// 否则所有快捷方式会共用同一个图标：
        ///   .lnk → 图标取自目标程序
        ///   .exe / .ico / .msi / .scr / .cpl → 图标取自自身资源
        ///   .url → 图标取自站点
        /// </summary>
        private static string BuildCacheKey(string path, bool isDirectory)
        {
            if (isDirectory) return "@dir:" + path;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            switch (ext)
            {
                case ".lnk":
                case ".exe":
                case ".ico":
                case ".url":
                case ".msi":
                case ".scr":
                case ".cpl":
                case ".appref-ms":
                    return "@path:" + path;
                default:
                    return "@ext:" + ext;
            }
        }

        private static bool ExistsSafe(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }

        private Icon LoadIcon(string path, bool isDirectory)
        {
            bool exists = ExistsSafe(path);

            // ---- 路径 1：通过系统图像列表取高清图标 ----
            IImageList list = EnsureImageList();
            if (list != null)
            {
                Icon result = TryViaImageList(list, path, isDirectory, exists);
                if (result != null) return result;
            }

            // ---- 路径 2：SHGetFileInfo 直接取 32×32 ----
            return TryViaShGetFileInfo(path, isDirectory, exists);
        }

        /// <summary>
        /// 关键点：文件真实存在时**不能**加 SHGFI_USEFILEATTRIBUTES。
        ///
        /// 该标志的含义是「只按 dwFileAttributes 猜图标，不要去读真实文件」。
        /// 一旦加上，.lnk 快捷方式就只会得到通用的「快捷方式」图标，
        /// 而不是它指向的目标程序图标 —— 这正是之前快捷方式没图标的原因。
        ///
        /// 仅当文件不存在时才退回按扩展名猜。
        /// </summary>
        private Icon TryViaImageList(IImageList list, string path, bool isDirectory, bool exists)
        {
            try
            {
                var info = new SHFILEINFO();
                uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

                // 只要索引，不分配 HICON —— 避免句柄泄漏
                uint flags = SHGFI_SYSICONINDEX;
                if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;

                IntPtr res = SHGetFileInfo(path, attrs, ref info,
                                           (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (res == IntPtr.Zero || info.iIcon < 0) return null;

                IntPtr hIcon = IntPtr.Zero;
                int hr = list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref hIcon);
                if (hr != 0 || hIcon == IntPtr.Zero) return null;

                Icon result = CloneIcon(hIcon);
                DestroyIcon(hIcon);
                return result;
            }
            catch
            {
                return null;
            }
        }

        private Icon TryViaShGetFileInfo(string path, bool isDirectory, bool exists)
        {
            try
            {
                var info = new SHFILEINFO();
                uint attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                if (!exists) flags |= SHGFI_USEFILEATTRIBUTES;

                IntPtr res = SHGetFileInfo(path, attrs, ref info,
                                           (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                Icon result = CloneIcon(info.hIcon);
                DestroyIcon(info.hIcon);
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把 HICON 复制为托管 Icon。
        /// 必须复制：DestroyIcon 之后原始句柄失效，托管 Icon 会引用到已释放资源。
        /// </summary>
        private static Icon CloneIcon(IntPtr hIcon)
        {
            try
            {
                using (Icon temp = Icon.FromHandle(hIcon))
                {
                    return (Icon)temp.Clone();
                }
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            foreach (Icon icon in _cache.Values)
            {
                if (icon != null)
                {
                    try { icon.Dispose(); } catch { }
                }
            }
            _cache.Clear();
            _lru.Clear();
        }
    }
}