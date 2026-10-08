using System;
using System.Windows.Forms;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// explorer 重启监视器。
    ///
    /// 为什么需要它：
    ///   接管桌面后我们隐藏了系统图标。但 explorer.exe 一旦重启
    ///   （崩溃自恢复、手动重启、安装软件触发），新的 explorer 会
    ///   默认把桌面图标重新显示出来 —— 于是系统图标和我们的自绘图标
    ///   会重叠在一起。
    ///
    ///   本类定期轮询桌面图层句柄，发现变化就通知上层重新接管。
    ///
    /// 用 WinForms 的 Timer，回调天然在 UI 线程，无需跨线程封送。
    /// </summary>
    public sealed class ExplorerWatcher : IDisposable
    {
        private readonly Timer _timer;
        private IntPtr _lastDefView;
        private bool _disposed;

        /// <summary>explorer 重启（桌面图层句柄变化）时触发。</summary>
        public event EventHandler ExplorerRestarted;

        public ExplorerWatcher()
        {
            _lastDefView = DesktopToggler.FindDefView();

            _timer = new Timer();
            _timer.Interval = 2000;   // 2 秒一次，开销可忽略
            _timer.Tick += OnTick;
        }

        public void Start()
        {
            if (_disposed) return;
            _timer.Start();
        }

        public void Stop()
        {
            if (_disposed) return;
            _timer.Stop();
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_disposed) return;

            IntPtr current;
            try { current = DesktopToggler.FindDefView(); }
            catch { return; }

            // 句柄变了，或原本就找不到而现在出现了 —— 都说明图层重建过
            bool changed = (current != _lastDefView);

            if (!changed) return;

            _lastDefView = current;

            // 图层还没准备好时先不下结论，等下一次轮询
            if (current == IntPtr.Zero) return;

            EventHandler handler = ExplorerRestarted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
        }
    }
}