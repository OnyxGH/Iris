namespace PiSharp.CodingAgent.Utils;

public sealed record GitPathsInfo(string RepoDir, string CommonGitDir, string HeadPath);

/// <summary>Git repository discovery without spawning git. Port of findGitPaths from core/footer-data-provider.ts.</summary>
public static class GitPaths
{
    public static GitPathsInfo? Find(string cwd)
    {
        var dir = cwd;
        while (true)
        {
            var gitPath = Path.Combine(dir, ".git");
            try
            {
                if (File.Exists(gitPath))
                {
                    var content = File.ReadAllText(gitPath).Trim();
                    if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        var gitDir = Path.GetFullPath(Path.Combine(dir, content[8..].Trim()));
                        var headPath = Path.Combine(gitDir, "HEAD");
                        if (!File.Exists(headPath)) return null;
                        var commonDirPath = Path.Combine(gitDir, "commondir");
                        var commonGitDir = File.Exists(commonDirPath)
                            ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirPath).Trim()))
                            : gitDir;
                        return new GitPathsInfo(dir, commonGitDir, headPath);
                    }
                }
                else if (Directory.Exists(gitPath))
                {
                    var headPath = Path.Combine(gitPath, "HEAD");
                    if (!File.Exists(headPath)) return null;
                    return new GitPathsInfo(dir, gitPath, headPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) return null;
            dir = parent;
        }
    }
}
