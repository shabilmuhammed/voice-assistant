# Voice (parked)

The "Hey Chat" voice feature, taken out of MarTechChat but kept here in case it comes back.
Nothing in this folder is in `Hark.sln`, referenced by the app or built by CI, and the build no longer downloads the
voice models. Ctrl+Alt+Space, which used to start listening, now opens the typed quick prompt instead.

| Path | What it is |
|---|---|
| `Hark.Voice/` | Offline wake phrase (sherpa-onnx keyword spotter), end-of-speech detection (Silero VAD) and speech-to-text (Moonshine). Its build downloads about 300 MB of models into `<repo>/models` once |
| `Hark.Voice.Tests/` | Tests that run the real models on recorded clips |
| `app/VoiceService.cs` | Microphone capture (NAudio) and the voice engine, as used by the app |
| `app/VoiceViewModel.cs` | Drove the voice panel: listening, working, answer |
| `app/VoiceOverlay.xaml(.cs)` | The voice panel. The quick prompt window (`src/Hark.App/Views/QuickPromptWindow.xaml`) is based on it |
| `app/SettingsViewModel.Voice.cs` | The voice members of the Settings page |
| `app/SettingsView.VoiceCard.xaml` | The Voice card of the Settings page |
| `app/AppSettings.Voice.cs` | The voice settings (wake phrase, sensitivity, microphone, corrections) |

## Bringing it back

1. Move `Hark.Voice` back to `src/` and `Hark.Voice.Tests` back to `tests/`, then fix the two relative paths in their
   project files (`ModelsDir` in `Hark.Voice.csproj`, the project reference in the tests), and add both to `Hark.sln`.
2. In `src/Hark.App/Hark.App.csproj`, reference `Hark.Voice` and the `NAudio.WinMM` package (3.3.0).
3. Move the files in `app/` back: `VoiceService.cs` and `VoiceOverlay.*` to `src/Hark.App/Voice/`, `VoiceViewModel.cs`
   to `src/Hark.App/ViewModels/`. Put the fragments back into `SettingsViewModel`, `SettingsView.xaml` and `AppSettings`.
4. `GlobalHotkey` now lives in `src/Hark.App/GlobalHotkey.cs` (namespace `Hark.App`) and its event is `QuickPrompt`;
   it still has `Escape` and `ListenForEscape`, which the voice panel used. Pick another shortcut for push to talk, or
   give Ctrl+Alt+Space back to voice.
5. In `App.xaml.cs` and `MainViewModel`, create the `VoiceViewModel` and `VoiceOverlay` again, call `Voice.ApplySettings()`
   in `InitializeAsync` and `Voice.Shutdown()` on exit, and restore the tray item "Listen for the wake phrase".
6. In `.github/workflows/build.yml`, restore the voice test step and the step that copies the models into the app
   folder, and remove the "no voice files" check (see the history of that file).
