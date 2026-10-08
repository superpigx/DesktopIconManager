using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 桌面图层定位器与 ListView 访问器。
    ///
    /// 原理（Windows 公开机制）：
    ///   桌面图标实际由一个 ListView 控件承载，层级为：
    ///     Progman / WorkerW
    ///       └─ SHELLDLL_DefView
    ///            └─ SysListView32   ← 图标就是它的列表项
    ///
    ///   由于该控件位于 explorer.exe 进程内，读写其中的数据
    ///   需借助跨进程内存（VirtualAllocEx + Read/WriteProcessMemory）。
    ///   这是微软文档公开的标准做法。
    /// </summary>
    internal sealed class DesktopLayer : IDisposable
    {
        private const string ClassProgman = "Progman";
        private const string ClassDefView = "SHELLDLL_DefView";
        private const string ClassListView = "SysListView32";

        private const int TextCapacity = 260;          // 图标标题最大字符数
        private readonly int _textBufferBytes = TextCapacity * 2;

        private IntPtr _hwndListView = IntPtr.Zero;
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _remoteStruct = IntPtr.Zero;
        private IntPtr _remoteText = IntPtr.Zero;
        private IntPtr _remotePoint = IntPtr.Zero;
        private bool _disposed;

        public IntPtr ListViewHandle { get { return _hwndListView; } }
        public IntPtr DefViewHandle { get; private set; }
        public IntPtr OwnerHandle { get; private set; }
        public uint ExplorerPid { get; private set; }

        private DesktopLayer() { }

        /// <summary>
        /// 定位桌面 ListView 并打开跨进程访问句柄。
        /// 返回 null 表示当前桌面并非标准结构（例如被第三方桌面工具接管）。
        /// </summary>
        public static DesktopLayer Attach()
        {
            IntPtr defView, owner;
            IntPtr lv = LocateListView(out defView, out owner);

            if (lv == IntPtr.Zero)
                return null;

            var layer = new DesktopLayer();
            layer._hwndListView = lv;
            layer.DefViewHandle = defView;
            layer.OwnerHandle = owner;

            uint pid;
            NativeMethods.GetWindowThreadProcessId(lv, out pid);
            layer.ExplorerPid = pid;

            const uint access = NativeMethods.PROCESS_VM_OPERATION
                              | NativeMethods.PROCESS_VM_READ
                              | NativeMethods.PROCESS_VM_WRITE
                              | NativeMethods.PROCESS_QUERY_INFORMATION;

            layer._hProcess = NativeMethods.OpenProcess(access, false, pid);
            if (layer._hProcess == IntPtr.Zero)
                return null;

            if (!layer.AllocateRemoteBuffers())
            {
                layer.Dispose();
                return null;
            }

            return layer;
        }

        /// <summary>查找桌面 ListView 句柄。</summary>
        private static IntPtr LocateListView(out IntPtr defView, out IntPtr owner)
        {
            defView = IntPtr.Zero;
            owner = IntPtr.Zero;

            // 路径 1：Progman → SHELLDLL_DefView
            IntPtr progman = NativeMethods.FindWindow(ClassProgman, null);
            if (progman != IntPtr.Zero)
            {
                IntPtr dv = NativeMethods.FindWindowEx(progman, IntPtr.Zero, ClassDefView, null);
                if (dv != IntPtr.Zero)
                {
                    owner = progman;
                    defView = dv;
                }
            }

            // 路径 2：遍历顶层窗口，找承载 SHELLDLL_DefView 的 WorkerW
            if (defView == IntPtr.Zero)
            {
                IntPtr foundDefView = IntPtr.Zero;
                IntPtr foundOwner = IntPtr.Zero;

                NativeMethods.EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
                {
                    IntPtr dv = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, ClassDefView, null);
                    if (dv != IntPtr.Zero)
                    {
                        foundDefView = dv;
                        foundOwner = hWnd;
                        return false; // 找到即停止
                    }
                    return true;
                };

                NativeMethods.EnumWindows(callback, IntPtr.Zero);
                GC.KeepAlive(callback);

                defView = foundDefView;
                owner = foundOwner;
            }

            if (defView == IntPtr.Zero)
                return IntPtr.Zero;

            return NativeMethods.FindWindowEx(defView, IntPtr.Zero, ClassListView, null);
        }

        private bool AllocateRemoteBuffers()
        {
            int structSize = Marshal.SizeOf(typeof(NativeMethods.LVITEM));

            _remoteStruct = NativeMethods.VirtualAllocEx(
                _hProcess, IntPtr.Zero, (IntPtr)structSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            _remoteText = NativeMethods.VirtualAllocEx(
                _hProcess, IntPtr.Zero, (IntPtr)_textBufferBytes,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            _remotePoint = NativeMethods.VirtualAllocEx(
                _hProcess, IntPtr.Zero, (IntPtr)8,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);

            return _remoteStruct != IntPtr.Zero
                && _remoteText != IntPtr.Zero
                && _remotePoint != IntPtr.Zero;
        }

        // ==================== 读取 ====================

        /// <summary>图标数量。</summary>
        public int Count
        {
            get
            {
                EnsureAlive();
                IntPtr r = NativeMethods.Send(_hwndListView, NativeMethods.LVM_GETITEMCOUNT,
                                              IntPtr.Zero, IntPtr.Zero);
                return r.ToInt32();
            }
        }

        /// <summary>读取指定序号的图标标题。</summary>
        public string GetItemText(int index)
        {
            EnsureAlive();

            var item = new NativeMethods.LVITEM();
            item.mask = NativeMethods.LVIF_TEXT;
            item.iItem = index;
            item.iSubItem = 0;
            item.pszText = _remoteText;
            item.cchTextMax = TextCapacity;

            int structSize = Marshal.SizeOf(typeof(NativeMethods.LVITEM));
            byte[] structBytes = StructToBytes(item);
            IntPtr written;
            if (!NativeMethods.WriteProcessMemory(_hProcess, _remoteStruct, structBytes,
                                                 (IntPtr)structSize, out written))
                return string.Empty;

            NativeMethods.Send(_hwndListView, NativeMethods.LVM_GETITEMTEXTW,
                               (IntPtr)index, _remoteStruct);

            byte[] textBytes = new byte[_textBufferBytes];
            IntPtr read;
            if (!NativeMethods.ReadProcessMemory(_hProcess, _remoteText, textBytes,
                                                (IntPtr)_textBufferBytes, out read))
                return string.Empty;

            string s = Encoding.Unicode.GetString(textBytes);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }

        /// <summary>读取指定序号的图标坐标（ListView 客户区坐标）。</summary>
        public Point GetItemPosition(int index)
        {
            EnsureAlive();

            NativeMethods.Send(_hwndListView, NativeMethods.LVM_GETITEMPOSITION,
                               (IntPtr)index, _remotePoint);

            byte[] buf = new byte[8];
            IntPtr read;
            if (!NativeMethods.ReadProcessMemory(_hProcess, _remotePoint, buf, (IntPtr)8, out read))
                return new Point(-1, -1);

            var pt = BytesToStruct<NativeMethods.POINT>(buf);
            return new Point(pt.X, pt.Y);
        }

        /// <summary>读取图标间距（大图标视图）。</summary>
        public Size GetIconSpacing()
        {
            EnsureAlive();

            IntPtr r = NativeMethods.Send(_hwndListView, NativeMethods.LVM_GETITEMSPACING,
                                          (IntPtr)1, IntPtr.Zero);
            int v = r.ToInt32();
            int cx = v & 0xFFFF;
            int cy = (v >> 16) & 0xFFFF;

            if (cx <= 0) cx = 75;
            if (cy <= 0) cy = 75;
            return new Size(cx, cy);
        }

        /// <summary>ListView 客户区尺寸。</summary>
        public Size GetClientSize()
        {
            EnsureAlive();
            NativeMethods.RECT rc;
            if (!NativeMethods.GetClientRect(_hwndListView, out rc))
                return new Size(1920, 1080);
            return new Size(rc.Width, rc.Height);
        }

        // ==================== 写入 ====================

        /// <summary>
        /// 设置图标坐标。这是唯一会改动桌面的操作。
        /// 调用方必须在此之前完成位置快照。
        /// </summary>
        public void SetItemPosition(int index, int x, int y)
        {
            EnsureAlive();

            if (x < 0) x = 0;
            if (y < 0) y = 0;

            NativeMethods.Send(_hwndListView, NativeMethods.LVM_SETITEMPOSITION,
                               (IntPtr)index, NativeMethods.MakeLParam(x, y));
        }

        /// <summary>请求重绘指定范围。</summary>
        public void Refresh(int firstIndex, int count)
        {
            if (_hwndListView == IntPtr.Zero) return;
            NativeMethods.Send(_hwndListView, NativeMethods.LVM_REDRAWITEMS,
                               (IntPtr)firstIndex, (IntPtr)count);
        }

        // ==================== 环境诊断 ====================

        /// <summary>描述当前桌面图层结构，用于故障排查。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendFormat("  桌面图层: ListView=0x{0:X8}  DefView=0x{1:X8}  Owner=0x{2:X8}\r\n",
                _hwndListView.ToInt64(), DefViewHandle.ToInt64(), OwnerHandle.ToInt64());

            string ownerClass = GetClassNameOf(OwnerHandle);
            sb.AppendFormat("  宿主窗口类名: {0}\r\n", string.IsNullOrEmpty(ownerClass) ? "?" : ownerClass);
            sb.AppendFormat("  explorer PID: {0}\r\n", ExplorerPid);
            sb.AppendFormat("  进程位数匹配: {0}\r\n", Environment.Is64BitProcess ? "64 位" : "32 位");
            return sb.ToString();
        }

        private static string GetClassNameOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            int n = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : string.Empty;
        }

        /// <summary>
        /// 检测是否存在冲突的第三方桌面管理程序。
        /// 两个程序同时接管桌面图层会导致图标行为异常。
        /// </summary>
        public static IList<string> DetectConflicts()
        {
            var conflicts = new List<string>();
            string[] known =
            {
                "360DesktopLite64", "360DesktopLite", "360DesktopService64",
                "DesklitePlatform", "360QuickPreview",
                "DeskBox", "CloudDriveDesktopWidgetIndependent"
            };

            foreach (string name in known)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }

                if (procs.Length > 0)
                {
                    conflicts.Add(string.Format("{0} (PID {1})", name, procs[0].Id));
                }

                foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }

            return conflicts;
        }

        // ==================== 辅助 ====================

        private static byte[] StructToBytes(object value)
        {
            Type t = value.GetType();
            int size = Marshal.SizeOf(t);
            byte[] bytes = new byte[size];
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, p, false);
                Marshal.Copy(p, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
            return bytes;
        }

        private static T BytesToStruct<T>(byte[] bytes) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, p, size);
                return (T)Marshal.PtrToStructure(p, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }

        private void EnsureAlive()
        {
            if (_disposed)
                throw new ObjectDisposedException("DesktopLayer");
            if (_hwndListView == IntPtr.Zero || !NativeMethods.IsWindow(_hwndListView))
                throw new InvalidOperationException("桌面 ListView 已失效（explorer 可能已重启），请重新附加。");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hProcess != IntPtr.Zero)
            {
                if (_remoteStruct != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_hProcess, _remoteStruct, IntPtr.Zero, NativeMethods.MEM_RELEASE);
                if (_remoteText != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_hProcess, _remoteText, IntPtr.Zero, NativeMethods.MEM_RELEASE);
                if (_remotePoint != IntPtr.Zero)
                    NativeMethods.VirtualFreeEx(_hProcess, _remotePoint, IntPtr.Zero, NativeMethods.MEM_RELEASE);

                NativeMethods.CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
            _hwndListView = IntPtr.Zero;
        }
    }
}