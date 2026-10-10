[English](fullscreen-capture.md) | **中文**

# 在全屏游戏中捕获光标

本文是对本项目中最棘手的未解问题的调查：全屏游戏的光标捕获不到，而一些显而易见的取巧想法一旦与文档对照便站不住脚。本文记录查了什么、答案是什么，以及什么值得去做。

## 1. 症状，精确表述

在窗口模式和无边框模式下，光标能被捕获。在独占全屏模式下不能。有三种不同机制会造成这种结果，而它们需要不同的应对：

| 原因 | 实际发生的事 | 可修复？ |
| --- | --- | --- |
| **应用程序自绘光标** | 游戏调用了 `ShowCursor(FALSE)` 并把自绘的十字准星画进自己的帧，因此该光标就是普通像素 | 可以——这正是重建求解器所针对的情形 |
| **硬件覆盖层／MPO 平面** | 光标被合成在一个独立平面上，捕获 API 不读取该平面 | 可以，通过另一个 API：见 §3 |
| **捕获保护** | 窗口调用了 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，或内容受 DRM 保护 | **不可以**——这是设计使然，任何权限级别都改变不了 |

该工具现在会在运行时区分这些情况，而不是报告一个笼统的失败：当合成器如此指示时它报告受保护内容，此外它还会报告一块纯色的捕获区域并列出三种可能原因（见 `CaptureSession.cs` 中的 `DiagnoseCapture`）。

## 2. 放大镜的思路行不通，文档说明了原因

曾有人提出：放大镜“破坏”了硬件覆盖层，迫使光标降级为软件光标并被合成进主帧缓冲，这正是放大镜能显示光标而屏幕捕获不能的原因。

前半句并非该 API 的行为。出自 [MagShowSystemCursor (magnification.h)](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowsystemcursor)，其全部文档化行为是：

> Shows or hides the system cursor.
> 中文：显示或隐藏系统光标。

> This function does not associate a reference count with the visibility state of the system cursor.
> Instead, the specified visibility state takes effect immediately, regardless of any previous calls to
> `MagShowSystemCursor`.
> 中文：此函数不会为系统光标的可见性状态关联引用计数。指定的可见性状态会立即生效，与之前对 `MagShowSystemCursor` 的任何调用无关。

> The system cursor is always magnified when it is shown while the full-screen magnifier is active.
> 中文：当全屏放大镜处于活动状态时，系统光标只要显示出来就总是被放大。

> When used with a magnifier control, calls to `MagShowSystemCursor` have no effect on the magnified
> system cursor. The visibility of the magnified system cursor depends on whether the magnifier control
> has the `MS_SHOWMAGNIFIEDCURSOR` style.
> 中文：与放大镜控件一起使用时，调用 `MagShowSystemCursor` 对放大后的系统光标没有影响。放大后的系统光标是否可见，取决于放大镜控件是否具有 `MS_SHOWMAGNIFIEDCURSOR` 样式。

文档中没有任何硬件光标降级为软件光标的内容，也没有关于它与合成引擎如何渲染光标交互的任何记载。放大镜实际做的事更简单、也更没用：在设置 `MS_SHOWMAGNIFIEDCURSOR` 时，它**把光标画进自己的窗口**，用的是它自己能读到的光标位图和位置。这与本项目自己早期的探针结果一致：设置该样式时放大镜的输出包含光标，未设置时不包含（`probe/out/mag_run2.txt`）。

因此放大镜唯一能帮忙的形式是“捕获放大镜的窗口”，这比它要替代的那个 API 更糟：它多出一个无法在独占全屏游戏之上显示的窗口，它的缩放回调（`MagSetImageScalingCallback`）已被弃用且在本机上实测触发次数为零，而且它首先就需要光标位图——而当绘制光标的是合成器时，我们本来就直接拥有它。

## 3. 答案：Windows.Graphics.Capture 已经解决了这个问题

存在一个公开的、有文档的、不做 hook 的 API，它会把光标包含在捕获内容中，而且操作系统自己的屏幕录制用的就是它：**Windows.Graphics.Capture（WGC）**。

[GraphicsCaptureSession.IsCursorCaptureEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled)：

> Gets or sets a value specifying whether the capture session will include the cursor in the captured
> content.
> 中文：获取或设置一个值，用于指定捕获会话是否会把光标包含在捕获内容中。

> `bool` - True if the cursor should be captured; otherwise, false.
> 中文：`bool` —— 如果应捕获光标则为 true；否则为 false。

> Device family: Windows 10, version 2004 (introduced in 10.0.19041.0)
> 中文：设备系列：Windows 10 版本 2004（自 10.0.19041.0 引入）

这正是本项目缺失的那一半，值得精确说明其中的原因。

`docs/measured-behaviour.zh-CN.md` §3 记录了塑造整个架构的那次测量：在本机上 GDI `BitBlt` 与桌面复制都不返回光标，而且两者在 128x128 区域上逐像素一致（16384 字节中有 0 字节不同）。最初的设计假设其中一个后端会包含光标而另一个不会，alpha 求解本应利用这一对。而这一对并不存在。

WGC 提供了它：

* `IsCursorCaptureEnabled = true`（**默认值**）给出**合成图**：帧中包含绘制在场景之上的光标。
* `IsCursorCaptureEnabled = false` 给出**干净的背景底板**。

这两者就是 `docs/algorithm.zh-CN.md` 中模型的 `D` 与 `B`，且观测于同一时刻，既不需要光标移动，也不需要背景变化。Alpha 不再是搜索问题，而变成一个两方程求解问题：

```
D = alpha*C + (1 - alpha)*B     and     B known exactly
```

对于在单一一对帧中被覆盖的像素，这仍是一个含两个未知量的方程，但在两对或更多具有不同 `B` 的样本上它就被完全确定——而且与当前方法不同，这种配对是可信的，因为两次观测来自同一时刻，而不是“大概”来自同一背景。

次要优点，都与全屏场景相关：

* 它捕获的是**显示器**或**窗口**，因此可以在不包含桌面其余部分的情况下捕获一个窗口化的游戏。
* 它是游戏栏（Game Bar）和截图工具（Snipping Tool）使用的 API，因此是经过最多真实游戏场景检验的路径。
* 它不受每个输出复制数量上限的限制，因此可以与另一个复制使用者（例如 OBS 的显示器捕获）同时运行。
* 无需 hook、无需驱动、无需注入：反作弊没有任何理由反对。这直接回应了促成这个问题的顾虑。

### 它解决不了什么

`WDA_EXCLUDEFROMCAPTURE` 对 WGC 同样有效。出自 [SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)：

> `WDA_EXCLUDEFROMCAPTURE` 0x00000011 - The window is displayed only on a monitor. Everywhere else, the
> window does not appear at all.
> 中文：`WDA_EXCLUDEFROMCAPTURE` 0x00000011 —— 该窗口仅在一台显示器上显示。在其他任何地方，该窗口完全不出现。

> This function and `GetWindowDisplayAffinity` are designed to support the window content protection
> feature that is new to Windows 7. This feature enables applications to protect their own onscreen
> window content from being captured or copied through a specific set of public operating system
> features and APIs.
> 中文：此函数与 `GetWindowDisplayAffinity` 旨在支持 Windows 7 新增的窗口内容保护功能。该功能使应用程序能够通过一组特定的公开操作系统功能和 API，保护自己屏幕上显示的窗口内容不被捕获或复制。

另请注意，`GetWindowDisplayAffinity` 要求窗口属于*当前进程*，因此无法读取另一个应用程序的 affinity；该工具改为检测症状（一块纯色的捕获区域）。

受 DRM 保护的输出同样被排除，复制会通过 `DXGI_OUTDUPL_FRAME_INFO.ProtectedContentMaskedOut` 显式报告这一点，本项目现在会把它呈现给用户。

## 4. 实现 WGC 需要做哪些工作

障碍不是概念上的，而是打包上的。WGC 属于 WinRT，而本项目**没有任何包引用**，因为 `nuget.config` 清除了所有源（构建机器没有可用的到 nuget.org 的 TLS 出网，而且本项目有意做到可离线构建）。常规路线——`Microsoft.Windows.SDK.NET.Ref` 中的 C#/WinRT 投影——就是一个包。

替代方案与本仓库中对 DXGI 已经采用的做法相同：手写声明 COM 接口并直接调用 vtable。需要的部件有：

| 部件 | 来源 |
| --- | --- |
| `RoGetActivationFactory`, `WindowsCreateString`, `WindowsDeleteString`, `WindowsGetStringRawBuffer` | `combase.dll`，平坦导出（flat exports） |
| `IGraphicsCaptureItemInterop` (`CreateForMonitor`, `CreateForWindow`) | 本地声明，IID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356` |
| `IGraphicsCaptureItem` (`DisplayName`, `Size`, `Closed`) | WinRT，按名称激活 |
| `IDirect3D11CaptureFramePoolStatics2` (`CreateFreeThreaded`), `IDirect3D11CaptureFramePool` (`Recreate`, `TryGetNextFrame`, `FrameArrived`) | 经由激活工厂获得 WinRT 静态接口 |
| `IDirect3D11CaptureFrame` (`SystemRelativeTime`, `ContentSize`, `Surface`) | WinRT |
| `IGraphicsCaptureSession` (`StartCapture`, `Close`, 以及其 Windows 10 2004 后继接口上的 `IsCursorCaptureEnabled`) | WinRT，外加一次 `QueryInterface` 以获取较新的接口 |
| `IDirect3DDxgiInterfaceAccess` (`GetInterface`) | 本地声明，用于从帧的 `Surface` 中取出 `ID3D11Texture2D` |
| 一个 `ID3D11Device` | `DxgiDuplicationSource` 中已有 |

大约 300-400 行，风格与 `src/RatTrap/Interop/DxgiInterop.cs` 相同，同样带有绝对 vtable 索引审计表，以及同样的逐步诊断钩子——正是它在 DXGI 的工作中抓到了那两个槽位错误。帧池可以在现有定时器节拍上用 `TryGetNextFrame` 轮询，而不必订阅 `FrameArrived`，这样就无需调度器队列，并使整套逻辑保持在 UI 线程上——与本项目其他所有后端的工作方式一致。

这是一项自成一体（self-contained）的工作，只有一个外部依赖（WinRT 激活路径）和一个可验证的验收测试：以两种方式设置 `IsCursorCaptureEnabled` 分别捕获桌面显示器，并确认光标恰好出现在两者之一中。

**正是这个验收测试使得它值得最先去做，早于路线图上的其他任何事情**：这将是本项目第一次拥有同一时刻的合成图与背景图，而求解器的其余部分也会因此变得更简单、更准确。

## 5. 建议，按顺序排列

1. **把 WGC 实现为第三个后端**，并用它包含光标与排除光标的两种模式作为合成／背景样本对。这是对全屏问题的直接回答，无需 hook，并且会在所有场景下提升精度，而不只是全屏。
2. **保留复制作为回退方案**，用于 WGC 不可用的机器（2004 之前的 Windows 10，或激活失败）。它已经可用，而且已经能精确报告指针形状。
3. **不要走放大镜这条路。** 文档并不支持它所依赖的机制，它唯一有用的回调已被弃用，而且它复杂度严格更高、收益严格更少。
4. **不要走驱动级或 API hook 的路子。** 既然 WGC 已经存在，这已无必要；而且它是唯一一个很有可能*会*引起反作弊注意的方案——这与需求恰好相反。
5. **接受受保护内容这一情形。** `WDA_EXCLUDEFROMCAPTURE` 与 DRM 都是有意为之、有文档记载的，而且不是权限问题。以管理员身份运行无法绕过它们，除了给屏幕拍照之外，其他任何办法也不行。该工具现在会明说这一点，而不是静默失败。
