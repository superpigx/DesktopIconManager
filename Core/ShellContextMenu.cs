using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 调用 Windows 原生 Shell 右键菜单（IContextMenu）。
    ///
    /// 为什么不能自己拼菜单：
    ///   用户右键一个文件时，期望看到的是「打开方式 / 7-Zip / 发送到 / 属性」
    ///   这些由已安装软件注册的菜单项。自绘菜单永远追不上，必须转调系统。
    ///
    /// 实现要点：
    ///   · Shell 扩展菜单是 owner-draw 的，会向宿主窗口发 WM_DRAWITEM /
    ///     WM_MEASUREITEM，所以窗体必须在 WndProc 里转交给 IContextMenu2/3，
    ///     否则菜单项会显示成空白。
    ///   · 优先用 IContextMenu3，回退 IContextMenu2，再回退 IContextMenu。
    /// </summary>
    public sealed class ShellContextMenu : IDisposable
    {
        private const uint WM_DRAWITEM = 0x002B;
        private const uint WM_MEASUREITEM = 0x002C;
        private const uint WM_INITMENUPOPUP = 0x0117;
        private const uint WM_MENUCHAR = 0x0120;

        private const uint CMF_NORMAL = 0x00000000;
        private const uint CMF_EXTENDEDVERBS = 0x00000100;

        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;

        private static ShellContextMenu _active;

        private IntPtr _parentPidl;
        private IntPtr _childPidl;
        private readonly IntPtr _hwndOwner;

        private IContextMenu _menu;
        private IContextMenu2 _menu2;
        private IContextMenu3 _menu3;
        private bool _disposed;

        // ============================================================
        //  COM 声明
        // ============================================================

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214E4-0000-0000-C000-000000000046")]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
                                               uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType,
                                               IntPtr pReserved, IntPtr pszName, uint cchMax);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214F4-0000-0000-C000-000000000046")]
        private interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
                                               uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType,
                                               IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("BCFCE0A0-EC17-11d0-8D10-00A0C90F2719")]
        private interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu,
                                               uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(IntPtr pici);
            [PreserveSig] int GetCommandString(IntPtr idCmd, uint uType,
                                               IntPtr pReserved, IntPtr pszName, uint cchMax);
            [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam,
                                             IntPtr lParam, IntPtr plResult);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214E6-0000-0000-C000-000000000046")]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
                [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
            [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl,
                                            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                uint uFlags, out IntPtr ppidlOut);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CMINVOKECOMMANDINFO
        {
            public int cbSize;
            public int fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;        // MAKEINTRESOURCE(命令偏移)
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public int dwHotKey;
            public IntPtr hIcon;
        }

        // ============================================================
        //  原生 API
        // ============================================================

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
                                                 out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags,
                                                    int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ============================================================
        //  构造
        // ============================================================

        private ShellContextMenu(IntPtr hwndOwner, IntPtr parentPidl, IntPtr childPidl)
        {
            _hwndOwner = hwndOwner;
            _parentPidl = parentPidl;
            _childPidl = childPidl;
        }

        /// <summary>
        /// 为指定路径准备右键菜单。
        /// 返回 null 表示准备失败（调用方应回退到自绘菜单）。
        /// </summary>
        public static ShellContextMenu Prepare(IntPtr hwndOwner, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            IntPtr pidl = IntPtr.Zero;
            IntPtr parent = IntPtr.Zero;
            IntPtr parentPidl = IntPtr.Zero;

            try
            {
                uint attrs;
                int hr = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
                if (hr != 0 || pidl == IntPtr.Zero) return null;

                Guid iidShellFolder = typeof(IShellFolder).GUID;
                IntPtr childPidl;
                hr = SHBindToParent(pidl, ref iidShellFolder, out parent, out childPidl);
                if (hr != 0 || parent == IntPtr.Zero) return null;

                // 父目录自身的 PIDL 需要重新解析，供 InvokeCommand 使用
                string parentPath = System.IO.Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parentPath)) parentPath = path;

                uint a2;
                hr = SHParseDisplayName(parentPath, IntPtr.Zero, out parentPidl, 0, out a2);
                if (hr != 0) parentPidl = IntPtr.Zero;

                var ctx = new ShellContextMenu(hwndOwner, parentPidl, pidl);

                // 从父 ShellFolder 取 IContextMenu
                var folder = (IShellFolder)Marshal.GetObjectForIUnknown(parent);

                IntPtr ptr = Marshal.AllocCoTaskMem(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(ptr, childPidl);

                    Guid iidMenu = new Guid("000214E4-0000-0000-C000-000000000046");
                    IntPtr menuPtr;
                    hr = folder.GetUIObjectOf(hwndOwner, 1, ptr, ref iidMenu, IntPtr.Zero, out menuPtr);
                    if (hr != 0 || menuPtr == IntPtr.Zero) { ctx.Dispose(); return null; }

                    ctx._menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);

                    // 尽量取到更高版本，才能正确处理 owner-draw 消息
                    try { ctx._menu3 = (IContextMenu3)Marshal.GetObjectForIUnknown(menuPtr); }
                    catch { ctx._menu3 = null; }

                    if (ctx._menu3 == null)
                    {
                        try { ctx._menu2 = (IContextMenu2)Marshal.GetObjectForIUnknown(menuPtr); }
                        catch { ctx._menu2 = null; }
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }

                // pidl 的所有权转交给 ctx，由 Dispose 释放
                ctx._ownedPidl = pidl;
                pidl = IntPtr.Zero;

                return ctx;
            }
            catch
            {
                if (pidl != IntPtr.Zero) ILFree(pidl);
                if (parentPidl != IntPtr.Zero) ILFree(parentPidl);
                return null;
            }
        }

        private IntPtr _ownedPidl;

        // ============================================================
        //  显示
        // ============================================================

        /// <summary>
        /// 弹出菜单并执行用户选择的命令。
        /// 返回 false 表示菜单创建失败。
        /// </summary>
        public bool Show(Form owner, int screenX, int screenY)
        {
            IntPtr hMenu = IntPtr.Zero;

            try
            {
                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;

                const uint idCmdFirst = 1;
                const uint idCmdLast = 0x7FFF;

                int hr = _menu.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, CMF_NORMAL);
                if (hr < 0) return false;

                // 激活期间挂上，供窗体 WndProc 转发 owner-draw 消息
                _active = this;

                SetForegroundWindow(_hwndOwner);

                uint cmd = TrackPopupMenuEx(hMenu,
                    TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                    screenX, screenY, _hwndOwner, IntPtr.Zero);

                // 微软文档要求：关闭菜单后补发 WM_NULL，避免菜单卡住
                PostMessage(_hwndOwner, 0x0000, IntPtr.Zero, IntPtr.Zero);

                _active = null;

                if (cmd == 0) return true;   // 用户取消

                var info = new CMINVOKECOMMANDINFO();
                info.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFO));
                info.fMask = 0;
                info.hwnd = _hwndOwner;
                // MAKEINTRESOURCE：命令偏移直接当作指针传入
                info.lpVerb = (IntPtr)(int)(cmd - idCmdFirst);
                info.lpDirectory = _parentPidl;
                info.nShow = 1;   // SW_SHOWNORMAL

                IntPtr pici = Marshal.AllocCoTaskMem(info.cbSize);
                try
                {
                    Marshal.StructureToPtr(info, pici, false);
                    _menu.InvokeCommand(pici);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pici);
                }

                return true;
            }
            catch
            {
                _active = null;
                return false;
            }
            finally
            {
                if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            }
        }

        // ============================================================
        //  消息转发
        // ============================================================

        /// <summary>
        /// 由宿主窗体的 WndProc 调用，把 owner-draw 消息转给 Shell 扩展，
        /// 否则带自绘项的菜单会显示成空白。
        /// </summary>
        public static bool TryHandleMessage(ref Message m)
        {
            ShellContextMenu active = _active;
            if (active == null) return false;

            uint msg = (uint)m.Msg;
            if (msg != WM_DRAWITEM && msg != WM_MEASUREITEM &&
                msg != WM_INITMENUPOPUP && msg != WM_MENUCHAR)
                return false;

            try
            {
                if (active._menu3 != null)
                {
                    IntPtr result = IntPtr.Zero;
                    active._menu3.HandleMenuMsg2(msg, m.WParam, m.LParam, result);
                    m.Result = result;
                    return true;
                }

                if (active._menu2 != null)
                {
                    active._menu2.HandleMenuMsg(msg, m.WParam, m.LParam);
                    m.Result = IntPtr.Zero;
                    return true;
                }
            }
            catch
            {
                // 忽略：转发失败不应影响主流程
            }

            return false;
        }

        /// <summary>当前是否有菜单正在显示。</summary>
        public static bool IsMenuActive { get { return _active != null; } }

        // ============================================================
        //  释放
        // ============================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_active == this) _active = null;

            if (_ownedPidl != IntPtr.Zero) { ILFree(_ownedPidl); _ownedPidl = IntPtr.Zero; }
            if (_parentPidl != IntPtr.Zero) { ILFree(_parentPidl); _parentPidl = IntPtr.Zero; }
            if (_childPidl != IntPtr.Zero) { ILFree(_childPidl); _childPidl = IntPtr.Zero; }

            _menu3 = null;
            _menu2 = null;

            if (_menu != null)
            {
                try { Marshal.ReleaseComObject(_menu); } catch { }
                _menu = null;
            }
        }
    }
}