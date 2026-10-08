# 桌面图标管理器

按「隐藏系统图标 + 自绘可交互覆盖层」的实现方式独立开发的桌面整理工具。
仅使用 Windows 官方公开 API，**不依赖、不反编译、不复制任何商业软件代码**。

---

## 命令一览

| 命令 | 作用 | 改桌面 |
|---|---|---|
| `--shell` | **桌面覆盖层**（核心）：接管桌面，常驻托盘 | 是（可自动恢复） |
| `--shell --console` | 同上，但保留控制台窗口便于看日志 | 是 |
| `--settings` | **设置窗口**（不接管桌面，随时可开） | 否 |
| `--show-icons` | 应急：恢复系统桌面图标 | 是（恢复） |
| `--ui [light/dark/glass]` | 样式预览（截屏叠加，不动桌面） | 否 |
| `--diagnose` | 诊断图层结构，检测冲突程序 | 否 |
| `--probe` | 扫描桌面文件，输出分类统计 | 否 |
| `--positions` | 列出当前图标坐标 | 否 |
| `--preview` | 预览系统图标重排方案 | 否 |
| `--apply` | 快照 + 应用系统图标重排 | 是 |
| `--restore` | 从快照还原图标位置 | 是 |

**推荐路径：`--diagnose` → `--ui` → `--shell`**

---

## 设置窗口

主题、尺寸、吸附、分类规则都在这里改，**不用改代码重编译**。
进入方式：托盘右键 →「设置…」，或命令行 `--settings`。

托盘里改是**边改边看**的：拖任何控件立刻生效在桌面覆盖层上。
点「取消」会自动回滚到打开窗口前的样子。命令行 `--settings` 没有运行中的覆盖层，
改动在下次 `--shell` / `--ui` 时生效。

| 页签 | 可调项 |
|---|---|
| **外观** | 主题（浅色/深色/玻璃）、面板底透明度、标题栏与标签条高度、圆角、投影深度与偏移、标题与标签字号 |
| **布局** | 大图标尺寸与单元格、小图标尺寸与单元格、列表行高与图标、内容边距、面板间距、自动分组每行列数 |
| **交互** | 拖动吸附开关、吸附间距、吸附阈值、自动收紧、启动时锁定图标、是否纳入公用桌面 |
| **分类规则** | 查看当前生效的完整规则；按 `分类=.扩展名,.扩展名` 追加扩展名（只追加，内置规则不可删） |

| 按钮 | 作用 |
|---|---|
| 恢复默认 | 全部设置回到出厂值 |
| 样式预览 | 用当前设置打开预览窗口（截屏叠加，不动桌面） |
| 确定 / 取消 | 写回并落盘 / 丢弃改动并回滚预览 |

设置存 `settings.txt`（`key=value`，UTF-8）。**任何一行损坏都只影响那一项**，
其余照常读取 —— 不会因为一个坏值让程序起不来。

> 「面板底透明度」默认是不勾选的「跟随主题」：三套主题各自的默认透明度不同
> （浅色/深色约 60%，玻璃主题更透），统一成绝对值会把玻璃主题弄得不玻璃。
> 想统一控制时才勾选。

---

## 数据目录

0.9.2 起，布局 / 设置 / 快照 / 日志都放在：

```
%LOCALAPPDATA%\DesktopIconManager\
    groups.txt                    布局（用户唯一资产，另有 groups.txt.bak）
    settings.txt                  设置
    positions_snapshot.txt        图标位置快照
    desktop-icon-manager.log      运行日志（512KB 轮转，保留 2 份）
    running.mark                  运行标记（用于异常退出自检）
```

`groups.txt` / `settings.txt` 若还留在 exe 同目录，首次运行会**自动复制**过去
（复制而非移动，老版本 exe 仍可读自己的文件）。

> 迁移的原因：程序被放进 Program Files 时，写自身目录会失败或触发 UAC 提示。

**写入是原子的**：先写 `.tmp` → 原子替换 → 把上一版留成 `.bak`。
写一半断电 / 崩溃不会损坏布局。

---

## 性能开关（settings.txt 高级项）

这几项都有保守回退路径，出问题把对应开关关掉即可，功能不受影响。

| 键 | 默认 | 作用 | 关掉会怎样 |
|---|---|---|---|
| `perfDirtyRect` | 1 | 拖动/悬停只重绘变化区域 | 一律整屏重绘（更慢但最稳） |
| `perfFullRedraw` | 0 | **强制**整屏重绘 | 0 = 允许走脏矩形 |
| `perfFastBlit` | 1 | 预乘 alpha 用指针版 | 改用托管版（逻辑一致，稍慢） |
| `perfCacheShadow` | 1 | 投影预渲染成位图 | 每次直接画（观感一致） |
| `perfCacheLabelHalo` | 1 | 标签光晕预渲染成位图 | 每次直接画（观感一致） |
| `iconCacheLimit` | 200 | 图标缓存 LRU 上限 | — |
| `hoverThrottleMs` | 16 | 悬停重绘节流 | 0 = 不节流 |
| `saveDebounceMs` | 3000 | 布局落盘去抖 | 0 = 改动即写（旧行为） |
| `useFolderView` | 0 | 用 Shell COM 接口读桌面图标文本（可去掉 `PROCESS_VM_WRITE`） | 0 = 走 LVM 通道（默认） |

想看实际效果，运行 `--stats`：输出版本、数据目录、布局规模、性能开关、
启动各阶段耗时、渲染帧数与耗时、内存 / 句柄 / GDI 对象。

---

## 功能取舍

按「保留主要功能、去掉无用功能」的原则裁剪。

### 保留
| 功能 | 说明 |
|---|---|
| 图标分组整理 | 核心，按文件类型自动分到「收纳盒」 |
| **拖动吸附对齐** | 拖拽/缩放时与其它盒、屏幕边缘自动对齐，带参考线 |
| **组合成格子组** | 把两个盒拖到一起 → 变成一个盒的多个**分类标签页** |
| **分类切换** | 点标签 / 鼠标滚轮 / `Ctrl+Tab` 循环切换 |
| **布局切换** | 每个盒独立选：大图标 / 小图标 / 列表 |
| 拖拽进组 | 拖图标到别的收纳盒即可 |
| 拖拽建组 | 拖一个图标到另一个上 → 自动建新收纳盒 |
| 图标锁定 | 锁定后禁止拖动，防误操作 |
| 外部拖入 | 从资源管理器拖文件进面板，自动收纳 |
| 自动补位 | 增删条目后面板自动收紧尺寸 |
| **设置窗口** | 主题 / 尺寸 / 吸附 / 分类规则，不用改代码，边改边看 |
| 面板拖动 / 缩放 | 拖标题栏移动，拖右下角缩放 |
| 折叠展开 | 双击标题栏 |
| 分类重命名 / 拆分 / 解散 | 右键菜单，可随时把分类拆回独立盒 |
| 双击打开 | 用系统默认程序打开文件 / 文件夹 |
| 系统原生右键菜单 | 转调 Shell，含已装软件注册的菜单项 |
| 布局持久化 | 存 `groups.txt`，下次自动载入 |
| 半透明主题 | 原版（默认，按截图标定）/ 浅色 / 深色 / 玻璃 |
| 分类规则可改 | 9 大类，规则集中在 `Classifier.cs` |
| 托盘常驻 | 后台运行，菜单控制，不占控制台 |
| explorer 自愈 | explorer 重启后自动重新接管 |
| 高 DPI 适配 | Per-Monitor V2，高缩放下坐标准确 |

### 去掉
广告推广、天气挂件、资讯流、火车票推广、纳米AI 上传、
定时关机、日历节假日、VIP 规则、账号登录、微信模块、
桌面备份还原、游戏推广、清理扫描、文件搜索、反馈上报、截图工具。

**理由**：这些与「整理桌面图标」无关，且多数依赖联网或需要常驻服务。

---

## 行为对照

分析原程序二进制符号后，按行为逐个对齐。其内部代号为 `DTFence`（桌面围栏）。

| 原程序符号 | 行为 | 本程序对应 |
|---|---|---|
| `AVCDTFenceMgr` | 围栏（收纳盒）管理器 | `GroupLayout` |
| `AVCDragCreateBoxMenu` | **拖拽创建收纳盒** | `CreateGroupWithItem()` |
| `locking_mode` / `locked` | 图标锁定 | `DesktopOverlayForm.Locked` |
| `auto_vacuum` | 自动补位 | `AutoFitPanel()` / `TidyAll()` |
| `RegisterDragDrop` / `IDropTargetImplEx` | OLE 外部拖放 | `OnDragDrop()` |
| `IDropTarget` / `IDataObject` | 内部拖放 | 鼠标拖拽状态机 |
| `VCDesktopRuleMgr` | 分类规则 | `Classifier` |
| `VCDesktopBackupMgr` | 位置备份还原 | `PositionStore` |
| `VCDesktopMgr` / `SysListView32` | 桌面图层读写 | `DesktopLayer` |
| `0x7402` 图标显隐 | 接管桌面 | `DesktopToggler` |

分析手段：对二进制做字符串扫描（`grep -a`）提取导出符号与配置键名，
据此推断行为，再用公开 API 独立实现。**不反编译、不复制代码。**

### 术语对齐：一个盒 = 多个「分类」

从二进制里扫到的文案确定了它的模型：

| 原文案（资源名） | 含义 |
|---|---|
| 「新建收纳盒」`new_folder.png` | 面板本身叫**收纳盒** |
| 「解散收纳盒」`remove_folder.png` | 删掉整个盒 |
| 「解散当前分类」`remove_category.png` | 删掉盒里的一个**标签页** |
| `folder_tab.png` | 标签页的图片资源 |
| 「点击标签查看整理后的文件哦！」`multi_folder_tips` | 点标签切换查看 |
| 「可创建多个并支持任意组合哦~」`multi_folder_newdrag_tips` | 拖到一起就是组合 |
| 「无法添加，收纳盒最多支持**15**个分类！」 | 标签页上限 15 |

所以把两个盒拖到一起**不是把条目合并**，而是给目标盒**加一个分类标签页**。
本项目据此实现：

- `FenceBox.Tabs` —— 一个盒持有的分类列表，`FenceBox.MaxTabs = 15`
- `FenceBox.Items` / `.Title` —— 当前分类的条目与标题（切标签即切换指向）
- 组合 / 拆分走 `CombineAsGroup()` / `DetachTab()`，**不移动任何文件**

同类的公开实现可作对照（均为独立项目，本项目未参考其代码）：

- **DeskBox** —— *"Merge file widgets into a group without changing their backing
  folders, then switch members from the title, mouse wheel, or cyclic Ctrl+Tab
  shortcut"* 以及 *"Snapping works while moving as well as resizing, with a
  configurable gap and screen-edge protection"* 与 *"Use icon or list layouts"*
- **SnowDesktop** —— 「集合组组件，将多个集合整合在一起，通过标签页切换」

两者印证了同一套交互：**标签页分组 + 拖动吸附 + 多种布局**。

---

## 视觉规格

色值**直接取自**主题文件 `360desktoplite_theme.xml` 的真实定义（该文件是「元素名即样式名」的结构）。
下表左列是本程序的取值，右列是主题文件里的出处。

| 元素 | 值 | 来源样式 |
|---|---|---|
| 面板底 | `#99FFFFFF`（60% 白） | `transparent_panel.canvas` |
| 面板边框 | `#C0C0C0` 1px | `transparent_panel.border` |
| 标题栏 | `#FFFFFF` **不透明纯白** | `panel_title.canvas` |
| 标题文字 | `#333333` | `g_333333_11.font` |
| 标题分隔线 | `#C0C0C0` | `panel_st_line.border` |
| 标签条底 | `#F0F0F0` | `wp_transparent_panel.canvas` |
| 计数徽标 | **不填充**，文字 `#999999` | `desktoplite_999999_9` |
| 次要文字 | `#666666` | `g_666666_9.font` |
| 主色（**仅描边/线**） | `#22CB64` | `panel_line_ecefe8.border` |
| 深色面板 | `#99000000` | `panel_99000000.canvas` |
| 玻璃面板 | 填充 `#00000000`，边框 `#4CFFFFFF` | `panel_4cffffff_border` |
| 拖放提示 | hover `#CDCDCD` | `drop_border.hot` |
| 投影 | 9 宫格边距 `5,3,5,7`（下重上轻） | `shadow.border` |
| 字体族 | 微软雅黑,宋体,Tahoma | `g_*` / `w_*` 系列的 face |
| 图标项（大） | 76 × 82，图标 48 × 48 | — |
| 图标项（小） | 76 × 62，图标 32 × 32 | — |
| 列表行 | 高 22，图标 16 × 16 | — |
| 分类标签行 | 高 22（仅组合后出现） | — |
| 当前分类标签 | 纯白底 + 底部 2px 主色线 | — |
| 非当前标签 | 透明底，文字 `#666666` | — |
| 吸附参考线 | `#AA22CB64` 1px | — |
| 面板圆角 | 0 | `transparent_panel.radius="0"` |

> **关键结论：绿色 `#22CB64` 在主题里只作 `border`（描边/线），从不作填充。**
>
> 三处曾被我误用，已修正：
> 1. **标题栏** —— 真实值是 `panel_title` 的**不透明**纯白 `#FFFFFF`，早期写成半透明 `#99FFFFFF`，
>    导致标题栏和面板分不出层次
> 2. **分隔线** —— 真实值是 `panel_st_line` 的 `#C0C0C0`，早期用 `#ECEFE8`（带绿味）
> 3. **计数徽标** —— 早期刷了一块饱和绿底 + 白字，在白色标题栏上非常突兀；
>    主题里绿色只出现在 `panel_line_ecefe8.border` 和 `panel_99000000.border` 这类**边框**上
>
> 另外注意：`panel_fm_title`（绿底）属于**「文件夹映射对话框」**，`fm` = folder map，
> 不是桌面分组，不要拿它当分组标题栏。

四套主题定义在 `ThemePalette.cs`：`Original()` / `Light()` / `Dark()` / `Glass()`。
**默认是「原版」** —— 按用户提供的 360 空白收纳盒截图逐像素标定：

| 标定项 | 值 | 方法 |
|---|---|---|
| 面板填充 | `#996F8391`（60% 深蓝灰） | 面板内均值 `(129,153,172)`、紧邻壁纸 `(156,186,212)`，假设 alpha=0.6（主题文件的 0x99）反解纯色；三个独立位置解出的 C 误差 ≤2，模型自洽 |
| 标题栏 | `#99446A81`（比内容区更深） | 同法，用标题栏区域均值反解 |

**★ 这推翻了此前的一个关键误判**：原版面板是**压暗壁纸**的深色半透明，
不是「60% 白」。用白的话面板在壁纸上会发白发浑 —— 这正是「颜色怪」的根源。

分组面板**没有任何图片资源**（主题只引用了设置窗口的按钮图），所以是纯色块绘制。

---

## 技术原理

### 桌面图标层结构
```
Progman / WorkerW
  └─ SHELLDLL_DefView
       └─ SysListView32     ← 图标是它的列表项
```

### 覆盖层工作流程
1. **隐藏系统图标** —— 向 `SHELLDLL_DefView` 发 `WM_COMMAND(0x7402)`
2. **建立分层窗口** —— `WS_EX_LAYERED`，支持逐像素 alpha
3. **GDI+ 绘制** —— 分组面板与图标画进 32bpp ARGB 位图
4. **预乘 alpha** —— `UpdateLayeredWindow` 要求预乘，否则半透明处有白边
5. **提交** —— `UpdateLayeredWindow` 输出到屏幕

### 关键 API
| API | 用途 |
|---|---|
| `FindWindow` / `FindWindowEx` / `EnumWindows` | 定位桌面图层 |
| `UpdateLayeredWindow` | 逐像素 alpha 输出 |
| `CreateDIBSection` / `CreateCompatibleDC` | 位图缓冲 |
| `OpenProcess` / `VirtualAllocEx` / `Read/WriteProcessMemory` | 跨进程读取图标坐标 |
| `SendMessageTimeout` + `LVM_*` | ListView 消息 |
| `SHGetImageList` / `SHGetFileInfo` | 提取 48×48 高清图标 |
| `IContextMenu3` / `SHParseDisplayName` | 转调系统原生右键菜单 |
| `ShellExecuteEx` | 打开文件 / 显示属性 |

### 为什么必须转调 Shell

接管桌面后系统图标被隐藏，用户就失去了双击打开、右键菜单这些能力。
这些不能自己实现：

- **打开**必须走 `ShellExecuteEx`，否则无法正确处理文件关联
- **右键菜单**必须转调 `IContextMenu`，否则看不到「打开方式 / 7-Zip /
  发送到」这些由已装软件注册的菜单项

Shell 扩展菜单是 owner-draw 的，会向宿主窗口发 `WM_DRAWITEM` /
`WM_MEASUREITEM`，所以窗体在 `WndProc` 里必须转发给 `IContextMenu3`，
否则菜单项会渲染成空白。

---

## 安全机制

改动桌面有风险，所以设了多重保险：

### 1. 图标自动恢复（最重要）
`--shell` 通过三条路径确保系统图标一定被还原：
- `FormClosing` 事件
- `AppDomain.ProcessExit`
- `AppDomain.UnhandledException`

即使程序崩溃，图标也会恢复。万一仍有意外，运行 `--show-icons` 即可。

另外还有一道**退出残留自检**（0.9.2 起）：启动时会写一个运行标记，正常退出时删除。
若下次启动发现标记还在，说明上次是被强杀 / 断电结束的（上述三条钩子都来不及跑），
此时会**自动**把系统桌面图标恢复回来 —— 不需要人工介入。

### 2. 幂等的显示开关
`HideIcons` / `ShowIcons` 都先探测 `SysListView32` 的可见性再决定是否发送
切换指令，重复调用不会出错。

### 3. 先预览后执行
`--ui` 只截屏叠加渲染，零风险地确认观感；满意后再上 `--shell`。

### 4. 系统图标重排的兜底
`--apply` 改动前强制写快照，快照失败则中止；`--restore` 按名称还原。

---

## 编译运行

### 方式一：一键脚本（推荐）
双击 `构建并运行.bat`，自动编译后提供菜单，`[S]` 进入桌面整理。

### 方式二：Visual Studio
打开 `DesktopIconManager.csproj`（.NET Framework 4.8），F5 运行。

### 方式三：命令行
```bat
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" ^
    /nologo /target:exe /platform:anycpu /unsafe /optimize+ ^
    /out:bin\DesktopIconManager.exe ^
    /reference:System.dll /reference:System.Core.dll ^
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    Program.cs Core\*.cs UI\*.cs
```

> `/unsafe` 是给预乘 alpha 的指针版用的。去掉它也能编译吗？
> **不能** —— 改回托管版请保留 `/unsafe`，并在 `settings.txt` 里设 `perfFastBlit=0`。

> 使用 `console` 子系统而非 `winexe`：GUI 能正常开窗，同时命令行模式保留输出。

### 编码注意事项（重要）

中文 Windows 下编译/运行有两个编码坑，本项目已处理：

| 项 | 要求 | 原因 |
|---|---|---|
| `.bat` 文件 | **GBK(cp936) + CRLF 行尾** | cmd.exe 按系统 OEM 代码页读取批处理，UTF-8 会全变乱码 |
| 批处理内不用 `chcp 65001` | 已移除 | 中途改代码页会让 cmd 的文件指针错位，后续命令解析失败 |
| `.cs` 源文件 | **UTF-8 带 BOM** | 无 BOM 时编译器可能按 GBK 读，中文字符串字面量变乱码 |
| 编译参数 | 含 `/codepage:65001` | 双保险 |
| 控制台输出 | 不强制 UTF-8 | 936 控制台下强制 UTF-8 反而乱码 |

在 UTF-8 终端（如 Windows Terminal）下运行时，可加 `--utf8` 显式切换。

---

## 目录结构

```
DesktopIconManager/
├── Program.cs                 主入口，命令分发
├── Core/
│   ├── AppSettings.cs         设置模型 + 持久化 + 当前生效实例
│   ├── NativeMethods.cs       P/Invoke 与常量
│   ├── DesktopLayer.cs        桌面图层读写（坐标）
│   ├── DesktopToggler.cs      系统图标显示开关
│   ├── Classifier.cs          分类引擎（9 大类）
│   ├── DesktopIconReader.cs   只读文件系统枚举
│   ├── IconLayoutEngine.cs    网格布局计算
│   ├── PositionStore.cs       系统图标位置快照
│   ├── GroupLayout.cs         收纳盒/分类模型 + 持久化
│   ├── ShellLauncher.cs       打开文件 / 显示属性 / 定位
│   ├── ShellContextMenu.cs    系统原生右键菜单转调
│   └── ExplorerWatcher.cs     explorer 重启监视
├── UI/
│   ├── ThemePalette.cs        主题调色板
│   ├── ThemeBinder.cs         把设置套到调色板上
│   ├── SettingsForm.cs        设置窗口
│   ├── IconExtractor.cs       系统图标提取
│   ├── GroupPanelRenderer.cs  面板 / 标签页 / 图标 / 列表渲染
│   ├── SnapEngine.cs          拖动与缩放的吸附对齐引擎
│   ├── OverlayNative.cs       分层窗口 API
│   ├── DesktopOverlayForm.cs  覆盖层窗体 + 交互
│   ├── TrayHost.cs            托盘图标 + 菜单
│   └── PreviewForm.cs         样式预览
├── DesktopIconManager.csproj
├── 构建并运行.bat
└── README.md
```

---

## 操作速查

| 操作 | 效果 |
|---|---|
| 拖标题栏 | 移动收纳盒（自动吸附对齐，显示参考线） |
| 拖右下角 | 缩放收纳盒（同样吸附） |
| **把一个盒拖到另一个盒上** | **组合成格子组：源盒的每个分类变成一个标签页** |
| 点分类标签 | 切换到该分类 |
| 在盒内滚滚轮 | 循环切换分类 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 循环切换鼠标所在盒的分类 |
| 右键分类标签 | 切换 / 重命名 / 拆出为独立盒 / 解散此分类 |
| 右键盒 → 布局 | 大图标 / 小图标 / 列表 |
| 右键盒 → 组合到… | 按分类变标签页（保留各自分类名） |
| 右键盒 → 全部并入一个分类 | 把条目混进目标的一个分类（会去重） |
| 右键盒 → 拆出当前分类 | 分类变成独立收纳盒，放在右侧 |
| 拖图标到空白或别的收纳盒 | 移到该盒的当前分类 |
| 拖图标到另一个图标上 | 自动创建包含两者的新收纳盒 |
| 从资源管理器拖文件进面板 | 加入当前分类 |
| 双击图标 | 用系统默认程序打开 |
| 右键图标 | 系统原生菜单（打开方式 / 属性 / 已装软件项） |
| 双击标题栏 | 折叠 / 展开 |
| 右键空白 | 新建收纳盒 / 按分类重新分组 / 清理失效条目 |
| 托盘 → 设置… | 打开设置窗口（边改边看） |
| Ctrl+S | 保存布局 |
| F5 | 重绘 |
| ESC | 退出（自动恢复系统图标） |

---

## 已知限制

- **多屏**：覆盖层铺满虚拟屏，但新建收纳盒默认落在主屏，跨屏摆放需手动拖
- **Win11 新版需实测**：Build 26340 的 WorkerW 行为可能与经典方案有差异
- **预览的壁纸是快照**：`--ui` 用截屏做背景，壁纸更换后需重开
- **分类过多的显示退化**：并排放不下时（每标签低于 46px），标签行整体退化成
  一块「当前分类 + n/N」，此时点它会弹出完整列表来切换；滚轮与 `Ctrl+Tab` 仍可用
- **列表模式宽度靠手动**：切到列表只重算高度，宽度沿用当前值（最小 240）

> 高 DPI 与 explorer 重启已在阶段 4 处理：
> 前者走 Per-Monitor V2，后者由 `ExplorerWatcher` 每 2 秒轮询图层句柄，
> 发现变化立即重新接管并恢复覆盖层。

---

## 吸附对齐

对应参考实现的 *"configurable gap and screen-edge protection"*，实现见 `UI/SnapEngine.cs`。

**候选位置**（X / Y 两轴独立求解，取距离最近且在阈值内的那个）：

| 来源 | 候选 |
|---|---|
| 其它收纳盒 | 左/右/上/下边对齐、水平/垂直居中、按间距紧贴（含 `Gap`） |
| 屏幕 | 左/右/上/下边对齐、居中 |

**三条规则**：

1. **阈值 12px** —— 超过就不吸，避免「甩不掉」
2. **先吸附再边缘保护** —— 顺序反了会把吸附结果又推出屏幕外
3. **屏幕边缘保护** —— 横向至少留 120px 可抓、纵向标题栏必须完整可见，
   保证盒不会被拖出屏幕后找不回来

缩放（拖右下角）只吸附右边缘与下边缘。参考线在拖拽期间实时绘制。

---

## 存档格式

`groups.txt`，制表符分隔，UTF-8（无 BOM）。当前为 **v2**：

```
# DIM-GROUPS v2
G <x> <y> <w> <h> <collapsed> <activeTab> <viewMode>
T <分类标题>
I <isDir> <名称> <完整路径>
...
```

顺序解析：`G` 起一个新盒 → `T` 在该盒里新增一个分类 → `I` 追加到当前分类。

**v1 旧存档可直接读**，会自动升级成「单分类的盒」：

```
# DIM-GROUPS v1
G <标题> <x> <y> <w> <h> <collapsed>
I <盒下标> <isDir> <名称> <完整路径>
```

两版的 `G` 行字段数相同但含义不同，所以**靠首行标记区分版本**，不靠字段数猜。
版本未知时按 v1 解析（v1 是历史数据，兼容优先）。

> 用自研文本格式而不用 JSON，是为了零依赖 —— .NET Framework 没有内置 JSON，
> 且纯文本便于人工查看与手改。

---

## 重要前提

**使用 `--shell` 前，请先停用 360 桌面助手等同类工具。**

两个程序同时接管桌面图层会互相覆盖图标位置，表现为图标乱跳。
`--diagnose` 与 `--shell` 都会自动检测并提示这类冲突。
