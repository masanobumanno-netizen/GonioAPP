param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../dist/GonioWeb-Windows-x64'),
    [string]$SignTool = 'signtool.exe',
    [string]$CertificateThumbprint,
    [string]$TimestampUrl,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PublishDirectory).Path
$ownFiles = @('GonioWeb.exe', 'GonioWeb.dll', 'launcher/GonioLauncher.exe', 'launcher/GonioLauncher.dll')
foreach ($relative in $ownFiles) {
    if (!(Test-Path (Join-Path $root $relative))) { throw "Missing application binary: $relative" }
}
if (!$VerifyOnly) {
    if (!$CertificateThumbprint -or !$TimestampUrl) { throw 'Specify a public-trust RSA code-signing certificate thumbprint and its RFC3161 timestamp URL.' }
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Invalid certificate thumbprint.' }
    $certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
    if (!$certificate.HasPrivateKey -or $certificate.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') { throw 'An RSA certificate with access to its private key is required.' }
    # The certificate must be from a public-trust CA. Never install a self-signed root to bypass App Control.
    foreach ($relative in $ownFiles) {
        & $SignTool sign /sha1 $CertificateThumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 (Join-Path $root $relative)
        if ($LASTEXITCODE -ne 0) { throw "Signing failed: $relative" }
    }
}
# Check runtime binaries too; preserve Microsoft's existing signatures.
$files = @(Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in @('.exe', '.dll') })
foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid') { throw "Invalid or missing Authenticode signature: $($file.FullName) [$($signature.Status)]" }
    & $SignTool verify /pa /all /tw $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signature or timestamp verification failed: $($file.FullName)" }
}
Write-Host "Verified $($files.Count) Windows binaries. Test installation with Smart App Control enabled before publishing."
