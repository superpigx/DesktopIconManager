using System.Drawing;
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

            // ---- 其余外观 ----
            p.PanelRadius = s.PanelRadius;
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
