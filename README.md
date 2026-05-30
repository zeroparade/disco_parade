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
tools/
  build_voice_pack_shards.py
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
- supports manifest-based sharded packs and downloads only missing or changed shards on later runs
- extracts voice packs into `BepInEx/voice-overrides`, `BepInEx/voice-overrides-female`, `BepInEx/voice-override-extras`, and `BepInEx/voice-override-narrator`
- records installed voice-pack metadata in `BepInEx/config/spore.zeroparades.voicepacks.json`
- checks configured remote pack metadata on later installer runs and marks packs as up to date, update available, untracked, or check unavailable

Before publishing, update the voice pack URLs in `installer/installer-config.json`. Prefer `manifestUrl` plus `baseUrl` for sharded packs; keep `url` only as a legacy full-zip fallback.

To generate sharded voice packs from an installed game folder:

```powershell
python .\tools\build_voice_pack_shards.py --game-root "<path-to-game-folder>" --output "<output-folder>" --base-url "https://huggingface.co/datasets/zeroparade/dead_disco/resolve/main"
```

To build and publish to Hugging Face in one step:

```powershell
$env:HF_TOKEN = "<your Hugging Face token>"
python .\tools\build_voice_pack_shards.py `
  --game-root "Q:\Games\ZERO PARADES For Dead Spies" `
  --output "Q:\Games\ZERO PARADES For Dead Spies\voice_packs" `
  --upload
```

Use `--clean` only when you intentionally want to discard the local generated pack tree and rebuild it. For normal updates, omit `--clean`; the generator reads the previous manifests, reuses unchanged shard zips, writes only changed/new shards, and prunes stale shard zips. Shard filenames are stable (`male-b000-p00.zip`, etc.) so publishing updates overwrites the same remote paths. Content hashes are stored in the manifests. Files are assigned to stable dialogue-id hash buckets, so normal line additions or replacements only invalidate the affected bucket shard instead of reshuffling the whole pack.

The builder hashes files and writes changed shard ZIPs in parallel. It defaults to up to 8 workers; use `--workers 4` or `--workers 12` to tune for your CPU/disk.

After each build, `publish-changes.txt` lists the relative files that changed in the generated pack tree. Use that list when copying into the Hugging Face repository; a male-only update should normally include `manifests/male.json`, `manifest-index.json`, and only the changed `packs/male/shards/*.zip` files.

If you rerun the builder after generating local changes, `publish-changes.txt` can correctly say `No files changed` because the local output is already up to date. The builder also writes `publish-changes-remote.txt` by default for the live Hugging Face repo:

```powershell
python tools\build_voice_pack_shards.py `
  --game-root "Q:\Games\ZERO PARADES For Dead Spies" `
  --output "Q:\Games\ZERO PARADES For Dead Spies\voice_packs"
```

That remote list compares local manifests against the remote manifests and includes required `DELETE ...` lines for stale shard zips. To compare against a different remote, pass `--compare-remote-base-url`; to skip the network check, pass `--compare-remote-base-url ""`.

To show release notes in the in-game update toast, pass a per-pack message while building:

```powershell
python .\tools\build_voice_pack_shards.py --game-root "<path-to-game-folder>" --output "<output-folder>" -c "male=Fixed 30 male narrator lines"
```

You can repeat `-c`, for example `-c "male=..." -c "extras=..."`. For longer notes, add an optional text file at `<output-folder>/update-messages/<pack>.txt`, for example `voice_packs/update-messages/male.txt`. The CLI message takes precedence. The builder stores the text as `updateMessage` in `manifests/<pack>.json`, and the mod displays it when that pack is out of date. The message is not part of the voice-file hash, so changing the note alone does not make an already-current pack look outdated.

## Controls

- `F1`: cycle presets: original game VO, original + missing VO, male redub, female redub
- `F2`: cycle redub profile: off, male, female
- `F3`: toggle extra-character missing VO
- `F4`: toggle narrator-only missing VO
- `F9`: download and install available voice-pack updates while the game is running
- `F10`: show the latest captured dialogue report and write `ZERO_PARADES_latest_dialogue_report.txt` in the game folder for sharing
- `F11`: toggle recurring voice-pack update toasts
- `F12`: toggle debug toasts for played override VO and generated missing VO

The extras and narrator folders are not F2 profiles. Existing game VO is replaced only by the active male/female redub profile. Missing/silent dialogue searches `voice-override-narrator` first when enabled, then `voice-override-extras`, then the active redub profile for cards listed in a silent-card index.

At game launch, the plugin reads `BepInEx/config/spore.zeroparades.voicepacks.json` and checks tracked voice pack URLs on a background thread. For sharded packs it compares the installed `manifestHash` against the remote manifest. If remote metadata has changed, it shows a bottom-screen update toast and repeats it every `VoicePackUpdateToastRepeatMinutes` while update toasts are enabled. Press `F9` while the toast is active to download only changed shards, install them in place, prune obsolete managed audio, and update the local pack state without leaving the game.
