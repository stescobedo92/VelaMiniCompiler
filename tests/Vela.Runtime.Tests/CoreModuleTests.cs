using System.Net;
using System.Net.Sockets;
using System.Text;
using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class CoreModuleTests
{
    [Fact]
    public void JsonCryptoTextMathAndEncoding_AreDeterministicAndValidated()
    {
        const string json = "{ \"id\": 42, \"active\": true, \"name\": \"Vela\" }";

        Assert.True(VelaJson.IsValid(json));
        Assert.Equal("{\"id\":42,\"active\":true,\"name\":\"Vela\"}", VelaJson.Compact(json, "test:1:1"));
        Assert.Equal(42, VelaJson.TryGetInt(json, "id", "test:1:1").Value);
        Assert.True(VelaJson.TryGetBool(json, "active", "test:1:1").Value);
        Assert.Equal("Vela", VelaJson.TryGetText(json, "name", "test:1:1").Value);
        Assert.True(VelaJson.TryGetText(json, "missing", "test:1:1").IsNone);
        Assert.Throws<VelaFormatException>(() => VelaJson.Pretty("{", "test:1:1"));

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", VelaCrypto.Sha256("hello"));
        Assert.True(VelaCrypto.ConstantTimeEquals("same", "same"));
        Assert.False(VelaCrypto.ConstantTimeEquals("same", "different"));
        Assert.Equal(16, VelaCrypto.RandomHex(8, "test:1:1").Length);

        Assert.True(VelaTextOperations.Contains("Vela Runtime", "Runtime"));
        Assert.Equal("TIME", VelaTextOperations.ToUpperInvariant(VelaTextOperations.Trim(" time ")));
        Assert.Equal("ela", VelaTextOperations.Slice("Vela", 1, 3, "test:1:1"));
        Assert.Throws<VelaIndexOutOfRangeException>(() => VelaTextOperations.Slice("Vela", 3, 2, "test:1:1"));

        Assert.Equal(42, VelaMath.Abs(-42, "test:1:1"));
        Assert.Equal(decimal.MaxValue, VelaMath.Abs(decimal.MinValue));
        Assert.Equal(7, VelaMath.Clamp(9, 0, 7));
        Assert.Equal(9d, VelaMath.Sqrt(81d, "test:1:1"));
        Assert.Throws<VelaArithmeticException>(() => VelaMath.Sqrt(-1d, "test:1:1"));
        Assert.Equal("Vela", VelaEncoding.Base64Decode(VelaEncoding.Base64Encode("Vela"), "test:1:1"));
        Assert.Equal("Vela", VelaEncoding.HexDecode(VelaEncoding.HexEncode("Vela"), "test:1:1"));
    }

    [Fact]
    public void TimeRandomIoAndEnvironment_ExposeExplicitHostOperations()
    {
        var start = VelaTime.MonotonicTicks();
        var end = VelaTime.MonotonicTicks();
        Assert.True(VelaTime.UtcUnixMilliseconds() > 0);
        Assert.True(VelaTime.ElapsedMilliseconds(start, end, "test:1:1") >= 0);
        Assert.Throws<VelaOverflowException>(() => VelaTime.ElapsedMilliseconds(long.MinValue, long.MaxValue, "test:1:1"));
        Assert.InRange(VelaRandom.NextInt(1, 3, "test:1:1"), 1, 2);
        Assert.InRange(VelaRandom.NextDouble(), 0d, 1d);
        Assert.Throws<VelaFormatException>(() => VelaRandom.NextInt(2, 2, "test:1:1"));

        var root = Path.Combine(Path.GetTempPath(), "vela-core-tests", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "message.txt");
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(VelaIo.Exists(file));
            VelaIo.WriteText(file, "one", "test:1:1");
            VelaIo.AppendText(file, " two", "test:1:1");
            Assert.Equal("one two", VelaIo.ReadText(file, "test:1:1"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        VelaEnvironment.Initialize(["first", "second"]);
        Assert.Equal(2, VelaEnvironment.ArgumentCount());
        Assert.Equal("second", VelaEnvironment.Argument(1).Value);
        Assert.True(VelaEnvironment.Argument(2).IsNone);
        Assert.False(string.IsNullOrWhiteSpace(VelaEnvironment.CurrentDirectory()));
    }

    [Fact]
    public async Task TcpConnection_RoundTripsWithALocalLoopbackServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[32];
            var count = await stream.ReadAsync(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, count));
        });

        using var connection = TcpConnection.Connect("127.0.0.1", port, 5_000, "test:1:1");
        connection.SendText("ping", "test:1:1");
        Assert.Equal("ping", connection.ReceiveText(32, "test:1:1"));
        await server;
    }
}
