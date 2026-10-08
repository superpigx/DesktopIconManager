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
            if (bgra == null) return;

            int limit = length - (length % 4);
            for (int i = 0; i < limit; i += 4)
            {
                byte a = bgra[i + 3];
                if (a == 255) continue;      // 完全不透明，无需处理
                if (a == 0)
                {
                    bgra[i] = 0;
                    bgra[i + 1] = 0;
                    bgra[i + 2] = 0;
                    continue;
                }
                bgra[i]     = (byte)(bgra[i]     * a / 255);
                bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
                bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            }
        }
    }
}