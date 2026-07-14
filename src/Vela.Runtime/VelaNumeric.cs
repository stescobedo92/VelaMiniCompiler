using System.Numerics;

namespace Vela.Runtime;

#pragma warning disable CS1591 // Numeric overloads are compiler intrinsics documented by the Vela language contract.

/// <summary>Implements the checked numeric semantics used by generated Vela code.</summary>
public static class VelaNumeric
{
    public static int Add(int left, int right, string location) => Checked("Int addition", location, () => checked(left + right));
    public static int Subtract(int left, int right, string location) => Checked("Int subtraction", location, () => checked(left - right));
    public static int Multiply(int left, int right, string location) => Checked("Int multiplication", location, () => checked(left * right));
    public static int Divide(int left, int right, string location) => DivideCore(left, right, location, "Int division", static (a, b) => checked(a / b));
    public static int Negate(int value, string location) => Checked("Int negation", location, () => checked(-value));

    public static uint Add(uint left, uint right, string location) => Checked("UInt addition", location, () => checked(left + right));
    public static uint Subtract(uint left, uint right, string location) => Checked("UInt subtraction", location, () => checked(left - right));
    public static uint Multiply(uint left, uint right, string location) => Checked("UInt multiplication", location, () => checked(left * right));
    public static uint Divide(uint left, uint right, string location) => DivideCore(left, right, location, "UInt division", static (a, b) => checked(a / b));

    public static long Add(long left, long right, string location) => Checked("Long addition", location, () => checked(left + right));
    public static long Subtract(long left, long right, string location) => Checked("Long subtraction", location, () => checked(left - right));
    public static long Multiply(long left, long right, string location) => Checked("Long multiplication", location, () => checked(left * right));
    public static long Divide(long left, long right, string location) => DivideCore(left, right, location, "Long division", static (a, b) => checked(a / b));
    public static long Negate(long value, string location) => Checked("Long negation", location, () => checked(-value));

    public static decimal Add(decimal left, decimal right, string location) => Checked("Decimal addition", location, () => left + right);
    public static decimal Subtract(decimal left, decimal right, string location) => Checked("Decimal subtraction", location, () => left - right);
    public static decimal Multiply(decimal left, decimal right, string location) => Checked("Decimal multiplication", location, () => left * right);
    public static decimal Divide(decimal left, decimal right, string location) => DivideCore(left, right, location, "Decimal division", static (a, b) => a / b);
    public static decimal Negate(decimal value, string location) => Checked("Decimal negation", location, () => decimal.Negate(value));

    public static float Add(float left, float right, string location) => Finite(left + right, "Float addition", location);
    public static float Subtract(float left, float right, string location) => Finite(left - right, "Float subtraction", location);
    public static float Multiply(float left, float right, string location) => Finite(left * right, "Float multiplication", location);
    public static float Divide(float left, float right, string location) => right == 0f ? throw new VelaArithmeticException("Float division by zero", location) : Finite(left / right, "Float division", location);
    public static float Negate(float value, string location) => Finite(-value, "Float negation", location);

    public static double Add(double left, double right, string location) => Finite(left + right, "Double addition", location);
    public static double Subtract(double left, double right, string location) => Finite(left - right, "Double subtraction", location);
    public static double Multiply(double left, double right, string location) => Finite(left * right, "Double multiplication", location);
    public static double Divide(double left, double right, string location) => right == 0d ? throw new VelaArithmeticException("Double division by zero", location) : Finite(left / right, "Double division", location);
    public static double Negate(double value, string location) => Finite(-value, "Double negation", location);

    /// <summary>Converts a numeric value to <see cref="int"/> with overflow detection.</summary>
    public static int ToInt<T>(T value, string location)
        where T : INumberBase<T> => Checked("conversion to Int", location, () => int.CreateChecked(value));

    /// <summary>Converts a numeric value to <see cref="uint"/> with overflow detection.</summary>
    public static uint ToUInt<T>(T value, string location)
        where T : INumberBase<T> => Checked("conversion to UInt", location, () => uint.CreateChecked(value));

    /// <summary>Converts a numeric value to <see cref="long"/> with overflow detection.</summary>
    public static long ToLong<T>(T value, string location)
        where T : INumberBase<T> => Checked("conversion to Long", location, () => long.CreateChecked(value));

    /// <summary>Converts a numeric value to a finite <see cref="float"/>.</summary>
    public static float ToFloat<T>(T value, string location)
        where T : INumberBase<T> => Finite(float.CreateChecked(value), "conversion to Float", location);

    /// <summary>Converts a numeric value to a finite <see cref="double"/>.</summary>
    public static double ToDouble<T>(T value, string location)
        where T : INumberBase<T> => Finite(double.CreateChecked(value), "conversion to Double", location);

    /// <summary>Converts a numeric value to <see cref="decimal"/> with overflow detection.</summary>
    public static decimal ToDecimal<T>(T value, string location)
        where T : INumberBase<T> => Checked("conversion to Decimal", location, () => decimal.CreateChecked(value));

    private static T Checked<T>(string operation, string location, Func<T> operationFactory)
    {
        try
        {
            return operationFactory();
        }
        catch (OverflowException exception)
        {
            throw new VelaOverflowException(operation, location, exception);
        }
    }

    private static T DivideCore<T>(T left, T right, string location, string operation, Func<T, T, T> division)
        where T : INumberBase<T>
    {
        if (T.IsZero(right))
        {
            throw new VelaArithmeticException(operation + " by zero", location);
        }

        return Checked(operation, location, () => division(left, right));
    }

    private static float Finite(float value, string operation, string location) => float.IsFinite(value)
        ? value
        : throw new VelaOverflowException(operation, location);

    private static double Finite(double value, string operation, string location) => double.IsFinite(value)
        ? value
        : throw new VelaOverflowException(operation, location);
}

#pragma warning restore CS1591
