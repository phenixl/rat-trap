[English](development.md) | **中文**

# 构建、签名，以及这台机器上会坑到你的那些事

写给下一个构建它的人，也包括未来的我自己。这里的每一条都花过时间才被发现。

## 构建

```powershell
dotnet build   RatTrap.sln -c Release
dotnet publish src\RatTrap\RatTrap.csproj -c Release -r win-x64 -o publish
```

产出 `publish\rattrap.exe`，一个自包含的 Native AOT 二进制，约 2 MB。目标机器上无需安装任何东西。

*构建*机器的要求：.NET 9 SDK、MSVC 构建工具，以及一个 Windows SDK。Native AOT 需要 MSVC 链接器；用 `vswhere` 能否找到 `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` 是最快的检查方式。

## 零包引用，以及为什么这不是故作姿态

`nuget.config` 有意清空了所有包源。在开发本项目的这台机器上，到 `nuget.org` 的 TLS 出网不通，所以一次会访问网络的 restore 会先卡住、然后失败。清空所有源之后，restore 完全从 `%USERPROFILE%\.nuget\packages` 解析，而 SDK 自带的 Native AOT 包本来就放在那里。

在添加一个依赖之前，这些后果值得了解：

* 每个 Win32 调用都用 `[LibraryImport]`，绝不用带运行时封送（runtime marshalling）的 `[DllImport]`。
* DXGI 和 D3D11 用手写的 COM vtable 调用，因为 AOT 下没有内置的 COM interop（见 `docs/algorithm.zh-CN.md` 以及 `src/RatTrap/Interop/DxgiInterop.cs` 顶部的审计表）。
* 不用 `System.Text.Json`；设置是一个手工解析的 INI 文件。
* 不用 WinForms、不用 WPF，它们与 AOT 不兼容。GUI 是原生 Win32。

如果哪天确实需要某个依赖，先往 `nuget.config` 里加回一个包源，并且要做好心理准备：那个包的 AOT 兼容性将成为你的问题。

## 构建环境的坑

这里刻意不编号：随着发现的坑越来越多，那个数字总是会过期——而这正是本文档存在的意义的一个小缩影。

**正在运行的实例会占住链接器输出。** 如果本仓库里的某个 `rattrap.exe` 还在运行，下一次构建会失败并报：

```
LINK : fatal error LNK1104: cannot open file "bin\Release\net9.0-windows\win-x64\native\rattrap.exe"
```

这条消息指向一个没人有意使用的文件，读起来像权限问题或磁盘问题，但两者都不是。Windows 会锁定已映射的可执行映像，所以一个从 `bin\...\native\` 启动的实例正占着原生链接器要写的那个文件。同一个实例还占着全局热键，导致新实例无法注册它。`tools/publish.ps1` 会在构建前检查这一点，并直接报出 pid 与路径；加 `-StopRunning` 可让它清理掉。

**命名管道。** `dotnet build` 可能失败，报 `MSB3883` 和从
`Microsoft.CSharp.Core.targets` 抛出的 `Access is denied`。这不是文件权限问题。MSBuild 的编译器任务和 Roslyn 服务器都通过*命名管道*捕获子进程输出，而一个受限沙箱可能禁止命名管道，却允许文件写入。`Directory.Build.props` 设置了 `UseSharedCompilation=false`，让编译留在同进程内进行，这解决了常见情形；如果仍然失败，构建就需要一个限制更少的环境，因为改任何文件权限都无济于事。

**管道传递 `dotnet` 输出。** 在同类受限环境中，用管道传递 `dotnet` 的输出
（`dotnet build ... | Select-String ...`）可能报访问被拒绝错误，而不用管道的写法却正常。请不带管道地运行 `dotnet`，或者重定向到文件。

**管道方式启动 GUI 进程。** PowerShell 不会等待一个 GUI 子系统进程，所以
`.\rattrap.exe capture ...` 在工作完成之前就返回了，而 `$LASTEXITCODE` 是空的。请用
`Start-Process -PassThru` 加 `Wait-Process -Id`，或者用 `cmd /c start /b` 然后轮询程序自己输出的文件。在这台主机上，对一个会创建窗口的进程，`Start-Process -Wait` 本身就会挂住，这就是为什么 GUI 带有 `--selfcheck --selfcheck-out <file>`，也是为什么自动化检查应当去读那份报告，而不是依赖等待。

**`%TEMP%` 可能不可写。** 在受限环境中，子进程对 `%TEMP%` 的写入可能被拒绝，而程序目录是可写的。因此 `OutputSelfTest` 会先探测 `%TEMP%`，再探测程序目录，然后是当前目录，并报告它用了哪一个。

## 代码签名

证书存放在仓库**之外**，位于这台机器上所有项目共享的一个文件夹里。该文件夹由用户环境变量 `certs_path` 指定，本仓库中**没有任何地方把它写死**。

```powershell
[Environment]::GetEnvironmentVariable('certs_path', 'User')   # 证书文件夹在哪里
```

```
<certs_path 指向的文件夹>\
    phenixl-codesign.pfx              私钥，受密码保护
    phenixl-codesign.pfx.password.txt 密码，因为构建代理需要无人值守地使用它
    phenixl-codesign.cer              公开部分，用于安装为受信任
    phenixl-codesign.thumbprint.txt   构建时读取它，用来在证书存储中找到证书
```

> 这个路径原先是项目文件里的一个字面量。文件夹被移动之后，字面量就失效了，而后果**不是**一个报错：签名静默降级为跳过，唯一的迹象是构建日志里的一行字。环境变量只有一处需要修改，也不会与机器上的实际情况脱节。若 `certs_path` 未设置，构建现在会明确说明，而不是悄悄产出一个未签名的二进制。

`tools/sign.ps1` 为二进制签名，优先按指纹（thumbprint）从证书存储取证书（不需要密码），回退到 `.pfx` 加其密码——构建代理用的就是后者。它按以下顺序解析证书文件夹：`-CertDir`、进程环境里的 `certs_path`、**注册表用户作用域里的 `certs_path`**。最后一步比看上去更重要：在某个 shell 启动**之后**创建的环境变量，不会出现在那个 shell 里，所以别人已经开着的终端或 IDE 是拿不到它的——而从那里发起的构建仍然必须能签名。

因此 `publish` 目标**总是**调用该脚本（除非 `SignOutput=false`），由脚本自己判断，而不是让 MSBuild 去判断证书是否存在。MSBuild 只能看见它自身进程启动时已存在的环境变量，所以它无法可靠地回答这个问题，而答错的后果是静默的。证书缺失会在构建日志里留下一行**可见的** `SKIPPED`：

```powershell
dotnet publish ... -p:SignOutput=false             # 从不签名
dotnet publish ... -p:RatTrapSigningDir=D:\certs    # 指定文件夹，优先于 certs_path
```

签名是尽力而为：证书缺失只产生那一行，而不是让构建失败。当一次发布绝不允许以未签名状态出厂时，请改为给 `tools/sign.ps1` 传 `-Require`——更省事的做法是用 `tools/publish.ps1`，它会把签名从发布出来的文件上读回来验证。

### 签名挂钩为什么挂在 `CopyNativeBinary` 上

把签名目标挂在 `AfterTargets="Publish"` 上是无效的，而且是**静默**失效：构建日志写着 `Successfully signed`，但发布出来的二进制上根本没有签名。

原因在 ILCompiler 包里，`Microsoft.NETCore.Native.Publish.targets`：

```xml
<Target Name="CopyNativeBinary" AfterTargets="Publish">
  <Delete Files="$(PublishDir)\$(TargetName)$(NativeBinaryExt)" />
  <Copy SourceFiles="$(NativeOutputPath)$(TargetName)$(NativeBinaryExt)" DestinationFolder="$(PublishDir)" />
</Target>
```

这个目标**同样**是 `Publish` 的 `AfterTarget`，而 NuGet 的 targets 在项目自身的 targets 之后导入，所以它会在项目挂到 `Publish` 上的任何目标**之后**运行：先把已签名的二进制删掉，再把未签名的原生产物复制回去。MSBuild 的 `Copy` 会保留源文件的时间戳，因此文件时间戳连动都不动，唯一的迹象就是"签名不见了"。这个坑花了一个下午才定位，因为当时每一个可观察的信号都在指向相反的结论。

`CopyNativeBinary` 是最后一个写入者，所以挂钩应该挂在这里。又因为日志不足以作为证据，`tools/publish.ps1` 会先发布、再签名，然后**从发布出来的文件上把签名读回来验证**，最后跑一次该二进制自己的自测，全部通过才报告成功：

```powershell
pwsh tools\publish.ps1                  # 发布闸门：构建、签名、验证、自测
pwsh tools\publish.ps1 -RequireSignature # 未签名时报错而不是警告
pwsh tools\publish.ps1 -StopRunning      # 清掉会占住链接器的运行中实例
```

### 签名买到了什么，又没买到什么

它产出一个可验证、可发现篡改的二进制。在当前这版发布上已实测：`Status` 读到 `Valid`，因为这台机器上 `phenixl-codesign.cer` 已装入 `Cert:\CurrentUser\Root` 与 `Cert:\CurrentUser\TrustedPublisher`；而在发布副本上翻转**一个字节**就会让状态变成 `HashMismatch`——所以这个签名不是装饰。

它**不**买来的是信誉（reputation），而且这种信任是逐机器、逐用户的。在没有安装该 `.cer` 的机器上，同一个二进制会报 `UnknownError`，即"证书链终止于一个不受信任的根"。在那里签名依然可验证、篡改依然会被发现，只是证书链不受信任而已。SmartScreen 对一个下载来的副本也依然会发出警告。

注意已安装的作用域：用户级根与用户级受信任发布者，而**不是** `LocalMachine`。对本用户身份下运行的一切来说这已经足够，而且不需要提权；若要全机器安装，或让以其它账户运行的构建代理使用，则需把 `.cer` 装进 `LocalMachine` 存储。

私钥才是有价值的部分。它按设计可跨项目复用，而且绝不能提交进仓库：`.gitignore` 出于这个原因排除了 `*.pfx`、`*.pvk`、`*.snk`、`*.cer` 和 `*.key`。该文件夹的 ACL 被限制为仅当前用户可访问。

## 以管理员身份运行

**它并非必需，本项目也不要求它。** 屏幕捕获读取的是合成后的桌面，它属于窗口站（window station）和会话，而不是某个进程，所以一个未提权的程序一样能完好读取提权程序的像素。两个后端在这台机器上都是在未提权状态下验证过的。

提权会改变的东西，仅限于本程序并不做的事情：向提权窗口发送消息、向其中注入，或者读取它们的内存。值得注意的是，它对那些真正让全屏游戏在捕获中消失的情形**没有**帮助：

* `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`——有意为之、有文档记载，且与权限无关。
* DRM 保护的输出——同理。
* 绕过桌面合成的独占全屏交换链（swapchain）。
* HDR，或者捕获 API 不包含的硬件覆盖层（hardware overlay）平面。

因此项目清单保持 `asInvoker`。强制 `requireAdministrator` 会在每次启动时弹出 UAC 提示、破坏从资源管理器拖放，而且对上面这些情形一个都解决不了。如果一次构建要分发到那些政策是"不提权就别用"的机器上，那是部署决策，不是本程序自身的属性。

## 生成图标

`src/RatTrap/app.ico` 是生成的，不是手绘的：

```powershell
python tools\make_icon.py src\RatTrap\app.ico
```

从 16 到 256 共九种尺寸，4 倍超采样，小尺寸用 32bpp DIB，大尺寸用 PNG 载荷。脚本会断言每种尺寸都确实画出了东西、留下了一些透明度，并且有一个实心的箭头主体，这样一个画坏的多边形就不可能悄悄出厂。

## 重新生成反馈提示音

三段提示音以 base64 WAV 镜像的形式存放在 `src/RatTrap/Audio/SoundCues.g.cs`，而该文件是提交进仓库的，所以**日常构建既不需要这个生成器，也不需要源音频**。

```powershell
# 资源包地址与许可证见 THIRD-PARTY-NOTICES.md
python tools\build_sounds.py --pack <解压后的资源包> --analyse            # 查看候选与其实测数据
python tools\build_sounds.py --pack <解压后的资源包> --out src\RatTrap\Audio\SoundCues.g.cs
python tools\build_sounds.py --verify src\RatTrap\Audio\SoundCues.g.cs    # 校验产物本身，而不是校验意图
```

`--verify` 不是走过场。这个生成器的第一版真正做了抽取，却仍然声明源采样率，导致每一段提示音都高一个八度、快一倍播放，而它唯一打印出来的证据是一个与错误采样率自洽的时长。现在它会用独立的 WAV 读取器把产出的字节读回来并断言格式；`rattrap selftest` 在 C# 里做同样的测量，所以载荷被截断时，程序自己的测试也会失败。

需要 `soundfile`（`pip install soundfile`），它会带上 libsndfile 与 numpy。仅构建期需要：构建或运行 rat-trap 都不依赖它。
## 验证一次改动

把下面这些全部跑一遍；每一类问题都各有其能被某一条抓住、而其他条抓不住的地方。

```powershell
dotnet build RatTrap.sln -c Release
.\publish\rattrap.exe selftest                 # 84 项确定性检查，不涉及硬件
.\publish\rattrap.exe capture --out out\       # 真实的端到端捕获
python tools\verify_cursor_output.py out\      # 对各文件格式的独立重新实现
.\publish\rattrap.exe gui --selfcheck --selfcheck-ms 8000 --selfcheck-out gui.txt
```

`tools/verify_cursor_output.py` 是必须保留的那个。它与程序本身、以及程序的自检都不共享代码，所以一个在写出器和它自己的往返解析器之间一致的 bug 依然会被暴露出来。正是它确立了：`.cur` 的 XOR 平面与 PNG 的 RGBA 逐字节相等。



## 观察视图的代价

以下均在本机实测，取自 GUI 自检——因为这些数字决定了设计，而且在事前并不显然：

| 区域 | 一次刷新（含 `Solve()`） | 背景面板重建 |
| --- | --- | --- |
| 128 x 128 | 0.34 ms | 0.29 ms |
| 256 x 256 | 2.67 ms | 0.29 ms |

在 100 ms 的捕获节拍下，256 px 区域占用那条同时驱动捕获的线程不到 3%。这正是观察视图**跟着捕获节拍刷新**、而不是另起一个显示定时器的原因：定时器会把一个需要数十帧才收敛的过程显示成被采样、被混叠的样子，而省下的那点开销并不值得。

背景面板既是最贵的一块，又是变化最慢的一块，所以它每 4 次刷新才重建一次——上表第二列即由此而来：9 次刷新只产生了 3 次重建。

### 录制的算术

环形缓冲受三重上限约束，因为任何单独一重都是错的：块数决定缩放倍率，字节预算决定容量，帧数上限让回放保持可导航。

| 区域 | 缩放 | 块数 | 每帧 | 容量 | 合计 |
| --- | --- | --- | --- | --- | --- |
| 160 x 160（光标） | 1/2 | 80 x 80 | 44.8 KB | 300 | 13.4 MB |
| 256 x 256（准星） | 1/2 | 128 x 128 | 114.7 KB | 256 | 32.0 MB |
| 1024 x 1024（准星上限） | 1/8 | 128 x 128 | 114.7 KB | 256 | 32.0 MB |

最后两行才是重点：字节预算是**录制本身的属性，而不是区域的属性**，所以大十六倍的捕获占用同样的内存，代价付在分辨率上。每块 8 字节承载合成 RGB、背景 RGB、分类与 alpha。

录制的每一帧都不做分配；环是在推导出的容量上一次性定尺的定长数组。一个在每次捕获节拍都分配的录制器，等于在它正在观察的那条路径上制造垃圾。

基于这些数字，把上限做成可配置是合理的下一步，已列入 `ROADMAP.md` 而不在此处实现，这样默认值就保持为实测过的那一组。

## 复现"受写保护的安装"，以及回退路径里的陷阱

这两点都是通过真正复现"没有管理员令牌时打开文件夹出错"得到的，都值得留下，因为两者的第一次尝试都错得**看起来像程序缺陷**。

**对目录拒绝写入，也会让里面的程序无法启动。** 构造不可写安装的显然做法是 `icacls <目录> /deny "<用户>:(OI)(CI)(W)"`。它确实让目录不可写，同时也让目录里的可执行文件以 `Access is denied` 启动失败——`Start-Process` 与 `cmd /c start` 都一样。原因是 `(W)` 别名包含 `SYNCHRONIZE`，而从映像创建进程需要它。只对目录本身拒绝 `(WD,AD)` 才是想要的效果：创建子项被拒、写文件被拒，而程序仍能运行。在这条上浪费了两次失败的复现尝试，之后才去认真读错误信息，而不是把责任推给程序。

**回退路径本身绝不能抛异常。** 程序目录不可写时，设置文件会回退到 `%LOCALAPPDATA%\rat-trap`。那个回退写在 `catch` 块里，并对回退根目录调用 `Directory.CreateDirectory` 而没有自己的保护，于是在**连每用户位置也不可用**的机器上——被锁定的或重定向的配置文件，或被沙箱限制的子进程（本问题正是这样发现的）——程序打印 `FATAL: UnauthorizedAccessException ...` 并在任何窗口出现之前退出。那个**唯一目的就是优雅降级**的分支，恰恰是崩溃的那一个。现在它会报告"设置无处可写"并继续运行，因为"不保存偏好设置"是个糟糕的结果，而"拒绝启动"是更糟的。

第二只 bug 也解释了为什么存盘目录必须一并修：它只相对程序目录解析，完全无视回退，于是同一次未提权运行会宣称一个它创建不了的保存路径、打开该文件夹被拒、随后保存捕获也会失败。现在两者共用同一个具名的 `FallbackRoot`，相对 `savedirectory` 跟随它，绝对路径按原样遵守。

## 当"目录存在"不等于"目录可用"

被报告了两次：先是"打开文件夹没有反应"，修好之后是"不是管理员模式，打开文件夹仍然弹出错框"。第二次才是关键，因为那个框**不是本程序弹的**：是 Explorer 拒绝打开一个本程序刚刚判定为可用的目录。

当时的检查测的是"能否创建"，而 `Directory.CreateDirectory` 对**已经存在**的目录一律返回成功，**不管它属于谁**。先前以管理员身份运行时留下的目录，属主是 Administrators，当前这个普通账户无法访问，于是检查通过、该目录被报告为保存位置，而 Explorer 随后用自己弹出的错误框拒绝它——Windows 在抱怨一个本程序刚说没问题的目录。复现它**完全不需要提权**：新建一个目录，对这个账户拒绝 (R,W)，然后确认 `CreateDirectory` 依然愉快地返回、列举会抛 `UnauthorizedAccessException`、写入也会抛。

现在的检查是它本该有的样子：写一个文件、列一次目录、删掉那个文件。这正是保存与打开真正需要的三件事，而"存在"一件都不是。这也堵掉了最后一条会静默丢失捕获的路径，因为同一个探针在启动时就会运行。

顺带，旁边那个启动调用不再用 `ShellExecute`。它是这条路径上唯一能自己拉起系统 UI 的调用，而一个本程序无法描述其内容的对话框，就是它无法解释的对话框。`explorer.exe` 现在用 `CreateProcessW` 加绝对路径启动：没有动词查找、没有关联解析、没有 PATH 搜索，并且失败时返回一个可报告的 Win32 错误码，而不是一个需要猜的 shell 码。该调用依赖的两个结构体在自检里按尺寸断言（x64 上 104 和 24 字节），因为短了几个字节的结构体会读错字段，而且看起来仍然能用。

## 离线还原下工具链可能坏掉，然后什么都构建不了

值得记录，因为它有几分钟看起来像代码缺陷。本仓库完全没有 NuGet 源——`nuget.config` 清空了它们，还原只从 `%USERPROFILE%\.nuget\packages` 解析。当本机 SDK 更新到 9.0.319 后，它开始要求 `Microsoft.DotNet.ILCompiler`、`Microsoft.NET.ILLink.Tasks` 和 9.0.21 的运行时包，而本地缓存只有 9.0.20。没有源可达，还原就以 NU1100 失败——并且之后每次构建都继续失败，因为那次失败的还原把错误写进了 `obj/project.assets.json`，而每次构建都会重放它，`--no-restore` 也不例外。几分钟前还通过的闸门，卡在了 `dotnet publish`。源码没有任何改动。

这里用的解套方式值得一提：去掉运行时标识符、去掉 AOT、去掉裁剪/AOT 分析器来构建程序集，就完全不需要那些包，并直接跑在已安装的运行时上：

```
dotnet build src\RatTrap\RatTrap.csproj -c Release -p:RuntimeIdentifier= -p:SelfContained=false \
  -p:PublishAot=false -p:IsAotCompatible=false -p:EnableAotAnalyzer=false \
  -p:EnableTrimAnalyzer=false -p:EnableSingleFileAnalyzer=false -p:IsTrimmable=false -p:PublishTrimmed=false
```

这会产出一个框架依赖的 `rattrap.dll`，它的 `selftest` 能正常运行，足以验证行为。但它**不是发布产物**：单文件原生 exe 及其签名仍然需要真正的发布，所以处于这种状态的机器需要一次联网还原（或把 9.0.21 的包放进缓存），才可能产出发布。

真正管用的修复方式，而且**不碰已提交的配置**——清空源的目的就是可复现的离线还原，为单次调用临时加一个源并不破坏这一点：

```
dotnet restore src\RatTrap\RatTrap.csproj -s https://api.nuget.org/v3/index.json
```

`-s` 只替换**那一次调用**的源列表，所以 `nuget.config` 保持原样，之后的离线构建行为不变。在本机通过 `127.0.0.1:7890` 的本地代理用了 22 秒，之后普通的 `dotnet build` 与 `dotnet publish` 就完全不需要源、直接从缓存解析成功。走代理时，务必在执行还原的那个 shell 里设置 `HTTP_PROXY` 与 `HTTPS_PROXY`，否则会超时，而错误信息会暗示"网络不通"而不是"没有源"。

## 发布门禁绝不能在还没编出新包之前就销毁旧包

这一条弄丢过一个发布包。`tools/publish.ps1` 先删掉输出目录、再往里发布，于是当还原失败时，操作者手上**一个包都不剩**，而不是保住原有的那个——门禁让局面比不跑它更糟。而且什么都找不回来：`publish/` 被 gitignore，删掉的产物从未进过仓库。

现在它先发布到**暂存目录**，并把替换作为**最后一步**——在可执行文件存在、签名已被读回、且发布出的程序通过自身自检之后。任何环节失败都会让输出目录保持原样、丢弃暂存目录，并明确说它这么做了。替换过程中旧包是被**挪到一边**而不是删除，所以即使替换中途失败也可恢复。

同一个错误的另一半值得单独说：**发布目录同时也是用户数据所在的地方。** 默认存盘目录是相对可执行文件的 `captures/`，设置文件 `rat-trap.ini` 也在那里，所以"替换发布包"曾经等于"删掉用户捕获到的光标"。两者现在都会随替换被**带过去**，因为它们都不是构建产物、也没有任何东西能重新生成它们。目录里其他东西仍按构建产物对待，这是诚实的规则：构建只产出 `rattrap.exe` 与 `rattrap.pdb`，这两个名字看一眼全新发布就能核对。

两个方向都验证过。注入一个刻意的编译错误后，脚本以 1 退出，旧包及其文件仍在，且没有留下暂存或备份目录。在 `publish/captures/` 里放一个带方括号的捕获文件 `[12,34]_0001.png` 后真实运行一次，日志报告它同时保留了该文件与 `rat-trap.ini`，事后两者都在且内容未变。
