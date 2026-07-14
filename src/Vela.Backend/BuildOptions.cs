namespace Vela.Backend;

/// <summary>Controls how a generated Vela application is packaged.</summary>
public enum ExecutableMode
{
    NativeAot,
    SingleFile,
    FrameworkDependent
}

/// <summary>Options used to turn generated C# into an executable artifact.</summary>
public sealed record BuildOptions(
    string ApplicationName,
    string OutputDirectory,
    string RuntimeIdentifier = "win-x64",
    ExecutableMode Mode = ExecutableMode.NativeAot);

/// <summary>Describes the files and command output produced by a Vela build.</summary>
public sealed record BuildResult(
    bool Succeeded,
    string SourceDirectory,
    string ProjectPath,
    string PublishDirectory,
    string StandardOutput,
    string StandardError);
