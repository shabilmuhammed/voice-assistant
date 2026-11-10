namespace Hark.Core;

/// <summary>Where Copilot may work on this PC, and the standing instructions that go with it.</summary>
public static class RepoPolicy
{
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

    /// <summary>Standing instructions given to Copilot at the start of every chat.</summary>
    public static string Instructions(string? reposRoot, string outputFolder)
    {
        var text = $"""
            <generated_files>
            When the user asks you to generate, create, export or save a file that is not part of a repository
            (a report, a CSV, a script, notes, a document), save it in {outputFolder}.
            Use a short descriptive file name, do not overwrite an existing file unless asked, and finish by telling the user
            the full path of every file you saved there.
            </generated_files>
            """;

        if (string.IsNullOrWhiteSpace(reposRoot)) return text;

        return text + Environment.NewLine + $"""
            <repository_work>
            The user's git repositories are cloned under {reposRoot}. Each subfolder there is one repository.
            When the user asks you to start or do work on a repository:
            1. List the folders under {reposRoot} and pick the one that matches. Names may have been spoken aloud, so match loosely
               (for example "cdp tax data orchestrator" means "cdp-taxdata-orchestrator"). If two folders are equally likely, ask which.
            2. Work only inside that repository folder.
            3. Run git status first. If there are uncommitted changes, stop and ask the user what to do with them.
            4. Switch to the default branch (master or main), pull the latest, then create a new branch with a short descriptive name.
            5. Make the changes and save the files, then tell the user which branch you created and which files you changed.
            Commit and push only when the user asks you to. When they do, commit on the working branch with a clear message and
            push that branch, so a pull request can be created from it. Never commit directly to the default branch and never force-push.
            </repository_work>
            """;
    }
}
