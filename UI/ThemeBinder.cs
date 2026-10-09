using System.Drawing;
using System.Windows.Forms;
using DesktopIconManager.Core;

namespace DesktopIconManager.UI
{
    /// <summary>
    /// 把 <see cref="AppSettings"/> 套到 <see cref="ThemePalette"/> 上。
    ///
    /// 之所以单独一个类、而不是让 AppSettings 直接依赖 ThemePalette：
    /// Core 层不该反过来引用 UI 层。设置是纯数据，配色属于 UI 关注点，
    /// 这层映射放在 UI 侧。
    /// </summary>
    internal static class ThemeBinder
    {
        /// <summary>按设置生成一份调色板（每次都是新对象，可安全替换）。</summary>
        public static ThemePalette Build(AppSettings s)
        {
            if (s == null) return ThemePalette.Original();

            ThemePalette p = ThemePalette.ByName(s.Theme);

            // ---- 面板底透明度 ----
            // -1 表示跟随主题；否则只改 alpha、保留主题的色相
            if (s.PanelAlpha >= 0)
                p.PanelFill = WithAlpha(p.PanelFill, s.PanelAlpha);

            // ---- 主题色（设置里的色块）----
            // 只覆盖强调色与其深色变体，面板底/边框保持主题原样，
            // 否则整块面板都会被染色，和「只换一个主色」的直觉不符。
            if (!string.IsNullOrEmpty(s.AccentColor))
            {
                try
                {
                    Color accent = ColorTranslator.FromHtml(s.AccentColor);
                    p.Accent = accent;
                    p.AccentDark = ControlPaint.Dark(accent, 0.18f);
                }
                catch
                {
                    // 颜色串坏了就用主题原色，不能让整个渲染崩掉
                }
            }

            // ---- 桌面字体 ----
            if (!string.IsNullOrEmpty(s.DesktopFont))
            {
                try
                {
                    // 只改字号体系，字体名本身由渲染器按 FontFamily 解析
                    p.FontFamily = s.DesktopFont;
                }
                catch { }
            }

            // ---- 盒子边框 / 圆角（设置里的两个开关）----
            p.PanelRadius = s.PanelRounded ? s.PanelRadius : 0;
            if (!s.PanelBorder)
                p.PanelBorder = WithAlpha(p.PanelBorder, 0);

            // ---- 其余外观 ----
            p.ShadowDepth = s.ShadowDepth;
            p.ShadowOffsetY = s.ShadowOffsetY;
            p.DrawShadow = s.ShadowDepth > 0;

            p.TitleFontSize = s.TitleFontSize;
            p.LabelFontSize = s.LabelFontSize;
            // 标签与徽标比正文略小 —— 缩放时保持这个相对关系
            p.TabFontSize = ClampFloat(s.LabelFontSize - 0.5f, 6f, 24f);
            p.BadgeFontSize = ClampFloat(s.LabelFontSize - 1f, 6f, 24f);

            // ---- 布局尺寸 ----
            p.IconSize = s.IconSize;
            p.ItemWidth = s.ItemWidth;
            p.ItemHeight = s.ItemHeight;
            p.SmallIconSize = s.SmallIconSize;
            p.SmallItemHeight = s.SmallItemHeight;
            p.ListRowHeight = s.ListRowHeight;
            p.ListIconSize = s.ListIconSize;
            p.BodyPadding = s.BodyPadding;
            p.TitleHeight = s.TitleHeight;
            p.TabBarHeight = s.TabBarHeight;
            p.PanelGap = s.PanelGap;

            return p;
        }

        private static Color WithAlpha(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c);
        }

        private static float ClampFloat(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}
