using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vela.Http;

/// <summary>Minimal GraphQL schema: named query fields returning JSON text fragments.</summary>
public sealed class VelaGraphqlSchema
{
    private readonly ConcurrentDictionary<string, Func<string, string>> _queries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<string, string>> _mutations = new(StringComparer.Ordinal);

    /// <summary>Registers a query field. Handler receives a JSON object of arguments (may be "{}").</summary>
    public void Query(string name, Func<string, string> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        _queries[name] = handler;
    }

    /// <summary>Registers a query field with no arguments.</summary>
    public void Query(string name, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Query(name, _ => handler());
    }

    /// <summary>Registers a mutation field.</summary>
    public void Mutation(string name, Func<string, string> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        _mutations[name] = handler;
    }

    /// <summary>Registers a mutation field with no arguments.</summary>
    public void Mutation(string name, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Mutation(name, _ => handler());
    }

    /// <summary>Executes a tiny subset of GraphQL queries/mutations and returns a JSON envelope.</summary>
    public string Execute(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document = document.Trim();
        if (document.Length == 0)
        {
            return Error("empty query");
        }

        var parser = new DocumentParser(document);
        if (!parser.TryParse(out var isMutation, out var field, out var arguments))
        {
            return Error("unsupported GraphQL document");
        }

        var map = isMutation ? _mutations : _queries;
        if (!map.TryGetValue(field, out var handler))
        {
            return Error($"unknown field '{field}'");
        }

        string value;
        try
        {
            value = handler(arguments.ToJsonString());
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        return new JsonObject
        {
            ["data"] = new JsonObject { [field] = EnsureJsonValue(value) },
        }.ToJsonString();
    }

    private static string Error(string message)
        => new JsonObject
        {
            ["errors"] = new JsonArray(new JsonObject { ["message"] = message }),
        }.ToJsonString();

    private static JsonNode? EnsureJsonValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return JsonValue.Create(trimmed);
        }
    }

    // One anonymous query/mutation, one field, and named scalar arguments. Consume
    // the entire document before invoking a resolver so unsupported syntax cannot
    // accidentally execute a valid-looking substring (especially a mutation).
    private sealed class DocumentParser(string document)
    {
        private int _position;

        public bool TryParse(out bool isMutation, out string field, out JsonObject arguments)
        {
            isMutation = false;
            field = string.Empty;
            arguments = new JsonObject();
            SkipIgnored();
            if (!Consume('{'))
            {
                var operation = ReadName();
                if (operation is not ("query" or "mutation"))
                {
                    return false;
                }

                isMutation = operation == "mutation";
                if (!Consume('{'))
                {
                    return false;
                }
            }

            field = ReadName();
            if (field.Length == 0)
            {
                return false;
            }

            if (Consume('('))
            {
                do
                {
                    var name = ReadName();
                    if (name.Length == 0 || arguments.ContainsKey(name) || !Consume(':')
                        || !TryReadScalar(out var value))
                    {
                        return false;
                    }

                    arguments.Add(name, value);
                }
                while (!Consume(')'));
            }

            return Consume('}') && _position == document.Length;
        }

        private bool TryReadScalar(out JsonNode? value)
        {
            value = null;
            SkipIgnored();
            var start = _position;
            if (_position < document.Length && document[_position] == '"')
            {
                _position++;
                var closed = false;
                while (_position < document.Length)
                {
                    var current = document[_position++];
                    if (current == '\\' && _position < document.Length)
                    {
                        _position++;
                    }
                    else if (current == '"')
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed)
                {
                    return false;
                }
            }
            else
            {
                while (_position < document.Length
                    && !IsIgnored(document[_position]) && document[_position] != ')')
                {
                    _position++;
                }
            }

            try
            {
                value = JsonNode.Parse(document[start.._position]);
                if (value is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String)
                {
                    // Decode now: JsonNode otherwise defers invalid Unicode
                    // escapes until the resolver tries to read its arguments.
                    value = JsonValue.Create(scalar.GetValue<string>());
                }

                return value is null or JsonValue;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private string ReadName()
        {
            SkipIgnored();
            var start = _position;
            if (_position < document.Length && IsNameStart(document[_position]))
            {
                _position++;
                while (_position < document.Length
                    && (IsNameStart(document[_position]) || char.IsAsciiDigit(document[_position])))
                {
                    _position++;
                }
            }

            return document[start.._position];
        }

        private bool Consume(char expected)
        {
            SkipIgnored();
            if (_position >= document.Length || document[_position] != expected)
            {
                return false;
            }

            _position++;
            SkipIgnored();
            return true;
        }

        private void SkipIgnored()
        {
            while (_position < document.Length && IsIgnored(document[_position]))
            {
                _position++;
            }
        }

        private static bool IsNameStart(char value) => char.IsAsciiLetter(value) || value == '_';

        private static bool IsIgnored(char value) => value is ' ' or '\t' or '\r' or '\n' or ',';
    }
}

/// <summary>Static helpers emitted by the Vela GraphQL core module.</summary>
public static class VelaGraphql
{
    /// <summary>Creates an empty GraphQL schema.</summary>
    public static VelaGraphqlSchema CreateSchema() => new();

    /// <summary>Registers a query field.</summary>
    public static void Query(VelaGraphqlSchema schema, string name, Func<string> handler)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schema.Query(name, handler);
    }

    /// <summary>Registers a query field that receives JSON arguments.</summary>
    public static void QueryArgs(VelaGraphqlSchema schema, string name, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schema.Query(name, handler);
    }

    /// <summary>Registers a mutation field.</summary>
    public static void Mutation(VelaGraphqlSchema schema, string name, Func<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(schema);
        schema.Mutation(name, handler);
    }

    /// <summary>Mounts the schema on an HTTP server path.</summary>
    public static void Mount(HttpServer server, string path, VelaGraphqlSchema schema)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.MountGraphql(path, schema);
    }
}
