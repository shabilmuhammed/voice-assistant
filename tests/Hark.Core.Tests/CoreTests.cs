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

    [Fact]
    public void Instructions_cover_generated_files_and_repository_work()
    {
        var withRepos = RepoPolicy.Instructions(@"C:\src\repos", @"C:\out");
        Assert.Contains(@"C:\out", withRepos);
        Assert.Contains(@"C:\src\repos", withRepos);
        Assert.Contains("only when the user asks", withRepos);

        var withoutRepos = RepoPolicy.Instructions(null, @"C:\out");
        Assert.Contains(@"C:\out", withoutRepos);
        Assert.DoesNotContain("repository_work", withoutRepos);
    }
}

public class SkillTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hark-skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData("release-notes", true)]
    [InlineData("sonarqube-coverage-boost", true)]
    [InlineData("a1", true)]
    [InlineData("Release-Notes", false)]
    [InlineData("release notes", false)]
    [InlineData("release--notes", false)]
    [InlineData("-release", false)]
    [InlineData("", false)]
    public void Validates_names(string name, bool ok) => Assert.Equal(ok, SkillFile.CheckName(name) is null);

    [Fact]
    public void Suggests_a_name_from_free_text() => Assert.Equal("release-notes-v2", SkillFile.Slug("  Release Notes (v2)! "));

    [Fact]
    public void Reads_plain_quoted_and_block_front_matter()
    {
        var text = "---\r\nname: coverage-boost\r\ndescription: >\r\n  Raises unit test coverage\r\n  for a .NET project.\r\nargument-hint: '<project>'\r\nallowed-tools: shell\r\n---\r\n\r\n# Steps\r\n1. Run tests.\r\n";
        var parsed = SkillFile.Parse(text);
        Assert.Equal("coverage-boost", parsed.Fields["name"]);
        Assert.Equal("Raises unit test coverage for a .NET project.", parsed.Fields["description"]);
        Assert.Equal("<project>", parsed.Fields["argument-hint"]);
        Assert.Equal("shell", parsed.Fields["allowed-tools"]);
        Assert.Equal("# Steps\n1. Run tests.", parsed.Body);
    }

    [Fact]
    public void Creates_lists_and_updates_a_skill_keeping_other_front_matter()
    {
        var root = TempRoot();
        var file = SkillLibrary.Save(new SkillDraft("release-notes", "Writes release notes: short, \"plain\" English.", "<version>", "# Release notes\nList changes."), null, root);

        var listed = Assert.Single(SkillLibrary.Scan(root));
        Assert.Equal("release-notes", listed.Name);
        Assert.Equal("Writes release notes: short, \"plain\" English.", listed.Description);
        Assert.Equal("<version>", listed.ArgumentHint);
        Assert.True(listed.Editable);

        // Someone adds front matter by hand; an update from the app must keep it.
        File.WriteAllText(file, File.ReadAllText(file).Replace("---\n\n", "allowed-tools:\n  - shell\n  - read\n---\n\n"));
        SkillLibrary.Save(new SkillDraft("release-notes", "Writes release notes.", null, "# Release notes\nGroup by feature."), file);

        var parsed = SkillFile.Parse(File.ReadAllText(file));
        Assert.Equal("Writes release notes.", parsed.Fields["description"]);
        Assert.False(parsed.Fields.ContainsKey("argument-hint"));
        Assert.Contains("  - read", File.ReadAllText(file));
        Assert.Equal("# Release notes\nGroup by feature.", parsed.Body);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Refuses_duplicates_and_incomplete_drafts()
    {
        var root = TempRoot();
        SkillLibrary.Save(new SkillDraft("one", "Does one thing.", null, "Do it."), null, root);
        Assert.Throws<ArgumentException>(() => SkillLibrary.Save(new SkillDraft("one", "Again.", null, "Do it."), null, root));
        Assert.Throws<ArgumentException>(() => SkillLibrary.Save(new SkillDraft("Bad Name", "x", null, "y"), null, root));
        Assert.Throws<ArgumentException>(() => SkillLibrary.Save(new SkillDraft("two", " ", null, "y"), null, root));
        Assert.Throws<ArgumentException>(() => SkillLibrary.Save(new SkillDraft("two", "x", null, ""), null, root));
    }

    [Fact]
    public void Matches_a_slash_command_to_a_skill()
    {
        SkillInfo[] skills = [new("release-notes", "d", null, "Personal", null, true)];
        var match = SkillLibrary.MatchCommand("/Release-Notes for 2.4\nthanks", skills);
        Assert.NotNull(match);
        Assert.Equal("release-notes", match.Value.Skill.Name);
        Assert.Equal("for 2.4\nthanks", match.Value.Arguments);
        Assert.Equal("", SkillLibrary.MatchCommand("/release-notes", skills)!.Value.Arguments);
        Assert.Null(SkillLibrary.MatchCommand("/unknown thing", skills));
        Assert.Null(SkillLibrary.MatchCommand("release-notes", skills));
    }

    [Fact]
    public void Inline_prompt_carries_the_instructions_and_request()
    {
        var skill = new SkillInfo("release-notes", "d", null, "Personal", @"C:\x\release-notes\SKILL.md", true);
        var prompt = SkillLibrary.InlinePrompt(skill, "---\nname: release-notes\n---\n\nGroup by feature.", "for 2.4");
        Assert.Contains("Group by feature.", prompt);
        Assert.EndsWith("for 2.4", prompt);
        Assert.DoesNotContain("name: release-notes", prompt);
    }
}
