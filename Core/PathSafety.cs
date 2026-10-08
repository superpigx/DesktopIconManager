using System;
using System.IO;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 来自外部数据（存档文件、命令行）的路径校验。
    ///
    /// 补上复盘里点名的两处硬伤：
    ///   1. groups.txt 里的 FullPath 曾被直接交给 ShellExecute —— 存档一旦被篡改
    ///      （或手工改错），就能诱导打开任意程序。现在打开前必须过 IsOpenable。
    ///   2. explorer 的 "/select,\"path\"" 参数拼接未转义引号 —— 已改为走
    ///      SHOpenFolderAndSelectItems（根本不再拼命令行），这里是双保险。
    ///
    /// 原则：宁可拒绝并明确提示，也不静默放行。
    /// </summary>
    public static class PathSafety
    {
        /// <summary>路径长度上限。正常桌面路径远小于此。</summary>
        public const int MaxLength = 1024;

        /// <summary>
        /// 路径是否"看起来像个合法的绝对路径"。
        /// 只做静态校验，不碰磁盘（离线可判，便宜且不阻塞）。
        /// </summary>
        public static bool IsPlausible(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            path = path.Trim();
            if (path.Length == 0 || path.Length > MaxLength) return false;

            // 含 NUL —— 会截断原生 API 的字符串，必须拦
            if (path.IndexOf('\0') >= 0) return false;

            try
            {
                // 含非法路径字符（含双引号，正好挡住命令行拼接注入）
                if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
                if (!Path.IsPathRooted(path)) return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>是否允许交给 Shell 打开：既合法又确实存在。</summary>
        public static bool IsOpenable(string path)
        {
            if (!IsPlausible(path)) return false;

            try
            {
                return Directory.Exists(path) || File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 给命令行参数加引号并转义内部的双引号。
        /// 现在主路径已改用 SHOpenFolderAndSelectItems，这里是回退分支用的兜底。
        /// </summary>
        public static string QuoteArgument(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";

            // Windows 命令行转义：内部双引号写成 \"（反斜杠本身也要成对）
            string escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + escaped + "\"";
        }
    }
}
