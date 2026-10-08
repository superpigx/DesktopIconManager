using System;
using System.Diagnostics;
using System.IO;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 运行标记 —— 用来检测「上次是不是异常退出」。
    ///
    /// 背景：本程序唯一的"高危副作用"是隐藏系统桌面图标。
    /// 正常关闭 / ProcessExit / 未捕获异常 三条路径都会恢复图标，
    /// 但**进程被强杀、系统断电、蓝屏**这三件事谁都覆盖不到 ——
    /// 结果就是用户重启电脑后发现桌面空空如也，只能手动跑 --show-icons。
    ///
    /// 做法（成本极低）：
    ///   · 启动时写标记文件（内容含 PID 与启动时间），退出时删除
    ///   · 下次启动若发现标记还在 → 上次没走正常退出 → 先把桌面图标恢复回来
    ///
    /// 这样最坏情况从「用户手动救」变成「自愈」。
    /// </summary>
    public static class RunningMarker
    {
        /// <summary>
        /// 抢占标记。
        /// </summary>
        /// <returns>true 表示发现了上次残留的标记（即上次异常退出）</returns>
        public static bool Take()
        {
            string path = AppPaths.MarkerPath;

            try
            {
                bool stale = false;

                if (File.Exists(path))
                {
                    // 标记里的进程还活着 → 那是另一个还在跑的实例，不算异常退出。
                    // （Local\ 前缀的 Mutex 是会话级的，多会话下确实可能各起一份）
                    int oldPid = ReadPid(path);
                    stale = !IsProcessAlive(oldPid);
                }

                Write(path);
                return stale;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>正常退出：删除标记。</summary>
        public static void Release()
        {
            try
            {
                string path = AppPaths.MarkerPath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉不影响退出流程
            }
        }

        private static void Write(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string text = Process.GetCurrentProcess().Id + " "
                              + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 标记写不了不影响主流程
            }
        }

        private static int ReadPid(string path)
        {
            try
            {
                string text = File.ReadAllText(path, System.Text.Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return -1;

                string head = text.Split(new[] { ' ', '\r', '\n', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries)[0];

                int pid;
                return int.TryParse(head, out pid) ? pid : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;

            try
            {
                Process p = Process.GetProcessById(pid);
                bool alive = p != null;
                if (p != null)
                {
                    try { p.Dispose(); }
                    catch { }
                }
                return alive;
            }
            catch
            {
                return false;
            }
        }
    }
}
