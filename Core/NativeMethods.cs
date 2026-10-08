using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// Windows 公开 API 声明（user32 / kernel32）。
    ///
    /// 说明：这里的每个 API 都是微软官方文档公开的，与任何商业软件的私有实现无关。
    /// 桌面图标层采用业界通行的 WorkerW + SysListView32 方案。
    /// </summary>
    internal static class NativeMethods
    {
        // ==================== 窗口查找 ====================

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
                                                   string lpszClass, string lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        // ==================== DPI 感知 ====================

        /// <summary>Per-Monitor V2（Win10 1703+）。</summary>
        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

        /// <summary>Win10 1703+ 的 DPI 感知设置。旧系统会失败，需回退。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>Vista+ 的系统级 DPI 感知（回退方案）。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetProcessDPIAware();

        // ==================== 控制台 ====================

        /// <summary>分离控制台窗口（后台常驻模式下调用）。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeConsole();

        // ==================== 窗口消息 ====================

        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
                                                        IntPtr lParam, uint fuFlags,
                                                        uint uTimeout, out IntPtr lpdwResult);

        /// <summary>
        /// 带超时的同步消息发送。超时 5 秒，避免 explorer 卡死时拖住本程序。
        /// </summary>
        internal static IntPtr Send(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr result;
            SendMessageTimeout(hWnd, msg, wParam, lParam, SMTO_ABORTIFHUNG, 5000, out result);
            return result;
        }

        // ==================== 窗口几何 ====================

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        // ==================== 跨进程内存 ====================

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize,
                                                     uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize,
                                                  uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
                                                      byte[] lpBuffer, IntPtr nSize,
                                                      out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
                                                       byte[] lpBuffer, IntPtr nSize,
                                                       out IntPtr lpNumberOfBytesWritten);

        // ==================== 结构体 ====================

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }

            public override string ToString()
            {
                return string.Format("({0},{1})-({2},{3})", Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;

            public override string ToString()
            {
                return string.Format("({0},{1})", X, Y);
            }
        }

        /// <summary>
        /// LVITEMW 结构。使用 IntPtr 表示指针字段，
        /// 由 CLR 按当前平台（32/64 位）自动对齐，与 explorer 位数保持一致。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct LVITEM
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        // ==================== ListView 消息常量 ====================

        internal const uint LVM_FIRST            = 0x1000;
        internal const uint LVM_GETITEMCOUNT     = LVM_FIRST + 4;    // 0x1004
        internal const uint LVM_SETITEMPOSITION  = LVM_FIRST + 15;   // 0x100F
        internal const uint LVM_GETITEMPOSITION  = LVM_FIRST + 16;   // 0x1010
        internal const uint LVM_REDRAWITEMS      = LVM_FIRST + 21;   // 0x1015
        internal const uint LVM_GETITEMSPACING   = LVM_FIRST + 51;   // 0x1033
        internal const uint LVM_GETITEMTEXTW     = LVM_FIRST + 115;  // 0x1073

        internal const uint LVIF_TEXT = 0x0001;

        // ==================== 进程访问权限 ====================

        internal const uint PROCESS_VM_OPERATION     = 0x0008;
        internal const uint PROCESS_VM_READ          = 0x0010;
        internal const uint PROCESS_VM_WRITE         = 0x0020;
        internal const uint PROCESS_QUERY_INFORMATION = 0x0400;

        internal const uint MEM_COMMIT  = 0x1000;
        internal const uint MEM_RESERVE = 0x2000;
        internal const uint MEM_RELEASE = 0x8000;
        internal const uint PAGE_READWRITE = 0x04;

        /// <summary>把两个 16 位值打包进 lParam。用于 LVM_SETITEMPOSITION。</summary>
        internal static IntPtr MakeLParam(int low, int high)
        {
            return (IntPtr)((high << 16) | (low & 0xFFFF));
        }
    }
}