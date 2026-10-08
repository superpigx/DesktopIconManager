using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopIconManager.Core
{
    /// <summary>
    /// 图标分类。文件夹单独识别，其余按扩展名归类。
    /// </summary>
    public enum IconCategory
    {
        Folder = 0,    // 文件夹
        Document = 1,  // 文档
        Archive = 2,   // 压缩包
        Image = 3,     // 图片
        Video = 4,     // 视频
        Audio = 5,     // 音频
        Shortcut = 6,  // 快捷方式
        WebLink = 7,   // 网址
        Executable = 8,// 程序（exe/msi/bat）
        Script = 9,    // 脚本配置
        Other = 10     // 未归类
    }

    public sealed class CategoryRule
    {
        public IconCategory Category { get; private set; }
        public string DisplayName { get; private set; }
        public HashSet<string> Extensions { get; private set; }

        public CategoryRule(IconCategory category, string displayName, string extList)
        {
            Category = category;
            DisplayName = displayName;
            Extensions = new HashSet<string>(
                SplitExtensions(extList),
                StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitExtensions(string extList)
        {
            if (string.IsNullOrEmpty(extList))
                return Enumerable.Empty<string>();

            return extList
                .Split(new[] { '|', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim().ToLowerInvariant())
                .Where(e => e.Length > 0)
                // 允许用户只写 md 而不写 .md
                .Select(e => e.StartsWith(".") ? e : "." + e);
        }

        /// <summary>供设置界面复用同一套扩展名切分规则。</summary>
        public static IEnumerable<string> ParseExtensions(string extList)
        {
            return SplitExtensions(extList);
        }
    }

    /// <summary>
    /// 分类引擎：纯数据匹配，不触碰桌面，不做任何修改。
    /// 规则为通用文件类型常识，用户可在设置中增删。
    /// </summary>
    public sealed class Classifier
    {
        private readonly List<CategoryRule> _rules;

        /// <summary>被禁用的分类显示名（大小写不敏感）。命中的文件落「未归类」。</summary>
        private readonly HashSet<string> _disabled =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>用当前生效的设置里的附加规则构造。</summary>
        public Classifier()
            : this(AppSettings.Current != null ? AppSettings.Current.ExtraRuleText : null,
                   AppSettings.Current != null ? AppSettings.Current.DisabledCategories : null)
        {
        }

        /// <summary>用指定的附加规则构造（设置界面用它做即时预览）。</summary>
        public Classifier(string extraRuleText)
            : this(extraRuleText, null)
        {
        }

        public Classifier(string extraRuleText, IEnumerable<string> disabledCategories)
        {
            _rules = BuildDefaultRules();
            ApplyExtraRules(extraRuleText);
            SetDisabledCategories(disabledCategories);
        }

        /// <summary>设置被禁用的分类；传 null 表示全部启用。</summary>
        public void SetDisabledCategories(IEnumerable<string> names)
        {
            _disabled.Clear();
            if (names == null) return;

            foreach (string n in names)
            {
                if (!string.IsNullOrEmpty(n)) _disabled.Add(n.Trim());
            }
        }

        public bool IsDisabled(string displayName)
        {
            return displayName != null && _disabled.Contains(displayName);
        }

        public IList<CategoryRule> Rules { get { return _rules.AsReadOnly(); } }

        /// <summary>
        /// 把「分类名=.ext,.ext」形式的附加规则合并到内置规则上。
        ///
        /// 只做**追加**，内置扩展名不可删 —— 免得用户误操作把基础分类搞坏。
        /// 无法识别的分类名会被忽略，不会报错。
        /// </summary>
        public void ApplyExtraRules(string extraRuleText)
        {
            if (string.IsNullOrEmpty(extraRuleText)) return;

            string[] lines = extraRuleText.Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in lines)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;

                string name = raw.Substring(0, eq).Trim();
                if (name.Length == 0) continue;

                string exts = raw.Substring(eq + 1);

                CategoryRule rule = _rules.Find(
                    delegate (CategoryRule r) { return r.DisplayName == name; });

                if (rule == null) continue;

                foreach (string e in CategoryRule.ParseExtensions(exts))
                    rule.Extensions.Add(e);
            }
        }

        private static List<CategoryRule> BuildDefaultRules()
        {
            return new List<CategoryRule>
            {
                new CategoryRule(IconCategory.Document, "文档",
                    ".doc|.docx|.dot|.dotm|.pdf|.xps|.htm|.html|.mht|.mhtml|.xml|.txt|.rtf|.wtf|.odt|" +
                    ".xls|.xlsx|.xlsb|.xltx|.xltm|.xlt|.csv|.prn|.dif|.slk|.xlam|.xla|.ods|.xlsm|" +
                    ".ppt|.pptx|.pptm|.pot|.potm|.potx|.ppsx|.pps|.ppsm|.ppam|.thmx|.ppa|.xmsl|.wps"),

                new CategoryRule(IconCategory.Archive, "压缩",
                    ".001|.7z|.a|.apm|.ar|.arj|.bz2|.bzip2|.cab|.cpio|.cramfs|.deb|.dmg|.epub|.esd|" +
                    ".ext|.ext2|.ext3|.ext4|.gpt|.gz|.gzip|.hfs|.hfsx|.hxi|.hxq|.hxr|.hxs|.hxw|" +
                    ".ihex|.img|.iso|.jar|.lha|.lib|.lit|.lzh|.lzma|.lzma86|.ova|.pkg|.pmd|.qcow|" +
                    ".qcow2|.qcow2c|.r00|.rar|.scap|.squashfs|.swm|.tar|.taz|.tbz|.tbz2|.txz|" +
                    ".uefif|.vdi|.vmdk|.wim|.xar|.xip|.xpi|.xz|.z|.z01|.zip|.zipx"),

                new CategoryRule(IconCategory.Image, "图片",
                    ".bmp|.jpg|.jpeg|.png|.gif|.tif|.tiff|.dib|.psd|.eps|.raw|.pxr|.mac|.tga|" +
                    ".vst|.pcd|.pct|.ai|.fpx|.cal|.img|.wi|.ico|.cr2|.crw|.cur|.ani|.psb|.sai|.webp"),

                new CategoryRule(IconCategory.Video, "视频",
                    ".mp4|.3gp|.avi|.wmv|.mpeg|.mpg|.mov|.flv|.swf|.qsv|.kux|.rm|.ram|.mkv|.m4v|.ts|.webm"),

                new CategoryRule(IconCategory.Audio, "音频",
                    ".mp3|.mp2|.mp1|.wav|.aif|.aiff|.au|.ra|.rm|.ram|.mid|.rmi|.flac|.m4a|.ogg|.wma"),

                new CategoryRule(IconCategory.Shortcut, "快捷方式", ".lnk|.pif|.appref-ms"),

                new CategoryRule(IconCategory.WebLink, "网址", ".url|.webloc|.website"),

                // 以下为实测补充：桌面上的可执行文件与配置脚本需要独立归类，
                // 否则会全部落入「未归类」而无法分组。
                new CategoryRule(IconCategory.Executable, "程序", ".exe|.msi|.bat|.cmd|.com"),

                new CategoryRule(IconCategory.Script, "脚本配置",
                    ".ini|.yaml|.yml|.json|.vbs|.js|.ps1|.conf|.cfg|.reg|.toml"),
            };
        }

        /// <summary>
        /// 判断图标属于哪个分类。纯函数，不修改任何状态。
        /// </summary>
        public IconCategory Classify(string path, bool isDirectory)
        {
            if (isDirectory)
                return IconCategory.Folder;

            if (string.IsNullOrEmpty(path))
                return IconCategory.Other;

            string ext;
            try { ext = Path.GetExtension(path).ToLowerInvariant(); }
            catch { return IconCategory.Other; }

            if (ext.Length == 0)
                return IconCategory.Other;

            for (int i = 0; i < _rules.Count; i++)
            {
                CategoryRule rule = _rules[i];

                // 被禁用的分类直接跳过 —— 文件落到「未归类」，
                // 而不是消失。对应原版「整理规则」表格的启用开关
                if (_disabled.Contains(rule.DisplayName)) continue;

                if (rule.Extensions.Contains(ext))
                    return rule.Category;
            }
            return IconCategory.Other;
        }

        public static string GetDisplayName(IconCategory category)
        {
            switch (category)
            {
                case IconCategory.Folder: return "文件夹";
                case IconCategory.Document: return "文档";
                case IconCategory.Archive: return "压缩";
                case IconCategory.Image: return "图片";
                case IconCategory.Video: return "视频";
                case IconCategory.Audio: return "音频";
                case IconCategory.Shortcut: return "快捷方式";
                case IconCategory.WebLink: return "网址";
                case IconCategory.Executable: return "程序";
                case IconCategory.Script: return "脚本配置";
                default: return "未归类";
            }
        }
    }
}