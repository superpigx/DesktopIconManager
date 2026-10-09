using System;
using System.Reflection;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 版本号。
    ///
    /// 单独抽出来，是为了让日志头、--stats、托盘提示、
    /// 设置窗口标题用同一个来源，不再各写一份字符串。
    /// </summary>
    public static class AppVersion
    {
        public const int Major = 0;
        public const int Minor = 9;
        public const int Patch = 11;

        /// <summary>形如 "0.9.11"。</summary>
        public static string Number
        {
            get { return Major + "." + Minor + "." + Patch; }
        }

        /// <summary>展示用：版本号 + 编译日期。</summary>
        public static string Display
        {
            get { return Number + " (build " + BuildDate + ")"; }
        }

        /// <summary>从程序集的 Build 号反推编译日期（AssemblyInfo 未设时给占位）。</summary>
        public static string BuildDate
        {
            get
            {
                try
                {
                    Assembly asm = typeof(AppVersion).Assembly;
                    AssemblyName name = asm.GetName();

                    // AssemblyVersion 形如 0.9.2.12345；后两位是 2000-01-01 起的天数/半分钟
                    Version v = name.Version;
                    if (v.Build > 0 && v.Revision >= 0)
                    {
                        var baseDate = new DateTime(2000, 1, 1);
                        DateTime d = baseDate.AddDays(v.Build).AddSeconds(v.Revision * 2);
                        if (d.Year >= 2015 && d.Year <= 2100)
                            return d.ToString("yyyy-MM-dd");
                    }
                }
                catch
                {
                    // 解析失败就用占位
                }
                return "unknown";
            }
        }
    }
}
