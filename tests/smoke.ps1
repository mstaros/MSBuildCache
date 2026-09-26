param
(
    [Parameter(Mandatory = $false)]
    [string] $LogDirectory = $env:LogDirectory,

    [Parameter(Mandatory = $false)]
    [string] $LocalPackageDir = $env:LocalPackageDir,

    [Parameter(Mandatory = $false)]
    [string] $TestRoot,

    [Parameter(Mandatory = $false)]
    [string] $CachePackage = "Microsoft.MSBuildCache.Local",

    [Parameter(Mandatory = $false)]
    [string] $MSBuildPath = $null,

    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Debug"
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

function Run-Test {
    param (
        [Parameter(Mandatory = $true)]
        [string] $TestName,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheMisses,

        [Parameter(Mandatory = $false)]
        [switch] $SkipClean,

        [Parameter(Mandatory = $false)]
        [string] $ExpectedOutputHash
    )

    Write-Host "[$TestName] Starting test"

    if (-not $SkipClean)
    {
        Write-Host "[$TestName] Cleaning"
        Push-Location $ProjectDir
        try
        {
            & git clean -fdx
            if ($LASTEXITCODE -ne 0)
            {
                throw "[$TestName] git clean failed."
            }
        }
        finally
        {
            Pop-Location
        }
    }

    Write-Host "[$TestName] Building"
    $result = Invoke-MSBuildCacheBuild `
        -MSBuildPath $MSBuildPath `
        -ProjectDir $ProjectDir `
        -LogDirectory (Join-Path $LogDirectory $TestName) `
        -CachePackage $CachePackage `
        -CacheUniverse $CacheUniverse `
        -CacheRoot "$TestRoot\MSBuildCache" `
        -ExtraProperties @{ Configuration = $Configuration } `
        -Context $TestName

    Assert-CacheStats `
        -Result $result `
        -ExpectedHits $ExpectedCacheHits `
        -ExpectedMisses $ExpectedCacheMisses `
        -Context $TestName

    if ($ExpectedOutputHash)
    {
        $outputPath = Join-Path $ProjectDir "bin\$Configuration\net9.0\TestProject.dll"
        $actualOutputHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
        if ($actualOutputHash -ne $ExpectedOutputHash)
        {
            throw "[$TestName] cached output was changed by a previous output mutation."
        }
    }

    Write-Host "[$TestName] Test complete"
}

Push-Location (Join-Path $PSScriptRoot "..")
$RepoRoot = "$PWD"
Pop-Location

if (-not $LocalPackageDir)
{
    $LocalPackageDir = Join-Path $RepoRoot "artifacts\$Configuration\packages"
}

if (-not $LogDirectory)
{
    $LogDirectory = Join-Path $RepoRoot "logs\Tests"
}

if (-not $TestRoot)
{
    $TestRoot = Join-Path $RepoRoot "TestResult\$CachePackage"
}

if (-not $MSBuildPath)
{
    # Use SDK MSBuild; an explicit dotnet.exe can select the patched SDK.
    $MSBuildPath = (Get-Command "dotnet").Path
}
# Use a unique cache universe for every test run
$CacheUniverse = (New-Guid).ToString()

$env:LocalPackageDir = $LocalPackageDir

Write-Host "Log Directory: $LogDirectory"
Remove-Item -Path $LogDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $LogDirectory > $null

# set up original run
Write-Host "Running test in $TestRoot"

$env:NUGET_PACKAGES="$TestRoot\.nuget"
$ProjectDir = Join-Path $TestRoot "src"

Remove-Item -Path $TestRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $TestRoot > $null

Write-Host "Creating Git repo in $ProjectDir"
New-MSBuildCacheTestProject `
    -ProjectDir $ProjectDir `
    -GitUserName $Env:UserName `
    -GitUserEmail "$Env:UserName@microsoft.com"

Run-Test `
    -TestName "ColdCache" `
    -ExpectedCacheHits 0 `
    -ExpectedCacheMisses 1

# Mutate the same file object in place so hardlink sharing cannot be hidden by replacement.
$outputPath = Join-Path $ProjectDir "bin\$Configuration\net9.0\TestProject.dll"
$originalOutputHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
foreach ($stage in @("AfterPublication", "AfterRestoration"))
{
    if ((Get-Item -LiteralPath $outputPath).IsReadOnly)
    {
        throw "[$stage] build output must remain writable."
    }

    $stream = [System.IO.File]::Open(
        $outputPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try
    {
        $firstByte = $stream.ReadByte()
        if ($firstByte -lt 0)
        {
            throw "[$stage] expected a nonempty build output."
        }
        $stream.Position = 0
        $stream.WriteByte([byte]($firstByte -bxor 1))
    }
    finally
    {
        $stream.Dispose()
    }

    if ((Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash -eq $originalOutputHash)
    {
        throw "[$stage] output mutation did not change the bytes."
    }

    Run-Test `
        -TestName "WarmCache$stage" `
        -ExpectedCacheHits 1 `
        -ExpectedCacheMisses 0 `
        -SkipClean `
        -ExpectedOutputHash $originalOutputHash
}

# A cache miss must also rebuild over restored outputs without a purger.
$programPath = Join-Path $ProjectDir "Program.cs"
$originalProgram = [System.IO.File]::ReadAllBytes($programPath)
try
{
    [System.IO.File]::AppendAllText($programPath, "`r`n// Exercise a rebuild over restored cache outputs.`r`n")
    Run-Test `
        -TestName "RebuildWithoutCleaning" `
        -ExpectedCacheHits 0 `
        -ExpectedCacheMisses 1 `
        -SkipClean
}
finally
{
    [System.IO.File]::WriteAllBytes($programPath, $originalProgram)
}

Run-Test `
    -TestName "RestoreAfterRebuild" `
    -ExpectedCacheHits 1 `
    -ExpectedCacheMisses 0 `
    -SkipClean `
    -ExpectedOutputHash $originalOutputHash

Run-Test `
    -TestName "WarmCache" `
    -ExpectedCacheHits 1 `
    -ExpectedCacheMisses 0 `
    -ExpectedOutputHash $originalOutputHash

# set up junction run
try {
    cmd /c mklink /J "$RepoRoot-OtherPath" "$RepoRoot"
    $TestRoot = $TestRoot.Replace($RepoRoot, "$RepoRoot-OtherPath")
    Write-Host "Running test in $TestRoot"

    $env:NUGET_PACKAGES="$TestRoot\.nuget"
    $ProjectDir = Join-Path $TestRoot "src"

    Run-Test `
        -TestName "WarmCacheOtherRoot" `
        -ExpectedCacheHits 1 `
        -ExpectedCacheMisses 0 `
        -ExpectedOutputHash $originalOutputHash
}
finally  {
    # weird way to delete a junction in PowerShell
    (Get-Item "$RepoRoot-OtherPath").Delete()
}
