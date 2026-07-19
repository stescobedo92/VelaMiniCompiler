using System.Globalization;
using System.Text;
using Vela.Backend.Emission;
using Vela.Core.Diagnostics;
using Vela.Core.Lexing;
using Vela.Core.Source;
using Vela.Core.Syntax;

namespace Vela.Backend;

internal sealed partial class CSharpEmitter
{
    private ExpressionResult EmitIndex(IndexExpressionSyntax index, Scope scope)
    {
        var receiver = EmitExpression(index.Receiver, scope);
        var indexValue = EmitExpression(index.Index, scope);
        return receiver.Type.Name switch
        {
            "List" => EmitVectorIndex(index, receiver, indexValue),
            "Array" => EmitArrayIndex(index, receiver, indexValue),
            "HashMap" => EmitHashMapIndex(index, receiver, indexValue),
            "SortedMap" => EmitSortedMapIndex(index, receiver, indexValue),
            _ => ReportUnsupportedIndex(index, receiver, indexValue)
        };
    }

    private ExpressionResult EmitVectorIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        EnsureAssignable(VelaType.WholeNumber, indexValue.Type, index.Index.Span);
        var elementType = receiver.Type.TypeArguments.Count == 1 ? receiver.Type.TypeArguments[0] : VelaType.Unknown;
        return new ExpressionResult(elementType, $"{receiver.Code}.Get({indexValue.Code}, {SourceLocationCode(index)})");
    }

    private ExpressionResult EmitArrayIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        EnsureAssignable(VelaType.Int, indexValue.Type, index.Index.Span);
        var elementType = receiver.Type.TypeArguments.Count == 1 ? receiver.Type.TypeArguments[0] : VelaType.Unknown;
        return new ExpressionResult(elementType, $"{receiver.Code}.Get({indexValue.Code}, {SourceLocationCode(index)})");
    }

    private ExpressionResult EmitHashMapIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        if (receiver.Type.TypeArguments.Count != 2)
        {
            return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
        }

        var keyType = receiver.Type.TypeArguments[0];
        EnsureAssignable(keyType, indexValue.Type, index.Index.Span);
        EnsureHashable(keyType, index.Index.Span);
        return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}]");
    }

    private ExpressionResult EmitSortedMapIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        if (receiver.Type.TypeArguments.Count != 2)
        {
            return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
        }

        var keyType = receiver.Type.TypeArguments[0];
        EnsureAssignable(keyType, indexValue.Type, index.Index.Span);
        EnsureOrdered(keyType, index.Index.Span);
        return new ExpressionResult(receiver.Type.TypeArguments[1], $"{receiver.Code}[{indexValue.Code}]");
    }

    private ExpressionResult ReportUnsupportedIndex(IndexExpressionSyntax index, ExpressionResult receiver, ExpressionResult indexValue)
    {
        Report("VEL3009", index.Span, $"Type '{receiver.Type}' does not support indexing.", "Use Vector or HashMap indexing, or call a supported collection method.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}[{indexValue.Code}]");
    }
    private ExpressionResult EmitCollectionMethodCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, Scope scope, ExpressionResult? knownReceiver = null)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, "Collection methods do not accept explicit type arguments.");
        }

        var receiver = knownReceiver ?? EmitExpression(member.Receiver, scope);
        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        return receiver.Type.Name switch
        {
            "List" when receiver.Type.TypeArguments.Count == 1 => EmitVectorMethod(call, receiver, member.Member, arguments),
            "HashMap" when receiver.Type.TypeArguments.Count == 2 => EmitHashMapMethod(call, receiver, member.Member, arguments),
            "HashSet" when receiver.Type.TypeArguments.Count == 1 => EmitHashSetMethod(call, receiver, member.Member, arguments),
            "Queue" when receiver.Type.TypeArguments.Count == 1 => EmitQueueMethod(call, receiver, member.Member, arguments),
            "Stack" when receiver.Type.TypeArguments.Count == 1 => EmitStackMethod(call, receiver, member.Member, arguments),
            "RingBuffer" when receiver.Type.TypeArguments.Count == 1 => EmitRingBufferMethod(call, receiver, member.Member, arguments),
            "BitSet" => EmitBitSetMethod(call, receiver, member.Member, arguments),
            "SortedMap" when receiver.Type.TypeArguments.Count == 2 => EmitSortedMapMethod(call, receiver, member.Member, arguments),
            "SortedSet" when receiver.Type.TypeArguments.Count == 1 => EmitSortedSetMethod(call, receiver, member.Member, arguments),
            "Deque" when receiver.Type.TypeArguments.Count == 1 => EmitDequeMethod(call, receiver, member.Member, arguments),
            "PriorityQueue" when receiver.Type.TypeArguments.Count == 1 => EmitPriorityQueueMethod(call, receiver, member.Member, arguments),
            "LinkedList" when receiver.Type.TypeArguments.Count == 1 => EmitLinkedListMethod(call, receiver, member.Member, arguments),
            "Array" when receiver.Type.TypeArguments.Count == 1 => ReportUnsupportedCollectionMethod(call, receiver, member.Member, arguments),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member.Member, arguments)
        };
    }

    private ExpressionResult EmitVectorMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "append" => EmitCollectionOperation(call, receiver, member, "Append", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitHashMapMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var key = receiver.Type.TypeArguments[0];
        var value = receiver.Type.TypeArguments[1];
        EnsureHashable(key, member.Span);
        return member.Text switch
        {
            "set" => EmitCollectionOperation(call, receiver, member, "Set", VelaType.Unit, arguments, [key, value]),
            "try_get" => EmitCollectionOperation(call, receiver, member, "TryGet", new VelaType("Option", [value]), arguments, [key]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [key]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [key]),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitHashSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureHashable(element, member.Span);
        return member.Text switch
        {
            "add" => EmitCollectionOperation(call, receiver, member, "Add", VelaType.Bool, arguments, [element]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitQueueMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "enqueue" => EmitCollectionOperation(call, receiver, member, "Enqueue", VelaType.Unit, arguments, [element]),
            "dequeue" => EmitCollectionOperation(call, receiver, member, "Dequeue", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitStackMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push" => EmitCollectionOperation(call, receiver, member, "Push", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitRingBufferMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "try_enqueue" => EmitCollectionOperation(call, receiver, member, "TryEnqueue", VelaType.Bool, arguments, [element]),
            "dequeue" => EmitCollectionOperation(call, receiver, member, "Dequeue", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitSortedMapMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var key = receiver.Type.TypeArguments[0];
        var value = receiver.Type.TypeArguments[1];
        EnsureOrdered(key, member.Span);
        return member.Text switch
        {
            "set" => EmitCollectionOperation(call, receiver, member, "Set", VelaType.Unit, arguments, [key, value]),
            "try_get" => EmitCollectionOperation(call, receiver, member, "TryGet", new VelaType("Option", [value]), arguments, [key]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [key]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [key]),
            "first_key" => EmitCollectionOperation(call, receiver, member, "FirstKey", new VelaType("Option", [key]), arguments, []),
            "last_key" => EmitCollectionOperation(call, receiver, member, "LastKey", new VelaType("Option", [key]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitSortedSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureOrdered(element, member.Span);
        return member.Text switch
        {
            "add" => EmitCollectionOperation(call, receiver, member, "Add", VelaType.Bool, arguments, [element]),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "first" => EmitCollectionOperation(call, receiver, member, "First", new VelaType("Option", [element]), arguments, []),
            "last" => EmitCollectionOperation(call, receiver, member, "Last", new VelaType("Option", [element]), arguments, []),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitDequeMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push_front" => EmitCollectionOperation(call, receiver, member, "PushFront", VelaType.Unit, arguments, [element]),
            "push_back" => EmitCollectionOperation(call, receiver, member, "PushBack", VelaType.Unit, arguments, [element]),
            "pop_front" => EmitCollectionOperation(call, receiver, member, "PopFront", new VelaType("Option", [element]), arguments, []),
            "pop_back" => EmitCollectionOperation(call, receiver, member, "PopBack", new VelaType("Option", [element]), arguments, []),
            "peek_front" => EmitCollectionOperation(call, receiver, member, "PeekFront", new VelaType("Option", [element]), arguments, []),
            "peek_back" => EmitCollectionOperation(call, receiver, member, "PeekBack", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitPriorityQueueMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        EnsureOrdered(element, member.Span);
        return member.Text switch
        {
            "push" => EmitCollectionOperation(call, receiver, member, "Push", VelaType.Unit, arguments, [element]),
            "pop" => EmitCollectionOperation(call, receiver, member, "Pop", new VelaType("Option", [element]), arguments, []),
            "peek" => EmitCollectionOperation(call, receiver, member, "Peek", new VelaType("Option", [element]), arguments, []),
            "reserve" => EmitCollectionOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, [VelaType.WholeNumber]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitLinkedListMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        var element = receiver.Type.TypeArguments[0];
        return member.Text switch
        {
            "push_front" => EmitCollectionOperation(call, receiver, member, "PushFront", VelaType.Unit, arguments, [element]),
            "push_back" => EmitCollectionOperation(call, receiver, member, "PushBack", VelaType.Unit, arguments, [element]),
            "pop_front" => EmitCollectionOperation(call, receiver, member, "PopFront", new VelaType("Option", [element]), arguments, []),
            "pop_back" => EmitCollectionOperation(call, receiver, member, "PopBack", new VelaType("Option", [element]), arguments, []),
            "peek_front" => EmitCollectionOperation(call, receiver, member, "PeekFront", new VelaType("Option", [element]), arguments, []),
            "peek_back" => EmitCollectionOperation(call, receiver, member, "PeekBack", new VelaType("Option", [element]), arguments, []),
            "contains" => EmitCollectionOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, [element]),
            "remove" => EmitCollectionOperation(call, receiver, member, "Remove", VelaType.Bool, arguments, [element]),
            "clear" => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitBitSetMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        return member.Text switch
        {
            "set" => EmitBitSetOperation(call, receiver, member, "Set", VelaType.Unit, arguments, expectsIndex: true),
            "clear" when arguments.Length == 0 => EmitCollectionOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, []),
            "clear" => EmitBitSetOperation(call, receiver, member, "Clear", VelaType.Unit, arguments, expectsIndex: true),
            "contains" => EmitBitSetOperation(call, receiver, member, "Contains", VelaType.Bool, arguments, expectsIndex: true),
            "reserve" => EmitBitSetOperation(call, receiver, member, "Reserve", VelaType.Unit, arguments, expectsIndex: false),
            _ => ReportUnsupportedCollectionMethod(call, receiver, member, arguments)
        };
    }

    private ExpressionResult EmitBitSetOperation(
        CallExpressionSyntax call,
        ExpressionResult receiver,
        SyntaxToken member,
        string runtimeMember,
        VelaType returnType,
        ExpressionResult[] arguments,
        bool expectsIndex)
    {
        if (arguments.Length != 1)
        {
            var kind = expectsIndex ? "index" : "capacity";
            Report("VEL3006", call.Span, $"Method '{member.Text}' expects exactly one Int {kind} argument.");
        }

        if (arguments.Length > 0)
        {
            EnsureAssignable(VelaType.WholeNumber, arguments[0].Type, call.Arguments[0].Span);
        }

        var argumentCode = arguments.Length == 0 ? "0" : $"checked((int){arguments[0].Code})";
        return new ExpressionResult(returnType, $"{receiver.Code}.{runtimeMember}({argumentCode})");
    }

    private ExpressionResult EmitCollectionOperation(
        CallExpressionSyntax call,
        ExpressionResult receiver,
        SyntaxToken member,
        string runtimeMember,
        VelaType returnType,
        ExpressionResult[] arguments,
        IReadOnlyList<VelaType> parameterTypes)
    {
        if (arguments.Length != parameterTypes.Count)
        {
            Report("VEL3006", call.Span, $"Method '{member.Text}' expects {parameterTypes.Count} argument(s), but received {arguments.Length}.");
        }

        for (var index = 0; index < Math.Min(arguments.Length, parameterTypes.Count); index++)
        {
            EnsureAssignable(parameterTypes[index], arguments[index].Type, call.Arguments[index].Span);
        }

        var argumentsCode = arguments.Select((argument, index) => index < parameterTypes.Count
            ? CoerceCode(parameterTypes[index], argument, call.Arguments[index])
            : argument.Code);
        return new ExpressionResult(returnType, $"{receiver.Code}.{runtimeMember}({string.Join(", ", argumentsCode)})");
    }

    private ExpressionResult ReportUnsupportedCollectionMethod(CallExpressionSyntax call, ExpressionResult receiver, SyntaxToken member, ExpressionResult[] arguments)
    {
        Report("VEL3009", member.Span, $"Type '{receiver.Type}' does not contain method '{member.Text}'.", "Use a supported collection operation or correct the method name.");
        return new ExpressionResult(VelaType.Unknown, $"{receiver.Code}.{EscapeIdentifier(member.Text)}({string.Join(", ", arguments.Select(static argument => argument.Code))})");
    }

    private ExpressionResult EmitCollectionConstruction(CallExpressionSyntax call, string name, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        return name switch
        {
            "List" or "Vector" => EmitGenericCollectionConstruction(call, name, "VelaVector", "List", 1, arguments, explicitTypes, capacityRequired: false),
            "HashMap" => EmitGenericCollectionConstruction(call, name, "VelaHashMap", "HashMap", 2, arguments, explicitTypes, capacityRequired: false),
            "HashSet" => EmitGenericCollectionConstruction(call, name, "VelaHashSet", "HashSet", 1, arguments, explicitTypes, capacityRequired: false),
            "Queue" => EmitGenericCollectionConstruction(call, name, "VelaQueue", "Queue", 1, arguments, explicitTypes, capacityRequired: false),
            "Stack" => EmitGenericCollectionConstruction(call, name, "VelaStack", "Stack", 1, arguments, explicitTypes, capacityRequired: false),
            "RingBuffer" => EmitGenericCollectionConstruction(call, name, "RingBuffer", "RingBuffer", 1, arguments, explicitTypes, capacityRequired: true),
            "Array" => EmitGenericCollectionConstruction(call, name, "VelaArray", "Array", 1, arguments, explicitTypes, capacityRequired: true),
            "BitSet" => EmitBitSetConstruction(call, arguments, explicitTypes),
            "SortedMap" => EmitGenericCollectionConstruction(call, name, "VelaSortedMap", "SortedMap", 2, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            "SortedSet" => EmitGenericCollectionConstruction(call, name, "VelaSortedSet", "SortedSet", 1, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            "Deque" => EmitGenericCollectionConstruction(call, name, "VelaDeque", "Deque", 1, arguments, explicitTypes, capacityRequired: false),
            "PriorityQueue" => EmitGenericCollectionConstruction(call, name, "VelaPriorityQueue", "PriorityQueue", 1, arguments, explicitTypes, capacityRequired: false),
            "LinkedList" => EmitGenericCollectionConstruction(call, name, "VelaLinkedList", "LinkedList", 1, arguments, explicitTypes, capacityRequired: false, capacitySupported: false),
            _ => throw new InvalidOperationException($"Unsupported collection constructor '{name}'.")
        };
    }

    private ExpressionResult EmitGenericCollectionConstruction(
        CallExpressionSyntax call,
        string sourceName,
        string runtimeName,
        string canonicalName,
        int expectedTypeArgumentCount,
        ExpressionResult[] arguments,
        VelaType[] explicitTypes,
        bool capacityRequired,
        bool capacitySupported = true)
    {
        var typeArguments = ValidateCollectionTypeArguments(call, sourceName, explicitTypes, expectedTypeArgumentCount);
        if (canonicalName is "HashMap" or "HashSet")
        {
            EnsureHashable(typeArguments[0], call.Span);
        }

        if (canonicalName is "SortedMap" or "SortedSet" or "PriorityQueue")
        {
            EnsureOrdered(typeArguments[0], call.Span);
        }

        if (!capacitySupported)
        {
            if (arguments.Length != 0)
            {
                Report("VEL3006", call.Span, $"Collection '{sourceName}' does not accept constructor arguments.");
            }

            return new ExpressionResult(new VelaType(canonicalName, typeArguments), $"new {runtimeName}<{string.Join(", ", typeArguments.Select(CSharpType))}>()");
        }

        ValidateCapacityConstructorArguments(call, sourceName, arguments, capacityRequired);
        var capacity = arguments.Length == 0 ? string.Empty : arguments[0].Code;
        var genericSuffix = $"<{string.Join(", ", typeArguments.Select(CSharpType))}>";
        return new ExpressionResult(new VelaType(canonicalName, typeArguments), $"new {runtimeName}{genericSuffix}({capacity})");
    }

    private ExpressionResult EmitBitSetConstruction(CallExpressionSyntax call, ExpressionResult[] arguments, VelaType[] explicitTypes)
    {
        _ = ValidateCollectionTypeArguments(call, "BitSet", explicitTypes, 0);
        ValidateCapacityConstructorArguments(call, "BitSet", arguments, capacityRequired: true);
        var capacity = arguments.Length == 0 ? "0L" : arguments[0].Code;
        return new ExpressionResult(new VelaType("BitSet"), $"new BitSet(checked((int){capacity}))");
    }

    private VelaType[] ValidateCollectionTypeArguments(CallExpressionSyntax call, string name, VelaType[] explicitTypes, int expectedCount)
    {
        if (explicitTypes.Length != expectedCount)
        {
            Report("VEL3006", call.Span, $"Collection '{name}' expects {expectedCount} type argument(s), but received {explicitTypes.Length}.", "Supply explicit type arguments for the collection element or key and value types.");
        }

        return Enumerable.Range(0, expectedCount)
            .Select(index => index < explicitTypes.Length ? explicitTypes[index] : VelaType.Unknown)
            .ToArray();
    }

    private void ValidateCapacityConstructorArguments(CallExpressionSyntax call, string name, ExpressionResult[] arguments, bool capacityRequired)
    {
        var minimum = capacityRequired ? 1 : 0;
        if (arguments.Length < minimum || arguments.Length > 1)
        {
            var expected = capacityRequired ? "exactly one" : "zero or one";
            Report("VEL3006", call.Span, $"Collection '{name}' expects {expected} Int capacity argument.");
        }

        if (arguments.Length > 0)
        {
            EnsureAssignable(VelaType.WholeNumber, arguments[0].Type, call.Arguments[0].Span);
            if (TryGetIntegerConstant(call.Arguments[0].Expression, out var capacity))
            {
                if (capacity < 0)
                {
                    Report("VEL3006", call.Arguments[0].Span, $"Collection '{name}' capacity cannot be negative.", "Use zero or a positive Int capacity.");
                }
                else if (name == "RingBuffer" && capacity == 0)
                {
                    Report("VEL3006", call.Arguments[0].Span, "RingBuffer capacity must be positive.", "Use an Int capacity greater than zero.");
                }
            }
        }
    }

    private static bool TryGetIntegerConstant(ExpressionSyntax expression, out long value)
    {
        if (expression is LiteralExpressionSyntax { LiteralToken.Kind: TokenKind.IntegerLiteral, LiteralToken.Value: long integer })
        {
            value = integer;
            return true;
        }

        if (expression is UnaryExpressionSyntax
            {
                OperatorToken.Kind: TokenKind.Minus,
                Operand: LiteralExpressionSyntax { LiteralToken.Kind: TokenKind.IntegerLiteral, LiteralToken.Value: long negativeInteger }
            })
        {
            value = -negativeInteger;
            return true;
        }

        value = default;
        return false;
    }
    private ExpressionResult EmitList(ListExpressionSyntax list, Scope scope)
    {
        var elements = list.Elements.Select(element => EmitExpression(element, scope)).ToArray();
        var elementType = elements.Length == 0 ? VelaType.Unknown : elements[0].Type;
        foreach (var element in elements.Skip(1))
        {
            EnsureAssignable(elementType, element.Type, list.Span);
        }

        if (elementType.IsUnknown)
        {
            Report("VEL3006", list.Span, "Cannot infer the type of an empty list.", "Add at least one element or use an explicitly typed variable.");
        }

        return new ExpressionResult(
            new VelaType("List", [elementType]),
            $"new VelaVector<{CSharpType(elementType)}>(new {CSharpType(elementType)}[] {{ {string.Join(", ", elements.Select(static element => element.Code))} }})");
    }
    private VelaType GetIterationElementType(VelaType collectionType, TextSpan span)
    {
        if (collectionType.Name is "List" or "Array" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "SortedSet" or "Deque" or "LinkedList" && collectionType.TypeArguments.Count == 1)
        {
            return collectionType.TypeArguments[0];
        }

        Report("VEL3006", span, $"Type '{collectionType}' cannot be iterated.", "Use Vector, HashSet, SortedSet, Queue, Stack, RingBuffer, Deque, or LinkedList in a for loop.");
        return VelaType.Unknown;
    }

    private static bool HasCountProperty(VelaType type) => type.Name is "List" or "HashMap" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "SortedMap" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList";

    private static bool HasCapacityProperty(VelaType type) => type.Name is "List" or "Queue" or "RingBuffer" or "BitSet" or "Deque" or "PriorityQueue";

    private static bool IsCollectionConstructor(string name) => name is "List" or "Vector" or "Array" or "HashMap" or "HashSet" or "Queue" or "Stack" or "RingBuffer" or "BitSet" or "SortedMap" or "SortedSet" or "Deque" or "PriorityQueue" or "LinkedList";
}
