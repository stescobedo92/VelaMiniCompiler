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
    private ExpressionResult EmitCoreModuleCall(CallExpressionSyntax call, MemberAccessExpressionSyntax member, string module, Scope scope)
    {
        if (call.TypeArguments.Count != 0)
        {
            Report("VEL3006", call.Span, $"Core module function '{member.Member.Text}' does not accept type arguments.");
        }

        var arguments = call.Arguments.Select(argument => EmitExpression(argument.Expression, scope)).ToArray();
        return module switch
        {
            "vela.core.json" => EmitJsonCall(call, member.Member, arguments),
            "vela.core.crypto" => EmitCryptoCall(call, member.Member, arguments),
            "vela.core.tcp" => EmitTcpCall(call, member.Member, arguments),
            "vela.core.text" => EmitTextCall(call, member.Member, arguments),
            "vela.core.math" => EmitMathCall(call, member.Member, arguments),
            "vela.core.time" => EmitTimeCall(call, member.Member, arguments),
            "vela.core.random" => EmitRandomCall(call, member.Member, arguments),
            "vela.core.io" => EmitIoCall(call, member.Member, arguments),
            "vela.core.encoding" => EmitEncodingCall(call, member.Member, arguments),
            "vela.core.env" => EmitEnvironmentCall(call, member.Member, arguments),
            "vela.core.system" => EmitSystemCall(call, member.Member, arguments),
            "vela.core.console" => EmitConsoleCall(call, member.Member, arguments),
            "vela.core.gui" => EmitGuiCall(call, member.Member, arguments),
            "vela.core.http" => EmitHttpCall(call, member.Member, arguments),
            "vela.core.graphql" => EmitGraphqlCall(call, member.Member, arguments),
            "vela.core.grpc" => EmitGrpcCall(call, member.Member, arguments),
            "vela.core.sqlite" => EmitSqliteCall(call, member.Member, arguments),
            "vela.core.postgres" => EmitPostgresCall(call, member.Member, arguments),
            "vela.concurrent" => EmitConcurrentCall(call, member.Member, arguments),
            _ => ReportUnknownCoreOperation(member.Member, module)
        };
    }

    private ExpressionResult EmitSqliteCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "open" => EmitCoreFunction(call, operation, arguments, VelaType.SqliteDatabase, [VelaType.Text], values => $"VelaSqlite.Open({values[0]})"),
        "execute" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Execute({values[0]}, {values[1]})"),
        "query" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Query({values[0]}, {values[1]})"),
        "migrate" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.SqliteDatabase, VelaType.Text], values => $"VelaSqlite.Migrate({values[0]}, {values[1]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.SqliteDatabase], values => $"VelaSqlite.Close({values[0]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.sqlite")
    };

    private ExpressionResult EmitPostgresCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "open" => EmitCoreFunction(call, operation, arguments, VelaType.PostgresDatabase, [VelaType.Text], values => $"VelaPostgres.Open({values[0]})"),
        "execute" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Execute({values[0]}, {values[1]})"),
        "query" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Query({values[0]}, {values[1]})"),
        "migrate" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.PostgresDatabase, VelaType.Text], values => $"VelaPostgres.Migrate({values[0]}, {values[1]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.PostgresDatabase], values => $"VelaPostgres.Close({values[0]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.postgres")
    };

    private ExpressionResult EmitJsonCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "is_valid" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaJson.IsValid({values[0]})"),
        "quote" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Quote({values[0]})"),
        "compact" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Compact({values[0]}, {SourceLocationCode(call)})"),
        "pretty" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaJson.Pretty({values[0]}, {SourceLocationCode(call)})"),
        "try_get_text" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "try_get_int" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Int]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetInt({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "try_get_bool" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Bool]), [VelaType.Text, VelaType.Text], values => $"VelaJson.TryGetBool({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.json")
    };

    private ExpressionResult EmitCryptoCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "sha256" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaCrypto.Sha256({values[0]})"),
        "hmac_sha256" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaCrypto.HmacSha256({values[0]}, {values[1]})"),
        "constant_time_equals" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaCrypto.ConstantTimeEquals({values[0]}, {values[1]})"),
        "random_hex" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaCrypto.RandomHex({values[0]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.crypto")
    };

    private ExpressionResult EmitTcpCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "connect" => EmitCoreFunction(call, operation, arguments, VelaType.TcpConnection, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"TcpConnection.Connect({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "connect_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.TcpConnection]), [VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Cancellation], values => $"TcpConnection.ConnectAsync({values[0]}, {values[1]}, {values[2]}, {values[3]}, {SourceLocationCode(call)})"),
        "send_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.TcpConnection, VelaType.Text], values => $"{values[0]}.SendText({values[1]}, {SourceLocationCode(call)})"),
        "send_text_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.Unit]), [VelaType.TcpConnection, VelaType.Text, VelaType.Cancellation], values => $"{values[0]}.SendTextAsync({values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "receive_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.TcpConnection, VelaType.Int], values => $"{values[0]}.ReceiveText({values[1]}, {SourceLocationCode(call)})"),
        "receive_text_async" => EmitCoreFunction(call, operation, arguments, new VelaType("Future", [VelaType.Text]), [VelaType.TcpConnection, VelaType.Int, VelaType.Cancellation], values => $"{values[0]}.ReceiveTextAsync({values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.TcpConnection], values => $"{values[0]}.Dispose()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.tcp")
    };

    private ExpressionResult EmitTextCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "length" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Text], values => $"VelaTextOperations.Length({values[0]})"),
        "contains" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.Contains({values[0]}, {values[1]})"),
        "starts_with" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.StartsWith({values[0]}, {values[1]})"),
        "ends_with" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text, VelaType.Text], values => $"VelaTextOperations.EndsWith({values[0]}, {values[1]})"),
        "trim" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.Trim({values[0]})"),
        "to_upper_invariant" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.ToUpperInvariant({values[0]})"),
        "to_lower_invariant" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaTextOperations.ToLowerInvariant({values[0]})"),
        "from_int" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaTextOperations.FromInt({values[0]})"),
        "from_long" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Long], values => $"VelaTextOperations.FromLong({values[0]})"),
        "from_double" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Double], values => $"VelaTextOperations.FromDouble({values[0]})"),
        "from_bool" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Bool], values => $"VelaTextOperations.FromBool({values[0]})"),
        "try_parse_int" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Int]), [VelaType.Text], values => $"VelaTextOperations.TryParseInt({values[0]})"),
        "try_parse_long" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Long]), [VelaType.Text], values => $"VelaTextOperations.TryParseLong({values[0]})"),
        "try_parse_double" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Double]), [VelaType.Text], values => $"VelaTextOperations.TryParseDouble({values[0]})"),
        "try_parse_bool" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Bool]), [VelaType.Text], values => $"VelaTextOperations.TryParseBool({values[0]})"),
        "from_code_point" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Int], values => $"VelaTextOperations.FromCodePoint({values[0]}, {SourceLocationCode(call)})"),
        "slice" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaTextOperations.Slice({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.text")
    };

    private ExpressionResult EmitMathCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments)
    {
        if (operation.Text is "sqrt" or "pow")
        {
            var count = operation.Text == "sqrt" ? 1 : 2;
            if (arguments.Length != count)
            {
                Report("VEL3006", call.Span, $"Core function 'math.{operation.Text}' expects {count} argument(s), but received {arguments.Length}.");
            }

            var values = Enumerable.Range(0, count).Select(index => index < arguments.Length ? arguments[index] : new ExpressionResult(VelaType.Double, "0d")).ToArray();
            foreach (var value in values)
            {
                if (!value.Type.IsNumeric)
                {
                    Report("VEL3002", call.Span, $"Core function 'math.{operation.Text}' requires numeric arguments.");
                }
            }

            var numbers = values.Select(value => CoerceNumeric(VelaType.Double, value, call)).ToArray();
            return operation.Text == "sqrt"
                ? new ExpressionResult(VelaType.Double, $"VelaMath.Sqrt({numbers[0]}, {SourceLocationCode(call)})")
                : new ExpressionResult(VelaType.Double, $"VelaMath.Pow({numbers[0]}, {numbers[1]}, {SourceLocationCode(call)})");
        }

        var expectedCount = operation.Text == "abs" ? 1 : operation.Text is "min" or "max" ? 2 : operation.Text == "clamp" ? 3 : 0;
        if (expectedCount == 0)
        {
            return ReportUnknownCoreOperation(operation, "vela.core.math");
        }

        if (arguments.Length != expectedCount)
        {
            Report("VEL3006", call.Span, $"Core function 'math.{operation.Text}' expects {expectedCount} argument(s), but received {arguments.Length}.");
        }

        var resultType = arguments.Length == 0 ? VelaType.Int : arguments[0].Type;
        if (!resultType.IsNumeric)
        {
            Report("VEL3002", call.Span, $"Core function 'math.{operation.Text}' requires numeric arguments.");
        }

        var numericValues = Enumerable.Range(0, expectedCount).Select(index => index < arguments.Length ? arguments[index] : new ExpressionResult(resultType, DefaultValue(resultType))).ToArray();
        foreach (var value in numericValues)
        {
            EnsureAssignable(resultType, value.Type, call.Span);
        }

        var codes = numericValues.Select(value => CoerceCode(resultType, value, call)).ToArray();
        var method = operation.Text switch
        {
            "abs" => "Abs",
            "min" => "Min",
            "max" => "Max",
            _ => "Clamp"
        };
        var location = method == "Abs" && resultType.Name is "Int" or "Long"
            ? ", " + SourceLocationCode(call)
            : string.Empty;
        return new ExpressionResult(resultType, $"VelaMath.{method}({string.Join(", ", codes)}{location})");
    }

    private ExpressionResult EmitTimeCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "utc_unix_milliseconds" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [], _ => "VelaTime.UtcUnixMilliseconds()"),
        "monotonic_ticks" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [], _ => "VelaTime.MonotonicTicks()"),
        "elapsed_milliseconds" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [VelaType.Long, VelaType.Long], values => $"VelaTime.ElapsedMilliseconds({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.time")
    };

    private ExpressionResult EmitRandomCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "next_int" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Int, VelaType.Int], values => $"VelaRandom.NextInt({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "next_double" => EmitCoreFunction(call, operation, arguments, VelaType.Double, [], _ => "VelaRandom.NextDouble()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.random")
    };

    private ExpressionResult EmitIoCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "exists" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaIo.Exists({values[0]})"),
        "directory_exists" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Text], values => $"VelaIo.DirectoryExists({values[0]})"),
        "read_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.ReadText({values[0]}, {SourceLocationCode(call)})"),
        "write_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaIo.WriteText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "append_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaIo.AppendText({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "read_lines" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ReadLines({values[0]}, {SourceLocationCode(call)})"),
        "write_lines" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, new VelaType("List", [VelaType.Text])], values => $"VelaIo.WriteLines({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "delete_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaIo.DeleteFile({values[0]}, {SourceLocationCode(call)})"),
        "copy_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text, VelaType.Bool], values => $"VelaIo.CopyFile({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "move_file" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text, VelaType.Bool], values => $"VelaIo.MoveFile({values[0]}, {values[1]}, {values[2]}, {SourceLocationCode(call)})"),
        "file_size" => EmitCoreFunction(call, operation, arguments, VelaType.Long, [VelaType.Text], values => $"VelaIo.FileSize({values[0]}, {SourceLocationCode(call)})"),
        "create_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaIo.CreateDirectory({values[0]}, {SourceLocationCode(call)})"),
        "delete_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Bool], values => $"VelaIo.DeleteDirectory({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "list_files" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ListFiles({values[0]}, {SourceLocationCode(call)})"),
        "list_directories" => EmitCoreFunction(call, operation, arguments, new VelaType("Array", [VelaType.Text]), [VelaType.Text], values => $"VelaIo.ListDirectories({values[0]}, {SourceLocationCode(call)})"),
        "combine" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaIo.Combine({values[0]}, {values[1]}, {SourceLocationCode(call)})"),
        "file_name" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.FileName({values[0]}, {SourceLocationCode(call)})"),
        "extension" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.Extension({values[0]}, {SourceLocationCode(call)})"),
        "full_path" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaIo.FullPath({values[0]}, {SourceLocationCode(call)})"),
        "temporary_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => $"VelaIo.TemporaryFile({SourceLocationCode(call)})"),
        "temporary_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => $"VelaIo.TemporaryDirectory({SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.io")
    };

    private ExpressionResult EmitSystemCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "exec" => EmitSystemExec(call, operation, arguments),
        "which" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text], values => $"VelaSystem.Which({values[0]})"),
        "process_id" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [], _ => "VelaSystem.ProcessId()"),
        "temporary_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => "VelaSystem.TemporaryDirectory()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.system")
    };

    private ExpressionResult EmitSystemExec(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments)
    {
        var values = BindNamedCoreArguments(call, operation, arguments, SystemExecParameters);
        return new ExpressionResult(VelaType.ProcessResult, $"VelaSystem.Exec({values[0]}, {values[1]}, {values[2]}, {values[3]}, {SourceLocationCode(call)})");
    }

    private ExpressionResult EmitConsoleCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "write" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.Write({values[0]})"),
        "write_line" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteLine({values[0]})"),
        "write_error" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteError({values[0]})"),
        "write_error_line" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text], values => $"VelaConsole.WriteErrorLine({values[0]})"),
        "is_output_redirected" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [], _ => "VelaConsole.IsOutputRedirected()"),
        "supports_color" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [], _ => "VelaConsole.SupportsColor()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.console")
    };

    private ExpressionResult EmitGuiCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "show_message" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Text, VelaType.Text], values => $"VelaGui.ShowMessage({values[0]}, {values[1]})"),
        "prompt" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text, VelaType.Text], values => $"VelaGui.Prompt({values[0]}, {values[1]}, {values[2]})"),
        "run_hello_form" => EmitCoreFunction(
            call,
            operation,
            arguments,
            VelaType.Int,
            [VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Text],
            values => $"VelaGui.RunHelloForm({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "run_counter_app" => EmitCoreFunction(
            call,
            operation,
            arguments,
            VelaType.Int,
            [VelaType.Text, VelaType.Text, VelaType.Int],
            values => $"VelaGui.RunCounterApp({values[0]}, {values[1]}, {values[2]})"),
        "create_form" => EmitCoreFunction(call, operation, arguments, VelaType.GuiForm, [VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.CreateForm({values[0]}, {values[1]}, {values[2]})"),
        "add_label" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddLabel({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_button" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddButton({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "add_textbox" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddTextBox({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "show" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.Show({values[0]})"),
        "show_owned" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm, VelaType.GuiForm], values => $"VelaGuiComponents.ShowOwned({values[0]}, {values[1]})"),
        "process_events" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.ProcessEvents({values[0]})"),
        "is_open" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiForm], values => $"VelaGuiComponents.IsOpen({values[0]})"),
        "close" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiForm], values => $"VelaGuiComponents.Close({values[0]})"),
        "set_text" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.SetText({values[0]}, {values[1]})"),
        "get_text" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.GetText({values[0]})"),
        "was_clicked" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiControl], values => $"VelaGuiComponents.WasClicked({values[0]})"),
        "on_click" => EmitGuiCallback(call, arguments, "on_click", CreateFunctionType([], VelaType.Unit), "OnClick"),
        "on_text_changed" => EmitGuiCallback(call, arguments, "on_text_changed", CreateFunctionType([VelaType.Text], VelaType.Unit), "OnTextChanged"),
        "on_checked_changed" => EmitGuiCallback(call, arguments, "on_checked_changed", CreateFunctionType([VelaType.Bool], VelaType.Unit), "OnCheckedChanged"),
        "on_value_changed" => EmitGuiCallback(call, arguments, "on_value_changed", CreateFunctionType([VelaType.Int], VelaType.Unit), "OnValueChanged"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GuiForm], values => $"VelaGuiComponents.Run({values[0]})"),
        "create_form_layout" => EmitCoreFunction(call, operation, arguments, VelaType.GuiForm, [VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Text], values => $"VelaGuiComponents.CreateFormLayout({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_checkbox" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddCheckBox({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_progress" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddProgress({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_combo" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddComboBox({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_list" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddList({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_grid" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddGrid({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]})"),
        "add_slider" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddSlider({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]}, {values[6]}, {values[7]})"),
        "add_textarea" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddTextArea({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]})"),
        "add_numeric" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddNumeric({values[0]}, {values[1]}, {values[2]}, {values[3]}, {values[4]}, {values[5]}, {values[6]}, {values[7]})"),
        "add_radio" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Text, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddRadio({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "add_separator" => EmitCoreFunction(call, operation, arguments, VelaType.GuiControl, [VelaType.GuiForm, VelaType.Int, VelaType.Int, VelaType.Int], values => $"VelaGuiComponents.AddSeparator({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "get_value" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GuiControl], values => $"VelaGuiComponents.GetValue({values[0]})"),
        "set_value" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Int], values => $"VelaGuiComponents.SetValue({values[0]}, {values[1]})"),
        "add_menu_item" => EmitGuiCallback(call, arguments, "add_menu_item", CreateFunctionType([], VelaType.Unit), "AddMenuItem", formFirst: true),
        "open_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiForm, VelaType.Text], values => $"VelaGuiComponents.OpenFile({values[0]}, {values[1]})"),
        "save_file" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiForm, VelaType.Text, VelaType.Text], values => $"VelaGuiComponents.SaveFile({values[0]}, {values[1]}, {values[2]})"),
        "combo_add" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.ComboAdd({values[0]}, {values[1]})"),
        "combo_selected" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.ComboSelected({values[0]})"),
        "list_add" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.ListAdd({values[0]}, {values[1]})"),
        "list_clear" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl], values => $"VelaGuiComponents.ListClear({values[0]})"),
        "list_selected" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.GuiControl], values => $"VelaGuiComponents.ListSelected({values[0]})"),
        "grid_add_row" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Text], values => $"VelaGuiComponents.GridAddRow({values[0]}, {values[1]})"),
        "grid_clear" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl], values => $"VelaGuiComponents.GridClear({values[0]})"),
        "is_checked" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.GuiControl], values => $"VelaGuiComponents.IsChecked({values[0]})"),
        "set_checked" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetChecked({values[0]}, {values[1]})"),
        "set_progress" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Int], values => $"VelaGuiComponents.SetProgress({values[0]}, {values[1]})"),
        "set_enabled" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetEnabled({values[0]}, {values[1]})"),
        "set_visible" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GuiControl, VelaType.Bool], values => $"VelaGuiComponents.SetVisible({values[0]}, {values[1]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.gui")
    };

    private ExpressionResult EmitHttpCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_server" => EmitCoreFunction(call, operation, arguments, VelaType.HttpServer, [VelaType.Text, VelaType.Int], values => $"VelaHttp.CreateServer({values[0]}, {values[1]})"),
        "get" => EmitHttpRouteCallback(call, arguments, "get", CreateFunctionType([], VelaType.Text), "Get"),
        "post" => EmitHttpRouteCallback(call, arguments, "post", CreateFunctionType([VelaType.Text], VelaType.Text), "Post"),
        "put" => EmitHttpRouteCallback(call, arguments, "put", CreateFunctionType([VelaType.Text], VelaType.Text), "Put"),
        "delete" => EmitHttpRouteCallback(call, arguments, "delete", CreateFunctionType([], VelaType.Text), "Delete"),
        "start" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.HttpServer], values => $"VelaHttp.Start({values[0]})"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.HttpServer], values => $"VelaHttp.Run({values[0]})"),
        "stop" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer], values => $"VelaHttp.Stop({values[0]})"),
        "set_max_body" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer, VelaType.Int], values => $"VelaHttp.SetMaxBody({values[0]}, {values[1]})"),
        "client_get" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text], values => $"VelaHttp.ClientGet({values[0]}, {values[1]}, {values[2]})"),
        "client_post" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text, VelaType.Text], values => $"VelaHttp.ClientPost({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        "request_get" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int], values => $"VelaHttpClient.Get({values[0]}, {values[1]})"),
        "request_post" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text, VelaType.Text, VelaType.Int], values => $"VelaHttpClient.Post({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.http")
    };

    private ExpressionResult EmitGraphqlCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_schema" => EmitCoreFunction(call, operation, arguments, VelaType.GraphqlSchema, [], _ => "VelaGraphql.CreateSchema()"),
        "query" => EmitGraphqlFieldCallback(call, arguments, "query", CreateFunctionType([], VelaType.Text), "Query"),
        "query_args" => EmitGraphqlFieldCallback(call, arguments, "query_args", CreateFunctionType([VelaType.Text], VelaType.Text), "QueryArgs"),
        "mutation" => EmitGraphqlFieldCallback(call, arguments, "mutation", CreateFunctionType([VelaType.Text], VelaType.Text), "Mutation"),
        "mount" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.HttpServer, VelaType.Text, VelaType.GraphqlSchema], values => $"VelaGraphql.Mount({values[0]}, {values[1]}, {values[2]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.graphql")
    };

    private ExpressionResult EmitGrpcCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create_server" => EmitCoreFunction(call, operation, arguments, VelaType.GrpcServer, [VelaType.Text, VelaType.Int], values => $"VelaGrpc.CreateServer({values[0]}, {values[1]})"),
        "map" => EmitGrpcMapCallback(call, arguments),
        "start" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GrpcServer], values => $"VelaGrpc.Start({values[0]})"),
        "run" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.GrpcServer], values => $"VelaGrpc.Run({values[0]})"),
        "stop" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.GrpcServer], values => $"VelaGrpc.Stop({values[0]})"),
        "client_call" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Int, VelaType.Text, VelaType.Text], values => $"VelaGrpc.ClientCall({values[0]}, {values[1]}, {values[2]}, {values[3]})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.grpc")
    };

    private ExpressionResult EmitHttpRouteCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod)
    {
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'http.{operationName}' expects 3 argument(s), but received {arguments.Length}.");
        }

        var server = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.HttpServer, "default!");
        var path = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!server.Type.IsSameAs(VelaType.HttpServer) && !server.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects HttpServer for argument 1.");
        }

        if (!path.Type.IsSameAs(VelaType.Text) && !path.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'http.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaHttp.{runtimeMethod}({server.Code}, {path.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGraphqlFieldCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod)
    {
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'graphql.{operationName}' expects 3 argument(s), but received {arguments.Length}.");
        }

        var schema = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GraphqlSchema, "default!");
        var name = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!schema.Type.IsSameAs(VelaType.GraphqlSchema) && !schema.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects GraphqlSchema for argument 1.");
        }

        if (!name.Type.IsSameAs(VelaType.Text) && !name.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'graphql.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGraphql.{runtimeMethod}({schema.Code}, {name.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGrpcMapCallback(CallExpressionSyntax call, ExpressionResult[] arguments)
    {
        var expectedHandler = CreateFunctionType([VelaType.Text], VelaType.Text);
        if (arguments.Length != 3)
        {
            Report("VEL3006", call.Span, $"Core function 'grpc.map' expects 3 argument(s), but received {arguments.Length}.");
        }

        var server = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GrpcServer, "default!");
        var method = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
        var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
        if (!server.Type.IsSameAs(VelaType.GrpcServer) && !server.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, "Core function 'grpc.map' expects GrpcServer for argument 1.");
        }

        if (!method.Type.IsSameAs(VelaType.Text) && !method.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, "Core function 'grpc.map' expects Text for argument 2.");
        }

        if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'grpc.map' expects {expectedHandler}, but received '{handler.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGrpc.Map({server.Code}, {method.Code}, {handler.Code})");
    }

    private ExpressionResult EmitGuiCallback(
        CallExpressionSyntax call,
        ExpressionResult[] arguments,
        string operationName,
        VelaType expectedHandler,
        string runtimeMethod,
        bool formFirst = false)
    {
        if (arguments.Length != 2 && !(formFirst && arguments.Length == 3))
        {
            var expected = formFirst ? 3 : 2;
            Report("VEL3006", call.Span, $"Core function 'gui.{operationName}' expects {expected} argument(s), but received {arguments.Length}.");
        }

        if (formFirst)
        {
            var form = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GuiForm, "default!");
            var path = arguments.Length > 1 ? arguments[1] : new ExpressionResult(VelaType.Text, "\"\"");
            var handler = arguments.Length > 2 ? arguments[2] : new ExpressionResult(expectedHandler, "null!");
            if (!form.Type.IsSameAs(VelaType.GuiForm) && !form.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects GuiForm for argument 1.");
            }

            if (!path.Type.IsSameAs(VelaType.Text) && !path.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects Text for argument 2.");
            }

            if (!handler.Type.IsSameAs(expectedHandler) && !handler.Type.IsUnknown)
            {
                Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects {expectedHandler}, but received '{handler.Type}'.");
            }

            return new ExpressionResult(VelaType.Unit, $"VelaGuiComponents.{runtimeMethod}({form.Code}, {path.Code}, {handler.Code})");
        }

        var control = arguments.Length > 0 ? arguments[0] : new ExpressionResult(VelaType.GuiControl, "default!");
        var callback = arguments.Length > 1 ? arguments[1] : new ExpressionResult(expectedHandler, "null!");
        if (!control.Type.IsSameAs(VelaType.GuiControl) && !control.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects GuiControl for argument 1, but received '{control.Type}'.");
        }

        if (!callback.Type.IsSameAs(expectedHandler) && !callback.Type.IsUnknown)
        {
            Report("VEL3002", call.Span, $"Core function 'gui.{operationName}' expects {expectedHandler}, but received '{callback.Type}'.");
        }

        return new ExpressionResult(VelaType.Unit, $"VelaGuiComponents.{runtimeMethod}({control.Code}, {callback.Code})");
    }

    private ExpressionResult EmitEncodingCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "utf8_byte_count" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [VelaType.Text], values => $"VelaEncoding.Utf8ByteCount({values[0]})"),
        "hex_encode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.HexEncode({values[0]})"),
        "base64_encode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.Base64Encode({values[0]})"),
        "hex_decode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.HexDecode({values[0]}, {SourceLocationCode(call)})"),
        "base64_decode" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text], values => $"VelaEncoding.Base64Decode({values[0]}, {SourceLocationCode(call)})"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.encoding")
    };

    private ExpressionResult EmitEnvironmentCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "get" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Text], values => $"VelaEnvironment.Get({values[0]})"),
        "get_or" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [VelaType.Text, VelaType.Text], values => $"VelaEnvironment.GetOr({values[0]}, {values[1]})"),
        "argument_count" => EmitCoreFunction(call, operation, arguments, VelaType.Int, [], _ => "VelaEnvironment.ArgumentCount()"),
        "argument" => EmitCoreFunction(call, operation, arguments, new VelaType("Option", [VelaType.Text]), [VelaType.Int], values => $"VelaEnvironment.Argument({values[0]})"),
        "current_directory" => EmitCoreFunction(call, operation, arguments, VelaType.Text, [], _ => "VelaEnvironment.CurrentDirectory()"),
        _ => ReportUnknownCoreOperation(operation, "vela.core.env")
    };

    private ExpressionResult EmitConcurrentCall(CallExpressionSyntax call, SyntaxToken operation, ExpressionResult[] arguments) => operation.Text switch
    {
        "create" => EmitCoreFunction(call, operation, arguments, VelaType.Cancellation, [], _ => "VelaCancellation.Create()"),
        "cancel" => EmitCoreFunction(call, operation, arguments, VelaType.Unit, [VelaType.Cancellation], values => $"{values[0]}.Cancel()"),
        "is_cancelled" => EmitCoreFunction(call, operation, arguments, VelaType.Bool, [VelaType.Cancellation], values => $"{values[0]}.IsCancellationRequested"),
        _ => ReportUnknownCoreOperation(operation, "vela.concurrent")
    };

    private ExpressionResult EmitCoreFunction(
        CallExpressionSyntax call,
        SyntaxToken operation,
        ExpressionResult[] arguments,
        VelaType returnType,
        IReadOnlyList<VelaType> parameterTypes,
        Func<string[], string> emit)
    {
        foreach (var argument in call.Arguments.Where(static argument => argument.IsNamed))
        {
            Report("VEL3023", argument.Name!.Span, $"Core function '{operation.Text}' does not expose named arguments.", "Pass these intrinsic arguments positionally; system.exec supports named limits.");
        }

        if (arguments.Length != parameterTypes.Count)
        {
            Report("VEL3006", call.Span, $"Core function '{operation.Text}' expects {parameterTypes.Count} argument(s), but received {arguments.Length}.");
        }

        var values = new string[parameterTypes.Count];
        for (var index = 0; index < parameterTypes.Count; index++)
        {
            var argument = index < arguments.Length ? arguments[index] : new ExpressionResult(parameterTypes[index], DefaultValue(parameterTypes[index]));
            EnsureAssignable(parameterTypes[index], argument.Type, index < call.Arguments.Count ? call.Arguments[index].Span : call.Span);
            values[index] = CoerceCode(parameterTypes[index], argument, call);
        }

        return new ExpressionResult(returnType, emit(values));
    }

    private string[] BindNamedCoreArguments(
        CallExpressionSyntax call,
        SyntaxToken operation,
        ExpressionResult[] suppliedValues,
        IReadOnlyList<CoreParameter> parameters)
    {
        var values = new ExpressionResult?[parameters.Count];
        var sources = new SyntaxNode?[parameters.Count];
        var nextPositional = 0;
        for (var sourceIndex = 0; sourceIndex < call.Arguments.Count; sourceIndex++)
        {
            var argument = call.Arguments[sourceIndex];
            var parameterIndex = argument.Name is null
                ? nextPositional++
                : parameters.Select(static (parameter, index) => (parameter, index))
                    .Where(pair => string.Equals(pair.parameter.Name, argument.Name.Text, StringComparison.Ordinal))
                    .Select(static pair => pair.index)
                    .DefaultIfEmpty(-1)
                    .First();
            if (parameterIndex < 0 || parameterIndex >= parameters.Count)
            {
                Report("VEL3023", argument.Span, argument.Name is null
                    ? $"Core function '{operation.Text}' received too many arguments."
                    : $"Core function '{operation.Text}' has no parameter named '{argument.Name.Text}'.");
                continue;
            }

            if (values[parameterIndex] is not null)
            {
                Report("VEL3023", argument.Span, $"Core parameter '{parameters[parameterIndex].Name}' is supplied more than once.", "Pass each parameter only once.");
                continue;
            }

            values[parameterIndex] = sourceIndex < suppliedValues.Length ? suppliedValues[sourceIndex] : new ExpressionResult(VelaType.Unknown, "default");
            sources[parameterIndex] = argument.Expression;
        }

        var codes = new string[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (values[index] is null)
            {
                if (parameter.DefaultCode is null)
                {
                    Report("VEL3023", call.Span, $"Required parameter '{parameter.Name}' is missing for core function '{operation.Text}'.", "Supply every required process argument.");
                    values[index] = new ExpressionResult(parameter.Type, DefaultValue(parameter.Type));
                }
                else
                {
                    values[index] = new ExpressionResult(parameter.Type, parameter.DefaultCode);
                }

                sources[index] = call;
            }

            EnsureAssignable(parameter.Type, values[index]!.Type, sources[index]!.Span);
            codes[index] = CoerceCode(parameter.Type, values[index]!, sources[index]!);
        }

        return codes;
    }

    private ExpressionResult ReportUnknownCoreOperation(SyntaxToken operation, string module)
    {
        Report("VEL3005", operation.Span, $"Core module '{module}' does not export '{operation.Text}'.", "Use a documented core module operation.");
        return new ExpressionResult(VelaType.Unknown, "default");
    }
}
