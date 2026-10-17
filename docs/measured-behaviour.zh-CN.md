[English](measured-behaviour.md) | **中文**

# 本机上光标捕获的实测行为

本文记录的是**实测**到的行为，而不是文档所述或所假设的行为。下文每一条结论都有 `rattrap probe` 或 `probe/` 中早期 C++ 探针产出的产物作为依据。凡是曾按文档行为作出假设、后来发现是错的地方，都明确标出，因为这些更正是本文最有价值的内容。

本机：Windows，主显示器 `\\.\DISPLAY1` 3440x1440 @ 120 DPI，副显示器 `\\.\DISPLAY2` 1920x1080 @ 96 DPI，`CursorBaseSize = 80`，辅助功能 `CursorSize = 4`。

---

## 1. 桌面复制可用，且是唯一可行的全屏路径

`rattrap probe`，第 4 节：

```
OK: image 1920x1080, rotation 1, desktop image in system memory: False
readback path: staging texture (CopySubresourceRegion)
CreateTexture2D  hr=0x00000000 texture=0x1E4010EB120
Map(staging, D3D11_MAP_READ) hr=0x00000000 pData=0x1E443276000 rowPitch=256 depthPitch=16384
```

* 复制会话会在指针所在的那台显示器上打开并投递帧。
* 在本机上 `DXGI_OUTDUPL_DESC.DesktopImageInSystemMemory` 为 **FALSE**，因此走的不是 `MapDesktopSurface` 这条路；像素来自一块由 `CopySubresourceRegion` 填充的小型暂存纹理（staging texture），它只传输所请求的那个矩形。
* `GetDesc` 报告 `Rotation = 1`（恒等），因此这里不需要旋转变换。

## 2. 复制得到的图像**不**包含光标

这是设计使然，也是对整个架构最重要的一个事实：合成器通过 `GetFramePointerShape` 单独交出光标。

实测到的指针形状：

```
type=2 89x87 pitch=356 hotspot=(3,3) bytes=30972
alpha histogram: opaque=40 transparent=6622 partial=1081
alpha bbox = (3,2) 36x51
```

解码并渲染后，该形状就是那个熟悉的白色箭头加上柔和的投影——一个半透明光标，这就是为什么只有 40 个像素是完全不透明的。

## 3. 更正：GDI `BitBlt` 同样**不**包含光标

这一点曾被假定为真，但在本机上是假的。用两种 API 在几乎同一时刻捕获同一块 128x128 区域：

```
background (DXGI) status = Captured
composite  (GDI)  status = Captured
differing pixels (>8) = 0 of 16384 (0.00%), mean |delta| = 0.00
```

差异像素为零，而把 GDI 捕获渲染成图像后，该区域内任何地方都看不到光标，尽管 `GetCursorPos` 显示光标就在其中。

DWM 把光标合成在自己的平面上，而 `BitBlt`（带 `CAPTUREBLT`）和桌面复制都不读取那个平面。**本机上不存在这样一对 API：其中一个返回光标而另一个不返回。**

为什么这个更正值得保留：最初的架构想要一对“合成来源／背景底板来源”，以便把 alpha 恢复变成一个两方程的求解问题。这条捷径并不存在，求解器必须改为从光标恰好没有覆盖某个像素的那些帧中获取背景样本。见 `algorithm.zh-CN.md`。

## 4. 更正：光标的 alpha 通道有两处需要小心

* 对活动光标调用 `GetIconInfo` 返回的 32x32 `hbmColor`，其 alpha 通道并不是干净的 0/255 掩码（早期探针：`nonzero=105/1024 full255=0 min=0 max=254`），因此它本身并不是渲染后光标的忠实来源。
* DXGI 的 `COLOR` 指针形状**是**预乘（premultiplied）BGRA。对这个光标而言，全部 1081 个半透明像素都满足 `R,G,B <= A`，这正是预乘的特征；`PointerShapeDecoder` 会对它做反预乘。**这一推断是启发式的**，对于一个抗锯齿边缘为中灰且使用直通 alpha（straight alpha）的光标来说它会是错的；因此解码器会报告自己做了什么，并且该模式可以覆盖。见 ROADMAP.zh-CN.md。

## 5. `DXGI_OUTDUPL_POINTER_POSITION` 是形状的左上角，而不是热点

这是通过实测而非阅读文档确定的：

```
GetCursorPos (settled)      = (1739,1954)   <- the hotspot, by definition
DXGI pointer (output space) = (957,503)  visible=True
DXGI pointer (desktop space)= (1727,1943)
measured offset P - Q       = (12,11)
DXGI shape hotspot field    = (11,11)
```

该偏移量与形状自身的 `HotSpot` 字段相差不超过一个像素（残差与鼠标在两次采样之间发生移动相符，这是意料之中的，因为采样与 `GetCursorPos` 调用不是原子的）。因此：

```
hotspot_on_screen = monitor_rect.origin + PointerPosition + shape.HotSpot
```

`PointerAnchorCalibrator` 在运行时重新测量这一点，而不是信任该常量，因此行为不同的驱动会被检测出来，而不是被静默地放错位置。

## 6. 桌面静止时，复制不投递任何帧

如果屏幕上没有任何变化，`AcquireNextFrame` 会一直返回 `DXGI_ERROR_WAIT_TIMEOUT`。即使什么都没动，采样也需要一次观测，因此 `DxgiDuplicationSource` 会缓存它上次读取的矩形，并用 `IsCached = true` 重放它。这样调用方就能区分“屏幕未变化”与“没有数据”。

## 7. COM vtable 布局：两个值得记录的失误

在 Native AOT 下手写 vtable 分发没有编译期安全网。以下两处都在构建时毫无动静，却在运行时产生了荒谬的结果：

1. 按*接口内部*而非*在 vtable 内部*来数方法索引（差了 3 个槽位，因为 IUnknown 占据槽位 0..2）。槽位 4 上的 `IDXGIDevice::GetAdapter` 实际上是 `IDXGIObject::SetPrivateDataInterface`；由此得到的垃圾指针在 `IUnknown::Release` 内部崩溃。
2. 假设 `ID3D11DeviceContext` 派生自 `IUnknown`。它派生自 **`ID3D11DeviceChild`**，后者增加了 4 个槽位，因此 `Map` 位于绝对槽位 **14**，而不是 10。槽位 10 是 `PSSetSamplers`，一个 `void` 方法，于是该调用返回了寄存器里残留的垃圾值：实测 `hr=0x794F9D00`，这不符合有效 HRESULT 的形式，且映射指针为空。`ID3D11Device` *确实*直接派生自 `IUnknown`（由 `CreateTexture2D` 在槽位 5 上正常工作得到确认）。

这两点都记录在 `src/RatTrap/Interop/DxgiInterop.cs` 中，并附有完整的绝对索引审计表；`rattrap probe` 中还有一套逐步的回读诊断（`DiagnoseReadback`），会报告每次原生调用的 HRESULT，因为这些失败看起来并不像失败。

## 8. 约束构建方式的环境事实

| 事实 | 后果 |
| --- | --- |
| `dotnet` SDK 9.0.318，本地 NuGet 缓存中的 ILCompiler 9.0.20 | Native AOT 发布可以完全离线完成 |
| NuGet 的 TLS 出网被阻断 | 无法使用包引用；本项目的包引用数为 **零** |
| MSBuild 的 `Csc` 任务与 Roslyn 服务器需要**命名管道** | 在禁止命名管道的沙箱下，`dotnet build` 会从 `Microsoft.CSharp.Core.targets` 抛出 `UnauthorizedAccessException` 而失败。`Directory.Build.props` 中的 `UseSharedCompilation=false` 让编译留在进程内 |
| 在受限沙箱中，用 PowerShell 管道接 `dotnet` 的输出同样会失败 | 构建行为异常时，不要用管道运行 `dotnet` |
| `SetProcessDpiAwarenessContext(PMv2)` 返回 FALSE 且错误码为 0 | 应用程序清单已经应用了它；这是成功，不是失败 |

## 9. 构建 GUI 过程中被迫作出的更正

构建交互式窗口又暴露出三处曾被假设为真、实则为假的地方。这三处都是通过实际运行发现的，而不是读文档读出来的。

### 9.1 复制会话是独占的，第二次尝试失败会损害第一个会话

第一版 GUI 先为指针形状打开了一个复制会话，然后又向 `CaptureFactory` 索取捕获源，后者在同一个输出上打开了**第二个**复制会话：

```
compositor: Desktop Duplication open on \\.\DISPLAY2 (1920x1080)
DXGI Desktop Duplication unavailable, falling back to GDI:
  DuplicateOutput failed on \\.\DISPLAY2: the output refused a duplication session (0x80070057)
```

`0x80070057` 就是 `E_INVALIDARG`。第二个会话如预期般失败——而从那一刻起，**第一个**会话就再也没有报告过任何指针形状，而这台机器上只打开一个复制会话时，第一帧就会报告形状。修复方式是结构性的而非防御性的：每个会话只用一个来源；这已经足够，因为复制只排除*系统*光标，仍会包含应用程序自绘的光标。工厂中旧的“合成来源 + 背景底板来源”配对已被移除，因为它存在的唯一目的是利用一个 §3 已表明并不存在的差异。

### 9.2 指针空闲就没有指针形状，而重新打开会话正是解决办法

`GetFramePointerShape` 只在形状*发生改变*的那一帧被填充。没人碰鼠标就没有变化，于是什么都不报告。实测：九秒内 83 帧，零次形状报告，而这块桌面上光标明明清晰可见。

试过两种补救办法：

| 补救办法 | 结果 |
| --- | --- |
| 把指针挪一个像素再挪回去（`SetCursorPos`） | 有效，但**两次移动之间必须有延迟**。连续发出时它们会合并成一次变化，合成器就不会重新合成。 |
| 关闭并重新打开复制会话 | 每次都能可靠生效，因为全新的 `DuplicateOutput` 总会投递一个完整的初始帧。这是主要补救办法。 |

尽管如此，这个轻推确实是对用户输入状态的一次真实（虽然微小）改动，因此每个会话最多执行一次，并且只在重新打开没有奏效时才执行，还可以用 `--no-nudge` 关闭。

### 9.3 零超时的 `AcquireNextFrame` 完全不会报告任何形状

第一版 GUI 以 0 超时进行轮询。即使复制会话正在投递帧、且有指针形状待取，也从未看到任何形状，因为 `GetFramePointerShape` 只对真正**已获取**的帧有效，而在没有任何变化时 `AcquireNextFrame(0)` 会立即超时。任何意在于观测指针形状的轮询间隔都必须传入一个真实的超时值——CLI 用的是 100 ms。

### 9.4 更正：本机的 `.cur` 加载器对任何文件都不保持像素

在这里加载*任何* `.cur` 再读回像素，得到的都不是当初存储的内容：

* `C:\Windows\Cursors\aero_arrow.cur` 的 32x32 条目读回后**有 4096 字节中的 551 字节不同**，最大差值为 255（存储的 alpha 189 读回来是 0）。通过 `GetBitmapBits` 得到完全相同的结果，所以问题在加载器，而不在读取 API。
* 无论存储尺寸如何，每个 `.cur` 都会被重新缩放到 32x32：16x16 和 48x48 的条目都返回 32x32，热点也按比例缩放。

这也回过头解释了 §4 中记录的那个反常的活动光标 alpha（`max=254`，没有完全 opaque 的像素）——问题出在加载器，而不是光标格式。其结果是，在本机上光标文件的像素保真度只能针对文件**字节**来确认，`tools/verify_cursor_output.py` 做的正是这件事；此外还有经由 `.ico` 路径（`idType=1`）的字节精确往返，该路径使用完全相同的 DIB 载荷，并且在这里*确实*是精确的。

## 10. 这些测量**未**覆盖的内容

* **从未捕获过应用程序自绘的光标**，因为本次会话中没有可用的独占全屏游戏。而那正是重建求解器所针对的场景。因此自检改为创建一个真实窗口，把一个已知的光标精灵画进它自己的帧，再通过桌面复制捕获它——真实的硬件像素、已知的真值——而不是模拟捕获过程。
* 除恒等之外的**旋转**未被测试；复制后端会拒绝旋转过的输出并回退到 GDI。
* **HDR** 和受 DRM 保护的输出未被测试。
* **并发复制者**（例如同时运行的另一个捕获或推流工具）未被测试；每个输出的复制数量上限被当作一种可能失败的情况来处理，并给出明确的诊断，而不是当作假设。

## 11. 第二轮工作被迫作出的更正

### 11.1 对临时文件而言，`%TEMP%` 不是一个安全的假设

在受限环境（`workspace-write` 模式下的 DSH 文件沙箱，以及任何类似的策略）中，子进程会被拒绝写入 `%TEMP%`，而程序目录却是可写的。实测：`rattrap probe --out %TEMP%\x` 退出码为 2 且什么都没写，输出自检也在它自己的临时目录上以 `UnauthorizedAccessException` 失败——这个失败与被测代码毫无关系。现在自检会依次探测 `%TEMP%`、程序目录、当前目录，并报告它实际用了哪个。

### 11.2 放大镜不会把硬件光标降级，文档也是这么说的

`MagShowSystemCursor` 的文档说明是显示或隐藏系统光标，没有引用计数，也不与光标如何被合成发生任何交互。放大镜实际做的是：当它具有 `MS_SHOWMAGNIFIEDCURSOR` 样式时，把光标画进它自己的窗口——这与本项目自己早期的探针结果一致。“它强制硬件光标变成软件光标”这一说法没有依据。见 `docs/fullscreen-capture.zh-CN.md`。

### 11.3 确实包含光标的公开 API

`GraphicsCaptureSession.IsCursorCaptureEnabled`，文档描述为“捕获会话是否会把光标包含在捕获内容中”，默认为 true，在 Windows 10 2004（10.0.19041.0）中引入。它就是上文 §3 所记录其缺失的那对合成／背景，而且它是一个有文档的 API，而不是 hook。这里尚未实现——要实现它需要做什么见 `docs/fullscreen-capture.zh-CN.md` §4。

### 11.4 捕获保护不是权限问题

`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 按设计会让窗口对每一种捕获 API 都隐藏，文档也明确指出它“仅在桌面窗口管理器（DWM）正在合成桌面时才有效”，并且它不是安全功能或 DRM 功能。`GetWindowDisplayAffinity` 要求窗口属于调用进程，因此无法读取另一个应用程序的 affinity；该工具改为检测症状，报告出一块纯色的捕获区域并列出可能的原因。

屏幕捕获本身不受完整性级别（integrity level）限制：它读取的是合成后的桌面，而桌面是会话对象而非每进程对象，并且两个后端都已在不提权的情况下验证通过。因此本项目不需要管理员权限。

## 13. 完整性边界不是捕获边界

这个问题是在"从非提权进程捕获一个**以管理员身份运行**的应用上的指针"之后提出的：按直觉屏幕应该不可访问，但光标却成功回来了。错的是那个前提，而它值得写下来，因为它是很自然的假设。

UIPI——UAC 背后的完整性机制——阻止低完整性进程**向高完整性进程发送消息**、**驱动它的窗口**、以及**以写权限打开它的进程**。它并不过滤合成器绘制的内容。Desktop Duplication 返回的是该输出的桌面合成结果，而合成是**按会话**的，不是按完整性级别的，所以普通进程复制显示器时会看到属于提权进程的窗口。指针形状通道来自同一个合成结果，这就是光标也会到手的原因。这也正是屏幕捕获工具不需要以管理员身份运行的原因；它与这里观察到的现象一致，而不是矛盾。`[依据该 API 的公开行为推理，且由用户报告；未对刻意提权的测试进程实测——本次会话无法提权]`

**真正**会阻止捕获的是另外几种边界，值得了解，因为它们不是靠变通能解决的：

* **不同的会话**。服务、其他已登录用户、登录界面都不属于本会话，其合成本会话的输出复制不到。
* **安全桌面**。UAC 同意提示会切换到它自己的桌面；在它显示期间复制普通桌面得不到有用内容。
* **受保护的输出**。显示驱动遵守其保护的内容会按设计返回黑色，其中包括某些串流与播放表面。
* **不支持复制的输出**。已被当作一种可能失败的情况处理并给出诊断，列在第 9 节，而不是被假设。

所以：从非提权进程捕获提权应用的窗口及其光标，是**预期行为**而不是漏洞。这也正是本程序从不请求提权的原因：它不需要提权就能看见屏幕，而提权只会让之后它写的文件和打开的文件夹更不好用——那恰恰就是"不是管理员模式，打开文件夹仍然弹出错框"所报告的那个缺陷。
