using Vela.Core.Lexing;
using Vela.Core.Parsing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.LanguageServer;

/// <summary>Classifies one indexed declaration for presentation.</summary>
public enum VelaSymbolKind
{
    Function,
    Method,
    Field,
    Value,
    Class,
    Struct,
    Interface,
    Record,
    Enum,
    EnumMember,
}

/// <summary>One named declaration harvested from a document.</summary>
public sealed record IndexedSymbol(
    string Name,
    string DisplayName,
    VelaSymbolKind Kind,
    TextSpan NameSpan,
    TextSpan FullSpan,
    string Detail,
    string? Documentation,
    IReadOnlyList<IndexedSymbol> Children)
{
    /// <summary>Gets whether this symbol declares members rather than being one.</summary>
    public bool IsContainer => Children.Count > 0;
}

/// <summary>Harvests the declarations of one document for completion, hover, and navigation.</summary>
/// <remarks>
/// The index is purely syntactic and limited to the document it was built
/// from: top-level functions, types and values, plus type members. Local
/// bindings inside function bodies are deliberately excluded because resolving
/// them correctly requires scope analysis the index does not perform.
/// </remarks>
public sealed class VelaDocumentIndex
{
    private readonly List<IndexedSymbol> _symbols = [];

    private VelaDocumentIndex(SourceText source)
    {
        Source = source;
        Tokens = new VelaLexer(source).Lex().Tokens;
    }

    /// <summary>Gets the source this index was built from.</summary>
    public SourceText Source { get; }

    /// <summary>Gets the lexical tokens of the source.</summary>
    public IReadOnlyList<SyntaxToken> Tokens { get; }

    /// <summary>Gets every indexed symbol, members included.</summary>
    public IReadOnlyList<IndexedSymbol> Symbols => _symbols;

    /// <summary>Gets the declarations that are not members of another symbol.</summary>
    public IEnumerable<IndexedSymbol> TopLevel => _symbols.Where(static symbol => !symbol.DisplayName.Contains('.'));

    /// <summary>Builds an index over <paramref name="source"/>, tolerating parse errors.</summary>
    public static VelaDocumentIndex Build(SourceText source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var index = new VelaDocumentIndex(source);
        var root = VelaParser.Parse(source).Root;
        foreach (var member in root.Members)
        {
            index.HarvestStatement(member, parent: null);
        }

        return index;
    }

    /// <summary>Finds the visible declaration called <paramref name="name"/>.</summary>
    /// <remarks>
    /// Top-level declarations win over members with the same name. Member
    /// display names are always qualified (<c>Parent.name</c>) while Vela
    /// identifiers cannot contain dots, so the dot test is exact.
    /// </remarks>
    public IndexedSymbol? FindDefinition(string name)
    {
        IndexedSymbol? member = null;
        foreach (var symbol in _symbols)
        {
            if (!string.Equals(symbol.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!symbol.DisplayName.Contains('.'))
            {
                return symbol;
            }

            member ??= symbol;
        }

        return member;
    }

    /// <summary>Gets the token covering <paramref name="offset"/>, preferring the left token on boundaries.</summary>
    public SyntaxToken? TokenAt(int offset)
    {
        SyntaxToken? candidate = null;
        foreach (var token in Tokens)
        {
            if (token.Span.Start <= offset && offset <= token.Span.End)
            {
                if (candidate is null || token.Span.Start < candidate.Span.Start)
                {
                    candidate = token;
                }
            }
        }

        return candidate;
    }

    private void HarvestStatement(StatementSyntax statement, string? parent)
    {
        switch (statement)
        {
            case FunctionDeclarationSyntax function:
                Add(new IndexedSymbol(
                    function.Identifier.Text,
                    parent is null ? function.Identifier.Text : parent + "." + function.Identifier.Text,
                    parent is null ? VelaSymbolKind.Function : VelaSymbolKind.Method,
                    function.Identifier.Span,
                    function.Span,
                    Header(function.FnKeyword.Span.Start, function.Body.Span.Start),
                    function.Documentation?.Text,
                    []));
                break;
            case LetStatementSyntax let:
                AddValue(let.Identifier, Header(let.LetKeyword.Span.Start, let.EqualsToken.Span.Start));
                break;
            case VarStatementSyntax variable:
                AddValue(variable.Identifier, Header(variable.VarKeyword.Span.Start, variable.EqualsToken.Span.Start));
                break;
            case TupleDestructuringStatementSyntax tuple:
                foreach (var binding in tuple.Bindings)
                {
                    AddValue(binding, binding.Text);
                }

                break;
            case RecordDestructuringStatementSyntax record:
                foreach (var field in record.Fields)
                {
                    AddValue(field, field.Text);
                }

                break;
            case ObjectDeclarationSyntax @object:
                HarvestType(
                    @object.Identifier,
                    @object.Kind switch
                    {
                        ObjectDeclarationKind.Interface => VelaSymbolKind.Interface,
                        ObjectDeclarationKind.Struct => VelaSymbolKind.Struct,
                        _ => VelaSymbolKind.Class,
                    },
                    @object.Keyword.Span.Start,
                    HeaderEnd(@object.Identifier, @object.GenericParameters),
                    @object.Span,
                    @object.Members,
                    @object.Documentation?.Text,
                    static member => member switch
                    {
                        ObjectMethodSyntax method => (method.Function.Identifier, (VelaSymbolKind?)VelaSymbolKind.Method, method.Function.Documentation?.Text),
                        ObjectFieldSyntax field => (field.Identifier, (VelaSymbolKind?)VelaSymbolKind.Field, field.Documentation?.Text),
                        InterfaceMethodSyntax method => (method.Identifier, (VelaSymbolKind?)VelaSymbolKind.Method, method.Documentation?.Text),
                        _ => (null, null, null),
                    });
                break;
            case RecordDeclarationSyntax record:
                HarvestType(
                    record.Identifier,
                    VelaSymbolKind.Record,
                    record.RecordKeyword.Span.Start,
                    HeaderEnd(record.Identifier, record.GenericParameters),
                    record.Span,
                    record.Members,
                    record.Documentation?.Text,
                    static member => member switch
                    {
                        RecordMethodSyntax method => (method.Function.Identifier, (VelaSymbolKind?)VelaSymbolKind.Method, method.Function.Documentation?.Text),
                        RecordFieldSyntax field => (field.Identifier, (VelaSymbolKind?)VelaSymbolKind.Field, field.Documentation?.Text),
                        _ => (null, null, null),
                    });
                break;
            case EnumDeclarationSyntax @enum:
                var enumMembers = new List<IndexedSymbol>();
                foreach (var member in @enum.Members)
                {
                    enumMembers.Add(new IndexedSymbol(
                        member.Identifier.Text,
                        @enum.Identifier.Text + "." + member.Identifier.Text,
                        VelaSymbolKind.EnumMember,
                        member.Identifier.Span,
                        member.Span,
                        member.Identifier.Text,
                        null,
                        []));
                }

                foreach (var member in enumMembers)
                {
                    _symbols.Add(member);
                }

                Add(new IndexedSymbol(
                    @enum.Identifier.Text,
                    @enum.Identifier.Text,
                    VelaSymbolKind.Enum,
                    @enum.Identifier.Span,
                    @enum.Span,
                    @enum.Identifier.Text,
                    null,
                    enumMembers));
                break;
            default:
                break;
        }
    }

    private void HarvestType<TMember>(
        SyntaxToken identifier,
        VelaSymbolKind kind,
        int headerStart,
        int headerEnd,
        TextSpan fullSpan,
        IReadOnlyList<TMember> members,
        string? documentation,
        Func<TMember, (SyntaxToken? Identifier, VelaSymbolKind? Kind, string? Documentation)> describe)
        where TMember : SyntaxNode
    {
        var children = new List<IndexedSymbol>();
        foreach (var member in members)
        {
            var (name, memberKind, memberDocumentation) = describe(member);
            if (name is null || memberKind is null)
            {
                continue;
            }

            children.Add(new IndexedSymbol(
                name.Text,
                identifier.Text + "." + name.Text,
                memberKind.Value,
                name.Span,
                ((SyntaxNode)member).Span,
                identifier.Text + "." + name.Text,
                memberDocumentation,
                []));
        }

        foreach (var child in children)
        {
            _symbols.Add(child);
        }

        Add(new IndexedSymbol(
            identifier.Text,
            identifier.Text,
            kind,
            identifier.Span,
            fullSpan,
            Header(headerStart, headerEnd),
            documentation,
            children));
    }

    private static int HeaderEnd(SyntaxToken identifier, IReadOnlyList<GenericParameterSyntax> generics) =>
        generics.Count == 0 ? identifier.Span.End : generics[^1].Span.End;

    private void AddValue(SyntaxToken identifier, string detail) =>
        Add(new IndexedSymbol(identifier.Text, identifier.Text, VelaSymbolKind.Value, identifier.Span, identifier.Span, detail, null, []));

    private void Add(IndexedSymbol symbol) => _symbols.Add(symbol);

    private string Header(int start, int end)
    {
        if (start < 0 || end > Source.Length || end <= start)
        {
            return string.Empty;
        }

        return string.Join(' ', Source.Text.Substring(start, end - start)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)).Trim().TrimEnd('{').TrimEnd();
    }
}
