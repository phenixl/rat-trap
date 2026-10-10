# rat-trap

[**English**](README.md) | [中文](README.zh-CN.md)

Recover a cursor — or a game's crosshair — as a PNG, `.cur` or `.ani`, from screen capture on Windows.

A single self-contained Native AOT executable with **zero package dependencies**, so it builds and runs
on a machine with no network access and no .NET runtime installed.

```
rattrap.exe capture --out out\
```

```
Cursor obtained via DxgiPointerShape: 89x87, hotspot (3,3), encoding AlphaBlend32
  alpha: 40 opaque, 1081 partial, 6622 transparent
Writing:
  out\cursor.cur  (.cur, AlphaBlend32, hotspot (3,3))
  out\cursor.png  (.png, 89x87, straight alpha RGBA)
  out\cursor.ani  (.ani, single frame)
```

Or run the GUI (`rattrap.exe` with no arguments), press the global hot key, and the files appear on
their own.

---

## Why this is not a one-line `GetIconInfo` call

There are two different situations, and they need different machinery. Which one applies is a property
of the program under the pointer, not a user preference.

| | Who draws the cursor | How rat-trap gets it |
| --- | --- | --- |
| **Compositor** | The operating system (desktop, most windowed apps, borderless games) | The composition engine's own bitmap, via Desktop Duplication `GetFramePointerShape`. Exact, already scaled for DPI and the accessibility pointer-size setting. ~1 second. |
| **Reconstruction** | The application itself (a game drawing a crosshair, a remote-desktop client rendering a remote pointer) | Solved from the captured pixels. The cursor exists only as pixels, so alpha and colour are recovered from how the cursor and the scene vary independently. |

Two measurements on the development machine shape this design, and both contradict the obvious
assumption:

1. **Neither `BitBlt` nor Desktop Duplication contains the system cursor.** Capturing the same 128×128
   region with both at the same moment produced **0 of 16384 differing pixels**; the DWM composites the
   cursor on its own plane and neither API reads it. So the "composite source / clean background source"
   pair that the original design wanted does not exist — but there is also nothing to reconstruct for a
   system cursor, because the compositor hands the bitmap over directly.
2. **A cursor an application draws is ordinary pixels in that application's own frame**, so it *is*
   present in the duplication image. That is exactly the case the solver is for.

Full evidence, with the raw numbers, is in [docs/measured-behaviour.md](docs/measured-behaviour.md).
For the fullscreen case specifically, including why the Magnifier idea does not work and which public API
does, see [docs/fullscreen-capture.md](docs/fullscreen-capture.md).

---

## Build

Requires the .NET 9 SDK, MSVC build tools and a Windows SDK for Native AOT. No network access is needed:
`nuget.config` clears every package source and the project references no packages.

```powershell
dotnet build   RatTrap.sln -c Release
dotnet publish src\RatTrap\RatTrap.csproj -c Release -r win-x64 -o publish
```

Produces a single `publish\rattrap.exe` of roughly 2 MB, signed with the shared personal code-signing
certificate when one is present. Build environment traps, signing, and the administrator question are all
covered in [docs/development.md](docs/development.md).

## Commands

| Command | What it does |
| --- | --- |
| `rattrap gui` | The interactive window (default with no arguments). |
| `rattrap capture` | Acquire a cursor and write `.cur`, `.png` and `.ani`. |
| `rattrap probe` | Inspect monitors, the live cursor, Desktop Duplication and the interop layer. Also runs the GDI-vs-DXGI comparison that established the behaviour above. |
| `rattrap selftest` | 111 deterministic checks: interop structure sizes and vtable slot arithmetic, the reconstruction mathematics against synthetic ground truth, and the PNG/CUR/ICO/ANI writers. |
| `rattrap help` | Options. |

## The window

| Control | Behaviour |
| --- | --- |
| **Target: Cursor / Crosshair** | Radio pair. `Cursor` follows the pointer. `Crosshair` watches a fixed square (256 px by default) at the centre of the foreground window — for a first-person shooter, where the crosshair does not move and the scene moves behind it. |
| **Start / Stop** | One button whose label states what pressing it does next. |
| **Open folder** | Opens the save directory in Explorer. |
| **Self-test** | Runs the same 111 checks the command line does. |
| **Global hot key** | `Ctrl+Insert` by default, changeable in `rat-trap.ini`. Works while a game has focus, which is the point: pressing it does not steal focus from the game. |
| **View: Delivery / Live/replay** | A second radio pair on the same row. `Delivery` is the recovered cursor over a checkerboard, hotspot marked. `Live/replay` shows the reconstruction's own progress - see below. |
| Transport (in the canvas) | Drawn inside the preview when `Live/replay` is selected: jump to oldest, step back, pause/resume, step forward, jump to newest. Space and the arrow keys do the same. Pausing freezes the *view*; the capture keeps recording, so the replay keeps growing behind it. |
| Log | Each new line is appended and scrolled into view. |

A capture saves itself. There is no export button, because a cursor that has been acquired and had its
alpha separated is finished work, and asking the user to press three more buttons is three chances to
lose it. Files are named `[hotspotX,hotspotY]_NNNN` so a re-run never overwrites an earlier capture, and
the completion sound is the system's "new fax notification" event (`FaxBeep`), so it follows whatever
sound scheme the machine uses.

## Settings

`rat-trap.ini`, stored **beside the executable** so the whole program is portable and every value can be
edited in a text editor. If the program directory is not writable — unpacked under `Program Files`, for
instance — it falls back to `%LOCALAPPDATA%\rat-trap` and says so in the log rather than silently losing
the change.

```ini
[capture]
target = cursor            ; or crosshair
savedirectory = captures   ; relative paths resolve against the program directory
autosave = true
playsound = true
; three cues, compiled into the program: one when a capture starts, a different one when it stops,
; and a third when a result is saved. 'builtin' plays the embedded cue; anything else is a .wav path.
startsound = builtin
stopsound = builtin
savesound = builtin
reconstructseconds = 8
trim = false
nudgepointer = true

[crosshair]
crosshairsize = 256
crosshairneighbourdistance = 9

[hotkey]
hotkey = Ctrl+Insert
hotkeyenabled = true
```

## Verifying a result

`tools/verify_cursor_output.py` re-derives the PNG, CUR and ANI layouts from the format definitions in
Python and cross-checks the three files against each other. It shares no code with the program or its
self-test, so a bug consistent between the writer and its own round-trip parser still shows up.

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

Note that this host's cursor *loader* is not pixel-preserving for any `.cur` (a first-party control file
comes back with 551/4096 bytes changed and every `.cur` is rescaled to 32×32), which is why the pixel
proof above is done against the file bytes rather than by loading the cursor back.

## How the reconstruction works

Short version — for each cursor-local offset the solver fits

```
D_c = (1 - alpha) * B_c + alpha * C_c
```

across observations with a **single slope shared by all three colour channels**, which has a closed-form
solution. Background `B` comes from one of two places:

* **A per-pixel median over a sliding window of recent frames** when the cursor moves. It covers any
  given pixel only part of the time, so the majority of the window is genuine background.
* **A median of the pixel's neighbourhood in the same frame** for a crosshair, which never moves. The
  temporal window would fill entirely with composite values and adopt the crosshair as its own
  background; a neighbourhood sample works instead because a crosshair is thin, so a few pixels away
  there is ordinary scene.

The full derivation, what is provably unrecoverable, and the failed approaches that were replaced are in
[docs/algorithm.md](docs/algorithm.md).

## Running as administrator

**Not required, and not requested by the manifest.** Screen capture reads the composited desktop, which
belongs to the session rather than to a process, so an unelevated program reads elevated windows
perfectly well. Elevation does not defeat `WDA_EXCLUDEFROMCAPTURE` or DRM either — nothing does. See
[docs/development.md](docs/development.md) for what elevation would and would not change.

## Repository layout

```
src/RatTrap/
  Interop/        Win32 P/Invoke and hand-written DXGI/D3D11 vtable calls (AOT has no built-in COM)
  Capture/        DXGI Desktop Duplication, GDI BitBlt, monitor and DPI enumeration
  Pointer/        GetIconInfo reader, DXGI pointer-shape decoder, hotspot anchor resolution, pointer nudge
  Reconstruct/    Background models, sufficient statistics, the alpha/XOR solver
  Imaging/        ArgbBitmap, CursorImage, 1bpp mask packing
  Output/         PNG, CUR/ICO and ANI writers
  Settings/       Portable INI preferences
  Ui/             Win32 window and the capture session behind it
  Cli/            Command line, probe and capture pipelines
  SelfTest/       Deterministic verification
  Audio/          The three feedback cues, as embedded WAV images
tools/            Independent Python verifier, icon generator, sound builder, signing and publish scripts
docs/             Algorithm, measured behaviour, fullscreen capture, development notes
THIRD-PARTY-NOTICES.md   The CC0 licence and provenance of the embedded feedback cues
probe/            The original C++ probes this started from, and their text logs as evidence
```

Every document exists in English as `NAME.md` and in Chinese as `NAME.zh-CN.md` - including `THIRD-PARTY-NOTICES.md` at the root, which records where the embedded audio came from and under what licence. Inside a Chinese document,
a reference to another document means the Chinese edition of it, so cross-references carry the `.zh-CN`
suffix; source paths and URLs are never rewritten.

## Licence and intended use

This is a capture tool. It reads the screen of the machine it runs on, with the privileges of the user
who runs it, and writes image files. It does not inject input, does not hook or patch another process,
does not hide itself, does not persist, and does not attempt to defeat any protection; it will simply
fail on DRM-protected or capture-protected content, and say why. Use it on machines you are authorised to
inspect.







