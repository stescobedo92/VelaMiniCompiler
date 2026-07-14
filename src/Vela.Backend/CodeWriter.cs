using System.Text;

namespace Vela.Backend;

internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indentation;

    public void WriteLine(string? value = null)
    {
        if (value is not null)
        {
            _builder.Append(' ', _indentation * 4);
            _builder.Append(value);
        }

        _builder.AppendLine();
    }

    public void Indent() => _indentation++;

    public void Unindent()
    {
        if (_indentation == 0)
        {
            throw new InvalidOperationException("Cannot unindent a writer that is already at the root level.");
        }

        _indentation--;
    }

    public override string ToString() => _builder.ToString();
}
