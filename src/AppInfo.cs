namespace Otter;

using System.Reflection;

/// <summary>
/// App-wide identity: the running version and the GitHub locations the UI links to. Centralised so
/// the settings window (and, later, the updater) all agree on one source.
/// </summary>
internal static class AppInfo
{
    public const string RepoUrl = "https://github.com/ArcticGizmo/otter";

    public static string IssuesUrl => string.IsNullOrEmpty(RepoUrl) ? "" : RepoUrl + "/issues/new";

    /// <summary>Human-readable version (e.g. "0.1.0"), read from the assembly's informational
    /// version with any "+commit" git metadata stripped; falls back to the numeric version.</summary>
    public static string Version { get; } = ResolveVersion();

    static string ResolveVersion()
    {
        var asm  = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus < 0 ? info : info[..plus];
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
