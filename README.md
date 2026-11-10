# Hark

A Windows desktop app that puts GitHub Copilot and your MCP tools in one window, without VS Code.
"Hark" is a working name.

## What it does today

- **Chat** with any model your Copilot plan offers. Answers stream in and are formatted (tables, code, lists).
- **Your MCP tools**, read from the same `mcp.json` VS Code uses (`%APPDATA%\Code\User\mcp.json`).
- **Tool approvals.** Tools that only read run straight away; anything else asks first.
- **Work in your repos.** Copilot can pull, create a branch, edit and save files under your repositories folder,
  and commit and push when you ask, so a pull request can follow. Each command asks first unless you allow commands for that chat.
- **Attach files and images.** Use the paperclip, drag files in, or paste an image.
- **Generate files.** Files you ask for are saved in a folder you choose (Documents\Hark by default) and listed in the chat.
- **Read your screen.** The screen button, or phrases like "my screen" and "this code", attach a capture of the
  whole desktop to your message.
- **Voice.** Say "Hey Chat" (or your own phrase) while Hark is in the tray. A small panel appears above your other
  windows, listens until you stop speaking, and shows the answer as text. Ctrl+Alt+Space does the same without the phrase.
  Wake phrase, speech recognition and end-of-speech detection all run on this PC.
- **History.** Every chat is saved on this PC and can be reopened and continued.
- **Tray.** Closing the window keeps Hark running; Quit is in the tray icon's menu.

## Run it

**From the build:** open the latest run under the repository's Actions tab, download `hark-win-x64`, unzip, run `Hark.exe`.
Nothing else needs installing.

**From Visual Studio:** open `Hark.sln`, set `Hark.App` as the startup project, press Ctrl+F5. Needs the .NET 10 SDK.
The first build downloads the Copilot runtime (about 90 MB) and the voice models (about 300 MB) from GitHub.
Add `-p:FetchVoiceModels=false` to skip the models; voice is then unavailable.

## First start

1. **Connections:** paste your fine-grained GitHub PAT (it needs the Copilot Requests permission) and click Save and test.
2. **Connections:** check your MCP servers are listed.
3. **Settings:** check the Repositories folder (Hark fills in `%USERPROFILE%\source\repos` if it exists) and the Generated files folder.

## Where things are kept

Everything is on this PC, in `%LOCALAPPDATA%\Hark`:

| File | Contents |
|---|---|
| `settings.json` | Settings and the GitHub PAT, in plain text |
| `chats.db` | Chat history |
| `captures\` | Screen captures and pasted images you sent (removed after 30 days) |
| `hark.log` | Log, with the PAT and MCP tokens masked |
| `workspace\` | Copilot's working folder when no repositories folder is set |

MCP tokens stay in `mcp.json`. Hark only reads that file.

## What leaves the PC

Microphone audio never leaves the PC; only the text of what you said after the wake phrase is sent.
Your messages, tool results and anything you attach (files, images, screen captures) go to GitHub Copilot, using your PAT.
Your MCP servers talk to their own services (Azure DevOps, Purview) as they do in VS Code. Nothing else is sent anywhere.

## Projects

| Project | Purpose |
|---|---|
| `src/Hark.App` | The WPF app |
| `src/Hark.Core` | Settings, chat storage, Copilot SDK wrapper, repo rules |
| `src/Hark.Voice` | Offline wake phrase, end-of-speech detection and speech-to-text |
| `src/Hark.Spike` | Console check: PAT, models, one prompt, MCP servers |
| `tests/` | Unit tests |

## Develop

```bash
dotnet test tests/Hark.Core.Tests
```

```bash
dotnet test tests/Hark.Voice.Tests
```

The second command downloads about 300 MB of voice models into `models/` the first time.
`settings.json` has a `VoiceCorrections` list for words the recognizer gets wrong (for example "per view" for "Purview").

`Hark.exe --demo` opens the app with sample data and talks to nothing. The build uses it to take screenshots,
which it publishes to the `ci-screenshots` branch.
