using System.Text.Json;

namespace Vela.Packages.Tuf;

internal sealed record TufTrustState(string RootJson, string BootstrapDigest, int TargetsVersion, string? TargetsDigest);

/// <summary>Serializes updates to the local, registry-specific trust state across processes.</summary>
internal sealed class TufTrustStore : IDisposable
{
    private readonly string _statePath;
    private readonly FileStream _lock;

    private TufTrustStore(string statePath, FileStream fileLock)
    {
        _statePath = statePath;
        _lock = fileLock;
    }

    public static async Task<TufTrustStore> OpenAsync(string registryCacheRoot, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(registryCacheRoot, "trust");
        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(Path.Combine(directory, "update.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return new TufTrustStore(Path.Combine(directory, "state.json"), stream);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public TufTrustState? Load()
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(_statePath);
            // A bounded 4 MiB root can expand sixfold when escaped as a JSON string.
            if (info.Length > 32 * 1024 * 1024)
            {
                throw new TufVerificationException("Persisted TUF trust state exceeds its size limit.");
            }

            var state = JsonSerializer.Deserialize(File.ReadAllText(_statePath), TufJsonContext.Default.TufTrustState)
                ?? throw new TufVerificationException("Persisted TUF trust state is empty.");
            if (state.TargetsVersion < 0 || (state.TargetsVersion > 0 && string.IsNullOrWhiteSpace(state.TargetsDigest))
                || string.IsNullOrWhiteSpace(state.BootstrapDigest))
            {
                throw new TufVerificationException("Persisted TUF trust state is invalid.");
            }

            return state;
        }
        catch (JsonException ex)
        {
            throw new TufVerificationException("Persisted TUF trust state is invalid.", ex);
        }
    }

    public void Save(TufTrustState state)
    {
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, TufJsonContext.Default.TufTrustState);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose() => _lock.Dispose();
}
