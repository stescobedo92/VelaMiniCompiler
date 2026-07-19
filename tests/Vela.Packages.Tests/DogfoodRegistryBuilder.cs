using Vela.Packages;
using Vela.Packages.Tuf;

namespace Vela.Packages.Tests;

public static class DogfoodRegistryBuilder
{
    public static void Build(string packageDirectory, string registryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(registryRoot);

        var manifest = VelaPackageManifest.FromToml(Path.Combine(packageDirectory, "vela.toml"));
        var versionDirectory = Path.Combine(registryRoot, manifest.Name, manifest.Version);
        Directory.CreateDirectory(versionDirectory);

        var archivePath = Path.Combine(versionDirectory, $"package{VelaDependencyArchive.Extension}");
        VelaDependencyArchive.Pack(packageDirectory, archivePath);

        var (privateKey, publicKey) = TufTestSigner.CreateTestKey();
        try
        {
            var targetPath = TufVerifier.BuildArchiveTargetPath(manifest.Name, manifest.Version);
            var targets = new Dictionary<string, TufTargetFile>
            {
                [targetPath] = new(
                    new FileInfo(archivePath).Length,
                    new TufTargetHashes(TufVerifier.ComputeSha256Hex(archivePath)))
            };
            var (rootJson, targetsJson) = TufTestSigner.SignTargets(privateKey, publicKey, targets);
            var tufDirectory = Path.Combine(registryRoot, "tuf");
            Directory.CreateDirectory(tufDirectory);
            File.WriteAllText(Path.Combine(tufDirectory, "root.json"), rootJson);
            File.WriteAllText(Path.Combine(tufDirectory, "targets.json"), targetsJson);
        }
        finally
        {
            privateKey.Dispose();
        }
    }
}
