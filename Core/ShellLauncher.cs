using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 调用系统 Shell 打开文件 / 文件夹，或弹出属性对话框。
    ///
    /// 背景：接管桌面后系统图标被隐藏，用户失去了原有的双击打开、
    /// 右键菜单等能力，必须由我们代为转调 Shell，否则这个工具只能看不能用。
    /// </summary>
    public static class ShellLauncher
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
        private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
        private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

        private const int SW_SHOWNORMAL = 1;

        /// <summary>用系统默认程序打开文件或文件夹。</summary>
        public static bool Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var info = new SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            info.fMask = SEE_MASK_FLAG_NO_UI;
            info.lpVerb = "open";
            info.lpFile = path;
            info.lpDirectory = Path.GetDirectoryName(path);
            info.nShow = SW_SHOWNORMAL;

            try { return ShellExecuteEx(ref info); }
            catch { return false; }
        }

        /// <summary>弹出系统「属性」对话框。</summary>
        public static bool ShowProperties(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var info = new SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            info.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI;
            info.lpVerb = "properties";
            info.lpFile = path;
            info.nShow = SW_SHOWNORMAL;

            try { return ShellExecuteEx(ref info); }
            catch { return false; }
        }

        /// <summary>在资源管理器中定位并选中该文件。</summary>
        public static bool OpenContainingFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                string args = File.Exists(path) || Directory.Exists(path)
                    ? "/select,\"" + path + "\""
                    : "\"" + (Path.GetDirectoryName(path) ?? path) + "\"";

                var psi = new ProcessStartInfo("explorer.exe", args);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>判断路径当前是否还存在。</summary>
        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch { return false; }
        }

        /// <summary>
        /// 打开路径失败时的兜底：改用系统默认动词重试一次。
        /// </summary>
        public static bool OpenWithFallback(string path)
        {
            if (Open(path)) return true;

            try
            {
                var psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}