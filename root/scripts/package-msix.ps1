[CmdletBinding()]
param(
    [string]$OutputDirectory = 'root\output\msix-test',

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

$packageName = "VoiceBridge-$Version-win-x64-unsigned.msix"
$packagePath = Join-Path $outputPath $packageName
if (Test-Path -LiteralPath $packagePath)
{
    throw "MSIX package already exists; refusing to overwrite it: $packagePath"
}

$msbuildCommand = Get-Command $MSBuildPath -ErrorAction Stop | Select-Object -First 1
$projectPath = Join-Path $repositoryRoot 'root\src\VoiceBridge.Desktop\VoiceBridge.Desktop.csproj'
$assetsPath = Join-Path $repositoryRoot 'root\src\VoiceBridge.Desktop\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf))
{
    throw "Desktop dependencies have not been restored. Run 'dotnet restore root/src/VoiceBridge.Desktop/VoiceBridge.Desktop.csproj' first."
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
        '/p:UapAppxPackageBuildMode=SideloadOnly',
        "/p:AppxPackageVersion=$packageVersion",
        "/p:AppxPackageDir=$stagePath/",
        '/m',
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
    if ($bundles.Count -gt 0)
    {
        throw 'MSIX build produced a bundle; this path must produce one x64 .msix.'
    }
    if ($packages.Count -ne 1)
    {
        throw "Expected exactly one .msix in staging; found $($packages.Count)."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Xml.Linq
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try
    {
        $entries = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries)
        {
            [void]$entries.Add($entry.FullName.Replace('\', '/'))
        }

        Assert-PackageEntry $entries 'AppxManifest.xml'
        Assert-PackageEntry $entries 'VoiceBridge.exe'
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
        if ($null -eq $identity -or
            $identity.Attribute('Name').Value -ne '404Builds.VoiceBridge.Local' -or
            $identity.Attribute('Publisher').Value -ne 'CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1' -or
            $identity.Attribute('Version').Value -ne $packageVersion -or
            $identity.Attribute('ProcessorArchitecture').Value -ne 'x64')
        {
            throw "Generated MSIX identity or version does not match the approved unsigned x64 test identity ($packageVersion)."
        }
    }
    finally
    {
        $archive.Dispose()
    }

    $makeAppxCandidates = @()
    $windowsKitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsKitsRoot -PathType Container)
    {
        $makeAppxCandidates += @(Get-ChildItem -LiteralPath $windowsKitsRoot -Filter 'MakeAppx.exe' -File -Recurse -ErrorAction SilentlyContinue)
    }

    $assetManifest = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($packageFolder in $assetManifest.packageFolders.PSObject.Properties.Name)
    {
        $buildToolsRoot = Join-Path $packageFolder 'microsoft.windows.sdk.buildtools'
        if (Test-Path -LiteralPath $buildToolsRoot -PathType Container)
        {
            $makeAppxCandidates += @(Get-ChildItem -LiteralPath $buildToolsRoot -Filter 'MakeAppx.exe' -File -Recurse -ErrorAction SilentlyContinue)
        }
    }

    $makeAppx = $makeAppxCandidates |
        Where-Object { $_.FullName -match '[\\/](x64|x86)[\\/]MakeAppx\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $makeAppx)
    {
        throw 'Windows SDK MakeAppx.exe was not found; install the Windows SDK to validate the MSIX package.'
    }

    $validationPath = Join-Path $stagePath 'validated-package'
    & $makeAppx.FullName unpack /p $packages[0].FullName /d $validationPath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Windows SDK MakeAppx validation failed with exit code $LASTEXITCODE."
    }
    Assert-NoReparsePointsWithin $validationPath

    [System.IO.File]::Move($packages[0].FullName, $packagePath)
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
[pscustomobject]@{
    PackagePath = $packageInfo.FullName
    Bytes = $packageInfo.Length
    SHA256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
}
