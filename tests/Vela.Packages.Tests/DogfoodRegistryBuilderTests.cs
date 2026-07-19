using Vela.Packages.Tests;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class DogfoodRegistryBuilderTests
{
    [Fact]
    public void BuildDogfoodRegistryExample()
    {
        if (Environment.GetEnvironmentVariable("VELA_BUILD_DOGFOOD") != "1")
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var packageDirectory = Path.Combine(repoRoot, "examples", "registry-dogfood", "package");
        var registryRoot = Path.Combine(repoRoot, "examples", "registry-dogfood", "registry-data");
        if (Directory.Exists(registryRoot))
        {
            Directory.Delete(registryRoot, recursive: true);
        }

        DogfoodRegistryBuilder.Build(packageDirectory, registryRoot);
        Assert.True(File.Exists(Path.Combine(registryRoot, "tuf", "root.json")));
        Assert.True(File.Exists(Path.Combine(registryRoot, "dogfood.lib", "0.1.0", "package.vlpkg")));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Vela.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
