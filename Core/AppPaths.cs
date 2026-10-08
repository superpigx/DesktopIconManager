using System;
using System.IO;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 数据目录与数据文件定位。
    ///
    /// 为什么从「exe 同目录」迁到 %LOCALAPPDATA%：
    ///   · 程序被放进 Program Files / 系统目录时，写自身目录会失败或触发 UAC 提示
    ///   · 写系统目录属于不必要的环境侵入（本项目其它方面都刻意做到零侵入）
    ///   · %LOCALAPPDATA% 是微软规定的单用户应用数据位置，无需管理员权限
    ///
    /// 迁移策略（幂等、可回退）：
    ///   1. 数据目录里已有该文件 → 直接用它（权威位置）
    ///   2. 数据目录没有、exe 同目录有 → 复制一次（**复制而非移动**，
    ///      老版本 exe 仍能读到自己的文件，不会出现"老程序读到空布局"）
    ///   3. 都没有 → 返回数据目录下的路径（首次写入时创建）
    /// </summary>
    public static class AppPaths
    {
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static string _dataDir;
        private static bool _resolved;

        /// <summary>程序所在目录（只读，永不写数据）。</summary>
        public static string BaseDirectory { get { return BaseDir; } }

        /// <summary>数据目录：%LOCALAPPDATA%\DesktopIconManager，失败时回退到 exe 同目录。</summary>
        public static string DataDirectory { get { Ensure(); return _dataDir; } }

        private static void Ensure()
        {
            if (_resolved) return;
            _resolved = true;
            _dataDir = ResolveDataDirectory();
        }

        private static string ResolveDataDirectory()
        {
            try
            {
                string local = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);

                if (!string.IsNullOrEmpty(local))
                {
                    string dir = Path.Combine(local, "DesktopIconManager");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch
            {
                // 拿不到 LOCALAPPDATA（极罕见）→ 沿用旧位置
            }
            return BaseDir;
        }

        /// <summary>定位一个数据文件，必要时做一次性迁移。</summary>
        public static string ResolveDataFile(string fileName)
        {
            Ensure();

            try
            {
                string target = Path.Combine(_dataDir, fileName);
                if (File.Exists(target)) return target;

                string legacy = Path.Combine(BaseDir, fileName);
                if (File.Exists(legacy))
                {
                    try
                    {
                        File.Copy(legacy, target, false);
                        return target;
                    }
                    catch
                    {
                        // 复制失败（权限/占用）→ 继续用老位置，功能不受影响
                        return legacy;
                    }
                }

                return target;
            }
            catch
            {
                return Path.Combine(BaseDir, fileName);
            }
        }

        /// <summary>统一日志文件。</summary>
        public static string LogPath
        {
            get { return Path.Combine(DataDirectory, "desktop-icon-manager.log"); }
        }

        /// <summary>运行标记文件（用于检测上次是否异常退出）。</summary>
        public static string MarkerPath
        {
            get { return Path.Combine(DataDirectory, "running.mark"); }
        }
    }
}
