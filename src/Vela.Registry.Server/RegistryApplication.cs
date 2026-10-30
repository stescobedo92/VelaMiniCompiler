using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace Vela.Registry.Server;

/// <summary>Builds the registry host with public reads and authenticated, immutable publication.</summary>
public static class RegistryApplication
{
    /// <summary>Builds a host, optionally configuring its builder before services are registered.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var settings = RegistrySettings.FromConfiguration(builder.Configuration);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = settings.MaxPackageBytes);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<RegistryStore>();
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(RegistryAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RegistryAuthenticationHandler>(RegistryAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("publish", policy => policy.RequireAuthenticatedUser().RequireClaim("registry:publish", "all"));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter("publish", limiter =>
            {
                limiter.PermitLimit = settings.MaxConcurrentUploads;
                limiter.QueueLimit = 0;
            });
        });

        var app = builder.Build();
        _ = app.Services.GetRequiredService<RegistryStore>();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapGet("/packages/{name}/index.json", (string name, RegistryStore store) => store.Index(name));
        app.MapGet("/packages/{name}/{version}/manifest.json", (string name, string version, RegistryStore store) => store.Manifest(name, version));
        app.MapGet("/packages/{name}/{version}/package.vlpkg", (string name, string version, RegistryStore store) => store.Archive(name, version));
        app.MapGet("/tuf/root.json", (RegistryStore store) => store.Metadata("root.json"));
        app.MapGet("/tuf/targets.json", (RegistryStore store) => store.Metadata("targets.json"));
        app.MapGet("/tuf/{version}.root.json", (string version, RegistryStore store) => store.VersionedRoot(version));
        app.MapPost("/packages/{name}/{version}", (string name, string version, HttpRequest request, RegistryStore store) => store.PublishAsync(name, version, request))
            .RequireAuthorization("publish")
            .RequireRateLimiting("publish");
        return app;
    }
}
