# 第三方声明

[English](THIRD-PARTY-NOTICES.md) | **中文**

rat-trap **不含任何第三方代码**。它包含**三段音频提示音**，本文件记录它们的来源与所做处理——因为这是整个仓库中唯一并非自产的内容。

## Kenney Interface Sounds

| | |
| --- | --- |
| **作品** | Interface Sounds (1.0) |
| **作者** | Kenney — <https://kenney.nl> |
| **资源包页面** | <https://kenney.nl/assets/interface-sounds> |
| **下载地址** | `https://kenney.nl/media/pages/assets/interface-sounds/fa43c1dd4d-1677589452/kenney_interface-sounds.zip` |
| **获取日期** | 2026-10-10 |
| **许可证** | Creative Commons Zero (CC0 1.0) — <https://creativecommons.org/publicdomain/zero/1.0/> |
| **使用文件** | `open_002.ogg`、`close_002.ogg`、`confirmation_001.ogg` |
| **是否修改** | 是——见下文 |

### 随包分发的许可证原文

许可证正文保持英文原样，不作翻译：许可证的法律效力以其原始文本为准，转译可能引入歧义。

```
Interface Sounds (1.0)

Created/distributed by Kenney (www.kenney.nl)
Creation date: 11-02-2020

License: (Creative Commons Zero, CC0)
http://creativecommons.org/publicdomain/zero/1.0/

This content is free to use in personal, educational and commercial projects.

Support us by crediting Kenney or www.kenney.nl (this is not mandatory)
```

CC0 将该作品置于公有领域：**不要求署名**，其条款也不限制商业使用。上面给出署名是因为这样做是对的，而不是因为许可证要求；同一署名也写进了生成出来的源文件，以便它随该文件一起流转。

### 对每个文件做了什么

变换由 `tools/build_sounds.py` 执行，结果由 `tools/build_sounds.py --verify` 测量。原始 `.ogg` 文件与整个资源包**都不在本仓库中**；仓库里只有这三段生成的 WAV 镜像，以 base64 内嵌在 `src/RatTrap/Audio/SoundCues.g.cs`。

| 提示音 | 来源 | 处理 |
| --- | --- | --- |
| 开始 | `open_002.ogg` | 裁掉静音、按 ×1.10 重采样（升高音高、变短）、22050 Hz 单声道 16 位、RMS 响度对齐、首尾 4 ms 淡入淡出 |
| 停止 | `close_002.ogg` | 裁掉静音、按 ×0.91 重采样（降低音高、变长）、22050 Hz 单声道 16 位、RMS 响度对齐、首尾 4 ms 淡入淡出 |
| 保存 | `confirmation_001.ogg` | 裁掉静音、不变调、22050 Hz 单声道 16 位、RMS 响度对齐、首尾 4 ms 淡入淡出 |

处理结果是 CC0 原作的衍生作品，因此同样不受限制。

### 为什么选这三个，以及为什么不用系统音效

这个功能的第一版是引用 Windows 声音方案里的事件。它被替换掉了，理由值得留下，因为它并不显然：

* 系统里表示"开始"和"结束"的两个事件是 `DeviceConnect` 与 `DeviceDisconnect`，它们是**硬件插入与硬件移除**事件。用它们播报"开始捕获"，听起来就是插上了一个 USB 设备——这是**主动误导**。
* 方案别名可能不存在、可能被用户改映射，也可能让多个事件指向同一个文件。在开发这台机器上，`FaxBeep`、`MailBeep` 和 `Notification.Mail` 全部指向同一个 WAV，所以"三个互不相同的提示音"并不是别名机制能够保证的事。
* 从一个要被分发的程序里去引用 Windows 自带的媒体文件，是做不到的事。这里的音频来源是 CC0，所以结果可以分发。

在这个资源包里，`open_*` 与 `close_*` 就是同一个声音的正放与倒放——实测时长、RMS 与频谱质心三位有效数字完全相同，只有音高扫向相反。一对"开始/结束"共用同一音色、仅方向相反，正是开始/停止提示音应有的样子，因此两者都取了第 2 号变体。保存音之所以选 `confirmation_001`，是因为它够暗：频谱质心 692 Hz，而那对是约 5.4 kHz，所以即使在低音量下或用笔记本扬声器，也不可能把它与那两者混淆。
