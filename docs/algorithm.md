# Recovering a cursor's colour and alpha from screen captures

[**English**](algorithm.md) | [中文](algorithm.zh-CN.md)

This is the derivation behind `src/RatTrap/Reconstruct/`. It records what the method is, why each
choice was made, which two approaches were discarded and what disqualified them, and — importantly —
what is provably not recoverable.

---

## 1. The problem

An application draws its own cursor into its own frame. The cursor is therefore ordinary pixels in
what the screen shows, and nothing in the operating system will hand it over: `GetCursorInfo` reports
the *system* cursor, and the desktop image from both Desktop Duplication and GDI `BitBlt` **excludes**
the system cursor (measured; see `measured-behaviour.md`). So the only source is the composite, and
the only way to separate the cursor from the scene behind it is to observe how the two vary
independently.

## 2. The model

Let `s` be a screen pixel, `t` a time, `h_t` the pointer hotspot, and `r = s - h_t` the
*cursor-local offset*. Give the cursor a fixed alpha `alpha(r)` and colour `C(r)` — fixed precisely
because the cursor is assumed not to change shape:

```
covered:    D_t(s) = alpha(r) * C(r) + (1 - alpha(r)) * B_t(s)
uncovered:  D_t(s) = B_t(s)
```

`B` is whatever was behind the cursor and is unknown. All arithmetic is on the 8-bit sRGB-encoded
values as captured, **not** linearised, because that is the domain in which the compositor blends;
linearising first would model a different operation than the one that produced the pixels.

## 3. Where the information comes from

Two mechanisms supply the variation that makes the system solvable, and the solver uses both:

**Spatial.** The same offset `r` lands on a different screen pixel every time the pointer moves,
because `s = h_t + r`. Each landing gives a pair `(B(s), D(s))` with a *different* `B`. So moving the
pointer once across a textured background already yields many pairs for every offset, with no change
to the scene at all. This is why the tool works on a completely static screen.

**Temporal.** A fixed screen position also sees its background change over time — animation,
scrolling, video — which adds pairs even if the pointer is parked.

The practical consequence is the usage instruction the tool prints: *move the pointer across a
textured area*. Nothing else is needed.

## 4. The solve

Per offset `r`, the observations are `(B_i, D_i)` in three channels. Write the model per channel as

```
D_c = m * B_c + k_c        with  m = 1 - alpha  and  k_c = alpha * C_c
```

### 4.1 One slope, not three

`alpha` is a property of the pixel, not of the channel, so `m` is **shared** across the three
channels while `k_c` is per-channel. This matters: fitting each channel independently would let the
channels disagree about `alpha`, and the disagreement shows up as colour fringing on anti-aliased
edges, where alpha is varying fastest and is least constrained.

### 4.2 Why it is closed-form

For a fixed `m`, the least-squares `k_c` is `mean(D_c) - m * mean(B_c)`. Substituting that leaves a
quadratic in the single unknown `m`, minimised at

```
        sum_c Cov(B_c, D_c)
m*  =  ---------------------
          sum_c Var(B_c)
```

So the accumulator stores only sums — `sum B`, `sum D`, `sum B²`, `sum D²`, `sum BD` per channel —
and never a sample list. Nothing iterates, nothing needs a starting guess, and there is no
convergence to fail. Then

```
alpha = 1 - m*          C_c = k_c / alpha        (for alpha away from 0 and 1)
```

and the residual RMS of the fit is available in closed form too, which is what the confidence value
reported per pixel is derived from.

## 5. Estimating the background

The solve needs `B` at the moment of each observation, and the background model is where the two
discarded designs both failed.

### 5.1 Discarded: a gated IIR filter

Fold a sample into a running estimate when it is close to that estimate; refuse it otherwise. This is
cheap, O(1) per pixel, and *looks* right — the cursor covers any given pixel only part of the time, so
the estimate should converge on the background.

It fails badly. Take a pixel whose **first** observation happens to be cursor coverage. An opaque
cursor produces the same value regardless of what is behind it, so the *next* observation also
matches, the estimate is declared confirmed, and the filter has now confidently adopted **the cursor
as the background**. Every later genuine background sample at that pixel is then paired against it —
inverted — and least squares quietly fits a slope to the inversion.

The symptom was unmistakable once the model map was printed: the recovered hotspot sat at offset 33
instead of 3, because a single phantom pair per offset was being recorded across the *entire* region
rather than on the glyph.

### 5.2 Discarded: confirming the estimate by sample count

Requiring the estimate to have been confirmed by a further observation (`age >= 2`) removes the
first-frame case but not the general one: a pixel that is covered twice in a row reaches `age >= 2`
while its estimate is still the cursor.

### 5.3 Used: the median of a bounded window

Per screen pixel, keep the last `N` (default 15) observations and take the per-channel median. The
cursor occupies a small part of the area it traverses, so for any given screen pixel the majority of
recent samples are genuine background, and the median picks them **no matter which sample came
first**. It also adapts to a permanent local change by itself, since stale samples simply age out, and
it has no learning rate to tune.

The estimate is only declared usable when a **strict majority** of the window agrees with it
(`MinBackgroundInlierFraction = 0.55`). That threshold is not arbitrary: it is exactly the condition
under which "the median is the background" is a true statement. A lower threshold admits windows
holding, say, 8 cursor samples and 7 background samples whose median *is* the cursor — the failure of
§5.1 all over again, and measurably still present at a 0.40 threshold, where it produced a spurious
checkerboard of phantom pixels beside a synthetic test cross.

### 5.4 Minimum coverage evidence

A cursor-local offset is only treated as part of the cursor once it has at least
`MinCoverageEvidence = 3` pairs. A single pair can come from a background estimate that was briefly
wrong, and it is not evidence of coverage. Without this floor one such pair extends the recovered
bounding box by twenty pixels and moves the hotspot with it, so the output is not merely noisy but
**mispositioned**.

### 5.5 The coverage rate, which is what separates a glyph from noise

An offset only counts as part of the cursor when at least `MinCoverageRate = 0.25` of the times it was
observed, it looked covered:

```
pairs / (pairs + uncovered)  >=  0.25
```

A real cursor covers a given offset **every** time the pointer places that offset on screen, so its
coverage rate is near 1 — measured 99.8% in the synthetic XOR case. Residue from a background model
that was briefly wrong is a handful of pairs against hundreds of ordinary observations — measured
0.2%.

This guard exists because of a real failure that every shape-based alternative failed to catch.
Running the reconstruction against an ordinary desktop, where **nothing draws a cursor at all**
(duplication excludes the system cursor), produced 402 "covered" pixels scattered over a 160×162 box,
because a terminal whose text was scrolling periodically disagreed with its own background estimate.
An earlier version reported that as a cursor, complete with a hotspot, and wrote it to disk. A wrong
answer that looks plausible is worse than a failure, because it cannot be told apart from a correct
one.

Shape tests were all rejected, and it is worth recording why, because each looks reasonable until
tried:

| Test | Why it is wrong |
| --- | --- |
| "The glyph must be compact — covered pixels must fill a decent fraction of their bounding box" | A crosshair is sparse. A 32×32 crosshair covers 63 pixels in a 1024-pixel box: 6%. |
| "The glyph must be connected" | A move/size-all cursor is four disconnected arrows. |
| "The hotspot itself must be drawn" | A move cursor's hotspot sits in the gap at the centre; some crosshairs have a gap too. |

Every one of those rejects a legitimate, common cursor. Availability of information has no such blind
spots, which is why the guards are about information: the coverage rate above, and the refusal in
§7 when neither mechanism could have learned anything.



### 6.1 Fully opaque pixels need no background variation

If `alpha = 1` then `D = C` for every background, so the composite is constant. The fit finds
`m = 0` and alpha comes out as 1 with no help from background variation at all — which is why the
interior of a solid cursor is recovered exactly even when the background underneath never changed.
Verified: a synthetic fully-opaque cursor recovers with **0 levels of colour error**.

### 6.2 XOR-composited cursors are a different model

Legacy and masked-colour cursors do not blend at all: `screen = background XOR mask` with a constant
mask. That is not affine in the background, so the alpha model fits it badly and the recovered
"alpha" would be meaningless. The accumulator therefore carries a second hypothesis — the XOR
difference of every pair — and a pixel is classified as XOR-composited when at least 95% of its pairs
agree on the same difference *and* the background varied. Verified: a synthetic XOR cross recovers
its mask bit-exactly (`FF,7F,00`) and is written out with the exact XOR mask, which reproduces the
original rendering.

### 6.3 Alpha exactly 0 or 1

At `alpha >= 0.98` the division `k / alpha` amplifies noise, so the composite mean is used instead —
correct, since an opaque pixel's composite *is* its colour. At `alpha < 0.02` the composite tracks
the background and the pixel is declared transparent rather than given a division by nearly zero.

## 6. The special cases, and why they are not optimisations

## 7. What is provably not recoverable

If a pixel is covered for the whole observation **and** the background under it never varied, the
observed value is a constant equal to `alpha*C + (1-alpha)*B` with both unknowns free. No amount of
further observation of the same configuration can resolve it. The solver reports those pixels rather
than inventing values, and the CLI prints how many there were and what to do about it.

Two configurations make this visible, and both are covered by self-tests that assert the **failure
reporting** rather than a magic result:

| Configuration | Expected behaviour |
| --- | --- |
| Stationary cursor on a stationary background | "no pairs were recorded, so nothing can be solved", with a pointer to the compositor path, which needs no reconstruction |
| Pointer moving over a perfectly uniform background | Pairs exist, but background variance is zero, so alpha is unidentified; the affected pixels are counted and the user is told to move across a more textured area |

Even in the uniform-background case, fully opaque pixels are still recovered exactly (§6.1), because
they never needed the background. Only the partially transparent ones are lost.

## 8. Assembling the output

* The output extent is the bounding box of every offset with enough coverage evidence, plus one pixel
  of margin for anti-aliased fringes that only ever produced weak samples.
* The hotspot is defined by the geometry rather than guessed: offset `(0,0)` *is* the pointer
  position, so the hotspot in the output image is `(-minRx, -minRy)`.
* RGB under fully transparent pixels is filled from the nearest opaque neighbour by a short dilation.
  Those bytes are invisible in a viewer but are read by bilinear filtering, where black under a
  transparent edge becomes a dark halo.
* If XOR-classified pixels are a large majority of the covered pixels, the cursor is written with the
  exact XOR mask (`CursorEncoding.XorMasked32`); otherwise it is written alpha-blended.

## 9. Known limits

* **Registration.** The solve assumes the pointer position reported for a frame is the true hotspot
  for the pixels of that frame. A one-pixel error smears alpha across neighbouring offsets. When the
  hotspot comes from `GetCursorPos` this is exact; a future motion-tracking pointer source would need
  a registration refinement pass (see `ROADMAP.md`).
* **Shape changes.** A cursor that animates or changes shape violates the fixed-`C(r)`, fixed-`alpha(r)`
  premise and produces a blend of its shapes. Detecting and phase-segmenting that is
  `ROADMAP.md`'s subject.
* **Background motion under a parked cursor.** Only the spatial mechanism is exploited. Predicting
  the background under a stationary cursor from elsewhere in the frame is a motion-estimation problem
  and is not implemented.
* **XOR plus alpha mixtures.** The XOR test and the affine fit are mutually exclusive per pixel. A
  cursor that used both would be classified per region, and a whole-file XOR encoding is only chosen
  when XOR dominates.

## 10. Crosshair mode: a fixed overlay needs a different background

Everything above assumes the cursor moves, which is what lets a per-pixel temporal median find the
background. A crosshair violates that assumption structurally, not incidentally: it sits at a fixed
screen position while the scene moves behind it. Every pixel it covers is covered in *every* frame, so the
temporal window fills entirely with composite values and the median of that window **is** the composite.
The background model has adopted the very thing it was supposed to see through - §5.1's failure arriving
by a different route, and one that no amount of extra observation fixes.

The replacement is a second background source, selected by
`BackgroundSource.SpatialNeighbourMedian`: the median of the pixel's eight neighbours, at two radii, **in
the same frame**. It works because a crosshair is thin. A few pixels away from any point on its stroke
there is ordinary scene, and from most points on a stroke most directions leave it - for a pixel on a
horizontal arm, the two samples along the arm land on it and the other six do not. Two radii rather than
one so that a stroke of unexpected width still leaves a majority of samples off it.

The sample is only a valid stand-in while the scene is smooth at that scale. The error is the curvature
term of the scene through the sampling ring, `d^2 * f'' / 2`, which is why the synthetic test uses a
gentle gradient rather than white noise: with a 9-pixel radius, a scene whose features are ~150 pixels
across contributes a few levels of error, whereas pixel noise would contribute its full amplitude.

Verified with ground truth: on a scene that changes, a synthetic crosshair comes back with **exact** stroke
alpha, **exact** stroke colour, a fringe-alpha mean error of 0.004, and **0 false positives** across
16 019 background pixels. On a scene that does not change, alpha is genuinely unidentified - and the
answer is still correct for the opaque parts, because an opaque pixel's composite does not depend on what
is behind it - so the tool answers and says which parts are assumptions rather than refusing.

Two consequences worth stating:

* **The refusal rule differs by mode.** In temporal mode, "the pointer never moved and nothing was solved"
  is a refusal, because then nothing in the observation carried information. In spatial mode that test
  cannot apply, because the geometry does not move by design; coverage is instead backed by the direct
  evidence of a consistent difference from the surrounding scene.
* **Scene-change detection is disabled in spatial mode.** It exists to invalidate a temporal model whose
  history describes content that is no longer on screen. In spatial mode there is no history to
  invalidate, and a busy scene would trip the detector constantly - suppressing pair recording exactly
  when the scene is giving the solver the most to work with.

## 11. Known limits of crosshair mode

* **A stroke wider than the sampling radius is not recoverable**, because the "background" samples land on
  the crosshair itself. `crosshairneighbourdistance` is a setting for exactly this reason, and its useful
  value is bounded by the ROI: at some point the samples leave the square.
* **The centre of the cross, where the arms meet**, has fewer valid directions than the arms. With
  sampling in eight directions the diagonals still leave the crosshair, but a shape that is thick in all
  directions at one point - a filled dot rather than a cross - defeats the method there.
* **A crosshair that is not at the centre of its window** is not found, since the watch square is
  positioned by the window rect. Games that offset the crosshair for a weapon's sight picture would need
  an offset setting.
* **A window that moves or resizes** restarts the accumulation, because an offset must always refer to the
  same screen pixel for the statistics to mean anything.

## 12. What crosshair mode still cannot tell apart, and the honest status

The spatial background makes a crosshair solvable, and the synthetic test recovers one exactly. On real
content it is nevertheless **not yet trustworthy**, and the reason is worth stating precisely because it is
not a tuning problem.

A crosshair and a stroke of text are the same thing to every test that can be applied to the pixels:

* both are thin, so both differ from a neighbourhood median;
* both are high-contrast;
* both are static in screen space if the text is not scrolling, so both are "covered" in every frame.

Three guards were added and each rejects part of the problem, but none of them is the right test:

| Guard | What it rejects | What it misses |
| --- | --- | --- |
| Coverage rate ≥ 0.85 | Scene structure that drifts through the watched square | Scene structure that is stationary on screen |
| Majority of covered pixels' alpha identified | Scene structure over a static background | Scene structure over something that is moving |
| Refusal when nothing was solved at all | A completely still scene | A scene with one animating pixel |

Measured on a text editor, which contains no crosshair: 193x193 and 193x94 "crosshairs" were produced by
successive versions, and the third guard above is what finally rejected them in the run that was measured.
The outcome depends on what is happening on screen, which is exactly the property an unreliable detector
has.

**The right test is motion**, and it is the one a person uses without thinking: a crosshair is the part of
the frame that does *not* move while the rest of it does. Text moves with the scene because it *is* the
scene. Estimating a coarse local motion field over the watched square and keeping only the pixels whose
motion disagrees with their neighbourhood would replace all three guards above rather than join them.

Until that exists, crosshair mode should be understood as a working demonstration of the solver on a fixed
overlay rather than a dependable tool, and it is listed as such in `ROADMAP.md`. The alternative that makes
the question disappear is a Windows.Graphics.Capture backend, which supplies the background directly and
needs no inference at all.

## 13. The observation view: three panels, one grid, and three coordinate spaces

The reconstruction is a process rather than a result, and a process is hard to argue with in a bug report.
The observation view exists so that the process can be looked at: three panels, refreshed on every captured
frame, plus a recording that can be paused, stepped and replayed.

| Panel | What it shows | Space |
| --- | --- | --- |
| Composite | The captured region as it arrived, cursor included | Screen |
| Background | The model's current estimate for each of those pixels, near-black where it has no samples yet | Screen |
| Model | Per-pixel classification as hue, recovered alpha as brightness | Screen, sampled through the pointer |

### The part that is easy to get wrong

There are **three** coordinate spaces in play, and they are all different sizes:

* **Screen space** - the captured region, `min(160, monitor)` square in cursor mode and `CrosshairSize`
  (256 by default) in crosshair mode.
* **The background model's space** - also screen coordinates, but it is the **union of every region observed
  so far**, so it grows as the pointer travels. A measured run with a 256-pixel region produced an
  accumulation area of 512 by 512.
* **Cursor-local offset space** - `KindMap` and the recovered cursor live here, anchored to the pointer
  hotspot rather than the screen, and bounded by `MaxOffsetRadius` (193 square at the default of 96).

The model is accumulated in the third space while the pixels being looked at are in the first. Sampling the
model through the current pointer position - `offset = pixel - hotspot` - puts all three panels in one grid.
That is what makes a replay comparable frame against frame, and it is what keeps a recording a fixed size.
The cost is that the model panel shows the model's opinion of the pixels **under the pointer**, not the whole
accumulated glyph at once. That is a deliberate trade, not an oversight: a view that showed the whole glyph
would have to rescale as the glyph's extent grew, so the same feature would move around the panel between
frames and two frames would not be comparable by eye.

### Reading the panel

| Hue | Classification |
| --- | --- |
| Green | alpha solved from background variation |
| Cyan | XOR-composited, written with the exact XOR mask |
| Orange | assumed opaque, because the background never varied underneath |
| Red | unresolved: too few pairs, or not enough background variance |
| Grey | never seen as foreground: the cursor does not cover this offset here |

Brightness is the recovered alpha, with a floor so that a nearly transparent edge is still visible as an edge.
The green region therefore does not merely appear, it **brightens** as alpha separates from the background -
which is what "progressively completed" looks like. Watching the first few frames, most of the panel is
orange and red; the green arrives as the scene moves under the pointer.

### Verified, not asserted

`rattrap selftest` checks that the model's coverage only ever grows, that recorded pairs never decrease
without a scene change, and - the one that matters most - that **refreshing the view does not change the
model**. The last of those guards a real hazard: the background model exposes an `Observe` that returns an
estimate while advancing its sample window, and a display that used it would be pushing the scene into the
model it is supposed to be reporting on, changing the result it draws at whatever rate the screen happened to
be repainted. Reading is therefore a separate entry point, and the test asserts the separation holds.

## 14. Why cursor mode usually has nothing to reconstruct, and crosshair mode always does

A reasonable question after watching the observation window: in cursor mode, even with the cursor captured
whole against a black background, the panels stay empty - so does cursor mode need the background-differencing
solver at all? The answer is no, and the reason is not an omission.

**The two modes do not divide as "cursor = compositor, crosshair = reconstruction". They divide as
"compositor first, reconstruction as the fallback", and crosshair can never use the first path.**

* In **cursor mode** the program asks the compositor first. When the operating system is drawing the cursor,
  `GetFramePointerShape` hands over the finished bitmap **with its alpha channel already in it**, exact and
  already scaled for DPI and the pointer-size setting. There is nothing to solve, so the solver never runs -
  which is why the observation panels are legitimately empty. The window says so rather than showing a void.
* Cursor mode **does** fall back to reconstruction when the compositor reports no shape for roughly twenty-five
  frames, which is what an application drawing its own cursor looks like. Then the solver runs and the panels
  fill exactly as they do in crosshair mode.
* **Crosshair mode** can never take the compositor path, because a crosshair is not a system cursor and the
  compositor has no bitmap to hand over. Reconstruction is the only option, so the panels always fill.

### Why one frame against a black background cannot give you alpha

This is worth stating precisely, because "I captured the whole cursor against pure black, so surely the alpha
is right there" is the intuitive reading and it is arithmetically wrong.

Every observation is one equation per channel:

```
D = alpha*C + (1 - alpha)*B
```

Against black, `B = 0`, so the observation collapses to `D = alpha*C`. That is **one product**, and alpha and C
cannot be separated from a single product - any pair `(alpha, C)` and `(alpha/k, k*C)` fits it equally well.
Infinite solutions, one equation. A second frame with the *same* black background changes nothing; you need a
second frame with a **different** background before the two can be told apart.

That is exactly what the compositor path avoids, and it is why it is preferred: the operating system reports
alpha and colour as separate values, so no differencing is required at all. Reconstruction exists for the case
where nobody will tell us, and then two distinct backgrounds are the minimum price - which is also why
`MinBackgroundVariance` exists, why a stationary pointer on a still scene is refused rather than guessed, and
why the observation view's alpha brightness only starts to appear once the scene has moved underneath.

### One more consequence, for the observation window specifically

The compositor reports a shape when it **changes**. A cursor that was already on screen before the capture
started may therefore report nothing until it moves, which is why the session reopens the duplication session
and can nudge the pointer one pixel. In cursor mode this means the observation window can sit empty for a
moment even when reconstruction will eventually run - the panels are not broken, they are waiting.
