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

## 两个构建环境的坑

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
```
### 签名买到了什么，又没买到什么

它产出一个可验证、可发现篡改的二进制：`Get-AuthenticodeSignature` 会报告一个签名者，而任何修改都会让签名失效。它**不**买来信誉（reputation）。因为证书是自签名的，`Status` 会读到 `UnknownError`——换成中文说就是"证书链终止于一个不受信任的根"——在每一台没有把 `phenixl-codesign.cer` 装进"受信任的根证书颁发机构"和"受信任的发布者"的机器上都是如此。SmartScreen 对一个下载来的副本依然会发出警告。

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


