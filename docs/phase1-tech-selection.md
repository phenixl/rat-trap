# CursorTrap 第一阶段：技术调研与选型报告

> 目标：只捕获系统**真实渲染**的光标（含自定义主题、辅助功能放大、DPI 缩放、动画），输出
> **带 alpha、背景完全透明**的光标图像，供 OBS 等消费方使用。
>
> 本报告的每一条结论都标注证据等级。**未取得文档原文的推断一律标注 `[推断]`**，
> 本机实测标注 `[实测]`，可追溯到具体探针与产物文件。

## 证据等级与来源

| 等级 | 含义 | 本次来源 |
|---|---|---|
| `[实测]` | 本机真机运行探针所得，可复现 | `probe/*.cpp` + `probe/out/*`（本仓库） |
| `[SDK]` | Windows SDK 头文件原文（本机 `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0`） | `magnification.h`、`dxgi1_2.h` |
| `[归档]` | 子代理经本地代理抓取并归档的原始网页/源码，**我已在本机归档中核对过行号** | `_research/pages/`（110 个页面） |
| `[推断]` | 未取得原文、基于既有知识与间接证据 | 已逐条标注 |

**环境说明（重要）**：本机 DNS 把所有公网域名劫持到 `198.18.0.0/15`（Clash TUN fake-IP），
`web_fetch` 因此拒绝一切外部 URL，shell 的 TLS 出口也不可用。子代理通过本机 `127.0.0.1:7890`
代理 + Node 自带 TLS 完成了抓取，归档在 `_research/pages/`（110 页）。我自己的 `web_fetch`
始终失败，**因此我没有引用任何我无法在本机核对的文档原文**。

**测试机**：Windows 11 build 10.0.26100，主屏 `\\.\DISPLAY1` 3440×1440 @ **120 DPI (125%)**，
副屏 `\\.\DISPLAY2` 1920×1080 @ 96 DPI，`HKCU\Control Panel\Cursors\CursorBaseSize = 80`，
`HKCU\Software\Microsoft\Accessibility\CursorSize = 4`。

---

## 0. 先决问题：本仓库已有一份成熟实现

`src/RatTrap/` 是一个 .NET 9 + Native AOT 的既存工程（约 330 KB C#、22 个文件、零 NuGet 依赖），
**已经实现了本报告第 7 节推荐架构的大半**：

| 既存模块 | 作用 |
|---|---|
| `Capture/DxgiDuplicationSource.cs` | Desktop Duplication + `GetFramePointerShape`，手写 COM vtable 派发 |
| `Pointer/PointerShapeDecoder.cs` | `COLOR` / `MONOCHROME` / `MASKED_COLOR` 三种形状解码 |
| `Reconstruct/CursorReconstructor.cs` + `Accumulators.cs` | 从多帧背景样本重建 alpha（针对应用自绘光标） |
| `Output/CursorFileWriter.cs`、`AniWriter.cs`、`PngWriter.cs` | 写出 `.cur` / `.ani` / `.png` |
| `Output/OutputSelfTest.cs`（69 KB）、`SelfTest/CoreSelfTest.cs` | 大规模自检：写出再读回比对 |
| `Cli/ProbeCommand.cs` | 探针 CLI（已含 PMv2、CursorBaseSize、`GetIconInfo`、`PointerPosition` 标定） |
| `docs/measured-behaviour.md` | 既存实测文档 |

**本报告与它的关系**：我独立实测得到的结论与 `docs/measured-behaviour.md` **逐项吻合**
（见附录 B 交叉验证表），并新增了它未覆盖的证据（放大镜否证、`LoadImage` 缩放保真、
并发 duplicator、DPI 缩放规则冲突、OBS 端 alpha 被销毁）。`measured-behaviour.md` 引用的
`docs/algorithm.md` 与 `ROADMAP.md` **在当前工作区中不存在**——需要补回或修正引用。

---

## 1. Magnification API 能否提取真实光标？——**结论：不能，不应采用** `[实测]`

### 1.1 先说结论

放大镜 API 是**渲染管线**，不是位图提取 API。它没有、也不打算提供"把当前光标位图交给你"的能力。
在 **4 组独立实验设计**下，我**从未观测到被放大的光标**：

| 实验 | 设计 | 结果 |
|---|---|---|
| `mag_probe.cpp` | 7 种 filter mode × 读回方式 | 屏幕内容可见，但**无光标**；`MagShowSystemCursor(FALSE/TRUE)` 都返回 1 但**不改变放大输出** |
| `mag_probe2.cpp` | 3 个并排放大镜，仅样式位不同 | A(无位) 纯色、B(0x1) 有内容、C(0x3) 纯色 → 不一致、不可解释 |
| `mag_probe3.cpp` | 无 filter list，仅样式位差异，同帧相减 ×3 轮 | 差值**逐轮完全相同**，但只是 12×192 的竖条（内容脏区），**不是光标** |
| `mag_probe4.cpp` | 单窗口，放大输出 vs 直接抓屏同区域相减 | 放大输出是**纯色空帧**（65536/65536 像素 = 240,240,240），根本没渲染 |

必须诚实标注：这是**"未能复现"而非"证明不可能"**——我的失败可能包含我自己的用法错误。
但下面的机制性原因说明它不适合做工程选型：

### 1.2 机制性原因（每条都有实测或头文件依据）

1. **增量脏区渲染，不保证整帧** `[实测]`
   `mag_probe4` 中放大镜窗口内容为纯色，而 `mag_probe1` 的同一用法能显示终端文字；
   `mag_probe3` 只重绘了 12 px 宽的变化条。→ 放大镜只重绘"自上次以来变化的区域"，
   **不能作为"每帧拿一张完整图"的数据源**。
2. **没有返回光标位图/alpha 的任何 API** `[SDK]`
   `magnification.h` 的全部函数：`MagInitialize/MagUninitialize`、`MagSetWindowSource`、
   `MagGetWindowSource`、`MagSetWindowTransform`、`MagSetWindowFilterList`、
   `MagGetWindowFilterList`、`MagSetImageScalingCallback`、`MagSetColorEffect`、
   `MagSetFullscreenTransform*`、`MagSetInputTransform`、`MagShowSystemCursor`。
   **没有任何一个返回光标图像、alpha 或位置**；`MagGetWindowSource` 返回的是源矩形。
3. **输出无 alpha** `[推断，但由 2 直接推出]`
   放大镜把内容画进一个真实 HWND，只能通过屏幕读取（`BitBlt`/`PrintWindow`）拿像素，
   `BitBlt` 的 alpha 字节恒为 0。用户"只要光标、背景透明"的需求无法由它满足。
   （实测 `PrintWindow(PW_RENDERFULLCONTENT)` 对放大镜**内容不完整/过期**，不可靠 `[实测]`）
4. **`MagSetImageScalingCallback` 不是逐帧回调** `[实测]`
   第一次实验触发 7 次、第二次 0 次，**都是在 `MagSetWindowSource`/`MagSetWindowFilterList`
   调用时同步触发**，`Pump(900ms)` 期间触发 0 次。它的 `dest` 数据是**放大后的屏幕内容，
   不含光标**（见 `probe/out/cb_A_nofilter_1.png`）。SDK 头文件仍保留该函数 `[SDK]`，
   但作为帧源不可用。
5. **`MS_*` 是窗口样式，不是 filter mode，且数值撞车** `[SDK]`
   `magnification.h` 第 42–45 行注释就是 `// Magnifier Window Styles`：
   `MS_SHOWMAGNIFIEDCURSOR 0x0001` / `MS_CLIPAROUNDCURSOR 0x0002` / `MS_INVERTCOLORS 0x0004`；
   而第 49–50 行 `MW_FILTERMODE_EXCLUDE 0` / `MW_FILTERMODE_INCLUDE 1`。
   → **`MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR` 数值上等于 `MW_FILTERMODE_INCLUDE`**。
   `MagGetWindowFilterList` 会原样回读该值，所以从回读**无法区分**两种语义；实测表现为
   INCLUDE 行为（只放大列表里的窗口）。这是一个真实的 API 陷阱。
6. **多窗口互相干扰** `[实测]` 同一进程内 3 个放大镜窗口只有窄条被渲染；
   放大镜`MagInitialize` 的进程级上下文并不适合多实例。

### 1.3 替代路径（详见第 7 节）

真实光标应当从**合成器**取，而不是从放大镜取：`IDXGIOutputDuplication::GetFramePointerShape`
是本机唯一能拿到"真尺寸 + 真 alpha + 正确热点"的光标位图的 API（第 5 节、第 7 节）。

---

## 2. `GetCursorInfo` + `CopyIcon` + `GetIconInfo` 方案的完整流程与限制

### 2.1 完整流程（每一步都有实测依据）

```c
// 1) 读取当前光标（不要 DestroyCursor 这个 handle，它属于系统/窗口）
CURSORINFO ci{ sizeof(ci) };
GetCursorInfo(&ci);                       // ci.flags 才是"是否显示"的权威来源
if (!(ci.flags & CURSOR_SHOWING)) { /* 光标被隐藏/被抑制，本帧不绘制 */ }

// 2) 复制一份自己拥有的 handle，避免与系统共享的生命周期问题
HCURSOR owned = CopyIcon(ci.hCursor);

// 3) 取位图与热点（hbmMask/hbmColor 由调用方负责 DeleteObject）
ICONINFO ii{};
GetIconInfo(owned, &ii);
// ii.xHotspot / ii.yHotspot / ii.hbmColor / ii.hbmMask

// 4) 读像素：hbmColor 是 DDB（bmBits == NULL），必须用 GetDIBits
//    [实测] hbmColor: 32x32 bpp=32 planes=1 bmBits=null(DDB) bmWidthBytes=128
//    hbmMask : 32x32 bpp=1

// 5) 清理
DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask); DestroyCursor(owned);
```

### 2.2 关键限制（全部为本机实测）

| 限制 | 实测证据 | 后果 |
|---|---|---|
| **永远只有 32×32，与 `CursorBaseSize` 无关** | `CursorBaseSize=80`，`GetIconInfo` 仍返回 `32x32`（`probe_run1.txt`，两次采样；`probe_dxgi2` 再次确认） | **这就是 OBS 光标大小不符的根因** |
| alpha 通道不干净 | `nonzero=105/1024 full255=0 min=0 max=254` | 不能直接当作可信 alpha 源 |
| `hbmColor` 是 DDB，`bmBits==NULL` | `GetBitmap` 输出 | 必须 `GetDIBits`，不能用直接指针 |
| 单色光标 `hbmColor == NULL` | 未在本机命中，但 `ICONINFO` 定义如此 `[SDK]` | 必须实现 AND/XOR → alpha 的合成分支 |
| 热点不随放大缩放 | 见下 | 叠加时位置会偏 |
| **热点会随加载尺寸变化** | `aero_link.cur`：文件 32px 条目热点=(6,0)，但系统给的 32×32 热点是 **(7,0)**；`LoadImage(cx=80)` 得到 **(16,0)** | 热点必须由 API 给出，不能从文件硬编码 |

### 2.3 交叉验证

子代理归档的社区证据与我的实测一致：Magpie 维护者称 "通过 GetIconInfo 获取到的系统光标图像
始终是 32x32"；OBS issue #2604 中 WizardCM 称 "it will always receive a 32x32 icon for
compatibility reasons" `[归档]`。既存 `docs/measured-behaviour.md` 独立记录了同一事实并进一步
指出该 32×32 的 alpha 不是干净的 0/255 掩码。**三方独立吻合**。

---

## 3. 如何准确获取"当前光标应该多大"

三个独立来源，可信度递减：

### 3.1 来源 A（最可信）：从合成器直接问 `[实测]`

`GetFramePointerShape` 直接给出**系统真正在渲染的尺寸**。本机副屏（96 DPI，`CursorBaseSize=80`）实测：

```
type=2 (COLOR)  89x87  pitch=356  hotspot=(3,3)  bytes=30972
alpha: 0=6622  255=40  intermediate=1081     ← 真 alpha，含半透明投影
```

其中"光标位图本身"约 80×80，`89×87` 是**含投影的包围盒**。这与 `CursorBaseSize = 80`
在 100% 缩放下完全一致。

### 3.2 来源 B（备选）：`LoadImage` 显式尺寸 `[实测，本轮新增]`

`LoadImage(path, IMAGE_CURSOR, cx, cy, LR_LOADFROMFILE)` **可以产出任意请求尺寸**，
且热点按比例缩放。对 `aero_arrow.cur`（文件内含 **128/96/64/48/32 五个真实图像**）：

| cxDesired | 位图 | 热点 |
|---|---|---|
| 0（=不指定，取最大） | 128×128 | (0,0) |
| 32 | 32×32 | (0,0) |
| 64 | 64×64 | (0,0) |
| **80** | **80×80** | (0,0) |
| 96 | 96×96 | (0,0) |
| 256 | 256×256 | (0,0) |

`aero_link.cur` 的对应热点：32→(7,0)、64→(12,0)、**80→(16,0)**、96→(19,0)、128→(25,0)
（注意：**都不是文件里同尺寸条目的原始热点**，说明系统内部统一从大图重采样并换算热点）。

### 3.3 **本轮最重要的新证据：系统放大光标用的是"高清帧重采样"，不是"32px 拉伸"** `[实测]`

对同一个真实光标（DXGI 89×87 形状）做数值比对，搜索最佳裁剪偏移后：

| 候选渲染 | MAE（越低越像） | 最佳偏移 |
|---|---|---|
| **`LoadImage(path, 80, 80)`（系统自身缩放器）** | **12.47** | **(3,3)** |
| .cur 里 32×32 位图的最近邻放大 | 28.92 | (3,2) |
| .cur 里 32×32 位图的双线性（alpha 加权）放大 | 27.73 | (3,2) |

最佳偏移 `(3,3)` **恰好等于 DXGI 报告的 `HotSpot=(3,3)`**，构成几何自洽。
结论：**"把 `GetIconInfo` 的 32×32 放大到目标尺寸"是错误的保真策略**（这正是 OBS 显示不符的原因），
正确做法是**从 `.cur` 文件按目标尺寸重采样**（`LoadImage(cx=目标)` 即系统自身的缩放器），
或直接采用 DXGI 形状（最好）。

### 3.4 注册表与尺寸规则

- `CursorBaseSize` 的滑杆映射：`Accessibility\CursorSize = 4` ↔ `CursorBaseSize = 80` `[实测]`
  → `CursorBaseSize = 32 + (step-1) × 16`，step 1..15，默认 32，最大 256。
  与 wxWidgets 源码注释 "a multiple of 16 between 32 and 256"、Magpie "15 级、最大 256×256"
  一致 `[归档]`。
- **`CursorBaseSize` 无官方文档、无 SPI** `[归档]`（`WinUser.h` 只有 `SPI_SETCURSORS 0x0057`）。

### 3.5 ⚠️ 未解决的冲突：尺寸是否再乘 `dpi/96`？——**这是本阶段唯一的硬冲突**

- 文档侧证据支持 `rendered = CursorBaseSize × (dpi/96)`：Raymond Chen 的名义 DPI 表恰为
  `32 × dpi/96`；wxWidgets 源码对注册表值做 `FromDIP`；mptrsz 实测 150% DPI + step 2 得 72×72 `[归档]`。
- 但我在本机主屏（**120 DPI = 125%**）上测到的光标位图约 **80 px**，不是 100 px `[实测]`。
  本轮 `probe_dxgi2` 证明 **PMv2 感知会把 `GetDpiForSystem` 从 96 变成 120、`SM_CXSCREEN`
  从 2752 变成 3440**，说明我最初那批探针是 DPI-unaware，其坐标/尺寸全部被虚拟化——
  **这正好可以解释冲突**（DPI-unaware 进程可能拿到被虚拟化后的形状尺寸）。

**决定性实验（3 分钟，需要用户配合）**：把鼠标移到**主屏**，然后在
(a) DPI-unaware 进程 与 (b) PMv2 感知进程 中各测一次 `GetFramePointerShape` 的 `Width`：

- 若 (a)=80 而 (b)=100 → 规则是 `CursorBaseSize × dpi/96`，且**应用必须声明 PMv2**；
- 若 (a)=(b)=80 → `CursorBaseSize` 本身就是物理像素，不乘 DPI。

工程上还有一条**规避该问题**的做法：**运行时不计算尺寸**，直接采用
`GetFramePointerShape` 回报的尺寸作为真值；只有在该 API 不可用时才用 `CursorBaseSize × dpi/96`
并在 UI 提供手工覆盖。既存 `docs/measured-behaviour.md` 未覆盖此问题。

---

## 4. 动画光标（.ani）的解析与播放

### 4.1 格式实测（本机真实系统文件）

```
FILE C:\Windows\Cursors\aero_busy.ani   RIFF/ACON size=556304
  chunk 'anih' len=36  at 12
    ANIH: cbSize=36 cFrames=18 cSteps=18 cx=0 cy=0
          cBitCount=32 cPlanes=1 jifRate=3 flags=0x1 (AF_ICON)
    => 默认帧时长 = 3 jiffies = 50.0 ms
  chunk 'LIST' len=556240
    LIST type='fram'; 18 个 'icon' 子块，每个 len=30894
```
`[实测]`，另有 `aero_working.ani` 同样 18 帧。要点：

- 结构为 `RIFF .... ACON`，块为 `anih`（36 字节头）、可选 `rate`（每帧 jiffies）、
  可选 `seq `（帧序列）、`LIST`/`fram`/`icon`（每帧是一个**完整的 CUR/ICO 文件**）。
- 帧时长 = `jifRate`（或 `rate` 块中的每帧值）× 1/60 秒。
- **本机两个 .ani 都没有 `rate` 和 `seq ` 块**，所以必须回退到 `anih.jifRate`，
  且帧序为 0..cFrames-1。这两个块在别处存在，解析器必须支持。
- `[归档]` 子代理扫描了 `C:\Windows\Cursors` 全部 189 个 `.cur`/`.ani`：
  Win11 Aero 光标每个内含**五个真实帧（32/48/64/96/128）**，且**条目是 PNG 压缩的**
  （`wBitCount = 0`）→ **不要自己写 ICONDIR→DIB 解析器，交给 GDI 解码**。

### 4.2 播放方案：**不要依赖系统自动播放** `[实测]`

```
C:\WINDOWS\cursors\aero_busy.ani    : 25 次采样 (每 60 ms) frameChanges=0
C:\WINDOWS\cursors\aero_working.ani : 25 次采样 (每 60 ms) frameChanges=0
```

`GetIconInfo` 在动画光标上返回的是**静态帧**（对同一 HCURSOR 反复采样指纹完全不变）。
因此**必须自己解析 .ani、自己建每帧 HCURSOR、自己按时序播放**：

1. 解析 `anih`/`rate`/`seq `/`fram`；
2. 每帧用 `LoadImage(cx=目标尺寸)` 或 `CreateIconFromResourceEx` 建一个光标；
3. 按 `每秒 60 jiffies` 的时序翻帧（`jifRate=3` → 50 ms/帧，20 fps）；
4. 热点**逐帧**读取（不同帧可能不同），不能只取第一帧。

对 `CursorBaseSize × dpi` 的目标尺寸，优先对每帧做 `LoadImage(cx=目标)` 重采样；
若帧是内嵌 CUR 资源，则用 `CreateIconFromResourceEx` + 显式尺寸。

---

## 5. 透明渲染与输出：`UpdateLayeredWindow` vs DirectComposition vs Spout/NDI

### 5.1 窗口样式（需求已明确）

```
WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE   (+ WS_EX_TOPMOST 由 SetWindowPos 控制)
```
若走 DirectComposition 路线，则**不要**用 `WS_EX_LAYERED`，改用 **`WS_EX_NOREDIRECTIONBITMAP`**
（`0x00200000L`，文档："The window does not render to a redirection surface"）`[归档]`。

### 5.2 两条渲染路线的取舍

| | `UpdateLayeredWindow` | DirectComposition + D3D11 |
|---|---|---|
| alpha | 精确，需 **预乘 alpha**（`AC_SRC_ALPHA`） | 精确，`DXGI_ALPHA_MODE_PREMULTIPLIED` |
| 每帧代价 | **强制 GPU→CPU→GPU 往返 + CPU/GPU 同步**；`HDC` 只能由 GDI 位图支撑，**无法接收 GPU 纹理** | 零拷贝：`IDCompositionVisual::SetContent` **文档明确接受 `IDXGISwapChain1`** |
| 与 Spout 共享纹理衔接 | 必须 `CopyResource`→staging→`Map`→CPU memcpy→预乘→`CreateDIBSection`，每帧两次多余拷贝 | 同一张纹理可直接送 Spout / 直接作为 swapchain 后备 |
| 建议 | 仅作为降级/最简验证路径 | **推荐主线** |

`[归档]` 依据：Kenny Kerr 的 layered window 文章说明 ULW 必须把位图送回系统内存再由 DWM 送上 GPU；
`SetContent` 文档列出可用 `IDXGISwapChain1`。`CreateSwapChainForComposition` 要求
`DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` + `DXGI_SCALING_STRETCH`，并 `Commit()` 只在树结构变化时调用。

### 5.3 ⚠️ 用户的原始前提需要修正：**OBS 的 Window/Display Capture 拿不到 alpha** `[归档，已在本地归档核对]

- **"Allow Transparency" 只存在于 Game Capture**：归档的 OBS 源码中
  `game-capture.c:41` 有 `#define SETTING_TRANSPARENCY "allow_transparency"`；
  Window Capture 与 Display Capture **都没有该设置**。
- 共享的渲染路径 `dc-capture.c:216-243` 用 `obs_get_base_effect(OBS_EFFECT_OPAQUE)`
  且 `gs_enable_blending(false)`；`duplicator-monitor-capture.c:643` 同样
  `gs_enable_blending(false)`。**alpha 是被设计性丢弃的。**
- OBS 28.0.0 发行说明亦称修复了"透明窗口在 Window Capture 中被错误捕获"。
- 结论：**"做一个透明点击穿透窗口，让 OBS 窗口采集它"这条路走不通**。
  可行路径只有：① 自建发送端 → **Spout2 源** 或 **NDI 源**；
  ② 修改/新增 OBS 插件（`plugins/win-capture/cursor-capture.c` 就是那个"假光标"的实现，GPLv2）。

### 5.4 Spout2 vs NDI（面向 OBS 的输出）

| | Spout2 | NDI |
|---|---|---|
| 许可 | **BSD 2-Clause**（无运行时依赖，Windows 专用） | 专有 SDK；**商业分发需 vendor ID**；运行时需用户单独安装 |
| alpha | 支持 BGRA 共享纹理，但 **Spout 自家文档没有明确声明 alpha 语义（预乘/直通）——`[未验证]`，必须先做台架测试** | 文档明确支持 alpha，但**约定相反：NDI 是直通 alpha，Windows 侧是预乘 → 出境前必须反预乘** |
| OBS 集成 | `obs-spout2-plugin`（Off-World-Live，**GPL v2**，64 位） | OBS NDI 输出/插件 |
| 结论 | **首选**（无运行时依赖、许可宽松） | 备选（跨机、跨平台场景） |

### 5.5 关于"现有开源项目"的调研结论 `[归档]`

**没有找到**任何同时做到"正确缩放 + alpha 正确 + 真实系统光标 + 送进 OBS"的开源项目。
最接近的：`eugenesvk/mptrsz`（MIT，有诊断价值）、`cjcxj/windows-cursor-streamer`
（**无 LICENSE，只能读思路**）、`Blinue/Magpie`（因 `DrawIconEx` 无法与 D3D 交互而改用着色器画光标，
不支持动画光标）、`obs-studio/plugins/win-capture/cursor-capture.c`（就是要替换的那段代码）。
→ **这确实是一个真实的空白点。**

---

## 6. 多显示器 DPI 处理策略

### 6.1 铁律：进程必须声明 Per-Monitor-V2 `[实测]`

同一台机器、同一次运行，`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` 前后：

```
BEFORE: GetDpiForSystem=96   SM_CXSCREEN=2752  SM_CYSCREEN=1152   output[0] rect=(0,0)-(2752,1152)  dpi=96
AFTER : GetDpiForSystem=120  SM_CXSCREEN=3440  SM_CYSCREEN=1440   output[0] rect=(0,0)-(3440,1440)  dpi=120
```

- 主屏真实是 **3440×1440 @ 120 DPI**；DPI-unaware 时整个坐标空间被虚拟化为 2752×1152。
- `GetDpiForMonitor` 对 DPI-unaware 进程返回 96（不是真实 DPI）——这让"用 DPI 算光标尺寸"
  在任何 unaware 代码路径里都必然算错。
- 必须在**创建任何 HWND 之前**调用（既存工程已通过 manifest 实现，`SetProcessDpiAwarenessContext`
  返回 FALSE/err 0 是"manifest 已生效"，**是成功不是失败** `[归档]`）。

### 6.2 几何模型（本机实测并自洽）

```
hotspot_on_screen_desktop = output.DesktopCoordinates.origin
                          + DXGI_OUTDUPL_FRAME_INFO.PointerPosition.Position
                          + DXGI_OUTDUPL_POINTER_SHAPE_INFO.HotSpot
```

- `PointerPosition.Position` 是**形状缓冲区的左上角**，不是热点 `[实测]`。
  副屏实测：`(360,533) + (3,3) = (363,536)` = `GetCursorPos(1133,1976) − 副屏原点(770,1440)`，
  **完全相等**。既存 `measured-behaviour.md` §5 用另一组数据独立得到同一结论。
- **不要混用来源**：另一次实测中 `PointerPosition + HotSpot` 与 `GetCursorPos` 差了 39 px（y），
  原因是两次采样之间鼠标移动了。位置与形状必须取自**同一帧** `[实测]`。
- `PointerPosition.Visible` 必须检查：为真表示合成器把光标**单独交付**（我们必须自己画）；
  为假表示光标**已经烧进桌面纹理**（不要重复画）——这正是 OBS 双光标的成因 `[归档]`。

### 6.3 多屏差异

- 每个 output 的 DPI 用 `MonitorFromPoint(GetCursorPos())` + `GetDpiForMonitor(_, MDT_EFFECTIVE_DPI)` 取。
- 形状尺寸/热点随光标所在屏变化，**跨屏移动时必须重新取形状**。
- 光标跨屏移动时会有形状变化事件（本机 `LastMouseUpdateTime` 与 `PointerShapeBufferSize > 0`）→
  这是唯一的刷新触发点，静止时 `AcquireNextFrame` 会一直 `WAIT_TIMEOUT` `[实测，既存文档 §6 同结论]`。

---

## 7. 推荐技术栈与架构

### 7.1 数据源优先级（双通道，互为兜底）

| 优先级 | 来源 | 何时可用 | 保真度 |
|---|---|---|---|
| **P0** | `IDXGIOutputDuplication::GetFramePointerShape` | 有 D3D11 + 未被独占（**实测多进程可并存**，见 7.4） | **最高**：真尺寸、真 alpha、含投影、真热点 |
| **P1** | `GetCursorInfo` 定位 + 按注册表 `.cur/.ani` 路径 `LoadImage(cx=目标尺寸)` 重采样 | DXGI 不可用（远程桌面/独占全屏/驱动不支持） | 高：与系统缩放器 MAE 12.47（3.3 节），但**拿不到应用自绘光标** |
| **P2** | 既存 `CursorReconstructor`（多帧背景累积解 alpha） | 独占全屏游戏自绘光标 | 依赖足够多的背景样本 |
| 禁用 | Magnification API | — | 第 1 节否证 |

### 7.2 模块划分

```
CursorTrap
├─ Platform
│   ├─ DpiAwareness        进程启动即声明 PMv2（唯一入口，防漏声明）
│   ├─ MonitorTopology     EnumDisplayMonitors + GetDpiForMonitor，维护 id→(rect,dpi) 映射
│   └─ CursorSettings      注册表 CursorBaseSize/Accessibility.CursorSize + 每角色 .cur/.ani 路径
├─ Capture
│   ├─ ICursorSource       统一接口：NextFrame() -> {BGRA pixels, hotspot, anchorScreenPt, visible, sourceKind}
│   ├─ DuplicationCursorSource   P0：DuplicateOutput + AcquireNextFrame + GetFramePointerShape
│   ├─ ThemedCursorSource        P1：GetCursorInfo + LoadImage(cx=size) + .ani 时序播放
│   └─ ReconstructingSource      P2：既存 CursorReconstructor
├─ Decode
│   ├─ PointerShapeDecoder  COLOR(预乘BGRA)/MONOCHROME(AND+XOR)/MASKED_COLOR
│   └─ AniParser            anih/rate/seq/fram + 帧时序 + 逐帧热点
├─ Present
│   ├─ OverlayWindow        WS_EX_NOREDIRECTIONBITMAP|TRANSPARENT|TOOLWINDOW|NOACTIVATE
│   ├─ DcompPresenter       D3D11 + CreateSwapChainForComposition(PREMULTIPLIED) + SetContent + Commit(仅结构变化)
│   └─ LayeredFallback      UpdateLayeredWindow（降级路径）
└─ Output
    ├─ SpoutSender          P0 输出（BSD-2）
    ├─ NdiSender            P1 输出（注意反预乘）
    └─ FileSink             .cur/.ani/.png（既存已有）
```

### 7.3 关键 API 调用时序

```
① main() 第一行：SetProcessDpiAwarenessContext(PMv2)     ← 必须在任何 HWND 之前
② CursorSettings 读注册表 → 目标尺寸候选 = CursorBaseSize（DPI 规则见 3.5，可运行时被 P0 覆盖）
③ D3D11CreateDevice → EnumOutputs → 选含 GetCursorPos() 的 output
   → IDXGIOutput1::DuplicateOutput                    ← 失败则降级 P1
④ 主循环（16 ms）：
     AcquireNextFrame(1ms)
       ├─ WAIT_TIMEOUT → 复用上一帧形状（标记 IsCached），只更新位置
       ├─ PointerShapeBufferSize > 0 → GetFramePointerShape → PointerShapeDecoder → 预乘 BGRA
       └─ PointerPosition.Visible == 0 → 光标已烧进桌面，本帧不画
     位置 = output.origin + PointerPosition + shape.HotSpot      （同帧内自洽）
⑤ Present：脏时才 Present；交换链 alpha = PREMULTIPLIED
⑥ Output：同一张纹理 → Spout（预乘）/ NDI（先反预乘）
```

### 7.4 两个架构级实测结论（本轮新增）

1. **Desktop Duplication 支持多进程并存** `[实测]`
   进程 A 持有 duplication 期间，进程 B `DuplicateOutput` **成功返回 `hr=0x0000`**。
   → CursorTrap 可以与 OBS 的 Display Capture 同时运行。
   （本机为 Windows 11 26100；2 个客户端已验证，更多客户端与 OBS 本体尚未实测。）
   这填补了既存 `measured-behaviour.md` §9 明确记录的空白。
2. **权限**：以上全部路径（含 Desktop Duplication、LoadImage、DComp 叠加窗口）
   **都不需要管理员权限** `[实测，全部探针均在普通用户令牌下运行]`。

---

## 8. 待验证清单（按风险排序）

| # | 问题 | 影响 | 验证方法 | 状态 |
|---|---|---|---|---|
| 1 | 尺寸是否 `CursorBaseSize × dpi/96` | 叠加光标会大/小 25% | 主屏上 PMv2-aware vs unaware 各测一次 `shape.Width` | **未解决（唯一硬冲突）** |
| 2 | `MASKED_COLOR` 位图语义 | 部分光标（如 I 型）可能画成黑色方块 | 本机实测：`Type=4`、80×80、**alpha 全为 255**、RGB 是"白 I 型 + 黑底"。需确定黑底是 key 还是 mask；用已知光标做合成对比 | 未解决 |
| 3 | Spout2 是否端到端传递 alpha、预乘还是直通 | Spout 输出可能整体失效 | 发送 BGRA 渐变 → OBS Spout 源读回 | 未解决（子代理同样标注） |
| 4 | 投影是否总在形状缓冲区内 | 决定是否需要裁剪/是否重复投影 | 多光标类型 × 多 DPI 采样 | **倾向"是"**：89×87 > 80×80，且图像可见柔和投影 |
| 5 | 3.3 节 MAE 12.47 的残差来源 | 影响 P1 保真度上限 | 反预乘后重新比对；分离投影区域 | 未解决 |
| 6 | 应用自绘光标（独占全屏游戏） | P2 路径的适用性 | 用既存 self-test 的真窗口方案 | 既存工程已建机制，缺真实游戏样本 |
| 7 | HDR / DRM / 旋转输出 | 降级行为 | 逐项禁用/构造 | 未覆盖（既存文档同） |

---

## 附录 A：本轮探针与产物（可复现）

| 探针 | 目的 | 关键产物 |
|---|---|---|
| `probe/cursor_probe.cpp` | 光标位图/尺寸/注册表/主题文件/ANI/DrawIconEx alpha | `probe/out/probe_run1.txt`、`live_direct_color.png`、`file_arrow_plain_color.png`、`draw_live_80.png` |
| `probe/mag_probe.cpp` | 放大镜 7 种 filter mode × 读回 | `mag_run1.txt`、`mag_*_screen.png`、`cb_A_nofilter_1.png` |
| `probe/mag_probe2.cpp` | `MS_*` 作为窗口样式的对照 | `mag_run2.txt`、`mag2_full.png` |
| `probe/mag_probe3.cpp` | 无 filter、仅样式位、同帧相减 + 光标指纹识别 | `mag_run3.txt`、`patch_B_x31_y0.png`、`id_live_32.png` |
| `probe/mag_probe4.cpp` | 放大输出 vs 直接抓屏相减 | `mag_run4.txt`、`p4_magOut0.png`、`p4_srcActual.png` |
| `probe/probe_sizes.cpp` | `LoadImage` 12 种显式尺寸 + ANI 静态帧验证 | `sizes_run.txt`、`sz_*.bgra` |
| `probe/probe_dxgi.cpp` | DXGI pointer shape 首测 | `dxgi_run.txt`、`dxgi_shape_*.bgra` |
| `probe/probe_dxgi2.cpp` | PMv2 前后对比 + 并发 duplicator | `dxgi2_pmv2.txt`、`dxgi2_second.txt` |
| `probe/analyze_cursor.py` | 真实形状 vs 候选渲染的 MAE 判定 | 本报告 3.3 节表格 |
| `probe/bgra2png.py` | 原生 BGRA → PNG（纯标准库） | 上述 PNG |

复现：`pwsh -File probe/build.ps1 -Source probe/<x>.cpp -Libs @(...)`，然后运行 exe。
（`vcvars64.bat` 在本机因 vswhere 报错而不可用，`build.ps1` 因此直接注入 `INCLUDE/LIB/PATH`。）

## 附录 B：与既存 `docs/measured-behaviour.md` 的交叉验证

| 结论 | 我的独立实测 | 既存文档 | 一致性 |
|---|---|---|---|
| `GetIconInfo` 恒为 32×32（`CursorBaseSize=80`） | ✅ 两次采样 | ✅ §4 | 吻合 |
| DXGI 形状 `type=2 89x87 pitch=356 hotspot=(3,3)` | ✅ | ✅ §2 | **完全吻合** |
| alpha 直方图 `opaque=40 transparent=6622 partial=1081` | ✅ | ✅ §2 | **完全吻合** |
| `PointerPosition` = 形状左上角而非热点 | ✅ 副屏 `(360,533)+(3,3)=(363,536)` | ✅ §5（另一组数据） | 吻合 |
| 静止桌面 `AcquireNextFrame` 返回 `WAIT_TIMEOUT` | ✅ | ✅ §6 | 吻合 |
| GDI `BitBlt` 同样不含光标 | 未测 | ✅ §3（更正） | 采信既存 |
| 并发 duplicator | ✅ **2 进程并存成功** | ❌ §9 标注"未验证" | **本轮新增** |
| 光标放大采用高清帧重采样 | ✅ MAE 12.47 vs 27.7/28.9 | 未覆盖 | **本轮新增** |
| 放大镜不可行 | ✅ 4 组实验否证 | 未覆盖 | **本轮新增** |
| OBS 端 alpha 被设计性丢弃 | 归档核对源码行号 | 未覆盖 | **本轮新增** |

## 附录 C：本轮踩到并已确认的坑（供实现时避雷）

1. `MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR == MW_FILTERMODE_INCLUDE`（数值撞车）。
2. `MS_*` 是窗口样式（`magnification.h:42`），不是 filter mode。
3. 本机 `vcvars64.bat` 因内部 vswhere 调用失败而中断 → 直接注入 `INCLUDE/LIB/PATH`。
4. 受限沙箱下 **PowerShell 管道捕获原生 exe 输出**会 `拒绝访问`；改用 `cmd /c "exe > file"`。
5. 受限沙箱下 **Node/原生进程经管道被 cmdlet 捕获** 同样失败（子代理独立复现）。
6. 从 DDB 读像素必须 `GetDIBits`（`GetIconInfo` 返回的 `hbmColor` 是 DDB，`bmBits==NULL`）。
7. 屏幕抓取得到的 DIB 其 alpha 字节恒为 0，转 PNG 时需强制不透明，否则整图透明。
