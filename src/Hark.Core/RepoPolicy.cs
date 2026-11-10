using System.Text.RegularExpressions;

namespace Hark.Core;

/// <summary>Rules for work inside the user's repositories: edit and save, never commit or push.</summary>
public static partial class RepoPolicy
{
    public const string CommitOrPushFeedback =
        "Committing and pushing are switched off in Hark. Leave the changes saved in the working tree and tell the user they are ready to review.";

    [GeneratedRegex(@"\bgit(\.exe)?\b[^|;&\r\n]*?\s(commit|push)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommitOrPush();

    /// <summary>True for any shell command line that runs git commit or git push.</summary>
    public static bool IsCommitOrPush(string? command) =>
        !string.IsNullOrWhiteSpace(command) && CommitOrPush().IsMatch(command);

    /// <summary>True when <paramref name="path"/> is the root folder or somewhere beneath it.</summary>
    public static bool IsInside(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path, fullRoot);
            return (fullPath + Path.DirectorySeparatorChar).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Standing instructions given to Copilot when a repos folder is configured.</summary>
    public static string Instructions(string reposRoot) => $"""
        <repository_work>
        The user's git repositories are cloned under {reposRoot}. Each subfolder there is one repository.
        When the user asks you to start or do work on a repository:
        1. List the folders under {reposRoot} and pick the one that matches. Names may have been spoken aloud, so match loosely
           (for example "cdp tax data orchestrator" means "cdp-taxdata-orchestrator"). If two folders are equally likely, ask which.
        2. Work only inside that repository folder.
        3. Run git status first. If there are uncommitted changes, stop and ask the user what to do with them.
        4. Switch to the default branch (master or main), pull the latest, then create a new branch with a short descriptive name.
        5. Make the changes and save the files.
        Never run git commit or git push, and do not open pull requests. Leave every change saved but uncommitted,
        then tell the user which branch you created and which files you changed.
        </repository_work>
        """;
}
