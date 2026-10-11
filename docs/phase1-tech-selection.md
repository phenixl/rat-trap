[**English**](phase1-tech-selection.md) | [中文](phase1-tech-selection.zh-CN.md)

# CursorTrap Phase 1: Technology Research and Selection Report

> Goal: capture only the cursor that the system **actually renders** (including custom themes, accessibility
> magnification, DPI scaling and animation), and output a cursor image that carries **alpha against a fully
> transparent background**, for consumers such as OBS.
>
> Every conclusion in this report is labelled with its evidence grade. **Inferences for which the primary
> documentation text was not obtained are always marked `[inferred]`**; measurements taken on this machine are
> marked `[measured]`, and are traceable to a specific probe and artefact file.

> **Superseded framing, recorded rather than edited away.** This report was written when the working assumption
> was that the result would be consumed by OBS Studio, either as an output fed into it or as a replacement for
> its cursor drawing. That direction has been dropped. **This project is standalone and will not become an OBS
> Studio plugin**, and the OBS-side defects this report documents - the 32x32 `GetIconInfo` size that makes the
> captured cursor the wrong scale, the double cursor caused by an already-composited pointer, and alpha being
> discarded by design on that side - are for OBS Studio to fix, not for this program to work around. They are
> kept here as the evidence that motivated building something standalone, and nothing else in this report has
> been rewritten: the measurements, the grades and the rejected approaches are as they were found.

## Evidence grades and sources

| Grade | Meaning | Source in this round |
|---|---|---|
| `[measured]` | Obtained by running probes on real hardware on this machine; reproducible | `probe/*.cpp` + `probe/out/*` (this repository) |
| `[SDK]` | Verbatim text from Windows SDK headers (this machine: `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0`) | `magnification.h`, `dxgi1_2.h` |
| `[archived]` | Original web pages/source code fetched and archived by a subagent through the local proxy; **I have checked the line numbers in the local archive myself** | `_research/pages/` (110 pages) |
| `[inferred]` | Primary text not obtained; based on prior knowledge and indirect evidence | Marked individually |

**Environment note (important)**: on this machine DNS hijacks every public domain to `198.18.0.0/15` (Clash TUN
fake-IP), so `web_fetch` refuses all external URLs, and the shell's TLS egress is unavailable as well. The
subagent completed the fetching through the local `127.0.0.1:7890` proxy plus Node's built-in TLS, and archived
it under `_research/pages/` (110 pages). My own `web_fetch` failed every single time, **so I have not cited the
primary text of any document that I could not check on this machine**.

**Test machine**: Windows 11 build 10.0.26100, primary display `\\.\DISPLAY1` 3440×1440 @ **120 DPI (125%)**,
secondary display `\\.\DISPLAY2` 1920×1080 @ 96 DPI, `HKCU\Control Panel\Cursors\CursorBaseSize = 80`,
`HKCU\Software\Microsoft\Accessibility\CursorSize = 4`.

---

## 0. The question that comes first: this repository already holds a mature implementation

`src/RatTrap/` is a pre-existing .NET 9 + Native AOT project (roughly 330 KB of C#, 22 files, zero NuGet
dependencies) that **already implements most of the architecture recommended in section 7 of this report**:

| Existing module | Role |
|---|---|
| `Capture/DxgiDuplicationSource.cs` | Desktop Duplication + `GetFramePointerShape`, with hand-written COM vtable dispatch |
| `Pointer/PointerShapeDecoder.cs` | Decoding of all three shapes: `COLOR` / `MONOCHROME` / `MASKED_COLOR` |
| `Reconstruct/CursorReconstructor.cs` + `Accumulators.cs` | Reconstructs alpha from multi-frame background samples (aimed at application-drawn cursors) |
| `Output/CursorFileWriter.cs`, `AniWriter.cs`, `PngWriter.cs` | Writes out `.cur` / `.ani` / `.png` |
| `Output/OutputSelfTest.cs` (69 KB), `SelfTest/CoreSelfTest.cs` | Large-scale self-test: write out, read back and compare |
| `Cli/ProbeCommand.cs` | Probe CLI (already covers PMv2, CursorBaseSize, `GetIconInfo`, `PointerPosition` calibration) |
| `docs/measured-behaviour.md` | Existing measured-behaviour documentation |

**How this report relates to it**: the conclusions I reached by independent measurement **agree item by item**
with `docs/measured-behaviour.md` (see the cross-validation table in Appendix B), and they add evidence that
document does not cover (the falsification of the magnifier, `LoadImage` scaling fidelity, concurrent
duplicators, the conflict in the DPI scaling rule, and alpha being destroyed on the OBS side). The
`docs/algorithm.md` and `ROADMAP.md` that `measured-behaviour.md` references **do not exist in the current
workspace** — they need to be restored, or the references corrected.

---

## 1. Can the Magnification API extract the real cursor? — **Conclusion: it cannot, and it should not be adopted** `[measured]`

### 1.1 The conclusion first

The magnifier API is a **rendering pipeline**, not a bitmap-extraction API. It does not offer — and does not
intend to offer — the ability to "hand you the current cursor bitmap". Across **4 independently designed
experiments** I **never once observed a magnified cursor**:

| Experiment | Design | Result |
|---|---|---|
| `mag_probe.cpp` | 7 filter modes × read-back method | Screen content visible, but **no cursor**; `MagShowSystemCursor(FALSE/TRUE)` both return 1 yet **do not change the magnified output** |
| `mag_probe2.cpp` | 3 side-by-side magnifiers differing only in style bits | A (no bits) solid colour, B (0x1) has content, C (0x3) solid colour → inconsistent and inexplicable |
| `mag_probe3.cpp` | No filter list, style-bit difference only, same-frame subtraction ×3 rounds | The difference is **identical in every round**, but it is only a 12×192 vertical strip (a dirty content region), **not the cursor** |
| `mag_probe4.cpp` | Single window, magnified output vs. direct screen grab of the same region, subtracted | The magnified output is a **solid-colour empty frame** (65536/65536 pixels = 240,240,240); nothing was rendered at all |

This must be labelled honestly: it is **"failed to reproduce", not "proved impossible"** — my failure may
include mistakes in my own usage. But the mechanistic reasons below show that it is unsuitable for an
engineering selection:

### 1.2 Mechanistic reasons (each one backed by measurement or by a header file)

1. **Incremental dirty-region rendering; a whole frame is not guaranteed** `[measured]`
   In `mag_probe4` the magnifier window content was solid colour, whereas the same usage in `mag_probe1` could
   display terminal text; `mag_probe3` redrew only a 12 px wide strip of change. → The magnifier redraws only
   "the region that has changed since last time", so it **cannot serve as a data source that yields one
   complete image per frame**.
2. **No API returns the cursor bitmap or its alpha** `[SDK]`
   The complete set of functions in `magnification.h`: `MagInitialize/MagUninitialize`, `MagSetWindowSource`,
   `MagGetWindowSource`, `MagSetWindowTransform`, `MagSetWindowFilterList`,
   `MagGetWindowFilterList`, `MagSetImageScalingCallback`, `MagSetColorEffect`,
   `MagSetFullscreenTransform*`, `MagSetInputTransform`, `MagShowSystemCursor`.
   **Not one of them returns a cursor image, alpha or position**; `MagGetWindowSource` returns the source
   rectangle.
3. **The output has no alpha** `[inferred, but it follows directly from 2]`
   The magnifier draws its content into a real HWND, so pixels can only be obtained by reading the screen
   (`BitBlt`/`PrintWindow`), and the alpha byte of `BitBlt` is always 0. The user's requirement of "the cursor
   only, with a transparent background" cannot be met by it.
   (Measured: `PrintWindow(PW_RENDERFULLCONTENT)` against the magnifier is **incomplete/stale**, hence
   unreliable `[measured]`)
4. **`MagSetImageScalingCallback` is not a per-frame callback** `[measured]`
   The first experiment fired it 7 times and the second 0 times, and **in both cases it fired synchronously
   inside the `MagSetWindowSource`/`MagSetWindowFilterList` calls**; it fired 0 times during `Pump(900ms)`.
   Its `dest` data is the **magnified screen content and does not contain the cursor**
   (see `probe/out/cb_A_nofilter_1.png`). The SDK header still declares the function `[SDK]`, but it is
   unusable as a frame source.
5. **`MS_*` are window styles, not filter modes, and the values collide** `[SDK]`
   The comment on lines 42–45 of `magnification.h` is `// Magnifier Window Styles`:
   `MS_SHOWMAGNIFIEDCURSOR 0x0001` / `MS_CLIPAROUNDCURSOR 0x0002` / `MS_INVERTCOLORS 0x0004`;
   while lines 49–50 give `MW_FILTERMODE_EXCLUDE 0` / `MW_FILTERMODE_INCLUDE 1`.
   → **`MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR` is numerically equal to `MW_FILTERMODE_INCLUDE`**.
   `MagGetWindowFilterList` reads that value back verbatim, so the read-back **cannot distinguish** the two
   semantics; the measured behaviour is that of INCLUDE (only the windows in the list are magnified). This is
   a real API trap.
6. **Multiple windows interfere with each other** `[measured]` Within a single process only a narrow strip was
   rendered for 3 magnifier windows; the process-wide context behind the magnifier's `MagInitialize` is not
   suited to multiple instances.

### 1.3 Alternative path (see section 7 for details)

The real cursor should be taken from the **compositor**, not from the magnifier:
`IDXGIOutputDuplication::GetFramePointerShape` is the only API on this machine that can obtain a cursor bitmap
with "true size + true alpha + correct hotspot" (sections 5 and 7).

---

## 2. The complete flow and the limits of the `GetCursorInfo` + `CopyIcon` + `GetIconInfo` approach

### 2.1 The complete flow (every step backed by measurement)

```c
// 1) Read the current cursor (do not DestroyCursor this handle; it belongs to the system/window)
CURSORINFO ci{ sizeof(ci) };
GetCursorInfo(&ci);                       // ci.flags is the authoritative source for "is it showing"
if (!(ci.flags & CURSOR_SHOWING)) { /* cursor hidden/suppressed; do not draw it this frame */ }

// 2) Copy a handle that we own, to avoid lifetime problems with the one shared with the system
HCURSOR owned = CopyIcon(ci.hCursor);

// 3) Get the bitmaps and the hotspot (hbmMask/hbmColor are the caller's to DeleteObject)
ICONINFO ii{};
GetIconInfo(owned, &ii);
// ii.xHotspot / ii.yHotspot / ii.hbmColor / ii.hbmMask

// 4) Read pixels: hbmColor is a DDB (bmBits == NULL), so GetDIBits is mandatory
//    [measured] hbmColor: 32x32 bpp=32 planes=1 bmBits=null(DDB) bmWidthBytes=128
//    hbmMask : 32x32 bpp=1

// 5) Clean up
DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask); DestroyCursor(owned);
```

### 2.2 Key limitations (all measured on this machine)

| Limitation | Measured evidence | Consequence |
|---|---|---|
| **Always 32×32, regardless of `CursorBaseSize`** | With `CursorBaseSize=80`, `GetIconInfo` still returns `32x32` (`probe_run1.txt`, two samples; `probe_dxgi2` confirms it again) | **This is the root cause of the wrong cursor size in OBS** |
| The alpha channel is not clean | `nonzero=105/1024 full255=0 min=0 max=254` | It cannot be used directly as a trustworthy alpha source |
| `hbmColor` is a DDB, `bmBits==NULL` | `GetBitmap` output | `GetDIBits` is mandatory; a direct pointer cannot be used |
| A monochrome cursor has `hbmColor == NULL` | Not hit on this machine, but this is how `ICONINFO` is defined `[SDK]` | An AND/XOR → alpha composition branch must be implemented |
| The hotspot does not scale with magnification | See below | The overlay will be positioned slightly off |
| **The hotspot changes with the load size** | `aero_link.cur`: the 32 px entry in the file has hotspot=(6,0), but the 32×32 the system provides has hotspot **(7,0)**; `LoadImage(cx=80)` yields **(16,0)** | The hotspot must come from the API; it cannot be hard-coded from the file |

### 2.3 Cross-validation

The community evidence archived by the subagent agrees with my own measurements: the Magpie maintainer says —
quoted here from a Chinese rendering of the statement, because the original English text was not retrieved —
that the system cursor image obtained through GetIconInfo is always 32x32; and in OBS issue #2604 WizardCM
says "it will always receive a 32x32 icon for compatibility reasons" `[archived]`. The existing
`docs/measured-behaviour.md` independently records the same fact and goes on to point out that the alpha of
that 32×32 is not a clean 0/255 mask. **Three independent sources agree**.

---

## 3. How to obtain "how large the current cursor should be" accurately

Three independent sources, in decreasing order of trustworthiness:

### 3.1 Source A (most trustworthy): ask the compositor directly `[measured]`

`GetFramePointerShape` gives **the size the system is actually rendering**. Measured on this machine's
secondary display (96 DPI, `CursorBaseSize=80`):

```
type=2 (COLOR)  89x87  pitch=356  hotspot=(3,3)  bytes=30972
alpha: 0=6622  255=40  intermediate=1081     ← true alpha, including the semi-transparent shadow
```

Here "the cursor bitmap itself" is about 80×80, and `89×87` is the **bounding box including the shadow**. This
agrees exactly with `CursorBaseSize = 80` at 100% scaling.

### 3.2 Source B (fallback): an explicit size with `LoadImage` `[measured, new in this round]`

`LoadImage(path, IMAGE_CURSOR, cx, cy, LR_LOADFROMFILE)` **can produce any requested size**, and the hotspot
is scaled proportionally. For `aero_arrow.cur` (which contains **five real images: 128/96/64/48/32**):

| cxDesired | Bitmap | Hotspot |
|---|---|---|
| 0 (= not specified, take the largest) | 128×128 | (0,0) |
| 32 | 32×32 | (0,0) |
| 64 | 64×64 | (0,0) |
| **80** | **80×80** | (0,0) |
| 96 | 96×96 | (0,0) |
| 256 | 256×256 | (0,0) |

The corresponding hotspots for `aero_link.cur`: 32→(7,0), 64→(12,0), **80→(16,0)**, 96→(19,0), 128→(25,0)
(note that **none of them is the original hotspot of the same-size entry in the file**, which shows that the
system internally resamples from the large image and converts the hotspot).

### 3.3 **The most important new evidence of this round: the system magnifies the cursor by resampling a high-resolution frame, not by stretching the 32 px one** `[measured]`

Performing a numerical comparison against the same real cursor (the DXGI 89×87 shape), and searching for the
best crop offset:

| Candidate rendering | MAE (lower is more alike) | Best offset |
|---|---|---|
| **`LoadImage(path, 80, 80)` (the system's own scaler)** | **12.47** | **(3,3)** |
| Nearest-neighbour upscale of the 32×32 bitmap in the .cur | 28.92 | (3,2) |
| Bilinear (alpha-weighted) upscale of the 32×32 bitmap in the .cur | 27.73 | (3,2) |

The best offset `(3,3)` **is exactly the `HotSpot=(3,3)` that DXGI reports**, which makes the geometry
self-consistent. Conclusion: **"upscale the 32×32 from `GetIconInfo` to the target size" is the wrong fidelity
strategy** (this is precisely why OBS displays it incorrectly); the right approach is **to resample from the
`.cur` file at the target size** (`LoadImage(cx=target)` is the system's own scaler), or better still to adopt
the DXGI shape directly.

### 3.4 Registry and size rules

- The slider mapping for `CursorBaseSize`: `Accessibility\CursorSize = 4` ↔ `CursorBaseSize = 80` `[measured]`
  → `CursorBaseSize = 32 + (step-1) × 16`, step 1..15, default 32, maximum 256.
  This agrees with the wxWidgets source comment "a multiple of 16 between 32 and 256" and with Magpie's
  "15 levels, up to 256×256" `[archived]`.
- **`CursorBaseSize` has no official documentation and no SPI** `[archived]` (`WinUser.h` has only
  `SPI_SETCURSORS 0x0057`).

### 3.5 ⚠️ Unresolved conflict: is the size multiplied by `dpi/96` on top of that? — **This is the only hard conflict in this phase**

- The documentation-side evidence supports `rendered = CursorBaseSize × (dpi/96)`: Raymond Chen's nominal DPI
  table is exactly `32 × dpi/96`; the wxWidgets source applies `FromDIP` to the registry value; mptrsz measured
  72×72 at 150% DPI with step 2 `[archived]`.
- But on this machine's primary display (**120 DPI = 125%**) I measured a cursor bitmap of about **80 px**, not
  100 px `[measured]`. This round `probe_dxgi2` proved that **PMv2 awareness changes `GetDpiForSystem` from 96
  to 120 and `SM_CXSCREEN` from 2752 to 3440**, which shows that my earliest batch of probes was DPI-unaware
  and had all of its coordinates/sizes virtualized — **which would explain the conflict exactly** (a
  DPI-unaware process may receive virtualized shape dimensions).

**Decisive experiment (3 minutes, needs the user's cooperation)**: move the mouse to the **primary display**,
then measure `GetFramePointerShape`'s `Width` once in (a) a DPI-unaware process and once in (b) a PMv2-aware
process:

- If (a)=80 and (b)=100 → the rule is `CursorBaseSize × dpi/96`, and **the application must declare PMv2**;
- If (a)=(b)=80 → `CursorBaseSize` is itself in physical pixels and is not multiplied by DPI.

There is also an engineering way to **sidestep the question**: **do not compute the size at runtime**, and take
the size that `GetFramePointerShape` reports as ground truth instead; only when that API is unavailable should
`CursorBaseSize × dpi/96` be used, with a manual override offered in the UI. The existing
`docs/measured-behaviour.md` does not cover this question.

---

## 4. Parsing and playing animated cursors (.ani)

### 4.1 The format as measured (real system files on this machine)

```
FILE C:\Windows\Cursors\aero_busy.ani   RIFF/ACON size=556304
  chunk 'anih' len=36  at 12
    ANIH: cbSize=36 cFrames=18 cSteps=18 cx=0 cy=0
          cBitCount=32 cPlanes=1 jifRate=3 flags=0x1 (AF_ICON)
    => default frame duration = 3 jiffies = 50.0 ms
  chunk 'LIST' len=556240
    LIST type='fram'; 18 'icon' sub-chunks, each len=30894
```
`[measured]`; `aero_working.ani` likewise has 18 frames. Key points:

- The structure is `RIFF .... ACON`, and the chunks are `anih` (a 36-byte header), optional `rate` (jiffies per
  frame), optional `seq ` (frame sequence), and `LIST`/`fram`/`icon` (each frame is a **complete CUR/ICO
  file**).
- Frame duration = `jifRate` (or the per-frame value in the `rate` chunk) × 1/60 second.
- **Neither .ani on this machine has a `rate` or a `seq ` chunk**, so the parser must fall back to
  `anih.jifRate`, and the frame sequence is 0..cFrames-1. These two chunks exist elsewhere, so the parser must
  support them.
- `[archived]` The subagent scanned all 189 `.cur`/`.ani` files in `C:\Windows\Cursors`: every Windows 11 Aero
  cursor contains **five real frames (32/48/64/96/128)**, and **the entries are PNG-compressed**
  (`wBitCount = 0`) → **do not write your own ICONDIR→DIB parser; leave the decoding to GDI**.

### 4.2 Playback plan: **do not rely on the system to play it automatically** `[measured]`

```
C:\WINDOWS\cursors\aero_busy.ani    : 25 samples (every 60 ms) frameChanges=0
C:\WINDOWS\cursors\aero_working.ani : 25 samples (every 60 ms) frameChanges=0
```

`GetIconInfo` returns a **static frame** for an animated cursor (repeated sampling of the same HCURSOR yields a
completely unchanged fingerprint). Therefore **you must parse the .ani yourself, build an HCURSOR per frame
yourself, and play them on your own timeline**:

1. Parse `anih`/`rate`/`seq `/`fram`;
2. Build one cursor per frame with `LoadImage(cx=target size)` or `CreateIconFromResourceEx`;
3. Flip frames on a timeline of `60 jiffies per second` (`jifRate=3` → 50 ms/frame, 20 fps);
4. Read the hotspot **frame by frame** (it may differ between frames); do not take only the first frame.

For a target size of `CursorBaseSize × dpi`, prefer resampling every frame with `LoadImage(cx=target)`; if the
frame is an embedded CUR resource, use `CreateIconFromResourceEx` with an explicit size.

---

## 5. Transparent rendering and output: `UpdateLayeredWindow` vs DirectComposition vs Spout/NDI

### 5.1 Window styles (the requirement is already clear)

```
WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE   (+ WS_EX_TOPMOST, controlled by SetWindowPos)
```
If the DirectComposition route is taken, then **do not** use `WS_EX_LAYERED`; use
**`WS_EX_NOREDIRECTIONBITMAP`** instead (`0x00200000L`, documentation: "The window does not render to a
redirection surface") `[archived]`.

### 5.2 The trade-off between the two rendering routes

| | `UpdateLayeredWindow` | DirectComposition + D3D11 |
|---|---|---|
| alpha | Exact, but requires **premultiplied alpha** (`AC_SRC_ALPHA`) | Exact, `DXGI_ALPHA_MODE_PREMULTIPLIED` |
| Per-frame cost | **Forces a GPU→CPU→GPU round trip plus CPU/GPU synchronisation**; the `HDC` can only be backed by a GDI bitmap, so **it cannot accept a GPU texture** | Zero-copy: `IDCompositionVisual::SetContent` **is documented to accept an `IDXGISwapChain1`** |
| Interfacing with a Spout shared texture | Requires `CopyResource`→staging→`Map`→CPU memcpy→premultiply→`CreateDIBSection`, i.e. two redundant copies per frame | The same texture can be handed to Spout directly / used directly as the swapchain back buffer |
| Recommendation | Only as a degraded/minimal verification path | **Recommended mainline** |

`[archived]` Basis: Kenny Kerr's article on layered windows explains that ULW must send the bitmap back to
system memory before DWM puts it on the GPU; the `SetContent` documentation lists `IDXGISwapChain1` as
acceptable. `CreateSwapChainForComposition` requires `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` +
`DXGI_SCALING_STRETCH`, and `Commit()` should be called only when the tree structure changes.

### 5.3 ⚠️ The user's original premise needs correcting: **OBS's Window/Display Capture cannot obtain alpha** `[archived, checked against the local archive]`

- **"Allow Transparency" exists only in Game Capture**: in the archived OBS source,
  `game-capture.c:41` has `#define SETTING_TRANSPARENCY "allow_transparency"`; Window Capture and Display
  Capture **have no such setting at all**.
- The shared rendering path `dc-capture.c:216-243` uses `obs_get_base_effect(OBS_EFFECT_OPAQUE)` together with
  `gs_enable_blending(false)`; `duplicator-monitor-capture.c:643` likewise calls
  `gs_enable_blending(false)`. **alpha is discarded by design.**
- The OBS 28.0.0 release notes also state that they fixed "transparent windows being captured incorrectly in
  Window Capture".
- Conclusion: **"make a transparent click-through window and have OBS window-capture it" does not work**. The
  only viable paths are: ① build your own sender → a **Spout2 source** or an **NDI source**;
  ② modify or add an OBS plugin (`plugins/win-capture/cursor-capture.c` is that "fake cursor" implementation,
  GPLv2).

### 5.4 Spout2 vs NDI (output aimed at OBS)

| | Spout2 | NDI |
|---|---|---|
| Licence | **BSD 2-Clause** (no runtime dependencies, Windows-only) | Proprietary SDK; **commercial distribution requires a vendor ID**; the runtime must be installed by the user separately |
| alpha | Supports BGRA shared textures, but **Spout's own documentation does not state the alpha semantics (premultiplied/straight) explicitly — `[unverified]`, so a bench test must come first** | The documentation explicitly supports alpha, but **the convention is the opposite: NDI is straight alpha while the Windows side is premultiplied → it must be un-premultiplied before it leaves** |
| OBS integration | `obs-spout2-plugin` (Off-World-Live, **GPL v2**, 64-bit) | OBS NDI output/plugin |
| Conclusion | **First choice** (no runtime dependencies, permissive licence) | Second choice (cross-machine, cross-platform scenarios) |

### 5.5 Findings on "existing open-source projects" `[archived]`

**I did not find** any open-source project that simultaneously achieves "correct scaling + correct alpha + the
real system cursor + delivery into OBS". The closest ones: `eugenesvk/mptrsz` (MIT, diagnostically useful),
`cjcxj/windows-cursor-streamer` (**no LICENSE, useful for its approach only**), `Blinue/Magpie` (which switched
to drawing the cursor with a shader because `DrawIconEx` cannot interact with D3D, and does not support
animated cursors), and `obs-studio/plugins/win-capture/cursor-capture.c` (the very code that is to be
replaced).
→ **This really is a genuine gap.**

---

## 6. Strategy for handling DPI across multiple displays

### 6.1 Iron rule: the process must declare Per-Monitor-V2 `[measured]`

On the same machine, in the same run, before and after `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`:

```
BEFORE: GetDpiForSystem=96   SM_CXSCREEN=2752  SM_CYSCREEN=1152   output[0] rect=(0,0)-(2752,1152)  dpi=96
AFTER : GetDpiForSystem=120  SM_CXSCREEN=3440  SM_CYSCREEN=1440   output[0] rect=(0,0)-(3440,1440)  dpi=120
```

- The primary display really is **3440×1440 @ 120 DPI**; when DPI-unaware, the entire coordinate space is
  virtualized to 2752×1152.
- `GetDpiForMonitor` returns 96 for a DPI-unaware process (not the real DPI) — which makes "compute the cursor
  size from the DPI" necessarily wrong on any unaware code path.
- It must be called **before any HWND is created** (the existing project already achieves this through a
  manifest; `SetProcessDpiAwarenessContext` returning FALSE/err 0 means "the manifest has already taken
  effect", which **is success, not failure** `[archived]`).

### 6.2 Geometry model (measured on this machine and self-consistent)

```
hotspot_on_screen_desktop = output.DesktopCoordinates.origin
                          + DXGI_OUTDUPL_FRAME_INFO.PointerPosition.Position
                          + DXGI_OUTDUPL_POINTER_SHAPE_INFO.HotSpot
```

- `PointerPosition.Position` is **the top-left corner of the shape buffer**, not the hotspot `[measured]`.
  Measured on the secondary display: `(360,533) + (3,3) = (363,536)` = `GetCursorPos(1133,1976) − secondary
  origin(770,1440)`, **exactly equal**. §5 of the existing `measured-behaviour.md` reaches the same conclusion
  independently from another data set.
- **Do not mix sources**: in another measurement, `PointerPosition + HotSpot` differed from `GetCursorPos` by
  39 px (y), because the mouse moved between the two samples. Position and shape must be taken from **the same
  frame** `[measured]`.
- `PointerPosition.Visible` must be checked: true means the compositor **delivers the cursor separately** (we
  must draw it ourselves); false means the cursor **is already burned into the desktop texture** (do not draw
  it again) — this is exactly what causes OBS's double cursor `[archived]`.

### 6.3 Differences between displays

- Take each output's DPI with `MonitorFromPoint(GetCursorPos())` + `GetDpiForMonitor(_, MDT_EFFECTIVE_DPI)`.
- The shape size/hotspot varies with the display the cursor is on, so **the shape must be re-fetched whenever
  the cursor moves across displays**.
- Moving the cursor across displays produces a shape-change event (on this machine `LastMouseUpdateTime` and
  `PointerShapeBufferSize > 0`) → this is the only refresh trigger; while static, `AcquireNextFrame` keeps
  returning `WAIT_TIMEOUT` `[measured, the existing documentation reaches the same conclusion in §6]`.

---

## 7. Recommended technology stack and architecture

### 7.1 Data source priority (two channels, each a fallback for the other)

| Priority | Source | When available | Fidelity |
|---|---|---|---|
| **P0** | `IDXGIOutputDuplication::GetFramePointerShape` | D3D11 present + not exclusively held (**multiple processes can coexist in practice**, see 7.4) | **Highest**: true size, true alpha, includes the shadow, true hotspot |
| **P1** | `GetCursorInfo` for position + `LoadImage(cx=target size)` resampling along the registry `.cur/.ani` path | DXGI unavailable (Remote Desktop / exclusive fullscreen / unsupported driver) | High: MAE 12.47 against the system scaler (section 3.3), but **application-drawn cursors cannot be obtained** |
| **P2** | The existing `CursorReconstructor` (accumulating multiple background frames to solve for alpha) | Application-drawn cursor in an exclusive-fullscreen game | Depends on having enough background samples |
| Disabled | Magnification API | — | Falsified in section 1 |

### 7.2 Module breakdown

```
CursorTrap
├─ Platform
│   ├─ DpiAwareness        declares PMv2 as the process starts (the single entry point, so no declaration is missed)
│   ├─ MonitorTopology     EnumDisplayMonitors + GetDpiForMonitor, maintains an id→(rect,dpi) map
│   └─ CursorSettings      registry CursorBaseSize/Accessibility.CursorSize + per-role .cur/.ani paths
├─ Capture
│   ├─ ICursorSource       unified interface: NextFrame() -> {BGRA pixels, hotspot, anchorScreenPt, visible, sourceKind}
│   ├─ DuplicationCursorSource   P0: DuplicateOutput + AcquireNextFrame + GetFramePointerShape
│   ├─ ThemedCursorSource        P1: GetCursorInfo + LoadImage(cx=size) + .ani timeline playback
│   └─ ReconstructingSource      P2: the existing CursorReconstructor
├─ Decode
│   ├─ PointerShapeDecoder  COLOR (premultiplied BGRA)/MONOCHROME (AND+XOR)/MASKED_COLOR
│   └─ AniParser            anih/rate/seq/fram + frame timing + per-frame hotspot
├─ Present
│   ├─ OverlayWindow        WS_EX_NOREDIRECTIONBITMAP|TRANSPARENT|TOOLWINDOW|NOACTIVATE
│   ├─ DcompPresenter       D3D11 + CreateSwapChainForComposition(PREMULTIPLIED) + SetContent + Commit (structure changes only)
│   └─ LayeredFallback      UpdateLayeredWindow (degraded path)
└─ Output
    ├─ SpoutSender          P0 output (BSD-2)
    ├─ NdiSender            P1 output (mind the un-premultiply)
    └─ FileSink             .cur/.ani/.png (already present)
```

### 7.3 Key API call sequence

```
① first line of main(): SetProcessDpiAwarenessContext(PMv2)     ← must come before any HWND
② CursorSettings reads the registry → target size candidate = CursorBaseSize (DPI rule in 3.5, overridable at runtime by P0)
③ D3D11CreateDevice → EnumOutputs → pick the output containing GetCursorPos()
   → IDXGIOutput1::DuplicateOutput                    ← on failure, degrade to P1
④ main loop (16 ms):
     AcquireNextFrame(1ms)
       ├─ WAIT_TIMEOUT → reuse the previous frame's shape (mark IsCached), update position only
       ├─ PointerShapeBufferSize > 0 → GetFramePointerShape → PointerShapeDecoder → premultiplied BGRA
       └─ PointerPosition.Visible == 0 → the cursor is already burned into the desktop; do not draw this frame
     position = output.origin + PointerPosition + shape.HotSpot      (self-consistent within the same frame)
⑤ Present: present only when dirty; swapchain alpha = PREMULTIPLIED
⑥ Output: the same texture → Spout (premultiplied) / NDI (un-premultiply first)
```

### 7.4 Two architecture-level measured conclusions (new in this round)

1. **Desktop Duplication supports multiple coexisting processes** `[measured]`
   While process A held a duplication, process B's `DuplicateOutput` **returned success with `hr=0x0000`**.
   → CursorTrap can run at the same time as OBS's Display Capture.
   (This machine is Windows 11 26100; 2 clients have been verified, while more clients and OBS itself have not
   yet been measured.)
   This fills the gap that §9 of the existing `measured-behaviour.md` explicitly records.
2. **Permissions**: none of the paths above (including Desktop Duplication, LoadImage and the DComp overlay
   window) **requires administrator rights** `[measured, all probes ran under a normal user token]`.

---

## 8. Checklist still to be verified (ordered by risk)

| # | Question | Impact | Verification method | Status |
|---|---|---|---|---|
| 1 | Whether the size is `CursorBaseSize × dpi/96` | The overlay cursor will be 25% too large/too small | Measure `shape.Width` once each on the primary display with PMv2-aware vs unaware | **Unresolved (the only hard conflict)** |
| 2 | Semantics of the `MASKED_COLOR` bitmap | Some cursors (such as the I-beam) may be drawn as a black square | Measured on this machine: `Type=4`, 80×80, **alpha all 255**, RGB is "a white I-beam on a black background". Need to determine whether the black background is a key or a mask; do a composition comparison with a known cursor | Unresolved |
| 3 | Whether Spout2 passes alpha end to end, and whether it is premultiplied or straight | The Spout output may fail entirely | Send a BGRA gradient → read it back with an OBS Spout source | Unresolved (the subagent flagged this as well) |
| 4 | Whether the shadow is always inside the shape buffer | Determines whether cropping is needed and whether the shadow is doubled | Sample multiple cursor types × multiple DPIs | **Leaning "yes"**: 89×87 > 80×80, and a soft shadow is visible in the image |
| 5 | Source of the residual in the MAE of 12.47 in section 3.3 | Affects the fidelity ceiling of P1 | Re-compare after un-premultiplying; separate the shadow region | Unresolved |
| 6 | Application-drawn cursors (exclusive-fullscreen games) | Applicability of the P2 path | Use the existing self-test's real-window approach | The existing project already has the mechanism; real game samples are missing |
| 7 | HDR / DRM / rotated outputs | Degraded behaviour | Disable/construct each one in turn | Not covered (same as the existing documentation) |

---

## Appendix A: Probes and artefacts from this round (reproducible)

| Probe | Purpose | Key artefacts |
|---|---|---|
| `probe/cursor_probe.cpp` | Cursor bitmap/size/registry/theme files/ANI/DrawIconEx alpha | `probe/out/probe_run1.txt`, `live_direct_color.png`, `file_arrow_plain_color.png`, `draw_live_80.png` |
| `probe/mag_probe.cpp` | Magnifier: 7 filter modes × read-back | `mag_run1.txt`, `mag_*_screen.png`, `cb_A_nofilter_1.png` |
| `probe/mag_probe2.cpp` | `MS_*` as window styles, for comparison | `mag_run2.txt`, `mag2_full.png` |
| `probe/mag_probe3.cpp` | No filter, style bits only, same-frame subtraction + cursor fingerprint identification | `mag_run3.txt`, `patch_B_x31_y0.png`, `id_live_32.png` |
| `probe/mag_probe4.cpp` | Magnified output vs. direct screen grab, subtracted | `mag_run4.txt`, `p4_magOut0.png`, `p4_srcActual.png` |
| `probe/probe_sizes.cpp` | `LoadImage` with 12 explicit sizes + ANI static-frame verification | `sizes_run.txt`, `sz_*.bgra` |
| `probe/probe_dxgi.cpp` | First test of the DXGI pointer shape | `dxgi_run.txt`, `dxgi_shape_*.bgra` |
| `probe/probe_dxgi2.cpp` | Before/after PMv2 comparison + concurrent duplicator | `dxgi2_pmv2.txt`, `dxgi2_second.txt` |
| `probe/analyze_cursor.py` | MAE decision between the real shape and candidate renderings | The table in section 3.3 of this report |
| `probe/bgra2png.py` | Native BGRA → PNG (standard library only) | The PNGs listed above |

Reproduce: `pwsh -File probe/build.ps1 -Source probe/<x>.cpp -Libs @(...)`, then run the exe.
(`vcvars64.bat` is unusable on this machine because vswhere errors out, so `build.ps1` injects
`INCLUDE/LIB/PATH` directly.)

## Appendix B: Cross-validation against the existing `docs/measured-behaviour.md`

| Conclusion | My independent measurement | Existing documentation | Agreement |
|---|---|---|---|
| `GetIconInfo` is always 32×32 (with `CursorBaseSize=80`) | ✅ two samples | ✅ §4 | Agrees |
| DXGI shape `type=2 89x87 pitch=356 hotspot=(3,3)` | ✅ | ✅ §2 | **Agrees exactly** |
| alpha histogram `opaque=40 transparent=6622 partial=1081` | ✅ | ✅ §2 | **Agrees exactly** |
| `PointerPosition` = top-left of the shape rather than the hotspot | ✅ secondary display `(360,533)+(3,3)=(363,536)` | ✅ §5 (another data set) | Agrees |
| `AcquireNextFrame` returns `WAIT_TIMEOUT` on a static desktop | ✅ | ✅ §6 | Agrees |
| GDI `BitBlt` likewise contains no cursor | Not tested | ✅ §3 (correction) | Accept the existing documentation |
| Concurrent duplicators | ✅ **2 coexisting processes succeeded** | ❌ §9 marks it "not verified" | **New in this round** |
| Cursor magnification uses high-resolution frame resampling | ✅ MAE 12.47 vs 27.7/28.9 | Not covered | **New in this round** |
| The magnifier is not viable | ✅ falsified by 4 experiments | Not covered | **New in this round** |
| alpha is discarded by design on the OBS side | Line numbers checked against the archive | Not covered | **New in this round** |

## Appendix C: Pitfalls hit and confirmed this round (to steer around during implementation)

1. `MW_FILTERMODE_EXCLUDE | MS_SHOWMAGNIFIEDCURSOR == MW_FILTERMODE_INCLUDE` (value collision).
2. `MS_*` are window styles (`magnification.h:42`), not filter modes.
3. On this machine `vcvars64.bat` aborts because its internal vswhere call fails → inject `INCLUDE/LIB/PATH`
   directly.
4. Under the restricted sandbox, **capturing a native exe's output through a PowerShell pipe** gives
   `Access denied`; use `cmd /c "exe > file"` instead.
5. Under the restricted sandbox, **a Node/native process whose output is captured through a pipe by a cmdlet**
   fails the same way (independently reproduced by the subagent).
6. Reading pixels from a DDB requires `GetDIBits` (the `hbmColor` that `GetIconInfo` returns is a DDB with
   `bmBits==NULL`).
7. The alpha byte of a DIB obtained by screen capture is always 0, so PNG conversion must force opacity or the
   whole image ends up transparent.
