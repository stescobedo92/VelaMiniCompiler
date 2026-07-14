namespace Vela.Backend;

/// <summary>Controls how a generated Vela application is packaged.</summary>
public enum ExecutableMode
{
    NativeAot,
    SingleFile,
    FrameworkDependent
}

/// <summary>Controls whether a Vela compilation is published as an application or shared library.</summary>
public enum VelaArtifactKind
{
    /// <summary>Produces a host executable.</summary>
    Application,

    /// <summary>Produces a Native AOT shared library.</summary>
    SharedLibrary
}

/// <summary>Options used to turn generated C# into an executable artifact.</summary>
public sealed record BuildOptions(
    string ApplicationName,
    string OutputDirectory,
    string RuntimeIdentifier = BuildTargetResolver.Auto,
    ExecutableMode Mode = ExecutableMode.NativeAot,
    VelaArtifactKind ArtifactKind = VelaArtifactKind.Application);

/// <summary>Describes the files and command output produced by a Vela build.</summary>
public sealed record BuildResult(
    bool Succeeded,
    string RuntimeIdentifier,
    string? ExecutablePath,
    string StandardOutput,
    string StandardError,
    string? LibraryPath = null)
{
    /// <summary>Gets the executable or shared library produced by a successful build.</summary>
    public string? PrimaryArtifactPath => ExecutablePath ?? LibraryPath;
}

/// <summary>Contains a shared-library build result and the manifest written beside it.</summary>
public sealed record VelaLibraryBuildResult(BuildResult Build, VelaAbiManifest? Manifest);
