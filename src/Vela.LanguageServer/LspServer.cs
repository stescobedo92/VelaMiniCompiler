using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vela.LanguageServer;

/// <summary>Minimal JSON-RPC language server over stdio with Content-Length framing.</summary>
public sealed class LspServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly VelaWorkspace _workspace = new();
    private readonly Stream _input;
    private readonly Stream _output;
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
            var message = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                break;
            }

            await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task HandleMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var method = message["method"]?.GetValue<string>();
        if (method is null)
        {
            if (message["id"] is JsonValue idValue)
            {
                await WriteResponseAsync(idValue, new JsonObject(), cancellationToken).ConfigureAwait(false);
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
            case "shutdown":
                _shutdownRequested = true;
                await WriteResponseAsync(message["id"]!, new JsonObject(), cancellationToken).ConfigureAwait(false);
                break;
            case "exit":
                Environment.Exit(0);
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

    private async Task HandleInitializeAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var result = new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = 1,
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
        if (uri is null)
        {
            return;
        }

        var contentChanges = parameters?["contentChanges"]?.AsArray();
        var text = contentChanges?.FirstOrDefault()?["text"]?.GetValue<string>();
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

    private async Task WriteResponseAsync(JsonNode id, JsonNode result, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result.DeepClone(),
        };

        await WriteMessageAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteErrorResponseAsync(JsonNode id, int code, string message, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
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

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var contentLength = await ReadContentLengthAsync(cancellationToken).ConfigureAwait(false);
        if (contentLength is null)
        {
            return null;
        }

        var buffer = new byte[contentLength.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await _input.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (chunk == 0)
            {
                return null;
            }

            read += chunk;
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(buffer))?.AsObject();
    }

    private async Task<int?> ReadContentLengthAsync(CancellationToken cancellationToken)
    {
        int? contentLength = null;

        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return contentLength;
            }

            if (line.Length == 0)
            {
                return contentLength;
            }

            const string prefix = "Content-Length: ";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[prefix.Length..], out var length))
            {
                contentLength = length;
            }
        }
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = _input.ReadByte();
            if (next == -1)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (next == '\r')
            {
                var following = _input.ReadByte();
                if (following == '\n')
                {
                    return builder.ToString();
                }

                builder.Append('\r');
                if (following != -1)
                {
                    builder.Append((char)following);
                }
            }
            else
            {
                builder.Append((char)next);
            }
        }
    }
}
