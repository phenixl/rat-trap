[English](README.md) | **中文**

# rat-trap

把光标（cursor）——或者游戏里的准星（crosshair）——从 Windows 屏幕捕获中还原成 PNG、`.cur` 或 `.ani` 文件。

单个自包含的 Native AOT 可执行文件，**零包依赖（zero package dependencies）**，因此在一台没有网络、也没装 .NET 运行时的机器上同样能构建和运行。

```
rattrap.exe capture --out out\
```

```
Cursor obtained via DxgiPointerShape: 89x87, hotspot (3,3), encoding AlphaBlend32
  alpha: 40 opaque, 1081 partial, 6622 transparent
Writing:
  out\cursor.cur  (.cur, AlphaBlend32, hotspot (3,3))
  out\cursor.png  (.png, 89x87, straight alpha RGBA)
  out\cursor.ani  (.ani, single frame)
```

或者直接运行 GUI（不带参数运行 `rattrap.exe`），按下全局热键，文件会自己出现。

---

## 为什么这不是一句 `GetIconInfo` 调用就能解决的事

存在两种不同的情况，而它们需要不同的机制。具体属于哪一种，取决于指针下方那个程序自身的性质，而不是用户的偏好。

| | 谁来绘制光标 | rat-trap 如何拿到它 |
| --- | --- | --- |
| **合成器（Compositor）** | 操作系统（桌面、大多数窗口化应用、无边框游戏） | 直接取合成引擎自己的位图，经由 Desktop Duplication 的 `GetFramePointerShape`。精确，且已经按 DPI 和辅助功能的指针大小设置缩放好了。约 1 秒。 |
| **逆运算重建（Reconstruction）** | 应用程序自己（游戏自己绘制的准星、远程桌面客户端渲染的远端指针） | 从捕获到的像素反解出来。光标只以像素形式存在，所以 alpha 和颜色要靠光标与场景各自独立变化的方式还原出来。 |

在这台开发机上的两项测量塑造了这个设计，而两者都与那个显而易见的假设相矛盾：

1. **`BitBlt` 和 Desktop Duplication 都不包含系统光标。** 在同一时刻用两者捕获同一块 128×128 区域，结果是 **16384 个像素中有 0 个不同**；DWM 把光标合成在它自己的平面上，这两个 API 都读不到。因此原设计想要的那对"合成源／干净背景源"根本不存在——但对系统光标而言也没有什么需要重建的，因为合成器会直接把位图交出来。
2. **应用程序绘制的光标，就是该应用程序自身帧画面中的普通像素**，所以它*确实*出现在 duplication 的图像里。这正是求解器（solver）存在的意义。

完整的证据连同原始数字在 [docs/measured-behaviour.zh-CN.md](docs/measured-behaviour.zh-CN.md)。专门针对全屏情形——包括为什么放大镜（Magnifier）思路行不通、以及哪个公开 API 可行——见 [docs/fullscreen-capture.zh-CN.md](docs/fullscreen-capture.zh-CN.md)。

---

## 构建

需要 .NET 9 SDK、MSVC 构建工具，以及用于 Native AOT 的 Windows SDK。不需要网络访问：`nuget.config` 清空了所有包源，而项目不引用任何包。

```powershell
dotnet build   RatTrap.sln -c Release
dotnet publish src\RatTrap\RatTrap.csproj -c Release -r win-x64 -o publish
```

产出单个约 2 MB 的 `publish\rattrap.exe`；当存在共享的个人代码签名证书时，它会被签名。构建环境里的坑、签名，以及管理员权限问题，都写在 [docs/development.zh-CN.md](docs/development.zh-CN.md) 里。

## 命令

| 命令 | 作用 |
| --- | --- |
| `rattrap gui` | 交互式窗口（不带参数时的默认行为）。 |
| `rattrap capture` | 获取一个光标并写出 `.cur`、`.png` 和 `.ani`。 |
| `rattrap probe` | 检查显示器、实时光标、Desktop Duplication 以及 interop 层。同时运行那个确立了上述行为的 GDI 与 DXGI 对比。 |
| `rattrap selftest` | 121 项确定性检查：interop 结构体大小与 vtable 槽位算术、用合成真值（synthetic ground truth）验证重建数学，以及 PNG/CUR/ICO/ANI 写出器。 |
| `rattrap help` | 选项说明。 |

## 窗口

| 控件 | 行为 |
| --- | --- |
| **Target: Cursor / Crosshair** | 一对单选按钮。`Cursor` 跟随指针。`Crosshair` 观察前台窗口中心的一个固定方块（默认 256 px）——对第一人称射击游戏而言，准星不动，动的是它背后的场景。 |
| **Start / Stop** | 单个按钮，它的标签说明了按下之后接下来会发生什么。 |
| **Open folder** | 在资源管理器中打开保存目录。 |
| **Crosshair width (px)** | 准星模式所观察方块的边长，32–1024，离开输入框时即保存。捕获进行中该值固定（累加器以该区域为键），因此运行中修改会在下次 Start 时生效。 |
| **Observe...** | 打开一个浮动的观察窗口：三格面板平排展示正在进行的重建，另有传输条可暂停、前后单步、跳到两端。该窗口由主窗口拥有，因此浮在它之上并随它一起关闭，同时仍可拖到另一个显示器。暂停只冻结**画面**，捕获仍在录制。 |
| **Global hot key** | 默认 `Ctrl+Insert`，可在 `rat-trap.ini` 中修改。在游戏持有焦点时依然有效，而这正是关键：按下它不会把焦点从游戏里抢走。 |
| Log | 每一条新日志被追加并滚动到可见位置。 |

`Self-test` 按钮仍然存在于窗口中，但被**有意隐藏**：它运行的是与 `rattrap selftest` 相同的检查，而后者会打印报告、也可以脚本化调用，所以从用户角度看它只是一个猜不出用途的控件。要恢复它，只需在其创建处去掉一个词；自检运行仍然会走到它背后的代码路径。

捕获会自动保存自己。这里没有导出按钮，因为一个已经获取、并且已经分离出 alpha 的光标就是完成态的工作，而要求用户再按三个按钮，就是三次弄丢它的机会。文件命名为 `[hotspotX,hotspotY]_NNNN`，所以重新运行绝不会覆盖先前的捕获；完成提示音使用的是系统的 "new fax notification" 事件（`FaxBeep`），因此它会遵循该机器所用的任何声音方案。

## 设置

`rat-trap.ini` 存放在**可执行文件旁边**，这样整个程序都是便携的，而且每个值都能用文本编辑器直接改。如果程序目录不可写——比如解包在 `Program Files` 下——它会回退到 `%LOCALAPPDATA%\rat-trap`，并在日志里说明这一点，而不是悄悄把改动丢掉。

```ini
[capture]
target = cursor            ; 或者 crosshair
savedirectory = captures   ; 相对路径基于程序目录解析
autosave = true
playsound = true
; 三段提示音，已编译进程序：开始捕获一段、停止捕获另一段、保存结果第三段。
; builtin 播放内嵌提示音；其它值会被当作 .wav 文件路径。
startsound = builtin
stopsound = builtin
savesound = builtin
reconstructseconds = 8
trim = false
nudgepointer = true

[crosshair]
crosshairsize = 256   ; 也可在窗口中"热键"那一行右侧直接编辑
crosshairneighbourdistance = 9

[hotkey]
hotkey = Ctrl+Insert
hotkeyenabled = true
```

## 验证结果

`tools/verify_cursor_output.py` 用 Python 依据各格式的规范重新推导 PNG、CUR 和 ANI 的布局，并把这三个文件互相对照检查。它与程序本身及其自检不共享任何代码，所以哪怕写出器和它自己的往返解析器之间犯了一个一致的错误，也依然会被暴露出来。

```powershell
python tools\verify_cursor_output.py out\
```

```
  ok   png chunk 1 (IDAT) CRC
  ok   cur XOR plane equals png RGBA byte for byte
  ok   cur AND mask marks exactly the transparent pixels
  ok   ani frame 0 is byte-identical to the .cur
45 checks, 0 failure(s)
```

注意，这台主机的光标*加载器（loader）*对任何 `.cur` 都不保像素（一个第一方对照文件读回来有 551/4096 字节发生变化，而且每个 `.cur` 都被重新缩放到 32×32），所以上面那条像素层面的证明是对文件字节做的，而不是把光标加载回来做的。

## 重建是如何工作的

简短版——对每一个光标局部坐标偏移，求解器拟合

```
D_c = (1 - alpha) * B_c + alpha * C_c
```

跨多次观测拟合，并且**三个颜色通道共用一个斜率（single slope）**，因此有闭式解（closed-form solution）。背景 `B` 来自两个地方之一：

* **当光标移动时，对最近若干帧构成的滑动窗口逐像素取中位数**。对任意给定像素而言，光标只在一部分时间里覆盖它，所以窗口中的多数仍是真正的背景。
* **对准星而言，取同一帧内该像素邻域的中位数**，因为准星从不移动。时间窗口会被合成值完全填满，从而把准星当成自己的背景；改用邻域采样则行得通，因为准星很细，隔几个像素之外就是普通场景。

完整的推导、哪些部分可以证明无法还原，以及那些被替换掉的失败方案，都在 [docs/algorithm.zh-CN.md](docs/algorithm.zh-CN.md) 中。

## 以管理员身份运行

**不需要，清单（manifest）也没有请求它。** 屏幕捕获读的是合成后的桌面，它属于会话而不是某个进程，所以一个未提权的程序一样能完好地读取提权窗口。提权也并不能击败 `WDA_EXCLUDEFROMCAPTURE` 或 DRM——没有任何东西能。关于提权会改变什么、不会改变什么，见 [docs/development.zh-CN.md](docs/development.zh-CN.md)。

## 仓库布局

```
src/RatTrap/
  Interop/        Win32 P/Invoke 与手写的 DXGI/D3D11 vtable 调用（AOT 下没有内置 COM）
  Capture/        DXGI Desktop Duplication、GDI BitBlt、显示器与 DPI 枚举
  Pointer/        GetIconInfo 读取器、DXGI 指针形状解码器、hotspot 锚点解析、指针轻推
  Reconstruct/    背景模型、充分统计量、alpha/XOR 求解器
  Imaging/        ArgbBitmap、CursorImage、1bpp 掩码打包
  Output/         PNG、CUR/ICO 和 ANI 写出器
  Settings/       便携式 INI 偏好设置
  Ui/             Win32 窗口以及它背后的捕获会话
  Cli/            命令行、probe 与 capture 流水线
  SelfTest/       确定性验证
  Audio/          三段反馈提示音，以内嵌 WAV 镜像形式存在
tools/            独立的 Python 验证器、图标生成器、提示音生成器、签名与发布脚本
docs/             算法、实测行为、全屏捕获、开发笔记
THIRD-PARTY-NOTICES.md   内嵌反馈提示音的 CC0 许可证与出处
probe/            本项目起步时所用的原始 C++ 探针，以及作为证据的文本日志
```

每份文档都有英文版 `NAME.md` 和中文版 `NAME.zh-CN.md`，包括根目录下的 `THIRD-PARTY-NOTICES.md`（记录内嵌音频的来源与许可证）。中文文档内部指向其他文档时，指的是该文档的中文版，因此交叉引用会带 `.zh-CN` 后缀；源码路径与 URL 一律不改写。

## 许可与预期用途

这是一个捕获工具。它以运行它的用户的权限，读取所在机器的屏幕，并写出图像文件。它不注入输入、不挂钩（hook）或修改其他进程、不隐藏自己、不做持久化，也不试图击败任何保护；遇到 DRM 保护或防捕获保护的内容时，它会直接失败，并说明原因。请只在你有权检查的机器上使用它。






