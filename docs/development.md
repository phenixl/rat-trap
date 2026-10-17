# Building, signing, and the things about this machine that will bite you

[**English**](development.md) | [中文](development.zh-CN.md)

Notes for whoever builds this next, including future me. Every item here cost time to discover.

## Build

```powershell
dotnet build   RatTrap.sln -c Release
dotnet publish src\RatTrap\RatTrap.csproj -c Release -r win-x64 -o publish
```

Produces `publish\rattrap.exe`, a self-contained Native AOT binary of about 2 MB. Nothing needs to be
installed on the target machine.

Requirements for the *build* machine: the .NET 9 SDK, MSVC build tools, and a Windows SDK. Native AOT
needs the MSVC linker; `vswhere` locating `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` is the
quick check.

## Zero package references, and why that is not an affectation

`nuget.config` clears every package source on purpose. On the machine this was developed on, TLS egress
to `nuget.org` did not work, so a restore that consults the network hangs and then fails. With all
sources cleared, restore resolves purely from `%USERPROFILE%\.nuget\packages`, where the SDK's own
Native AOT packs already live.

The consequences are worth knowing before adding a dependency:

* `[LibraryImport]` for every Win32 call, never `[DllImport]` with runtime marshalling.
* Hand-written COM vtable calls for DXGI and D3D11, because the built-in COM interop is unavailable
  under AOT (see `docs/algorithm.md` and the audit table at the top of `src/RatTrap/Interop/DxgiInterop.cs`).
* No `System.Text.Json`; settings are an INI file parsed by hand.
* No WinForms and no WPF, which are not AOT-compatible. The GUI is raw Win32.

If a dependency ever becomes necessary, re-add a package source to `nuget.config` first, and expect the
AOT compatibility of that package to become your problem.

## Build-environment traps

Deliberately not numbered: the count kept going stale as more were found, which is a small version of the
same problem this whole document exists to prevent.

**A running instance blocks the linker.** If a `rattrap.exe` from this repository is still running, the next
build fails with

```
LINK : fatal error LNK1104: cannot open file "bin\Release\net9.0-windows\win-x64\native\rattrap.exe"
```

That message names a file nobody is consciously using and reads as a permissions or disk problem. It is
neither. Windows locks a mapped executable image, so an instance started from `bin\...\native\` holds the
very file the native linker is trying to write. The same instance is also holding the global hot key, so a
second one cannot register it. `tools/publish.ps1` checks for this before building and names the pid and
path instead; pass `-StopRunning` to have it clear them.

**Named pipes.** `dotnet build` can fail with `MSB3883` and `Access is denied` thrown from
`Microsoft.CSharp.Core.targets`. That is not a file permission problem. MSBuild's compiler task and the
Roslyn server both capture child-process output through *named pipes*, and a confined sandbox can forbid
named pipes while permitting file writes. `Directory.Build.props` sets `UseSharedCompilation=false` so
compilation stays in process, which fixes the common case; if it still fails, the build needs a less
confined environment, because no file permission will change it.

**Piping `dotnet` output.** In the same kind of confined environment, piping `dotnet`'s output
(`dotnet build ... | Select-String ...`) can fail with an access-denied error when the unpiped form
works. Run `dotnet` without a pipe, or redirect to a file.

**Piping a GUI process.** PowerShell does not wait for a GUI-subsystem process, so
`.\rattrap.exe capture ...` returns before the work is done and `$LASTEXITCODE` is empty. Use
`Start-Process -PassThru` plus `Wait-Process -Id`, or `cmd /c start /b` followed by polling for the
program's own output file. `Start-Process -Wait` itself hangs on this host for a process that creates a
window, which is why the GUI has `--selfcheck --selfcheck-out <file>` and why an automated check should
read that report rather than depend on the wait.

**`%TEMP%` may not be writable.** Under a confined environment a child process can be refused writes to
`%TEMP%` while the program directory is writable. `OutputSelfTest` therefore probes `%TEMP%`, then the
program directory, then the current directory, and reports which one it used.

## Code signing

The certificate lives **outside** the repository, in a folder shared by every project on this machine. That
folder is named by the `certs_path` user environment variable, and it is not hard-coded anywhere in this
repository.

```powershell
[Environment]::GetEnvironmentVariable('certs_path', 'User')   # where the certificate folder is
```

```
<the folder certs_path names>\
    phenixl-codesign.pfx              private key, password protected
    phenixl-codesign.pfx.password.txt the password, because a build agent needs it unattended
    phenixl-codesign.cer              public part, for installing as trusted
    phenixl-codesign.thumbprint.txt   what the build reads to find it in the certificate store
```

> The path used to be a literal in the project file. When the folder was moved, the literal went stale,
> and the consequence was not an error: signing silently degraded to a skip, and the only evidence was a
> single line in a build log. An environment variable is one place to change and cannot drift out of step
> with the machine. If `certs_path` is unset, the build now says so explicitly rather than quietly
> producing an unsigned binary.

`tools/sign.ps1` signs a binary, preferring the certificate store by thumbprint (no password needed) and
falling back to the `.pfx` plus its password, which is what a build agent uses. It resolves the certificate
folder in this order: `-CertDir`, then `certs_path` from the process environment, then `certs_path` from the
**registry's user scope**. That last step matters more than it looks. An environment variable created after a
shell was started is absent from that shell, so the terminal or IDE someone already has open will not have
it — and a build run from there must still sign.

The `publish` target therefore always invokes the script (unless `SignOutput=false`) and lets it decide,
rather than asking MSBuild whether a certificate exists. MSBuild only sees environment variables that were
present when its process started, so it cannot answer that question reliably, and a wrong answer is silent.
A missing certificate produces one visible `SKIPPED` line in the build log:

```powershell
dotnet publish ... -p:SignOutput=false             # never sign
dotnet publish ... -p:RatTrapSigningDir=D:\certs    # a specific folder, beating certs_path
```

Signing is best effort: a missing certificate produces that one line, not a failed build. Pass `-Require` to
`tools/sign.ps1` instead when a release must never ship unsigned — `tools/publish.ps1` is the easier way to be
sure, since it reads the signature back off the published file.

### The signing hook, and why it is on `CopyNativeBinary`

Hooking the signing target to `AfterTargets="Publish"` does not work, and it fails silently: the build log
reads `Successfully signed` while the published binary carries no signature at all.

The cause is in the ILCompiler pack, `Microsoft.NETCore.Native.Publish.targets`:

```xml
<Target Name="CopyNativeBinary" AfterTargets="Publish">
  <Delete Files="$(PublishDir)\$(TargetName)$(NativeBinaryExt)" />
  <Copy SourceFiles="$(NativeOutputPath)$(TargetName)$(NativeBinaryExt)" DestinationFolder="$(PublishDir)" />
</Target>
```

That target is *also* an `AfterTarget` of `Publish`, and NuGet targets are imported after the project's own,
so it runs **after** anything the project hooks to `Publish`: it deletes the signed binary and copies the
unsigned native one back over it. MSBuild's `Copy` preserves the source file's timestamp, so the file's
timestamp does not even move, and the only symptom is a signature that is not there. It cost an afternoon to
find, because every observable signal said the opposite of the truth.

`CopyNativeBinary` is the last writer, so it is where the hook belongs. And because a log line is not
evidence, `tools/publish.ps1` publishes, signs, then **reads the signature back off the published file** and
runs that binary's own self-test before reporting success:

```powershell
pwsh tools\publish.ps1                  # release gate: build, sign, verify, self-test
pwsh tools\publish.ps1 -RequireSignature # fail instead of warning when unsigned
pwsh tools\publish.ps1 -StopRunning      # clear a running instance that would block the linker
```

### What signing does and does not buy

It produces a verifiable, tamper-evident binary. Verified on the current release: `Status` reads `Valid`
because `phenixl-codesign.cer` is installed into `Cert:\CurrentUser\Root` and
`Cert:\CurrentUser\TrustedPublisher` on this machine, and flipping a single byte in a copy of the published
binary turns the status into `HashMismatch` — so the signature is not decoration.

What it does **not** buy is reputation, and the trust is per-machine and per-user. On a machine that has not
installed the `.cer`, the same binary reports `UnknownError`, "the certificate chain terminated in an
untrusted root". The signature is still verifiable there and tampering is still detected; only the chain is
untrusted. SmartScreen will also warn about a downloaded copy.

Note the scope that is installed: user root and user trusted publishers, not `LocalMachine`. That is enough
for anything running as this user, and it needs no elevation; a machine-wide install or a build agent
running as another account needs the `.cer` added to the `LocalMachine` stores instead.

The private key is the valuable part. It is reusable across projects by design, and it must never be
committed: `.gitignore` excludes `*.pfx`, `*.pvk`, `*.snk`, `*.cer` and `*.key` for that reason. The
folder's ACL is restricted to the current user.

## Running as administrator

**It is not required, and the project does not require it.** Screen capture reads the composited
desktop, which belongs to the window station and the session rather than to a process, so an unelevated
program reads the pixels of elevated programs perfectly well. Both backends were verified unelevated on
this machine.

What elevation would change is limited to things this program does not do: sending messages to elevated
windows, injecting into them, or reading their memory. Notably it does **not** help with the cases that
actually make a fullscreen game invisible to capture:

* `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` — deliberate, documented, and unrelated to privilege.
* DRM-protected output — same.
* An exclusive-fullscreen swapchain that bypasses desktop composition.
* HDR or a hardware overlay plane the capture API does not include.

The project manifest therefore stays `asInvoker`. Forcing `requireAdministrator` would raise a UAC
prompt on every launch, break drag-and-drop from Explorer, and fix none of the above. If a build is
distributed to machines where the policy is "elevated or nothing", that is a deployment decision, not a
property of this program.

## Generating the icon

`src/RatTrap/app.ico` is generated, not drawn by hand:

```powershell
python tools\make_icon.py src\RatTrap\app.ico
```

Nine sizes from 16 to 256, supersampled 4x, with the small sizes as 32bpp DIBs and the large ones as PNG
payloads. The script asserts that each size drew something, left some transparency, and has a solid
arrow body, so a broken polygon cannot ship silently.

## Regenerating the feedback cues

The three cues live in `src/RatTrap/Audio/SoundCues.g.cs` as base64 WAV images, and that file is committed, so
an ordinary build never needs the generator or the source audio.

```powershell
# the pack URL and licence are in THIRD-PARTY-NOTICES.md
python tools\build_sounds.py --pack <extracted pack> --analyse            # candidates and their measurements
python tools\build_sounds.py --pack <extracted pack> --out src\RatTrap\Audio\SoundCues.g.cs
python tools\build_sounds.py --verify src\RatTrap\Audio\SoundCues.g.cs    # check the artifact, not the intent
```

`--verify` is not a formality. The first version of the generator decimated the audio while still declaring
the source sample rate, so every cue played an octave high and at twice the speed, and the only evidence it
printed was a duration that agreed with the wrong rate. It now reads the produced bytes back through an
independent WAV reader and asserts the format, and `rattrap selftest` makes the same measurements in C# so a
truncated payload fails the program's own test as well.

Needs `soundfile` (`pip install soundfile`), which brings libsndfile and numpy. Build time only: none of it is
required to build or run rat-trap.
## Verifying a change

Run all of these; each catches a class of problem the others do not.

```powershell
dotnet build RatTrap.sln -c Release
.\publish\rattrap.exe selftest                 # 84 deterministic checks, no hardware involved
.\publish\rattrap.exe capture --out out\       # real end-to-end capture
python tools\verify_cursor_output.py out\      # independent re-implementation of the file formats
.\publish\rattrap.exe gui --selfcheck --selfcheck-ms 8000 --selfcheck-out gui.txt
```

`tools/verify_cursor_output.py` is the one to keep. It shares no code with the program or with its
self-test, so a bug that is consistent between a writer and its own round-trip parser still shows up. It
is what established that the `.cur` XOR plane equals the PNG's RGBA byte for byte.


## What the observation view costs

Measured on this machine, from the GUI self-check, because the numbers decide the design and were not obvious
in advance:

| Region | One refresh, including `Solve()` | Background-panel rebuild |
| --- | --- | --- |
| 128 x 128 | 0.34 ms | 0.29 ms |
| 256 x 256 | 2.67 ms | 0.29 ms |

At the capture tick of 100 ms, a 256-pixel region spends under 3% of the thread that also drives capture. That
is why the view refreshes on the capture tick itself rather than on a display timer: a timer would show a
sampled, aliased version of a process that converges over tens of frames, and for no saving worth having.

The background panel is the expensive part and the slowest-moving, so it is rebuilt every fourth refresh, which
is where the second column comes from - nine refreshes produced three rebuilds.

### The recording's arithmetic

The ring is bounded by three limits, because any one alone is wrong: a block count decides the scale, a byte
budget decides the capacity, and a frame cap keeps the replay navigable.

| Region | Scale | Blocks | Per frame | Capacity | Total |
| --- | --- | --- | --- | --- | --- |
| 160 x 160 (cursor) | 1/2 | 80 x 80 | 44.8 KB | 300 | 13.4 MB |
| 256 x 256 (crosshair) | 1/2 | 128 x 128 | 114.7 KB | 256 | 32.0 MB |
| 1024 x 1024 (maximum crosshair) | 1/8 | 128 x 128 | 114.7 KB | 256 | 32.0 MB |

The last two rows are the point: the byte budget is a property of the recording, not of the region, so a
sixteen-times larger capture costs the same memory and pays for it in resolution instead. Eight bytes per block
carries composite RGB, background RGB, classification and alpha.

Nothing is allocated per recorded frame; the ring is fixed arrays sized once at the derived capacity. A recorder
that allocated on every capture tick would be adding garbage to the path it is observing.

Given the numbers, making the caps configurable is a reasonable next step and is listed in `ROADMAP.md` rather
than done here, so that the defaults stay the ones that were measured.

## Reproducing a write-protected install, and the trap in the fallback

Two things learned by actually reproducing "open folder fails without an administrator token", both worth
keeping because the first attempt at each was wrong in a way that looked like a program fault.

**Denying write on a directory also stops programs being launched from it.** The obvious way to build a
non-writable installation is `icacls <dir> /deny "<user>:(OI)(CI)(W)"`. It does make the directory
non-writable, and it also makes the executable inside it fail to launch with `Access is denied` — from
`Start-Process` and from `cmd /c start` alike. The reason is that the `(W)` alias includes `SYNCHRONIZE`, which
creating a process from an image needs. Denying `(WD,AD)` on the directory alone gives what was wanted:
creating children is refused, writing files is refused, and the program still runs. Two failed reproduction
attempts were spent on this before the error message was read properly instead of blamed on the program.

**A fallback path must not be able to throw.** The settings file falls back to `%LOCALAPPDATA%\rat-trap` when
the program directory cannot be written. That fallback lived inside a `catch` block and called
`Directory.CreateDirectory` on the fallback root without a guard of its own, so on a machine where the
per-user location is unavailable as well — a locked-down or redirected profile, or a sandboxed child process,
which is how this was found — the program printed `FATAL: UnauthorizedAccessException ...` and exited before
any window appeared. The branch whose whole purpose is to degrade gracefully was the one that crashed. It now
reports that settings cannot be persisted anywhere and carries on, because running without saving preferences
is a poor outcome but refusing to start is a worse one.

That second bug also explains why the save directory had to be fixed alongside it: it resolved against the
program directory only, ignoring the fallback entirely, so the same unelevated run advertised a save path it
could not create, refused to open that folder, and would then have failed to save a capture into it. Both now
follow one named `FallbackRoot`, and a relative `savedirectory` follows it while an absolute one is honoured as
written.

## When "the folder exists" is not the same as "the folder works"

Reported twice: "open folder does nothing", then after that was fixed, "without administrator rights, opening
the folder still shows an error box". The second report was the interesting one, because the box was not this
program's: it was Explorer refusing to open a folder that this program had just called ready.

The check tested creation, and `Directory.CreateDirectory` succeeds on a directory that already exists **whoever
owns it**. A folder left behind by an earlier run as administrator is owned by Administrators and is inaccessible
to the ordinary account running now, so the check passed, the folder was reported as the save location, and
Explorer then refused it with its own error box - Windows complaining about a folder this program had just
described as fine. Reproducing it needs no elevation at all: create a directory and deny this account (R,W) on
it, then confirm that `CreateDirectory` still returns happily, that listing throws `UnauthorizedAccessException`,
and that writing throws too.

The check is now what it should always have been: write a file, list the directory, delete the file. Those are
the three things that saving and opening actually need, and existence is none of them. This also removes the
last way a capture could be lost silently, since the same probe runs at start-up.

The launch beside it stopped using `ShellExecute`. It was the only call in that path able to raise system UI of
its own, and a dialog this program cannot describe is one it cannot explain. `explorer.exe` is now started with
`CreateProcessW` and an absolute path: no verb lookup, no association resolution, no PATH search, and a Win32
error code to report instead of a shell code to interpret. The two structures that call depends on are asserted
by size in the self-test (104 and 24 bytes on x64), because a struct that is a few bytes short reads the wrong
field and still looks like it works.

## The toolchain can break under an offline restore, and then nothing builds

Worth recording because it looked like a code fault for several minutes. This repository has no NuGet sources at
all - `nuget.config` clears them and restore resolves from `%USERPROFILE%\.nuget\packages`. When the SDK on this
machine updated to 9.0.319, it began requiring `Microsoft.DotNet.ILCompiler`, `Microsoft.NET.ILLink.Tasks` and
the 9.0.21 runtime packs, while the local cache held 9.0.20. With no sources to reach, restore failed with
NU1100 - and it kept failing on every later build, because the failed restore had written its errors into
`obj/project.assets.json` and each build replays them, `--no-restore` included. The gate that had passed minutes
earlier failed at `dotnet publish`. Nothing in the source had changed.

The unblock used here, and worth knowing: build the assembly without a runtime identifier, without AOT and
without the trim/AOT analyzers, which needs none of those packs and runs on the installed runtime:

```
dotnet build src\RatTrap\RatTrap.csproj -c Release -p:RuntimeIdentifier= -p:SelfContained=false \
  -p:PublishAot=false -p:IsAotCompatible=false -p:EnableAotAnalyzer=false \
  -p:EnableTrimAnalyzer=false -p:EnableSingleFileAnalyzer=false -p:IsTrimmable=false -p:PublishTrimmed=false
```

That produces a framework-dependent `rattrap.dll` that runs `selftest` normally, which is enough to verify
behaviour. It is not the release artefact: the single-file native exe and its signature still need the real
publish, so a machine in this state needs one restore with network access (or the 9.0.21 packages placed in the
cache) before a release can be built.
