<#
.SYNOPSIS
    Produce a release build of rat-trap, sign it, and verify the result.

.DESCRIPTION
    `dotnet publish` alone is not enough to trust a release, for two reasons this script exists to close:

      1. The build log can say "Successfully signed" while the published binary has no signature. The
         ILCompiler pack's CopyNativeBinary target also runs after Publish, deletes the published binary and
         copies the unsigned native one back over it. The project hooks signing to CopyNativeBinary so this
         no longer happens, but a log line is not evidence and this script checks the file.
      2. A native binary can build, publish and be signed, and still fail at startup for a reason that only
         shows at run time. So the published executable is asked to run its own self-test.

    It also never destroys the previous release before it knows a new one exists. An earlier version published
    straight into the output directory after deleting it, which meant that any failure - a restore that could
    not reach the package cache, a linker error, a failed self-test - left the operator with no package at all
    instead of the one they had. A release gate that can leave you worse off than not running it is not a gate.
    Publishing now goes to a staging directory and the output is replaced only after the new build has been
    signed and has passed its own self-test.

    Exits non-zero if anything fails, so it can be used as a release gate.

.EXAMPLE
    pwsh tools\publish.ps1
    pwsh tools\publish.ps1 -OutputDirectory D:\dist -SkipSelfTest
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $OutputDirectory = 'publish',
    [switch] $RequireSignature,
    [switch] $SkipSelfTest,
    [switch] $SkipBuild,

    # Stop any rat-trap instance running from this repository before building. Off by default, because
    # killing a process is not this script's decision to make silently.
    [switch] $StopRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RatTrap\RatTrap.csproj'
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }

# Both staging names start with an underscore, which .gitignore already covers, so a failed run cannot leave
# build output looking like a source change.
$staging = Join-Path $root '_publish-staging'
$previous = Join-Path $root '_publish-previous'
$staged = -not $SkipBuild
$exe = if ($staged) { Join-Path $staging 'rattrap.exe' } else { Join-Path $output 'rattrap.exe' }

function Step([string] $message) { Write-Host "publish: $message" }
function Die([string] $message) {
    # A failure must not leave a half-built staging directory behind to be mistaken for a package, and must
    # not have touched the output directory at all. Saying so out loud is the point of the second line.
    if ($staged -and (Test-Path $staging)) {
        Write-Host 'publish: discarding the staging directory; the previous package was never touched'
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Error "publish: $message"
    exit 1
}

if (-not $SkipBuild) {
    # A running instance blocks the build in a way that does not look like its cause. Windows locks a
    # mapped executable image, so an instance started from bin\...\native\ stops the native linker from
    # writing its own output, and the only symptom is LNK1104 naming a file nobody is consciously using -
    # which reads as a permissions or disk problem and is neither. The same instance is also holding the
    # global hot key, so a second one cannot register it.
    $running = @()
    foreach ($candidate in (Get-Process -Name 'rattrap' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $candidate.Path } catch { continue }   # protected or already exited
        if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            $running += [pscustomobject]@{ Id = $candidate.Id; Path = $path }
        }
    }
    if ($running) {
        foreach ($process in $running) { Step "running instance: pid $($process.Id) from $($process.Path)" }
        if ($StopRunning) {
            foreach ($process in $running) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                Step "stopped pid $($process.Id)"
            }
            Start-Sleep -Milliseconds 700
        } else {
            Die ("a rat-trap instance from this repository is running, and it holds the file the native " +
                 "linker needs to write. Windows locks a mapped executable image, so the build would fail " +
                 "with LNK1104 naming bin\...\native\rattrap.exe, which reads as a file permission " +
                 "problem and is not one. Close it, or re-run with -StopRunning.")
        }
    }

    # Say what the build will use for signing before it starts, because a missing certificate causes a
    # skip rather than a failure, and discovering that after the fact is how an unsigned release ships.
    $certDir = $env:certs_path
    if (-not $certDir) { $certDir = [Environment]::GetEnvironmentVariable('certs_path', 'User') }
    if ($certDir) {
        Step "certificate folder: $certDir (certs_path)"
    } else {
        Step 'WARNING: the certs_path user environment variable is not set, so this build will not sign'
    }

    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    Step "building and publishing $Configuration/$RuntimeIdentifier into the staging directory"
    $buildLog = Join-Path ([IO.Path]::GetTempPath()) "rattrap-publish-build-$PID.log"
    & dotnet publish $project -c $Configuration -r $RuntimeIdentifier -o $staging --nologo 2>&1 |
        Set-Content -Path $buildLog -Encoding UTF8
    $buildExit = $LASTEXITCODE

    if ($buildExit -ne 0) {
        $buildOutput = Get-Content $buildLog -ErrorAction SilentlyContinue
        Remove-Item $buildLog -Force -ErrorAction SilentlyContinue

        # NU1100 is the failure worth naming. This repository clears every NuGet source on purpose, so restore
        # depends entirely on what is already in %USERPROFILE%\.nuget\packages; when the SDK updates and starts
        # asking for a newer pack revision than the cache holds, nothing can be restored and the only clue is a
        # package id and a version range. That happened here, and it is worth a paragraph rather than an exit
        # code, because the repair is one command and the symptom does not suggest it.
        if ($buildOutput -match 'NU1100') {
            $missing = $buildOutput | Where-Object { $_ -match 'NU1100' } | Select-Object -First 3
            foreach ($line in $missing) { Write-Host "  $($line.Trim())" }
            Die ("restore cannot satisfy the SDK's package versions from the offline cache. This project " +
                 "clears all NuGet sources, so restore only sees what is already under " +
                 "%USERPROFILE%\.nuget\packages, and the SDK in use (" + ((dotnet --version) 2>&1) + ") asks " +
                 "for packs the cache does not have. Repair it with one online restore, which adds a source " +
                 "for that single invocation and does not change nuget.config:`n" +
                 "    dotnet restore src\RatTrap\RatTrap.csproj -s https://api.nuget.org/v3/index.json`n" +
                 "Behind a proxy, make sure HTTP_PROXY and HTTPS_PROXY are set for the shell running it.")
        }

        Die "dotnet publish exited with $buildExit"
    }
    Remove-Item $buildLog -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $exe)) { Die "no executable at $exe" }
$item = Get-Item $exe
Step ("built {0:N0} bytes ({1:N2} MB)" -f $item.Length, ($item.Length / 1MB))

# --- signature ---------------------------------------------------------------------------------
$signature = Get-AuthenticodeSignature -LiteralPath $exe
Step "signature status: $($signature.Status)"
if ($signature.SignerCertificate) {
    Step "  signer    : $($signature.SignerCertificate.Subject)"
    Step "  thumbprint: $($signature.SignerCertificate.Thumbprint)"
    Step "  valid to  : $($signature.SignerCertificate.NotAfter)"
} else {
    $message = "the published executable is NOT signed. The certificate folder comes from the certs_path " +
               "user environment variable; check that it is set and that it holds " +
               "phenixl-codesign.thumbprint.txt, or pass -p:RatTrapSigningDir=<folder> to dotnet publish."
    if ($RequireSignature) { Die $message } else { Step "  WARNING: $message" }
}

if ($signature.Status -eq 'HashMismatch') {
    # Distinct from an untrusted chain: this one means the file was modified after signing.
    Die "the signature does not match the file contents; the binary was altered after signing"
}

if ($signature.Status -eq 'Valid') {
    Step 'the signature chains to a trusted root on this machine, so it reads as Valid here'
} elseif ($signature.Status -ne 'NotSigned') {
    # Only said when it applies. Printing this next to a Valid signature would train the reader to skip it,
    # and on a machine where the certificate is not installed it is the difference between "expected" and
    # "something is wrong".
    Step ("note: this certificate is self-signed, so on a machine that has not installed " +
          "phenixl-codesign.cer into Trusted Root and Trusted Publishers the status reads " +
          "'$($signature.Status)' rather than Valid. The signature is still verifiable and tampering " +
          "still invalidates it; only the chain is untrusted.")
}

# --- the binary actually runs -------------------------------------------------------------------
if (-not $SkipSelfTest) {
    Step 'running the published self-test'
    $log = Join-Path ([IO.Path]::GetTempPath()) "rattrap-publish-selftest-$PID.txt"
    $process = Start-Process -FilePath $exe -ArgumentList 'selftest' -WorkingDirectory $root `
        -NoNewWindow -PassThru -RedirectStandardOutput $log
    # Start-Process -Wait has hung on this host for a process that creates a window, so wait on the id
    # with a bound instead of trusting -Wait.
    Wait-Process -Id $process.Id -Timeout 300 -ErrorAction SilentlyContinue
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        Die 'the self-test did not finish within 300 seconds'
    }

    $lines = Get-Content $log -ErrorAction SilentlyContinue
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $summary = $lines | Where-Object { $_ -match 'self-test (PASSED|FAILED)' } | Select-Object -Last 1
    $failures = $lines | Where-Object { $_ -match '^FAIL' }
    if ($summary) { Step $summary.Trim() }
    foreach ($failure in $failures) { Write-Host "  $failure" }
    if ($process.ExitCode -ne 0) { Die "the published binary's self-test failed (exit $($process.ExitCode))" }
}

# --- the swap, and the only destructive step ----------------------------------------------------
# Names this program creates at run time rather than the build creating them. They matter because the default
# save directory is relative to the executable, so "the publish folder" is where a user's captures and their
# settings actually live. Replacing a package must not take those with it: they are not build output, and
# nothing regenerates them.
$preserve = @('captures', 'rat-trap.ini')

if ($staged) {
    # Everything above is verification. Nothing has touched the output directory yet, so reaching this line
    # means the new package is built, signed as configured, and runs. The previous one is moved aside rather
    # than deleted, so even a failure in the middle of the swap leaves it recoverable.
    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    if (Test-Path $output) { Move-Item -LiteralPath $output -Destination $previous }
    Move-Item -LiteralPath $staging -Destination $output

    foreach ($name in $preserve) {
        $carried = Join-Path $previous $name
        if (Test-Path $carried) {
            Move-Item -LiteralPath $carried -Destination (Join-Path $output $name)
            Step "kept $name from the previous package (user data, not build output)"
        }
    }

    if (Test-Path $previous) { Remove-Item $previous -Recurse -Force }
    Step "replaced $output with the verified build"
}

Step "done: $(Join-Path $output 'rattrap.exe')"
