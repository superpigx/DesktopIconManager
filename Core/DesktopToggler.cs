using System;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 系统桌面图标显示开关。
    ///
    /// 原理：向 SHELLDLL_DefView 发送 WM_COMMAND(0x7402)，
    /// 这是 Shell 内部「显示/隐藏桌面图标」的命令。
    /// 它是「切换」语义而非「设置」语义，所以必须先探测当前状态，
    /// 否则会越切越乱。
    ///
    /// 安全性：探测依据是 SysListView32 的可见性（IsWindowVisible），
    /// 因此 Hide/Show 都是幂等的 —— 重复调用不会出错。
    /// </summary>
    public static class DesktopToggler
    {
        private const uint WM_COMMAND = 0x0111;
        private const int ToggleDesktopIcons = 0x7402;

        private const string ClassDefView = "SHELLDLL_DefView";
        private const string ClassProgman = "Progman";
        private const string ClassListView = "SysListView32";

        /// <summary>找到桌面图标的 ListView 句柄（找不到返回 IntPtr.Zero）。</summary>
        public static IntPtr FindListView()
        {
            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;

            return NativeMethods.FindWindowEx(defView, IntPtr.Zero, ClassListView, null);
        }

        /// <summary>找到承载桌面图标的 SHELLDLL_DefView 句柄。</summary>
        public static IntPtr FindDefView()
        {
            // 路径 1：Progman → SHELLDLL_DefView
            IntPtr progman = NativeMethods.FindWindow(ClassProgman, null);
            if (progman != IntPtr.Zero)
            {
                IntPtr dv = NativeMethods.FindWindowEx(progman, IntPtr.Zero, ClassDefView, null);
                if (dv != IntPtr.Zero) return dv;
            }

            // 路径 2：遍历顶层窗口找 WorkerW 变体
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
            {
                IntPtr dv = NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, ClassDefView, null);
                if (dv != IntPtr.Zero)
                {
                    found = dv;
                    return false;
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            return found;
        }

        /// <summary>桌面图标当前是否可见。</summary>
        public static bool AreIconsVisible()
        {
            IntPtr listView = FindListView();
            if (listView == IntPtr.Zero) return false;
            return NativeMethods.IsWindowVisible(listView);
        }

        /// <summary>隐藏桌面图标。已隐藏则不做任何事。</summary>
        public static bool HideIcons()
        {
            if (!AreIconsVisible()) return true;
            return SendToggle();
        }

        /// <summary>显示桌面图标。已显示则不做任何事。</summary>
        public static bool ShowIcons()
        {
            if (AreIconsVisible()) return true;
            return SendToggle();
        }

        private static bool SendToggle()
        {
            IntPtr defView = FindDefView();
            if (defView == IntPtr.Zero) return false;

            try
            {
                NativeMethods.Send(defView, WM_COMMAND, (IntPtr)ToggleDesktopIcons, IntPtr.Zero);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>描述当前状态，用于诊断。</summary>
        public static string Describe()
        {
            IntPtr listView = FindListView();
            var sb = new StringBuilder();
            sb.AppendFormat("  DefView=0x{0:X8}  ListView=0x{1:X8}\r\n",
                FindDefView().ToInt64(), listView.ToInt64());
            sb.AppendFormat("  桌面图标可见: {0}\r\n", AreIconsVisible() ? "是" : "否");
            return sb.ToString();
        }
    }
}