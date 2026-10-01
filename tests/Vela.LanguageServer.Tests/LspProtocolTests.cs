using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.LanguageServer;
using Xunit;

namespace Vela.LanguageServer.Tests;

public sealed class LspProtocolTests
{
    private const string Uri = "file:///test/main.vela";

    private const string Document = """
        /// Adds two numbers.
        /// @param a First value.
        /// @param b Second value.
        fn add(a: Int, b: Int) -> Int {
            return a + b;
        }

        record Point {
            x: Int;
            y: Int;
        }

        let answer: Int = add(20, 22);
        print(answer);
        """;

    [Fact]
    public async Task InitializeAdvertisesCapabilities()
    {
        var responses = await RunSessionAsync(
            Request(1, "initialize", new JsonObject { ["capabilities"] = new JsonObject() }),
            Notification("initialized", new JsonObject()));

        var result = ById(responses, 1)["result"]!.AsObject()["capabilities"]!.AsObject();
        Assert.NotNull(result["completionProvider"]);
        Assert.True(result["hoverProvider"]!.GetValue<bool>());
        Assert.True(result["definitionProvider"]!.GetValue<bool>());
        Assert.True(result["documentSymbolProvider"]!.GetValue<bool>());
        Assert.True(result["documentFormattingProvider"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CompletionReturnsKeywordsAndSymbols()
    {
        var responses = await RunSessionAsync(
            Open(Uri, Document),
            Request(2, "textDocument/completion", DocRef(Uri)));

        var labels = ById(responses, 2)["result"]!.AsArray()
            .Select(static item => item!["label"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("fn", labels);
        Assert.Contains("let", labels);
        Assert.Contains("if", labels);
        Assert.Contains("add", labels);
        Assert.Contains("Point", labels);
        Assert.Contains("answer", labels);
    }

    [Fact]
    public async Task HoverShowsSignatureAndDocs()
    {
        var responses = await RunSessionAsync(
            Open(Uri, Document),
            Request(3, "textDocument/hover", At(Uri, 12, 20)));

        var result = ById(responses, 3)["result"]!.AsObject();
        var value = result["contents"]!.AsObject()["value"]!.GetValue<string>();
        Assert.Contains("fn add(a: Int, b: Int) -> Int", value, StringComparison.Ordinal);
        Assert.Contains("Adds two numbers.", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefinitionJumpsToDeclaration()
    {
        var responses = await RunSessionAsync(
            Open(Uri, Document),
            Request(4, "textDocument/definition", At(Uri, 12, 20)));

        var result = ById(responses, 4)["result"]!.AsObject();
        Assert.Equal(Uri, result["uri"]!.GetValue<string>());
        var start = result["range"]!.AsObject()["start"]!.AsObject();
        Assert.Equal(3, start["line"]!.GetValue<int>());
        Assert.Equal(3, start["character"]!.GetValue<int>());
    }

    [Fact]
    public async Task DocumentSymbolListsDeclarations()
    {
        var responses = await RunSessionAsync(
            Open(Uri, Document),
            Request(5, "textDocument/documentSymbol", DocRef(Uri)));

        var symbols = ById(responses, 5)["result"]!.AsArray().Select(static item => item!.AsObject()).ToArray();
        var names = symbols.Select(static symbol => symbol["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("add", names);
        Assert.Contains("Point", names);
        Assert.Contains("answer", names);
        var point = symbols.Single(symbol => symbol["name"]!.GetValue<string>() == "Point");
        var children = point["children"]!.AsArray().Select(static item => item!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("Point.x", children);
        Assert.Contains("Point.y", children);
    }

    [Fact]
    public async Task FormattingReturnsCanonicalEdit()
    {
        const string messyUri = "file:///test/messy.vela";
        const string messy = "fn   main( )  ->  Int {\nlet x:Int=1;\nreturn x;\n}\n";

        var responses = await RunSessionAsync(
            Open(messyUri, messy),
            Request(6, "textDocument/formatting", DocRef(messyUri)));

        var newText = ById(responses, 6)["result"]![0]!["newText"]!.GetValue<string>();
        Assert.Equal("fn main() -> Int {\n    let x: Int = 1;\n    return x;\n}\n", newText);
    }

    [Fact]
    public async Task ChangeRebuildsSymbols()
    {
        var responses = await RunSessionAsync(
            Open(Uri, Document),
            Notification("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 2 },
                ["contentChanges"] = new JsonArray
                {
                    new JsonObject { ["text"] = "fn main() -> Int {\n    return 0;\n}\n" },
                },
            }),
            Request(7, "textDocument/hover", At(Uri, 12, 20)));

        Assert.Null(ById(responses, 7)["result"]);
    }

    [Fact]
    public async Task UnknownMethodReturnsMethodNotFound()
    {
        var responses = await RunSessionAsync(Request(99, "nothing/here", new JsonObject()));

        var error = ById(responses, 99)["error"]!.AsObject();
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
    }

    [Fact]
    public void KeywordListLexesAsKeywords()
    {
        foreach (var keyword in new[]
                 {
                     "as", "async", "assert", "await", "break", "case", "catch", "class",
                     "continue", "default", "defer", "else", "enum", "false", "ffi",
                     "finally", "fn", "for", "if", "implements", "in", "include",
                     "interface", "let", "match", "nil", "public", "record", "return",
                     "struct", "switch", "true", "try", "var", "while",
                 })
        {
            var tokens = new VelaLexer(new SourceText(keyword, "keywords")).Lex().Tokens;
            Assert.True(
                tokens.Count > 0 && tokens[0].Kind != TokenKind.Identifier && tokens[0].Kind != TokenKind.BadToken,
                $"Expected '{keyword}' to lex as a keyword.");
        }
    }

    private static JsonObject Request(int id, string method, JsonObject parameters) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

    private static JsonObject Notification(string method, JsonObject parameters) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };

    private static JsonObject Open(string uri, string text) =>
        Notification("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = "vela",
                ["version"] = 1,
                ["text"] = text,
            },
        });

    private static JsonObject DocRef(string uri) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

    private static JsonObject At(string uri, int line, int character) =>
        new()
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        };

    private static JsonObject ById(JsonArray responses, int id) =>
        responses.Select(static node => node!.AsObject())
            .First(node => node["id"]?.GetValue<int>() == id);

    private static async Task<JsonArray> RunSessionAsync(params JsonObject[] payloads)
    {
        using var input = new MemoryStream();
        foreach (var payload in payloads)
        {
            WriteFrame(input, payload.ToJsonString());
        }

        // A shutdown request ends the loop; EOF would too.
        WriteFrame(input, Request(0, "shutdown", new JsonObject()).ToJsonString());
        input.Position = 0;

        using var output = new MemoryStream();
        await new LspServer(input, output).RunAsync();

        var responses = new JsonArray();
        var transcript = Encoding.UTF8.GetString(output.ToArray());
        var index = 0;
        while (true)
        {
            var headerEnd = transcript.IndexOf("\r\n\r\n", index, StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                break;
            }

            var length = int.Parse(transcript[index..headerEnd].Split(':')[1].Trim(), CultureInfo.InvariantCulture);
            var bodyStart = headerEnd + 4;
            responses.Add(JsonNode.Parse(transcript.Substring(bodyStart, length)));
            index = bodyStart + length;
        }

        return responses;
    }

    private static void WriteFrame(Stream stream, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
    }
}
