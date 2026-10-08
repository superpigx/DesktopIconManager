using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopIconManager.Core
{
    /// <summary>快照中的一个图标位置记录。</summary>
    public sealed class SnapshotEntry
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>
    /// 图标位置快照的存取。
    ///
    /// 这是安全网：任何改动桌面的操作之前，都先落一份快照。
    /// 出问题时一条命令即可还原。
    ///
    /// 文件格式（制表符分隔，UTF-8）：
    ///   # DIM-SNAPSHOT v1
    ///   # time=2026-10-08T17:40:00
    ///   index<TAB>name<TAB>x<TAB>y
    /// </summary>
    public static class PositionStore
    {
        private const string Marker = "# DIM-SNAPSHOT v1";

        public static string DefaultPath()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(dir, "positions_snapshot.txt");
        }

        public static void Save(string path, IList<SnapshotEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException("entries");

            var sb = new StringBuilder();
            sb.AppendLine(Marker);
            sb.AppendLine("# time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# count=" + entries.Count);
            sb.AppendLine("# 说明: 还原时优先按图标名称匹配，名称找不到才回退到序号。");

            foreach (var e in entries.OrderBy(x => x.Index))
            {
                sb.Append(e.Index.ToString())
                  .Append('\t')
                  .Append(Escape(e.Name))
                  .Append('\t')
                  .Append(e.X.ToString())
                  .Append('\t')
                  .Append(e.Y.ToString())
                  .Append("\r\n");
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static IList<SnapshotEntry> Load(string path)
        {
            var list = new List<SnapshotEntry>();

            if (!File.Exists(path))
                return list;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            foreach (string raw in lines)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw[0] == '#') continue;

                string[] parts = raw.Split('\t');
                if (parts.Length < 4) continue;

                int index, x, y;
                if (!int.TryParse(parts[0], out index)) continue;
                if (!int.TryParse(parts[2], out x)) continue;
                if (!int.TryParse(parts[3], out y)) continue;

                list.Add(new SnapshotEntry
                {
                    Index = index,
                    Name = Unescape(parts[1]),
                    X = x,
                    Y = y
                });
            }

            return list;
        }

        /// <summary>判断文件是否为本程序的快照格式。</summary>
        public static bool IsSnapshotFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    string first = reader.ReadLine();
                    return first != null && first.Trim() == Marker;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace("\t", "\\t")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == 'r') { sb.Append('\r'); i++; continue; }
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}