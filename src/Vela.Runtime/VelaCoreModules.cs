using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vela.Runtime;

#pragma warning disable CS1591 // The overload families are compiler intrinsics documented by Vela's core-module contract.

/// <summary>Provides JSON operations for the explicitly imported Vela JSON module.</summary>
public static class VelaJson
{
    /// <summary>Returns whether the input is a complete JSON value.</summary>
    public static bool IsValid(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns canonical compact JSON or raises a source-aware format error.</summary>
    public static string Compact(string value, string sourceLocation) => Write(value, indented: false, sourceLocation);

    /// <summary>Returns indented JSON or raises a source-aware format error.</summary>
    public static string Pretty(string value, string sourceLocation) => Write(value, indented: true, sourceLocation);

    /// <summary>Returns a string property from a JSON object when present and string-valued.</summary>
    public static Option<string> TryGetText(string value, string property, string sourceLocation)
    {
        using var document = Parse(value, sourceLocation);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            ? Option.Some(element.GetString() ?? string.Empty)
            : Option.None<string>();
    }

    /// <summary>Returns an Int property from a JSON object when present and representable.</summary>
    public static Option<int> TryGetInt(string value, string property, string sourceLocation)
    {
        using var document = Parse(value, sourceLocation);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out var element)
            && element.TryGetInt32(out var number)
            ? Option.Some(number)
            : Option.None<int>();
    }

    /// <summary>Returns a Boolean property from a JSON object when present and Boolean-valued.</summary>
    public static Option<bool> TryGetBool(string value, string property, string sourceLocation)
    {
        using var document = Parse(value, sourceLocation);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? Option.Some(element.GetBoolean())
            : Option.None<bool>();
    }

    private static JsonDocument Parse(string value, string sourceLocation)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new VelaFormatException("Invalid JSON input.", sourceLocation, exception);
        }
    }

    private static string Write(string value, bool indented, string sourceLocation)
    {
        using var document = Parse(value, sourceLocation);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented });
        document.RootElement.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>Provides cryptographic hashes and comparisons for explicitly imported Vela code.</summary>
public static class VelaCrypto
{
    /// <summary>Computes a lower-case hexadecimal SHA-256 digest of UTF-8 text.</summary>
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Computes a lower-case hexadecimal HMAC-SHA256 digest of UTF-8 text.</summary>
    public static string HmacSha256(string key, string value) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Compares UTF-8 text in fixed time when both encoded inputs have equal length.</summary>
    public static bool ConstantTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    /// <summary>Creates a lower-case hexadecimal value from cryptographically secure random bytes.</summary>
    public static string RandomHex(int byteCount, string sourceLocation)
    {
        if (byteCount is < 1 or > 1_048_576)
        {
            throw new VelaFormatException("Crypto random byte count must be between 1 and 1048576.", sourceLocation);
        }

        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }
}

/// <summary>Provides ordinal, allocation-conscious text operations.</summary>
public static class VelaTextOperations
{
    /// <summary>Gets the UTF-16 code-unit length of a text value.</summary>
    public static int Length(string value) => value.Length;
    /// <summary>Returns whether text contains a value using ordinal comparison.</summary>
    public static bool Contains(string value, string search) => value.Contains(search, StringComparison.Ordinal);
    /// <summary>Returns whether text starts with a value using ordinal comparison.</summary>
    public static bool StartsWith(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal);
    /// <summary>Returns whether text ends with a value using ordinal comparison.</summary>
    public static bool EndsWith(string value, string suffix) => value.EndsWith(suffix, StringComparison.Ordinal);
    /// <summary>Trims Unicode whitespace from both text ends.</summary>
    public static string Trim(string value) => value.Trim();
    /// <summary>Applies invariant upper casing.</summary>
    public static string ToUpperInvariant(string value) => value.ToUpperInvariant();
    /// <summary>Applies invariant lower casing.</summary>
    public static string ToLowerInvariant(string value) => value.ToLowerInvariant();

    /// <summary>Returns a source-aware text slice.</summary>
    public static string Slice(string value, int start, int length, string sourceLocation)
    {
        if (start < 0 || length < 0 || start > value.Length - length)
        {
            throw new VelaIndexOutOfRangeException(start, value.Length, sourceLocation);
        }

        return value.Substring(start, length);
    }
}

/// <summary>Provides direct numeric helpers without boxing.</summary>
public static class VelaMath
{
    public static int Abs(int value, string location) => value < 0 ? VelaNumeric.Negate(value, location) : value;
    public static uint Abs(uint value) => value;
    public static long Abs(long value, string location) => value < 0 ? VelaNumeric.Negate(value, location) : value;
    public static float Abs(float value) => MathF.Abs(value);
    public static double Abs(double value) => Math.Abs(value);
    public static decimal Abs(decimal value) => decimal.Abs(value);
    public static int Min(int left, int right) => Math.Min(left, right);
    public static uint Min(uint left, uint right) => Math.Min(left, right);
    public static long Min(long left, long right) => Math.Min(left, right);
    public static float Min(float left, float right) => MathF.Min(left, right);
    public static double Min(double left, double right) => Math.Min(left, right);
    public static decimal Min(decimal left, decimal right) => Math.Min(left, right);
    public static int Max(int left, int right) => Math.Max(left, right);
    public static uint Max(uint left, uint right) => Math.Max(left, right);
    public static long Max(long left, long right) => Math.Max(left, right);
    public static float Max(float left, float right) => MathF.Max(left, right);
    public static double Max(double left, double right) => Math.Max(left, right);
    public static decimal Max(decimal left, decimal right) => Math.Max(left, right);
    public static int Clamp(int value, int minimum, int maximum) => Math.Clamp(value, minimum, maximum);
    public static uint Clamp(uint value, uint minimum, uint maximum) => Math.Clamp(value, minimum, maximum);
    public static long Clamp(long value, long minimum, long maximum) => Math.Clamp(value, minimum, maximum);
    public static float Clamp(float value, float minimum, float maximum) => Math.Clamp(value, minimum, maximum);
    public static double Clamp(double value, double minimum, double maximum) => Math.Clamp(value, minimum, maximum);
    public static decimal Clamp(decimal value, decimal minimum, decimal maximum) => Math.Clamp(value, minimum, maximum);
    public static double Sqrt(double value, string location) => value < 0d ? throw new VelaArithmeticException("square root of a negative value", location) : Math.Sqrt(value);
    public static double Pow(double left, double right, string location)
    {
        var result = Math.Pow(left, right);
        return double.IsFinite(result) ? result : throw new VelaOverflowException("Double exponentiation", location);
    }
}

/// <summary>Provides wall-clock and monotonic timing primitives.</summary>
public static class VelaTime
{
    public static long UtcUnixMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static long MonotonicTicks() => Stopwatch.GetTimestamp();
    public static long ElapsedMilliseconds(long startTicks, long endTicks, string sourceLocation)
    {
        try
        {
            return checked((endTicks - startTicks) * 1000L / Stopwatch.Frequency);
        }
        catch (OverflowException exception)
        {
            throw new VelaOverflowException("elapsed monotonic time", sourceLocation, exception);
        }
    }
}

/// <summary>Provides process-shared fast pseudo-random values.</summary>
public static class VelaRandom
{
    public static int NextInt(int minimum, int maximum, string sourceLocation)
    {
        if (minimum >= maximum)
        {
            throw new VelaFormatException("Random minimum must be smaller than maximum.", sourceLocation);
        }

        return Random.Shared.Next(minimum, maximum);
    }

    public static double NextDouble() => Random.Shared.NextDouble();
}

/// <summary>Provides UTF-8 text file operations with Vela-specific failures.</summary>
public static class VelaIo
{
    public static bool Exists(string path) => File.Exists(path);
    public static string ReadText(string path, string sourceLocation) => Execute(() => File.ReadAllText(path, Encoding.UTF8), sourceLocation);
    public static void WriteText(string path, string value, string sourceLocation) => Execute(() => File.WriteAllText(path, value, Encoding.UTF8), sourceLocation);
    public static void AppendText(string path, string value, string sourceLocation) => Execute(() => File.AppendAllText(path, value, Encoding.UTF8), sourceLocation);

    private static T Execute<T>(Func<T> operation, string sourceLocation)
    {
        try
        {
            return operation();
        }
        catch (IOException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
        catch (ArgumentException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
    }

    private static void Execute(Action operation, string sourceLocation)
    {
        try
        {
            operation();
        }
        catch (IOException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
        catch (ArgumentException exception)
        {
            throw new VelaIoException(exception.Message, sourceLocation, exception);
        }
    }
}

/// <summary>Provides explicit text encoding utilities.</summary>
public static class VelaEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
    public static string HexEncode(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
    public static string Base64Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    public static string HexDecode(string value, string sourceLocation) => Decode(() => Convert.FromHexString(value), sourceLocation);
    public static string Base64Decode(string value, string sourceLocation) => Decode(() => Convert.FromBase64String(value), sourceLocation);

    private static string Decode(Func<byte[]> decode, string sourceLocation)
    {
        try
        {
            return StrictUtf8.GetString(decode());
        }
        catch (FormatException exception)
        {
            throw new VelaFormatException("Invalid encoded text.", sourceLocation, exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new VelaFormatException("Encoded bytes are not valid UTF-8 text.", sourceLocation, exception);
        }
    }
}

/// <summary>Exposes process configuration explicitly to generated Vela programs.</summary>
public static class VelaEnvironment
{
    private static string[] _arguments = [];

    /// <summary>Stores the process arguments before user code executes.</summary>
    public static void Initialize(string[] arguments) => _arguments = arguments ?? [];
    public static Option<string> Get(string name) => Environment.GetEnvironmentVariable(name) is { } value ? Option.Some(value) : Option.None<string>();
    public static string GetOr(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
    public static int ArgumentCount() => _arguments.Length;
    public static Option<string> Argument(int index) => (uint)index < (uint)_arguments.Length ? Option.Some(_arguments[index]) : Option.None<string>();
    public static string CurrentDirectory() => Environment.CurrentDirectory;
}

/// <summary>Owns a synchronous TCP connection exposed through the Vela TCP module.</summary>
public sealed class TcpConnection : IDisposable
{
    private readonly TcpClient _client;
    private bool _disposed;

    private TcpConnection(TcpClient client) => _client = client;

    /// <summary>Connects with a bounded timeout.</summary>
    public static TcpConnection Connect(string host, int port, int timeoutMilliseconds, string sourceLocation)
    {
        if (port is < 1 or > 65535 || timeoutMilliseconds is < 1 or > 120_000)
        {
            throw new VelaNetworkException("TCP port or timeout is outside the supported range.", sourceLocation);
        }

        var client = new TcpClient();
        try
        {
            using var cancellation = new CancellationTokenSource(timeoutMilliseconds);
            client.ConnectAsync(host, port, cancellation.Token).AsTask().GetAwaiter().GetResult();
            client.ReceiveTimeout = timeoutMilliseconds;
            client.SendTimeout = timeoutMilliseconds;
            return new TcpConnection(client);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            client.Dispose();
            throw new VelaNetworkException("TCP connection failed.", sourceLocation, exception);
        }
    }

    /// <summary>Sends UTF-8 text synchronously.</summary>
    public void SendText(string value, string sourceLocation)
    {
        try
        {
            ThrowIfDisposed(sourceLocation);
            var bytes = Encoding.UTF8.GetBytes(value);
            _client.GetStream().Write(bytes);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            throw new VelaNetworkException("TCP send failed.", sourceLocation, exception);
        }
    }

    /// <summary>Reads one bounded UTF-8 chunk synchronously.</summary>
    public string ReceiveText(int maximumBytes, string sourceLocation)
    {
        if (maximumBytes is < 1 or > 1_048_576)
        {
            throw new VelaNetworkException("TCP receive size must be between 1 and 1048576 bytes.", sourceLocation);
        }

        try
        {
            ThrowIfDisposed(sourceLocation);
            var buffer = GC.AllocateUninitializedArray<byte>(maximumBytes);
            var count = _client.GetStream().Read(buffer, 0, buffer.Length);
            return new UTF8Encoding(false, true).GetString(buffer, 0, count);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or DecoderFallbackException)
        {
            throw new VelaNetworkException("TCP receive failed.", sourceLocation, exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private void ThrowIfDisposed(string sourceLocation)
    {
        if (_disposed)
        {
            throw new VelaNetworkException("TCP connection is closed.", sourceLocation);
        }
    }
}

#pragma warning restore CS1591
