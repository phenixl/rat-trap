# Recovering a cursor's colour and alpha from screen captures

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
