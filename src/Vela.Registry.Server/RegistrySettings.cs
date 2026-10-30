using System.Security.Cryptography;
using System.Text;

namespace Vela.Registry.Server;

internal sealed class RegistrySettings
{
    private RegistrySettings(string root, string? key, bool allowInsecureHttp, long maxPackageBytes, int maxConcurrentUploads, long maxExpandedBytes, int maxArchiveEntries)
    {
        Root = Path.GetFullPath(root);
        KeyHash = key is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));
        AllowInsecureHttp = allowInsecureHttp;
        MaxPackageBytes = maxPackageBytes;
        MaxConcurrentUploads = maxConcurrentUploads;
        MaxExpandedBytes = maxExpandedBytes;
        MaxArchiveEntries = maxArchiveEntries;
    }

    public string Root { get; }
    public byte[]? KeyHash { get; }
    public bool AllowInsecureHttp { get; }
    public long MaxPackageBytes { get; }
    public int MaxConcurrentUploads { get; }
    public long MaxExpandedBytes { get; }
    public int MaxArchiveEntries { get; }

    public static RegistrySettings FromConfiguration(IConfiguration configuration)
    {
        var key = configuration["Registry:ApiKey"];
        if (string.IsNullOrEmpty(key))
        {
            key = null;
        }
        else if (key.Length is < 32 or > 512 || key.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Registry:ApiKey must contain 32 to 512 non-whitespace characters.");
        }

        var maximum = configuration.GetValue("Registry:MaxPackageBytes", 32L * 1024 * 1024);
        var concurrency = configuration.GetValue("Registry:MaxConcurrentUploads", 4);
        var expanded = configuration.GetValue("Registry:MaxExpandedBytes", 256L * 1024 * 1024);
        var entries = configuration.GetValue("Registry:MaxArchiveEntries", 10000);
        if (maximum is < 1 or > 256L * 1024 * 1024 || concurrency is < 1 or > 64
            || expanded is < 1 or > 256L * 1024 * 1024 || entries is < 1 or > 10000)
        {
            throw new InvalidOperationException("Registry limits must be 1..268435456 bytes, 1..64 concurrent uploads, and 1..10000 archive entries.");
        }

        return new RegistrySettings(
            configuration["RegistryRoot"] ?? Path.Combine(Directory.GetCurrentDirectory(), "registry-data"),
            key,
            configuration.GetValue("Registry:AllowInsecureHttp", false),
            maximum,
            concurrency,
            expanded,
            entries);
    }
}
