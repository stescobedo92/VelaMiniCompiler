namespace Vela.Core.Source;

/// <summary>Describes a one-based source location suitable for diagnostics.</summary>
public readonly record struct TextLocation(string FilePath, int Line, int Column);
