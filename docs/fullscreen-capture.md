# Capturing a cursor in a fullscreen game

[**English**](fullscreen-capture.md) | [中文](fullscreen-capture.zh-CN.md)

This is the investigation behind the hardest open question in the project: a fullscreen game's cursor is
not captured, and the obvious ideas for getting it do not survive contact with the documentation. This
file records what was checked, what the answer is, and what is worth building.

## 1. The symptom, stated precisely

In windowed and borderless mode the cursor is captured. In exclusive fullscreen it is not. Three
different mechanisms can produce that, and they need different answers:

| Cause | What is actually happening | Fixable? |
| --- | --- | --- |
| **Application-drawn cursor** | The game called `ShowCursor(FALSE)` and paints its own crosshair into its own frame, so the cursor is ordinary pixels | Yes - it is the case the reconstruction solver exists for |
| **Hardware overlay / MPO plane** | The cursor is composited on a separate plane that capture APIs do not read | Yes, via a different API: see §3 |
| **Capture protection** | The window called `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, or the content is DRM-protected | **No** - by design, and no privilege level changes it |

The tool now distinguishes these at runtime rather than reporting a generic failure: it reports
protected content when the compositor says so, and it reports a flat-colour capture area together with
the three likely causes (see `DiagnoseCapture` in `CaptureSession.cs`).

## 2. The Magnifier idea does not work, and the documentation says why

The suggestion was that the Magnifier "breaks" the hardware overlay, forcing the cursor to be
demoted to a software cursor and composited into the main framebuffer, and that this is why the
Magnifier can show a cursor that screen capture cannot.

The first half of that is not what the API does. From
[MagShowSystemCursor (magnification.h)](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowsystemcursor),
the entire documented behaviour is:

> Shows or hides the system cursor.

> This function does not associate a reference count with the visibility state of the system cursor.
> Instead, the specified visibility state takes effect immediately, regardless of any previous calls to
> `MagShowSystemCursor`.

> The system cursor is always magnified when it is shown while the full-screen magnifier is active.

> When used with a magnifier control, calls to `MagShowSystemCursor` have no effect on the magnified
> system cursor. The visibility of the magnified system cursor depends on whether the magnifier control
> has the `MS_SHOWMAGNIFIEDCURSOR` style.

There is no demotion of a hardware cursor to software, and no documented interaction with how the
composition engine renders the cursor. What the magnifier actually does is simpler and less useful:
with `MS_SHOWMAGNIFIEDCURSOR` it **draws the cursor into its own window**, using the cursor bitmap and
position which it can read for itself. That is consistent with this project's own earlier probes, where
the magnifier's output contained the cursor when the style was set and did not when it was not
(`probe/out/mag_run2.txt`).

So the Magnifier would only help in the form of "capture the magnifier's window", which is worse than
the API it would replace: it adds a window that cannot be shown over an exclusive-fullscreen game, its
scaling callback (`MagSetImageScalingCallback`) is deprecated and was measured firing zero times on this
machine, and it needs the cursor bitmap in the first place - which, when the compositor is the one
drawing it, we already have directly.

## 3. The answer: Windows.Graphics.Capture already solves this

There is a public, documented, non-hooking API that includes the cursor in the captured content, and it
is the one the operating system's own screen recording uses: **Windows.Graphics.Capture (WGC)**.

[GraphicsCaptureSession.IsCursorCaptureEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled):

> Gets or sets a value specifying whether the capture session will include the cursor in the captured
> content.

> `bool` - True if the cursor should be captured; otherwise, false.

> Device family: Windows 10, version 2004 (introduced in 10.0.19041.0)

This is the missing half of this project, and it is worth being precise about why.

`docs/measured-behaviour.md` §3 records the measurement that shaped the whole architecture: on this
machine neither GDI `BitBlt` nor Desktop Duplication returns the cursor, and the two agree pixel for
pixel over a 128x128 region (0 of 16384 bytes differing). The original design assumed one backend would
include the cursor and the other would not, and the alpha solve was supposed to exploit that pair. The
pair does not exist.

WGC provides it:

* `IsCursorCaptureEnabled = true` (the **default**) gives a **composite**: the frame includes the cursor
  drawn over the scene.
* `IsCursorCaptureEnabled = false` gives a **clean background plate**.

Those two are the `D` and the `B` of the model in `docs/algorithm.md`, observed for the same instant,
without needing the cursor to move and without needing the background to vary. Alpha stops being a
search problem and becomes a two-equation solve:

```
D = alpha*C + (1 - alpha)*B     and     B known exactly
```

which for a pixel covered in a single frame pair leaves one equation in two unknowns, but across two
or more pairs with different `B` it is exactly determined - and unlike the current method, the pairing
is trustworthy because both observations are of the same moment rather than of the same background
"probably".

Secondary advantages, all relevant to the fullscreen case:

* It captures a **monitor** or a **window**, so a windowed game can be captured without the rest of the
  desktop.
* It is the API the Game Bar and Snipping Tool use, so it is the path with the most real-world testing
  against games.
* It is not subject to the per-output duplication limit, so it can run alongside another duplication
  consumer such as OBS display capture.
* No hooking, no driver, no injection: nothing for an anti-cheat to object to. This directly addresses
  the concern that motivated the question.

### What it does not solve

`WDA_EXCLUDEFROMCAPTURE` wins against WGC too. From
[SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity):

> `WDA_EXCLUDEFROMCAPTURE` 0x00000011 - The window is displayed only on a monitor. Everywhere else, the
> window does not appear at all.

> This function and `GetWindowDisplayAffinity` are designed to support the window content protection
> feature that is new to Windows 7. This feature enables applications to protect their own onscreen
> window content from being captured or copied through a specific set of public operating system
> features and APIs.

Note also that `GetWindowDisplayAffinity` requires the window to belong to the *current process*, so
another application's affinity cannot be read; the tool detects the symptom instead (a flat capture
area).

DRM-protected output is likewise excluded, and duplication reports it explicitly through
`DXGI_OUTDUPL_FRAME_INFO.ProtectedContentMaskedOut`, which this project now surfaces to the user.

## 4. What implementing WGC would take

The blocker is not conceptual, it is packaging. WGC is WinRT, and this project has **zero package
references** because `nuget.config` clears every source (the build machine has no working TLS egress to
nuget.org, and the project is deliberately buildable offline). The normal route - the C#/WinRT
projections in `Microsoft.Windows.SDK.NET.Ref` - is a package.

The alternative is the same approach already taken for DXGI in this repository: declare the COM
interfaces by hand and call the vtables directly. The pieces needed are:

| Piece | Where it comes from |
| --- | --- |
| `RoGetActivationFactory`, `WindowsCreateString`, `WindowsDeleteString`, `WindowsGetStringRawBuffer` | `combase.dll`, flat exports |
| `IGraphicsCaptureItemInterop` (`CreateForMonitor`, `CreateForWindow`) | declared locally, IID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356` |
| `IGraphicsCaptureItem` (`DisplayName`, `Size`, `Closed`) | WinRT, activated by name |
| `IDirect3D11CaptureFramePoolStatics2` (`CreateFreeThreaded`), `IDirect3D11CaptureFramePool` (`Recreate`, `TryGetNextFrame`, `FrameArrived`) | WinRT statics via the activation factory |
| `IDirect3D11CaptureFrame` (`SystemRelativeTime`, `ContentSize`, `Surface`) | WinRT |
| `IGraphicsCaptureSession` (`StartCapture`, `Close`, and `IsCursorCaptureEnabled` on its Windows 10 2004 successor interface) | WinRT, plus a `QueryInterface` for the newer interface |
| `IDirect3DDxgiInterfaceAccess` (`GetInterface`) | declared locally, to get the `ID3D11Texture2D` out of the frame's `Surface` |
| An `ID3D11Device` | already present in `DxgiDuplicationSource` |

Roughly 300-400 lines, in the same style as `src/RatTrap/Interop/DxgiInterop.cs`, with the same
absolute-vtable-index audit table and the same step-by-step diagnosis hook that caught the two slot
errors during the DXGI work. The frame pool can be polled with `TryGetNextFrame` on the existing timer
tick instead of subscribing to `FrameArrived`, which avoids needing a dispatcher queue and keeps the
whole thing on the UI thread - consistent with how every other backend in this project works.

It is a self-contained piece of work with one external dependency (the WinRT activation path) and one
verifiable acceptance test: capture the desktop monitor with `IsCursorCaptureEnabled` set both ways and
confirm that the cursor appears in exactly one of the two.

**That acceptance test is the reason this is worth doing first, before anything else on the roadmap**:
it would be the first time this project has a composite and a background for the same instant, and the
rest of the solver becomes both simpler and more accurate as a result.

## 5. Recommendation, in order

1. **Implement WGC as a third backend**, and use its cursor-included and cursor-excluded modes as the
   composite/background pair. This is the direct answer to the fullscreen question, needs no hooking,
   and improves accuracy everywhere, not just in fullscreen.
2. **Keep duplication as the fallback** for machines where WGC is unavailable (Windows 10 before 2004,
   or an activation failure). It already works, and it already reports the pointer shape exactly.
3. **Do not pursue the Magnifier route.** The documentation does not support the mechanism it relies on,
   its one useful callback is deprecated, and it is strictly more complex for strictly less.
4. **Do not pursue driver-level or API hooking.** It is unnecessary now that WGC exists, and it is the
   one approach that plausibly *would* attract an anti-cheat's attention - which is the opposite of what
   was asked for.
5. **Accept the protected-content case.** `WDA_EXCLUDEFROMCAPTURE` and DRM are deliberate, documented,
   and not a privilege problem. Running as administrator does not defeat them, and neither does anything
   else short of photographing the screen. The tool now says so instead of failing silently.
