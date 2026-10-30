using System.Text.Json.Nodes;
using Vela.Packages.Tuf;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class TufVerifierTests
{
    private const string TargetPath = "acme.demo/1.0.0/package.vlpkg";

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

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"signed\":null,\"signatures\":[]}")]
    public void VerifyTargets_RejectsInvalidEnvelopesWithVerificationError(string malformed)
    {
        var (rootJson, targetsJson) = CreateMetadata();

        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(malformed, targetsJson));
        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(rootJson, malformed));
    }

    [Theory]
    [InlineData("signed")]
    [InlineData("signatures")]
    [InlineData("signed._type")]
    [InlineData("signed.version")]
    [InlineData("signed.expires")]
    [InlineData("signed.keys")]
    [InlineData("signed.roles")]
    [InlineData("signed.keys.dogfood-test-key.keytype")]
    [InlineData("signed.keys.dogfood-test-key.keyval")]
    [InlineData("signed.keys.dogfood-test-key.keyval.public")]
    [InlineData("signed.roles.root.keyids")]
    [InlineData("signed.roles.root.threshold")]
    public void VerifyTargets_RejectsNullAndMissingRootMembers(string memberPath)
    {
        var (rootJson, targetsJson) = CreateMetadata();
        foreach (var omit in new[] { false, true })
        {
            var root = JsonNode.Parse(rootJson)!.AsObject();
            RemoveOrNullMember(root, memberPath, omit);
            Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(root.ToJsonString(), targetsJson));
        }
    }

    [Theory]
    [InlineData("signed")]
    [InlineData("signatures")]
    [InlineData("signed._type")]
    [InlineData("signed.version")]
    [InlineData("signed.expires")]
    [InlineData("signed.targets")]
    public void VerifyTargets_RejectsNullAndMissingTargetsMembers(string memberPath)
    {
        var (rootJson, targetsJson) = CreateMetadata();
        foreach (var omit in new[] { false, true })
        {
            var targets = JsonNode.Parse(targetsJson)!.AsObject();
            RemoveOrNullMember(targets, memberPath, omit);
            Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(rootJson, targets.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hashes")]
    [InlineData("hashes.sha256")]
    public void VerifyTargets_RejectsNullAndMissingTargetFileMembers(string memberPath)
    {
        var (rootJson, targetsJson) = CreateMetadata();
        foreach (var omit in new[] { false, true })
        {
            var targets = JsonNode.Parse(targetsJson)!.AsObject();
            var target = targets["signed"]!["targets"]![TargetPath]!.AsObject();
            RemoveOrNullMember(target, memberPath, omit);
            Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(rootJson, targets.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("role")]
    [InlineData("keyid")]
    [InlineData("root-signature")]
    [InlineData("targets-signature")]
    [InlineData("target")]
    public void VerifyTargets_RejectsNullCollectionElements(string element)
    {
        var (rootJson, targetsJson) = CreateMetadata();
        var root = JsonNode.Parse(rootJson)!.AsObject();
        var targets = JsonNode.Parse(targetsJson)!.AsObject();
        switch (element)
        {
            case "key":
                root["signed"]!["keys"]![TufTestSigner.TestKeyId] = null;
                break;
            case "role":
                root["signed"]!["roles"]!["root"] = null;
                break;
            case "keyid":
                root["signed"]!["roles"]!["root"]!["keyids"]!.AsArray().Add(null);
                break;
            case "root-signature":
                root["signatures"]!.AsArray().Add(null);
                break;
            case "targets-signature":
                targets["signatures"]!.AsArray().Add(null);
                break;
            case "target":
                targets["signed"]!["targets"]![TargetPath] = null;
                break;
        }

        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyTargets(root.ToJsonString(), targets.ToJsonString()));
    }

    [Fact]
    public void VerifyArchive_RejectsMissingHashesWithVerificationError()
    {
        var archivePath = WriteTempFile("archive.bin", [1]);
        Assert.Throws<TufVerificationException>(() => TufVerifier.VerifyArchive(archivePath, new TufTargetFile(1, null!)));
    }

    private static (string RootJson, string TargetsJson) CreateMetadata()
    {
        var (key, publicKey) = TufTestSigner.CreateTestKey();
        using (key)
        {
            return TufTestSigner.SignTargets(key, publicKey, new Dictionary<string, TufTargetFile>
            {
                [TargetPath] = new(1, new TufTargetHashes(new string('0', 64)))
            });
        }
    }

    private static void RemoveOrNullMember(JsonObject root, string memberPath, bool omit)
    {
        var components = memberPath.Split('.');
        var parent = root;
        foreach (var component in components[..^1])
        {
            parent = parent[component]!.AsObject();
        }

        if (omit)
        {
            parent.Remove(components[^1]);
        }
        else
        {
            parent[components[^1]] = null;
        }
    }

    private static string WriteTempFile(string name, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "vela-packages-tests", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }
}
