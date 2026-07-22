[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$OnlineChecks
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Solution = Join-Path $Root 'KClaudeDesktop.slnx'
$Artifacts = Join-Path $Root 'artifacts'
$Publish = Join-Path $Artifacts 'publish-win-x64'
$ReleaseExe = Join-Path $Artifacts 'KClaudeDesktop.exe'
$SetupExe = Join-Path $Artifacts 'KClaudeDesktop-Setup.exe'
$SourceZip = Join-Path $Artifacts 'KClaudeDesktop-Source.zip'

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
        $testArgs = @(
            'run', '--project', 'tests\KClaudeDesktop.SmokeTests\KClaudeDesktop.SmokeTests.csproj',
            '-c', 'Release', '--no-build'
        )
        if ($OnlineChecks) {
            $testArgs += '--'
            $testArgs += '--online'
        }
        & $dotnet.Source @testArgs
        if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
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
    Copy-Item -LiteralPath $PublishedExe -Destination $ReleaseExe -Force
    Copy-Item -LiteralPath $PublishedExe -Destination $SetupExe -Force

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
        Set-Content -LiteralPath (Join-Path $Artifacts 'SHA256SUMS.txt') -Encoding utf8

    Write-Host ''
    Write-Host 'Build completed:'
    Write-Host "  $ReleaseExe"
    Write-Host "  $SetupExe"
    Write-Host "  $SourceZip"
}
finally {
    Pop-Location
}
