# ZERO PARADES Voice Override

Source code for the ZERO PARADES voice override BepInEx plugin and its player installer.

This repository is source-only. It does not include generated voice WAVs, BepInEx, Doorstop, `dotnet`, `winhttp.dll`, game files, or extracted dialogue indexes.

## Layout

```text
src/
  VoiceOverridePlugin.cs
  ZeroParadesVoiceOverride.csproj
installer/
  Install.bat
  install.ps1
  installer-config.json
  INSTALLER_README.txt
config/
  spore.zeroparades.voiceoverride.cfg
```

## Build

Install BepInEx IL2CPP in a local copy of the game and run the game once so `BepInEx/interop` exists. Then build:

```powershell
dotnet build .\src\ZeroParadesVoiceOverride.csproj -c Release /p:GameDir="<path-to-game-folder>"
```

If this repo is kept at the same relative depth used by the included project file inside a local game checkout, the default `GameDir` property points at the game root and this shorter command should work:

```powershell
dotnet build .\src\ZeroParadesVoiceOverride.csproj -c Release
```

The built plugin is:

```text
src/bin/Release/net6.0/ZeroParadesVoiceOverride.dll
```

## Release Installer

The installer is intended to be extracted into the same folder as `ZeroParades.exe`.

At install time it:

- downloads BepInEx IL2CPP x64 if missing
- installs/configures the voice override plugin
- asks which configured voice packs to download
- downloads male/female/extras/narrator voice packs from the URLs in `installer/installer-config.json`
- extracts voice packs into `BepInEx/voice-overrides`, `BepInEx/voice-overrides-female`, `BepInEx/voice-override-extras`, and `BepInEx/voice-override-narrator`
- records installed voice-pack metadata in `BepInEx/config/spore.zeroparades.voicepacks.json`
- checks configured remote pack metadata on later installer runs and marks packs as up to date, update available, untracked, or check unavailable

Before publishing, update the voice pack URLs in `installer/installer-config.json`. If a pack's Git/raw update-check URL should differ from its download URL, add `updateUrl`; otherwise the installer checks the download URL.

## Controls

- `F1`: cycle presets: original game VO, original + missing VO, male redub, female redub
- `F2`: cycle redub profile: off, male, female
- `F3`: toggle extra-character missing VO
- `F4`: toggle narrator-only missing VO
- `F10`: show the latest captured dialogue report and write `ZERO_PARADES_latest_dialogue_report.txt` in the game folder for sharing
- `F11`: toggle recurring voice-pack update toasts
- `F12`: toggle debug toasts for played override VO and generated missing VO

The extras and narrator folders are not F2 profiles. Existing game VO is replaced only by the active male/female redub profile. Missing/silent dialogue searches `voice-override-narrator` first when enabled, then `voice-override-extras`, then the active redub profile for cards listed in a silent-card index.

At game launch, the plugin reads `BepInEx/config/spore.zeroparades.voicepacks.json` and checks tracked voice pack URLs on a background thread. If remote metadata has changed, it shows a bottom-screen update toast and repeats it every `VoicePackUpdateToastRepeatMinutes` while update toasts are enabled.
