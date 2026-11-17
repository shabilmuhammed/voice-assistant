using GitHub.Copilot;
using Hark.Core;
using Xunit;

namespace Hark.Core.Tests;

public class AzureCliTests
{
    [Theory]
    [InlineData("az boards work-item show --id 42")]
    [InlineData("az.cmd repos pr list")]
    [InlineData("& \"C:\\Program Files\\Microsoft SDKs\\Azure\\CLI2\\wbin\\az.cmd\" account show")]
    [InlineData("cd C:\\repos\\app; az devops project list")]
    [InlineData("git pull && az repos pr create --title x")]
    [InlineData("powershell -Command \"az login\"")]
    [InlineData("AZ LOGIN")]
    public void Spots_azure_cli_commands(string command) => Assert.True(AzureCli.UsesAz(command));

    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet build C:\\repos\\az\\app.csproj")]
    [InlineData("azcopy copy a b")]
    [InlineData("Get-ChildItem C:\\repos\\hazel")]
    [InlineData("echo lazy")]
    [InlineData("")]
    public void Leaves_other_commands_alone(string command) => Assert.False(AzureCli.UsesAz(command));

    [Fact]
    public void Uses_the_executables_copilot_parsed()
    {
        Assert.True(AzureCli.UsesAz("run-it.ps1", ["az"]));
        Assert.True(AzureCli.UsesAz("x", [@"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"]));
        Assert.False(AzureCli.UsesAz("git log", ["git"]));
    }

    [Theory]
    [InlineData("az login", true)]
    [InlineData("az login --allow-no-subscriptions", true)]
    [InlineData("az.cmd login --use-device-code", true)]
    [InlineData("az account show; az login", true)]
    [InlineData("az account show", false)]
    [InlineData("az devops login", false)]
    [InlineData("az boards query --wiql \"login\"", false)]
    public void Spots_az_login(string command, bool expected) => Assert.Equal(expected, AzureCli.IsLogin(command));

    private static Dictionary<string, McpServerConfig> Servers(params (string Name, McpServerConfig Config)[] servers) =>
        servers.ToDictionary(s => s.Name, s => s.Config);

    [Fact]
    public void Finds_the_devops_organisation_in_mcp_json()
    {
        var url = Servers(("martechchat", new McpStdioServerConfig
        {
            Command = "dotnet",
            Env = new Dictionary<string, string> { ["ADO_PAT"] = "secret", ["ADO_ORG_URL"] = "https://dev.azure.com/contoso/" },
        }));
        Assert.Equal("https://dev.azure.com/contoso", AzureCli.FindDevOpsOrganization(url));

        var name = Servers(("martechchat", new McpStdioServerConfig
        {
            Command = "dotnet",
            Env = new Dictionary<string, string> { ["ADO_ORGANIZATION"] = "contoso", ["ADO_PROJECT"] = "Demo" },
        }));
        Assert.Equal("https://dev.azure.com/contoso", AzureCli.FindDevOpsOrganization(name));

        var npx = Servers(("ado", new McpStdioServerConfig { Command = "npx", Args = ["-y", "@azure-devops/mcp", "contoso"] }));
        Assert.Equal("https://dev.azure.com/contoso", AzureCli.FindDevOpsOrganization(npx));

        var none = Servers(("other", new McpStdioServerConfig
        {
            Command = "node",
            Env = new Dictionary<string, string> { ["ADO_ORG"] = "${input:org}" },
        }));
        Assert.Null(AzureCli.FindDevOpsOrganization(none));
    }

    [Fact]
    public void Instructions_follow_the_setting()
    {
        var off = AzureCli.Instructions(enabled: false, installed: true, null);
        Assert.Contains("turned off", off);
        Assert.Contains("Do not use the Azure CLI", off);

        var missing = AzureCli.Instructions(enabled: true, installed: false, null);
        Assert.Contains("not installed", missing);

        var on = AzureCli.Instructions(enabled: true, installed: true, "https://dev.azure.com/contoso");
        Assert.Contains("--organization https://dev.azure.com/contoso", on);
        Assert.Contains("Never run az login a second time", on);
    }

    [Theory]
    [InlineData("ERROR: Please run 'az login' to setup account.\r\n", "Please run 'az login' to setup account.")]
    [InlineData("WARNING: something\nmore", "something")]
    [InlineData("", "")]
    public void Logs_az_messages_without_their_error_label(string stderr, string expected) =>
        Assert.Equal(expected, AzureCli.FirstLine(stderr));

    [Fact]
    public void Settings_have_the_fallback_off_by_default() => Assert.False(new AppSettings().AzureCliFallback);

    [Fact]
    public async Task Login_gate_reuses_a_working_sign_in()
    {
        var gate = new AzureLoginGate(() => Task.FromResult(true));
        Assert.Equal(AzureLoginGate.Decision.AlreadySignedIn, await gate.DecideAsync());
        Assert.Equal(AzureLoginGate.Decision.AlreadySignedIn, await gate.DecideAsync());
    }

    [Fact]
    public async Task Login_gate_lets_one_sign_in_through_at_a_time()
    {
        var now = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
        var signedIn = false;
        var gate = new AzureLoginGate(() => Task.FromResult(signedIn), TimeSpan.FromMinutes(2), () => now);

        Assert.Equal(AzureLoginGate.Decision.Allow, await gate.DecideAsync());
        Assert.Equal(AzureLoginGate.Decision.InProgress, await gate.DecideAsync());

        now = now.AddMinutes(1);
        Assert.Equal(AzureLoginGate.Decision.InProgress, await gate.DecideAsync());

        // The sign-in finished: later requests reuse it.
        now = now.AddMinutes(2);
        signedIn = true;
        Assert.Equal(AzureLoginGate.Decision.AlreadySignedIn, await gate.DecideAsync());

        // The cached sign-in expired much later: one new sign-in is allowed.
        now = now.AddDays(90);
        signedIn = false;
        Assert.Equal(AzureLoginGate.Decision.Allow, await gate.DecideAsync());
    }

    [Fact]
    public async Task Login_gate_answers_parallel_requests_once()
    {
        var checks = 0;
        var gate = new AzureLoginGate(async () => { Interlocked.Increment(ref checks); await Task.Delay(50); return false; });
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gate.DecideAsync()));
        Assert.Single(answers, a => a == AzureLoginGate.Decision.Allow);
        Assert.Equal(3, answers.Count(a => a == AzureLoginGate.Decision.InProgress));
        Assert.Equal(1, checks);
    }
}
