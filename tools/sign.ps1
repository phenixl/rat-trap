<#
.SYNOPSIS
    Authenticode-sign a build output with the shared personal code-signing certificate.

.DESCRIPTION
    The certificate lives OUTSIDE any repository, in a folder shared by every project on this
    machine (default C:\ai-project\certs), so that one private key serves all of them and can
    never be committed by accident.

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

    # Folder holding the certificate files. Overridable so a build agent can point elsewhere.
    [string] $CertDir = 'C:\ai-project\certs',

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
    Write-Step "SKIPPED - $message"
    exit 0
}

if (-not (Test-Path -LiteralPath $Path)) { Fail "no such file: $Path" }
$target = (Resolve-Path -LiteralPath $Path).Path

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
$thumbprintFile = Join-Path $CertDir $ThumbprintName
$pfxPath = Join-Path $CertDir $PfxName
$passwordFile = Join-Path $CertDir $PasswordName

if (-not $Thumbprint -and (Test-Path $thumbprintFile)) {
    $Thumbprint = (Get-Content $thumbprintFile -Raw).Trim()
}
if (-not $Pfx -and (Test-Path $pfxPath)) { $Pfx = $pfxPath }
if (-not $PfxPassword -and (Test-Path $passwordFile)) {
    $PfxPassword = (Get-Content $passwordFile -Raw).Trim()
}

$inStore = $false
if ($Thumbprint) {
    $inStore = $null -ne (Get-Item -Path "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue)
}

if (-not $inStore -and -not $Pfx) {
    Fail ("no signing certificate found. Looked for thumbprint '$Thumbprint' in Cert:\CurrentUser\My " +
          "and for a .pfx at '$pfxPath'. Create one with New-SelfSignedCertificate, or point " +
          "-CertDir/-Thumbprint/-Pfx at an existing one.")
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

