using System.Xml.Linq;

namespace VoiceBridge.Tests;

public sealed class MsixPackagingContractTests
{
    private static readonly XNamespace PackageNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace UapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace RestrictedCapabilitiesNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    [Fact]
    public void PackageManifest_UsesUnsignedDevelopmentIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "root", "src", "VoiceBridge.Desktop", "Package.appxmanifest");
        Assert.True(File.Exists(manifestPath), $"Package manifest is missing: {manifestPath}");

        var manifest = XDocument.Load(manifestPath);
        var identity = manifest.Root?.Element(PackageNamespace + "Identity");

        Assert.NotNull(identity);
        Assert.Equal("404Builds.VoiceBridge.Local", (string?)identity.Attribute("Name"));
        Assert.Equal("CN=404 Builds, OID.2.25.311729368913984317654407730594956997722=1", (string?)identity.Attribute("Publisher"));
        Assert.Equal("1.0.0.0", (string?)identity.Attribute("Version"));
    }

    [Fact]
    public void PackageManifest_DeclaresGeneratedResourceLanguage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "root", "src", "VoiceBridge.Desktop", "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        var resources = manifest.Root?.Element(PackageNamespace + "Resources");

        Assert.NotNull(resources);
        Assert.Equal("x-generate", (string?)resources!.Element(PackageNamespace + "Resource")?.Attribute("Language"));
    }

    [Fact]
    public void PackageManifest_DeclaresFullTrustForDesktopApp()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "root", "src", "VoiceBridge.Desktop", "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        var capability = manifest.Descendants(RestrictedCapabilitiesNamespace + "Capability")
            .SingleOrDefault(element => (string?)element.Attribute("Name") == "runFullTrust");

        Assert.NotNull(capability);
    }

    [Fact]
    public void PackageManifest_ReferencesExistingArtwork()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "root", "src", "VoiceBridge.Desktop", "Package.appxmanifest");
        Assert.True(File.Exists(manifestPath), $"Package manifest is missing: {manifestPath}");

        var manifest = XDocument.Load(manifestPath);
        var visualElements = manifest.Descendants(UapNamespace + "VisualElements").Single();
        var defaultTile = visualElements.Element(UapNamespace + "DefaultTile");
        var artworkPaths = new[]
        {
            (string?)visualElements.Attribute("Square44x44Logo"),
            (string?)visualElements.Attribute("Square150x150Logo"),
            (string?)defaultTile?.Attribute("Wide310x150Logo"),
            (string?)manifest.Root?.Element(PackageNamespace + "Properties")?.Element(PackageNamespace + "Logo")
        };

        var assetsDirectory = Path.Combine(repositoryRoot, "root", "src", "VoiceBridge.Desktop", "Assets");
        foreach (var artworkPath in artworkPaths)
        {
            Assert.False(string.IsNullOrWhiteSpace(artworkPath));
            var assetName = artworkPath!
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            Assert.True(File.Exists(Path.Combine(assetsDirectory, Path.GetFileName(assetName))), $"Manifest asset is missing: {assetName}");
        }

        Assert.True(File.Exists(Path.Combine(assetsDirectory, "AppList.scale-100.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "AppList.targetsize-16_altform-unplated.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "Square150x150Logo.scale-200.png")));
        Assert.True(File.Exists(Path.Combine(assetsDirectory, "StoreLogo.scale-200.png")));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "root", "VoiceBridge.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the VoiceBridge repository root.");
    }
}
