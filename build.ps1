[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$OnlineChecks,
    [switch]$Release,
    [string]$SigningCertificateThumbprint,
    [string]$TimestampUrl,
    [switch]$ShowBuildPlan
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($Release) {
    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        throw 'Release builds require -SigningCertificateThumbprint from the CurrentUser certificate store.'
    }
    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        throw 'Release builds require -TimestampUrl for an RFC 3161 timestamp service.'
    }
}

function Invoke-SmokeTests {
    param(
        [Parameter(Mandatory)] [string]$DotnetPath,
        [Parameter(Mandatory)] [string]$RootPath,
        [switch]$RunOnlineChecks
    )

    $testProject = Join-Path $RootPath 'tests\KClaudeDesktop.SmokeTests\KClaudeDesktop.SmokeTests.csproj'
    $testArgs = @('run', '--project', $testProject, '-c', 'Release', '--no-build')
    if ($RunOnlineChecks) {
        $testArgs += '--'
        $testArgs += '--online'
    }

    $testOutput = (& $DotnetPath @testArgs 2>&1 | Out-String)
    $testExitCode = $LASTEXITCODE
    if (-not [string]::IsNullOrWhiteSpace($testOutput)) {
        Write-Host $testOutput.TrimEnd()
    }
    if ($testExitCode -eq 0) {
        return
    }

    $applicationControlBlocked = $testOutput -match '0x800711C7|应用程序控制策略已阻止此文件|blocked by.*application control'
    if (-not $applicationControlBlocked) {
        throw 'Smoke tests failed.'
    }

    Write-Warning 'Windows application control blocked the framework-dependent test host. Retrying the same tests as a self-contained single-file executable.'
    & $DotnetPath restore $testProject -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Single-file smoke test runtime restore failed.' }

    $testStage = Join-Path ([IO.Path]::GetTempPath()) ('KClaudeDesktop.SmokeTests.' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $testStage -Force | Out-Null
        & $DotnetPath publish $testProject `
            -c Release `
            -r win-x64 `
            --self-contained true `
            --no-restore `
            -o $testStage `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:PublishTrimmed=false `
            -p:DebugType=None `
            -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Single-file smoke test publish failed.' }

        $testExe = Join-Path $testStage 'KClaudeDesktop.SmokeTests.exe'
        if (-not (Test-Path -LiteralPath $testExe -PathType Leaf)) {
            throw 'Single-file smoke test executable was not produced.'
        }

        $runnerArgs = @()
        if ($RunOnlineChecks) {
            $runnerArgs += '--online'
        }
        & $testExe @runnerArgs
        if ($LASTEXITCODE -ne 0) { throw 'Single-file smoke tests failed.' }
    }
    finally {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedStage = [IO.Path]::GetFullPath($testStage)
        if ($resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedStage -PathType Container)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $windowsKitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.DirectoryName -like '*\x64' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }
    throw 'Release builds require signtool.exe from the Windows SDK.'
}

function Assert-TrustedSignature {
    param(
        [Parameter(Mandatory)] [string]$SignToolPath,
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string]$ExpectedThumbprint
    )

    $verifyOutput = (& $SignToolPath verify /pa /all /tw $FilePath 2>&1 | Out-String)
    $verifyExitCode = $LASTEXITCODE
    if (-not [string]::IsNullOrWhiteSpace($verifyOutput)) {
        Write-Host $verifyOutput.TrimEnd()
    }
    if ($verifyExitCode -ne 0) {
        throw "Authenticode verification failed: $FilePath"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        -not $signature.SignerCertificate.Thumbprint.Equals($ExpectedThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Authenticode signer verification failed: $FilePath"
    }
}

function Invoke-ReleaseSigning {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string]$CertificateThumbprint,
        [Parameter(Mandatory)] [string]$Rfc3161TimestampUrl
    )

    $normalizedThumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'SigningCertificateThumbprint must be a 40-character SHA-1 certificate thumbprint.'
    }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($Rfc3161TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) {
        throw 'TimestampUrl must be an absolute HTTP or HTTPS RFC 3161 endpoint.'
    }
    $certificatePath = "Cert:\CurrentUser\My\$normalizedThumbprint"
    $certificate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw "Signing certificate with a private key was not found in CurrentUser\\My: $normalizedThumbprint"
    }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
        throw 'Signing certificate is not currently valid.'
    }
    if (-not ($certificate.EnhancedKeyUsageList.ObjectId.Value -contains '1.3.6.1.5.5.7.3.3')) {
        throw 'Signing certificate is not valid for code signing.'
    }

    $signTool = Find-SignTool
    $signOutput = (& $signTool sign `
        /sha1 $normalizedThumbprint `
        /s My `
        /fd SHA256 `
        /tr $Rfc3161TimestampUrl `
        /td SHA256 `
        /d 'KClaude Desktop' `
        $FilePath 2>&1 | Out-String)
    $signExitCode = $LASTEXITCODE
    if (-not [string]::IsNullOrWhiteSpace($signOutput)) {
        Write-Host $signOutput.TrimEnd()
    }
    if ($signExitCode -ne 0) {
        throw 'Authenticode signing failed.'
    }
    Assert-TrustedSignature -SignToolPath $signTool -FilePath $FilePath -ExpectedThumbprint $normalizedThumbprint
    return [pscustomobject]@{
        SignTool = $signTool
        Thumbprint = $normalizedThumbprint
    }
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Solution = Join-Path $Root 'KClaudeDesktop.slnx'
$Artifacts = Join-Path $Root 'artifacts'
$artifactSuffix = if ($Release) { '' } else { '-unsigned' }
$publishDirectoryName = if ($Release) { 'publish-win-x64' } else { 'publish-win-x64-unsigned' }
$Publish = Join-Path $Artifacts $publishDirectoryName
$ReleaseExe = Join-Path $Artifacts ("KClaudeDesktop$artifactSuffix.exe")
$SetupExe = Join-Path $Artifacts ("KClaudeDesktop-Setup$artifactSuffix.exe")
$SourceZip = Join-Path $Artifacts ("KClaudeDesktop-Source$artifactSuffix.zip")
$HashFile = Join-Path $Artifacts ("SHA256SUMS$artifactSuffix.txt")

if ($ShowBuildPlan) {
    [pscustomobject]@{
        Mode = if ($Release) { 'ReleaseSigned' } else { 'DevelopmentUnsigned' }
        Executable = Split-Path -Leaf $ReleaseExe
        Setup = Split-Path -Leaf $SetupExe
        Source = Split-Path -Leaf $SourceZip
        Hashes = Split-Path -Leaf $HashFile
    } | ConvertTo-Json
    return
}

Push-Location $Root
try {
    $dotnet = Get-Command dotnet -ErrorAction Stop
    $version = (& $dotnet.Source --version).Trim()
    if ($version -notmatch '^10\.') {
        throw "The .NET 10 SDK is required. Found: $version"
    }

    & $dotnet.Source restore $Solution
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    & $dotnet.Source restore 'src\KClaudeDesktop\KClaudeDesktop.csproj' -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'win-x64 runtime restore failed.' }

    & $dotnet.Source build $Solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    if (-not $SkipTests) {
        Invoke-SmokeTests -DotnetPath $dotnet.Source -RootPath $Root -RunOnlineChecks:$OnlineChecks
    }

    New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
    New-Item -ItemType Directory -Path $Publish -Force | Out-Null

    & $dotnet.Source publish 'src\KClaudeDesktop\KClaudeDesktop.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --no-restore `
        -o $Publish `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $PublishedExe = Join-Path $Publish 'KClaudeDesktop.exe'
    if (-not (Test-Path -LiteralPath $PublishedExe -PathType Leaf)) {
        throw 'Published single-file EXE was not produced.'
    }

    $signingResult = $null
    if ($Release) {
        $signingResult = Invoke-ReleaseSigning `
            -FilePath $PublishedExe `
            -CertificateThumbprint $SigningCertificateThumbprint `
            -Rfc3161TimestampUrl $TimestampUrl
    }
    else {
        $developmentSignature = Get-AuthenticodeSignature -LiteralPath $PublishedExe
        if ($developmentSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
            throw 'Development artifact unexpectedly contains an Authenticode signature and cannot be labeled unsigned.'
        }
    }

    Copy-Item -LiteralPath $PublishedExe -Destination $ReleaseExe -Force
    Copy-Item -LiteralPath $PublishedExe -Destination $SetupExe -Force

    if ($Release) {
        Assert-TrustedSignature -SignToolPath $signingResult.SignTool -FilePath $ReleaseExe -ExpectedThumbprint $signingResult.Thumbprint
        Assert-TrustedSignature -SignToolPath $signingResult.SignTool -FilePath $SetupExe -ExpectedThumbprint $signingResult.Thumbprint
    }

    $SourceStage = Join-Path ([IO.Path]::GetTempPath()) ('KClaudeDesktop.Source.' + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $SourceStage -Force | Out-Null
        foreach ($directory in @('.github', 'docs', 'src', 'tests')) {
            Copy-Item -LiteralPath (Join-Path $Root $directory) -Destination (Join-Path $SourceStage $directory) -Recurse -Force
        }
        foreach ($file in @(
            '.gitignore', 'build.ps1', 'CHANGELOG.md', 'Directory.Build.props',
            'global.json', 'KClaudeDesktop.slnx', 'README.md', 'SECURITY.md'
        )) {
            Copy-Item -LiteralPath (Join-Path $Root $file) -Destination (Join-Path $SourceStage $file) -Force
        }
        Get-ChildItem -LiteralPath $SourceStage -Directory -Recurse -Force |
            Where-Object { $_.Name -in @('bin', 'obj') } |
            Sort-Object FullName -Descending |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
        if (Test-Path -LiteralPath $SourceZip -PathType Leaf) {
            Remove-Item -LiteralPath $SourceZip -Force
        }
        Compress-Archive -Path (Join-Path $SourceStage '*') -DestinationPath $SourceZip -CompressionLevel Optimal
    }
    finally {
        $TempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $ResolvedStage = [IO.Path]::GetFullPath($SourceStage)
        if ($ResolvedStage.StartsWith($TempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $ResolvedStage -PathType Container)) {
            Remove-Item -LiteralPath $ResolvedStage -Recurse -Force
        }
    }

    Get-FileHash -Algorithm SHA256 -LiteralPath $ReleaseExe, $SetupExe, $SourceZip |
        ForEach-Object { "{0}  {1}" -f $_.Hash.ToLowerInvariant(), (Split-Path -Leaf $_.Path) } |
        Set-Content -LiteralPath $HashFile -Encoding utf8

    Write-Host ''
    if ($Release) {
        Write-Host 'SIGNED RELEASE BUILD completed:'
    }
    else {
        Write-Warning 'UNSIGNED DEVELOPMENT BUILD: do not publish these artifacts as a trusted release.'
    }
    Write-Host "  $ReleaseExe"
    Write-Host "  $SetupExe"
    Write-Host "  $SourceZip"
    Write-Host "  $HashFile"
}
finally {
    Pop-Location
}
