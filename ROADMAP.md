# ROADMAP

[**English**](ROADMAP.md) | [中文](ROADMAP.zh-CN.md)

Ordered by value delivered per unit of risk. Each item states what is known today, so nobody has to
re-derive it.

---

## 1. A Windows.Graphics.Capture backend — the highest-value item by a wide margin

**Status: researched, not implemented.** Full reasoning, with citations, in
[docs/fullscreen-capture.md](docs/fullscreen-capture.md).

**What it fixes.** `GraphicsCaptureSession.IsCursorCaptureEnabled` is documented as "whether the capture
session will include the cursor in the captured content", true by default, and available since Windows 10
2004. That gives a **composite** and, with the flag off, a **clean background plate** for the same
instant — the pair whose absence shaped this entire project. Measured on this machine, GDI `BitBlt` and
Desktop Duplication agree pixel for pixel over a 128×128 region, so neither provides it; WGC does.

The consequences go well beyond fullscreen:

* Alpha stops being a search problem. Instead of needing the cursor to move so that a pixel is sometimes
  covered and sometimes not, one frame pair gives `D` and `B` together, and the solve becomes exact rather
  than statistical.
* Crosshair mode stops needing the neighbourhood-background workaround.
* A windowed game can be captured without the rest of the desktop.
* It is not subject to the per-output duplication limit, so it can coexist with OBS display capture.
* No hooking, no driver, no injection — which matters, because a hook is the one approach that could
  plausibly attract an anti-cheat's attention.

**The blocker is packaging, not concept.** WGC is WinRT and the normal route is the C#/WinRT projections
in a NuGet package, which this project cannot restore. The workaround is the one already used for DXGI:
declare the COM interfaces by hand and call the vtables. About 300–400 lines: `RoGetActivationFactory` and
the HSTRING helpers from `combase.dll`, plus local declarations for `IGraphicsCaptureItemInterop`,
`IGraphicsCaptureItem`, `IDirect3D11CaptureFramePool(Statics2)`, `IDirect3D11CaptureFrame`,
`IGraphicsCaptureSession` and `IDirect3DDxgiInterfaceAccess`. Poll `TryGetNextFrame` on the existing timer
tick rather than subscribing to `FrameArrived`, which avoids needing a dispatcher queue and keeps
everything on the UI thread like every other backend here.

**Acceptance test, and it is a good one.** Capture a monitor twice, once with `IsCursorCaptureEnabled`
true and once false, and confirm that the cursor appears in exactly one of the two. That single test would
establish both that the interop works and that the missing half of the architecture now exists.

## 1b. Motion segmentation, which is what crosshair mode is really missing

**Status: not implemented, and it is the reason crosshair mode must be treated as experimental.**

Crosshair mode separates a fixed overlay from the scene by asking the scene to vary underneath it. That is
necessary but not sufficient, and the gap was found by pointing it at a text editor:

* Text is thin. Text is high-contrast. Text differs from its neighbourhood median. A wall of text therefore
  satisfies every spatial test a crosshair satisfies.
* Coverage rate (≥0.85) rejects text that *drifts*, because a moving stroke is not at the same screen
  position in every frame. It does not reject text that is *stationary on screen*.
* Requiring the majority of covered pixels to have had their alpha identified rejects the case where the
  scene under the text is still. It does not reject text over something that happens to be moving.
* This was measured twice: a 193x193 "crosshair" over a document, and a 193x94 one, both on a window that
  contains no crosshair.

The discriminator that actually works is **motion**, and it is the same one a human uses: a crosshair is the
region of the frame with **zero local motion** while the surrounding scene moves. Text and other scene
structure move with the scene, because they *are* the scene.

**Design sketch.** Estimate a coarse local motion field between consecutive frames - block matching over the
watched square is enough, and the square is only 256 px. Then classify each candidate pixel by whether its
own motion is consistent with its neighbourhood's:

* consistent (including "both moving") → scene structure, reject;
* zero motion while the neighbourhood moves → a fixed overlay, keep.

That single test subsumes the coverage-rate and solved-fraction guards rather than adding to them, and it
would also fix the same ambiguity in cursor mode. It is bounded work: one block-matching pass per frame over
a small square, reusing the `ArgbBitmap` and span machinery already present.

**Acceptance test.** The synthetic crosshair over a *scrolling* background must be recovered, and the same
synthetic crosshair over a *scrolling text* pattern must be rejected - the second is the case that fails today.

**The alternative is to make the whole question go away**: item 1's WGC backend supplies the background
directly, so no inference about what is overlay and what is scene is needed at all. That is why it is item 1.
## 2. Dynamic (animated) cursors

**Status: not implemented.** The solver assumes a cursor whose shape does not change. A dynamic cursor
breaks that premise: `alpha(r)` and `C(r)` differ between phases, so pooling observations across phases
fits a model true of no single frame.

**What is already known.**

* The compositor path does not have this problem in principle: `GetFramePointerShape` reports each phase
  as a new shape and each decodes independently. What is missing is *collecting* the phases into one
  `.ani` and ordering them.
* An `.ani` `icon` chunk contains a **complete `.cur` file image**, not a bare DIB. Proven by byte
  arithmetic on `aero_busy.ani`: chunk length 30894 = `6 + 3*16` header + DIBs `16936 + 9640 + 4264`.
  Hand-building the bare-DIB alternative makes `LoadCursorFromFileW` reject the file outright.
  `AniWriter` already emits whole-`.cur` frames and accepts per-frame durations and a step sequence.
* An opaque interior is recovered exactly with no background variation at all, which makes phase *change
  detection* cheap even when full recovery is not possible.

**Design sketch.** Detect that phases exist by change-point detection on the recovered model, not by
counting shapes — a phase change and a cursor *type* change look identical from outside. Segment per-frame
features (starting with the fully-opaque subset, which needs no background variation). Solve each phase
with the existing solver restricted to its frames. Order phases from the transition graph, and estimate
durations from dwell times, emitting a `rate ` chunk only when the observation ran long enough to see
several cycles.

**Trails and motion blur are explicitly out of scope.** A trail is a composite of several past positions,
which is a deconvolution problem with its own model.

## 3. Motion-compensated background for a parked cursor

**Status: not implemented.** Variation currently comes from the cursor moving, the scene changing, or (in
crosshair mode) the neighbourhood. A cursor parked over a static scene is reported as unsolvable, which is
correct but unhelpful when the background is scrolling or animating.

**Design sketch.** For a covered pixel, search a bounded temporal window and a small spatial neighbourhood
for a patch matching the observed *border* — the ring just outside the glyph, which is never covered — and
use the matched patch's centre as the background prediction, with its match residual as the confidence.

**Risk.** A wrong match injects a wrong `B`, the failure mode that took the most work to remove in the
static case. Any implementation must carry the match residual into the fit and be evaluated against a
synthetic scrolling background where the true `B` is known.

## 4. Pointer tracking for cursors the OS cannot locate

**Status: partial.** The pointer position comes from `GetCursorPos`, which is right whenever the system
cursor is being *positioned* even if it is not drawn — covering most games that hide it and draw their own.
An application reading raw input and never moving the system cursor defeats it.

**Design sketch.** Recover the position from the frames using the background model the solver already
builds: the glyph is the region that disagrees with a well-established background, so its centroid, or a
match against the partially recovered glyph, gives the next position.

**Prerequisite.** Registration. Every estimate has error, and a one-pixel error smears alpha across
neighbouring offsets; the fix is to estimate the sub-pixel alignment that minimises the reconstruction
residual and re-solve with it.

## 5. Rotation, hot-plug, and per-monitor scaling

Monitors are enumerated with per-monitor-V2 awareness, DPI and scale are reported, the ROI is clamped to
the monitor, and capture follows the pointer between monitors. Missing:

* **Rotated outputs.** Duplication refuses a rotated output and falls back to GDI, which returns an upright
  image. The transform would be a small amount of code in `CopyRegion` plus coordinate mapping for
  `PointerPosition`.
* **Hot-plug during a session.** A monitor change surfaces as `NeedsReinitialize` and a reopen, which is
  correct but unverified against an actual unplug.
* **Per-monitor cursor scaling for the GDI fallback**, which reports native size and says so but does not
  offer to scale.

## 6. Test coverage gaps

| Gap | Why it matters |
| --- | --- |
| **`.cur` pixel bytes are not verified through the Windows loader on this host** | This machine's loader is not pixel-preserving for *any* `.cur`, including a first-party one (551/4096 bytes differ, everything rescaled to 32×32), so the proof is against file bytes by an independent parser, plus a byte-exact round trip via `.ico`, which shares the payload. A host with a 1:1 loader would close this. |
| **`seq ` / multi-step `.ani` timing** | Structurally verified and accepted by Windows, but no reference file with a real `seq ` chunk was available to compare against, and playback was not observed. |
| **A real exclusive-fullscreen game** | The reconstruction and crosshair paths are verified against synthetic ground truth, including a real window captured through Desktop Duplication, but not against an actual game. |
| **`MapDesktopSurface`** | Implemented and preferred when `DesktopImageInSystemMemory` is set, but this machine reports it clear, so the path has never executed. |
| **The global hot key under focus contention** | Registration is verified; that `Ctrl+Insert` reaches the window while an exclusive-fullscreen game holds focus is not, and cannot be without such a game. A game that grabs the keyboard at the driver level (raw input with no `Alt+Tab` out) would swallow it. |

## 7. Smaller items

* **The CLI and the GUI carry parallel capture logic.** `Cli/CaptureCommand.cs` and
  `Ui/CaptureSession.cs` share every component that matters — capture source, pointer decoder, solver,
  writers — but sequence them separately, so a fix in one can miss the other. Folding the CLI onto
  `CaptureSession` would remove the duplication at the cost of the CLI's more granular options.
* **Optional elevation.** If a deployment needs it, offer to relaunch elevated via `ShellExecute("runas")`
  rather than forcing it in the manifest, which would prompt on every launch and break drag-and-drop. Note
  that this is a deployment convenience, not a fix for anything: no capture failure on this machine is
  caused by integrity level.
* **Premultiplied-alpha detection is heuristic.** `PointerShapeDecoder` un-premultiplies a `COLOR` shape
  when every semi-transparent pixel satisfies `R,G,B <= A`. Correct for this machine's cursor, but it
  would brighten a cursor whose anti-aliased edges are mid-grey with straight alpha. The decision is
  reported and should become an explicit option.
* **Confidence is computed but not shown.** `PixelModel.Confidence` exists per pixel and is used only in
  the report; the model map is logged with `verbosemodelmap = true` but not drawn.
* **`.ico` export.** `CursorFileWriter.EncodeIco` exists and is verified; no command exposes it.
* **Crosshair offset.** A game that offsets its crosshair for a weapon's sight picture is not found, since
  the watch square is positioned by the window rect.
* **Dynamic range.** HDR and 10-bit output are not handled; the capture path assumes 8-bit BGRA.
* **Protected content** is detected and reported but naturally not worked around.

