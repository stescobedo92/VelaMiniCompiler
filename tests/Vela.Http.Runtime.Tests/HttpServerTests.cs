using Vela.Http;
using Xunit;

namespace Vela.Http.Runtime.Tests;

public sealed class HttpServerTests
{
    [Fact]
    public void RestRoutes_EchoAndHealth()
    {
        var server = VelaHttp.CreateServer("127.0.0.1", 0);
        VelaHttp.Get(server, "/health", static () => "{\"ok\":true}");
        VelaHttp.Post(server, "/echo", static body => body);
        var port = VelaHttp.Start(server);
        try
        {
            Assert.True(port > 0);
            Assert.Contains("ok", VelaHttp.ClientGet("127.0.0.1", port, "/health"), StringComparison.Ordinal);
            Assert.Equal("hello", VelaHttp.ClientPost("127.0.0.1", port, "/echo", "hello"));
        }
        finally
        {
            VelaHttp.Stop(server);
        }
    }

    [Fact]
    public void Graphql_MountExecutesQuery()
    {
        var schema = VelaGraphql.CreateSchema();
        VelaGraphql.Query(schema, "hello", static () => "\"world\"");
        var server = VelaHttp.CreateServer("127.0.0.1", 0);
        VelaGraphql.Mount(server, "/graphql", schema);
        var port = VelaHttp.Start(server);
        try
        {
            var body = VelaHttp.ClientPost("127.0.0.1", port, "/graphql", "{\"query\":\"{ hello }\"}");
            Assert.Contains("world", body, StringComparison.Ordinal);
        }
        finally
        {
            VelaHttp.Stop(server);
        }
    }
}
