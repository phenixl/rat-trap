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
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RatTrap\RatTrap.csproj'
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
$exe = Join-Path $output 'rattrap.exe'

function Step([string] $message) { Write-Host "publish: $message" }
function Die([string] $message) { Write-Error "publish: $message"; exit 1 }

if (-not $SkipBuild) {
    # Say what the build will use for signing before it starts, because a missing certificate causes a
    # skip rather than a failure, and discovering that after the fact is how an unsigned release ships.
    $certDir = $env:certs_path
    if (-not $certDir) { $certDir = [Environment]::GetEnvironmentVariable('certs_path', 'User') }
    if ($certDir) {
        Step "certificate folder: $certDir (certs_path)"
    } else {
        Step 'WARNING: the certs_path user environment variable is not set, so this build will not sign'
    }

    Step "building and publishing $Configuration/$RuntimeIdentifier into $output"
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    & dotnet publish $project -c $Configuration -r $RuntimeIdentifier -o $output --nologo
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish exited with $LASTEXITCODE" }
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

if ($signature.Status -ne 'NotSigned') {
    Step ("note: a self-signed certificate reports 'UnknownError' or 'UntrustedRoot' on any machine " +
          "that has not installed the matching .cer into Trusted Root and Trusted Publishers. The " +
          "signature is still verifiable and tampering still invalidates it.")
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

Step 'done'
