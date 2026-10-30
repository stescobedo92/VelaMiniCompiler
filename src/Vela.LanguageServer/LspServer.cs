using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vela.LanguageServer;

/// <summary>Minimal JSON-RPC language server over stdio with Content-Length framing.</summary>
public sealed partial class LspServer
{
    private const int MaximumContentLength = 16 * 1024 * 1024;
    private const int MaximumHeaderLength = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly VelaWorkspace _workspace = new();
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly byte[] _headerByte = new byte[1];
    private bool _shutdownRequested;

    public LspServer(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            byte[]? payload;
            try
            {
                payload = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // An invalid frame cannot be safely resynchronized. Report it and
                // close this session without allocating an untrusted body length.
                await WriteErrorResponseAsync(null, -32700, "Invalid message framing", cancellationToken).ConfigureAwait(false);
                break;
            }

            if (payload is null)
            {
                break;
            }

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(payload, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
                ValidateJsonStrings(message);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                await WriteErrorResponseAsync(null, -32700, "Parse error", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message is JsonObject request)
            {
                await HandleMessageAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteErrorResponseAsync(null, -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateJsonStrings(JsonNode? node)
    {
        // JsonNode decodes property names and string values lazily. Force that
        // decoding inside the parse-error boundary, before any request handlers,
        // so invalid UTF-8 or unpaired Unicode escapes cannot fail during dispatch.
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value)
                {
                    ValidateJsonStrings(property.Value);
                }

                break;
            case JsonArray value:
                foreach (var item in value)
                {
                    ValidateJsonStrings(item);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                _ = value.GetValue<string>();
                break;
        }
    }

    internal async Task HandleMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"];
        var hasId = message.ContainsKey("id");
        var validId = id is JsonValue idValue
            && (idValue.TryGetValue<string>(out _) || idValue.TryGetValue<int>(out _));
        if (!TryGetString(message["jsonrpc"], out var version) || version != "2.0"
            || !TryGetString(message["method"], out var method)
            || (hasId && !validId))
        {
            await WriteErrorResponseAsync(validId ? id : null, -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        var requiresId = method is "initialize" or "shutdown" or "textDocument/completion"
            or "textDocument/hover" or "textDocument/definition" or "textDocument/documentSymbol"
            or "textDocument/formatting";
        if (requiresId && !hasId)
        {
            // JSON-RPC notifications never receive responses. Do not run a
            // request-only operation such as shutdown when its ID is missing.
            return;
        }

        if (hasId && method is "initialized" or "exit" or "textDocument/didOpen"
            or "textDocument/didChange" or "textDocument/didClose")
        {
            await WriteErrorResponseAsync(id, -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!HasValidParameters(method, message["params"]))
        {
            if (hasId)
            {
                await WriteErrorResponseAsync(id, -32602, "Invalid params", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        switch (method)
        {
            case "initialize":
                await HandleInitializeAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "initialized":
                break;
            case "textDocument/didOpen":
                HandleDidOpen(message);
                break;
            case "textDocument/didChange":
                HandleDidChange(message);
                break;
            case "textDocument/didClose":
                HandleDidClose(message);
                break;
            case "textDocument/completion":
                await HandleCompletionAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/hover":
                await HandleHoverAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/definition":
                await HandleDefinitionAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/documentSymbol":
                await HandleDocumentSymbolAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "textDocument/formatting":
                await HandleFormattingAsync(message, cancellationToken).ConfigureAwait(false);
                break;
            case "shutdown":
                _shutdownRequested = true;
                await WriteResponseAsync(message["id"]!, null, cancellationToken).ConfigureAwait(false);
                break;
            case "exit":
                _shutdownRequested = true;
                break;
            default:
                if (message["id"] is JsonValue unknownId)
                {
                    await WriteErrorResponseAsync(
                        unknownId,
                        -32601,
                        $"Method not found: {method}",
                        cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private static bool HasValidParameters(string method, JsonNode? parameters)
    {
        if (parameters is not null and not JsonObject)
        {
            return false;
        }

        if (method is not ("textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose"
                or "textDocument/completion" or "textDocument/hover" or "textDocument/definition"
                or "textDocument/documentSymbol" or "textDocument/formatting"))
        {
            return true;
        }

        if (parameters is not JsonObject values || values["textDocument"] is not JsonObject document
            || !TryGetString(document["uri"], out _)
            || (document.ContainsKey("version") && !IsInteger(document["version"])))
        {
            return false;
        }

        if (method == "textDocument/didOpen")
        {
            return TryGetString(document["text"], out _);
        }

        if (method == "textDocument/didChange")
        {
            return values["contentChanges"] is JsonArray { Count: > 0 } changes
                && changes.All(static change => change is JsonObject entry
                    && !entry.ContainsKey("range") && TryGetString(entry["text"], out _));
        }

        if (method is "textDocument/hover" or "textDocument/definition" || values.ContainsKey("position"))
        {
            return values["position"] is JsonObject position
                && IsInteger(position["line"], nonnegative: true)
                && IsInteger(position["character"], nonnegative: true);
        }

        return true;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        return node is JsonValue jsonValue && jsonValue.TryGetValue(out value!);
    }

    private static bool IsInteger(JsonNode? node, bool nonnegative = false)
        => node is JsonValue value && value.TryGetValue<int>(out var number) && (!nonnegative || number >= 0);

    private async Task HandleInitializeAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var result = new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = 1,
                ["completionProvider"] = new JsonObject(),
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["documentFormattingProvider"] = true,
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "vela-language-server",
                ["version"] = "0.1.0",
            },
        };

        await WriteResponseAsync(message["id"]!, result, cancellationToken).ConfigureAwait(false);
    }

    private void HandleDidOpen(JsonObject message)
    {
        var textDocument = message["params"]?["textDocument"]?.AsObject();
        if (textDocument is null)
        {
            return;
        }

        var uri = textDocument["uri"]?.GetValue<string>();
        var text = textDocument["text"]?.GetValue<string>();
        var version = textDocument["version"]?.GetValue<int>() ?? 1;
        if (uri is null || text is null)
        {
            return;
        }

        _workspace.OpenDocument(uri, text, version);
        PublishDiagnostics(uri);
    }

    private void HandleDidChange(JsonObject message)
    {
        var parameters = message["params"]?.AsObject();
        var textDocument = parameters?["textDocument"]?.AsObject();
        var uri = textDocument?["uri"]?.GetValue<string>();
        var version = textDocument?["version"]?.GetValue<int>() ?? 1;
        if (uri is null || !_workspace.TryGetDocument(uri, out _))
        {
            return;
        }

        var contentChanges = parameters?["contentChanges"]?.AsArray();
        var text = contentChanges?.LastOrDefault()?["text"]?.GetValue<string>();
        if (text is null)
        {
            return;
        }

        _workspace.ChangeDocument(uri, text, version);
        PublishDiagnostics(uri);
    }

    private void HandleDidClose(JsonObject message)
    {
        var uri = message["params"]?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is null)
        {
            return;
        }

        _workspace.CloseDocument(uri);
    }

    private void PublishDiagnostics(string uri)
    {
        var compilation = _workspace.CompileDocument(uri);
        var diagnostics = new JsonArray();
        foreach (var report in VelaWorkspace.BuildDiagnosticReports(compilation))
        {
            diagnostics.Add(new JsonObject
            {
                ["range"] = CreateRange(report),
                ["severity"] = report.Severity == "error" ? 1 : 2,
                ["code"] = report.Code,
                ["source"] = "vela",
                ["message"] = report.Message,
            });
        }

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
                ["diagnostics"] = diagnostics,
            },
        };

        WriteMessage(notification);
    }

    private static JsonObject CreateRange(DiagnosticReport report)
        => new()
        {
            ["start"] = new JsonObject
            {
                ["line"] = Math.Max(0, report.Line - 1),
                ["character"] = Math.Max(0, report.Column - 1),
            },
            ["end"] = new JsonObject
            {
                ["line"] = Math.Max(0, report.EndLine - 1),
                ["character"] = Math.Max(0, report.EndColumn - 1),
            },
        };

    private async Task WriteResponseAsync(JsonNode id, JsonNode? result, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result?.DeepClone(),
        };

        await WriteMessageAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorResponseAsync(JsonNode? id, int code, string message, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        await WriteMessageAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void WriteMessage(JsonObject message)
        => WriteMessageAsync(message, CancellationToken.None).GetAwaiter().GetResult();

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {payloadBytes.Length}\r\n\r\n");
        await _output.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(payloadBytes, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var contentLength = await ReadContentLengthAsync(cancellationToken).ConfigureAwait(false);
        if (contentLength is null)
        {
            return null;
        }

        var buffer = new byte[contentLength.Value];
        await _input.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private async Task<int?> ReadContentLengthAsync(CancellationToken cancellationToken)
    {
        int? contentLength = null;
        var headerLength = 0;

        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return headerLength == 0 ? null : throw new EndOfStreamException();
            }

            headerLength += line.Length + 2;
            if (headerLength > MaximumHeaderLength)
            {
                throw new InvalidDataException("Message headers exceed the supported limit.");
            }

            if (line.Length == 0)
            {
                return contentLength ?? throw new InvalidDataException("Content-Length is required.");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException("Invalid message header.");
            }

            if (line[..colon].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (contentLength is not null
                    || !int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length > MaximumContentLength)
                {
                    throw new InvalidDataException("Invalid Content-Length.");
                }

                contentLength = length;
            }
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            if (await _input.ReadAsync(_headerByte, cancellationToken).ConfigureAwait(false) == 0)
            {
                return builder.Length == 0 ? null : throw new EndOfStreamException();
            }

            var next = _headerByte[0];
            if (next == '\r')
            {
                if (await _input.ReadAsync(_headerByte, cancellationToken).ConfigureAwait(false) == 0)
                {
                    throw new EndOfStreamException();
                }

                if (_headerByte[0] != '\n')
                {
                    throw new InvalidDataException("Message headers must use CRLF line endings.");
                }

                return builder.ToString();
            }

            if (next > 127 || next == '\n' || builder.Length >= MaximumHeaderLength)
            {
                throw new InvalidDataException("Invalid or oversized message header.");
            }

            builder.Append((char)next);
        }
    }
}
