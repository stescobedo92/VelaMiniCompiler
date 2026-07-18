using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vela.Grpc.Protocol;

namespace Vela.Grpc;

/// <summary>gRPC server that dispatches unary calls by method name to Vela handlers.</summary>
public sealed class GrpcServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Func<string, string>> _handlers = new(StringComparer.Ordinal);
    private readonly string _host;
    private readonly int _requestedPort;
    private WebApplication? _app;
    private int _port;

    internal GrpcServer(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host;
        _requestedPort = port;
    }

    /// <summary>Creates a gRPC server.</summary>
    public static GrpcServer Create(string host, int port) => new(host, port);

    /// <summary>Maps a fully-qualified method such as <c>hello.Greeter/SayHello</c>.</summary>
    public void Map(string method, Func<string, string> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[method] = handler;
    }

    /// <summary>Starts the server and returns the bound port.</summary>
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
            options.Listen(IPAddress.Parse(NormalizeHost(_host)), _requestedPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
            });
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(this);

        var app = builder.Build();
        app.MapGrpcService<VelaRpcDispatcher>();
        app.Start();
        _app = app;
        _port = app.Urls.Select(static url => new Uri(url).Port).FirstOrDefault();
        if (_port == 0 && _requestedPort > 0)
        {
            _port = _requestedPort;
        }

        return _port;
    }

    /// <summary>Runs until shutdown.</summary>
    public int Run()
    {
        Start();
        _app!.WaitForShutdown();
        return 0;
    }

    /// <summary>Stops the server.</summary>
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

    internal string Dispatch(string method, string body)
    {
        if (!_handlers.TryGetValue(method, out var handler))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Unknown method '{method}'."));
        }

        return handler(body ?? string.Empty) ?? string.Empty;
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
}

/// <summary>Generated-service dispatcher for <see cref="VelaRpc"/>.</summary>
public sealed class VelaRpcDispatcher : VelaRpc.VelaRpcBase
{
    private readonly GrpcServer _server;

    /// <summary>Creates the dispatcher.</summary>
    public VelaRpcDispatcher(GrpcServer server) => _server = server;

    /// <inheritdoc />
    public override Task<RpcResponse> Call(RpcRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var body = _server.Dispatch(request.Method, request.Body);
            return Task.FromResult(new RpcResponse { Body = body, Status = 0 });
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }
}

/// <summary>Static helpers emitted by the Vela gRPC core module.</summary>
public static class VelaGrpc
{
    /// <summary>Creates a gRPC server.</summary>
    public static GrpcServer CreateServer(string host, int port) => GrpcServer.Create(host, port);

    /// <summary>Maps a unary method handler.</summary>
    public static void Map(GrpcServer server, string method, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Map(method, handler);
    }

    /// <summary>Starts the server and returns the bound port.</summary>
    public static int Start(GrpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Start();
    }

    /// <summary>Runs until shutdown.</summary>
    public static int Run(GrpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Run();
    }

    /// <summary>Stops the server.</summary>
    public static void Stop(GrpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Stop();
    }

    /// <summary>Calls a unary method on a running Vela gRPC server (test/client helper).</summary>
    public static string ClientCall(string host, int port, string method, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        using var channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        var client = new VelaRpc.VelaRpcClient(channel);
        var response = client.Call(new RpcRequest { Method = method, Body = body ?? string.Empty });
        return response.Body;
    }
}
