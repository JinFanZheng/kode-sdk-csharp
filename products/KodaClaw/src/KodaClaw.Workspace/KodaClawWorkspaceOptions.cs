using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace;

public sealed class KodaClawWorkspaceOptions
{
    public string? RootPath { get; set; }

    public string ResolveRootPath() => ResolveRootPathStatic(RootPath);

    public static string ResolveRootPathStatic(string? rootPath)
    {
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            return Path.GetFullPath(rootPath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, KodaClawWorkspaceLayout.RootDirectoryName);
    }
}
