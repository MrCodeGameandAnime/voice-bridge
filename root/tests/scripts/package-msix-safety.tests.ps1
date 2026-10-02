Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$allowedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'root\output'))
$outputRootPrefix = $allowedOutputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$packageScript = Join-Path $repositoryRoot 'root\scripts\package-msix.ps1'
$uniqueId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $allowedOutputRoot ".package-msix-safety-$uniqueId"
$junctionPath = Join-Path $allowedOutputRoot ".package-msix-junction-$uniqueId"
$junctionTarget = Join-Path $allowedOutputRoot ".package-msix-target-$uniqueId"

function Assert-True([bool]$Condition, [string]$Message)
{
    if (-not $Condition)
    {
        throw $Message
    }
}

function Invoke-PackageScript([string]$OutputDirectory, [string]$Version, [string]$MSBuildPath)
{
    $previousErrorActionPreference = $ErrorActionPreference
    try
    {
        $ErrorActionPreference = 'Continue'
        $lines = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript `
            -OutputDirectory $OutputDirectory `
            -Version $Version `
            -MSBuildPath $MSBuildPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($lines | Out-String)
    }
}

function Assert-TestPathUnderOutputRoot([string]$Path)
{
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    Assert-True $fullPath.StartsWith($outputRootPrefix, [System.StringComparison]::OrdinalIgnoreCase) "Refusing to clean test path outside root/output: $fullPath"
}

Assert-True (-not (Test-Path -LiteralPath $testRoot)) "Unique test output path already exists: $testRoot"
Assert-True (-not (Test-Path -LiteralPath $junctionPath)) "Unique test junction path already exists: $junctionPath"
Assert-True (-not (Test-Path -LiteralPath $junctionTarget)) "Unique test junction target already exists: $junctionTarget"
[void][System.IO.Directory]::CreateDirectory($testRoot)

try
{
    $outsideOutput = [System.IO.Path]::GetFullPath((Join-Path $allowedOutputRoot '..\..\msix-safety-outside'))
    $outsideResult = Invoke-PackageScript $outsideOutput '1.0.0' 'msbuild-must-not-be-looked-up.exe'
    Assert-True ($outsideResult.ExitCode -ne 0 -and $outsideResult.Output -match 'inside.*root/output|outside.*root/output') 'RejectsOutputOutsideRootBeforeCallingMsBuild did not reject the outside output path first.'
    Assert-True (-not (Test-Path -LiteralPath $outsideOutput)) 'The outside output test unexpectedly created a directory.'
    Write-Output 'PASS RejectsOutputOutsideRootBeforeCallingMsBuild'

    $existingDirectory = Join-Path $testRoot 'existing'
    [void][System.IO.Directory]::CreateDirectory($existingDirectory)
    $existingArtifact = Join-Path $existingDirectory 'VoiceBridge-9.8.7-win-x64-unsigned.msix'
    [System.IO.File]::WriteAllText($existingArtifact, 'marker owned by package-msix safety test')
    $beforeHash = (Get-FileHash -LiteralPath $existingArtifact -Algorithm SHA256).Hash
    $existingResult = Invoke-PackageScript $existingDirectory '9.8.7' 'msbuild-must-not-be-looked-up.exe'
    $afterHash = (Get-FileHash -LiteralPath $existingArtifact -Algorithm SHA256).Hash
    Assert-True ($existingResult.ExitCode -ne 0 -and $existingResult.Output -match 'already exists|refusing to overwrite') 'RefusesExistingArtifactWithoutChangingMarker did not refuse the existing package.'
    Assert-True ($beforeHash -eq $afterHash) 'RefusesExistingArtifactWithoutChangingMarker changed the marker file.'
    Write-Output 'PASS RefusesExistingArtifactWithoutChangingMarker'

    [void][System.IO.Directory]::CreateDirectory($junctionTarget)
    New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget | Out-Null
    try
    {
        $junctionOutput = Join-Path $junctionPath 'child-output'
        $junctionResult = Invoke-PackageScript $junctionOutput '1.0.0' 'msbuild-must-not-be-looked-up.exe'
        Assert-True ($junctionResult.ExitCode -ne 0 -and $junctionResult.Output -match 'junction|symbolic link|reparse') 'RejectsOutputPathTraversingJunction did not reject the reparse point.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $junctionTarget 'child-output'))) 'RejectsOutputPathTraversingJunction wrote through the junction.'
        Write-Output 'PASS RejectsOutputPathTraversingJunction'
    }
    finally
    {
        if (Test-Path -LiteralPath $junctionPath)
        {
            Remove-Item -LiteralPath $junctionPath -Force
        }
    }

    $unavailableBuildOutput = Join-Path $testRoot 'missing-msbuild'
    $unavailableResult = Invoke-PackageScript $unavailableBuildOutput '1.0.0' 'msbuild-unavailable-for-safety-test.exe'
    $expectedPackage = Join-Path $unavailableBuildOutput 'VoiceBridge-1.0.0-win-x64-unsigned.msix'
    Assert-True ($unavailableResult.ExitCode -ne 0) 'LeavesNoFinalArtifactWhenMsBuildIsUnavailable unexpectedly succeeded.'
    Assert-True (-not (Test-Path -LiteralPath $expectedPackage)) 'LeavesNoFinalArtifactWhenMsBuildIsUnavailable left a final package behind.'
    Assert-True (-not (Test-Path -LiteralPath $unavailableBuildOutput)) 'LeavesNoFinalArtifactWhenMsBuildIsUnavailable left a staging output directory behind.'
    Write-Output 'PASS LeavesNoFinalArtifactWhenMsBuildIsUnavailable'
}
finally
{
    if (Test-Path -LiteralPath $junctionPath)
    {
        Remove-Item -LiteralPath $junctionPath -Force
    }

    if (Test-Path -LiteralPath $junctionTarget)
    {
        Assert-TestPathUnderOutputRoot $junctionTarget
        Remove-Item -LiteralPath $junctionTarget -Recurse -Force
    }

    if (Test-Path -LiteralPath $testRoot)
    {
        Assert-TestPathUnderOutputRoot $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
