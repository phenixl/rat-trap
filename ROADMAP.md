# ROADMAP

[**English**](ROADMAP.md) | [中文](ROADMAP.zh-CN.md)

Ordered by value delivered per unit of risk. Each item states what is known today, so nobody has to
re-derive it.

---

## 1. Animated cursors, through the compositor

**Status: not implemented. Highest priority.**

The compositor route makes this far cheaper than it looks, and the reason is already in
`docs/measured-behaviour.md`: `GetFramePointerShape` hands over the finished bitmap **with its alpha channel**,
so each phase of an animation arrives complete, exact, and already scaled for DPI and pointer size. Nothing has
to be solved. `PointerShapeDecoder` already decodes an individual phase. What is missing is noticing that there
is more than one, holding them in order, and knowing how long each is shown.

**The real problem is the cycle, and it is a timing problem.** An animated cursor loops, so its phases are the
cycle of a repeating sequence. Three things have to come out of watching one:

1. **Which observed shapes are phases of one animation**, as opposed to the cursor changing *type* because the
   pointer moved onto a different control. From the shape channel alone these look identical, and getting it
   wrong produces an `.ani` that flickers between an arrow and an I-beam.
2. **The order** of the phases within the cycle.
3. **How long each phase lasts**, because a wrong `rate ` chunk makes a correct animation play at the wrong
   speed - which is more obviously wrong than a missing one.

**What is already known.**

* A shape is reported **when it changes**, and the frame info carries `LastMouseUpdateTime`. The phases
  therefore arrive as a sequence of timestamped events rather than as a stream of frames, and the interval
  between consecutive shape updates is a *measurement* of the phase duration rather than an estimate. That is
  the strongest signal available for (3), and it is strictly better than polling, which could only sample the
  cycle and would have to guess the rest.
* **Position is the discriminator for (1).** The phases of an animation arrive while the pointer is stationary;
  a type change coincides with the pointer having moved onto something else. `DXGI_OUTDUPL_POINTER_POSITION`
  comes with every frame, so a shape change at the same position is a phase and a shape change at a new position
  is a type change. The existing one-pixel nudge is compatible with this, being a controlled move of one pixel,
  so a tolerance of a few pixels keeps phases together.
* **A cycle is visible as a repeat.** Order (2) falls out of finding the smallest period `p` such that
  `shape[i] == shape[i+p]` across the observed run, which is ordinary cycle detection over a short sequence of
  hashes. `CaptureSession` already hashes each shape to detect repeats, so the comparison exists.
* `AniWriter` already emits whole-`.cur` frames with per-frame durations and a step sequence, and each `.cur`
  carries its own hotspot, so phases with different shapes and hotspots need no new file-format work. That an
  `.ani` `icon` chunk holds a complete `.cur` image rather than a bare DIB is established by byte arithmetic on
  the system's own `aero_busy.ani` in
  [docs/phase1-tech-selection.md](docs/phase1-tech-selection.md): eighteen `icon` sub-chunks of 30894 bytes
  each, against `6 + 3*16` for the header plus DIBs of 16936, 9640 and 4264. Hand-building the bare-DIB
  alternative makes `LoadCursorFromFileW` reject the file outright.

**The rule that matters: refuse an incomplete cycle.** Emitting three of four phases as an `.ani` produces a
file that plays, looks plausible, and is wrong - the worst outcome this project keeps running into. If no full
cycle was observed, write the single best phase, say in the report how many distinct shapes were seen and that
no cycle closed, and offer to keep collecting. A user who is told "four shapes seen, cycle not closed, press
Start again" can act on it; a user handed a broken animation cannot.

**Interaction with item 3.** The shape channel is event-driven, so its timing is only as good as the operating
system's reporting, and that is unmeasured. Windows.Graphics.Capture delivers a steady frame stream with the
cursor composited in, which measures phase durations directly and does not depend on change notifications at
all. If the shape-channel timestamps turn out to be too coarse, item 3 becomes the better instrument for this
item rather than a separate feature - which is worth knowing before building either.

**Out of scope, stated rather than discovered later:** trails and motion blur. A trail is a composite of several
past positions, which is a deconvolution problem with its own model, not an extra phase.

## 2. Animated cursors without the compositor, and animated crosshairs

**Status: not implemented. Second priority.**

When the compositor is unavailable - an exclusive-fullscreen game, or an application drawing its own cursor -
there is no finished bitmap to collect and the animation has to come out of the solver. That breaks the solver's
premise rather than straining it: `alpha(r)` and `C(r)` differ between phases, so observations pooled across
phases fit a model true of no single frame, and the fit degrades quietly rather than failing.

**The approach, in the order the pieces become available.**

1. **Find the phases before solving for anything.** The cheap detector is already established and needs no
   background model at all: a cursor's **fully opaque interior** is recovered exactly from any single frame,
   because an opaque pixel's composite does not depend on what is behind it. A per-frame signature taken from
   the opaque subset - its shape, its colour, or both - therefore separates frames by phase without knowing
   alpha anywhere.
2. **Segment the run into phases** by that signature, using change-point detection rather than a fixed frame
   count, because the phases have different and unknown durations.
3. **Solve each phase with the existing solver, restricted to that phase's frames.** Each phase then gets the
   full set of guards the solver already applies, and the background variance it needs comes from the pointer
   moving across the scene while that phase is showing.
4. **Order and time the phases** from the observed sequence: first-appearance order within a cycle, and
   per-phase durations from frame timestamps aggregated over the whole run rather than from a single cycle,
   which is what makes the durations survive one unusually long or short pass.
5. **Report incomplete phases instead of padding them.** A phase with too few frames for `MinPairs` or too
   little `MinBackgroundVariance` must be named as unresolved in the report and in the observation view, rather
   than silently filled with whatever the fit happened to produce. The observation view is the right place to
   watch this, which is a second reason it exists.

**Animated crosshairs come almost for free, and that is the reason to build it this way.** The acquisition
region is already a parameter: cursor mode follows the pointer, crosshair mode watches a fixed square. Phase
segmentation and per-phase solving do not care which. Once step 3 works, pointing the same machinery at the
crosshair's square gives an animated crosshair - a spread that opens, a hit marker, a recoil bloom - with no new
algorithm, and the existing crosshair guards continue to apply per phase.

**The one thing that does not carry over.** In cursor mode the pointer's own motion supplies the changing
background. In crosshair mode the scene has to move, which that path already requires and already refuses
without - so a crosshair animation over a still scene stays refused, phase by phase, rather than becoming
answerable merely because there are now several phases to pool.

## 3. A Windows.Graphics.Capture backend

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

## 4. Motion segmentation, which is what crosshair mode is really missing

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

## 5. Motion-compensated background for a parked cursor

**Status: not implemented.** Variation currently comes from the cursor moving, the scene changing, or (in
crosshair mode) the neighbourhood. A cursor parked over a static scene is reported as unsolvable, which is
correct but unhelpful when the background is scrolling or animating.

**Design sketch.** For a covered pixel, search a bounded temporal window and a small spatial neighbourhood
for a patch matching the observed *border* — the ring just outside the glyph, which is never covered — and
use the matched patch's centre as the background prediction, with its match residual as the confidence.

**Risk.** A wrong match injects a wrong `B`, the failure mode that took the most work to remove in the
static case. Any implementation must carry the match residual into the fit and be evaluated against a
synthetic scrolling background where the true `B` is known.

## 6. Pointer tracking for cursors the OS cannot locate

**Status: partial.** The pointer position comes from `GetCursorPos`, which is right whenever the system
cursor is being *positioned* even if it is not drawn — covering most games that hide it and draw their own.
An application reading raw input and never moving the system cursor defeats it.

**Design sketch.** Recover the position from the frames using the background model the solver already
builds: the glyph is the region that disagrees with a well-established background, so its centroid, or a
match against the partially recovered glyph, gives the next position.

**Prerequisite.** Registration. Every estimate has error, and a one-pixel error smears alpha across
neighbouring offsets; the fix is to estimate the sub-pixel alignment that minimises the reconstruction
residual and re-solve with it.

## 7. Rotation, hot-plug, and per-monitor scaling

Monitors are enumerated with per-monitor-V2 awareness, DPI and scale are reported, the ROI is clamped to
the monitor, and capture follows the pointer between monitors. Missing:

* **Rotated outputs.** Duplication refuses a rotated output and falls back to GDI, which returns an upright
  image. The transform would be a small amount of code in `CopyRegion` plus coordinate mapping for
  `PointerPosition`.
* **Hot-plug during a session.** A monitor change surfaces as `NeedsReinitialize` and a reopen, which is
  correct but unverified against an actual unplug.
* **Per-monitor cursor scaling for the GDI fallback**, which reports native size and says so but does not
  offer to scale.

## 8. Test coverage gaps

| Gap | Why it matters |
| --- | --- |
| **`.cur` pixel bytes are not verified through the Windows loader on this host** | This machine's loader is not pixel-preserving for *any* `.cur`, including a first-party one (551/4096 bytes differ, everything rescaled to 32×32), so the proof is against file bytes by an independent parser, plus a byte-exact round trip via `.ico`, which shares the payload. A host with a 1:1 loader would close this. |
| **`seq ` / multi-step `.ani` timing** | Structurally verified and accepted by Windows, but no reference file with a real `seq ` chunk was available to compare against, and playback was not observed. |
| **A real exclusive-fullscreen game** | The reconstruction and crosshair paths are verified against synthetic ground truth, including a real window captured through Desktop Duplication, but not against an actual game. |
| **`MapDesktopSurface`** | Implemented and preferred when `DesktopImageInSystemMemory` is set, but this machine reports it clear, so the path has never executed. |
| **The global hot key under focus contention** | Registration is verified; that `Ctrl+Insert` reaches the window while an exclusive-fullscreen game holds focus is not, and cannot be without such a game. A game that grabs the keyboard at the driver level (raw input with no `Alt+Tab` out) would swallow it. |

## 9. Smaller items

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

## 10. Configurable observation caps

**Status: not implemented.** The observation recording is bounded by three constants in
`ReconstructionHistory`: 16384 blocks per frame (which derives the scale), 32 MB of panels, and 300 frames.
The measured consequences are in `docs/development.md`, and the defaults are the measured ones. They are
constants rather than settings because the numbers that chose them were measured rather than guessed, and a
knob nobody has a reason to turn is a knob that turns itself by accident.

What would justify settings: a machine with more memory than this one wanting a longer replay, and a
crosshair region of 1024 pixels wanting more than 1/8 scale, which is the one case where the block cap
forces a visibly coarse panel. Both are legitimate, and both should come with the derived numbers displayed
so the cost stays visible - which is what the transport caption already does.