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
| Microsoft sign-in | `@azure/msal-node` + DPAPI-encrypted cache, system browser + PKCE | MIT |
| Secrets | Electron `safeStorage` (Windows DPAPI) | free |
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

## How secrets reach MCP servers

MCP configs reference secrets by name (`${secret:ado_pat}`, `${msal:account@tenant}`); the app
resolves them at launch into the server's env (stdio) or headers (HTTP). If a prompt needs Purview and
no valid token exists, the app opens the browser, completes sign-in, then starts or restarts that server.
The exact hand-off depends on how the existing Purview server expects its token (open question).

## Phases

0. **Spike (1 to 2 days)**: PAT, list models, one prompt, one existing MCP tool call, on Windows.
1. **Chat core**: shell, tray, streaming chat, model picker, tool approvals, History tab.
2. **Connections**: vault, Azure DevOps PAT, MCP manager with VS Code import, Microsoft accounts.
3. **Voice**: wake phrase, push to talk, STT, spoken answers, floating bar.
4. **Ship**: installer, auto-update, first-run setup.

## Risks

- Short wake phrases mis-fire; mitigated by a sensitivity setting, a confirm chime and push to talk.
- Entra conditional access or admin consent may be required for the app registration.
- Unsigned installer triggers a one-time SmartScreen warning.
- Always-on microphone: visible indicator, one-click mute, audio never leaves the PC.

## Open questions

1. Current VS Code `mcp.json` (secrets removed): transport and launch command per server.
2. How Purview tools obtain their token today; is there an Entra client ID and tenant to use?
3. Spoken answers by default, or text only?
4. Electron (fast to build, 150 to 250 MB idle) or native .NET (lighter, slower to build)?
