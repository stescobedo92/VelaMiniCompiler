using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Vela.LanguageServer;
using Xunit;

namespace Vela.LanguageServer.Tests;

public sealed class LspMalformedMessageTests
{
    private const string Initialize = """{"jsonrpc":"2.0","id":42,"method":"initialize","params":{}}""";

    [Theory]
    [InlineData("initialize")]
    [InlineData("shutdown")]
    [InlineData("textDocument/completion")]
    [InlineData("textDocument/hover")]
    [InlineData("textDocument/definition")]
    [InlineData("textDocument/documentSymbol")]
    [InlineData("textDocument/formatting")]
    public async Task RequestMethodsWithoutIdAreIgnored(string method)
    {
        var responses = await RunMessagesAsync($$"""{"jsonrpc":"2.0","method":"{{method}}"}""", Initialize);

        Assert.Single(responses);
        Assert.Equal(42, responses[0]["id"]!.GetValue<int>());
        Assert.NotNull(responses[0]["result"]);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":42}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":null}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":2,"id":1,"method":"initialize"}""")]
    [InlineData("""{"id":1,"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"initialize"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task InvalidRequestsReturnErrorsAndSessionContinues(string message)
    {
        var responses = await RunMessagesAsync(message, Initialize);

        Assert.Equal(2, responses.Count);
        Assert.Equal(-32600, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.Equal(42, responses[1]["id"]!.GetValue<int>());
        Assert.NotNull(responses[1]["result"]);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"\uD800"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":"\uDC00","method":"initialize"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"\uD800":1}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"initialize","method":"shutdown"}""")]
    public async Task InvalidJsonReturnsParseErrorAndSessionContinues(string message)
    {
        var responses = await RunMessagesAsync(message, Initialize);

        Assert.Equal(2, responses.Count);
        Assert.Null(responses[0]["id"]);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(responses[1]["result"]);
    }

    [Fact]
    public async Task InvalidUtf8ReturnsParseError()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"?"}""");
        body[Array.IndexOf(body, (byte)'?')] = 0xFF;
        using var input = new MemoryStream();
        input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        input.Write(body);

        var responses = await RunBytesAsync(input.ToArray());

        Assert.Single(responses);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("initialize", "[]")]
    [InlineData("shutdown", "1")]
    [InlineData("textDocument/completion", "{}")]
    [InlineData("textDocument/completion", """{"textDocument":true}""")]
    [InlineData("textDocument/documentSymbol", """{"textDocument":{"uri":42}}""")]
    [InlineData("textDocument/formatting", """{"textDocument":{"uri":"file:///test.vela"},"position":[]} """)]
    [InlineData("textDocument/hover", """{"textDocument":{"uri":"file:///test.vela"},"position":{"line":"zero","character":0}}""")]
    [InlineData("textDocument/definition", """{"textDocument":{"uri":"file:///test.vela"},"position":{"line":-1,"character":0}}""")]
    public async Task InvalidRequestParametersReturnErrorsAndSessionContinues(string method, string parameters)
    {
        var responses = await RunMessagesAsync(
            $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""", Initialize);

        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses[0]["id"]!.GetValue<int>());
        Assert.Equal(-32602, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(responses[1]["result"]);
    }

    [Theory]
    [InlineData("textDocument/didOpen", """{"textDocument":{"uri":"file:///test.vela","text":1}}""")]
    [InlineData("textDocument/didOpen", """{"textDocument":{"uri":"file:///test.vela","text":"","version":{}}}""")]
    [InlineData("textDocument/didChange", """{"textDocument":{"uri":"file:///test.vela"},"contentChanges":[true]}""")]
    [InlineData("textDocument/didClose", """{"textDocument":[]}""")]
    public async Task InvalidNotificationsAreIgnored(string method, string parameters)
    {
        var responses = await RunMessagesAsync(
            $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters}}}""", Initialize);

        Assert.Single(responses);
        Assert.Equal(42, responses[0]["id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("initialized")]
    [InlineData("exit")]
    [InlineData("textDocument/didOpen")]
    [InlineData("textDocument/didChange")]
    [InlineData("textDocument/didClose")]
    public async Task NotificationMethodsWithIdsAreRejected(string method)
    {
        var responses = await RunMessagesAsync(
            $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{} }""", Initialize);

        Assert.Equal(2, responses.Count);
        Assert.Equal(-32600, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(responses[1]["result"]);
    }

    [Fact]
    public async Task ChangeOfUnopenedDocumentIsIgnored()
    {
        var responses = await RunMessagesAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///missing.vela"},"contentChanges":[{"text":""}]}}""",
            Initialize);

        Assert.Single(responses);
        Assert.NotNull(responses[0]["result"]);
    }

    [Fact]
    public async Task ExitEndsSessionWithoutTerminatingHostProcess()
    {
        var responses = await RunMessagesAsync("""{"jsonrpc":"2.0","method":"exit"}""", Initialize);

        Assert.Empty(responses);
    }

    [Theory]
    [InlineData("Content-Length: -1\r\n\r\n")]
    [InlineData("Content-Length: +1\r\n\r\n")]
    [InlineData("Content-Length: 16777217\r\n\r\n")]
    [InlineData("Content-Length: 2147483648\r\n\r\n")]
    [InlineData("Content-Length: invalid\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\n{")]
    [InlineData("Content-Type: application/json\r\n\r\n")]
    [InlineData("invalid header\r\n\r\n")]
    [InlineData("Content-Length: 2\n\n{}")]
    [InlineData("Content-Length: 2\r\n")]
    [InlineData("Content-Length: 2\r\n\r\n{")]
    public async Task InvalidFramingReturnsErrorAndEndsSession(string frame)
    {
        var responses = await RunBytesAsync(Encoding.UTF8.GetBytes(frame));

        Assert.Single(responses);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task OversizedHeadersAreRejected()
    {
        var responses = await RunBytesAsync(Encoding.ASCII.GetBytes("Unknown: " + new string('x', 8192) + "\r\n\r\n"));

        Assert.Single(responses);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MultipleHeadersHaveAnAggregateLimit()
    {
        var headers = string.Concat(Enumerable.Repeat("Unknown: " + new string('x', 1000) + "\r\n", 9));
        var responses = await RunBytesAsync(Encoding.ASCII.GetBytes(headers + "Content-Length: 2\r\n\r\n{}"));

        Assert.Single(responses);
        Assert.Equal(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task StringIdsRoundTripAndShutdownResultIsNull()
    {
        var responses = await RunMessagesAsync("""{"jsonrpc":"2.0","id":"shutdown-☃","method":"shutdown"}""");

        Assert.Single(responses);
        Assert.Equal("shutdown-☃", responses[0]["id"]!.GetValue<string>());
        Assert.True(responses[0].ContainsKey("result"));
        Assert.Null(responses[0]["result"]);
    }

    private static async Task<List<JsonObject>> RunMessagesAsync(params string[] messages)
    {
        using var input = new MemoryStream();
        foreach (var message in messages)
        {
            var body = Encoding.UTF8.GetBytes(message);
            input.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            input.Write(body);
        }

        return await RunBytesAsync(input.ToArray());
    }

    private static async Task<List<JsonObject>> RunBytesAsync(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        await new LspServer(input, output).RunAsync();

        var transcript = output.ToArray();
        var responses = new List<JsonObject>();
        var position = 0;
        while (position < transcript.Length)
        {
            var headerEnd = transcript.AsSpan(position).IndexOf("\r\n\r\n"u8);
            Assert.True(headerEnd >= 0);
            var header = Encoding.ASCII.GetString(transcript, position, headerEnd);
            var length = int.Parse(header.AsSpan("Content-Length: ".Length), CultureInfo.InvariantCulture);
            position += headerEnd + 4;
            responses.Add(JsonNode.Parse(transcript.AsSpan(position, length))!.AsObject());
            position += length;
        }

        return responses;
    }
}
