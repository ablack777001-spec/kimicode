[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BuildScript
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$resolvedBuildScript = (Resolve-Path -LiteralPath $BuildScript).Path
$ErrorActionPreference = 'Continue'
$output = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $resolvedBuildScript -Release 2>&1 | Out-String)
$exitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

if ($exitCode -eq 0) {
    throw 'Release build unexpectedly succeeded without signing configuration.'
}
if ($output -notmatch 'SigningCertificateThumbprint') {
    throw "Release build did not fail with the signing contract message. Output: $output"
}

Write-Host 'PASS  Release build rejects missing signing certificate configuration before publishing.'

$ErrorActionPreference = 'Continue'
$developmentPlan = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $resolvedBuildScript -ShowBuildPlan 2>&1 | Out-String)
$developmentExitCode = $LASTEXITCODE
$releasePlan = (& powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $resolvedBuildScript `
    -Release `
    -SigningCertificateThumbprint '00112233445566778899AABBCCDDEEFF00112233' `
    -TimestampUrl 'https://timestamp.example.invalid' `
    -ShowBuildPlan 2>&1 | Out-String)
$releaseExitCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

if ($developmentExitCode -ne 0 -or $developmentPlan -notmatch 'KClaudeDesktop-unsigned\.exe' -or
    $developmentPlan -notmatch 'KClaudeDesktop-Setup-unsigned\.exe' -or
    $developmentPlan -notmatch 'SHA256SUMS-unsigned\.txt') {
    throw "Development build plan is not clearly unsigned. Output: $developmentPlan"
}
if ($releaseExitCode -ne 0 -or $releasePlan -notmatch 'KClaudeDesktop\.exe' -or
    $releasePlan -notmatch 'KClaudeDesktop-Setup\.exe' -or
    $releasePlan -match 'KClaudeDesktop-unsigned\.exe') {
    throw "Release build plan does not use canonical signed artifact names. Output: $releasePlan"
}

Write-Host 'PASS  Development and release artifact names are separated.'
