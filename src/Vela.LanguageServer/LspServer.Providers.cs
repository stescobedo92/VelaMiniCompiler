using System.Text.Json.Nodes;
using Vela.Core.Formatting;
using Vela.Core.Lexing;
using Vela.Core.Source;

namespace Vela.LanguageServer;

/// <summary>Completion, hover, definition, symbols, and formatting requests.</summary>
public sealed partial class LspServer
{
    private static readonly string[] Keywords =
    [
        "as", "async", "assert", "await", "break", "case", "catch", "class",
        "continue", "default", "defer", "else", "enum", "false", "ffi",
        "finally", "fn", "for", "if", "implements", "in", "include",
        "interface", "let", "match", "nil", "public", "record", "return",
        "struct", "switch", "true", "try", "var", "while",
    ];

    private async Task HandleCompletionAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var items = new JsonArray();
        foreach (var keyword in Keywords)
        {
            items.Add(new JsonObject { ["label"] = keyword, ["kind"] = 14 });
        }

        if (message["params"]?["textDocument"]?["uri"]?.GetValue<string>() is string uri
            && _workspace.TryGetIndex(uri, out var index))
        {
            foreach (var symbol in index.Symbols)
            {
                items.Add(new JsonObject
                {
                    ["label"] = symbol.Name,
                    ["kind"] = CompletionKind(symbol.Kind),
                    ["detail"] = symbol.DisplayName == symbol.Name ? symbol.Detail : symbol.DisplayName,
                });
            }
        }

        await WriteResponseAsync(message["id"]!, items, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleHoverAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (TryFindAt(message, out var index, out var symbol))
        {
            var markdown = $"```vela\n{symbol.Detail}\n```";
            if (!string.IsNullOrWhiteSpace(symbol.Documentation))
            {
                markdown += $"\n{symbol.Documentation}";
            }

            await WriteResponseAsync(message["id"]!, new JsonObject
            {
                ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown },
                ["range"] = SpanToRange(index.Source, symbol.NameSpan),
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteNullResponseAsync(message["id"]!, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDefinitionAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (TryFindAt(message, out var index, out var symbol)
            && message["params"]?["textDocument"]?["uri"]?.GetValue<string>() is string uri)
        {
            await WriteResponseAsync(message["id"]!, new JsonObject
            {
                ["uri"] = uri,
                ["range"] = SpanToRange(index.Source, symbol.NameSpan),
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteNullResponseAsync(message["id"]!, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleDocumentSymbolAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var symbols = new JsonArray();
        if (message["params"]?["textDocument"]?["uri"]?.GetValue<string>() is string uri
            && _workspace.TryGetIndex(uri, out var index))
        {
            foreach (var symbol in index.TopLevel)
            {
                symbols.Add(SymbolNode(index.Source, symbol));
            }
        }

        await WriteResponseAsync(message["id"]!, symbols, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFormattingAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (message["params"]?["textDocument"]?["uri"]?.GetValue<string>() is string uri
            && _workspace.TryGetIndex(uri, out var index))
        {
            var formatted = VelaFormatter.Format(index.Source);
            if (!formatted.HasErrors)
            {
                var lastLine = index.Source.LineCount - 1;
                var lastLength = index.Source.GetLineSpan(lastLine).Length;
                await WriteResponseAsync(message["id"]!, new JsonArray
                {
                    new JsonObject
                    {
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                            ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = lastLength },
                        },
                        ["newText"] = formatted.Text,
                    },
                }, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await WriteNullResponseAsync(message["id"]!, cancellationToken).ConfigureAwait(false);
    }

    private bool TryFindAt(JsonObject message, out VelaDocumentIndex index, out IndexedSymbol symbol)
    {
        index = null!;
        symbol = null!;
        if (!TryGetOffset(message, out var found, out var offset))
        {
            return false;
        }

        index = found;
        if (found.TokenAt(offset) is not { Kind: TokenKind.Identifier } token)
        {
            return false;
        }

        var definition = found.FindDefinition(token.Text);
        if (definition is null)
        {
            return false;
        }

        symbol = definition;
        return true;
    }

    private bool TryGetOffset(JsonObject message, out VelaDocumentIndex index, out int offset)
    {
        index = null!;
        offset = 0;
        var parameters = message["params"]?.AsObject();
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is null || !_workspace.TryGetIndex(uri, out var found))
        {
            return false;
        }

        index = found;
        var position = parameters?["position"]?.AsObject();
        var line = position?["line"]?.GetValue<int>() ?? 0;
        var character = position?["character"]?.GetValue<int>() ?? 0;
        var clampedLine = Math.Clamp(line, 0, found.Source.LineCount - 1);
        var lineSpan = found.Source.GetLineSpan(clampedLine);
        offset = lineSpan.Start + Math.Clamp(character, 0, lineSpan.Length);
        return true;
    }

    private static JsonObject SymbolNode(SourceText source, IndexedSymbol symbol)
    {
        var children = new JsonArray();
        foreach (var child in symbol.Children)
        {
            children.Add(SymbolNode(source, child));
        }

        return new JsonObject
        {
            ["name"] = symbol.DisplayName,
            ["kind"] = SymbolKind(symbol.Kind),
            ["range"] = SpanToRange(source, symbol.FullSpan),
            ["selectionRange"] = SpanToRange(source, symbol.NameSpan),
            ["children"] = children,
        };
    }

    private static JsonObject SpanToRange(SourceText source, TextSpan span)
    {
        var start = source.GetLocation(span.Start);
        var end = source.GetLocation(Math.Max(span.Start, span.End));
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = start.Line - 1, ["character"] = start.Column - 1 },
            ["end"] = new JsonObject { ["line"] = end.Line - 1, ["character"] = end.Column - 1 },
        };
    }

    private static int CompletionKind(VelaSymbolKind kind) => kind switch
    {
        VelaSymbolKind.Function => 3,
        VelaSymbolKind.Method => 2,
        VelaSymbolKind.Field => 5,
        VelaSymbolKind.Value => 6,
        VelaSymbolKind.Class => 7,
        VelaSymbolKind.Struct => 22,
        VelaSymbolKind.Interface => 8,
        VelaSymbolKind.Record => 7,
        VelaSymbolKind.Enum => 13,
        VelaSymbolKind.EnumMember => 20,
        _ => 1,
    };

    private static int SymbolKind(VelaSymbolKind kind) => kind switch
    {
        VelaSymbolKind.Function => 12,
        VelaSymbolKind.Method => 6,
        VelaSymbolKind.Field => 8,
        VelaSymbolKind.Value => 13,
        VelaSymbolKind.Class => 5,
        VelaSymbolKind.Struct => 23,
        VelaSymbolKind.Interface => 11,
        VelaSymbolKind.Record => 5,
        VelaSymbolKind.Enum => 10,
        VelaSymbolKind.EnumMember => 22,
        _ => 13,
    };

    private async Task WriteNullResponseAsync(JsonNode id, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id.ToJsonString(JsonOptions)},\"result\":null}}");
        var header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
