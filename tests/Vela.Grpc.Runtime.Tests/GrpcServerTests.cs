using Vela.Grpc;
using Xunit;

namespace Vela.Grpc.Runtime.Tests;

public sealed class GrpcServerTests
{
    [Fact]
    public void UnaryMap_DispatchesByMethodName()
    {
        var server = VelaGrpc.CreateServer("127.0.0.1", 0);
        VelaGrpc.Map(server, "hello.Greeter/SayHello", static body => $"{{\"echo\":{body}}}");
        var port = VelaGrpc.Start(server);
        try
        {
            var response = VelaGrpc.ClientCall("127.0.0.1", port, "hello.Greeter/SayHello", "\"Ada\"");
            Assert.Contains("Ada", response, StringComparison.Ordinal);
        }
        finally
        {
            VelaGrpc.Stop(server);
        }
    }
}
