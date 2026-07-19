using Vela.Packages.Tuf;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class TufVerifierTests
{
    [Fact]
    public void VerifyTargets_AcceptsSignedMetadata()
    {
        var (privateKey, publicKey) = TufTestSigner.CreateTestKey();
        var archivePath = WriteTempFile("archive.bin", [1, 2, 3, 4]);
        var targetPath = TufVerifier.BuildArchiveTargetPath("acme.demo", "1.0.0");
        var digest = TufVerifier.ComputeSha256Hex(archivePath);
        var targets = new Dictionary<string, TufTargetFile>
        {
            [targetPath] = new(new FileInfo(archivePath).Length, new TufTargetHashes(digest))
        };

        var (rootJson, targetsJson) = TufTestSigner.SignTargets(privateKey, publicKey, targets);
        var verified = TufVerifier.VerifyTargets(rootJson, targetsJson);

        Assert.True(verified.ContainsKey(targetPath));
        TufVerifier.VerifyArchive(archivePath, verified[targetPath]);
        privateKey.Dispose();
    }

    [Fact]
    public void VerifyTargets_RejectsTamperedHash()
    {
        var (privateKey, publicKey) = TufTestSigner.CreateTestKey();
        var archivePath = WriteTempFile("archive.bin", [9, 8, 7]);
        var targetPath = TufVerifier.BuildArchiveTargetPath("acme.demo", "2.0.0");
        var targets = new Dictionary<string, TufTargetFile>
        {
            [targetPath] = new(new FileInfo(archivePath).Length, new TufTargetHashes("deadbeef"))
        };

        var (rootJson, targetsJson) = TufTestSigner.SignTargets(privateKey, publicKey, targets);
        var verified = TufVerifier.VerifyTargets(rootJson, targetsJson);

        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyArchive(archivePath, verified[targetPath]));
        privateKey.Dispose();
    }

    private static string WriteTempFile(string name, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "vela-packages-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }
}
