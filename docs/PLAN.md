# Hark (working name): plan

A native .NET Windows tray app that wakes on a custom phrase ("Hey Chat"), takes voice or typed
prompts, and runs them through GitHub Copilot with the existing MCP tools (Azure DevOps, Purview).
No VS Code required. Answers are text only. Clickable mock: `design/mockup.html`.

## Decisions

- **Native .NET**, not Electron.
- **Text answers only.** No text-to-speech.
- **One work laptop.** Tokens are plain text; no vault, no encryption.
- **No app registration and no service principal.** Purview uses browser SSO inside the MCP server.
- **MCP config is VS Code's own file**, `%APPDATA%\Code\User\mcp.json` (top-level `servers` key).
  The app reads it directly so VS Code and the app never drift. Path is changeable in Settings.
- **Only the Copilot PAT pays for AI.** `UseLoggedInUser = false`, token passed explicitly.

## Stack (all free)

| Piece | Choice | Licence |
|---|---|---|
| UI shell | WPF on .NET 8+, Fluent theme, tray icon, global hotkey, single instance | MIT |
| Agent | `GitHub.Copilot.SDK` NuGet (1.0.29) | MIT |
| Wake phrase, speech to text, VAD | `org.k2fsa.sherpa.onnx` NuGet (2.0.1, win-x64 runtime) | Apache-2.0 |
| Microphone capture | NAudio | MIT |
| Chats | `Microsoft.Data.Sqlite` in `%LOCALAPPDATA%\Hark` + SDK session ids for resume | MIT |
| Settings, Copilot PAT | `settings.json` in `%LOCALAPPDATA%\Hark`, plain text | n/a |
| Installer | Self-contained publish folder or zip, unsigned | n/a |

## Checked in the NuGet packages

- Copilot SDK: `GitHubToken`, `UseLoggedInUser`, `ListModelsAsync` (with `ModelBilling`),
  `McpServers` with `McpStdioServerConfig` / `McpHttpServerConfig`, `ListSessionsAsync`,
  `ResumeSessionAsync`, `OnPermissionRequest`.
- sherpa-onnx: `KeywordSpotter`, `OnlineRecognizer`, `OfflineRecognizer`, `VoiceActivityDetector`.

## Not yet confirmed

- The fine-grained PAT needs the "Copilot Requests" permission, and the GitHub org must allow
  Copilot CLI/SDK use. The spike settles this on the laptop.
- Copilot moved to usage-based billing in June 2026. If the org has paid overage enabled, heavy use
  could bill beyond the plan; an overage budget of zero prevents that.
- Wake-phrase accuracy for a short custom phrase needs tuning on the real microphone.
- The app is written in a Linux sandbox that cannot run WPF. Builds and tests happen on the laptop.

## MCP servers

- `martechchat`: local .NET project (`dotnet run --project … --no-launch-profile`), Azure DevOps and
  Purview tools, configured through `env` (ADO org, project, PAT, Purview account name).
- `ado-mcp`: `npx` package.
- `dotnet run` rebuilds on start, so the app starts each server once and keeps it alive in the tray.
- VS Code-only syntax (`${input:…}`, `inputs`) is detected and reported rather than silently ignored.

## Purview sign-in

`PurviewApiClient` uses `ChainedTokenCredential(DefaultAzureCredential, InteractiveBrowserCredential)`
and keeps the access token in memory until five minutes before expiry. It works unchanged under the
app. The login is lost only when the server process stops. Two free ways to make it last:

1. **`az login` once** (if Azure CLI is installed). `DefaultAzureCredential` then gets tokens silently
   from the CLI's own cache. No code change.
2. **Persistent cache in the tool** (about 15 lines): `InteractiveBrowserCredentialOptions` with
   `TokenCachePersistenceOptions`, plus saving the `AuthenticationRecord` to a file after the first
   login. The app reads that record (account name only, no secret) to show status in Connections;
   "Sign out" deletes it.

## Status

Done:

- Spike on the laptop: PAT accepted on its own, six models listed, MCP servers started outside VS Code.
- Chat core: streaming chat, model picker, tool approvals, history with resume, tray, settings.
- Formatted answers (tables, code blocks, lists, links).
- Repository work: sessions start in the repositories folder with standing rules. Commit and push are allowed when the
  user asks (never on the default branch, never forced); edits outside that folder always ask.
- Attachments: files and images by button, drag and drop, or paste.
- Generated files: saved to a configurable folder and listed in the chat with Open and Show in folder.
- Screen reading: whole-desktop capture attached to a message, by button or by phrase.
- Voice: "Hey Chat" wake phrase (configurable, no training), push to talk, on-screen panel, offline speech-to-text.
  Tested on recorded clips; not yet tested with a real microphone.
- Windows build on GitHub Actions with tests, a zip to download and screenshots of every screen.

Future:

- Keep MCP servers running between chats (each new chat currently restarts them).
- Purview sign-in that survives an app restart (`az login`, or a persistent token cache in the server).

## Open questions

1. Is Azure CLI installed (option 1 above), or should the tool get the cache patch (option 2)?
2. Should Copilot's built-in GitHub tools stay on?
