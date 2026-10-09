using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
            // 路径可能来自存档文件（外部数据）—— 打开前必须校验，
            // 否则被篡改的 groups.txt 能诱导打开任意程序（存档投毒）
            if (!PathSafety.IsOpenable(path)) return false;

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
            if (!PathSafety.IsOpenable(path)) return false;

            var info = new SHELLEXECUTEINFO();
            info.cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO));
            info.fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI;
            info.lpVerb = "properties";
            info.lpFile = path;
            info.nShow = SW_SHOWNORMAL;

            try { return ShellExecuteEx(ref info); }
            catch { return false; }
        }

        /// <summary>
        /// 在资源管理器中定位并选中该文件。
        ///
        /// ★ 这里修掉了一处真实的注入面：
        ///   旧实现把路径直接拼进 `explorer.exe "/select,\"path\""`，
        ///   文件名里只要带一个双引号，参数结构就被破坏，
        ///   最坏会定位/打开到意外路径（引号注入）。
        ///
        ///   现在改走 SHOpenFolderAndSelectItems —— 传 PIDL 而不是命令行，
        ///   根本不存在"拼字符串"这件事，也从根上消除了转义问题。
        ///   该 API 不可用时才回退到旧写法（并且回退分支会先做转义）。
        /// </summary>
        public static bool OpenContainingFolder(string path)
        {
            if (!PathSafety.IsPlausible(path)) return false;

            try
            {
                if (!(File.Exists(path) || Directory.Exists(path)))
                {
                    string parent = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parent)) return false;
                    if (!PathSafety.IsPlausible(parent)) return false;
                    if (!Directory.Exists(parent)) return false;

                    path = parent;
                }

                if (TrySelectViaShell(path)) return true;
                return TrySelectViaExplorer(path);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHParseDisplayName(
            string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        /// <summary>
        /// cidl=0 且传入的是目标文件自身的 PIDL 时，
        /// Shell 会打开其所在文件夹并选中它 —— 正是我们要的"/select"效果。
        /// </summary>
        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        private static bool TrySelectViaShell(string path)
        {
            IntPtr pidl = IntPtr.Zero;

            try
            {
                uint flags;
                SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out flags);
                if (pidl == IntPtr.Zero) return false;

                int hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                return hr >= 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    try { ILFree(pidl); }
                    catch { }
                }
            }
        }

        /// <summary>回退分支：拼命令行，但先把引号转义干净。</summary>
        private static bool TrySelectViaExplorer(string path)
        {
            try
            {
                string quoted = PathSafety.QuoteArgument(path);
                string args = File.Exists(path) || Directory.Exists(path)
                    ? "/select," + quoted
                    : quoted;

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
        /// 为目标创建一个 .lnk 快捷方式（供右键菜单「粘贴快捷方式」用）。
        ///
        /// 走系统自带的 WScript.Shell：它内部就是 IShellLink 的封装，
        /// 用 late binding 调用，既不需要引用互操作程序集，
        /// 也不会像手写 vtable 那样容易错位。
        /// 任何一步失败都返回 false，由调用方跳过这一项。
        /// </summary>
        public static bool CreateShortcut(string target, string linkPath)
        {
            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(linkPath)) return false;
            if (!Exists(target)) return false;

            object shell = null;
            object link = null;

            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;

                shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                link = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });

                if (link == null) return false;

                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty,
                    null, link, new object[] { target });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty,
                    null, link, new object[] { Path.GetDirectoryName(target) });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod,
                    null, link, null);

                return Exists(linkPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                Release(link);
                Release(shell);
            }
        }

        /// <summary>
        /// 释放 COM 对象。
        ///
        /// WScript.Shell 这类对象不显式释放会一直挂在进程的 COM 表里，
        /// 反复「粘贴快捷方式」几次就会攒出可观的句柄与内存。
        /// </summary>
        private static void Release(object comObject)
        {
            if (comObject == null) return;

            try
            {
                if (Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
            catch
            {
                // 已经不是 COM 对象了，无所谓
            }
        }

        /// <summary>
        /// 打开路径失败时的兜底：改用系统默认动词重试一次。
        /// </summary>
        public static bool OpenWithFallback(string path)
        {
            if (Open(path)) return true;

            if (!PathSafety.IsOpenable(path)) return false;

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