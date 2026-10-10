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

## Two build-environment traps

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
```
### What signing does and does not buy

It produces a verifiable, tamper-evident binary: `Get-AuthenticodeSignature` reports a signer, and any
modification invalidates the signature. It does **not** buy reputation. Because the certificate is
self-signed, `Status` reads `UnknownError` — in English, "the certificate chain terminated in an
untrusted root" — on every machine that has not installed `phenixl-codesign.cer` into Trusted Root
Certification Authorities and Trusted Publishers. SmartScreen will still warn about a downloaded copy.

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

