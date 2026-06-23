namespace CodingAgentRunner.Abstractions;

/// <summary>
/// Tells the runner where the user home and a temp root live, so clean-context
/// homes and caches are placed without the library hard-coding application paths.
/// </summary>
public interface IUserHomeProvider
{
    /// <summary>The user's home directory (where a CLI keeps its real config).</summary>
    string GetUserHome();

    /// <summary>A writable root for per-run temp homes and caches.</summary>
    string GetTempRoot();
}

/// <summary>Default provider: <c>USERPROFILE</c>/<c>HOME</c> and the system temp path.</summary>
internal sealed class DefaultUserHomeProvider : IUserHomeProvider
{
    /// <inheritdoc />
    public string GetUserHome()
        => Environment.GetEnvironmentVariable("USERPROFILE")
           ?? Environment.GetEnvironmentVariable("HOME")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    public string GetTempRoot() => Path.GetTempPath();
}
