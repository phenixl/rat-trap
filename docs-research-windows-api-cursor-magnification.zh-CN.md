[English](docs-research-windows-api-cursor-magnification.md) | **中文**

# Windows API 文档调研 —— 放大镜（Magnification）／光标 API／ANI 格式

**交付状态：受阻（BLOCKED）—— 未能取回任何一个权威页面。**
本报告中的任何引文都不是引文。每一条“原文引用”条目都标注为
`NOT RETRIEVABLE`。没有任何内容被改写成转述后当作文档使用。

---

## A. 环境阻塞（为什么没有任何引文）

| 事实 | 证据 |
| --- | --- |
| 所有 DNS 应答都是 fake-IP（`198.18.0.0/15`），包括凭空编造的主机名 | `nonexistent-zzz-12345.example -> 198.18.1.89`；`learn.microsoft.com -> 198.18.1.86`；`raw.githubusercontent.com -> 198.18.0.239`。解析器是 Clash for Windows（PID 21452）的 TUN fake-IP 模式，它同样会应答发往 `8.8.8.8` 的查询。 |
| 从 shell 出发的出站 HTTPS/TLS 不可能成功 | `curl.exe` → `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS (0x8009030e)`；.NET `SslStream.AuthenticateAsClient` → `Authentication failed, see inner exception`。直连与经由代理 `127.0.0.1:7890` 都是如此；尝试过的主机：`1.1.1.1`、`github.com`、`learn.microsoft.com`。 |
| 纯 HTTP 出站**确实**可用（因此该阻塞是 TLS 特有的） | `GET http://learn.microsoft.com/...` 返回 `301`，带 `Server: AkamaiGHost`、`Location: https://learn.microsoft.com/...`、`Akamai-Cache-Status: Redirect from child` —— 真实的 Akamai 边缘节点，重定向到 HTTPS。 |
| `web_fetch` 拒绝每一个外部主机 | `Error: URL hostname "learn.microsoft.com" resolves to a non-public IP address`（对 `example.com`、`r.jina.ai`、`cdn.jsdelivr.net`、`raw.githubusercontent.com` 同样如此）。 |
| `web_search` 只返回 URL 列表，没有页面正文或摘要片段 | 两次查询返回 16 条结果，全是标题加 URL，零条被引用的文档文本。 |

结论：本次会话中不存在能拿到页面正文的可用路径。这与工具文档针对受限沙箱模式所描述的
TLS/schannel 边界是同一条；按照指示，在确认该边界之后没有再换别的花招去重试。

---

## B. 抓取过的 URL

### B.1 实际抓取过的 URL —— 全部失败，零字节内容

| # | URL | 工具 | 结果 |
| --- | --- | --- | --- |
| 1 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist | web_fetch | ERROR non-public IP |
| 2 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowwindow | web_fetch | ERROR non-public IP |
| 3 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowcursor | web_fetch | ERROR non-public IP |
| 4 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetsystemcursor | web_fetch | ERROR non-public IP |
| 5 | https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md | web_fetch | ERROR non-public IP |
| 6 | https://example.com/ | web_fetch | ERROR non-public IP |
| 7 | https://r.jina.ai/https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist | web_fetch | ERROR non-public IP |
| 8 | https://cdn.jsdelivr.net/gh/MicrosoftDocs/win32@docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md | web_fetch | ERROR non-public IP |
| 9 | (shell) `GET http://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist` | Invoke-WebRequest/HttpWebRequest | 301 AkamaiGHost → 无正文 |
| 10 | (shell) `GET https://example.com/`、`https://learn.microsoft.com/...`（外加经由 `-x http://127.0.0.1:7890`、`socks5://127.0.0.1:7890`） | curl.exe / .NET | TLS 失败（见 A） |
| 11 | (shell) `Test-Tls` 到 `1.1.1.1`/`cloudflare-dns.com`、`140.82.121.4`/`github.com`、`198.18.1.86`/`learn.microsoft.com` | SslStream | TLS 失败（见 A） |

### B.2 由 web_search 浮现出的候选 URL（未抓取 —— 从未看到其内容）

这些是一旦出站可用就应当去取回的权威目标。把它们列出来并不构成第 C 节中任何陈述为真的证据。

放大镜 API（Magnification API）：
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowsystemcursor
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetimagescalingcallback
- https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-magnifier-styles
- https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/entry-magapi-functions
- https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/entry-magapi-functions.md
- https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md
- https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/magnification/nf-magnification-magsetimagescalingcallback.md

---

## C. 被请求的条目 —— 原始调查结果

格式：**论断** → **原文引用** → **URL**。

### C.1 放大镜 API（`Magnification.dll`、`magnification.h`）

| # | 请求查证的论断 | 原文引用 | URL |
| --- | --- | --- | --- |
| 1 | `MagSetWindowFilterList` 的签名 | NOT RETRIEVABLE | (B.2 #1) |
| 2 | `MW_FILTERMODE_EXCLUDE` / `MW_FILTERMODE_INCLUDE` 的 `dwFilterMode` 语义 | NOT RETRIEVABLE | (B.2 #1) |
| 3 | 哪个函数接受 `MS_SHOWMAGNIFIEDCURSOR`，以及它的文本 | NOT RETRIEVABLE | (B.2 #4) |
| 4 | `MagShowSystemCursor` 的准确文档文本 | NOT RETRIEVABLE | (B.2 #2) |
| 5 | `MagSetWindowSource` 的准确文档文本 | NOT RETRIEVABLE | not located |
| 6 | `MagGetWindowSource` 的准确文档文本 | NOT RETRIEVABLE | not located |
| 7 | 放大镜是渲染进一个真实 HWND，还是暴露一张位图 | NOT RETRIEVABLE | (B.2 #5, #6) |
| 8 | `MagSetImageScalingCallback` 是否已弃用／移除？从哪个版本起 | NOT RETRIEVABLE | (B.2 #3) |
| 9 | 是否存在任何返回光标位图／alpha／位置的放大镜 API | NOT RETRIEVABLE —— 无法读取完整的函数清单，因此“不存在”这一结论**未**被确立 | (B.2 #5, #6) |
| 10 | `MagInitialize` / `MagUninitialize` 的要求说明 | NOT RETRIEVABLE | not located |
| 11 | 关于放大镜窗口必须是真实可见窗口的陈述 | NOT RETRIEVABLE | (B.2 #6) |

### C.2 光标 API（Cursor APIs）

| # | 请求查证的论断 | 原文引用 | URL |
| --- | --- | --- | --- |
| 12 | `GetCursorInfo` + `CURSORINFO` 的准确字段 | NOT RETRIEVABLE | not located |
| 13 | `CURSORINFO.hCursor` 的归属／共享说明（是否要销毁） | NOT RETRIEVABLE | not located |
| 14 | `GetIconInfo` + `ICONINFO` 的字段（`hbmMask`、`hbmColor`、`xHotspot`、`yHotspot`） | NOT RETRIEVABLE | not located |
| 15 | 关于单色光标 `hbmColor == NULL` 的说明 | NOT RETRIEVABLE | not located |
| 16 | 由哪张位图携带 alpha 通道 | NOT RETRIEVABLE | not located |
| 17 | `CopyIcon` 与 `CopyImage` 的文档文本及其差异 | NOT RETRIEVABLE | not located |
| 18 | `GetIconInfoEx` 的签名与用途 | NOT RETRIEVABLE | not located |
| 19 | `CreateIconFromResourceEx` 的签名与用途 | NOT RETRIEVABLE | not located |
| 20 | `LookupIconIdFromDirectoryEx` 的签名与用途 | NOT RETRIEVABLE | not located |
| 21 | `GetSystemMetrics(SM_CXCURSOR / SM_CYCURSOR)` 的文档含义 | NOT RETRIEVABLE | not located |
| 22 | `DrawIconEx` + `DI_NORMAL` 的 alpha 行为 | NOT RETRIEVABLE | not located |
| 23 | 动画光标：绘制的是哪一帧 | NOT RETRIEVABLE | not located |

### C.3 尺寸、辅助功能指针大小、`.ani` 格式

| # | 请求查证的论断 | 原文引用 | URL |
| --- | --- | --- | --- |
| 24 | 渲染出的系统光标尺寸与辅助功能指针大小设置的关系 | NOT RETRIEVABLE | not located |
| 25 | `GetIconInfo` 返回的是缩放后还是未缩放的位图 | NOT RETRIEVABLE | not located |
| 26 | `.ani` 的 RIFF/`ANIH`/`ACON` 格式：`ANIH` 字段 | NOT RETRIEVABLE | not located |
| 27 | `.ani` 的标志位（`AF_ICON`、`AF_SEQUENCE`、`AF_DISPLAY`） | NOT RETRIEVABLE | not located |
| 28 | `rate ` 块的语义 | NOT RETRIEVABLE | not located |
| 29 | `seq ` 块的语义 | NOT RETRIEVABLE | not located |

**重要区分：** Microsoft 把 `.ani` 容器格式记载为一种 RIFF／Windows 媒体格式，而不是
Learn 的“win32/api”参考页，所以正确的目标与 B.2 节不同。此类页面既没有被定位到，也没有被抓取过。

---

## D. 什么属于 NOT DOCUMENTED，什么属于 NOT RETRIEVABLE

- **NOT DOCUMENTED（未记载）**＝ 已读过权威页面，而该页面没有陈述该条目。
  本报告**不含任何** NOT DOCUMENTED 判定，因为没有任何页面被读过。
- **NOT RETRIEVABLE（无法取回）**＝ 在本环境中取回失败；该条目的文档状况未知。
  全部 29 个条目都是 NOT RETRIEVABLE。

不要把上表中任何一行当作支持或反对“存在一个有文档的放大镜光标位图 API”的证据。

---

## E. 给上级（parent）的下一步建议

在一个 shell 的 HTTPS 出站可用、或者抓取工具不被限制为公网 IP 的环境中，用 B.2 节作为精确的
目标列表，重跑同一组抓取。放大镜参考页的批量正文也可以在 `MicrosoftDocs/sdk-api` 与
`MicrosoftDocs/win32` 这两个 Git 仓库中逐字获得
（`sdk-api-src/content/magnification/*.md`、
`desktop-src/WinAuto/magapi/*.md`），如果能够抓取原始文件，这是获得准确引文最省事的单一来源。
