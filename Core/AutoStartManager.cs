using System;
using Microsoft.Win32;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 开机自启的注册表开关（HKCU Run，不需要管理员权限）。
    ///
    /// 独立成类而不是塞进 AppSettings：注册表是**系统副作用**，
    /// 与「纯数据」的设置对象分开 —— 设置窗口取消时不会误写注册表。
    /// </summary>
    internal static class AutoStartManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DesktopIconManager";

        /// <summary>当前是否已登记自启。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 按开关状态写入或删除自启项。
        ///
        /// 注册表不可写（权限/杀软拦截）时返回 false，调用方提示用户即可 ——
        /// 这不是致命故障，程序本体照常能手动启动。
        /// </summary>
        public static bool Apply(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;

                    if (!enable)
                    {
                        // 不存在时静默通过 —— 幂等，重复取消不报错
                        if (k.GetValue(ValueName) != null)
                            k.DeleteValue(ValueName, false);
                        return true;
                    }

                    // 路径带空格必须加引号；--shell 让开机直接进覆盖层
                    string exe;
                    try { exe = System.Reflection.Assembly.GetEntryAssembly().Location; }
                    catch { return false; }

                    if (string.IsNullOrEmpty(exe)) return false;

                    k.SetValue(ValueName, "\"" + exe + "\" --shell", RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
