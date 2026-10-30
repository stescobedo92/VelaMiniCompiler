using System.Globalization;
using System.Text.Json;
using Vela.Packages;

namespace Vela.Registry.Server;

internal sealed partial class RegistryStore
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RegistrySettings _settings;
    private readonly ILogger<RegistryStore> _logger;

    public RegistryStore(RegistrySettings settings, ILogger<RegistryStore> logger)
    {
        _settings = settings;
        _logger = logger;
        Directory.CreateDirectory(settings.Root);
    }

    public IResult Index(string name)
    {
        if (!ValidName(name))
        {
            return InvalidIdentity();
        }

        var directory = Path.Combine(_settings.Root, name);
        if (!Directory.Exists(directory) || HasLink(directory))
        {
            return Results.NotFound();
        }

        var versions = Directory.EnumerateDirectories(directory)
            .Where(path => !HasLink(path))
            .Select(Path.GetFileName)
            .Where(static version => version is not null && ValidVersion(version))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return Results.Json(new { name, versions });
    }

    public IResult Manifest(string name, string version)
    {
        if (!TryPackageDirectory(name, version, out var directory))
        {
            return InvalidIdentity();
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath) && !HasLink(manifestPath))
        {
            return Results.File(manifestPath, "application/json");
        }

        var tomlPath = Path.Combine(directory, "vela.toml");
        if (File.Exists(tomlPath) && !HasLink(tomlPath))
        {
            try
            {
                var manifest = VelaPackageManifest.FromToml(tomlPath);
                return manifest.Name == name && manifest.Version == version ? Results.Json(manifest) : Results.NotFound();
            }
            catch (InvalidDataException)
            {
                return Results.NotFound();
            }
        }

        var archivePath = Path.Combine(directory, "package.vlpkg");
        return File.Exists(archivePath) && !HasLink(archivePath)
            ? Results.Json(new VelaPackageManifest(name, version, VelaPackageKind.Library, new Dictionary<string, string>()))
            : Results.NotFound();
    }

    public IResult Archive(string name, string version)
    {
        if (!TryPackageDirectory(name, version, out var directory))
        {
            return InvalidIdentity();
        }

        var path = Path.Combine(directory, "package.vlpkg");
        return File.Exists(path) && !HasLink(path)
            ? Results.File(path, "application/zip", "package.vlpkg")
            : Results.NotFound();
    }

    public IResult Metadata(string fileName)
    {
        var path = Path.Combine(_settings.Root, "tuf", fileName);
        return File.Exists(path) && !HasLink(path) ? Results.File(path, "application/json") : Results.NotFound();
    }

    public IResult VersionedRoot(string version)
        => int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0 && number.ToString(CultureInfo.InvariantCulture) == version
            ? Metadata(version + ".root.json")
            : Results.BadRequest();

    public async Task<IResult> PublishAsync(string name, string version, HttpRequest request)
    {
        if (!TryPackageDirectory(name, version, out var destination))
        {
            return InvalidIdentity();
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return AlreadyPublished();
        }

        if (request.ContentLength > _settings.MaxPackageBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var stagingRoot = Path.Combine(_settings.Root, ".uploads");
        if (HasLink(stagingRoot))
        {
            return Results.Problem("Registry storage is unavailable.");
        }

        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, "package.vlpkg");
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long length = 0;
                while (true)
                {
                    var read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                    if (length > _settings.MaxPackageBytes)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), request.HttpContext.RequestAborted).ConfigureAwait(false);
                }

                if (request.ContentLength is { } expected && expected != length)
                {
                    return Results.BadRequest("Incomplete package upload.");
                }
            }

            var extracted = Path.Combine(staging, "content");
            VelaDependencyArchive.Unpack(archivePath, extracted, _settings.MaxExpandedBytes, _settings.MaxArchiveEntries);
            var manifest = VelaPackageManifest.FromToml(Path.Combine(extracted, "vela.toml"));
            if (manifest.Name != name || manifest.Version != version)
            {
                return Results.BadRequest("Archive identity must match the package name and version in the URL.");
            }

            Directory.Delete(extracted, recursive: true);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, ManifestJsonOptions), request.HttpContext.RequestAborted)
                .ConfigureAwait(false);
            request.HttpContext.RequestAborted.ThrowIfCancellationRequested();
            // Directory.Move is an atomic, no-overwrite publication on the same
            // filesystem. Concurrent publishers can never replace the winner.
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (HasLink(destination))
            {
                return InvalidIdentity();
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination) || File.Exists(destination))
            {
                return AlreadyPublished();
            }

            return Results.Created($"/packages/{name}/{version}/manifest.json", manifest);
        }
        catch (BadHttpRequestException exception)
        {
            return Results.StatusCode(exception.StatusCode);
        }
        catch (Exception exception) when (exception is InvalidDataException or VelaDependencyArchiveException)
        {
            return Results.BadRequest("Invalid package archive or manifest.");
        }
        finally
        {
            // Staging is generated below the configured root, never from a route value.
            var prefix = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(staging).StartsWith(prefix, StringComparison.Ordinal) && Directory.Exists(staging) && !HasLink(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException exception)
                {
                    LogAbandonedUpload(_logger, exception);
                }
            }
        }
    }

    private bool TryPackageDirectory(string name, string version, out string directory)
    {
        directory = string.Empty;
        if (!ValidName(name) || !ValidVersion(version))
        {
            return false;
        }

        directory = Path.Combine(_settings.Root, name, version);
        return !HasLink(directory);
    }

    private bool HasLink(string path)
    {
        var relative = Path.GetRelativePath(_settings.Root, path);
        var current = _settings.Root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                // GetAttributes also detects dangling links, for which Exists
                // reports false even though a directory entry is present.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool ValidName(string name)
    {
        if (name.Length is < 1 or > 128 || name[0] is < 'a' or > 'z'
            || name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal)
            || name.Any(static value => value is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '-' and not '_'))
        {
            return false;
        }

        var device = name.Split('.')[0];
        return device is not ("con" or "prn" or "aux" or "nul")
            && !(device.Length == 4 && (device.StartsWith("com", StringComparison.Ordinal) || device.StartsWith("lpt", StringComparison.Ordinal))
                && device[3] is >= '0' and <= '9');
    }

    private static bool ValidVersion(string version)
    {
        if (version.Length is < 5 or > 32 || version.Any(static value => value is not (>= '0' and <= '9') and not '.'))
        {
            return false;
        }

        try
        {
            return SemVer.TryParse(version, out var parsed) && parsed!.ToString() == version;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static IResult InvalidIdentity() => Results.BadRequest("Invalid package name or canonical major.minor.patch version.");

    private static IResult AlreadyPublished() => Results.Conflict("This package version is immutable and already exists.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove an abandoned registry upload.")]
    private static partial void LogAbandonedUpload(ILogger logger, Exception exception);
}
