using GitHub.Copilot;
using Hark.Core;
using Xunit;

namespace Hark.Core.Tests;

public class McpConfigTests
{
    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hark-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Reads_vscode_format_with_comments_and_trailing_commas()
    {
        var path = Write("""
            {
              // VS Code allows comments here
              "servers": {
                "local": {
                  "type": "stdio",
                  "command": "dotnet",
                  "args": ["run", "--project", "C:/x/y.csproj", "--no-launch-profile"],
                  "env": { "ADO_PAT": "secret-value-1", "ADO_PROJECT": "Demo" },
                },
                "remote": { "type": "http", "url": "https://example.test/mcp", "headers": { "Authorization": "Bearer abcdef123" } },
              }
            }
            """);

        var loaded = McpConfig.Load(path);

        Assert.Equal(2, loaded.Servers.Count);
        var local = Assert.IsType<McpStdioServerConfig>(loaded.Servers["local"]);
        Assert.Equal("dotnet", local.Command);
        Assert.Equal(4, local.Args!.Count);
        Assert.Equal("Demo", local.Env!["ADO_PROJECT"]);
        var remote = Assert.IsType<McpHttpServerConfig>(loaded.Servers["remote"]);
        Assert.Equal("https://example.test/mcp", remote.Url);
        Assert.Contains("secret-value-1", loaded.Secrets);
        Assert.Contains("Bearer abcdef123", loaded.Secrets);
        Assert.DoesNotContain("Demo", loaded.Secrets);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void Warns_about_vscode_only_inputs_and_bad_entries()
    {
        var path = Write("""
            { "servers": {
                "needs-input": { "command": "npx", "args": ["-y", "pkg"], "env": { "TOKEN": "${input:token}" } },
                "broken": { "type": "stdio" } } }
            """);

        var loaded = McpConfig.Load(path);

        Assert.Single(loaded.Servers);
        Assert.Equal(2, loaded.Warnings.Count);
    }
}

public class ChatStoreTests
{
    [Fact]
    public void Saves_lists_searches_and_deletes_conversations()
    {
        var file = Path.Combine(Path.GetTempPath(), $"hark-test-{Guid.NewGuid():N}.db");
        var store = new ChatStore(file);

        var first = store.CreateConversation("Sprint status", "auto");
        store.AddMessage(first, "user", "What is open this sprint?");
        store.AddMessage(first, "assistant", "Three items are open.");
        store.SetSession(first, "session-1", "auto");

        var second = store.CreateConversation("Lineage question", null);
        store.AddMessage(second, "user", "Where does customer_orders come from?");

        var all = store.ListConversations();
        Assert.Equal(2, all.Count);
        Assert.Equal("Lineage question", all[0].Title); // most recently updated first

        var found = Assert.Single(store.ListConversations("customer_orders"));
        Assert.Equal(second, found.Id);

        var saved = all.Single(c => c.Id == first);
        Assert.Equal("session-1", saved.SessionId);
        Assert.Equal("Three items are open.", saved.Preview);
        Assert.Equal(["user", "assistant"], store.GetMessages(first).Select(m => m.Kind));

        store.DeleteConversation(first);
        Assert.Single(store.ListConversations());
        Assert.Empty(store.GetMessages(first));
    }
}

public class LogTests
{
    [Fact]
    public void Masks_registered_secrets()
    {
        Log.AddSecrets(["github_pat_ABCDEFG123"]);
        Assert.Equal("token=***", Log.Mask("token=github_pat_ABCDEFG123"));
    }
}

public class RepoPolicyTests
{
    [Theory]
    [InlineData("git commit -m \"x\"", true)]
    [InlineData("git push origin feature/x", true)]
    [InlineData("cd repo; git add -A; git commit -am wip", true)]
    [InlineData("git -C C:\\src\\repo push", true)]
    [InlineData("GIT.EXE Push --force", true)]
    [InlineData("git status", false)]
    [InlineData("git pull origin master", false)]
    [InlineData("git checkout -b fix/commit-message-parser", false)]
    [InlineData("git log --oneline | Select-String push", false)]
    [InlineData("", false)]
    public void Detects_commit_and_push(string command, bool expected) =>
        Assert.Equal(expected, RepoPolicy.IsCommitOrPush(command));

    [Fact]
    public void Knows_which_paths_are_inside_the_repos_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "hark-repos");
        Assert.True(RepoPolicy.IsInside(root, Path.Combine(root, "one", "src", "a.cs")));
        Assert.True(RepoPolicy.IsInside(root, root));
        Assert.False(RepoPolicy.IsInside(root, Path.Combine(root, "..", "elsewhere", "a.cs")));
        Assert.False(RepoPolicy.IsInside(root, root + "-other"));
        Assert.False(RepoPolicy.IsInside(null, root));
    }
}
