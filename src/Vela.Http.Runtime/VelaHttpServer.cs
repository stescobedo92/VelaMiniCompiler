using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vela.Http;

/// <summary>Bounded Kestrel HTTP server for Vela REST and GraphQL hosting.</summary>
public sealed class HttpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Func<string, string>> _routes = new(StringComparer.Ordinal);
    private readonly List<(string Path, VelaGraphqlSchema Schema)> _graphqlMounts = [];
    private WebApplication? _app;
    private int _port;
    private readonly string _host;
    private readonly int _requestedPort;
    private int _maxBodyBytes = 1_048_576;

    internal HttpServer(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _requestedPort = port;
    }

    /// <summary>Creates a server bound to <paramref name="host"/>:<paramref name="port"/> (0 = ephemeral).</summary>
    public static HttpServer Create(string host, int port) => new(host, port);

    /// <summary>Sets the maximum request body size in bytes.</summary>
    public void SetMaxBodyBytes(int maxBodyBytes) =>
        _maxBodyBytes = Math.Clamp(maxBodyBytes, 256, 64 * 1024 * 1024);

    /// <summary>Maps a route. Handler receives the request body (empty for GET/DELETE) and returns the response body.</summary>
    public void Map(string method, string path, Func<string, string> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        _routes[$"{method.ToUpperInvariant()} {path}"] = handler;
    }

    /// <summary>Maps GET <paramref name="path"/> to a no-argument handler.</summary>
    public void Get(string path, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Map("GET", path, _ => handler());
    }

    /// <summary>Maps GET <paramref name="path"/> to a body-aware handler (body usually empty).</summary>
    public void Get(string path, Func<string, string> handler) => Map("GET", path, handler);

    /// <summary>Maps POST <paramref name="path"/>.</summary>
    public void Post(string path, Func<string, string> handler) => Map("POST", path, handler);

    /// <summary>Maps PUT <paramref name="path"/>.</summary>
    public void Put(string path, Func<string, string> handler) => Map("PUT", path, handler);

    /// <summary>Maps DELETE <paramref name="path"/>.</summary>
    public void Delete(string path, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Map("DELETE", path, _ => handler());
    }

    /// <summary>Maps DELETE <paramref name="path"/> with a body-aware handler.</summary>
    public void Delete(string path, Func<string, string> handler) => Map("DELETE", path, handler);

    /// <summary>Mounts a GraphQL schema at <paramref name="path"/> (POST JSON {"query":"..."}).</summary>
    public void MountGraphql(string path, VelaGraphqlSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(schema);
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        _graphqlMounts.Add((path, schema));
    }

    /// <summary>Starts the server and returns the bound TCP port.</summary>
    public int Start()
    {
        if (_app is not null)
        {
            return _port;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = _maxBodyBytes;
            options.Listen(IPAddress.Parse(NormalizeHost(_host)), _requestedPort);
        });
        builder.Services.ConfigureHttpJsonOptions(_ => { });

        var app = builder.Build();
        foreach (var (path, schema) in _graphqlMounts)
        {
            var mountPath = path;
            var mountSchema = schema;
            app.MapPost(mountPath, async (HttpRequest request, CancellationToken cancellationToken) =>
            {
                var body = await ReadBodyAsync(request, _maxBodyBytes, cancellationToken);
                var result = mountSchema.Execute(ExtractGraphqlQuery(body));
                return Results.Content(result, "application/json", Encoding.UTF8);
            });
        }

        app.MapMethods("/{**path}", ["GET", "POST", "PUT", "DELETE", "PATCH"], async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var key = $"{request.Method.ToUpperInvariant()} {request.Path.Value ?? "/"}";
            if (!_routes.TryGetValue(key, out var handler))
            {
                // Also try without trailing slash normalization
                if (key.EndsWith('/') && key.Length > ("GET /").Length)
                {
                    _routes.TryGetValue(key.TrimEnd('/'), out handler);
                }
            }

            if (handler is null)
            {
                return Results.NotFound("{\"error\":\"not_found\"}");
            }

            var body = await ReadBodyAsync(request, _maxBodyBytes, cancellationToken);
            string responseBody;
            try
            {
                responseBody = handler(body);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }

            return Results.Content(responseBody ?? string.Empty, "application/json", Encoding.UTF8);
        });

        app.Start();
        _app = app;
        _port = app.Urls
            .Select(static url => new Uri(url).Port)
            .FirstOrDefault();
        if (_port == 0 && _requestedPort > 0)
        {
            _port = _requestedPort;
        }

        return _port;
    }

    /// <summary>Runs until the process is stopped (blocking).</summary>
    public int Run()
    {
        Start();
        _app!.WaitForShutdown();
        return 0;
    }

    /// <summary>Stops the server if running.</summary>
    public void Stop()
    {
        if (_app is null)
        {
            return;
        }

        _app.StopAsync().GetAwaiter().GetResult();
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _app = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static string NormalizeHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? "127.0.0.1" : host;

    private static async Task<string> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0 or null && !request.Body.CanRead)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var buffer = new char[Math.Min(maxBytes, 8192)];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > maxBytes)
            {
                throw new InvalidOperationException($"Request body exceeded {maxBytes} bytes.");
            }
        }

        return builder.ToString();
    }

    private static string ExtractGraphqlQuery(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        // Minimal JSON extraction: {"query":"..."} without a full JSON parser dependency for nested escapes.
        const string marker = "\"query\"";
        var index = body.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return body.Trim();
        }

        var colon = body.IndexOf(':', index + marker.Length);
        if (colon < 0)
        {
            return body.Trim();
        }

        var startQuote = body.IndexOf('"', colon + 1);
        if (startQuote < 0)
        {
            return body.Trim();
        }

        var sb = new StringBuilder();
        for (var i = startQuote + 1; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '\\' && i + 1 < body.Length)
            {
                var next = body[i + 1];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => next
                });
                i++;
                continue;
            }

            if (ch == '"')
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}

/// <summary>Static helpers emitted by the Vela HTTP core module.</summary>
public static class VelaHttp
{
    /// <summary>Creates an HTTP server.</summary>
    public static HttpServer CreateServer(string host, int port) => HttpServer.Create(host, port);

    /// <summary>Maps GET with a no-arg handler.</summary>
    public static void Get(HttpServer server, string path, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Get(path, handler);
    }

    /// <summary>Maps POST.</summary>
    public static void Post(HttpServer server, string path, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Post(path, handler);
    }

    /// <summary>Maps PUT.</summary>
    public static void Put(HttpServer server, string path, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Put(path, handler);
    }

    /// <summary>Maps DELETE with a no-arg handler.</summary>
    public static void Delete(HttpServer server, string path, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Delete(path, handler);
    }

    /// <summary>Starts the server and returns the bound port.</summary>
    public static int Start(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Start();
    }

    /// <summary>Runs the server until shutdown.</summary>
    public static int Run(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Run();
    }

    /// <summary>Stops the server.</summary>
    public static void Stop(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Stop();
    }

    /// <summary>Sets max request body bytes.</summary>
    public static void SetMaxBody(HttpServer server, int maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.SetMaxBodyBytes(maxBodyBytes);
    }

    /// <summary>Performs a plaintext HTTP/1.1 GET and returns the response body (not headers).</summary>
    public static string ClientGet(string host, int port, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var url = $"http://{host}:{port}{path}";
        return client.GetStringAsync(url).GetAwaiter().GetResult();
    }

    /// <summary>Performs a plaintext HTTP/1.1 POST with a text body and returns the response body.</summary>
    public static string ClientPost(string host, int port, string path, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(body);
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var url = $"http://{host}:{port}{path}";
        using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
