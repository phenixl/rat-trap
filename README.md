# rat-trap

Recover a cursor — colour and alpha — as a PNG, `.cur` or `.ani`, from screen capture on Windows.

Written as a single self-contained Native AOT executable with **zero package dependencies**, so it
builds and runs on a machine with no network access and no .NET runtime installed.

```
rattrap.exe capture --out out\ --preview out\preview.png
```

```
Cursor obtained via DxgiPointerShape: 89x87, hotspot (3,3), encoding AlphaBlend32
  alpha: 40 opaque, 1081 partial, 6622 transparent
Writing:
  out\cursor.cur  (.cur, AlphaBlend32, hotspot (3,3))
  out\cursor.png  (.png, 89x87, straight alpha RGBA)
  out\cursor.ani  (.ani, single frame)
```

---

## Why this is not a one-line `GetIconInfo` call

There are two different situations, and they need different machinery. Which one applies is a
property of the program under the pointer, not a user preference.

| | Who draws the cursor | How rat-trap gets it |
| --- | --- | --- |
| **Compositor** | The operating system (desktop, most windowed apps, borderless games) | The composition engine's own bitmap, via Desktop Duplication `GetFramePointerShape`. Exact, already scaled for DPI and the accessibility pointer-size setting. ~1 second. |
| **Reconstruction** | The application itself (exclusive-fullscreen games drawing a crosshair, remote-desktop clients rendering a remote pointer) | Solved from the captured pixels. The cursor is only present as pixels, so alpha and colour are recovered from how the cursor and the scene vary independently. |

Two measurements on the development machine shape this design, and both contradict the obvious
assumption:

1. **Neither `BitBlt` nor Desktop Duplication contains the system cursor.** Capturing the same
   128×128 region with both at the same moment produced **0 of 16384 differing pixels**; the DWM
   composites the cursor on its own plane and neither API reads it. So there is no
   "composite source / clean background source" pair to exploit — but there is also nothing to
   reconstruct for a system cursor, because the compositor hands the bitmap over directly.
2. **A cursor an application draws is ordinary pixels in that application's own frame**, so it
   *is* present in the duplication image. That is exactly the case the solver is for.

Full evidence, with the raw numbers, is in [docs/measured-behaviour.md](docs/measured-behaviour.md).

---

## Build

Requires the .NET 9 SDK, MSVC build tools and a Windows SDK for Native AOT. No network access is
needed: `nuget.config` clears every package source and the project references no packages.

```powershell
dotnet build   src\RatTrap\RatTrap.csproj -c Release
dotnet publish src\RatTrap\RatTrap.csproj -c Release -r win-x64 -o publish
```

Produces a single `publish\rattrap.exe` of roughly 2 MB.

> **If `dotnet build` fails with `MSB3883` / `Access is denied` from `Microsoft.CSharp.Core.targets`:**
> that is a sandbox denying *named pipes*, not a file permission. MSBuild's compiler task and the
> Roslyn server both use named pipes to capture child-process output. `Directory.Build.props` already
> sets `UseSharedCompilation=false` to keep compilation in-process; if it still fails, the build needs
> a less confined environment, because the restriction is on named pipes rather than on files.

## Commands

| Command | What it does |
| --- | --- |
| `rattrap capture` | Acquire a cursor and write `.cur`, `.png` and `.ani`. |
| `rattrap probe` | Inspect monitors, the live cursor, Desktop Duplication and the interop layer. Also runs the GDI-vs-DXGI comparison that established the behaviour above. |
| `rattrap selftest` | 75 deterministic checks: interop structure sizes and vtable slot arithmetic, the reconstruction mathematics against synthetic ground truth, and the PNG/CUR/ICO/ANI writers. |
| `rattrap gui` | The interactive window. `--selfcheck` runs it headlessly and writes a report. |
| `rattrap help` | Options. |

Useful options:

| Option | Meaning |
| --- | --- |
| `--out <dir>` | Where to write artefacts (default `rattrap-out`). |
| `--source auto\|compositor\|reconstruct` | Which acquisition strategy to use. `auto` inspects what is actually available. |
| `--backend auto\|dxgi\|gdi` | Which capture backend to use. |
| `--seconds <n>` | Observation budget. |
| `--roi <px>` | Capture window size around the pointer. |
| `--preview <png>` | Also write the cursor over a checkerboard, so transparency is visible. |
| `--trim` | Shrink the output to the alpha bounding box. |
| `--no-nudge` | Never touch the pointer. |
| `--verbose` | Per-frame detail, and the model map for a reconstruction. |

## Verifying a result

`tools/verify_cursor_output.py` re-derives the PNG, CUR and ANI layouts from the format definitions
in Python and cross-checks the three files against each other. It shares no code with the program or
its self-test, so a bug consistent between the writer and its own round-trip parser still shows up.

```powershell
python tools\verify_cursor_output.py out\
```

```
  ok   png chunk 1 (IDAT) CRC
  ok   cur XOR plane equals png RGBA byte for byte
  ok   cur AND mask marks exactly the transparent pixels
  ok   ani frame 0 is byte-identical to the .cur
45 checks, 0 failure(s)
```

Note that this host's cursor *loader* is not pixel-preserving for any `.cur` (a first-party control
file comes back with 551/4096 bytes changed and every `.cur` is rescaled to 32×32), which is why the
pixel proof above is done against the file bytes rather than by loading the cursor back.

## How the reconstruction works

Short version — for each cursor-local offset the solver fits

```
D_c = (1 - alpha) * B_c + alpha * C_c
```

across observations with a **single slope shared by all three colour channels**, which has a
closed-form solution. Background `B` comes from a per-pixel median over a sliding window of recent
frames, which is what makes an idle-looking screen useful as long as the pointer moves.

The full derivation, what is provably unrecoverable, and the two failed approaches that were replaced
are in [docs/algorithm.md](docs/algorithm.md).

## Repository layout

```
src/RatTrap/
  Interop/        Win32 P/Invoke and hand-written DXGI/D3D11 vtable calls (AOT has no built-in COM)
  Capture/        DXGI Desktop Duplication, GDI BitBlt, monitor and DPI enumeration
  Pointer/        GetIconInfo reader, DXGI pointer-shape decoder, hotspot anchor resolution
  Reconstruct/    Background model, sufficient statistics, the alpha/XOR solver
  Imaging/        ArgbBitmap, CursorImage, 1bpp mask packing
  Output/         PNG, CUR/ICO and ANI writers
  Ui/             Win32 window (WinForms and WPF are not AOT-compatible)
  Cli/            Command line, probe and capture pipelines
  SelfTest/       Deterministic verification
tools/            Independent Python verifier for the output files
docs/             Measured behaviour and the algorithm derivation
probe/ _research/ The original C++ probes and fetched documentation this started from
ROADMAP.md        Dynamic cursors, motion-compensated backgrounds, and what is known not to work
```

## Licence and intended use

This is a capture tool. It reads the screen of the machine it runs on, with the privileges of the
user who runs it, and writes image files. It does not inject input, does not hide itself, does not
persist, and does not attempt to defeat any protection; it will simply fail on DRM-protected or
hardware-overlay content. Use it on machines you are authorised to inspect.
