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
- downloads male/female/extras voice packs from the URLs in `installer/installer-config.json`
- extracts voice packs into `BepInEx/voice-overrides`, `BepInEx/voice-overrides-female`, and `BepInEx/voice-override-extras`

Before publishing, update the voice pack URLs in `installer/installer-config.json`.

## Controls

- `F1`: toggle override voices on/off
- `F2`: switch male/female profile
- `F12`: toggle debug toasts for played override VO and generated missing VO

The extras folder is not an F2 profile. It is searched after the active male/female profile whenever overrides are on, and is used for extra character voices and dialogue cards that originally had no VO. When overrides are off, original game VO is allowed to play.
