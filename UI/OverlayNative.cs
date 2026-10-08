using System;
using System.Runtime.InteropServices;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 分层窗口（Layered Window）所需的 GDI / User32 声明。
    ///
    /// 用途：UpdateLayeredWindow 让窗口支持「逐像素 alpha」，
    /// 这是实现真半透明面板的唯一途径 —— 普通 WinForms 的 Opacity
    /// 会让整个窗口（包括图标）一起变淡，达不到玻璃质感。
    ///
    /// 关键点：UpdateLayeredWindow 要求位图为「预乘 alpha」，
    /// 而 GDI+ 输出的是「直通 alpha」，所以必须自行预乘。
    /// </summary>
    internal static class OverlayNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RGBQUAD
        {
            public byte rgbBlue;
            public byte rgbGreen;
            public byte rgbRed;
            public byte rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public RGBQUAD[] bmiColors;
        }

        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_OVER = 0;
        public const byte AC_SRC_ALPHA = 1;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            int crKey,
            ref BLENDFUNCTION pblend,
            int dwFlags);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BITMAPINFO pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        /// <summary>
        /// 把直通 alpha 的 BGRA 像素就地转换为预乘 alpha。
        /// UpdateLayeredWindow 要求预乘，否则半透明区域会出现白边。
        /// </summary>
        public static void Premultiply(byte[] bgra, int length)
        {
            Premultiply(bgra, 0, length);
        }

        /// <summary>
        /// 带起始偏移的预乘。
        /// 局部更新时，脏区在整屏缓冲里是「每行一段」，必须能按行处理。
        /// </summary>
        public static void Premultiply(byte[] bgra, int offset, int length)
        {
            if (bgra == null) return;
            if (offset < 0 || offset >= bgra.Length) return;

            int limit = length - (length % 4);
            for (int i = 0; i < limit; i += 4)
            {
                byte a = bgra[offset + i + 3];
                if (a == 255) continue;      // 完全不透明，无需处理
                if (a == 0)
                {
                    bgra[offset + i] = 0;
                    bgra[offset + i + 1] = 0;
                    bgra[offset + i + 2] = 0;
                    continue;
                }
                bgra[offset + i]     = (byte)(bgra[offset + i]     * a / 255);
                bgra[offset + i + 1] = (byte)(bgra[offset + i + 1] * a / 255);
                bgra[offset + i + 2] = (byte)(bgra[offset + i + 2] * a / 255);
            }
        }

        /// <summary>
        /// 预乘 alpha —— 指针版（需要 /unsafe 编译）。
        ///
        /// 与托管版逐字节索引相比，这里省掉了每次访问的
        /// 数组边界检查与长度校验；1080p 有 207 万像素，
        /// 逐像素 3 次乘除，省下来的边界检查很可观。
        ///
        /// 逻辑与托管版**完全一致**（a==255/0 的短路也一样），
        /// 所以可以放心用设置项在两者之间切换做对照。
        /// </summary>
        public static unsafe void PremultiplyFast(byte[] bgra, int offset, int length)
        {
            if (bgra == null || bgra.Length == 0) return;
            if (offset < 0 || offset >= bgra.Length) return;

            // 指针版没有边界检查，越界就是内存破坏 —— 这里必须自己夹住
            int avail = bgra.Length - offset;
            if (avail <= 0) return;

            int limit = length - (length % 4);
            if (limit <= 0) return;
            if (limit > avail) limit = avail;

            fixed (byte* basePtr = bgra)
            {
                byte* p = basePtr + offset;
                byte* end = p + limit;

                while (p < end)
                {
                    byte a = p[3];

                    if (a == 255) { p += 4; continue; }

                    if (a == 0)
                    {
                        p[0] = 0;
                        p[1] = 0;
                        p[2] = 0;
                        p += 4;
                        continue;
                    }

                    p[0] = (byte)(p[0] * a / 255);
                    p[1] = (byte)(p[1] * a / 255);
                    p[2] = (byte)(p[2] * a / 255);
                    p += 4;
                }
            }
        }
    }
}