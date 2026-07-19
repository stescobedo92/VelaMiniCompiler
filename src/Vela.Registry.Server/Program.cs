using System.Text.Json;
using Vela.Packages;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var root = app.Configuration["RegistryRoot"]
    ?? Path.Combine(Directory.GetCurrentDirectory(), "registry-data");
Directory.CreateDirectory(root);

app.MapGet("/packages/{name}/index.json", (string name) =>
{
    var packageDirectory = Path.Combine(root, name);
    if (!Directory.Exists(packageDirectory))
    {
        return Results.NotFound();
    }

    var versions = Directory.GetDirectories(packageDirectory)
        .Select(Path.GetFileName)
        .Where(static version => !string.IsNullOrWhiteSpace(version))
        .OrderBy(static version => version, StringComparer.Ordinal)
        .ToArray();
    return Results.Json(new { name, versions });
});

app.MapGet("/packages/{name}/{version}/manifest.json", (string name, string version) =>
{
    var packagePath = Path.Combine(root, name, version);
    var manifestPath = Path.Combine(packagePath, "manifest.json");
    if (File.Exists(manifestPath))
    {
        return Results.File(manifestPath, "application/json");
    }

    var tomlPath = Path.Combine(packagePath, "vela.toml");
    if (!File.Exists(tomlPath) && !File.Exists(Path.Combine(packagePath, $"package{VelaDependencyArchive.Extension}")))
    {
        return Results.NotFound();
    }

    return Results.Json(new VelaPackageManifest(name, version, VelaPackageKind.Library, new Dictionary<string, string>()));
});

app.MapGet("/packages/{name}/{version}/package.vlpkg", (string name, string version) =>
{
    var archivePath = Path.Combine(root, name, version, $"package{VelaDependencyArchive.Extension}");
    return File.Exists(archivePath)
        ? Results.File(archivePath, "application/zip", $"package{VelaDependencyArchive.Extension}")
        : Results.NotFound();
});

app.MapGet("/tuf/root.json", () =>
{
    var path = Path.Combine(root, "tuf", "root.json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});

app.MapGet("/tuf/targets.json", () =>
{
    var path = Path.Combine(root, "tuf", "targets.json");
    return File.Exists(path) ? Results.File(path, "application/json") : Results.NotFound();
});

app.MapPost("/packages/{name}/{version}", async (string name, string version, HttpRequest request) =>
{
    var packageDirectory = Path.Combine(root, name, version);
    Directory.CreateDirectory(packageDirectory);
    var archivePath = Path.Combine(packageDirectory, $"package{VelaDependencyArchive.Extension}");
    await using (var file = File.Create(archivePath))
    {
        await request.Body.CopyToAsync(file);
    }

    var manifest = new VelaPackageManifest(name, version, VelaPackageKind.Library, new Dictionary<string, string>());
    await File.WriteAllTextAsync(
        Path.Combine(packageDirectory, "manifest.json"),
        JsonSerializer.Serialize(manifest));
    return Results.Created($"/packages/{name}/{version}/manifest.json", manifest);
});

Console.WriteLine($"Vela registry listening with root '{root}'");
app.Run();
