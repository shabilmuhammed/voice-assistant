# Hark (working name): plan

A Windows tray app that wakes on a custom phrase ("Hey Chat"), takes voice or typed prompts,
and runs them through GitHub Copilot with the team's MCP tools (Azure DevOps, Purview).
No VS Code required. The clickable mock is in `design/mockup.html`.

## Stack (everything free except Copilot usage)

| Piece | Choice | Licence / cost |
|---|---|---|
| Agent | `@github/copilot-sdk` (Node), runtime bundled per platform | MIT, billed to the Copilot seat |
| Shell | Electron + React + TypeScript (tray, global shortcut, autostart) | MIT |
| Wake phrase, STT, VAD | `sherpa-onnx-node` (open-vocabulary keyword spotting, local ASR) | Apache-2.0 |
| TTS | Windows built-in voices (optional local sherpa-onnx voices) | free |
| Microsoft sign-in | Browser SSO as today, login cached until expiry, no app registration | free |
| Tokens | Plain text in `mcp.json` / `settings.json` on the laptop | free |
| Chats | Local SQLite + SDK session ids for resume | free |
| Installer / updates | electron-builder, unsigned, GitHub Releases | free (no code-signing cert) |

## Checked against the installed packages (SDK 1.0.30, sherpa-onnx-node 2.0.1)

- `gitHubToken` option, and `useLoggedInUser: false` ignores env tokens and any other GitHub login,
  so only the PAT is ever used.
- `client.listModels()` returns id, name, policy state and billing multiplier / token prices.
- `mcpServers`: stdio (`command`, `args`, `env`, `workingDirectory`) and `http`/`sse` (`url`, `headers`),
  plus per-server `tools` filter and `timeout`. Same shape as VS Code `mcp.json`.
- `listSessions`, `resumeSession`, `deleteSession`; streaming events (`assistant.message_delta`,
  `tool.execution_*`, `assistant.usage`); permission handler for tool approvals; quota warnings.
- `@github/copilot-sdk-win32-x64` / `-arm64` ship the runtime, so no separate CLI install.
- sherpa-onnx exposes `KeywordSpotter`, streaming and offline recognisers, `Vad`, `OfflineTts`,
  with a prebuilt `win-x64` binary.

## Not yet confirmed (GitHub docs were unreachable from the build sandbox)

- The fine-grained PAT needs the "Copilot Requests" permission; classic PATs are not accepted.
- The GitHub org must allow Copilot CLI/SDK use and that kind of PAT.
- Copilot moved to usage-based billing in June 2026. If the org has paid overage enabled, heavy use
  could bill beyond the plan; an overage budget of zero prevents that.

The phase 0 spike settles the first two on a real machine.

## Config and tokens (decided)

- The app runs on one work laptop only. Tokens are stored in plain text, by the owner's choice.
- `mcp.json` is the single source for MCP servers and holds their tokens directly (for example the
  Azure DevOps PAT in `env`). Same shape as the VS Code file, so the existing one imports unchanged.
- The Copilot PAT is stored the same way in the app's `settings.json`.
- The custom MCP servers are local .NET projects started with `dotnet run --project <path>.csproj`.
  `dotnet run` rebuilds on start and is slow, so the app starts each server once and keeps it running
  while it sits in the tray. Pointing at the built DLL instead is an optional speed-up.
- `mcp.json` and `settings.json` are git-ignored so tokens cannot be committed by accident.

## Purview sign-in (no app registration, no service principal)

Today the Purview MCP tool opens the default browser for SSO when a Purview tool is first called.
That keeps working unchanged under the Copilot SDK, because the server process does the login itself.
Two levels, both free:

- **A. Zero change to the server.** The app keeps the Purview server process alive while it sits in
  the tray, so the login is reused until it expires. Quitting the app loses it (in-memory cache).
- **B. Cache survives restarts, and accounts appear in the Connections tab.** Either the server turns
  on its library's persistent token cache (a few lines of C#, e.g. `TokenCachePersistenceOptions`
  in Azure.Identity), or the app performs the same browser sign-in with the same public client the
  server already uses, stores the cache with Windows DPAPI, and hands the token to the server.

B is the target. Which variant depends on the server's login code (open question 2).

## Phases

0. **Spike (1 to 2 days)**: PAT, list models, one prompt, one existing MCP tool call, on Windows.
1. **Chat core**: shell, tray, streaming chat, model picker, tool approvals, History tab.
2. **Connections**: Copilot PAT, `mcp.json` editor with VS Code import, Microsoft accounts.
3. **Voice**: wake phrase, push to talk, STT, spoken answers, floating bar.
4. **Ship**: installer, auto-update, first-run setup.

## Risks

- Short wake phrases mis-fire; mitigated by a sensitivity setting, a confirm chime and push to talk.
- If the tenant restricts sign-in clients, the app must use the same client the Purview tool uses today.
- Unsigned installer triggers a one-time SmartScreen warning.
- Always-on microphone: visible indicator, one-click mute, audio never leaves the PC.

## Open questions

1. The Purview MCP tool's login code: the C# lines that create the credential.
2. Spoken answers by default, or text only?
3. Electron (fast to build, 150 to 250 MB idle) or native .NET (lighter, slower to build)?
