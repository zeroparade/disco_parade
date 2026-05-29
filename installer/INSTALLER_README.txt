ZERO PARADES Voice Override

Install:
1. Extract this ZIP into the folder that contains ZeroParades.exe.
2. Double-click Install.bat.
3. Choose which voice packs to download: male, female, extras, narrator, or any combination.
4. Wait for the success message.
5. Launch the game.

The installer must be in the same folder as ZeroParades.exe. If BepInEx is missing, it downloads and installs the pinned IL2CPP x64 build automatically.

Controls:
F1 cycles presets: original game VO, original + missing VO, male redub, female redub.
F2 cycles redub profile: off, male, female.
F3 toggles extra-character missing VO.
F4 toggles narrator-only missing VO.
F10 shows the latest captured dialogue report and writes ZERO_PARADES_latest_dialogue_report.txt in the game folder for sharing.
F11 toggles recurring voice-pack update toasts.
F12 toggles debug toasts for played override VO and generated missing VO.

Notes:
The ZIP does not bundle BepInEx itself.
The installer hides the BepInEx console window but keeps disk logging enabled.
The installer window stays visible and shows download progress.
You can rerun Install.bat later to add voice packs you skipped or update packs. Manifest-based packs download only missing or changed shards.
Voice pack install/update metadata is saved in BepInEx/config/spore.zeroparades.voicepacks.json.
