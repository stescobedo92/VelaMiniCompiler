using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Vela.Http;

/// <summary>Minimal GraphQL schema: named query fields returning JSON text fragments.</summary>
public sealed partial class VelaGraphqlSchema
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
            return "{\"errors\":[{\"message\":\"empty query\"}]}";
        }

        var isMutation = document.StartsWith("mutation", StringComparison.OrdinalIgnoreCase);
        var map = isMutation ? _mutations : _queries;
        var match = FieldRegex().Match(document);
        if (!match.Success)
        {
            return "{\"errors\":[{\"message\":\"unsupported GraphQL document\"}]}";
        }

        var field = match.Groups["name"].Value;
        var argsText = match.Groups["args"].Success ? match.Groups["args"].Value : string.Empty;
        var argsJson = ArgsToJson(argsText);
        if (!map.TryGetValue(field, out var handler))
        {
            return $"{{\"errors\":[{{\"message\":\"unknown field '{field}'\"}}]}}";
        }

        try
        {
            var value = handler(argsJson);
            return $"{{\"data\":{{\"{Escape(field)}\":{EnsureJsonValue(value)}}}}}";
        }
        catch (Exception ex)
        {
            return $"{{\"errors\":[{{\"message\":{Quote(ex.Message)}}}]}}";
        }
    }

    private static string ArgsToJson(string argsText)
    {
        if (string.IsNullOrWhiteSpace(argsText))
        {
            return "{}";
        }

        // text: "hello", n: 1, flag: true  -> {"text":"hello","n":1,"flag":true}
        var parts = argsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var part in parts)
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = part[..colon].Trim();
            var value = part[(colon + 1)..].Trim();
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(Quote(key));
            sb.Append(':');
            sb.Append(value);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string EnsureJsonValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "null";
        }

        if (trimmed[0] is '"' or '{' or '[' or 't' or 'f' or 'n' || char.IsDigit(trimmed[0]) || trimmed[0] == '-')
        {
            return trimmed;
        }

        return Quote(trimmed);
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString()
            });
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    [GeneratedRegex("""(?:query|mutation)?\s*\{\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\((?<args>[^)]*)\))?\s*\}""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FieldRegex();
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
