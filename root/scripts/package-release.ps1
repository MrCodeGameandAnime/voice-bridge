[CmdletBinding()]
param(
    [string]$OutputDirectory = 'root\output\gate10-release',

    [string]$Version = '1.0.0',

    [string]$MSBuildPath = 'MSBuild.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$allowedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'root\output'))
$outputRootPrefix = $allowedOutputRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Resolve-FromRepositoryRoot([string]$Path)
{
    if ([System.IO.Path]::IsPathRooted($Path))
    {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Assert-NoOutputReparsePoints([string]$Path)
{
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($outputRootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Output path must be inside '$allowedOutputRoot': $fullPath"
    }

    $relativePath = $fullPath.Substring($outputRootPrefix.Length)
    $currentPath = $allowedOutputRoot
    $pathsToCheck = @($currentPath)
    foreach ($segment in $relativePath.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries))
    {
        $currentPath = Join-Path $currentPath $segment
        $pathsToCheck += $currentPath
    }

    foreach ($candidate in $pathsToCheck)
    {
        if (Test-Path -LiteralPath $candidate)
        {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
            {
                throw "Release output cannot pass through a junction or symbolic link: $candidate"
            }
        }
    }
}

function Add-ZipEntry(
    [System.IO.Compression.ZipArchive]$Archive,
    [string]$SourcePath,
    [string]$EntryPath)
{
    $entry = $Archive.CreateEntry($EntryPath, [System.IO.Compression.CompressionLevel]::Optimal)
    $sourceStream = [System.IO.File]::OpenRead($SourcePath)
    try
    {
        $entryStream = $entry.Open()
        try
        {
            $sourceStream.CopyTo($entryStream)
        }
        finally
        {
            $entryStream.Dispose()
        }
    }
    finally
    {
        $sourceStream.Dispose()
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "Version must use major.minor.patch format; received '$Version'."
}

$outputPath = Resolve-FromRepositoryRoot $OutputDirectory
if (-not $outputPath.StartsWith($outputRootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "Release output must be inside '$allowedOutputRoot'."
}

$archivePath = Join-Path $outputPath "VoiceBridge-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archivePath)
{
    throw "Release archive already exists; refusing to overwrite it: $archivePath"
}

Assert-NoOutputReparsePoints $outputPath
[void][System.IO.Directory]::CreateDirectory($outputPath)
Assert-NoOutputReparsePoints $outputPath

$projectPath = Join-Path $repositoryRoot 'root\src\VoiceBridge.Desktop\VoiceBridge.Desktop.csproj'
$msbuildCommand = Get-Command $MSBuildPath -ErrorAction Stop | Select-Object -First 1
$publishPath = Join-Path $allowedOutputRoot ".voicebridge-publish-$([Guid]::NewGuid().ToString('N'))"
$temporaryArchivePath = "$archivePath.$([Guid]::NewGuid().ToString('N')).tmp"
$archive = $null
$archiveStream = $null
$entryCount = 0
$stageCreated = $false
$archiveCreated = $false

try
{
    [void][System.IO.Directory]::CreateDirectory($publishPath)
    $stageCreated = $true
    Assert-NoOutputReparsePoints $publishPath

    & $msbuildCommand.Source $projectPath /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true "/p:PublishDir=$publishPath/" /p:PublishSingleFile=false /m /v:minimal
    if ($LASTEXITCODE -ne 0)
    {
        throw "Self-contained Release publish failed with exit code $LASTEXITCODE."
    }

    foreach ($requiredFile in @('VoiceBridge.exe', 'VoiceBridge.pri', 'Assets\Square150x150Logo.png'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $publishPath $requiredFile) -PathType Leaf))
        {
            throw "Fresh publish output is incomplete; missing '$requiredFile'."
        }
    }

    $docsRoot = Join-Path $repositoryRoot 'docs'
    $documentationFiles = @('README.md', 'privacy.md', 'support.md', 'release.md')
    foreach ($document in $documentationFiles)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $docsRoot $document) -PathType Leaf))
        {
            throw "Release documentation is missing: $document"
        }
    }

    $screenshotsRoot = Join-Path $docsRoot 'screenshots'
    $screenshotNames = @('welcome.png', 'scan-summary.png', 'import-results.png')
    foreach ($screenshot in $screenshotNames)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $screenshotsRoot $screenshot) -PathType Leaf))
        {
            throw "Release screenshot is missing: $screenshot"
        }
    }

    $publishItems = @(Get-ChildItem -LiteralPath $publishPath -Force -Recurse)
    $reparseItems = @($publishItems | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($reparseItems.Count -gt 0)
    {
        throw "Fresh publish output contains a junction or symbolic link: $($reparseItems[0].FullName)"
    }

    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [System.IO.File]::Open(
        $temporaryArchivePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $archive = [System.IO.Compression.ZipArchive]::new(
        $archiveStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)

    $publishPrefix = $publishPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in $publishItems | Where-Object { -not $_.PSIsContainer })
    {
        if ($file.Extension -ieq '.pdb')
        {
            continue
        }

        $relativePath = $file.FullName.Substring($publishPrefix.Length).Replace('\', '/')
        Add-ZipEntry $archive $file.FullName $relativePath
        $entryCount++
    }

    foreach ($document in $documentationFiles)
    {
        Add-ZipEntry $archive (Join-Path $docsRoot $document) $document
        $entryCount++
    }

    foreach ($screenshot in $screenshotNames)
    {
        Add-ZipEntry $archive (Join-Path $screenshotsRoot $screenshot) "screenshots/$screenshot"
        $entryCount++
    }

    $archive.Dispose()
    $archive = $null
    $archiveStream = $null
    [System.IO.File]::Move($temporaryArchivePath, $archivePath)
    $archiveCreated = $true
}
finally
{
    if ($null -ne $archive)
    {
        $archive.Dispose()
    }
    elseif ($null -ne $archiveStream)
    {
        $archiveStream.Dispose()
    }

    if (Test-Path -LiteralPath $temporaryArchivePath)
    {
        Remove-Item -LiteralPath $temporaryArchivePath -Force
    }

    if ($stageCreated -and (Test-Path -LiteralPath $publishPath))
    {
        try
        {
            Assert-NoOutputReparsePoints $publishPath
            Remove-Item -LiteralPath $publishPath -Recurse -Force
        }
        catch
        {
            Write-Warning "Could not safely remove the fresh publish staging folder '$publishPath': $($_.Exception.Message)"
        }
    }
}

if (-not $archiveCreated)
{
    throw 'Release package was not created.'
}

$archiveInfo = Get-Item -LiteralPath $archivePath
[pscustomobject]@{
    ArchivePath = $archiveInfo.FullName
    Entries = $entryCount
    Bytes = $archiveInfo.Length
    SHA256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
}
