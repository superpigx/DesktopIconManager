# 参考实现借鉴分析：SnowDesktop / DeskBox

> 分析对象（已 clone 到 `/tmp/ref/`）：
> - **SnowDesktop** — <https://github.com/FreeFallingSnow/SnowDesktop>
>   C++ / CMake / 原生 Win32 + D2D + DirectComposition，**约 15.6 万行**，v1.0.9.0。
>   **路线与我们一致**：隐藏原生桌面图标 + 全屏自绘覆盖层接管。可比性极高。
> - **DeskBox** — <https://github.com/Tianyu199509/DeskBox>
>   C# WinUI 3 / .NET 10 + Rust native，**每格子一个独立无边框窗口**，明确声明"不替换桌面"。
>   **路线与我们相反**（不接管、不改原生图标坐标），但**工程化部分价值极高**。

本文只做"可借鉴点 → 落地建议"，不含具体代码实现。

---

## 0. 一句话结论

| 仓库 | 对我们最有价值的一点 |
|---|---|
| SnowDesktop | **印证并细化了我们的架构选择**，同时指出两处我们做错/没做的：隐藏原生层应打标记住"是我干的"；拖拽应把被拖项单独提到独立小窗口，主画面每帧不重画它 |
| DeskBox | **性能要有"协议 + 基线 + 采样 + 脚本"闭环**，而不是临时调参；以及"空闲主动归还内存"要有二次校验护栏 |

**两个仓库交叉给出的最强信号**：它们**都不用"一个全屏 layered 窗口 + 每帧 UpdateLayeredWindow"**。
SnowDesktop 主桌面走 DirectComposition，DeskBox 走"每格子一个独立窗口"。
我们的单全屏窗口 + 全屏 ULW 提交，正是刚踩的闪烁坑与性能天花板的根源（见 §5 架构级备选）。

---

## 1. 逐环节现状对照（我们 vs SnowDesktop）

| 环节 | 我们（v0.9.2） | SnowDesktop | 判断 |
|---|---|---|---|
| 定位桌面 ListView | `Progman/WorkerW → SHELLDLL_DefView → SysListView32` | 同样三级查找，但**校验 DefView 属于 Explorer 进程**（防伪装窗口） | 我们缺 PID 校验 |
| 隐藏原生图标 | `SendMessage(DefView, WM_COMMAND, 0x7402)`（切换命令）+ `AreIconsVisible()` 前置判断 | `SetPropW("SnowDesktop.HiddenExplorerIconLayer")` 打标 → `ShowWindow(SW_HIDE)`；退出按标记还原 | 🔴 **我们的做法有状态竞态**，见 §3.1 |
| 崩溃/强杀后恢复 | `RunningMarker` + `--show-icons` | 同上 + **进程外可调用的 `RestoreExplorerIconLayerNow()`** | 基本一致，可补一个"外部兜底入口" |
| 读图标坐标 | 跨进程 `VirtualAllocEx` + `LVM_GETITEMTEXT/POSITION`，主 UI 线程 | COM `IFolderView::GetItemPosition` + `ClientToScreen`，**独立 STA 线程 + deadline 超时放弃 + 10000 条硬上限** | 🔴 我们的方式权限重（`PROCESS_VM_WRITE`）、且卡 explorer 就卡我们 |
| 是否写回坐标 | 写回（移动条目时 `LVM_SETITEMPOSITION`） | **全仓无 `LVM_SETITEMPOSITION`**，只读一次用于网格播种 | 差异来自功能定位（我们要让 explorer 记住位置），可接受 |
| 主窗口 | 单个全屏 `WS_EX_LAYERED`，GDI+ 自绘 + `UpdateLayeredWindow` | 单个全屏窗口，但**提交走 DirectComposition**（ULW 只用于菜单/预览/拖拽提示条） | 我们受限于 GDI+，见 §5 |
| 窗口归属 | 顶层窗口 | `SetParent` 挂到 Progman/WorkerW + 转 `WS_CHILD` | 可考虑（Z 序更稳），但跨进程 SetParent 有风险 |
| 键盘焦点 | 无特殊处理 | 额外开 **1×1 的 WS_POPUP 工具窗口**专门吃键盘（layered/transparent 窗口收不到可靠焦点） | 🔴 我们有滚轮/Ctrl+Tab，可能受影响 |
| 拖拽 | 拖到哪，主画面每帧重绘「旧位置 ∪ 新位置」 | **被拖项单独渲染到一个小巧的顶层 ghost 窗口**，主 surface 每帧只画插入标记/高亮 | 🔴 **最大性能差距，见 §3.2** |
| 拖拽静态层 | 每次全画 | `DragRenderCache`：静态内容按 **revision** 缓存，每帧只 Blt 一次 | 我们已有阴影/光晕缓存，可扩到"面板背景层" |
| 鼠标穿透 | 无 | 热键整层 `SW_HIDE` + 每台显示器 **4 条 4px 逃逸带**，点边缘条恢复 | 功能待定，见 §4.3 |
| 重绘策略 | 脏矩形（刚修好）+ hover 节流 16ms | 脏矩形**一穿到底**；交互态跳过昂贵背景重算，交互结束后补一次全量 | 可补"交互态降级" |
| 图标获取 | `SHGetFileInfo` / 系统 ImageList，同步 | **四级降级链**（IExtractIcon → IShellItemImageFactory → SHGetImageList Jumbo/ExtraLarge/Large → SHGetFileInfo）+ **两阶段**（占位→精修）+ 有界 LRU | 🔴 可补"两阶段 + 调用命名" |
| 后台 IO | 全部在 UI 线程 | `BackgroundWork`：4 worker / 2048 队列 / **key 去重** / UI 每帧 **Drain 4ms 且 ≤32 条** | 🔴 我们 P2-16 没做，现在有范式了 |
| explorer 重启自愈 | 2s 轮询 Timer | `TaskbarCreated` **按 Explorer PID 去重** + 2s 看门狗巡检 6 个条件 + 分辨率/DPI 变化 750ms 防抖 | 🔴 我们只有轮询，缺消息通道 |
| 多屏 | 单一虚拟屏幕 | 每台屏独立 work area；**窗口变大才重建句柄，变小只同步** | 见 §4.5 |

---

## 2. DeskBox 的工程化做法（可直接搬运，与语言无关）

这部分与渲染无关，但正是"证明不拖累系统"所缺的一环。

| 做法 | 出处 | 我们的落地建议 |
|---|---|---|
| **性能基线入库**：每次 commit 记录 `ws/priv` 的 Start/Peak/End/AfterIdle 四个口径 | `docs/baselines/memory-scenarios.json` | 建 `docs/baselines/`，`--stats` 输出机器可读格式，脚本追加到 JSON |
| **采样 CSV schema**：`timestamp, phase, elapsedSeconds, privateMB, workingSetMB, commitMB, handles, threads, cpuSeconds, note` | `deskbox-aot-curve-samples.csv` | **直接抄这个 schema**——我们 `--stats` 已覆盖内存/句柄/GDI，补 threads 与 elapsed 即可 |
| **实验协议文档**：先定义"什么算退化"（峰值涨幅 ≤15% 为 plateau，>15% 需查） | `scripts/aot-memory-curve-protocol.md` | 写一份我们的 `--stats` 判读标准，否则数字没有意义 |
| **启动耗时靠日志哨兵行**：App 打一行 `[HH:mm:ss.fff] OnLaunched completed successfully`，脚本解析时间戳 | `scripts/measure-startup.ps1` | 零侵入；我们已有 `--stats`，加一行哨兵日志即可 |
| **空闲归还内存**：`SetProcessWorkingSetSize(-1,-1)`，2s 轮询，**执行前二次确认用户没开始交互** | `App.QuiescenceWorkingSetTrim.cs` | 适合常驻托盘的我们；**二次校验是护栏**，否则会把用户刚碰到的面板换出导致明显卡顿 |
| **日志异步有界队列，超载主动丢行** | `App.xaml.cs` `MaxQueuedLogLines` | 我们的 `AppLog` 是同步写，可加有界队列 |
| **错误显式分类为 degradation，禁止静默 swallow** | `App.Startup.cs` | 我们大量 `catch { }`，建议至少降级为 `AppLog.Warn` |
| **绝不主动创建 WorkerW**（`0x52C`），避免与 Explorer 图标布局恢复抢时序 | `WidgetLayerService.cs` | ✅ 我们已符合（只查找，不创建） |
| **设置窗口复用**（关闭即隐藏，不重复 new） | CHANGELOG：每开关一次泄漏 ~10MB | 我们的 `SettingsForm` 每次 new，可改为复用实例 |
| **批量窗口定位用 `BeginDeferWindowPos`** | `Win32Helper.cs` | 单窗口用不上；若将来走多窗口则必用 |
| **等稳定再动**：文件变化后等 4s 静默 + 内容指纹确认，再处理 | `DesktopAutoOrganizationWatcher.cs` | 我们的 `ExplorerWatcher` 可加"静默窗口"，避免 explorer 正在重排时抢读 |

---

## 3. 三个我认为最值得做的（SnowDesktop）

### 3.1 隐藏原生图标：改「打标 + SW_HIDE」，告别 0x7402 状态竞态 ⭐ 收益高 / 成本低 / 风险低

**我们现在的做法**（`Core/DesktopToggler.cs`）：发 `WM_COMMAND 0x7402`，这是个**切换**命令，不是"设置"命令。
它依赖 `AreIconsVisible()` 的前置判断，而这个判断读的是 DefView 的子窗口可见性——
一旦 explorer 自己重置过、或用户手动切换过"显示桌面图标"、或第三个程序也发过一次切换，
我们的判断就会失准，结果是**该显示时隐藏（桌面图标消失）/ 该隐藏时显示（图标重影）**。
这类偶发"图标不见了"的反馈，典型根因就在这。

**SnowDesktop 的做法**（`app_lifecycle.cpp:123-163`）：
- 隐藏前 `SetPropW(listView, "…HiddenExplorerIconLayer", 1)` **打标记**，再 `ShowWindow(SW_HIDE)`
- 同时用 `listViewWasVisible` 记住"我动过它"
- 恢复时**按标记判断**，而不是猜当前状态
- 另有一个**进程外可调用**的 `RestoreExplorerIconLayerNow()` 硬兜底

**落地**：`DesktopToggler` 增加"按标记隐藏/恢复"通道，保留 `0x7402` 作为回退；
`RunningMarker` 启动时若发现标记还在，直接走硬恢复。

### 3.2 拖拽：被拖项提到独立 ghost 窗口 ⭐⭐ 收益最高 / 成本中 / 风险中

**我们现在的代价**：拖一个图标时，每一帧都要重绘「旧位置 ∪ 新位置」两块区域——
包括面板背景、投影、标题栏、标签行、**该盒内所有条目的图标与文字**。
条目越多、盒越大，每帧越重。这就是拖拽时的主要开销来源。

**SnowDesktop 的答案**（`app_drag_preview_window.cpp:6-8`，注释即结论）：

> Compact top-level DComp surface used only for the custom drag ghost. The desktop,
> floating Dock and popup surfaces keep rendering drop guidance, but **no longer submit
> a second copy of the dragged item on every pointer move**.

即：拖起来的东西画到**一个跟鼠标走的小窗口**上，主画面每帧只画"插入位置指示/落点高亮"。
主 surface 的面板内容在这一整段拖拽期间**完全不用重画**。

配套三条细节：
- ghost 窗口样式 `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`（鼠标穿透，不干扰命中测试）
- `DwmSetWindowAttribute(DWMWA_TRANSITIONS_FORCEDISABLED)` 关掉 DWM 动效，消除拖影抖动
- 位置更新前先判 `!EqualRect(已应用, 请求)`，**没变就不动**

**落地**：新增 `DragGhostForm`（无边框、layered、transparent、不抢焦点），
拖拽开始时把被拖项（图标+文字）画上去并跟随鼠标；主画面只画落点提示。
另一半收益来自 `drag_render_cache.cpp` 的 **revision 缓存**：面板静态层（背景+投影+标题栏+未受影响的条目）
按"内容版本号"缓存成位图，每帧只 Blt —— 我们已有阴影/光晕缓存，扩到面板层即可。

### 3.3 读图标坐标：IFolderView + 独立 STA + deadline ⭐ 收益高 / 成本中 / 风险中

SnowDesktop 全仓**没有** `VirtualAllocEx + LVM_GETITEMPOSITION`，改用 COM：
`IShellWindows → IShellBrowser → IShellView → IFolderView::GetItemPosition` + `ClientToScreen`，
并且**跑在独立 STA 线程**上（不是 UI 线程），带 `deadline` 超时放弃、`10000` 条硬上限。
注释里把立场写得很直白：*"A slow Shell cannot block initialization beyond this budget."*

这正好印证我们 **P1-14（已实现但默认关闭）** 的方向是对的，并且给出了我们缺的两点：
1. **必须在独立 STA 线程跑**，而不是主线程——主线程跑 Shell COM，explorer 一卡我们就卡（我们现在的写法正是主线程）
2. **必须有 deadline + 条数上限**——绝不能无限等

同时它的 `FindDesktopWindows()` 会**校验 DefView 属于 Explorer 进程**，防伪装窗口——我们缺这一步。

---

## 4. 其余可借鉴项（按优先级）

### P1（建议做）
| # | 借鉴点 | 来源 | 说明 |
|---|---|---|---|
| 4.1 | **1×1 输入窗口**解决焦点 | SnowDesktop `app_lifecycle.cpp:335` | layered/transparent 窗口收不到可靠焦点，单独开一个屏外的 WS_POPUP 工具窗口吃键盘与热键。我们有滚轮切 tab、Ctrl+Tab，这条能解释"偶尔滚轮失灵" |
| 4.2 | **BackgroundWork 范式** | `background_work.h` | 4 worker / 2048 队列 / **key 去重**（同一路径不排两次）/ UI 每帧 **Drain 4ms 且 ≤32 条**。这正是我们 P2-16"IO 下沉后台"缺的具体范式，有了它风险可控 |
| 4.3 | **图标两阶段 + 调用命名** | `shell_icon_request.h` / `utils.cpp:1270-1479` | 先出占位图（Phase1）再精修（Phase2），**精修图不被草图覆盖**；每个 Shell 调用带名字（`ImageList.SHGetImageList.Jumbo`），卡死时日志能定位到具体哪一步 |
| 4.4 | **自愈补消息通道** | `app_desktop_reload.cpp:527` | 监听 `TaskbarCreated` 按 Explorer PID 去重（explorer 会连发多次），2s 轮询作兜底。我们现在只有轮询 |
| 4.5 | **窗口变大才重建** | `display_topology_refresh.h:40-48` | layered 窗口的输入面被创建时的分配锁死：**变小/内移只需 SetWindowPos，变大（超出原虚拟屏）必须重建句柄**。这解释了"加了显示器后那块区域点不到" |
| 4.6 | **交互态降级** | `desktop_backdrop_update_rules.h:86` | 拖拽/框选期间跳过昂贵的背景重算，交互结束后补一次全量。我们的脏矩形可以再加这一层 |

### P2（可选）
| # | 借鉴点 | 来源 | 说明 |
|---|---|---|---|
| 4.7 | **桌面穿透 + 逃逸带** | `desktop_passthrough_indicator.*` | 热键整层 `SW_HIDE`，每台显示器留 4 条 4px（随 DPI 缩放）窄窗口，点边缘条恢复。注意其注释：*"Create the complete escape surface before making any part visible"* —— 先全建好再统一 Show |
| 4.8 | **设置窗口复用** | DeskBox CHANGELOG | 关闭即隐藏复用实例，避免重复构造 |
| 4.9 | **空闲归还内存** | DeskBox `App.QuiescenceWorkingSetTrim.cs` | 2s 轮询 + **执行前二次确认**用户未交互 |
| 4.10 | **防抖常量表** | SnowDesktop `constants.h` | Shell 变更 50ms、显示拓扑 750ms、轮询退避阶梯（2s→15s→2min→5min→10min）。我们目前是固定 2s |
| 4.11 | **文件"等稳定"再处理** | DeskBox `WaitForDirectoryQuietAsync` | 等 4s 静默 + 内容指纹确认，避免 explorer 正在重排时我们抢读 |

### 明确不抄
| 项 | 理由 |
|---|---|
| DirectComposition / D2D 渲染 | 我们是 .NET Framework 4.8 + GDI+，换渲染栈等于重写 |
| Rust native 层 | 除非有 AOT/ABI 需求，我们零依赖是优势 |
| WinUI 3 / .NET 10 / AOT | 与目标框架冲突；AOT 对 4.8 不适用 |
| 多窗口架构（每盒一窗） | 见 §5，成本高；但这是**架构级备选**，值得单独立项评估 |
| SnowDesktop 的 Lua 小组件 / Steam 创意工坊 | 与我们功能定位无关 |

---

## 5. 架构级备选：从「单全屏窗口」改为「每盒一个独立窗口」

**这是两个仓库交叉给出的最强信号，但改动量最大，需要你决定是否立项。**

| | 现状（单全屏） | 备选（每盒一窗） |
|---|---|---|
| 提交像素量 | 每帧整屏（1080p 8.3MB / 4K 33MB） | 每帧**只提交变化的那一盒**（通常 <0.5MB） |
| 拖拽移动 | 每帧重绘旧∪新两块 | 移动盒子 = `SetWindowPos`，**由 DWM 合成，CPU 几乎零成本** |
| 闪烁风险 | 全屏提交，一错全屏错 | 单窗提交，影响面小 |
| 脏矩形 | 需要，且容易出错（我们刚踩过） | **天然不需要**——窗口自己就是脏区 |
| 代价 | — | 命中测试要按窗口分发；Z 序/遮挡顺序要自己维护（DeskBox 为此写了大量 Z-order 代码，且自承是技术债）；跨盒拖拽、组合动画要重做 |

**我的判断**：
- 如果目标是"稳"，**不要现在动**。我们刚把全屏提交修对，脏矩形也刚落地，应先让真机稳定跑一段时间。
- 如果真机实测 4K/多屏下拖拽仍然吃力，**这是唯一能根治的路径**——
  在现有脏矩形框架里继续抠，收益是有上限的（提交永远是整屏）。
- 折中方案：**先只做 §3.2 的 ghost 独立窗口**。它把"最贵的那部分拖拽"移出主画面，
  却不需要动窗口架构，是通往多窗口架构的低成本第一步，也能验证效果。

---

## 6. 如果要做，我的顺序建议

1. **§3.1 隐藏层打标**（半天，低风险，消除一类偶发"图标消失"）
2. **§2 性能基线闭环**（半天，让后续所有优化有判读标准）
3. **§4.1 1×1 输入窗口 + §4.4 TaskbarCreated**（一天，解决焦点与自愈）
4. **§3.2 拖拽 ghost + 面板层 revision 缓存**（两三天，收益最大）
5. **§4.2 BackgroundWork 下沉 IO**（两三天，需谨慎）
6. **§3.3 IFolderView 独立 STA**（可在 5 之后合并做）
7. **§5 多窗口架构**（单独立项，先做 4 验证收益再决定）

---

## 7. 附：关键文件索引（便于回查）

**SnowDesktop**（`/tmp/ref/snow`）
- `src/common/utils.cpp:391-442` 桌面窗口查找（含 Explorer PID 校验）
- `src/common/utils.cpp:450-458` `RestoreExplorerIconLayerNow()` 进程外硬恢复
- `src/app/lifecycle/app_lifecycle.cpp:123-163` 隐藏/恢复原生图标（SetProp 打标）
- `src/app/lifecycle/app_lifecycle.cpp:300-325 / 829 / 335` 窗口创建、SetParent 到桌面宿主、1×1 输入窗口
- `src/desktop/windows_desktop_layout.cpp:34-177` IFolderView 读坐标 + 独立 STA + deadline
- `src/app/render/app_paint.cpp:57-119` 脏矩形一穿到底
- `src/app/drag_drop/app_drag_preview_window.cpp:6-8` 拖拽 ghost 独立窗口（核心注释）
- `src/app/drag_drop/drag_render_cache.cpp:15-86` 静态层 revision 缓存（仅 87 行，值得整段读）
- `src/common/background_work.h:16-89` 后台工作队列 + 4ms/32 条 Drain 配额
- `src/common/bounded_lru_cache.h:13-47` O(1) 有界 LRU
- `src/common/constants.h:263-345` 防抖/轮询常量表
- `src/app/lifecycle/app_lifecycle.cpp:905-1030` explorer 重启自愈 + 2s 看门狗
- `src/desktop/display_topology_refresh.h:40-48` 变大才重建窗口
- `src/app/input/desktop_passthrough_indicator.h:12-34` 4 条 4px 逃逸带

**DeskBox**（`/tmp/ref/deskbox`）
- `docs/baselines/memory-scenarios.json` 性能基线四口径
- `deskbox-aot-curve-samples.csv` 采样 CSV schema（含 handles/threads）
- `scripts/aot-memory-curve-protocol.md` 实验协议（含踩坑清单）
- `scripts/measure-startup.ps1:31-45` 启动耗时哨兵行解析
- `src/DeskBox/App.QuiescenceWorkingSetTrim.cs:109-115` 空闲还内存的二次校验
- `src/DeskBox/App.xaml.cs:647-665` 日志有界队列
- `src/DeskBox/Services/DesktopBlankHitTest.cs:74-111` SysListView32 归属校验（含 GetShellWindow 返回 0 的兼容）
- `src/DeskBox/Services/WidgetLayerService.cs:1075-1077` 绝不主动创建 WorkerW
- `src/DeskBox/Platform/Win32Helper.cs:195-216` `BeginDeferWindowPos` 批量定位
