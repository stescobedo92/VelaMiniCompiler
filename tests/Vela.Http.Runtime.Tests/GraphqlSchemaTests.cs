using System.Text.Json;
using Vela.Http;
using Xunit;

namespace Vela.Http.Runtime.Tests;

public sealed class GraphqlSchemaTests
{
    [Fact]
    public void ArgumentsPreserveQuotedDelimitersAndEscapes()
    {
        var schema = new VelaGraphqlSchema();
        schema.Query("echo", static arguments => arguments);

        using var result = JsonDocument.Parse(schema.Execute(
            """{ echo(text: "hello, world): {\"quoted\"}", path: "C:\\temp", line: "one\ntwo", unicode: "\u0061", emoji: "\uD83D\uDE00") }"""));

        var arguments = result.RootElement.GetProperty("data").GetProperty("echo");
        Assert.Equal("hello, world): {\"quoted\"}", arguments.GetProperty("text").GetString());
        Assert.Equal("C:\\temp", arguments.GetProperty("path").GetString());
        Assert.Equal("one\ntwo", arguments.GetProperty("line").GetString());
        Assert.Equal("a", arguments.GetProperty("unicode").GetString());
        Assert.Equal("😀", arguments.GetProperty("emoji").GetString());
    }

    [Fact]
    public void ArgumentsSupportScalarsAndOptionalCommas()
    {
        var schema = new VelaGraphqlSchema();
        schema.Query("echo", static arguments => arguments);

        using var result = JsonDocument.Parse(schema.Execute(
            "query { echo(integer: -2, number: 1.25e2 flag: true other: false absent: null) }"));

        var arguments = result.RootElement.GetProperty("data").GetProperty("echo");
        Assert.Equal(-2, arguments.GetProperty("integer").GetInt32());
        Assert.Equal(125, arguments.GetProperty("number").GetDouble());
        Assert.True(arguments.GetProperty("flag").GetBoolean());
        Assert.False(arguments.GetProperty("other").GetBoolean());
        Assert.Equal(JsonValueKind.Null, arguments.GetProperty("absent").ValueKind);
    }

    [Fact]
    public void MutationUsesMutationResolver()
    {
        var schema = new VelaGraphqlSchema();
        schema.Query("echo", static () => "\"query\"");
        schema.Mutation("echo", static arguments => arguments);

        using var result = JsonDocument.Parse(schema.Execute("mutation { echo(text: \"saved\") }"));

        Assert.Equal("saved", result.RootElement.GetProperty("data").GetProperty("echo").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("prefix { echo }")]
    [InlineData("{ echo } trailing")]
    [InlineData("{ echo } { echo }")]
    [InlineData("{ echo other }")]
    [InlineData("{ alias: echo }")]
    [InlineData("{ echo { nested } }")]
    [InlineData("mutation Named { echo }")]
    [InlineData("MUTATION { echo }")]
    [InlineData("{ echo(text: $value) }")]
    [InlineData("{ echo(text: VALUE) }")]
    [InlineData("{ echo(text: [1, 2]) }")]
    [InlineData("{ echo(text: {value: 1}) }")]
    [InlineData("{ echo(text: true, text: false) }")]
    [InlineData("{ echo(text: falsehood) }")]
    [InlineData("{ echo(text: 01) }")]
    [InlineData("{ echo(text) }")]
    [InlineData("{ echo(text:) }")]
    [InlineData("{ echo(text: \"unterminated) }")]
    [InlineData("{ echo(text: \"bad\\q\") }")]
    [InlineData("{ echo(text: \"line\nbreak\") }")]
    [InlineData("""{ echo(text: "\uD800") }""")]
    [InlineData("""{ echo(text: "\uDC00") }""")]
    public void UnsupportedDocumentsDoNotExecuteResolvers(string document)
    {
        var calls = 0;
        var schema = new VelaGraphqlSchema();
        schema.Query("echo", () => { calls++; return "null"; });
        schema.Mutation("echo", () => { calls++; return "null"; });

        using var result = JsonDocument.Parse(schema.Execute(document));

        Assert.True(result.RootElement.TryGetProperty("errors", out _));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("falsehood")]
    [InlineData("true story")]
    [InlineData("not null")]
    [InlineData("123abc")]
    [InlineData("-word")]
    [InlineData("{invalid}")]
    [InlineData("[invalid]")]
    [InlineData("\"unterminated")]
    [InlineData("text\bwith\fcontrols\u0001")]
    [InlineData("null,\"injected\":true")]
    public void NonJsonResolverValuesAreSerializedAsStrings(string value)
    {
        var schema = new VelaGraphqlSchema();
        schema.Query("value", () => value);

        using var result = JsonDocument.Parse(schema.Execute("{ value }"));

        Assert.Equal(value, result.RootElement.GetProperty("data").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("null", JsonValueKind.Null)]
    [InlineData("", JsonValueKind.Null)]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    [InlineData("1.5", JsonValueKind.Number)]
    [InlineData("\"hello\"", JsonValueKind.String)]
    [InlineData("{\"items\":[1,2]}", JsonValueKind.Object)]
    [InlineData("[1,2]", JsonValueKind.Array)]
    public void JsonResolverValuesKeepTheirTypes(string value, JsonValueKind expected)
    {
        var schema = new VelaGraphqlSchema();
        schema.Query("value", () => value);

        using var result = JsonDocument.Parse(schema.Execute("{ value }"));

        Assert.Equal(expected, result.RootElement.GetProperty("data").GetProperty("value").ValueKind);
    }

    [Fact]
    public void ResolverErrorsAreValidJsonEvenWithControlCharacters()
    {
        const string message = "failed\u0001\b\f\n\"quoted\"";
        var schema = new VelaGraphqlSchema();
        schema.Query("fail", static () => throw new InvalidOperationException(message));

        using var result = JsonDocument.Parse(schema.Execute("{ fail }"));

        Assert.Equal(message, result.RootElement.GetProperty("errors")[0].GetProperty("message").GetString());
    }
}
