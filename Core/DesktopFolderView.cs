using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 通过 Shell 公开 COM 接口读取桌面图标，替代「跨进程写 explorer 内存」。
    ///
    /// ★ 为什么需要它（复盘 P1-14）：
    ///   现行 DesktopLayer 为了给 LVM_GETITEMTEXTW 准备 LVITEM 结构，
    ///   必须对 explorer 申请 PROCESS_VM_WRITE —— 也就是说，**为了"读"而申请了"写"**。
    ///   这是目前唯一一处放大的权限，也是最容易被 EDR / 杀软额外关注的行为。
    ///
    ///   本类走 Shell 官方接口链：
    ///     SHELLDLL_DefView --WM_GETISHELLBROWSER--> IShellBrowser
    ///       --> QueryActiveShellView --> IShellView
    ///       --> QueryInterface(IFolderView) --> IFolderView::Items
    ///       --> IShellItemArray --> IShellItem::GetDisplayName
    ///   全程不写 explorer 内存。
    ///
    /// ★ 为什么默认关闭：
    ///   这是**纯 COM vtable 调用**，一旦接口槽位与真实实现不符，
    ///   后果是访问违规而不是异常，try/catch 兜不住。
    ///   沙箱内无法真机验证，所以默认 useFolderView=0（沿用久经考验的 LVM 通道）；
    ///   确认可用后在 settings.txt 里置 1 即可切换；任何时候失败都会自动回退。
    /// </summary>
    internal sealed class DesktopFolderView : IDisposable
    {
        private const uint WM_GETISHELLBROWSER = 0x0400 + 7;   // WM_USER + 7
        private const uint SVGIO_ALLVIEW = 0x00000000;
        private const uint SIGDN_NORMALDISPLAY = 0x00000000;

        private IntPtr _folderViewRaw = IntPtr.Zero;
        private IFolderView _folderView;
        private bool _disposed;

        public bool IsAvailable { get { return _folderView != null; } }

        // ==================== COM 声明 ====================

        [ComImport]
        [Guid("000214E2-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            // IOleWindow
            [PreserveSig] int GetWindow(out IntPtr phwnd);
            [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
            // IShellBrowser
            [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
            [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
            [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
            [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
            [PreserveSig] int EnableModelessSB(int fEnable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
            [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
            [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
            [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
            [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
            [PreserveSig] int OnViewWindowActive(IntPtr ppshv);
            [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
        }

        [ComImport]
        [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            [PreserveSig] int GetCurrentFolder(out IntPtr ppidl);
            [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint cItems, out int piItemsInView);
            [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetSelectionMarkedItem(out int piItem);
            [PreserveSig] int GetFolder(uint uFlags, ref Guid riid, out IntPtr ppv);
        }

        [ComImport]
        [Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int GetCount(out uint pdwNumItems);
            [PreserveSig] int GetItemAt(uint dwIndex, out IntPtr ppsi);
            [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IntPtr ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName,
                [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
        }

        // ==================== 附加 ====================

        /// <summary>尝试附加到桌面视图。失败返回 null（调用方应回退到 LVM 通道）。</summary>
        public static DesktopFolderView TryAttach(IntPtr defView)
        {
            if (defView == IntPtr.Zero) return null;

            IntPtr shellBrowserRaw = IntPtr.Zero;
            IntPtr shellViewRaw = IntPtr.Zero;
            IntPtr folderViewRaw = IntPtr.Zero;

            try
            {
                shellBrowserRaw = NativeMethods.Send(defView, WM_GETISHELLBROWSER,
                                                     IntPtr.Zero, IntPtr.Zero);
                if (shellBrowserRaw == IntPtr.Zero) return null;

                var browser = Marshal.GetObjectForIUnknown(shellBrowserRaw) as IShellBrowser;
                if (browser == null) return null;

                if (browser.QueryActiveShellView(out shellViewRaw) != 0
                    || shellViewRaw == IntPtr.Zero)
                    return null;

                Guid iid = new Guid("cde725b0-ccc9-4519-917e-325d72fab4ce");
                int hr = Marshal.QueryInterface(shellViewRaw, ref iid, out folderViewRaw);
                if (hr != 0 || folderViewRaw == IntPtr.Zero) return null;

                IFolderView fv = Marshal.GetObjectForIUnknown(folderViewRaw) as IFolderView;
                if (fv == null) return null;

                var view = new DesktopFolderView();
                view._folderView = fv;
                view._folderViewRaw = folderViewRaw;
                folderViewRaw = IntPtr.Zero;      // 所有权移交

                // 自检：取一次条目数，取不到就当作不可用
                int count;
                if (!view.TryGetItemCount(out count))
                {
                    view.Dispose();
                    return null;
                }

                return view;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (folderViewRaw != IntPtr.Zero)
                {
                    try { Marshal.Release(folderViewRaw); }
                    catch { }
                }
                if (shellViewRaw != IntPtr.Zero)
                {
                    try { Marshal.Release(shellViewRaw); }
                    catch { }
                }
                if (shellBrowserRaw != IntPtr.Zero)
                {
                    try { Marshal.Release(shellBrowserRaw); }
                    catch { }
                }
            }
        }

        // ==================== 读取 ====================

        public bool TryGetItemCount(out int count)
        {
            count = 0;
            if (_disposed || _folderView == null) return false;

            try
            {
                return _folderView.ItemCount(SVGIO_ALLVIEW, out count) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读取全部条目的显示名（顺序与 ListView 一致）。失败返回 false。</summary>
        public bool TryGetItemTexts(out List<string> names)
        {
            names = null;
            if (_disposed || _folderView == null) return false;

            IntPtr arrayRaw = IntPtr.Zero;
            IShellItemArray array = null;

            try
            {
                Guid riid = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
                if (_folderView.Items(SVGIO_ALLVIEW, ref riid, out arrayRaw) != 0
                    || arrayRaw == IntPtr.Zero)
                    return false;

                array = Marshal.GetObjectForIUnknown(arrayRaw) as IShellItemArray;
                if (array == null) return false;

                uint n;
                if (array.GetCount(out n) != 0) return false;

                var list = new List<string>((int)n);

                for (uint i = 0; i < n; i++)
                {
                    IntPtr itemRaw = IntPtr.Zero;
                    try
                    {
                        if (array.GetItemAt(i, out itemRaw) != 0 || itemRaw == IntPtr.Zero)
                        {
                            list.Add(string.Empty);
                            continue;
                        }

                        IShellItem item = Marshal.GetObjectForIUnknown(itemRaw) as IShellItem;
                        if (item == null) { list.Add(string.Empty); continue; }

                        string name;
                        list.Add(item.GetDisplayName(SIGDN_NORMALDISPLAY, out name) == 0
                                     ? (name ?? string.Empty)
                                     : string.Empty);
                    }
                    finally
                    {
                        if (itemRaw != IntPtr.Zero)
                        {
                            try { Marshal.Release(itemRaw); }
                            catch { }
                        }
                    }
                }

                names = list;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (array != null)
                {
                    try { Marshal.ReleaseComObject(array); }
                    catch { }
                }
                if (arrayRaw != IntPtr.Zero)
                {
                    try { Marshal.Release(arrayRaw); }
                    catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_folderView != null)
            {
                try { Marshal.ReleaseComObject(_folderView); }
                catch { }
                _folderView = null;
            }

            if (_folderViewRaw != IntPtr.Zero)
            {
                try { Marshal.Release(_folderViewRaw); }
                catch { }
                _folderViewRaw = IntPtr.Zero;
            }
        }
    }
}
