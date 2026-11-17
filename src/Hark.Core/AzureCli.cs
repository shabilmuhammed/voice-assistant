using System.Diagnostics;
using System.Text.RegularExpressions;
using GitHub.Copilot;

namespace Hark.Core;

/// <summary>
/// The Azure CLI fallback: when a tool server fails, Copilot may reach Azure DevOps with the Azure CLI (az).
/// Off by default. When on, Copilot signs in at most once and reuses the Azure CLI's cached sign-in.
/// </summary>
public static partial class AzureCli
{
    /// <summary>The Azure DevOps resource; a token for it means az devops, boards and repos commands work without a browser.</summary>
    public const string DevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    public const string TurnedOffFeedback =
        "The Azure CLI fallback is turned off in MarTechChat's settings, so az commands are not allowed. " +
        "Do not use the Azure CLI or try to sign in to Azure. Tell the user which tool failed and the error it returned.";

    public const string AlreadySignedInFeedback =
        "The Azure CLI is already signed in on this PC and its cached sign-in still works. " +
        "Do not run az login; run the az command you needed directly.";

    public const string SignInInProgressFeedback =
        "An Azure CLI sign-in was started a moment ago and may still be open in the browser. " +
        "Do not start another az login. Wait for it, then continue with the az command you needed.";

    [GeneratedRegex(@"(?i)(?:^|[\s;&|(""'`\\/])az(?:\.cmd|\.exe|\.bat)?(?=\s|$|[""';|&)])")]
    private static partial Regex AzWord();

    [GeneratedRegex(@"(?i)(?:^|[\s;&|(""'`\\/])az(?:\.cmd|\.exe|\.bat)?[""']?\s+login\b")]
    private static partial Regex AzLoginWords();

    private static bool IsAzExecutable(string identifier)
    {
        var trimmed = identifier.Trim().Trim('"', '\'');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        return name is "az" or "az.cmd" or "az.exe" or "az.bat";
    }

    /// <summary>True when a shell command runs the Azure CLI, judged from the executables Copilot parsed out of it and from its text.</summary>
    public static bool UsesAz(string? commandText, IReadOnlyList<string>? executables = null) =>
        (executables?.Any(IsAzExecutable) ?? false) ||
        (!string.IsNullOrWhiteSpace(commandText) && AzWord().IsMatch(commandText));

    /// <summary>True when a shell command signs in to the Azure CLI (az login), which opens the browser.</summary>
    public static bool IsLogin(string? commandText) =>
        !string.IsNullOrWhiteSpace(commandText) && AzLoginWords().IsMatch(commandText);

    /// <summary>The Azure DevOps organisation URL from the tool servers in mcp.json, if one names it.</summary>
    public static string? FindDevOpsOrganization(IReadOnlyDictionary<string, McpServerConfig> servers)
    {
        foreach (var config in servers.Values)
        {
            if (config is not McpStdioServerConfig stdio) continue;
            foreach (var (key, raw) in stdio.Env ?? new Dictionary<string, string>())
            {
                var value = raw?.Trim() ?? "";
                if (value.Length == 0 || value.Contains("${", StringComparison.Ordinal)) continue;
                if (value.Contains("dev.azure.com/", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                    return value.TrimEnd('/');
                var k = key.ToUpperInvariant();
                if (k.Contains("ORG") && (k.Contains("ADO") || k.Contains("DEVOPS")) && OrgName().IsMatch(value))
                    return "https://dev.azure.com/" + value;
            }
            // The Azure DevOps MCP server takes the organisation as the argument after its package name.
            var args = stdio.Args ?? [];
            var package = args.ToList().FindIndex(a => a.StartsWith("@azure-devops/mcp", StringComparison.OrdinalIgnoreCase));
            if (package >= 0 && package + 1 < args.Count && OrgName().IsMatch(args[package + 1]))
                return "https://dev.azure.com/" + args[package + 1];
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex OrgName();

    /// <summary>Standing instructions for Copilot about the Azure CLI.</summary>
    public static string Instructions(bool enabled, bool installed, string? organization)
    {
        if (!enabled)
            return """
                <azure_cli>
                The Azure CLI fallback is turned off in this app. Do not use the Azure CLI (az) and do not try to sign in to Azure.
                If a tool server fails, tell the user which tool failed and the error it returned.
                </azure_cli>
                """;
        if (!installed)
            return """
                <azure_cli>
                The Azure CLI fallback is turned on, but the Azure CLI is not installed on this PC. Do not try to use it.
                If a tool server fails, tell the user which tool failed and the error it returned.
                </azure_cli>
                """;

        var org = organization is null ? "" : $"\nThe Azure DevOps organisation is {organization}; pass --organization {organization} to az devops, boards, repos and pipelines commands.";
        return $"""
            <azure_cli>
            Tool servers come first. Use the Azure CLI (az) only when a tool server fails or cannot do the job.{org}
            The Azure CLI keeps its sign-in cached on this PC. Run the az command you need directly; do not sign in first.
            Only if an az command says you are not logged in, run az login --allow-no-subscriptions once and wait for it to finish.
            Never run az login a second time in this chat. Never print tokens or secrets.
            </azure_cli>
            """;
    }

    /// <summary>Where az is installed, or null when it is not.</summary>
    public static string? FindExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "az.cmd", "az.exe" } : new[] { "az" };
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft SDKs\Azure\CLI2\wbin"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft SDKs\Azure\CLI2\wbin"));
        }
        foreach (var folder in folders)
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(folder.Trim().Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        return null;
    }

    /// <summary>
    /// az login asks in the terminal which subscription to use after the browser step, and a shell that nobody types into
    /// waits on that question for ever. Turn the question off for az run from this app, unless the user set it themselves.
    /// </summary>
    public static void SkipSubscriptionQuestion()
    {
        const string name = "AZURE_CORE_LOGIN_EXPERIENCE_V2";
        if (Environment.GetEnvironmentVariable(name) is null) Environment.SetEnvironmentVariable(name, "off");
    }

    /// <summary>
    /// True when the Azure CLI's cached sign-in still gives an Azure DevOps token. Asks for a token, which refreshes it
    /// silently when needed and never opens a browser. False when az is missing, not signed in, or the sign-in has expired.
    /// </summary>
    public static async Task<bool> HasWorkingSignInAsync()
    {
        var result = await RunAsync(["account", "get-access-token", "--resource", DevOpsResource, "--query", "expiresOn", "--output", "tsv", "--only-show-errors"],
            TimeSpan.FromSeconds(25));
        if (result is { ExitCode: 0, Output.Length: > 0 }) return true;
        if (result is not null) Log.Info("Azure CLI is not signed in: " + FirstLine(result.Errors));
        return false;
    }

    /// <summary>Signs in through the browser (az login) and waits for it to finish. The Azure CLI keeps the sign-in on this PC.</summary>
    public static async Task<bool> SignInAsync()
    {
        var result = await RunAsync(["login", "--allow-no-subscriptions", "--output", "none", "--only-show-errors"], TimeSpan.FromMinutes(5));
        if (result is { ExitCode: 0 }) return true;
        if (result is not null) Log.Info("Azure CLI sign-in did not finish: " + FirstLine(result.Errors));
        return false;
    }

    private sealed record Result(int ExitCode, string Output, string Errors);

    private static async Task<Result?> RunAsync(string[] args, TimeSpan timeout)
    {
        var az = FindExecutable();
        if (az is null) return null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = az,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off";
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            using var cancel = new CancellationTokenSource(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            try
            {
                await process.WaitForExitAsync(cancel.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                Log.Info($"az {args[0]} {args[1]} timed out.");
                return null;
            }
            return new Result(process.ExitCode, (await output).Trim(), await errors);
        }
        catch (Exception ex)
        {
            Log.Error($"Running az {args[0]} failed", ex);
            return null;
        }
    }

    /// <summary>The Azure CLI's first message line, without its own "ERROR:" label: not being signed in is expected, not an app error.</summary>
    public static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        foreach (var label in new[] { "ERROR:", "WARNING:" })
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return line[label.Length..].Trim();
        return line;
    }
}

/// <summary>
/// Lets one az login through and refuses the rest: refused while the cached sign-in still works,
/// and while an approved sign-in may still be open in the browser.
/// </summary>
public sealed class AzureLoginGate(Func<Task<bool>> hasWorkingSignIn, TimeSpan? inProgressWindow = null, Func<DateTime>? clock = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _window = inProgressWindow ?? TimeSpan.FromMinutes(2);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private DateTime _lastApproved = DateTime.MinValue;

    public enum Decision { Allow, AlreadySignedIn, InProgress }

    public async Task<Decision> DecideAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_now() - _lastApproved < _window) return Decision.InProgress;
            if (await hasWorkingSignIn()) return Decision.AlreadySignedIn;
            _lastApproved = _now();
            return Decision.Allow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
