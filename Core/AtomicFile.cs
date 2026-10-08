using System;
using System.IO;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 小数据文件的「安全写 + 安全读」。
    ///
    /// 安全写（原子化）：
    ///   1. 先写同目录的 .tmp
    ///   2. 目标已存在 → File.Replace(tmp, target, target.bak)：系统级原子替换，
    ///      并把上一版留成 .bak（布局是用户唯一资产，断电/写一半损坏代价太大）
    ///   3. 目标不存在 → File.Move
    ///   4. File.Replace 不可用（少见的文件系统）→ 退化为 Copy + 删 tmp
    ///
    /// 安全读（防损坏/恶意大文件）：
    ///   · 先查文件长度，超过上限直接拒绝
    ///   · 逐行读，超过行数上限就停 —— 不会把几百 MB 的垃圾文件全读进内存
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>数据文件的体积上限（8MB）。正常布局文件只有几十 KB。</summary>
        public const long MaxFileBytes = 8 * 1024 * 1024;

        /// <summary>数据文件的行数上限。</summary>
        public const int MaxLines = 200000;

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>原子写入一个 UTF-8(无 BOM) 文本文件，并保留一份 .bak。</summary>
        public static void WriteAllText(string path, string content)
        {
            if (string.IsNullOrEmpty(path)) return;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";

            // 先清掉可能残留的临时文件，避免 File.Replace 撞上旧内容
            TryDelete(tmp);

            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
                fs.Write(bytes, 0, bytes.Length);
                // 真正刷到磁盘：写一半断电也就只坏 .tmp，不坏目标文件
                fs.Flush(true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, path + ".bak");
                    return;
                }
                catch
                {
                    // 落到下面的退化路径
                }
            }

            try
            {
                File.Copy(tmp, path, true);
            }
            finally
            {
                TryDelete(tmp);
            }
        }

        /// <summary>
        /// 安全读取全部行。文件不存在 / 过大 / 读失败 → 返回 false。
        /// </summary>
        public static bool TryReadLines(string path, out string[] lines)
        {
            lines = null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) return false;

                var list = new System.Collections.Generic.List<string>(256);

                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        list.Add(line);
                        if (list.Count >= MaxLines) break;
                    }
                }

                lines = list.ToArray();
                return true;
            }
            catch
            {
                lines = null;
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
