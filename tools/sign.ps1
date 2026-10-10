<#
.SYNOPSIS
    Authenticode-sign a build output with the shared personal code-signing certificate.

.DESCRIPTION
    The certificate lives OUTSIDE any repository, in a folder shared by every project on this
    machine and named by the user environment variable certs_path, so that one private key serves
    all of them and can never be committed by accident.

    The folder is NOT hard-coded here. It was, and the hard-coded path silently went stale when the
    folder moved, which is the failure this environment variable exists to prevent: signing
    degraded to a skip and the only sign of it was a line in a build log nobody reads. The folder is
    resolved in this order - the -CertDir parameter, then the certs_path environment variable. With
    neither set there is no lookup at all, and the script says that rather than guessing.

    Signing is deliberately best-effort. A build must not fail because a developer does not have
    the certificate, so this script exits 0 and explains itself when no certificate is available.
    Pass -Require to turn that into a failure instead, for release pipelines where an unsigned
    binary is worse than no binary.

    Two ways to reach the key, tried in order:
      1. The certificate store, by thumbprint. Needs no password, because the key is already
         installed for the current user. This is what a local build uses.
      2. A .pfx file plus its password. Needs no store, so this is what a build agent uses.

.NOTES
    A self-signed certificate is NOT trusted by other machines. Windows will still warn about a
    downloaded binary until the matching .cer is installed into Trusted Root Certification
    Authorities and Trusted Publishers on the machine doing the trusting. Signing gives you a
    verifiable, tamper-evident build; it does not buy reputation with SmartScreen.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    # Folder holding the certificate files. Defaults to the certs_path environment variable, so that
    # moving the certificate folder is a one-place change on the machine and needs no edit here.
    [string] $CertDir,

    [string] $PfxName = 'phenixl-codesign.pfx',
    [string] $ThumbprintName = 'phenixl-codesign.thumbprint.txt',
    [string] $PasswordName = 'phenixl-codesign.pfx.password.txt',

    # Explicit overrides; any of these beat the files in $CertDir.
    [string] $Thumbprint,
    [string] $Pfx,
    [string] $PfxPassword,
    [string] $Description = 'rat-trap',

    # RFC3161 timestamp server. Off by default so that a build never depends on the network.
    [string] $TimestampUrl,

    [switch] $Require,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $message) {
    if (-not $Quiet) { Write-Host "sign: $message" }
}

function Fail([string] $message) {
    if ($Require) { Write-Error $message; exit 1 }
    # Deliberately not gated on -Quiet. Quiet suppresses routine chatter; "there is no certificate, so
    # this binary is unsigned" is the one message whose absence caused a release to ship unsigned, and a
    # build log is the only place anyone would notice.
    Write-Host "sign: SKIPPED - $message"
    exit 0
}

if (-not (Test-Path -LiteralPath $Path)) { Fail "no such file: $Path" }
$target = (Resolve-Path -LiteralPath $Path).Path

# --- resolve the certificate folder ------------------------------------------------------------
# An explicit -CertDir wins, otherwise certs_path. The User scope is read as well as the process scope,
# and that is not belt-and-braces: an environment variable created after a shell was started is absent
# from that shell's environment, which is the normal state of affairs for the terminal or IDE someone is
# already sitting in when they set it. Reading the registry makes the script work anyway.
if (-not $CertDir) { $CertDir = $env:certs_path }
if (-not $CertDir) { $CertDir = [Environment]::GetEnvironmentVariable('certs_path', 'User') }
if ($CertDir) { Write-Step "certificate folder: $CertDir" }
else { Write-Step 'no -CertDir and no certs_path; looking only at -Thumbprint/-Pfx' }

# --- locate signtool -------------------------------------------------------------------------
# Set-AuthenticodeSignature would work too, but signtool is the reference implementation and is
# what produces the digest algorithm and timestamp structures that other verifiers expect.
$signtool = $null
$kitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
if (Test-Path $kitsBin) {
    $signtool = Get-ChildItem $kitsBin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signtool) {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { $signtool = $onPath.Source }
}

# --- resolve a certificate -------------------------------------------------------------------
# [IO.Path]::Combine rather than Join-Path, and that is not a style preference. Join-Path resolves and
# validates the path, so a certificate folder on a drive that is not currently mapped throws - and with
# $ErrorActionPreference = 'Stop' that turns "there is no certificate here" into exit code 1 with a
# PowerShell error, instead of the clean, visible skip this script is supposed to produce. A missing
# certificate is a normal situation, so it must not be able to fail as an exception.
# An empty folder yields $null names rather than bare filenames, which would otherwise be tested relative
# to the current directory and could accidentally match something.
$thumbprintFile = if ($CertDir) { [IO.Path]::Combine($CertDir, $ThumbprintName) } else { $null }
$pfxPath = if ($CertDir) { [IO.Path]::Combine($CertDir, $PfxName) } else { $null }
$passwordFile = if ($CertDir) { [IO.Path]::Combine($CertDir, $PasswordName) } else { $null }

if (-not $Thumbprint -and $thumbprintFile -and (Test-Path $thumbprintFile)) {
    $Thumbprint = (Get-Content $thumbprintFile -Raw).Trim()
}
if (-not $Pfx -and $pfxPath -and (Test-Path $pfxPath)) { $Pfx = $pfxPath }
if (-not $PfxPassword -and $passwordFile -and (Test-Path $passwordFile)) {
    $PfxPassword = (Get-Content $passwordFile -Raw).Trim()
}

$inStore = $false
if ($Thumbprint) {
    $inStore = $null -ne (Get-Item -Path "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue)
}

if (-not $inStore -and -not $Pfx) {
    $where = if ($CertDir) {
        "thumbprint '$Thumbprint' in Cert:\CurrentUser\My and a .pfx at '$pfxPath'"
    } else {
        "nothing, because no certificate folder was given"
    }
    Fail ("no signing certificate found. Looked for $where. Set the certs_path user environment " +
          "variable to the folder holding the certificate, pass -CertDir, or pass -Thumbprint/-Pfx " +
          "directly.")
}

if (-not $signtool) { Fail 'signtool.exe was not found; install the Windows SDK signing tools.' }

# Idempotent: signing is hooked to every copy in the publish flow, so it can run more than once. A file
# already carrying this certificate's signature is left alone, which also keeps a re-signed binary's
# certificate table from growing on each pass.
$existing = Get-AuthenticodeSignature -LiteralPath $target
if ($existing.Status -ne 'NotSigned' -and $existing.SignerCertificate -and $Thumbprint -and
    $existing.SignerCertificate.Thumbprint -eq $Thumbprint) {
    Write-Step "already signed with $Thumbprint; nothing to do"
    exit 0
}

# --- sign ------------------------------------------------------------------------------------
$arguments = @('sign', '/fd', 'SHA256', '/d', $Description, '/v')
if ($inStore) {
    Write-Step "signing with the certificate in the store, thumbprint $Thumbprint"
    $arguments += @('/sha1', $Thumbprint)
} else {
    if (-not (Test-Path -LiteralPath $Pfx)) { Fail "no such .pfx: $Pfx" }
    if (-not $PfxPassword) { Fail "the .pfx at $Pfx needs a password; pass -PfxPassword or place it in $passwordFile" }
    Write-Step "signing with the .pfx at $Pfx"
    $arguments += @('/f', $Pfx, '/p', $PfxPassword)
}
if ($TimestampUrl) {
    Write-Step "timestamping against $TimestampUrl"
    $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
}
$arguments += $target

& $signtool @arguments
if ($LASTEXITCODE -ne 0) { Fail "signtool exited with $LASTEXITCODE" }

# --- verify ----------------------------------------------------------------------------------
$signature = Get-AuthenticodeSignature -LiteralPath $target
Write-Step "status: $($signature.Status)   signer: $($signature.SignerCertificate.Subject)"
if ($signature.Status -eq 'NotSigned') { Fail 'the file is still unsigned after signing' }

if (-not $Quiet) {
    Write-Host ("sign: note - this is a self-signed certificate, so Status will read " +
                "'UnknownError' or 'UntrustedRoot' on any machine that has not installed " +
                "$([IO.Path]::GetFileNameWithoutExtension($PfxName)).cer into Trusted Root and " +
                'Trusted Publishers. The signature is still verifiable, and tampering with the ' +
                'binary still invalidates it.')
}
exit 0

