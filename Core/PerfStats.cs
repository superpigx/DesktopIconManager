using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 运行指标采集。
    ///
    /// 复盘里最刺眼的一条结论是：本程序**没有任何运行指标**，
    /// 于是"是否拖累操作系统"只能靠猜，优化顺序也只能靠猜。
    /// 这里把渲染耗时、帧数、内存、句柄、GC、启动阶段耗时集中起来，
    /// 通过 --stats 一次性输出，让优化有据可依。
    ///
    /// 只在单 STA UI 线程里更新，不加锁（避免锁开销影响被测对象本身）。
    /// 若在别的线程读，数值可能略有抖动，但量级判断不受影响。
    /// </summary>
    public static class PerfStats
    {
        // ---- 渲染 ----
        public static long Frames;
        public static long FullFrames;
        public static long PartialFrames;
        public static double LastRenderMs;
        public static double TotalRenderMs;
        public static double MaxRenderMs;

        // ---- 资源 ----
        public static int IconCacheCount;
        public static int ItemCount;
        public static int GroupCount;

        /// <summary>渲染计时用的秒表（复用，避免每帧 new）。</summary>
        private static readonly Stopwatch RenderWatch = new Stopwatch();

        /// <summary>记录一次渲染。ms 为该帧耗时。</summary>
        public static void NoteFrame(bool partial, double ms)
        {
            Frames++;
            if (partial) PartialFrames++; else FullFrames++;

            LastRenderMs = ms;
            TotalRenderMs += ms;
            if (ms > MaxRenderMs) MaxRenderMs = ms;
        }

        public static void BeginFrame()
        {
            RenderWatch.Reset();
            RenderWatch.Start();
        }

        /// <summary>结束计时并记账。返回本帧毫秒数。</summary>
        public static double EndFrame(bool partial)
        {
            RenderWatch.Stop();
            double ms = RenderWatch.Elapsed.TotalMilliseconds;
            NoteFrame(partial, ms);
            return ms;
        }

        public static double AverageRenderMs
        {
            get { return Frames > 0 ? TotalRenderMs / Frames : 0.0; }
        }

        public static void Reset()
        {
            Frames = 0;
            FullFrames = 0;
            PartialFrames = 0;
            LastRenderMs = 0;
            TotalRenderMs = 0;
            MaxRenderMs = 0;
        }

        // ============================================================
        //  启动阶段计时
        // ============================================================

        private static readonly StringBuilder PhaseLog = new StringBuilder();
        private static readonly Stopwatch PhaseWatch = new Stopwatch();
        private static bool _phaseRunning;

        /// <summary>开始一个启动阶段的计时。</summary>
        public static void BeginPhase(string name)
        {
            _currentPhase = name;
            _phaseRunning = true;
            PhaseWatch.Reset();
            PhaseWatch.Start();
        }

        private static string _currentPhase;

        /// <summary>结束当前阶段，记录耗时。</summary>
        public static void EndPhase()
        {
            if (!_phaseRunning) return;
            PhaseWatch.Stop();
            _phaseRunning = false;

            PhaseLog.Append("    ")
                    .Append(_currentPhase)
                    .Append(": ")
                    .Append(PhaseWatch.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture))
                    .Append(" ms")
                    .Append(Environment.NewLine);
        }

        public static string PhaseReport
        {
            get { return PhaseLog.ToString(); }
        }

        // ============================================================
        //  进程资源
        // ============================================================

        /// <summary>当前进程的资源占用摘要。</summary>
        public static string SystemReport()
        {
            var sb = new StringBuilder();

            try
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    p.Refresh();

                    sb.Append("    私有内存: ").Append(FormatMb(p.PrivateMemorySize64)).Append(Environment.NewLine);
                    sb.Append("    工作集  : ").Append(FormatMb(p.WorkingSet64)).Append(Environment.NewLine);
                    sb.Append("    峰值工作集: ").Append(FormatMb(p.PeakWorkingSet64)).Append(Environment.NewLine);
                    sb.Append("    句柄数  : ").Append(p.HandleCount).Append(Environment.NewLine);
                    sb.Append("    线程数  : ").Append(p.Threads.Count).Append(Environment.NewLine);
                    sb.Append("    GDI 对象: ").Append(GetGuiResourcesGdi()).Append(Environment.NewLine);
                    sb.Append("    USER 对象: ").Append(GetGuiResourcesUser()).Append(Environment.NewLine);

                    try
                    {
                        sb.Append("    启动至今: ").Append(
                            ((long)(DateTime.Now - p.StartTime).TotalSeconds))
                          .Append(" 秒").Append(Environment.NewLine);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                sb.Append("    （读取失败: ").Append(ex.Message).Append("）").Append(Environment.NewLine);
            }

            return sb.ToString();
        }

        private static string FormatMb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        private static int GetGuiResourcesGdi()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return GetGuiResources(p.Handle, 0);   // GR_GDIOBJECTS
            }
            catch { return -1; }
        }

        private static int GetGuiResourcesUser()
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    return GetGuiResources(p.Handle, 1);   // GR_USEROBJECTS
            }
            catch { return -1; }
        }

        // ============================================================
        //  汇总
        // ============================================================

        public static string Report()
        {
            var sb = new StringBuilder();

            sb.Append("  渲染").Append(Environment.NewLine);
            sb.Append("    总帧数: ").Append(Frames).Append(Environment.NewLine);
            sb.Append("    整屏帧: ").Append(FullFrames).Append(Environment.NewLine);
            sb.Append("    局部帧: ").Append(PartialFrames).Append(Environment.NewLine);
            sb.Append("    最近一帧: ").Append(LastRenderMs.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" ms").Append(Environment.NewLine);
            sb.Append("    平均帧: ").Append(AverageRenderMs.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" ms").Append(Environment.NewLine);
            sb.Append("    最慢帧: ").Append(MaxRenderMs.ToString("0.00", CultureInfo.InvariantCulture))
              .Append(" ms").Append(Environment.NewLine);
            sb.Append("    累计耗时: ").Append(TotalRenderMs.ToString("0.0", CultureInfo.InvariantCulture))
              .Append(" ms").Append(Environment.NewLine);
            sb.Append(Environment.NewLine);

            sb.Append("  场景规模").Append(Environment.NewLine);
            sb.Append("    收纳盒: ").Append(GroupCount).Append(Environment.NewLine);
            sb.Append("    条目  : ").Append(ItemCount).Append(Environment.NewLine);
            sb.Append("    图标缓存: ").Append(IconCacheCount).Append(Environment.NewLine);
            sb.Append(Environment.NewLine);

            sb.Append("  进程资源").Append(Environment.NewLine);
            sb.Append(SystemReport());

            return sb.ToString();
        }
    }
}
