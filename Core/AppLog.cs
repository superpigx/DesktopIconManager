using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 统一日志入口。
    ///
    /// 收敛前有两个写日志的地方（Program.WriteCrashLog 与
    /// DesktopOverlayForm.WriteLog），各自 File.AppendAllText，
    /// 既不轮转也没有级别与环境上下文，长期运行会无限增长。
    ///
    /// 现在统一到这里：
    ///   · 单一入口 + 单一文件（数据目录下 desktop-icon-manager.log）
    ///   · 首次写入时输出"环境头"（版本 / OS / 位数 / DPI / 屏幕 / 数据目录）
    ///   · 超过 512KB 自动轮转，保留 2 份备份
    ///   · 全程 try/catch —— 日志永远不能成为崩溃原因
    /// </summary>
    public static class AppLog
    {
        private const long MaxBytes = 512 * 1024;
        private const int KeepBackups = 2;

        private static readonly object Sync = new object();
        private static bool _headerWritten;

        public static void Info(string text) { Write("INFO ", text); }
        public static void Warn(string text) { Write("WARN ", text); }
        public static void Error(string text) { Write("ERROR", text); }

        /// <summary>记录异常。context 说明发生在哪个环节。</summary>
        public static void Error(string context, Exception ex)
        {
            if (ex == null) { Error(context); return; }
            Write("ERROR", context + " | " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>完整堆栈（仅用于致命错误，避免日志被大堆栈刷满）。</summary>
        public static void Fatal(string context, Exception ex)
        {
            if (ex == null) { Error(context); return; }
            Write("FATAL", context + Environment.NewLine + ex);
        }

        private static void Write(string level, string text)
        {
            try
            {
                lock (Sync)
                {
                    string path = AppPaths.LogPath;

                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (!_headerWritten)
                    {
                        _headerWritten = true;
                        try { File.AppendAllText(path, BuildHeader(), Encoding.UTF8); }
                        catch { }
                    }

                    RotateIfNeeded(path);

                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                                  + "  [" + level + "]  "
                                  + (text ?? string.Empty)
                                  + Environment.NewLine;

                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写不了就算了 —— 绝不能因为日志让程序挂掉
            }
        }

        private static string BuildHeader()
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=========================================================");
            sb.AppendLine("  桌面图标管理器 · 运行日志");
            sb.AppendLine("=========================================================");

            try { sb.AppendLine("  版本: " + AppVersion.Display); }
            catch { }

            try { sb.AppendLine("  启动时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); }
            catch { }

            try { sb.AppendLine("  进程: PID " + Process.GetCurrentProcess().Id
                                + "  " + (Environment.Is64BitProcess ? "64 位" : "32 位")); }
            catch { }

            try { sb.AppendLine("  系统: " + Environment.OSVersion
                                + "  " + Environment.Version); }
            catch { }

            try { sb.AppendLine("  数据目录: " + AppPaths.DataDirectory); }
            catch { }

            try
            {
                System.Drawing.Rectangle vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                sb.AppendLine("  虚拟屏幕: " + vs.Width + "×" + vs.Height
                              + "  显示器 " + System.Windows.Forms.Screen.AllScreens.Length + " 台");
            }
            catch { }

            sb.AppendLine("=========================================================");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>超过上限就轮转：当前 → .1，.1 → .2，.2 丢弃。</summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxBytes) return;

                for (int i = KeepBackups; i >= 1; i--)
                {
                    string from = (i == 1) ? path : (path + "." + (i - 1));
                    string to = path + "." + i;

                    if (!File.Exists(from)) continue;

                    if (i == KeepBackups)
                    {
                        try { File.Delete(to); }
                        catch { }
                    }

                    try { File.Move(from, to); }
                    catch { }
                }
            }
            catch
            {
                // 轮转失败不影响本次写入
            }
        }
    }
}
