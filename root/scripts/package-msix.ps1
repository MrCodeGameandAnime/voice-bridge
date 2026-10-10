[CmdletBinding()]
param(
    [string]$OutputDirectory = 'root\output\msix-store-upload',

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

function Assert-OutputPath([string]$Path)
{
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($outputRootPrefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Output directory must stay inside root/output: $fullPath"
    }

    $volumeRoot = [System.IO.Path]::GetPathRoot($fullPath)
    $currentPath = $volumeRoot
    $pathsToCheck = @($currentPath)
    $pathFromVolumeRoot = $fullPath.Substring($volumeRoot.Length)
    foreach ($segment in $pathFromVolumeRoot.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries))
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
                throw "Output directory cannot pass through a junction or symbolic link: $candidate"
            }
        }
    }
}

function Assert-NoReparsePointsWithin([string]$Path)
{
    Assert-OutputPath $Path
    $items = @(Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop)
    $reparseItem = $items | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1
    if ($null -ne $reparseItem)
    {
        throw "MSIX staging contains a junction or symbolic link: $($reparseItem.FullName)"
    }
}

function Assert-PackageEntry(
    [System.Collections.Generic.HashSet[string]]$Entries,
    [string]$EntryPath)
{
    if (-not $Entries.Contains($EntryPath))
    {
        throw "MSIX package is incomplete; missing '$EntryPath'."
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "Version must use major.minor.patch format; received '$Version'."
}

$packageVersion = "$Version.0"
$outputPath = Resolve-FromRepositoryRoot $OutputDirectory
Assert-OutputPath $outputPath

$packageName = "VoiceBridge-$Version-win-x64.msix"
$packageUploadName = "VoiceBridge-$Version-win-x64.msixupload"
$packagePath = Join-Path $outputPath $packageName
$packageUploadPath = Join-Path $outputPath $packageUploadName
if ((Test-Path -LiteralPath $packagePath) -or (Test-Path -LiteralPath $packageUploadPath))
{
    throw "Store package output already exists; refusing to overwrite it: $outputPath"
}

$msbuildCommand = Get-Command $MSBuildPath -ErrorAction Stop | Select-Object -First 1
$projectPath = Join-Path $repositoryRoot 'root\src\VoiceBridge.Desktop\VoiceBridge.Desktop.csproj'
$assetsPath = Join-Path $repositoryRoot 'root\src\VoiceBridge.Desktop\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf))
{
    throw "Desktop dependencies have not been restored. Run 'dotnet restore root/src/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj' first."
}

$windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppxCandidates = @()
if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container)
{
    $makeAppxCandidates = @(Get-ChildItem -LiteralPath $windowsKitsRoot -Filter 'MakeAppx.exe' -File -Recurse -ErrorAction SilentlyContinue)
}

$makeAppx = $makeAppxCandidates |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]MakeAppx\.exe$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if ($null -eq $makeAppx)
{
    throw 'Windows SDK x64 MakeAppx.exe was not found; install the Windows SDK to build and validate the MSIX package.'
}

$stagePath = Join-Path $allowedOutputRoot ".voicebridge-msix-staging-$([Guid]::NewGuid().ToString('N'))"
$stageCreated = $false
$packageCreated = $false

try
{
    [void][System.IO.Directory]::CreateDirectory($allowedOutputRoot)
    [void][System.IO.Directory]::CreateDirectory($outputPath)
    Assert-OutputPath $outputPath
    [void][System.IO.Directory]::CreateDirectory($stagePath)
    $stageCreated = $true
    Assert-OutputPath $stagePath

    $msbuildArguments = @(
        $projectPath,
        '/t:Build',
        '/p:Configuration=Release',
        '/p:Platform=x64',
        '/p:RuntimeIdentifier=win-x64',
        '/p:SelfContained=true',
        '/p:VoiceBridgeBuildMsix=true',
        '/p:EnableMsixTooling=true',
        '/p:WindowsPackageType=MSIX',
        '/p:GenerateAppxPackageOnBuild=true',
        '/p:AppxPackageSigningEnabled=false',
        '/p:AppxBundle=Never',
        '/p:UapAppxPackageBuildMode=StoreUpload',
        "/p:MakeAppxExeFullPath=$($makeAppx.FullName)",
        "/p:AppxPackageVersion=$packageVersion",
        "/p:AppxPackageDir=$stagePath/",
        '/m:1',
        '/nr:false',
        '/v:minimal'
    )

    & $msbuildCommand.Source @msbuildArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Unsigned MSIX Release build failed with exit code $LASTEXITCODE."
    }

    Assert-NoReparsePointsWithin $stagePath

    $packages = @(Get-ChildItem -LiteralPath $stagePath -Filter '*.msix' -File -Recurse)
    $bundles = @(Get-ChildItem -LiteralPath $stagePath -Filter '*.msixbundle' -File -Recurse)
    $packageUploads = @(Get-ChildItem -LiteralPath $stagePath -Filter '*.msixupload' -File -Recurse)
    if ($bundles.Count -gt 0)
    {
        throw 'MSIX build produced a bundle; this path must produce one x64 .msix.'
    }
    if ($packages.Count -ne 1)
    {
        throw "Expected exactly one .msix in staging; found $($packages.Count)."
    }
    if ($packageUploads.Count -ne 1)
    {
        throw "Expected exactly one .msixupload in staging; found $($packageUploads.Count)."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Xml.Linq
    $uploadArchive = [System.IO.Compression.ZipFile]::OpenRead($packageUploads[0].FullName)
    try
    {
        $uploadPackages = @($uploadArchive.Entries | Where-Object { $_.FullName -match '\.msix$' })
        $uploadSymbols = @($uploadArchive.Entries | Where-Object { $_.FullName -match '\.appxsym$' })
        if ($uploadPackages.Count -ne 1)
        {
            throw "Expected exactly one MSIX inside the Store upload artifact; found $($uploadPackages.Count)."
        }
        if ($uploadSymbols.Count -ne 1)
        {
            throw "Expected exactly one symbol package inside the Store upload artifact; found $($uploadSymbols.Count)."
        }

        $uploadMsixPath = Join-Path $stagePath 'store-upload-package.msix'
        $uploadPackageStream = $uploadPackages[0].Open()
        $stagedUploadPackageStream = [System.IO.File]::Open(
            $uploadMsixPath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try
        {
            $uploadPackageStream.CopyTo($stagedUploadPackageStream)
        }
        finally
        {
            $stagedUploadPackageStream.Dispose()
            $uploadPackageStream.Dispose()
        }
        if ((Get-Item -LiteralPath $uploadMsixPath).Length -ne $uploadPackages[0].Length)
        {
            throw 'Extracted Store upload MSIX size does not match the package embedded in the .msixupload.'
        }
    }
    finally
    {
        $uploadArchive.Dispose()
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($uploadMsixPath)
    try
    {
        $entries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries)
        {
            [void]$entries.Add($entry.FullName.Replace('\', '/'))
        }

        Assert-PackageEntry $entries 'AppxManifest.xml'
        Assert-PackageEntry $entries 'VoiceBridge.exe'
        Assert-PackageEntry $entries 'Assets/AppIcon.ico'
        Assert-PackageEntry $entries 'resources.pri'
        Assert-PackageEntry $entries 'Assets/AppList.png'
        Assert-PackageEntry $entries 'Assets/Square150x150Logo.png'
        Assert-PackageEntry $entries 'Assets/Wide310x150Logo.png'
        Assert-PackageEntry $entries 'Assets/StoreLogo.png'
        Assert-PackageEntry $entries 'Assets/AppList.scale-100.png'
        Assert-PackageEntry $entries 'Assets/AppList.targetsize-16_altform-unplated.png'
        Assert-PackageEntry $entries 'Assets/Square150x150Logo.scale-200.png'
        Assert-PackageEntry $entries 'Assets/StoreLogo.scale-200.png'
        if ($entries.Contains('AppxSignature.p7x'))
        {
            throw 'MSIX package unexpectedly contains a signature block; unsigned output was required.'
        }

        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        $manifestStream = $manifestEntry.Open()
        try
        {
            $manifest = [System.Xml.Linq.XDocument]::Load($manifestStream)
        }
        finally
        {
            $manifestStream.Dispose()
        }

        $packageNamespace = [System.Xml.Linq.XNamespace]::Get('http://schemas.microsoft.com/appx/manifest/foundation/windows10')
        $identity = $manifest.Root.Element($packageNamespace + 'Identity')
        $properties = $manifest.Root.Element($packageNamespace + 'Properties')
        if ($null -eq $identity -or
            $identity.Attribute('Name').Value -ne '404Builds.VoiceBridge' -or
            $identity.Attribute('Publisher').Value -ne 'CN=2BB531F8-77DD-4D82-AA5F-230F26EB09EE' -or
            $identity.Attribute('Version').Value -ne $packageVersion -or
            $identity.Attribute('ProcessorArchitecture').Value -ne 'x64' -or
            $null -eq $properties -or
            $properties.Element($packageNamespace + 'DisplayName').Value -ne 'Voice Bridge' -or
            $properties.Element($packageNamespace + 'PublisherDisplayName').Value -ne '404 Builds')
        {
            throw "Generated Store upload MSIX identity, display names, or version do not match the Partner Center product identity ($packageVersion)."
        }
    }
    finally
    {
        $archive.Dispose()
    }

    $validationPath = Join-Path $stagePath 'validated-package'
    & $makeAppx.FullName unpack /p $uploadMsixPath /d $validationPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Windows SDK MakeAppx validation failed with exit code $LASTEXITCODE."
    }
    Assert-NoReparsePointsWithin $validationPath

    [System.IO.File]::Move($uploadMsixPath, $packagePath)
    [System.IO.File]::Move($packageUploads[0].FullName, $packageUploadPath)
    $packageCreated = $true
}
finally
{
    if ($stageCreated -and (Test-Path -LiteralPath $stagePath))
    {
        try
        {
            Assert-NoReparsePointsWithin $stagePath
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
        catch
        {
            Write-Warning "Could not safely remove the MSIX staging folder '$stagePath': $($_.Exception.Message)"
        }
    }
}

if (-not $packageCreated)
{
    throw 'Unsigned MSIX package was not created.'
}

$packageInfo = Get-Item -LiteralPath $packagePath
$uploadInfo = Get-Item -LiteralPath $packageUploadPath
[pscustomobject]@{
    PackagePath = $packageInfo.FullName
    PackageBytes = $packageInfo.Length
    PackageSHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    UploadPath = $uploadInfo.FullName
    UploadBytes = $uploadInfo.Length
    UploadSHA256 = (Get-FileHash -LiteralPath $packageUploadPath -Algorithm SHA256).Hash
}
