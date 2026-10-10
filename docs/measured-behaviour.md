# Measured behaviour of cursor capture on this machine

This file records what was **measured**, not what is documented or assumed. Every claim
below is backed by an artefact produced by `rattrap probe` or by the earlier C++ probes in
`probe/`. Where a documented behaviour was assumed and turned out to be wrong, that is
called out explicitly, because those corrections are the most valuable content here.

Machine: Windows, primary `\\.\DISPLAY1` 3440x1440 @ 120 DPI, secondary `\\.\DISPLAY2`
1920x1080 @ 96 DPI, `CursorBaseSize = 80`, accessibility `CursorSize = 4`.

---

## 1. Desktop Duplication works, and is the only viable fullscreen path

`rattrap probe`, section 4:

```
OK: image 1920x1080, rotation 1, desktop image in system memory: False
readback path: staging texture (CopySubresourceRegion)
CreateTexture2D  hr=0x00000000 texture=0x1E4010EB120
Map(staging, D3D11_MAP_READ) hr=0x00000000 pData=0x1E443276000 rowPitch=256 depthPitch=16384
```

* The duplication session opens on the monitor under the pointer and delivers frames.
* `DXGI_OUTDUPL_DESC.DesktopImageInSystemMemory` is **FALSE** on this machine, so
  `MapDesktopSurface` is not the path taken; the pixels come from a small staging texture
  filled by `CopySubresourceRegion`, which transfers only the requested rectangle.
* `GetDesc` reports `Rotation = 1` (identity), so no rotation transform is needed here.

## 2. The duplication image does NOT contain the cursor

This is by design and is the single most important fact for the architecture: the
compositor hands the cursor over separately via `GetFramePointerShape`.

Measured pointer shape:

```
type=2 89x87 pitch=356 hotspot=(3,3) bytes=30972
alpha histogram: opaque=40 transparent=6622 partial=1081
alpha bbox = (3,2) 36x51
```

Decoded and rendered, that shape is the familiar white arrow with a soft drop shadow — a
semi-transparent cursor, which is why only 40 pixels are fully opaque.

## 3. CORRECTION: GDI `BitBlt` also does NOT contain the cursor

This was assumed to be true, and it is false on this machine. Capturing the same 128x128
region with both APIs at effectively the same moment:

```
background (DXGI) status = Captured
composite  (GDI)  status = Captured
differing pixels (>8) = 0 of 16384 (0.00%), mean |delta| = 0.00
```

Zero differing pixels, and the GDI capture rendered as an image shows the desktop with no
cursor anywhere in the region, even though `GetCursorPos` placed the cursor inside it.

The DWM composites the cursor on its own plane, and neither `BitBlt` (with `CAPTUREBLT`)
nor Desktop Duplication reads that plane. **There is no pair of APIs on this machine where
one returns the cursor and the other does not.**

Why this is a correction worth keeping: the original architecture wanted a
"composite source / background-plate source" pair to make alpha recovery a two-equation
solve. That shortcut does not exist, and the solver must instead obtain background samples
from frames in which the cursor happened not to cover a pixel. See `algorithm.md`.

## 4. CORRECTION: the cursor's alpha channel needs care in two places

* `GetIconInfo` on the live cursor returns a 32x32 `hbmColor` whose alpha channel is not a
  clean 0/255 mask (earlier probe: `nonzero=105/1024 full255=0 min=0 max=254`), so it is not
  by itself a faithful source of the rendered cursor.
* The DXGI `COLOR` pointer shape **is** premultiplied BGRA. For this cursor, all 1081
  semi-transparent pixels satisfy `R,G,B <= A`, which is the signature of premultiplication;
  `PointerShapeDecoder` un-premultiplies it. **This inference is heuristic**, and for a
  cursor whose anti-aliased edges are mid-grey with straight alpha it would be wrong; the
  decoder therefore reports what it did, and the mode is overridable. See ROADMAP.md.

## 5. `DXGI_OUTDUPL_POINTER_POSITION` is the shape's TOP-LEFT, not the hotspot

Determined by measurement rather than by reading the documentation:

```
GetCursorPos (settled)      = (1739,1954)   <- the hotspot, by definition
DXGI pointer (output space) = (957,503)  visible=True
DXGI pointer (desktop space)= (1727,1943)
measured offset P - Q       = (12,11)
DXGI shape hotspot field    = (11,11)
```

The offset equals the shape's own `HotSpot` field to within one pixel (the residual is
consistent with the mouse moving between the two samples, which is expected because the
sampling and the `GetCursorPos` call are not atomic). So:

```
hotspot_on_screen = monitor_rect.origin + PointerPosition + shape.HotSpot
```

`PointerAnchorCalibrator` re-measures this at runtime instead of trusting the constant, so a
driver that behaves differently is detected rather than silently mis-placed.

## 6. Duplication delivers no frame while the desktop is still

`AcquireNextFrame` returns `DXGI_ERROR_WAIT_TIMEOUT` indefinitely if nothing on the screen
changes. Sampling needs an observation even when nothing moved, so
`DxgiDuplicationSource` caches the last rectangle it read and replays it with
`IsCached = true`. Callers can therefore distinguish "the screen is unchanged" from "no data".

## 7. COM vtable layout: two mistakes worth recording

Hand-written vtable dispatch under Native AOT has no compile-time safety net. Both of these
were silent at build time and produced nonsense at runtime:

1. Counting method indices *within* an interface instead of *within the vtable* (a 3-slot
   error, because IUnknown occupies slots 0..2). `IDXGIDevice::GetAdapter` at slot 4 is
   really `IDXGIObject::SetPrivateDataInterface`; the resulting garbage pointer crashed
   inside `IUnknown::Release`.
2. Assuming `ID3D11DeviceContext` derives from `IUnknown`. It derives from
   **`ID3D11DeviceChild`**, which adds 4 slots, so `Map` is at absolute slot **14**, not 10.
   Slot 10 is `PSSetSamplers`, a `void` method, so the call returned leftover register
   garbage: measured `hr=0x794F9D00`, which is not a valid HRESULT shape, with a null mapped
   pointer. `ID3D11Device` *does* derive from `IUnknown` directly (confirmed by
   `CreateTexture2D` working at slot 5).

Both are documented in `src/RatTrap/Interop/DxgiInterop.cs` with the full absolute-index
audit table, and `rattrap probe` contains a step-by-step readback diagnosis (`DiagnoseReadback`)
that reports each native call's HRESULT, because these failures do not look like failures.

## 8. Environment facts that constrain the build

| Fact | Consequence |
| --- | --- |
| `dotnet` SDK 9.0.318, ILCompiler 9.0.20 in the local NuGet cache | Native AOT publish works fully offline |
| NuGet TLS egress blocked | Package references are impossible; the project has **zero** |
| MSBuild's `Csc` task and the Roslyn server need **named pipes** | Under a sandbox that forbids named pipes, `dotnet build` fails with `UnauthorizedAccessException` from `Microsoft.CSharp.Core.targets`. `UseSharedCompilation=false` in `Directory.Build.props` keeps compilation in-process |
| PowerShell piping `dotnet` output also fails in a confined sandbox | Run `dotnet` without pipes when builds misbehave |
| `SetProcessDpiAwarenessContext(PMv2)` returns FALSE with error 0 | The application manifest already applied it; this is success, not failure |

## 9. Corrections forced by building the GUI

Building the interactive window exposed three more things that were assumed and are false. All three
were found by running it, not by reading about it.

### 9.1 A duplication session is exclusive, and a failed second attempt damages the first

The first GUI opened a duplication for the pointer shape and then asked `CaptureFactory` for a capture
source, which opened a **second** duplication on the same output:

```
compositor: Desktop Duplication open on \\.\DISPLAY2 (1920x1080)
DXGI Desktop Duplication unavailable, falling back to GDI:
  DuplicateOutput failed on \\.\DISPLAY2: the output refused a duplication session (0x80070057)
```

`0x80070057` is `E_INVALIDARG`. The second session failed as expected - and from that moment the
**first** one never reported a pointer shape at all, on a machine where opening a single duplication
reports one on the very first frame. The fix is structural rather than defensive: one source per
session, which is sufficient because duplication excludes only the *system* cursor and still contains
a cursor the application draws itself. The factory's old "composite source + background plate source"
pair was removed, since it existed only to exploit a difference that §3 shows does not exist.

### 9.2 An idle pointer means no pointer shape, and reopening is what fixes it

`GetFramePointerShape` is filled in on the frame where the shape *changed*. With nobody touching the
mouse nothing changes, so nothing is reported. Measured: 83 frames over nine seconds, zero shape
reports, on a desktop where the cursor was plainly visible.

Two remedies were tried:

| Remedy | Result |
| --- | --- |
| Displace the pointer one pixel and put it back (`SetCursorPos`) | Works, but **only with a delay between the two moves**. Issued back to back they coalesce into a single change and the compositor never recomposites. |
| Close and reopen the duplication session | Works reliably, every time, because a fresh `DuplicateOutput` always delivers a full initial frame. This is the primary remedy. |

The nudge is nonetheless a real, if tiny, modification of the user's input state, so it happens at
most once per session, only if the reopen did not already work, and is switchable off with
`--no-nudge`.

### 9.3 `AcquireNextFrame` with a zero timeout never reports a shape at all

The GUI's first version polled with a timeout of 0. Even with a duplication that was delivering frames
and a pointer shape pending, no shape was ever seen, because `GetFramePointerShape` is only valid for
a frame that was actually **acquired** and `AcquireNextFrame(0)` times out immediately when nothing
has changed. Any poll interval intended to observe pointer shapes must pass a real timeout - 100 ms is
what the CLI uses.

### 9.4 CORRECTION: this host's `.cur` loader is not pixel-preserving, for any file

Loading *any* `.cur` here and reading the pixels back does not return what was stored:

* `C:\Windows\Cursors\aero_arrow.cur`, the 32x32 entry, comes back with **551 of 4096 bytes differing**
  and a maximum delta of 255 (stored alpha 189 comes back as 0). Identical result via `GetBitmapBits`,
  so it is the loader rather than the read API.
* Every `.cur` is rescaled to 32x32 regardless of stored size: both a 16x16 and a 48x48 entry return
  32x32, with the hotspot scaled to match.

This also retroactively explains the odd live-cursor alpha recorded in §4 (`max=254`, no fully opaque
pixel) - it was the loader, not the cursor format. The consequence is that cursor-file pixel fidelity
on this machine can only be established against the file **bytes**, which is what
`tools/verify_cursor_output.py` does, plus a byte-exact round trip through the `.ico` path
(`idType=1`), which uses the identical DIB payload and *is* exact here.

## 10. What is NOT covered by these measurements

* **An application-rendered cursor was never captured**, because no exclusive-fullscreen
  game was available in this session. That is the regime the reconstruction solver targets.
  The self-test therefore creates a real window that draws a known cursor sprite into its
  own frame and captures it through Desktop Duplication — real hardware pixels, known
  ground truth — rather than simulating the capture.
* **Rotation** other than identity was not exercised; the duplication backend refuses
  rotated outputs and falls back to GDI.
* **HDR** and DRM-protected output were not exercised.
* **Concurrent duplicators** (e.g. OBS display capture running at the same time) were not
  exercised; the per-output duplication limit is handled as a fallible condition with a
  clear diagnostic, not as an assumption.
