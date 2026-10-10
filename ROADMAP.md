# ROADMAP

Ordered by value delivered per unit of risk. Each item states what is known today, so nobody has to
re-derive it.

---

## 1. Dynamic (animated) cursors — the main open problem

**Status: not implemented.** The solver assumes a cursor whose shape does not change. A dynamic
cursor breaks that premise: `alpha(r)` and `C(r)` differ between phases, so pooling observations
across phases fits a model that is true of no single frame. A `.ani` has phases; the current output
is a single-frame `.ani` or `.cur`.

**What is already known.**

* The compositor path does not have this problem in principle: `GetFramePointerShape` reports each
  phase as a new shape, and each one decodes independently. So on the desktop, an animated cursor can
  already be captured phase by phase — what is missing is *collecting* the phases into one `.ani` and
  ordering them.
* An `.ani` `icon` chunk contains a **complete `.cur` file image**, not a bare DIB. Proven by byte
  arithmetic on `C:\Windows\Cursors\aero_busy.ani` (a zero-`seq`, 18-frame, `AF_ICON` file): chunk
  length 30894 = `6 + 3*16` header + the three DIBs `16936 + 9640 + 4264`. Hand-building the
  bare-DIB alternative makes `LoadCursorFromFileW` reject the file outright. `AniWriter` already emits
  whole-`.cur` frames and accepts per-frame durations and a step sequence.
* Measurements relevant to phase identification: the pointer shape arrives with a frame index
  (`AccumulatedFrames`) and a `LastPresentTime`, and an opaque interior is recovered exactly with no
  background variation at all (§6.1 of `algorithm.md`), which makes phase *change detection* cheap
  even without full recovery.

**Design sketch.**

1. **Detect that phases exist.** Not by counting shapes — a phase change and a cursor *type* change
   look identical from outside. Instead, change-point detection on the recovered model: segment the
   observation timeline at frames where the composite at a *fixed* offset changes without the
   background having changed.
2. **Segment into phases.** Per-frame features (the fully-opaque subset of the glyph, which needs no
   background variation) clustered over time. The number of phases is not known in advance, so this is
   a change-point problem, not a fixed-`k` clustering.
3. **Solve each phase independently** with the existing solver, restricted to its frames. Each phase
   needs its own background coverage; a cursor that only ever appears in one pose on a uniform
   background hands the user the same "move the pointer" instruction that already exists.
4. **Order and time the phases.** Phase order comes from the transition graph. Phase *durations* are
   the hard part: the capture frame rate is unrelated to the cursor's own rate, so durations have to
   be estimated from dwell times, and they are only trustworthy if the observation ran long enough to
   see several cycles. Emit a `rate ` chunk only when the durations are confident; otherwise let all
   phases share the `anih` default and say so.
5. **Trails and motion blur.** Explicitly out of scope. A cursor with a trail violates "the composite
   is the cursor over the background" — the trail is a *composite of several past positions*, which is
   a deconvolution problem and needs its own model.

**Acceptance test.** A synthetic animated cursor drawn into a real window, captured through Desktop
Duplication, must come back as the right number of phases with per-phase pixel accuracy comparable to
the static case, and the resulting `.ani` must load with the expected frame count.

---

## 2. Motion-compensated background for a parked cursor

**Status: not implemented.** Today, variation for the solve comes from the cursor moving or the scene
changing. A cursor that sits still forever over a *static* scene is reported as unsolvable, correctly
but unhelpfully: if the background under it is scrolling, panning or animating, that motion is
information that is currently thrown away.

**What is already known.** The `(B, D)` pair for a covered pixel needs `B` at that pixel and moment.
When the pixel is never uncovered, `B` can still be recovered from elsewhere in the frame if the same
content appears there at a time when the pixel *was* visible — a block-matching search over a small
temporal neighbourhood, with the residual of the match deciding whether the prediction is usable.

**Design sketch.** For each covered pixel, search a bounded temporal window and a small spatial
neighbourhood for a patch whose content matches the observed neighbourhood *border* (the ring just
outside the glyph, which is never covered), then use the matched patch's centre as the background
prediction and its match residual as the confidence. Feed those pairs into the same accumulator. Keep
the existing spatial mechanism as the primary and let this only add pairs where the primary produced
none.

**Risk.** A wrong match injects a wrong `B`, which is the failure mode that took the most work to
remove in the static case (§5 of `algorithm.md`). Any implementation must carry the match residual all
the way into the fit and must be evaluated against a synthetic scrolling background where the true
`B` is known.

---

## 3. Windows.Graphics.Capture backend

**Status: not implemented.** Desktop Duplication is currently the only fullscreen-capable backend.

**Why it would help.** WGC captures per-window as well as per-monitor, is not subject to the
per-output duplication limit (which is why the tool cannot currently run alongside another duplication
consumer such as OBS display capture), and handles some HDR and hardware-overlay cases better. It also
has an explicit cursor-inclusion switch (`GraphicsCaptureSession.IsCursorCaptureEnabled`), which would
give a **cursor-including and a cursor-excluding view of the same frame** — the exact pair whose
absence is documented in `measured-behaviour.md` §3, and which would make alpha recovery a
two-equation solve.

**Why it is not done.** WGC is a WinRT API. Under Native AOT the projections have to come from a
package, and this machine cannot restore packages at all. It would need either a hand-written
activation-factory interop layer (feasible, comparable in size to the existing DXGI interop) or a
machine with network access.

**Worth knowing before starting.** `IGraphicsCaptureItemInterop` needs a monitor `HMONITOR`, which is
already computed by `Monitors`; the frame pool and a D3D11 device are already available in
`DxgiDuplicationSource`. The main new work is the WinRT activation and the `IGraphicsCaptureItem`
plumbing.

---

## 4. Pointer tracking for cursors the OS cannot locate

**Status: partial.** The pointer position comes from `GetCursorPos`. That is right whenever the
system cursor is being *positioned* even if it is not drawn — which covers most games that hide it and
draw their own — and `--x`/`--y` manual placement covers fixed-reticle cases. It is wrong for an
application that reads raw input and never moves the system cursor at all.

**Design sketch.** Recover the pointer position from the frames themselves, using the background model
the solver already builds: the glyph is the region that disagrees with a well-established background,
so its centroid (or its match against the partially recovered glyph) gives the next position. This
also finally enables the fully hands-off mode — "put the reticle on a textured background and leave it
alone" — for the reconstruction case.

**Prerequisite.** Registration. Every position estimate has an error, and a one-pixel error smears
alpha across neighbouring offsets; §9 of `algorithm.md` lists this as a known limit. The fix is to
estimate the sub-pixel alignment that minimises the reconstruction residual, and to re-solve with it.

---

## 5. Multi-monitor capture and per-monitor DPI — partially done

Monitors are enumerated with per-monitor-V2 awareness, DPI and scale are reported, the capture ROI is
clamped to the monitor under the pointer, and the tools follow the pointer between monitors. What is
*not* done:

* **Rotated outputs.** The duplication backend refuses a rotated output and falls back to GDI, which
  returns an upright image. Implementing the rotation transform would be a small amount of code in
  `CopyRegion` plus a coordinate mapping for `PointerPosition`.
* **Hot-plug during a session.** A monitor change invalidates the duplication session; today that
  surfaces as `NeedsReinitialize` and a reopen, which is correct but not verified against an actual
  unplug.
* **Per-monitor cursor scaling.** The compositor path reports the shape at the size it is drawn, which
  is right. The GDI fallback reports native size only, and says so, but does not offer to scale.

---

## 6. Test coverage gaps

| Gap | Why it matters |
| --- | --- |
| **`.cur` pixel bytes are not verified through the Windows loader on this host** | This machine's loader is not pixel-preserving for *any* `.cur`, including a first-party one (551/4096 bytes differ, everything rescaled to 32×32), so the proof is done against file bytes by an independent parser and against the loader via the `.ico` path, which shares the payload. A host with a 1:1 loader would close this. |
| **`seq ` / multi-step `.ani` timing** | Structurally verified and accepted by Windows, but no reference file with a real `seq ` chunk was available to compare against, and playback was not observed. |
| **A real exclusive-fullscreen game** | The reconstruction path is verified against synthetic ground truth, including a real window captured through Desktop Duplication, but not against an actual game. |
| **`MapDesktopSurface`** | Implemented and preferred when `DesktopImageInSystemMemory` is set, but this machine reports it clear, so the code path has never executed. |

---

## 7. Smaller items

* **Premultiplied-alpha detection is heuristic.** `PointerShapeDecoder` un-premultiplies a COLOR
  shape when every semi-transparent pixel satisfies `R,G,B <= A`. For this machine's cursor that
  inference is correct, but a cursor whose anti-aliased edges are mid-grey with straight alpha would
  be brightened. The decision is reported and should become an explicit option.
* **Confidence is computed but not exposed.** `PixelModel.Confidence` exists per pixel and is used
  only for the report; the GUI shows the model map with `--verbose` but not a confidence overlay.
* **`.ico` export.** `CursorFileWriter.EncodeIco` exists and is verified; no command exposes it.
* **Dynamic range.** HDR and 10-bit output are not handled; the capture path assumes 8-bit BGRA.
* **Protected content.** Duplication reports `ProtectedContentMaskedOut`; the tool ignores it and would
  capture a black rectangle rather than explaining why.
