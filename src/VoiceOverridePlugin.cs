using System;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

[BepInPlugin("spore.zeroparades.voiceoverride", "ZERO PARADES Voice Override", "0.3.21")]
public class VoiceOverridePlugin : BasePlugin
{
    internal static VoiceOverridePlugin? Instance;
    internal static string LogPath = "";
    internal static string OverrideRoot = "";
    internal static string ExtraOverrideRoot = "";
    internal static string NarratorOverrideRoot = "";
    internal static float Volume = 1.0f;
    internal static bool UseFmodStreaming = true;
    internal static bool OverrideEnabled = true;
    internal static bool ExtraVoicesEnabled = true;
    internal static bool NarratorMissingVoicesEnabled = false;
    internal static bool OriginalVoiceEnabledForOverrides = false;
    internal static bool AllowOriginalOnOverrideFailure = true;
    internal static bool DebugToastsEnabled = false;
    internal static bool VoicePackUpdateToastsEnabled = true;
    internal static bool LiveFixEnabled = true;
    internal static bool LiveFixSkipOriginal = true;
    internal static string LiveFixRoot = "";
    internal static string LiveFixOverrideRoot = "";
    internal static string LiveFixEventsPath = "";
    internal static string LiveFixLatestPath = "";
    internal static string LiveFixCommandPath = "";
    private static readonly HashSet<string> _silentCardIds = new(StringComparer.Ordinal);
    private static bool _silentCardIndexLoaded;
    private static ConfigEntry<bool>? _overrideEnabledEntry;
    private static ConfigEntry<bool>? _extraVoicesEnabledEntry;
    private static ConfigEntry<bool>? _narratorMissingVoicesEnabledEntry;
    private static ConfigEntry<bool>? _originalVoiceEnabledEntry;
    private static ConfigEntry<bool>? _allowOriginalOnOverrideFailureEntry;
    private static ConfigEntry<bool>? _useFmodStreamingEntry;
    private static ConfigEntry<bool>? _voicePackUpdateToastsEnabledEntry;
    private static ConfigEntry<int>? _voicePackUpdateToastRepeatMinutesEntry;
    private static ConfigEntry<bool>? _liveFixEnabledEntry;
    private static ConfigEntry<bool>? _liveFixSkipOriginalEntry;
    private static ConfigEntry<string>? _liveFixRootEntry;
    private static ConfigEntry<string>? _overrideProfileEntry;
    private static ConfigEntry<string>? _maleOverrideRootEntry;
    private static ConfigEntry<string>? _femaleOverrideRootEntry;
    private static ConfigEntry<string>? _extraOverrideRootEntry;
    private static ConfigEntry<string>? _narratorOverrideRootEntry;
    private static ConfigEntry<KeyCode>? _toggleOverrideKeyEntry;
    private static ConfigEntry<KeyCode>? _cycleProfileKeyEntry;
    private static ConfigEntry<KeyCode>? _toggleExtraVoicesKeyEntry;
    private static ConfigEntry<KeyCode>? _toggleNarratorMissingVoicesKeyEntry;
    private static ConfigEntry<KeyCode>? _reportLatestDialogueKeyEntry;
    private static ConfigEntry<KeyCode>? _installVoicePackUpdatesKeyEntry;
    private static ConfigEntry<KeyCode>? _toggleVoicePackUpdateToastsKeyEntry;
    private static ConfigEntry<KeyCode>? _toggleDebugToastsKeyEntry;
    private static ConfigEntry<KeyCode>? _liveFixMarkKeyEntry;
    private static ConfigEntry<KeyCode>? _liveFixReplayKeyEntry;
    private static ConfigEntry<KeyCode>? _liveFixToggleKeyEntry;
    private static string _overrideProfile = "male";
    private static string _toastMessage = "";
    private static float _toastUntilRealtime;
    private Harmony? _harmony;
    private static GameObject? _audioRoot;
    private static AudioSource? _audioSource;
    private static AudioClip? _lastClip;
    private static Il2CppStructArray<float>? _lastSampleArray;
    private static MethodInfo? _audioClipStaticSetData;
    private static VoiceOverrideRunner? _runner;
    private static AudioMixerGroup? _observedMixerGroup;
    private static string _observedMixerGroupName = "";
    private static readonly Dictionary<string, DateTime> _recentImmediateOverridesUtc = new();
    private static readonly Dictionary<string, DateTime> _recentCardShownQueuesUtc = new();
    private static int _dialogueStopGeneration;
    private static readonly object _voicePackUpdateLock = new();
    private static readonly object _voicePackInstallLock = new();
    private static bool _voicePackUpdateCheckRunning;
    private static bool _voicePackInstallRunning;
    private static DateTime _lastVoicePackUpdateCheckUtc = DateTime.MinValue;
    private static DateTime _nextVoicePackUpdateToastUtc = DateTime.MinValue;
    private static string _voicePackUpdateToastMessage = "";
    private static List<VoicePackUpdateInfo> _voicePackUpdatesAvailable = new();
    private static readonly Dictionary<string, string> _dialogueTextById = new(StringComparer.Ordinal);
    private static string _latestDialogueId = "";
    private static string _latestDialogueText = "";
    private static string _latestDialogueSource = "";
    private static DateTime _latestDialogueUtc = DateTime.MinValue;
    private static string _latestFlowId = "";
    private static string _latestCardType = "";
    private static string _latestSpeakerId = "";
    private static string _latestSpeakerName = "";
    private static string _latestStandardType = "";
    private static DateTime _lastLiveFixEventUtc = DateTime.MinValue;
    private static string _lastLiveFixEventKey = "";
    private static DateTime _lastLiveFixCommandPollUtc = DateTime.MinValue;
    private static DateTime _lastLiveFixCommandWriteUtc = DateTime.MinValue;
    private static string _lastLiveFixCommandRequestId = "";
    private static string _cardShownAppearanceId = "";
    private static int _cardShownAppearanceStopGeneration = -1;
    private static bool _cardShownAppearanceQueuedOrPlayed;
    private static string _lastShownCardId = "";
    private static DateTime _lastShownCardUtc = DateTime.MinValue;
    private const int ImmediateDuplicateSuppressMs = 500;
    private const int CardShownFallbackDelayMs = 175;
    private const int DefaultVoicePackUpdateToastRepeatMinutes = 30;
    private const float DefaultVoicePackUpdateToastSeconds = 14f;
    private const string LatestDialogueReportFileName = "ZERO_PARADES_latest_dialogue_report.txt";
    private const int LiveFixDuplicateEventSuppressMs = 250;
    private const int LiveFixCommandPollMs = 250;
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    public override void Load()
    {
        Instance = this;
        BindConfig();
        ResolveOverrideRoot();
        var logDir = Path.Combine(Paths.BepInExRootPath, "voice-override-logs");
        Directory.CreateDirectory(logDir);
        if (!string.IsNullOrWhiteSpace(OverrideRoot)) Directory.CreateDirectory(OverrideRoot);
        if (!string.IsNullOrWhiteSpace(ExtraOverrideRoot)) Directory.CreateDirectory(ExtraOverrideRoot);
        if (!string.IsNullOrWhiteSpace(NarratorOverrideRoot)) Directory.CreateDirectory(NarratorOverrideRoot);
        if (!string.IsNullOrWhiteSpace(LiveFixRoot)) Directory.CreateDirectory(LiveFixRoot);
        if (!string.IsNullOrWhiteSpace(LiveFixOverrideRoot)) Directory.CreateDirectory(LiveFixOverrideRoot);
        LogPath = Path.Combine(logDir, "voice-override.log");
        File.AppendAllText(LogPath, $"\n=== ZERO PARADES Voice Override v0.3.21 loaded {DateTime.Now:O} ===\n");
        WriteLog($"OverrideRoot={OverrideRoot}");
        WriteLog($"ExtraOverrideRoot={ExtraOverrideRoot}");
        WriteLog($"NarratorOverrideRoot={NarratorOverrideRoot}");
        WriteLog($"LiveFixRoot={LiveFixRoot}");
        WriteLog($"LiveFixOverrideRoot={LiveFixOverrideRoot}");
        WriteLog($"OPTIONS overrideEnabled={OverrideEnabled} extraVoices={ExtraVoicesEnabled} narratorMissingVoices={NarratorMissingVoicesEnabled} originalVoiceWithOverride={OriginalVoiceEnabledForOverrides} allowOriginalOnFailure={AllowOriginalOnOverrideFailure} useFmodStreaming={UseFmodStreaming} debugToasts={DebugToastsEnabled} updateToasts={VoicePackUpdateToastsEnabled} updateRepeatMinutes={GetVoicePackUpdateToastRepeatMinutes()} liveFix={LiveFixEnabled} liveFixSkipOriginal={LiveFixSkipOriginal} profile={_overrideProfile} presetKey={GetConfiguredKeyName(_toggleOverrideKeyEntry)} cycleProfile={GetConfiguredKeyName(_cycleProfileKeyEntry)} toggleExtras={GetConfiguredKeyName(_toggleExtraVoicesKeyEntry)} toggleNarrator={GetConfiguredKeyName(_toggleNarratorMissingVoicesKeyEntry)} reportLatest={GetConfiguredKeyName(_reportLatestDialogueKeyEntry)} installUpdates={GetConfiguredKeyName(_installVoicePackUpdatesKeyEntry)} liveFixMark={GetConfiguredKeyName(_liveFixMarkKeyEntry)} liveFixReplay={GetConfiguredKeyName(_liveFixReplayKeyEntry)} liveFixToggle={GetConfiguredKeyName(_liveFixToggleKeyEntry)} updateToast={GetConfiguredKeyName(_toggleVoicePackUpdateToastsKeyEntry)} debugToast={GetConfiguredKeyName(_toggleDebugToastsKeyEntry)}");
        StartVoicePackUpdateCheck("LOAD", force: true);
        LoadSilentCardIndex();
        try
        {
            _runner = AddComponent<VoiceOverrideRunner>();
            WriteLog("UNITY_AUDIO_RUNNER_READY");
        }
        catch (Exception ex)
        {
            WriteLog($"UNITY_AUDIO_RUNNER_FAIL {ex.GetType().Name}: {ex.Message}");
        }

        _harmony = new Harmony("spore.zeroparades.voiceoverride.v02");
        Patch("ZAUM.C4.Audio.Dialogue.DialogueManagerAudioPresenter", "PlayVO", typeof(Patch_PlayVO), nameof(Patch_PlayVO.Prefix));
        Patch("ZAUM.C4.Audio.Dialogue.DialogueManagerAudioPresenter", "StopVO", typeof(Patch_StopVO), nameof(Patch_StopVO.Prefix));
        Patch("ZAUM.FELD.C4.Dialogues.Management.C4DialogueManager", "FireVOEvent", typeof(Patch_FireVOEvent), nameof(Patch_FireVOEvent.Prefix));
        Patch("ZAUM.FELD.C4.Dialogues.Management.C4DialogueManager", "FireStopVOEvent", typeof(Patch_FireStopVOEvent), nameof(Patch_FireStopVOEvent.Prefix));
        Patch("ZAUM.FELD.C4.Dialogues.Management.C4DialogueManager", "BeforeUpdateConversation", typeof(Patch_BeforeUpdateConversation), nameof(Patch_BeforeUpdateConversation.Prefix));
        WriteLog("PLUGIN_READY v0.3.21");
    }

    private void Patch(string typeName, string methodName, Type patchType, string patchMethod)
    {
        try
        {
            var t = FindType(typeName);
            if (t == null)
            {
                WriteLog($"PATCH_TYPE_MISSING {typeName}.{methodName}");
                return;
            }
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.Name == methodName)
                .ToArray();
            if (methods.Length == 0)
            {
                WriteLog($"PATCH_METHOD_MISSING {typeName}.{methodName}");
                return;
            }
            foreach (var m in methods)
            {
                _harmony!.Patch(m, prefix: new HarmonyMethod(patchType.GetMethod(patchMethod, BindingFlags.Public | BindingFlags.Static)));
                WriteLog($"PATCHED {typeName}.{Signature(m)}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"PATCH_FAIL {typeName}.{methodName}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void BindConfig()
    {
        _overrideEnabledEntry = Config.Bind(
            "Runtime",
            "OverrideEnabled",
            true,
            "Master custom voice switch. Presets and feature hotkeys update this automatically.");
        _extraVoicesEnabledEntry = Config.Bind(
            "Runtime",
            "ExtraVoicesEnabled",
            true,
            "If true, supplemental extra-character voices are used for matching missing/silent dialogue cards.");
        _narratorMissingVoicesEnabledEntry = Config.Bind(
            "Runtime",
            "NarratorMissingVoicesEnabled",
            false,
            "If true, narrator-only missing/silent dialogue is filled from the narrator override folder.");
        _originalVoiceEnabledEntry = Config.Bind(
            "Runtime",
            "OriginalVoiceEnabledWhenOverrideExists",
            false,
            "If true, the game's original VO is allowed to play even when an override file exists.");
        _allowOriginalOnOverrideFailureEntry = Config.Bind(
            "Runtime",
            "AllowOriginalVoiceWhenOverrideFails",
            true,
            "If true, the game's original VO is allowed if an override file exists but cannot be played.");
        _useFmodStreamingEntry = Config.Bind(
            "Audio",
            "UseFmodStreaming",
            true,
            "If true, external WAV/OGG files are opened with FMOD.CREATESTREAM instead of FMOD.CREATESAMPLE to reduce per-line load hitches.");
        _voicePackUpdateToastsEnabledEntry = Config.Bind(
            "Runtime",
            "VoicePackUpdateToastsEnabled",
            true,
            "If true, the mod checks installed voice-pack metadata against remote URLs and shows recurring update toasts when updates are available.");
        _voicePackUpdateToastRepeatMinutesEntry = Config.Bind(
            "Runtime",
            "VoicePackUpdateToastRepeatMinutes",
            DefaultVoicePackUpdateToastRepeatMinutes,
            "How often to repeat the voice-pack update toast while updates are available.");
        _liveFixEnabledEntry = Config.Bind(
            "LiveFix",
            "LiveFixEnabled",
            true,
            "If true, write live dialogue events and check the live-fix override folder before shipped voice packs.");
        _liveFixSkipOriginalEntry = Config.Bind(
            "LiveFix",
            "LiveFixSkipOriginal",
            true,
            "If true, a live-fix WAV suppresses the original game VO when a VO event exists.");
        _liveFixRootEntry = Config.Bind(
            "LiveFix",
            "LiveFixRoot",
            "voice-live-fix",
            "Live repair workspace. Relative paths are resolved under BepInEx.");
        _overrideProfileEntry = Config.Bind(
            "Profiles",
            "OverrideProfile",
            "male",
            "Active redub profile. Supported values: off, male, female.");
        _maleOverrideRootEntry = Config.Bind(
            "Profiles",
            "MaleOverrideRoot",
            "voice-overrides",
            "Override folder for the male profile. Relative paths are resolved under BepInEx.");
        _femaleOverrideRootEntry = Config.Bind(
            "Profiles",
            "FemaleOverrideRoot",
            "voice-overrides-female",
            "Override folder for the female profile. Relative paths are resolved under BepInEx.");
        _extraOverrideRootEntry = Config.Bind(
            "Profiles",
            "ExtraOverrideRoot",
            "voice-override-extras",
            "Supplemental override folder used for extra character voices. Relative paths are resolved under BepInEx.");
        _narratorOverrideRootEntry = Config.Bind(
            "Profiles",
            "NarratorOverrideRoot",
            "voice-override-narrator",
            "Supplemental override folder used for narrator-only missing dialogue. Relative paths are resolved under BepInEx.");
        _toggleOverrideKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleOverrideKey",
            KeyCode.F1,
            "Runtime hotkey for cycling voice presets. Set to None to disable.");
        _cycleProfileKeyEntry = Config.Bind(
            "Hotkeys",
            "CycleProfileKey",
            KeyCode.F2,
            "Runtime hotkey for switching redub profile between off, male, and female. Set to None to disable.");
        _toggleExtraVoicesKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleExtraVoicesKey",
            KeyCode.F3,
            "Runtime hotkey for toggling extra-character missing voices. Set to None to disable.");
        _toggleNarratorMissingVoicesKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleNarratorMissingVoicesKey",
            KeyCode.F4,
            "Runtime hotkey for toggling narrator-only missing voices. Set to None to disable.");
        _reportLatestDialogueKeyEntry = Config.Bind(
            "Hotkeys",
            "ReportLatestDialogueKey",
            KeyCode.F10,
            "Runtime hotkey for showing and writing the latest captured dialogue report. Set to None to disable.");
        _installVoicePackUpdatesKeyEntry = Config.Bind(
            "Hotkeys",
            "InstallVoicePackUpdatesKey",
            KeyCode.F9,
            "Runtime hotkey for downloading and installing available voice-pack updates while the game is running. Set to None to disable.");
        _toggleVoicePackUpdateToastsKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleVoicePackUpdateToastsKey",
            KeyCode.F11,
            "Runtime hotkey for toggling recurring voice-pack update toasts. Set to None to disable.");
        _toggleDebugToastsKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleDebugToastsKey",
            KeyCode.F12,
            "Runtime hotkey for toggling debug playback toasts. Set to None to disable.");
        _liveFixMarkKeyEntry = Config.Bind(
            "Hotkeys",
            "LiveFixMarkKey",
            KeyCode.F6,
            "Runtime hotkey for sending/marking the latest dialogue in the live-fix tool. Set to None to disable.");
        _liveFixReplayKeyEntry = Config.Bind(
            "Hotkeys",
            "LiveFixReplayKey",
            KeyCode.F7,
            "Runtime hotkey for replaying the latest live-fix/override WAV. Set to None to disable.");
        _liveFixToggleKeyEntry = Config.Bind(
            "Hotkeys",
            "LiveFixToggleKey",
            KeyCode.F8,
            "Runtime hotkey for toggling live-fix event capture and live override lookup. Set to None to disable.");

        if (_toggleOverrideKeyEntry.Value == KeyCode.F8) _toggleOverrideKeyEntry.Value = KeyCode.F1;
        if (_cycleProfileKeyEntry.Value == KeyCode.F10) _cycleProfileKeyEntry.Value = KeyCode.F2;

        OverrideEnabled = _overrideEnabledEntry.Value;
        ExtraVoicesEnabled = _extraVoicesEnabledEntry.Value;
        NarratorMissingVoicesEnabled = _narratorMissingVoicesEnabledEntry.Value;
        OriginalVoiceEnabledForOverrides = _originalVoiceEnabledEntry.Value;
        AllowOriginalOnOverrideFailure = _allowOriginalOnOverrideFailureEntry.Value;
        UseFmodStreaming = _useFmodStreamingEntry.Value;
        VoicePackUpdateToastsEnabled = _voicePackUpdateToastsEnabledEntry.Value;
        LiveFixEnabled = _liveFixEnabledEntry.Value;
        LiveFixSkipOriginal = _liveFixSkipOriginalEntry.Value;
        _overrideProfile = NormalizeProfile(_overrideProfileEntry.Value);
        _overrideProfileEntry.Value = _overrideProfile;
    }

    private static string NormalizeProfile(string? profile)
    {
        if (string.Equals(profile, "off", StringComparison.OrdinalIgnoreCase)) return "off";
        if (string.Equals(profile, "female", StringComparison.OrdinalIgnoreCase)) return "female";
        return "male";
    }

    private static void ResolveOverrideRoot()
    {
        _overrideProfile = NormalizeProfile(_overrideProfileEntry?.Value);
        if (string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase))
        {
            OverrideRoot = "";
        }
        else
        {
            var configured = _overrideProfile switch
            {
                "female" => _femaleOverrideRootEntry?.Value,
                _ => _maleOverrideRootEntry?.Value,
            };
            var fallback = _overrideProfile switch
            {
                "female" => "voice-overrides-female",
                _ => "voice-overrides",
            };
            OverrideRoot = ResolveBepInExPath(configured, fallback);
        }
        ExtraOverrideRoot = ResolveBepInExPath(_extraOverrideRootEntry?.Value, "voice-override-extras");
        NarratorOverrideRoot = ResolveBepInExPath(_narratorOverrideRootEntry?.Value, "voice-override-narrator");
        LiveFixRoot = ResolveBepInExPath(_liveFixRootEntry?.Value, "voice-live-fix");
        LiveFixOverrideRoot = Path.Combine(LiveFixRoot, "overrides");
        LiveFixEventsPath = Path.Combine(LiveFixRoot, "events.jsonl");
        LiveFixLatestPath = Path.Combine(LiveFixRoot, "latest-dialogue.json");
        LiveFixCommandPath = Path.Combine(LiveFixRoot, "command.json");
        try { if (!string.IsNullOrWhiteSpace(OverrideRoot)) Directory.CreateDirectory(OverrideRoot); } catch { }
        try { if (!string.IsNullOrWhiteSpace(ExtraOverrideRoot)) Directory.CreateDirectory(ExtraOverrideRoot); } catch { }
        try { if (!string.IsNullOrWhiteSpace(NarratorOverrideRoot)) Directory.CreateDirectory(NarratorOverrideRoot); } catch { }
        try { if (!string.IsNullOrWhiteSpace(LiveFixRoot)) Directory.CreateDirectory(LiveFixRoot); } catch { }
        try { if (!string.IsNullOrWhiteSpace(LiveFixOverrideRoot)) Directory.CreateDirectory(LiveFixOverrideRoot); } catch { }
    }

    private static string ResolveBepInExPath(string? configured, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        if (Path.IsPathRooted(value)) return value;
        return Path.Combine(Paths.BepInExRootPath, value);
    }

    private static string GetConfiguredKeyName(ConfigEntry<KeyCode>? entry)
    {
        return entry == null ? "<unset>" : entry.Value.ToString();
    }

    private static int GetVoicePackUpdateToastRepeatMinutes()
    {
        var configured = _voicePackUpdateToastRepeatMinutesEntry?.Value ?? DefaultVoicePackUpdateToastRepeatMinutes;
        return Math.Max(5, configured);
    }

    private static void StartVoicePackUpdateCheck(string source, bool force)
    {
        if (!VoicePackUpdateToastsEnabled)
        {
            WriteLog($"VOICE_PACK_UPDATE_CHECK_DISABLED source={source}");
            return;
        }

        var now = DateTime.UtcNow;
        var repeatMinutes = GetVoicePackUpdateToastRepeatMinutes();
        lock (_voicePackUpdateLock)
        {
            if (_voicePackUpdateCheckRunning) return;
            if (!force
                && _lastVoicePackUpdateCheckUtc != DateTime.MinValue
                && (now - _lastVoicePackUpdateCheckUtc).TotalMinutes < repeatMinutes)
            {
                return;
            }

            _voicePackUpdateCheckRunning = true;
            _lastVoicePackUpdateCheckUtc = now;
        }

        try
        {
            var thread = new System.Threading.Thread(() => RunVoicePackUpdateCheck(source))
            {
                IsBackground = true,
                Name = "ZeroParadesVoicePackUpdateCheck",
            };
            thread.Start();
            WriteLog($"VOICE_PACK_UPDATE_CHECK_STARTED source={source}");
        }
        catch (Exception ex)
        {
            lock (_voicePackUpdateLock) _voicePackUpdateCheckRunning = false;
            WriteLog($"VOICE_PACK_UPDATE_CHECK_START_FAIL {source}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void RunVoicePackUpdateCheck(string source)
    {
        try
        {
            var installedPacks = LoadInstalledVoicePackStates();
            if (installedPacks.Count == 0)
            {
                WriteLog($"VOICE_PACK_UPDATE_STATE_EMPTY source={source}");
                lock (_voicePackUpdateLock)
                {
                    _voicePackUpdateToastMessage = "";
                    _voicePackUpdatesAvailable = new List<VoicePackUpdateInfo>();
                    _voicePackUpdateCheckRunning = false;
                }
                return;
            }

            var changed = new List<VoicePackUpdateInfo>();
            foreach (var pack in installedPacks)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(pack.UpdateUrl))
                    {
                        WriteLog($"VOICE_PACK_UPDATE_SKIP_NO_URL {pack.Name}");
                        continue;
                    }

                    var remote = GetRemoteVoicePackMetadata(pack.UpdateUrl);
                    if (remote == null)
                    {
                        WriteLog($"VOICE_PACK_UPDATE_CHECK_UNAVAILABLE {pack.Name}");
                        continue;
                    }

                    if (IsVoicePackRemoteChanged(pack, remote))
                    {
                        var update = new VoicePackUpdateInfo
                        {
                            Name = pack.Name,
                            DisplayName = pack.DisplayName,
                            Message = NormalizeUpdateMessage(remote.UpdateMessage),
                        };
                        changed.Add(update);
                        WriteLog($"VOICE_PACK_UPDATE_AVAILABLE {pack.Name} installedManifest={pack.ManifestHash} remoteManifest={remote.ManifestHash} installedVersion={pack.ManifestVersion} remoteVersion={remote.ManifestVersion} message={(!string.IsNullOrWhiteSpace(update.Message))} etag={remote.Etag} length={remote.ContentLength} modified={remote.LastModified}");
                    }
                    else
                    {
                        WriteLog($"VOICE_PACK_UPDATE_CURRENT {pack.Name} manifest={remote.ManifestHash} version={remote.ManifestVersion} installedManifest={pack.ManifestHash} installedVersion={pack.ManifestVersion}");
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"VOICE_PACK_UPDATE_PACK_FAIL {pack.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            lock (_voicePackUpdateLock)
            {
                _voicePackUpdateToastMessage = changed.Count == 0 ? "" : BuildVoicePackUpdateToast(changed);
                _voicePackUpdatesAvailable = changed;
                _nextVoicePackUpdateToastUtc = DateTime.MinValue;
                _voicePackUpdateCheckRunning = false;
            }
            if (changed.Count > 0) WriteLog($"VOICE_PACK_UPDATE_TOAST_READY source={source} packs={string.Join(",", changed.Select(item => item.Name))}");
        }
        catch (Exception ex)
        {
            lock (_voicePackUpdateLock) _voicePackUpdateCheckRunning = false;
            WriteLog($"VOICE_PACK_UPDATE_CHECK_FAIL {source}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static List<InstalledVoicePackState> LoadInstalledVoicePackStates()
    {
        var statePath = Path.Combine(Paths.BepInExRootPath, "config", "spore.zeroparades.voicepacks.json");
        var result = new List<InstalledVoicePackState>();
        if (!File.Exists(statePath)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!doc.RootElement.TryGetProperty("packs", out var packsElement)
                || packsElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var packProperty in packsElement.EnumerateObject())
            {
                var pack = packProperty.Value;
                if (pack.ValueKind != JsonValueKind.Object) continue;

                var name = packProperty.Name;
                var displayName = GetJsonString(pack, "displayName");
                if (string.IsNullOrWhiteSpace(displayName)) displayName = name;

                var updateUrl = GetJsonString(pack, "updateUrl");
                if (string.IsNullOrWhiteSpace(updateUrl)) updateUrl = GetJsonString(pack, "url");

                result.Add(new InstalledVoicePackState
                {
                    Name = name,
                    DisplayName = displayName,
                    Destination = GetJsonString(pack, "destination"),
                    Format = GetJsonString(pack, "format"),
                    PackageUrl = GetJsonString(pack, "url"),
                    UpdateUrl = updateUrl,
                    Etag = GetJsonString(pack, "etag"),
                    LastModified = GetJsonString(pack, "lastModified"),
                    ContentLength = GetJsonInt64(pack, "contentLength"),
                    ManifestHash = GetJsonString(pack, "manifestHash"),
                    ManifestVersion = GetJsonString(pack, "manifestVersion"),
                    ShardSha256 = ReadInstalledShardHashes(pack),
                });
            }
        }
        catch (Exception ex)
        {
            WriteLog($"VOICE_PACK_UPDATE_STATE_READ_FAIL {statePath}: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    private static Dictionary<string, string> ReadInstalledShardHashes(JsonElement pack)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!pack.TryGetProperty("shards", out var shards) || shards.ValueKind != JsonValueKind.Object) return result;
            foreach (var shard in shards.EnumerateObject())
            {
                if (shard.Value.ValueKind != JsonValueKind.Object) continue;
                var sha = GetJsonString(shard.Value, "sha256");
                if (!string.IsNullOrWhiteSpace(sha)) result[shard.Name] = sha;
            }
        }
        catch { }
        return result;
    }

    private static string GetJsonString(JsonElement element, string name)
    {
        try
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static long GetJsonInt64(JsonElement element, string name)
    {
        try
        {
            if (element.TryGetProperty(name, out var property))
            {
                if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)) return value;
                if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value)) return value;
            }
        }
        catch { }
        return -1;
    }

    private static RemoteVoicePackMetadata? GetRemoteVoicePackMetadata(string url)
    {
        System.Net.HttpWebResponse? response = null;
        try
        {
            var requestUrl = AddNoCacheQuery(url);
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(requestUrl);
            request.Method = "HEAD";
            request.UserAgent = "ZeroParadesVoiceOverride/0.3.21";
            request.AllowAutoRedirect = true;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            PrepareNoCacheRequest(request);

            response = (System.Net.HttpWebResponse)request.GetResponse();
            var lastModified = "";
            try
            {
                if (response.LastModified > DateTime.MinValue)
                {
                    lastModified = response.LastModified.ToUniversalTime().ToString("o");
                }
            }
            catch { }

            var metadata = new RemoteVoicePackMetadata
            {
                Etag = response.Headers["ETag"] ?? "",
                LastModified = lastModified,
                ContentLength = response.ContentLength,
            };
            FillRemoteManifestMetadata(url, metadata);
            return metadata;
        }
        catch (Exception ex)
        {
            WriteLog($"VOICE_PACK_UPDATE_HEAD_FAIL {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { response?.Dispose(); } catch { }
        }
    }

    private static string AddNoCacheQuery(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var separator = url.IndexOf('?') >= 0 ? "&" : "?";
        return $"{url}{separator}zpv_update_check={DateTime.UtcNow.Ticks}";
    }

    private static void PrepareNoCacheRequest(System.Net.HttpWebRequest request)
    {
        try { request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore); } catch { }
        try { request.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate"; } catch { }
        try { request.Headers["Pragma"] = "no-cache"; } catch { }
    }

    private static void FillRemoteManifestMetadata(string url, RemoteVoicePackMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(url) || url.IndexOf(".json", StringComparison.OrdinalIgnoreCase) < 0) return;

        System.Net.HttpWebResponse? response = null;
        try
        {
            var requestUrl = AddNoCacheQuery(url);
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(requestUrl);
            request.Method = "GET";
            request.UserAgent = "ZeroParadesVoiceOverride/0.3.21";
            request.AllowAutoRedirect = true;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            PrepareNoCacheRequest(request);
            response = (System.Net.HttpWebResponse)request.GetResponse();
            using var stream = response.GetResponseStream();
            if (stream == null) return;
            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;
            metadata.ManifestHash = GetJsonString(root, "manifestHash");
            metadata.ManifestVersion = GetJsonString(root, "version");
            metadata.UpdateMessage = GetJsonString(root, "updateMessage");
            if (string.IsNullOrWhiteSpace(metadata.UpdateMessage)) metadata.UpdateMessage = GetJsonString(root, "releaseNotes");
            if (string.IsNullOrWhiteSpace(metadata.UpdateMessage)) metadata.UpdateMessage = GetJsonString(root, "changelog");
            metadata.FileCount = GetJsonInt64(root, "fileCount");
            metadata.ShardCount = GetJsonInt64(root, "shardCount");
            metadata.TotalBytes = GetJsonInt64(root, "totalBytes");
        }
        catch (Exception ex)
        {
            WriteLog($"VOICE_PACK_UPDATE_MANIFEST_READ_FAIL {url}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { response?.Dispose(); } catch { }
        }
    }

    private static bool IsVoicePackRemoteChanged(InstalledVoicePackState installed, RemoteVoicePackMetadata remote)
    {
        if (!string.IsNullOrWhiteSpace(installed.ManifestHash) && !string.IsNullOrWhiteSpace(remote.ManifestHash))
        {
            return !string.Equals(installed.ManifestHash, remote.ManifestHash, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(installed.Etag) && !string.IsNullOrWhiteSpace(remote.Etag))
        {
            return !string.Equals(installed.Etag, remote.Etag, StringComparison.Ordinal);
        }

        if (installed.ContentLength > 0 && remote.ContentLength > 0)
        {
            return installed.ContentLength != remote.ContentLength;
        }

        if (!string.IsNullOrWhiteSpace(installed.LastModified) && !string.IsNullOrWhiteSpace(remote.LastModified))
        {
            return !string.Equals(installed.LastModified, remote.LastModified, StringComparison.Ordinal);
        }

        return false;
    }

    private static string NormalizeUpdateMessage(string message)
    {
        message = message.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (message.Length > 520) message = message.Substring(0, 517).TrimEnd() + "...";
        return message;
    }

    private static string BuildVoicePackUpdateToast(List<VoicePackUpdateInfo> changedPacks)
    {
        var messages = changedPacks
            .Where(pack => !string.IsNullOrWhiteSpace(pack.Message))
            .Select(pack => changedPacks.Count == 1 ? pack.Message : $"{pack.DisplayName}: {pack.Message}")
            .ToList();
        var installKey = GetConfiguredKeyName(_installVoicePackUpdatesKeyEntry);
        var action = installKey.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "Run Install.bat to update."
            : $"Press {installKey} to update now, or run Install.bat.";
        var messageSuffix = messages.Count == 0 ? "" : "\n\nWhat's new:\n" + string.Join("\n\n", messages);

        if (changedPacks.Count == 1)
        {
            return $"Voice line update available: {changedPacks[0].DisplayName}. {action}{messageSuffix}";
        }

        if (changedPacks.Count <= 3)
        {
            return $"Voice line updates available: {string.Join(", ", changedPacks.Select(pack => pack.DisplayName))}. {action}{messageSuffix}";
        }

        return $"Voice line updates available for {changedPacks.Count} packs. {action}{messageSuffix}";
    }

    internal static void PollVoicePackUpdateNotifications()
    {
        if (!VoicePackUpdateToastsEnabled) return;

        var now = DateTime.UtcNow;
        StartVoicePackUpdateCheck("POLL", force: false);

        string message;
        lock (_voicePackUpdateLock)
        {
            if (string.IsNullOrWhiteSpace(_voicePackUpdateToastMessage)
                || now < _nextVoicePackUpdateToastUtc)
            {
                return;
            }

            message = _voicePackUpdateToastMessage;
            _nextVoicePackUpdateToastUtc = now.AddMinutes(GetVoicePackUpdateToastRepeatMinutes());
        }

        ShowToast(message, DefaultVoicePackUpdateToastSeconds);
    }

    private static void SetVoicePackUpdateToastsEnabled(bool enabled, string source)
    {
        VoicePackUpdateToastsEnabled = enabled;
        if (_voicePackUpdateToastsEnabledEntry != null) _voicePackUpdateToastsEnabledEntry.Value = enabled;
        if (!enabled)
        {
            lock (_voicePackUpdateLock)
            {
                _voicePackUpdateToastMessage = "";
                _voicePackUpdatesAvailable = new List<VoicePackUpdateInfo>();
                _nextVoicePackUpdateToastUtc = DateTime.MaxValue;
            }
        }
        else
        {
            lock (_voicePackUpdateLock)
            {
                _nextVoicePackUpdateToastUtc = DateTime.MinValue;
                _lastVoicePackUpdateCheckUtc = DateTime.MinValue;
            }
            StartVoicePackUpdateCheck(source, force: true);
        }

        SaveConfig();
        WriteLog($"OPTION_VOICE_PACK_UPDATE_TOASTS {enabled} source={source}");
        ShowToast(enabled ? "Voice update toasts: ON" : "Voice update toasts: OFF");
    }

    private static void StartVoicePackUpdateInstall(string source)
    {
        lock (_voicePackInstallLock)
        {
            if (_voicePackInstallRunning)
            {
                ShowToast("Voice update already running.", 8f);
                return;
            }
            _voicePackInstallRunning = true;
        }

        try
        {
            var thread = new System.Threading.Thread(() => RunVoicePackUpdateInstall(source))
            {
                IsBackground = true,
                Name = "ZeroParadesVoicePackInstall",
            };
            thread.Start();
            WriteLog($"VOICE_PACK_INSTALL_STARTED source={source}");
        }
        catch (Exception ex)
        {
            lock (_voicePackInstallLock) _voicePackInstallRunning = false;
            WriteLog($"VOICE_PACK_INSTALL_START_FAIL {source}: {ex.GetType().Name}: {ex.Message}");
            ShowToast($"Voice update failed to start: {ex.Message}", 12f);
        }
    }

    private static void RunVoicePackUpdateInstall(string source)
    {
        var updatedPacks = new List<string>();
        var changedShardCount = 0;
        var downloadedShardCount = 0;
        try
        {
            ShowToast("Checking voice line updates...", 8f);
            var installedPacks = LoadInstalledVoicePackStates();
            if (installedPacks.Count == 0)
            {
                WriteLog($"VOICE_PACK_INSTALL_STATE_EMPTY source={source}");
                ShowToast("No installed voice-pack metadata found. Run Install.bat once.", 14f);
                return;
            }

            foreach (var pack in installedPacks)
            {
                if (string.IsNullOrWhiteSpace(pack.UpdateUrl))
                {
                    WriteLog($"VOICE_PACK_INSTALL_SKIP_NO_URL {pack.Name}");
                    continue;
                }

                ShowToast($"Checking {pack.DisplayName}...", 8f);
                var manifest = DownloadVoicePackManifest(pack);
                if (manifest == null)
                {
                    WriteLog($"VOICE_PACK_INSTALL_MANIFEST_UNAVAILABLE {pack.Name}");
                    continue;
                }

                if (!IsVoicePackRemoteChanged(pack, manifest.Metadata) && VoicePackShardFilesCurrent(pack, manifest))
                {
                    WriteLog($"VOICE_PACK_INSTALL_CURRENT {pack.Name} version={manifest.Version}");
                    continue;
                }

                var result = InstallVoicePackManifest(pack, manifest);
                updatedPacks.Add(pack.DisplayName);
                changedShardCount += result.ChangedShards;
                downloadedShardCount += result.DownloadedShards;
                WriteLog($"VOICE_PACK_INSTALL_PACK_DONE {pack.Name} changedShards={result.ChangedShards} downloadedShards={result.DownloadedShards} pruned={result.PrunedFiles} files={result.AudioFiles}");
            }

            if (updatedPacks.Count == 0)
            {
                lock (_voicePackUpdateLock)
                {
                    _voicePackUpdateToastMessage = "";
                    _voicePackUpdatesAvailable = new List<VoicePackUpdateInfo>();
                    _nextVoicePackUpdateToastUtc = DateTime.MaxValue;
                    _lastVoicePackUpdateCheckUtc = DateTime.UtcNow;
                }
                ShowToast("Voice lines are already up to date.", 10f);
                return;
            }

            LoadSilentCardIndex();
            lock (_voicePackUpdateLock)
            {
                _voicePackUpdateToastMessage = "";
                _voicePackUpdatesAvailable = new List<VoicePackUpdateInfo>();
                _nextVoicePackUpdateToastUtc = DateTime.MaxValue;
                _lastVoicePackUpdateCheckUtc = DateTime.UtcNow;
            }

            var packList = string.Join(", ", updatedPacks);
            ShowToast($"Voice update installed.\nUpdated: {packList}\nShards changed: {changedShardCount}, downloaded: {downloadedShardCount}\nNew lines will be used immediately.", 18f);
        }
        catch (Exception ex)
        {
            WriteLog($"VOICE_PACK_INSTALL_FAIL {source}: {ex.GetType().Name}: {ex.Message}");
            WriteLog($"VOICE_PACK_INSTALL_FAIL_STACK {ex}");
            ShowToast($"Voice update failed: {ex.GetType().Name}: {ex.Message}\nRun Install.bat if this keeps happening.", 18f);
        }
        finally
        {
            lock (_voicePackInstallLock) _voicePackInstallRunning = false;
        }
    }

    private static VoicePackManifest? DownloadVoicePackManifest(InstalledVoicePackState pack)
    {
        System.Net.HttpWebResponse? response = null;
        try
        {
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(AddNoCacheQuery(pack.UpdateUrl));
            request.Method = "GET";
            request.UserAgent = "ZeroParadesVoiceOverride/0.3.21";
            request.AllowAutoRedirect = true;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            PrepareNoCacheRequest(request);
            response = (System.Net.HttpWebResponse)request.GetResponse();
            using var stream = response.GetResponseStream();
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var metadata = new RemoteVoicePackMetadata
            {
                Etag = response.Headers["ETag"] ?? "",
                LastModified = response.LastModified > DateTime.MinValue ? response.LastModified.ToUniversalTime().ToString("o") : "",
                ContentLength = response.ContentLength,
                ManifestHash = GetJsonString(root, "manifestHash"),
                ManifestVersion = GetJsonString(root, "version"),
                UpdateMessage = GetJsonString(root, "updateMessage"),
                FileCount = GetJsonInt64(root, "fileCount"),
                ShardCount = GetJsonInt64(root, "shardCount"),
                TotalBytes = GetJsonInt64(root, "totalBytes"),
            };
            var manifest = new VoicePackManifest
            {
                Name = GetJsonString(root, "pack"),
                DisplayName = GetJsonString(root, "displayName"),
                Destination = GetJsonString(root, "destination"),
                Format = GetJsonString(root, "format"),
                ManifestHash = metadata.ManifestHash,
                Version = metadata.ManifestVersion,
                UpdateMessage = metadata.UpdateMessage,
                FileCount = metadata.FileCount,
                ShardCount = metadata.ShardCount,
                TotalBytes = metadata.TotalBytes,
                ManifestUrl = pack.UpdateUrl,
                Metadata = metadata,
                RawJson = json,
            };
            if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = pack.Name;
            if (string.IsNullOrWhiteSpace(manifest.DisplayName)) manifest.DisplayName = pack.DisplayName;
            if (string.IsNullOrWhiteSpace(manifest.Destination)) manifest.Destination = pack.Destination;

            if (root.TryGetProperty("shards", out var shardsElement) && shardsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var shard in shardsElement.EnumerateArray())
                {
                    var shardInfo = new VoicePackShard
                    {
                        Name = GetJsonString(shard, "name"),
                        Path = GetJsonString(shard, "path"),
                        Url = GetJsonString(shard, "url"),
                        Sha256 = GetJsonString(shard, "sha256"),
                        Size = GetJsonInt64(shard, "size"),
                        FileCount = GetJsonInt64(shard, "fileCount"),
                    };
                    if (string.IsNullOrWhiteSpace(shardInfo.Name)) continue;
                    if (string.IsNullOrWhiteSpace(shardInfo.Url)) shardInfo.Url = JoinVoicePackRemotePath(pack.UpdateUrl, shardInfo.Path);
                    manifest.Shards.Add(shardInfo);
                }
            }

            if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var file in filesElement.EnumerateObject())
                {
                    if (file.Value.ValueKind != JsonValueKind.Object) continue;
                    var relativePath = GetJsonString(file.Value, "path");
                    var shardName = GetJsonString(file.Value, "shard");
                    if (string.IsNullOrWhiteSpace(relativePath)) continue;
                    manifest.ManagedRelativePaths.Add(NormalizeRelativePath(relativePath));
                    if (!string.IsNullOrWhiteSpace(shardName))
                    {
                        if (!manifest.FilesByShard.TryGetValue(shardName, out var paths))
                        {
                            paths = new List<string>();
                            manifest.FilesByShard[shardName] = paths;
                        }
                        paths.Add(relativePath);
                    }
                }
            }

            return manifest;
        }
        catch (Exception ex)
        {
            WriteLog($"VOICE_PACK_INSTALL_MANIFEST_FAIL {pack.Name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            try { response?.Dispose(); } catch { }
        }
    }

    private static VoicePackInstallResult InstallVoicePackManifest(InstalledVoicePackState pack, VoicePackManifest manifest)
    {
        if (!string.Equals(manifest.Format, "sharded-zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Voice pack '{pack.Name}' manifest format is '{manifest.Format}', expected sharded-zip.");
        }

        var destination = ResolveGameRelativePath(!string.IsNullOrWhiteSpace(manifest.Destination) ? manifest.Destination : pack.Destination);
        if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException($"Voice pack '{pack.Name}' has no destination.");
        Directory.CreateDirectory(destination);

        var safeName = SafeFileName(pack.Name);
        var downloadRoot = Path.Combine(GetGameRootPath(), "_voicepack-downloads", safeName);
        Directory.CreateDirectory(downloadRoot);
        var changedItems = new List<VoicePackShardInstallItem>();

        foreach (var shard in manifest.Shards)
        {
            var installedSha = pack.ShardSha256.TryGetValue(shard.Name, out var oldSha) ? oldSha : "";
            var alreadyInstalled = !string.IsNullOrWhiteSpace(installedSha)
                && string.Equals(installedSha, shard.Sha256, StringComparison.OrdinalIgnoreCase)
                && ShardFilesPresent(destination, manifest, shard.Name);
            if (alreadyInstalled) continue;

            var archive = Path.Combine(downloadRoot, shard.Name);
            var archiveOk = File.Exists(archive)
                && !string.IsNullOrWhiteSpace(shard.Sha256)
                && string.Equals(Sha256File(archive), shard.Sha256, StringComparison.OrdinalIgnoreCase);
            changedItems.Add(new VoicePackShardInstallItem
            {
                Shard = shard,
                Archive = archive,
                NeedsDownload = !archiveOk,
            });
        }

        var changedShards = changedItems.Count;
        var downloadedShards = 0;
        if (changedItems.Count > 0)
        {
            var downloadItems = changedItems.Where(item => item.NeedsDownload).ToList();
            var downloadWorkers = GetVoicePackDownloadParallelism();
            var extractWorkers = GetVoicePackExtractParallelism();
            WriteLog($"VOICE_PACK_INSTALL_PLAN {pack.Name} changedShards={changedItems.Count} downloads={downloadItems.Count} downloadWorkers={downloadWorkers} extractWorkers={extractWorkers}");

            if (downloadItems.Count > 0)
            {
                var downloaded = 0;
                ShowToast($"Downloading voice update...\n{pack.DisplayName}\n0 / {downloadItems.Count} shards", 12f);
                try
                {
                    System.Threading.Tasks.Parallel.ForEach(downloadItems, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = downloadWorkers }, item =>
                    {
                        ShowToast($"Downloading voice update...\n{pack.DisplayName}\n{item.Shard.Name}", 10f);
                        DownloadFileToPath(item.Shard.Url, item.Archive, $"{pack.DisplayName} {item.Shard.Name}");
                        var done = System.Threading.Interlocked.Increment(ref downloaded);
                        ShowToast($"Downloading voice update...\n{pack.DisplayName}\n{done} / {downloadItems.Count} shards", 10f);
                    });
                }
                catch (AggregateException ex)
                {
                    throw ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
                }
                downloadedShards = downloaded;
            }

            foreach (var item in changedItems)
            {
                var shard = item.Shard;
                if (!string.IsNullOrWhiteSpace(shard.Sha256))
                {
                    var actualSha = Sha256File(item.Archive);
                    if (!string.Equals(actualSha, shard.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Voice pack '{pack.Name}' shard '{shard.Name}' hash mismatch.");
                    }
                }
            }

            StopOverridePlaybackFromGame("VOICE_PACK_UPDATE");
            var extracted = 0;
            ShowToast($"Installing voice update...\n{pack.DisplayName}\n0 / {changedItems.Count} shards", 12f);
            try
            {
                System.Threading.Tasks.Parallel.ForEach(changedItems, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = extractWorkers }, item =>
                {
                    ExtractZipToDirectory(item.Archive, destination);
                    var done = System.Threading.Interlocked.Increment(ref extracted);
                    ShowToast($"Installing voice update...\n{pack.DisplayName}\n{done} / {changedItems.Count} shards", 10f);
                });
            }
            catch (AggregateException ex)
            {
                throw ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
            }
        }

        var pruned = PruneVoicePackFiles(destination, manifest);
        var manifestPath = Path.Combine(destination, "_voice-pack-manifest.json");
        File.WriteAllText(manifestPath, manifest.RawJson, System.Text.Encoding.UTF8);
        var audioFiles = CountAudioFiles(destination);
        WriteVoicePackState(pack, manifest, audioFiles);
        return new VoicePackInstallResult
        {
            ChangedShards = changedShards,
            DownloadedShards = downloadedShards,
            PrunedFiles = pruned,
            AudioFiles = audioFiles,
        };
    }

    private static int GetVoicePackDownloadParallelism()
    {
        return Math.Max(1, Math.Min(6, Environment.ProcessorCount));
    }

    private static int GetVoicePackExtractParallelism()
    {
        return Math.Max(1, Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)));
    }

    private static bool VoicePackShardFilesCurrent(InstalledVoicePackState pack, VoicePackManifest manifest)
    {
        var destination = ResolveGameRelativePath(!string.IsNullOrWhiteSpace(manifest.Destination) ? manifest.Destination : pack.Destination);
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination)) return false;
        foreach (var shard in manifest.Shards)
        {
            if (!pack.ShardSha256.TryGetValue(shard.Name, out var installedSha)
                || !string.Equals(installedSha, shard.Sha256, StringComparison.OrdinalIgnoreCase)
                || !ShardFilesPresent(destination, manifest, shard.Name))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ShardFilesPresent(string destination, VoicePackManifest manifest, string shardName)
    {
        if (!manifest.FilesByShard.TryGetValue(shardName, out var paths)) return false;
        foreach (var relativePath in paths)
        {
            var fullPath = SafeCombineUnderRoot(destination, relativePath);
            if (fullPath == null || !File.Exists(fullPath)) return false;
        }
        return true;
    }

    private static void DownloadFileToPath(string url, string destination, string displayName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        var tempPath = destination + ".tmp";
        System.Net.HttpWebResponse? response = null;
        try
        {
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(AddNoCacheQuery(url));
            request.Method = "GET";
            request.UserAgent = "ZeroParadesVoiceOverride/0.3.21";
            request.AllowAutoRedirect = true;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            PrepareNoCacheRequest(request);
            response = (System.Net.HttpWebResponse)request.GetResponse();
            var total = response.ContentLength;
            using (var input = response.GetResponseStream())
            {
                if (input == null) throw new IOException("No response stream.");
                using (var output = File.Create(tempPath))
                {
                    var buffer = new byte[1024 * 1024];
                    long copied = 0;
                    var lastToast = DateTime.UtcNow;
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        output.Write(buffer, 0, read);
                        copied += read;
                        if ((DateTime.UtcNow - lastToast).TotalSeconds >= 4)
                        {
                            lastToast = DateTime.UtcNow;
                            var status = total > 0
                                ? $"{FormatBytes(copied)} / {FormatBytes(total)}"
                                : FormatBytes(copied);
                            ShowToast($"Downloading voice update...\n{displayName}\n{status}", 10f);
                        }
                    }
                    output.Flush();
                }
            }
            MoveFileIntoPlace(tempPath, destination);
        }
        finally
        {
            try { response?.Dispose(); } catch { }
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void MoveFileIntoPlace(string tempPath, string destination)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(tempPath, destination);
                return;
            }
            catch (IOException ex) when (attempt < 4)
            {
                lastError = ex;
                System.Threading.Thread.Sleep(250 * (attempt + 1));
            }
        }
        throw lastError ?? new IOException($"Failed to move downloaded file into place: {destination}");
    }

    private static void ExtractZipToDirectory(string archive, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName) || entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Unsafe zip entry path: {entry.FullName}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static int PruneVoicePackFiles(string destination, VoicePackManifest manifest)
    {
        if (!Directory.Exists(destination) || manifest.ManagedRelativePaths.Count == 0) return 0;
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToArray())
        {
            var ext = Path.GetExtension(file);
            var name = Path.GetFileName(file);
            var managed = ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                || name.Equals("_silent-card-ids.txt", StringComparison.OrdinalIgnoreCase);
            if (!managed) continue;
            var relative = NormalizeRelativePath(file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (manifest.ManagedRelativePaths.Contains(relative)) continue;
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                WriteLog($"VOICE_PACK_PRUNE_FAIL {file}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return removed;
    }

    private static void WriteVoicePackState(InstalledVoicePackState pack, VoicePackManifest manifest, int audioFiles)
    {
        var statePath = Path.Combine(Paths.BepInExRootPath, "config", "spore.zeroparades.voicepacks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath) ?? ".");
        JsonObject root;
        try
        {
            root = File.Exists(statePath)
                ? (JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["schemaVersion"] = 1;
        root["updatedAt"] = DateTime.UtcNow.ToString("O");
        var packs = root["packs"] as JsonObject;
        if (packs == null)
        {
            packs = new JsonObject();
            root["packs"] = packs;
        }

        var shardState = new JsonObject();
        foreach (var shard in manifest.Shards.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            shardState[shard.Name] = new JsonObject
            {
                ["name"] = shard.Name,
                ["path"] = shard.Path,
                ["url"] = shard.Url,
                ["sha256"] = shard.Sha256,
                ["size"] = shard.Size,
                ["fileCount"] = shard.FileCount,
                ["installedAt"] = DateTime.UtcNow.ToString("O"),
            };
        }

        packs[pack.Name] = new JsonObject
        {
            ["name"] = pack.Name,
            ["displayName"] = string.IsNullOrWhiteSpace(manifest.DisplayName) ? pack.DisplayName : manifest.DisplayName,
            ["destination"] = string.IsNullOrWhiteSpace(manifest.Destination) ? pack.Destination : manifest.Destination,
            ["format"] = "sharded-zip",
            ["url"] = pack.PackageUrl,
            ["manifestUrl"] = pack.UpdateUrl,
            ["updateUrl"] = pack.UpdateUrl,
            ["manifestHash"] = manifest.ManifestHash,
            ["manifestVersion"] = manifest.Version,
            ["installedAt"] = DateTime.UtcNow.ToString("O"),
            ["etag"] = manifest.Metadata.Etag,
            ["lastModified"] = manifest.Metadata.LastModified,
            ["contentLength"] = manifest.Metadata.ContentLength,
            ["downloadedBytes"] = manifest.Metadata.ContentLength,
            ["fileCount"] = manifest.FileCount,
            ["shardCount"] = manifest.ShardCount,
            ["totalBytes"] = manifest.TotalBytes,
            ["wavCount"] = audioFiles,
            ["renamedCount"] = 0,
            ["shards"] = shardState,
        };

        File.WriteAllText(statePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
    }

    private static string JoinVoicePackRemotePath(string manifestUrl, string shardPath)
    {
        if (string.IsNullOrWhiteSpace(shardPath)) return "";
        if (shardPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || shardPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return shardPath;
        }

        var baseUrl = manifestUrl;
        var query = baseUrl.IndexOf('?');
        if (query >= 0) baseUrl = baseUrl.Substring(0, query);
        var marker = baseUrl.IndexOf("/manifests/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            baseUrl = baseUrl.Substring(0, marker);
        }
        else
        {
            var slash = baseUrl.LastIndexOf('/');
            if (slash >= 0) baseUrl = baseUrl.Substring(0, slash);
        }
        return baseUrl.TrimEnd('/') + "/" + shardPath.Replace("\\", "/").TrimStart('/');
    }

    private static string ResolveGameRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Path.IsPathRooted(value) ? value : Path.Combine(GetGameRootPath(), value.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string? SafeCombineUnderRoot(string rootPath, string relativePath)
    {
        try
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? "").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim('.', ' ', '_');
        return string.IsNullOrWhiteSpace(safe) ? "pack" : safe;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    private static int CountAudioFiles(string root)
    {
        if (!Directory.Exists(root)) return 0;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Count(path =>
                Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.0} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
        return $"{bytes} B";
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static string Signature(MethodInfo m)
    {
        try
        {
            return m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
        }
        catch { return m.Name; }
    }

    internal static string S(object? x)
    {
        if (x == null) return "<null>";
        try { return x.ToString() ?? "<nullstr>"; } catch (Exception ex) { return "<tostring:" + ex.GetType().Name + ">"; }
    }

    internal static string? TryGetMemberString(object? obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var flags in new[] { BindingFlags.Public | BindingFlags.Instance, BindingFlags.NonPublic | BindingFlags.Instance })
        {
            try
            {
                var p = t.GetProperty(name, flags);
                if (p != null) return S(p.GetValue(obj));
            }
            catch { }
            try
            {
                var f = t.GetField(name, flags);
                if (f != null) return S(f.GetValue(obj));
            }
            catch { }
        }
        return null;
    }

    internal static object? TryGetMemberObject(object? obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        foreach (var flags in new[] { BindingFlags.Public | BindingFlags.Instance, BindingFlags.NonPublic | BindingFlags.Instance })
        {
            try
            {
                var p = t.GetProperty(name, flags);
                if (p != null) return p.GetValue(obj);
            }
            catch { }
            try
            {
                var f = t.GetField(name, flags);
                if (f != null) return f.GetValue(obj);
            }
            catch { }
        }
        return null;
    }

    internal static string? CardIdFromRtCard(object? card)
    {
        if (card == null) return null;
        foreach (var n in new[] { "CardId", "cardId", "CardID", "cardID", "Id", "id", "cardVOID", "CardVOID" })
        {
            var v = TryGetMemberString(card, n);
            if (!string.IsNullOrWhiteSpace(v) && v != "<null>" && v != "<nullstr>") return v;
        }
        return null;
    }

    private static string? DialogueTextFromRtCard(object? card)
    {
        if (card == null) return null;

        foreach (var n in DialogueTextMemberNames)
        {
            var text = CleanDialogueText(TryGetMemberString(card, n));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        foreach (var n in new[] { "CardData", "cardData", "m_cardData", "Data", "data", "Properties", "properties" })
        {
            var data = TryGetMemberObject(card, n);
            var text = DialogueTextFromProperties(data);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    private static readonly string[] DialogueTextMemberNames =
    {
        "Text", "text", "DialogueText", "dialogueText", "Line", "line", "Content", "content",
        "Body", "body", "Description", "description", "Title", "title", "Subtitle", "subtitle",
        "DisplayText", "displayText", "LocalizedText", "localizedText", "m_text", "m_dialogueText",
    };

    private static readonly string[] DialogueTextKeys =
    {
        "text", "Text", "dialogueText", "DialogueText", "dialogue_text", "line", "Line",
        "content", "Content", "body", "Body", "description", "Description", "title", "Title",
        "displayText", "DisplayText", "localizedText", "LocalizedText",
    };

    private static string? DialogueTextFromProperties(object? data)
    {
        if (data == null) return null;

        foreach (var n in DialogueTextMemberNames)
        {
            var text = CleanDialogueText(TryGetMemberString(data, n));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        foreach (var key in DialogueTextKeys)
        {
            var value = TryGetIndexedOrNamedValue(data, key);
            var text = CleanDialogueTextFromValue(value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    private static object? TryGetIndexedOrNamedValue(object data, string key)
    {
        var t = data.GetType();
        foreach (var flags in new[] { BindingFlags.Public | BindingFlags.Instance, BindingFlags.NonPublic | BindingFlags.Instance })
        {
            try
            {
                foreach (var p in t.GetProperties(flags))
                {
                    var indexParameters = p.GetIndexParameters();
                    if (indexParameters.Length == 1)
                    {
                        return p.GetValue(data, new object[] { key });
                    }
                }
            }
            catch { }

            foreach (var methodName in new[] { "get_Item", "Get", "GetValue", "GetString" })
            {
                try
                {
                    var methods = t.GetMethods(flags).Where(m => m.Name == methodName);
                    foreach (var m in methods)
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length == 1)
                        {
                            return m.Invoke(data, new object[] { key });
                        }
                    }
                }
                catch { }
            }

            try
            {
                var tryGetMethods = t.GetMethods(flags).Where(m => m.Name == "TryGetValue");
                foreach (var m in tryGetMethods)
                {
                    var parameters = m.GetParameters();
                    if (parameters.Length == 2 && parameters[1].ParameterType.IsByRef)
                    {
                        var args = new object?[] { key, null };
                        var ok = m.Invoke(data, args);
                        if (ok is bool b && b) return args[1];
                    }
                }
            }
            catch { }
        }

        return null;
    }

    private static string? CleanDialogueTextFromValue(object? value)
    {
        if (value == null) return null;

        foreach (var n in new[] { "Value", "value", "StringValue", "stringValue", "Text", "text", "RawValue", "rawValue" })
        {
            var text = CleanDialogueText(TryGetMemberString(value, n));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return CleanDialogueText(S(value));
    }

    private static string? CleanDialogueText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.Length == 0
            || text.Equals("<null>", StringComparison.OrdinalIgnoreCase)
            || text.Equals("<nullstr>", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ZAUM.", StringComparison.Ordinal)
            || text.Contains("RtPropertiesDictionary", StringComparison.Ordinal)
            || text.Contains("Il2Cpp", StringComparison.Ordinal))
        {
            return null;
        }

        return string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()));
    }

    private static void TrackLatestDialogueCard(string source, object? card)
    {
        var id = CardIdFromRtCard(card);
        if (string.IsNullOrWhiteSpace(id)) return;
        UpdateLatestDialogueMetadata(card);
        TrackLatestDialogue(source, id, DialogueTextFromRtCard(card), card);
    }

    private static void TrackLatestDialogueId(string source, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        TrackLatestDialogue(source, id, null, null);
    }

    private static void TrackLatestDialogue(string source, string id, string? text, object? card)
    {
        var cleanedText = CleanDialogueText(text);
        if (!string.IsNullOrWhiteSpace(cleanedText))
        {
            _dialogueTextById[id] = cleanedText;
        }
        else if (_dialogueTextById.TryGetValue(id, out var cached))
        {
            cleanedText = cached;
        }

        _latestDialogueId = id;
        _latestDialogueText = cleanedText ?? "";
        _latestDialogueSource = source;
        _latestDialogueUtc = DateTime.UtcNow;
        WriteLiveFixDialogueEvent("dialogue_seen", source, id, cleanedText, card, force: false);
    }

    private static void UpdateLatestDialogueMetadata(object? card)
    {
        if (card == null) return;

        var flowId = FirstMemberString(card, "FlowId", "flowId", "m_flowId");
        if (!string.IsNullOrWhiteSpace(flowId)) _latestFlowId = flowId;

        var cardType = FirstMemberString(card, "CardType", "cardType", "m_cardType");
        if (!string.IsNullOrWhiteSpace(cardType)) _latestCardType = cardType;

        var standardType = FirstMemberString(card, "StandardType", "standardType");
        if (!string.IsNullOrWhiteSpace(standardType)) _latestStandardType = standardType;

        var speakerId = FirstMemberString(card, "EntityId", "entityId", "m_entityId", "SpeakerId", "speakerId");
        if (!string.IsNullOrWhiteSpace(speakerId)) _latestSpeakerId = speakerId;

        var speakerObject = TryGetMemberObject(card, "Speaker")
            ?? TryGetMemberObject(card, "speaker")
            ?? TryGetMemberObject(card, "Entity")
            ?? TryGetMemberObject(card, "entity");
        var speakerName = FirstMemberString(speakerObject, "FullName", "fullName", "Name", "name", "ShortName", "shortName", "m_shortName");
        if (!string.IsNullOrWhiteSpace(speakerName)) _latestSpeakerName = speakerName;
    }

    private static string FirstMemberString(object? obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = CleanDialogueText(TryGetMemberString(obj, name));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static bool SourceImpliesOriginalVo(string source)
    {
        return source.IndexOf("PlayVO", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("FireVOEvent", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void WriteLiveFixDialogueEvent(string kind, string source, string id, string? text, object? card, bool force)
    {
        if (!LiveFixEnabled || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(LiveFixEventsPath)) return;

        try
        {
            UpdateLatestDialogueMetadata(card);
            Directory.CreateDirectory(LiveFixRoot);
            Directory.CreateDirectory(LiveFixOverrideRoot);

            var now = DateTime.UtcNow;
            var cleanedText = CleanDialogueText(text) ?? _latestDialogueText;
            var eventKey = $"{kind}|{source}|{id}|{cleanedText}|{_latestFlowId}|{_latestSpeakerId}";
            if (!force
                && string.Equals(eventKey, _lastLiveFixEventKey, StringComparison.Ordinal)
                && (now - _lastLiveFixEventUtc).TotalMilliseconds < LiveFixDuplicateEventSuppressMs)
            {
                return;
            }

            _lastLiveFixEventKey = eventKey;
            _lastLiveFixEventUtc = now;

            var liveFile = FindLiveFixOverrideFile(id);
            var record = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["seen_at_utc"] = now.ToString("O"),
                ["source"] = source,
                ["card_id"] = id,
                ["text"] = cleanedText ?? "",
                ["flow_id"] = _latestFlowId,
                ["card_type"] = _latestCardType,
                ["standard_type"] = _latestStandardType,
                ["speaker_id"] = _latestSpeakerId,
                ["speaker_name"] = _latestSpeakerName,
                ["had_original_vo_guess"] = SourceImpliesOriginalVo(source),
                ["preset"] = CurrentPresetName(),
                ["override_enabled"] = OverrideEnabled,
                ["redub_profile"] = _overrideProfile,
                ["extras_enabled"] = ExtraVoicesEnabled,
                ["narrator_missing_enabled"] = NarratorMissingVoicesEnabled,
                ["live_fix_enabled"] = LiveFixEnabled,
                ["live_override_wav"] = liveFile ?? "",
                ["live_fix_root"] = LiveFixRoot,
            };
            var json = JsonSerializer.Serialize(record);
            File.AppendAllText(LiveFixEventsPath, json + Environment.NewLine);
            File.WriteAllText(LiveFixLatestPath, json);
        }
        catch (Exception ex)
        {
            WriteLog($"LIVE_FIX_EVENT_WRITE_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void WriteLiveFixPlaybackEvent(string source, string id, string playedSource, string file, bool skippedOriginal)
    {
        if (!LiveFixEnabled || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(LiveFixEventsPath)) return;

        try
        {
            Directory.CreateDirectory(LiveFixRoot);
            var record = new Dictionary<string, object?>
            {
                ["kind"] = "playback",
                ["seen_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = source,
                ["card_id"] = id,
                ["text"] = _latestDialogueText,
                ["flow_id"] = _latestFlowId,
                ["card_type"] = _latestCardType,
                ["standard_type"] = _latestStandardType,
                ["speaker_id"] = _latestSpeakerId,
                ["speaker_name"] = _latestSpeakerName,
                ["had_original_vo_guess"] = SourceImpliesOriginalVo(source),
                ["played_source"] = playedSource,
                ["played_wav"] = file,
                ["skipped_original"] = skippedOriginal,
                ["preset"] = CurrentPresetName(),
                ["redub_profile"] = _overrideProfile,
            };
            File.AppendAllText(LiveFixEventsPath, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            WriteLog($"LIVE_FIX_PLAYBACK_WRITE_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReportLatestDialogue(string source)
    {
        if (string.IsNullOrWhiteSpace(_latestDialogueId))
        {
            WriteLog($"LATEST_DIALOGUE_REPORT_EMPTY source={source}");
            ShowToast("No dialogue captured yet.", 4f);
            return;
        }

        var ageSeconds = Math.Max(0, (int)(DateTime.UtcNow - _latestDialogueUtc).TotalSeconds);
        var text = string.IsNullOrWhiteSpace(_latestDialogueText) ? "(text not captured)" : _latestDialogueText;
        WriteLog($"LATEST_DIALOGUE_REPORT source={source} id={_latestDialogueId} ageSeconds={ageSeconds} from={_latestDialogueSource} text={SanitizeForLog(text)}");
        var reportPath = Path.Combine(GetGameRootPath(), LatestDialogueReportFileName);
        var report = BuildLatestDialogueReport(source, ageSeconds, text, reportPath);
        try
        {
            File.WriteAllText(reportPath, report, System.Text.Encoding.UTF8);
            WriteLog($"LATEST_DIALOGUE_REPORT_FILE {reportPath}");
            ShowToast(report, 16f);
        }
        catch (Exception ex)
        {
            WriteLog($"LATEST_DIALOGUE_REPORT_FILE_FAIL {reportPath}: {ex.GetType().Name}: {ex.Message}");
            ShowToast($"{report}\n\nReport file write failed: {ex.GetType().Name}: {ex.Message}", 16f);
        }
    }

    private static string BuildLatestDialogueReport(string source, int ageSeconds, string text, string reportPath)
    {
        var capturedLocal = _latestDialogueUtc == DateTime.MinValue ? "(unknown)" : _latestDialogueUtc.ToLocalTime().ToString("O");
        var capturedUtc = _latestDialogueUtc == DateTime.MinValue ? "(unknown)" : _latestDialogueUtc.ToString("O");
        var maleRoot = ResolveBepInExPath(_maleOverrideRootEntry?.Value, "voice-overrides");
        var femaleRoot = ResolveBepInExPath(_femaleOverrideRootEntry?.Value, "voice-overrides-female");

        return string.Join(Environment.NewLine, new[]
        {
            "ZERO PARADES Voice Override latest dialogue report",
            "Share this file with the mod author when reporting a dialogue issue.",
            $"Report file: {reportPath}",
            $"Reported local time: {DateTime.Now:O}",
            $"Reported UTC: {DateTime.UtcNow:O}",
            $"Report source: {source}",
            $"Dialogue ID: {_latestDialogueId}",
            "Dialogue text:",
            text,
            $"Flow ID: {_latestFlowId}",
            $"Card type: {_latestCardType}",
            $"Speaker ID: {_latestSpeakerId}",
            $"Speaker name: {_latestSpeakerName}",
            $"Captured by: {_latestDialogueSource}",
            $"Captured local time: {capturedLocal}",
            $"Captured UTC: {capturedUtc}",
            $"Age seconds: {ageSeconds}",
            $"Preset: {CurrentPresetName()}",
            $"Voice state: {FormatVoiceState()}",
            $"Override enabled: {OverrideEnabled}",
            $"Original voice when override exists: {OriginalVoiceEnabledForOverrides}",
            $"Allow original if override fails: {AllowOriginalOnOverrideFailure}",
            $"Debug toasts enabled: {DebugToastsEnabled}",
            $"Update toasts enabled: {VoicePackUpdateToastsEnabled}",
            $"Active redub root: {(string.IsNullOrWhiteSpace(OverrideRoot) ? "(none)" : OverrideRoot)}",
            $"Male redub root: {maleRoot}",
            $"Female redub root: {femaleRoot}",
            $"Extras root: {ExtraOverrideRoot}",
            $"Narrator missing VO root: {NarratorOverrideRoot}",
            $"Live-fix enabled: {LiveFixEnabled}",
            $"Live-fix root: {LiveFixRoot}",
            $"Live-fix override root: {LiveFixOverrideRoot}",
            $"Live-fix events: {LiveFixEventsPath}",
            $"Plugin log: {LogPath}"
        });
    }

    private static string GetGameRootPath()
    {
        try
        {
            var bepinexRoot = Paths.BepInExRootPath;
            if (!string.IsNullOrWhiteSpace(bepinexRoot))
            {
                return Path.GetFullPath(Path.Combine(bepinexRoot, ".."));
            }
        }
        catch { }

        try { return Directory.GetCurrentDirectory(); }
        catch { return "."; }
    }

    private static string SanitizeForLog(string value)
    {
        return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
    }

    internal static string? FindOverrideFile(string id)
    {
        return FindOverrideFile(id, EnumerateOverrideRoots());
    }

    private static string? FindLiveFixOverrideFile(string id)
    {
        if (!LiveFixEnabled || string.IsNullOrWhiteSpace(LiveFixOverrideRoot)) return null;
        return FindOverrideFile(id, new[] { LiveFixOverrideRoot });
    }

    private static string? FindOverrideFile(string id, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var safe = id.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var ext in new[] { ".wav", ".ogg" })
            {
                var direct = Path.Combine(root, safe + ext);
                if (File.Exists(direct)) return direct;
            }
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, safe + ".wav", SearchOption.AllDirectories)) return f;
                foreach (var f in Directory.EnumerateFiles(root, safe + ".ogg", SearchOption.AllDirectories)) return f;
            }
            catch (Exception ex)
            {
                WriteLog($"ENUM_FAIL {id} root={root}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateOverrideRoots()
    {
        if (IsRedubEnabled() && !string.IsNullOrWhiteSpace(OverrideRoot)) yield return OverrideRoot;
    }

    private static string? FindReplacementVoiceFile(string id)
    {
        var live = FindLiveFixOverrideFile(id);
        if (live != null) return live;
        if (!IsRedubEnabled()) return null;
        return FindOverrideFile(id, EnumerateOverrideRoots());
    }

    private static string? FindAuxiliaryOverrideFile(string id, out string source)
    {
        source = "";
        if (NarratorMissingVoicesEnabled && !string.IsNullOrWhiteSpace(NarratorOverrideRoot))
        {
            var narrator = FindOverrideFile(id, new[] { NarratorOverrideRoot });
            if (narrator != null)
            {
                source = "narrator";
                return narrator;
            }
        }

        if (ExtraVoicesEnabled && !string.IsNullOrWhiteSpace(ExtraOverrideRoot))
        {
            var extra = FindOverrideFile(id, new[] { ExtraOverrideRoot });
            if (extra != null)
            {
                source = "extras";
                return extra;
            }
        }

        return null;
    }

    private static string? FindMissingVoiceFile(string id, out string source)
    {
        source = "";
        var live = FindLiveFixOverrideFile(id);
        if (live != null)
        {
            source = "live-fix";
            return live;
        }

        if (!OverrideEnabled) return null;

        if (NarratorMissingVoicesEnabled && !string.IsNullOrWhiteSpace(NarratorOverrideRoot))
        {
            var narrator = FindOverrideFile(id, new[] { NarratorOverrideRoot });
            if (narrator != null)
            {
                source = "narrator";
                return narrator;
            }
        }

        if (ExtraVoicesEnabled && !string.IsNullOrWhiteSpace(ExtraOverrideRoot))
        {
            var extra = FindOverrideFile(id, new[] { ExtraOverrideRoot });
            if (extra != null)
            {
                source = "extras";
                return extra;
            }
        }

        if (IsRedubEnabled() && IsSilentCardFallbackAllowed(id))
        {
            var redub = FindOverrideFile(id, EnumerateOverrideRoots());
            if (redub != null)
            {
                source = _overrideProfile;
                return redub;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSilentCardIndexRoots()
    {
        if (!string.IsNullOrWhiteSpace(LiveFixOverrideRoot)) yield return LiveFixOverrideRoot;
        if (!string.IsNullOrWhiteSpace(OverrideRoot)) yield return OverrideRoot;
        if (!string.IsNullOrWhiteSpace(ExtraOverrideRoot)
            && !string.Equals(ExtraOverrideRoot, OverrideRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return ExtraOverrideRoot;
        }
        if (!string.IsNullOrWhiteSpace(NarratorOverrideRoot)
            && !string.Equals(NarratorOverrideRoot, OverrideRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(NarratorOverrideRoot, ExtraOverrideRoot, StringComparison.OrdinalIgnoreCase))
        {
            yield return NarratorOverrideRoot;
        }
    }

    private static bool IsRedubEnabled()
    {
        return OverrideEnabled && !string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadSilentCardIndex()
    {
        _silentCardIds.Clear();
        _silentCardIndexLoaded = false;

        var candidates = EnumerateSilentCardIndexRoots()
            .Select(root => Path.Combine(root, "_silent-card-ids.txt"))
            .Concat(new[]
            {
                Path.Combine(Paths.BepInExRootPath, "voice-overrides", "_silent-card-ids.txt"),
                Path.Combine(Paths.BepInExRootPath, "voice-override-extras", "_silent-card-ids.txt"),
                Path.Combine(Paths.BepInExRootPath, "voice-override-narrator", "_silent-card-ids.txt"),
                Path.Combine(Paths.BepInExRootPath, "voice-overrides-silent-card-ids.txt"),
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var comma = line.IndexOf(',');
                    if (comma >= 0) line = line.Substring(0, comma).Trim();
                    if (line.Length == 0 || line.Equals("card_id", StringComparison.OrdinalIgnoreCase)) continue;
                    _silentCardIds.Add(line.Replace('/', '_').Replace('\\', '_').Replace(':', '_'));
                }
                _silentCardIndexLoaded = true;
                WriteLog($"SILENT_CARD_INDEX_LOADED count={_silentCardIds.Count} path={path}");
            }
            catch (Exception ex)
            {
                WriteLog($"SILENT_CARD_INDEX_LOAD_FAIL {path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!_silentCardIndexLoaded) WriteLog("SILENT_CARD_INDEX_MISSING fallback disabled");
    }

    private static bool IsSilentCardFallbackAllowed(string id)
    {
        return _silentCardIndexLoaded && _silentCardIds.Contains(id);
    }

    internal static bool TryReplace(string source, string? id, object? busContext = null)
    {
        ObserveAudioBus(busContext);
        if (string.IsNullOrWhiteSpace(id))
        {
            WriteLog($"{source} NO_ID");
            return true;
        }
        TrackLatestDialogueId(source, id);

        var liveFixFile = FindLiveFixOverrideFile(id);
        if (liveFixFile != null)
        {
            WriteLog($"{source} PLAY_LIVE_FIX {id} {liveFixFile}");
            if (PlayExternal(liveFixFile, id))
            {
                MarkImmediateOverride(id);
                ShowDebugToast($"Live-fix VO: {id}");
                WriteLiveFixPlaybackEvent(source, id, "live-fix", liveFixFile, LiveFixSkipOriginal);
                WriteLog(LiveFixSkipOriginal
                    ? $"{source} LIVE_FIX_SKIP_ORIGINAL {id}"
                    : $"{source} LIVE_FIX_ALLOW_ORIGINAL {id}");
                return !LiveFixSkipOriginal;
            }
            WriteLog(AllowOriginalOnOverrideFailure
                ? $"{source} LIVE_FIX_FAILED_ALLOW_ORIGINAL {id}"
                : $"{source} LIVE_FIX_FAILED_SKIP_ORIGINAL {id}");
            return AllowOriginalOnOverrideFailure || OriginalVoiceEnabledForOverrides;
        }

        if (!OverrideEnabled)
        {
            WriteLog($"{source} OVERRIDE_DISABLED_ALLOW_ORIGINAL {id}");
            return true;
        }

        var auxiliaryFile = FindAuxiliaryOverrideFile(id, out var auxiliarySource);
        if (auxiliaryFile != null)
        {
            var auxiliaryNow = DateTime.UtcNow;
            if (_recentImmediateOverridesUtc.TryGetValue(id, out var auxiliaryRecentImmediateUtc)
                && (auxiliaryNow - auxiliaryRecentImmediateUtc).TotalMilliseconds < ImmediateDuplicateSuppressMs)
            {
                _recentImmediateOverridesUtc[id] = auxiliaryNow;
                WriteLog($"{source} DUPLICATE_SUPPRESS {id}");
                return OriginalVoiceEnabledForOverrides;
            }

            WriteLog($"{source} PLAY_AUX_OVERRIDE {id} source={auxiliarySource} {auxiliaryFile}");
            if (PlayExternal(auxiliaryFile, id))
            {
                MarkImmediateOverride(id);
                ShowDebugToast($"{auxiliarySource} VO: {id}");
                WriteLog(OriginalVoiceEnabledForOverrides
                    ? $"{source} ALLOW_ORIGINAL {id}"
                    : $"{source} SKIP_ORIGINAL {id}");
                WriteLiveFixPlaybackEvent(source, id, auxiliarySource, auxiliaryFile, !OriginalVoiceEnabledForOverrides);
                return OriginalVoiceEnabledForOverrides;
            }
            WriteLog(AllowOriginalOnOverrideFailure
                ? $"{source} AUX_OVERRIDE_FAILED_ALLOW_ORIGINAL {id}"
                : $"{source} AUX_OVERRIDE_FAILED_SKIP_ORIGINAL {id}");
            return AllowOriginalOnOverrideFailure || OriginalVoiceEnabledForOverrides;
        }

        if (!IsRedubEnabled())
        {
            WriteLog($"{source} REDUB_DISABLED_ALLOW_ORIGINAL {id}");
            return true;
        }

        var file = FindReplacementVoiceFile(id);
        if (file == null)
        {
            WriteLog($"{source} NO_REDUB_OVERRIDE {id}");
            return true;
        }

        var now = DateTime.UtcNow;
        if (_recentImmediateOverridesUtc.TryGetValue(id, out var recentImmediateUtc)
            && (now - recentImmediateUtc).TotalMilliseconds < ImmediateDuplicateSuppressMs)
        {
            _recentImmediateOverridesUtc[id] = now;
            WriteLog($"{source} DUPLICATE_SUPPRESS {id}");
            return OriginalVoiceEnabledForOverrides;
        }

        WriteLog($"{source} PLAY_OVERRIDE {id} {file}");
        if (PlayExternal(file, id))
        {
            MarkImmediateOverride(id);
            ShowDebugToast($"Override VO: {id}");
            WriteLog(OriginalVoiceEnabledForOverrides
                ? $"{source} ALLOW_ORIGINAL {id}"
                : $"{source} SKIP_ORIGINAL {id}");
            WriteLiveFixPlaybackEvent(source, id, _overrideProfile, file, !OriginalVoiceEnabledForOverrides);
            return OriginalVoiceEnabledForOverrides;
        }
        WriteLog(AllowOriginalOnOverrideFailure
            ? $"{source} OVERRIDE_FAILED_ALLOW_ORIGINAL {id}"
            : $"{source} OVERRIDE_FAILED_SKIP_ORIGINAL {id}");
        return AllowOriginalOnOverrideFailure || OriginalVoiceEnabledForOverrides;
    }

    internal static void TryPlayCardShownFallback(string source, object? card, object? busContext = null)
    {
        try
        {
            ObserveAudioBus(busContext);
            var id = CardIdFromRtCard(card);
            if (string.IsNullOrWhiteSpace(id)) return;
            TrackLatestDialogueCard(source, card);

            var now = DateTime.UtcNow;
            var stopGeneration = _dialogueStopGeneration;
            if (!string.Equals(_cardShownAppearanceId, id, StringComparison.Ordinal)
                || _cardShownAppearanceStopGeneration != stopGeneration)
            {
                _cardShownAppearanceId = id;
                _cardShownAppearanceStopGeneration = stopGeneration;
                _cardShownAppearanceQueuedOrPlayed = false;
            }

            if (_recentImmediateOverridesUtc.TryGetValue(id, out var immediateUtc)
                && (now - immediateUtc).TotalMilliseconds < 1000)
            {
                _lastShownCardId = id;
                _lastShownCardUtc = now;
                _cardShownAppearanceQueuedOrPlayed = true;
                return;
            }

            var file = FindMissingVoiceFile(id, out var missingSource);
            if (file == null)
            {
                return;
            }

            if (!string.Equals(_lastShownCardId, id, StringComparison.Ordinal))
            {
                var previous = _lastShownCardId;
                _lastShownCardId = id;
                _lastShownCardUtc = now;
                _runner?.StopPlaybackForCardAdvance(previous, id);
            }
            else
            {
                _lastShownCardUtc = now;
            }

            if (_cardShownAppearanceQueuedOrPlayed)
            {
                WriteLog($"{source} APPEARANCE_DUPLICATE_SUPPRESS {id} stopGen={stopGeneration}");
                return;
            }

            _recentCardShownQueuesUtc[id] = now;
            _cardShownAppearanceQueuedOrPlayed = true;
            if (_runner != null && _runner.QueueDelayedUnityAudio(file, id, CardShownFallbackDelayMs))
            {
                WriteLog($"{source} DELAYED_OVERRIDE {id} source={missingSource} {file}");
                WriteLiveFixPlaybackEvent(source, id, missingSource, file, skippedOriginal: true);
                ShowDebugToast($"Missing VO ({missingSource}): {id}");
            }
            else if (string.Equals(Path.GetExtension(file), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                WriteLog($"{source} DELAYED_FALLBACK_NATIVE {id} source={missingSource} {file}");
                PlayWavNative(file, id);
                MarkImmediateOverride(id);
                WriteLiveFixPlaybackEvent(source, id, missingSource, file, skippedOriginal: true);
                ShowDebugToast($"Missing VO ({missingSource}): {id}");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"{source} DELAYED_OVERRIDE_FAIL {ex.GetType().Name}: {ex.Message}");
            WriteLog($"{source} DELAYED_OVERRIDE_FAIL_STACK {ex}");
        }
    }

    private static void MarkImmediateOverride(string id)
    {
        _recentImmediateOverridesUtc[id] = DateTime.UtcNow;
        if (string.Equals(_cardShownAppearanceId, id, StringComparison.Ordinal))
        {
            _cardShownAppearanceQueuedOrPlayed = true;
        }
    }

    internal static void StopOverridePlaybackFromGame(string source)
    {
        _dialogueStopGeneration++;
        _recentImmediateOverridesUtc.Clear();
        _recentCardShownQueuesUtc.Clear();
        _cardShownAppearanceQueuedOrPlayed = false;
        if (_runner != null)
        {
            _runner.StopPlaybackFromGame(source);
        }
        else
        {
            StopNativePlayback();
            StopUnityAudioSourceIfAlive();
        }
    }

    internal static void PollRuntimeHotkeys()
    {
        PollLiveFixCommand();

        if (WasKeyPressed(_toggleOverrideKeyEntry))
        {
            ApplyNextPreset("HOTKEY");
        }

        if (WasKeyPressed(_cycleProfileKeyEntry))
        {
            SetOverrideProfile(NextOverrideProfile(), "HOTKEY");
        }

        if (WasKeyPressed(_toggleExtraVoicesKeyEntry))
        {
            SetExtraVoicesEnabled(!ExtraVoicesEnabled, "HOTKEY");
        }

        if (WasKeyPressed(_toggleNarratorMissingVoicesKeyEntry))
        {
            SetNarratorMissingVoicesEnabled(!NarratorMissingVoicesEnabled, "HOTKEY");
        }

        if (WasKeyPressed(_toggleVoicePackUpdateToastsKeyEntry))
        {
            SetVoicePackUpdateToastsEnabled(!VoicePackUpdateToastsEnabled, "HOTKEY");
        }

        if (WasKeyPressed(_installVoicePackUpdatesKeyEntry))
        {
            StartVoicePackUpdateInstall("HOTKEY");
        }

        if (WasKeyPressed(_reportLatestDialogueKeyEntry))
        {
            ReportLatestDialogue("HOTKEY");
        }

        if (WasKeyPressed(_liveFixMarkKeyEntry))
        {
            MarkLatestDialogueForLiveFix("HOTKEY");
        }

        if (WasKeyPressed(_liveFixReplayKeyEntry))
        {
            ReplayLatestLiveFixDialogue("HOTKEY");
        }

        if (WasKeyPressed(_liveFixToggleKeyEntry))
        {
            SetLiveFixEnabled(!LiveFixEnabled, "HOTKEY");
        }

        if (WasKeyPressed(_toggleDebugToastsKeyEntry))
        {
            SetDebugToastsEnabled(!DebugToastsEnabled, "HOTKEY");
        }
    }

    private static void PollLiveFixCommand()
    {
        if (!LiveFixEnabled || string.IsNullOrWhiteSpace(LiveFixCommandPath)) return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastLiveFixCommandPollUtc).TotalMilliseconds < LiveFixCommandPollMs) return;
            _lastLiveFixCommandPollUtc = now;
            if (!File.Exists(LiveFixCommandPath)) return;

            var writeUtc = File.GetLastWriteTimeUtc(LiveFixCommandPath);
            if (writeUtc <= _lastLiveFixCommandWriteUtc) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(LiveFixCommandPath));
            var root = doc.RootElement;
            var requestId = GetJsonPropertyString(root, "request_id");
            if (!string.IsNullOrWhiteSpace(requestId)
                && string.Equals(requestId, _lastLiveFixCommandRequestId, StringComparison.Ordinal))
            {
                _lastLiveFixCommandWriteUtc = writeUtc;
                return;
            }

            var command = GetJsonPropertyString(root, "command");
            var cardId = GetJsonPropertyString(root, "card_id");
            _lastLiveFixCommandWriteUtc = writeUtc;
            _lastLiveFixCommandRequestId = requestId;

            if (string.Equals(command, "replay", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(cardId)) cardId = _latestDialogueId;
                ReplayLiveFixDialogue(cardId, "COMMAND");
            }
            else if (string.Equals(command, "reload", StringComparison.OrdinalIgnoreCase))
            {
                LoadSilentCardIndex();
                ShowToast("Live-fix reloaded");
                WriteLog("LIVE_FIX_COMMAND_RELOAD");
            }
            else if (string.Equals(command, "mark", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(cardId)) _latestDialogueId = cardId;
                MarkLatestDialogueForLiveFix("COMMAND");
            }
        }
        catch (Exception ex)
        {
            WriteLog($"LIVE_FIX_COMMAND_FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetJsonPropertyString(JsonElement element, string name)
    {
        try
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static string NextOverrideProfile()
    {
        return _overrideProfile switch
        {
            "male" => "female",
            "female" => "off",
            _ => "male",
        };
    }

    private static bool WasKeyPressed(ConfigEntry<KeyCode>? entry)
    {
        if (entry == null || entry.Value == KeyCode.None) return false;
        try { return Input.GetKeyDown(entry.Value); }
        catch { return false; }
    }

    private static bool WasKeyPressed(KeyCode key)
    {
        if (key == KeyCode.None) return false;
        try { return Input.GetKeyDown(key); }
        catch { return false; }
    }

    private static void SetOverrideEnabled(bool enabled, string source)
    {
        OverrideEnabled = enabled;
        if (_overrideEnabledEntry != null) _overrideEnabledEntry.Value = enabled;
        SaveConfig();
        if (!enabled) _runner?.StopPlaybackFromGame($"{source}_OVERRIDE_DISABLED");
        WriteLog($"OPTION_OVERRIDE_ENABLED {enabled} source={source}");
        ShowToast(enabled ? "Voice overrides: ON" : "Voice overrides: OFF - original VO");
    }

    private static void ApplyNextPreset(string source)
    {
        var current = CurrentPresetName();
        var next = current switch
        {
            "original" => "missing",
            "missing" => "male",
            "male" => "female",
            "female" => "original",
            _ => "original",
        };
        ApplyPreset(next, source);
    }

    private static string CurrentPresetName()
    {
        if (!OverrideEnabled || (string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase)
            && !ExtraVoicesEnabled
            && !NarratorMissingVoicesEnabled))
        {
            return "original";
        }

        if (string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase)
            && ExtraVoicesEnabled
            && NarratorMissingVoicesEnabled)
        {
            return "missing";
        }

        if (string.Equals(_overrideProfile, "male", StringComparison.OrdinalIgnoreCase)
            && ExtraVoicesEnabled
            && !NarratorMissingVoicesEnabled)
        {
            return "male";
        }

        if (string.Equals(_overrideProfile, "female", StringComparison.OrdinalIgnoreCase)
            && ExtraVoicesEnabled
            && !NarratorMissingVoicesEnabled)
        {
            return "female";
        }

        return "custom";
    }

    private static void ApplyPreset(string preset, string source)
    {
        switch (preset)
        {
            case "missing":
                ApplyVoiceState("off", extraVoices: true, narratorMissingVoices: true, masterEnabled: true, source, "Preset: Original + missing VO");
                break;
            case "male":
                ApplyVoiceState("male", extraVoices: true, narratorMissingVoices: false, masterEnabled: true, source, "Preset: Male redub");
                break;
            case "female":
                ApplyVoiceState("female", extraVoices: true, narratorMissingVoices: false, masterEnabled: true, source, "Preset: Female redub");
                break;
            default:
                ApplyVoiceState("off", extraVoices: false, narratorMissingVoices: false, masterEnabled: false, source, "Preset: Original game VO");
                break;
        }
    }

    private static void ApplyVoiceState(string profile, bool extraVoices, bool narratorMissingVoices, bool masterEnabled, string source, string toast)
    {
        var normalized = NormalizeProfile(profile);
        _overrideProfile = normalized;
        ExtraVoicesEnabled = extraVoices;
        NarratorMissingVoicesEnabled = narratorMissingVoices;
        OverrideEnabled = masterEnabled && (!string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase) || extraVoices || narratorMissingVoices);

        if (_overrideProfileEntry != null) _overrideProfileEntry.Value = normalized;
        if (_extraVoicesEnabledEntry != null) _extraVoicesEnabledEntry.Value = ExtraVoicesEnabled;
        if (_narratorMissingVoicesEnabledEntry != null) _narratorMissingVoicesEnabledEntry.Value = NarratorMissingVoicesEnabled;
        if (_overrideEnabledEntry != null) _overrideEnabledEntry.Value = OverrideEnabled;

        ResolveOverrideRoot();
        LoadSilentCardIndex();
        _runner?.StopPlaybackFromGame($"{source}_VOICE_STATE_CHANGE");
        SaveConfig();
        WriteLog($"OPTION_VOICE_STATE preset={CurrentPresetName()} overrideEnabled={OverrideEnabled} profile={_overrideProfile} extras={ExtraVoicesEnabled} narratorMissing={NarratorMissingVoicesEnabled} source={source}");
        ShowToast($"{toast} ({FormatVoiceState()})");
    }

    private static string FormatVoiceState()
    {
        var redub = string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase)
            ? "redub off"
            : $"redub {_overrideProfile}";
        var extras = ExtraVoicesEnabled ? "extras on" : "extras off";
        var narrator = NarratorMissingVoicesEnabled ? "narrator missing on" : "narrator missing off";
        return $"{redub}, {extras}, {narrator}";
    }

    private static void SetOverrideProfile(string profile, string source)
    {
        var normalized = NormalizeProfile(profile);
        var masterEnabled = !string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase)
            || ExtraVoicesEnabled
            || NarratorMissingVoicesEnabled;
        if (_overrideProfileEntry != null) _overrideProfileEntry.Value = normalized;
        _overrideProfile = normalized;
        OverrideEnabled = masterEnabled;
        if (_overrideEnabledEntry != null) _overrideEnabledEntry.Value = OverrideEnabled;
        ResolveOverrideRoot();
        LoadSilentCardIndex();
        _runner?.StopPlaybackFromGame($"{source}_PROFILE_SWITCH");
        SaveConfig();
        WriteLog($"OPTION_OVERRIDE_PROFILE {normalized} root={OverrideRoot} overrideEnabled={OverrideEnabled} source={source}");
        ShowToast($"Redub profile: {normalized.ToUpperInvariant()} ({FormatVoiceState()})");
    }

    private static void SetExtraVoicesEnabled(bool enabled, string source)
    {
        ExtraVoicesEnabled = enabled;
        OverrideEnabled = enabled || NarratorMissingVoicesEnabled || !string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase);
        if (_extraVoicesEnabledEntry != null) _extraVoicesEnabledEntry.Value = enabled;
        if (_overrideEnabledEntry != null) _overrideEnabledEntry.Value = OverrideEnabled;
        if (!OverrideEnabled) _runner?.StopPlaybackFromGame($"{source}_EXTRAS_DISABLED");
        SaveConfig();
        WriteLog($"OPTION_EXTRA_VOICES {enabled} overrideEnabled={OverrideEnabled} source={source}");
        ShowToast(enabled ? $"Extras: ON ({FormatVoiceState()})" : $"Extras: OFF ({FormatVoiceState()})");
    }

    private static void SetNarratorMissingVoicesEnabled(bool enabled, string source)
    {
        NarratorMissingVoicesEnabled = enabled;
        OverrideEnabled = enabled || ExtraVoicesEnabled || !string.Equals(_overrideProfile, "off", StringComparison.OrdinalIgnoreCase);
        if (_narratorMissingVoicesEnabledEntry != null) _narratorMissingVoicesEnabledEntry.Value = enabled;
        if (_overrideEnabledEntry != null) _overrideEnabledEntry.Value = OverrideEnabled;
        if (!OverrideEnabled) _runner?.StopPlaybackFromGame($"{source}_NARRATOR_DISABLED");
        SaveConfig();
        WriteLog($"OPTION_NARRATOR_MISSING_VOICES {enabled} overrideEnabled={OverrideEnabled} source={source}");
        ShowToast(enabled ? $"Narrator missing VO: ON ({FormatVoiceState()})" : $"Narrator missing VO: OFF ({FormatVoiceState()})");
    }

    private static void SetLiveFixEnabled(bool enabled, string source)
    {
        LiveFixEnabled = enabled;
        if (_liveFixEnabledEntry != null) _liveFixEnabledEntry.Value = enabled;
        ResolveOverrideRoot();
        LoadSilentCardIndex();
        SaveConfig();
        WriteLog($"OPTION_LIVE_FIX {enabled} source={source}");
        ShowToast(enabled ? "Live-fix: ON" : "Live-fix: OFF");
    }

    private static void MarkLatestDialogueForLiveFix(string source)
    {
        if (string.IsNullOrWhiteSpace(_latestDialogueId))
        {
            ShowToast("Live-fix: no dialogue captured yet.", 4f);
            WriteLog($"LIVE_FIX_MARK_EMPTY source={source}");
            return;
        }

        WriteLiveFixDialogueEvent("manual_mark", source, _latestDialogueId, _latestDialogueText, null, force: true);
        ShowToast($"Live-fix marked: {_latestDialogueId}", 4f);
        WriteLog($"LIVE_FIX_MARK {source} {_latestDialogueId}");
    }

    private static void ReplayLatestLiveFixDialogue(string source)
    {
        ReplayLiveFixDialogue(_latestDialogueId, source);
    }

    private static void ReplayLiveFixDialogue(string id, string source)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            ShowToast("Live-fix replay: no dialogue captured yet.", 4f);
            WriteLog($"LIVE_FIX_REPLAY_EMPTY source={source}");
            return;
        }

        LoadSilentCardIndex();
        var file = FindLiveFixOverrideFile(id);
        var playedSource = "live-fix";
        if (file == null)
        {
            file = FindReplacementVoiceFile(id);
            playedSource = string.Equals(file, FindLiveFixOverrideFile(id), StringComparison.OrdinalIgnoreCase)
                ? "live-fix"
                : _overrideProfile;
        }
        if (file == null)
        {
            file = FindMissingVoiceFile(id, out playedSource);
        }
        if (file == null)
        {
            ShowToast($"Live-fix replay: no WAV for {id}", 4f);
            WriteLog($"LIVE_FIX_REPLAY_NO_FILE source={source} id={id}");
            return;
        }

        WriteLog($"LIVE_FIX_REPLAY {source} {id} source={playedSource} {file}");
        if (PlayExternal(file, id))
        {
            WriteLiveFixPlaybackEvent(source, id, playedSource, file, skippedOriginal: true);
            ShowToast($"Live-fix replay: {id}", 3f);
        }
        else
        {
            ShowToast($"Live-fix replay failed: {id}", 4f);
        }
    }

    private static void SetDebugToastsEnabled(bool enabled, string source)
    {
        DebugToastsEnabled = enabled;
        WriteLog($"OPTION_DEBUG_TOASTS {enabled} source={source}");
        ShowToast(enabled ? "Voice debug toasts: ON" : "Voice debug toasts: OFF");
    }

    internal static void ShowToast(string message, float seconds = 2.75f)
    {
        _toastMessage = message;
        try { _toastUntilRealtime = Time.realtimeSinceStartup + Math.Max(1f, seconds); }
        catch { _toastUntilRealtime = 0f; }
        WriteLog($"TOAST {message}");
    }

    private static void ShowDebugToast(string message)
    {
        if (!DebugToastsEnabled) return;
        ShowToast(message);
    }

    internal static void DrawToast()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_toastMessage)) return;
            if (Time.realtimeSinceStartup > _toastUntilRealtime)
            {
                _toastMessage = "";
                return;
            }

            var isReportToast = _toastMessage.IndexOf("ZERO PARADES Voice Override latest dialogue report", StringComparison.OrdinalIgnoreCase) >= 0;
            var isUpdateToast = _toastMessage.IndexOf("Voice line update", StringComparison.OrdinalIgnoreCase) >= 0;
            var width = isReportToast
                ? Math.Min(1200f, Math.Max(520f, Screen.width - 64f))
                : isUpdateToast
                    ? Math.Min(1040f, Math.Max(460f, Screen.width - 80f))
                : Math.Min(860f, Math.Max(360f, Screen.width - 96f));
            var style = new GUIStyle(GUI.skin.box)
            {
                fontSize = isReportToast ? 16 : isUpdateToast ? 18 : 22,
                fontStyle = isReportToast || isUpdateToast ? FontStyle.Normal : FontStyle.Bold,
                alignment = isReportToast || isUpdateToast ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter,
                wordWrap = true,
            };
            style.padding = isReportToast || isUpdateToast ? new RectOffset(26, 26, 18, 18) : new RectOffset(24, 24, 14, 14);
            style.normal.textColor = Color.white;

            var content = new GUIContent(_toastMessage);
            var maxHeight = isReportToast ? Math.Max(160f, Screen.height - 56f) : isUpdateToast ? Math.Min(420f, Math.Max(180f, Screen.height - 96f)) : 220f;
            if (isReportToast || isUpdateToast)
            {
                for (var size = style.fontSize; size > 11 && style.CalcHeight(content, width) + 24f > maxHeight; size--)
                {
                    style.fontSize = size - 1;
                }
            }

            var height = Math.Min(maxHeight, Math.Max(72f, style.CalcHeight(content, width) + (isReportToast || isUpdateToast ? 26f : 18f)));
            var bottomOffset = isReportToast ? 28f : isUpdateToast ? 42f : 72f;
            var rect = new Rect((Screen.width - width) * 0.5f, Math.Max(20f, Screen.height - height - bottomOffset), width, height);
            GUI.Box(rect, content, style);
        }
        catch
        {
            _toastMessage = "";
        }
    }

    private static void SaveConfig()
    {
        try { Instance?.Config.Save(); } catch { }
    }

    internal static bool PlayExternal(string file, string id)
    {
        try
        {
            var ext = Path.GetExtension(file);
            if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase))
            {
                if (_runner != null && _runner.QueueUnityAudio(file, id))
                {
                    return true;
                }

                if (!string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog($"PLAY_START_FAIL {id}: no Unity runner for {ext}");
                    return false;
                }

                return PlayWavNative(file, id);
            }

            var clip = LoadWavClipWithSetData(file, id);
            if (clip == null) return false;
            var src = EnsureAudioSource();
            if (src == null)
            {
                WriteLog($"PLAY_START_FAIL {id}: no AudioSource");
                return false;
            }
            if (src.isPlaying) src.Stop();
            _lastClip = clip; // keep the managed/IL2CPP wrapper rooted while Unity plays it
            src.clip = clip;
            src.volume = Volume;
            src.spatialBlend = 0f;
            src.playOnAwake = false;
            src.loop = false;
            src.Play();
            WriteLog($"PLAYING {id} length={clip.length:0.000} channels={clip.channels} hz={clip.frequency}");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"PLAY_START_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
            WriteLog($"PLAY_START_FAIL_STACK {id}: {ex}");
            ResetAudioSourceIfCollected(ex);
            return false;
        }
    }

    internal static bool PlayWavNative(string file, string id)
    {
        if (!File.Exists(file))
        {
            WriteLog($"PLAY_NATIVE_FAIL {id}: file missing {file}");
            return false;
        }

        try
        {
            StopNativePlayback(); // stop prior native override, if one is still active
            bool ok = PlaySound(file, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
            if (!ok)
            {
                WriteLog($"PLAY_NATIVE_FAIL {id}: PlaySoundW returned false win32={Marshal.GetLastWin32Error()}");
                return false;
            }
            WriteLog($"PLAYING_NATIVE_WAV {id} bytes={new FileInfo(file).Length}");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"PLAY_NATIVE_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
            WriteLog($"PLAY_NATIVE_FAIL_STACK {id}: {ex}");
            return false;
        }
    }

    internal static void StopNativePlayback()
    {
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    internal static AudioSource? EnsureAudioSource()
    {
        if (IsUnityObjectAlive(_audioSource)) return _audioSource;
        _audioSource = null;
        if (!IsUnityObjectAlive(_audioRoot)) _audioRoot = null;
        _audioRoot = new GameObject("SporeVoiceOverrideAudioRoot");
        UnityEngine.Object.DontDestroyOnLoad(_audioRoot);
        _audioSource = _audioRoot.AddComponent<AudioSource>();
        _audioSource.volume = Volume;
        _audioSource.spatialBlend = 0f;
        _audioSource.playOnAwake = false;
        _audioSource.loop = false;
        ApplyObservedMixerGroup(_audioSource);
        WriteLog("AUDIO_ROOT_READY");
        return _audioSource;
    }

    internal static bool IsUnityObjectAlive(UnityEngine.Object? obj)
    {
        if (obj == null) return false;
        try
        {
            _ = obj.GetInstanceID();
            return true;
        }
        catch (Exception ex) when (IsIl2CppCollected(ex))
        {
            return false;
        }
    }

    internal static void StopUnityAudioSourceIfAlive()
    {
        try
        {
            if (IsUnityObjectAlive(_audioSource) && _audioSource!.isPlaying) _audioSource.Stop();
        }
        catch (Exception ex)
        {
            ResetAudioSourceIfCollected(ex);
        }
    }

    private static bool IsIl2CppCollected(Exception ex)
    {
        return ex.GetType().Name.Contains("ObjectCollected", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("garbage collected in IL2CPP", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetAudioSourceIfCollected(Exception ex)
    {
        if (!IsIl2CppCollected(ex)) return;
        _audioSource = null;
        _audioRoot = null;
        WriteLog("AUDIO_ROOT_RESET_AFTER_IL2CPP_COLLECTED");
    }

    internal static void ObserveAudioBus(object? obj)
    {
        if (IsUnityObjectAlive(_observedMixerGroup)) return;
        var group = FindMixerGroup(obj, 0, new HashSet<string>());
        if (!IsUnityObjectAlive(group)) return;
        _observedMixerGroup = group;
        _observedMixerGroupName = SafeUnityName(group);
        WriteLog($"UNITY_AUDIO_BUS_OBSERVED {_observedMixerGroupName}");
        if (IsUnityObjectAlive(_audioSource)) ApplyObservedMixerGroup(_audioSource!);
    }

    private static void ApplyObservedMixerGroup(AudioSource src)
    {
        if (!IsUnityObjectAlive(_observedMixerGroup))
        {
            WriteLog("AUDIO_SOURCE_BUS default-master");
            return;
        }

        try
        {
            src.outputAudioMixerGroup = _observedMixerGroup;
            WriteLog($"AUDIO_SOURCE_BUS {_observedMixerGroupName}");
        }
        catch (Exception ex)
        {
            WriteLog($"AUDIO_SOURCE_BUS_FAIL {ex.GetType().Name}: {ex.Message}");
            _observedMixerGroup = null;
            _observedMixerGroupName = "";
        }
    }

    private static AudioMixerGroup? FindMixerGroup(object? obj, int depth, HashSet<string> seen)
    {
        if (obj == null || depth > 1) return null;
        if (obj is AudioMixerGroup group && IsUnityObjectAlive(group)) return group;
        if (obj is AudioSource source)
        {
            try
            {
                var sourceGroup = source.outputAudioMixerGroup;
                if (IsUnityObjectAlive(sourceGroup)) return sourceGroup;
            }
            catch { }
        }

        var type = obj.GetType();
        var key = type.FullName ?? type.Name;
        if (depth > 0 && !seen.Add(key)) return null;

        foreach (var flags in new[] { BindingFlags.Public | BindingFlags.Instance, BindingFlags.NonPublic | BindingFlags.Instance })
        {
            foreach (var field in type.GetFields(flags))
            {
                if (depth > 0 && !LooksAudioMember(field.Name)) continue;
                object? value = null;
                try { value = field.GetValue(obj); } catch { }
                var found = FindMixerGroup(value, depth + 1, seen);
                if (IsUnityObjectAlive(found)) return found;
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.GetIndexParameters().Length != 0) continue;
                if (depth > 0 && !LooksAudioMember(prop.Name)) continue;
                object? value = null;
                try { value = prop.GetValue(obj); } catch { }
                var found = FindMixerGroup(value, depth + 1, seen);
                if (IsUnityObjectAlive(found)) return found;
            }
        }

        return null;
    }

    private static bool LooksAudioMember(string name)
    {
        return name.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mixer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voice", StringComparison.OrdinalIgnoreCase)
            || name.Contains("dialogue", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vo", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeUnityName(UnityEngine.Object? obj)
    {
        if (!IsUnityObjectAlive(obj)) return "<none>";
        try { return obj!.name ?? obj.GetType().Name; } catch { return obj!.GetType().Name; }
    }

    internal static AudioClip? LoadWavClipWithSetData(string path, string id)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || ReadAscii(bytes, 0, 4) != "RIFF" || ReadAscii(bytes, 8, 4) != "WAVE")
        {
            WriteLog($"WAV_FAIL {id}: not RIFF/WAVE");
            return null;
        }

        int pos = 12;
        int audioFormat = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        int dataOffset = -1, dataSize = 0;
        while (pos + 8 <= bytes.Length)
        {
            string chunk = ReadAscii(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            int next = pos + 8 + size + (size & 1);
            if (chunk == "fmt ")
            {
                audioFormat = BitConverter.ToInt16(bytes, pos + 8);
                channels = BitConverter.ToInt16(bytes, pos + 10);
                sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                bitsPerSample = BitConverter.ToInt16(bytes, pos + 22);
            }
            else if (chunk == "data")
            {
                dataOffset = pos + 8;
                dataSize = Math.Min(size, bytes.Length - dataOffset);
                break;
            }
            if (next <= pos) break;
            pos = next;
        }

        if (dataOffset < 0 || dataSize <= 0 || channels <= 0 || sampleRate <= 0)
        {
            WriteLog($"WAV_FAIL {id}: missing fmt/data");
            return null;
        }
        if (audioFormat != 1 && audioFormat != 3)
        {
            WriteLog($"WAV_FAIL {id}: unsupported format={audioFormat} bits={bitsPerSample}");
            return null;
        }

        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        int totalSamples = dataSize / bytesPerSample;
        int frames = totalSamples / channels;
        float[] samples = new float[frames * channels];
        int p = dataOffset;
        for (int i = 0; i < samples.Length && p + bytesPerSample <= bytes.Length; i++, p += bytesPerSample)
        {
            if (audioFormat == 3 && bitsPerSample == 32)
            {
                samples[i] = BitConverter.ToSingle(bytes, p);
            }
            else if (bitsPerSample == 16)
            {
                samples[i] = BitConverter.ToInt16(bytes, p) / 32768f;
            }
            else if (bitsPerSample == 24)
            {
                int v = bytes[p] | (bytes[p + 1] << 8) | (bytes[p + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                samples[i] = v / 8388608f;
            }
            else if (bitsPerSample == 32)
            {
                samples[i] = BitConverter.ToInt32(bytes, p) / 2147483648f;
            }
            else if (bitsPerSample == 8)
            {
                samples[i] = (bytes[p] - 128) / 128f;
            }
            else
            {
                WriteLog($"WAV_FAIL {id}: unsupported bits={bitsPerSample}");
                return null;
            }
        }

        WriteLog($"UNITY_PCM_PARSE {id} bytes={bytes.Length} frames={frames} samples={samples.Length} channels={channels} hz={sampleRate} bits={bitsPerSample} format={audioFormat}");
        try
        {
            var sampleArray = new Il2CppStructArray<float>(samples);
            _lastSampleArray = sampleArray; // root the IL2CPP array wrapper while Unity copies/plays the data

            var clip = new AudioClip();
            _lastClip = clip; // root the managed/IL2CPP wrapper while Unity plays it
            WriteLog($"UNITY_PCM_CLIP_CONSTRUCT {id} alive={IsUnityObjectAlive(clip)}");
            clip.CreateUserSound("override_" + id, frames, channels, sampleRate, false);
            WriteLog($"UNITY_PCM_CREATE_USER_SOUND {id} length={clip.length:0.000} samples={clip.samples} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay}");
            var ok = InvokeAudioClipSetData(clip, sampleArray, 0, samples.Length);
            WriteLog($"UNITY_PCM_SETDATA_OK {id} ok={ok} length={clip.length:0.000} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay}");
            GC.KeepAlive(sampleArray);
            GC.KeepAlive(clip);
            return clip;
        }
        catch (Exception ex)
        {
            WriteLog($"UNITY_PCM_SETDATA_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
            WriteLog($"UNITY_PCM_SETDATA_FAIL_STACK {id}: {ex}");
            return null;
        }
    }

    private static bool InvokeAudioClipSetData(AudioClip clip, Il2CppStructArray<float> samples, int offsetSamples, int count)
    {
        try
        {
            _audioClipStaticSetData ??= typeof(AudioClip)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "SetData") return false;
                    var p = m.GetParameters();
                    return p.Length == 4
                        && p[0].ParameterType == typeof(AudioClip)
                        && p[2].ParameterType == typeof(int)
                        && p[3].ParameterType == typeof(int);
                })
                ?? throw new MissingMethodException("AudioClip static SetData(AudioClip, Il2CppStructArray<float>, int, int) not found");

            var result = _audioClipStaticSetData.Invoke(null, new object?[] { clip, samples, offsetSamples, count });
            return result is bool ok && ok;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static string ReadAscii(byte[] b, int offset, int count)
    {
        return System.Text.Encoding.ASCII.GetString(b, offset, count);
    }

    internal static void WriteLog(string msg)
    {
        var line = $"[{DateTime.Now:O}] {msg}";
        try { Instance?.Log.LogInfo(msg); } catch { }
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    private static class Patch_PlayVO
    {
        public static bool Prefix(object __instance, string cardVOID)
        {
            VoiceOverridePlugin.TrackLatestDialogueId("PlayVO", cardVOID);
            return VoiceOverridePlugin.TryReplace("PlayVO", cardVOID, __instance);
        }
    }

    private static class Patch_StopVO
    {
        public static void Prefix()
        {
            VoiceOverridePlugin.StopOverridePlaybackFromGame("StopVO");
        }
    }

    private static class Patch_FireVOEvent
    {
        public static bool Prefix(object currentCard)
        {
            VoiceOverridePlugin.TrackLatestDialogueCard("FireVOEvent", currentCard);
            var id = VoiceOverridePlugin.CardIdFromRtCard(currentCard);
            return VoiceOverridePlugin.TryReplace("FireVOEvent", id);
        }
    }

    private static class Patch_FireStopVOEvent
    {
        public static void Prefix()
        {
            VoiceOverridePlugin.StopOverridePlaybackFromGame("FireStopVOEvent");
        }
    }

  private static class Patch_BeforeUpdateConversation
  {
      public static void Prefix(object __instance, object __0)
      {
          VoiceOverridePlugin.TryPlayCardShownFallback("BeforeUpdateConversation", __0, __instance);
      }
  }

  private sealed class InstalledVoicePackState
  {
      public string Name = "";
      public string DisplayName = "";
      public string Destination = "";
      public string Format = "";
      public string PackageUrl = "";
      public string UpdateUrl = "";
      public string Etag = "";
      public string LastModified = "";
      public long ContentLength = -1;
      public string ManifestHash = "";
      public string ManifestVersion = "";
      public Dictionary<string, string> ShardSha256 = new(StringComparer.OrdinalIgnoreCase);
  }

  private sealed class RemoteVoicePackMetadata
  {
      public string Etag = "";
      public string LastModified = "";
      public long ContentLength = -1;
      public string ManifestHash = "";
      public string ManifestVersion = "";
      public string UpdateMessage = "";
      public long FileCount = -1;
      public long ShardCount = -1;
      public long TotalBytes = -1;
  }

  private sealed class VoicePackUpdateInfo
  {
      public string Name = "";
      public string DisplayName = "";
      public string Message = "";
  }

  private sealed class VoicePackManifest
  {
      public string Name = "";
      public string DisplayName = "";
      public string Destination = "";
      public string Format = "";
      public string ManifestHash = "";
      public string Version = "";
      public string UpdateMessage = "";
      public string ManifestUrl = "";
      public long FileCount = -1;
      public long ShardCount = -1;
      public long TotalBytes = -1;
      public string RawJson = "";
      public RemoteVoicePackMetadata Metadata = new();
      public List<VoicePackShard> Shards = new();
      public Dictionary<string, List<string>> FilesByShard = new(StringComparer.OrdinalIgnoreCase);
      public HashSet<string> ManagedRelativePaths = new(StringComparer.OrdinalIgnoreCase);
  }

  private sealed class VoicePackShard
  {
      public string Name = "";
      public string Path = "";
      public string Url = "";
      public string Sha256 = "";
      public long Size = -1;
      public long FileCount = -1;
  }

  private sealed class VoicePackShardInstallItem
  {
      public VoicePackShard Shard = new();
      public string Archive = "";
      public bool NeedsDownload;
  }

  private sealed class VoicePackInstallResult
  {
      public int ChangedShards;
      public int DownloadedShards;
      public int PrunedFiles;
      public int AudioFiles;
  }
}

public sealed class VoiceOverrideRunner : MonoBehaviour
{
    private readonly Queue<PendingAudio> _pending = new();
    private readonly List<DelayedAudio> _delayed = new();
    private ActiveLoad? _active;
    private ClipLoad? _clipLoad;
    private UnityWebRequest? _playingRequest;
    private AudioClip? _playingClip;
    private FMOD.Sound _fmodSound;
    private FMOD.Channel _fmodChannel;
    private bool _hasFmodSound;
    private bool _hasFmodChannel;
    private DateTime _fmodReleaseAtUtc;
    private string _fmodCurrentId = "";

    public VoiceOverrideRunner(IntPtr ptr) : base(ptr)
    {
    }

    public void Awake()
    {
        try
        {
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_RUNNER_AWAKE_FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool QueueUnityAudio(string file, string id)
    {
        if (!File.Exists(file))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_QUEUE_FAIL {id}: file missing {file}");
            return false;
        }

        if (!TryGetAudioType(file, out _))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_QUEUE_FAIL {id}: unsupported extension {Path.GetExtension(file)}");
            return false;
        }

        try
        {
            StopCurrent();
            _pending.Clear();
            _delayed.Clear();
            _pending.Enqueue(new PendingAudio(id, file));
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_QUEUED {id} {file}");
            return true;
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_QUEUE_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_QUEUE_FAIL_STACK {id}: {ex}");
            return false;
        }
    }

    public bool QueueDelayedUnityAudio(string file, string id, int delayMs)
    {
        if (!File.Exists(file))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAY_QUEUE_FAIL {id}: file missing {file}");
            return false;
        }

        if (!TryGetAudioType(file, out _))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAY_QUEUE_FAIL {id}: unsupported extension {Path.GetExtension(file)}");
            return false;
        }

        try
        {
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                if (_delayed[i].Pending.Id == id) _delayed.RemoveAt(i);
            }

            _delayed.Add(new DelayedAudio(new PendingAudio(id, file), DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMs))));
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAYED_QUEUED {id} delayMs={delayMs} {file}");
            return true;
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAY_QUEUE_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAY_QUEUE_FAIL_STACK {id}: {ex}");
            return false;
        }
    }

    public void StopPlaybackForCardAdvance(string previousId, string nextId)
    {
        if (string.IsNullOrWhiteSpace(previousId) || string.Equals(previousId, nextId, StringComparison.Ordinal)) return;
        var hadPlayback = HasOverridePlayback();
        StopCurrent();
        if (hadPlayback) VoiceOverridePlugin.WriteLog($"CARD_ADVANCE_STOP {previousId} -> {nextId}");
    }

    public void StopPlaybackFromGame(string source)
    {
        var hadPlayback = HasOverridePlayback();
        StopCurrent();
        if (hadPlayback) VoiceOverridePlugin.WriteLog($"{source} STOP_OVERRIDE_PLAYBACK");
    }

    public void Update()
    {
      try
      {
          VoiceOverridePlugin.PollRuntimeHotkeys();
          VoiceOverridePlugin.PollVoicePackUpdateNotifications();
          CleanupFinishedPlayback();
            PollClipLoad();
            PollDelayedAudio();
            if (_active == null && _clipLoad == null) StartNext();
            PollActive();
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_UPDATE_FAIL {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_UPDATE_FAIL_STACK {ex}");
        }
    }

    public void OnGUI()
    {
        VoiceOverridePlugin.DrawToast();
    }

    private bool HasOverridePlayback()
    {
        return _pending.Count > 0
            || _delayed.Count > 0
            || _active != null
            || _clipLoad != null
            || _playingRequest != null
            || VoiceOverridePlugin.IsUnityObjectAlive(_playingClip)
            || _hasFmodSound
            || _hasFmodChannel;
    }

    private void PollDelayedAudio()
    {
        if (_delayed.Count == 0 || _pending.Count > 0 || _active != null || _clipLoad != null) return;

        var now = DateTime.UtcNow;
        for (int i = 0; i < _delayed.Count; i++)
        {
            if (_delayed[i].DueUtc > now) continue;

            var pending = _delayed[i].Pending;
            _delayed.RemoveAt(i);
            StopCurrent(clearDelayed: false);
            _pending.Clear();
            _pending.Enqueue(pending);
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_DELAYED_DUE {pending.Id} {pending.File}");
            return;
        }
    }

    private void StartNext()
    {
        if (_pending.Count == 0) return;

        var pending = _pending.Dequeue();
        if (!TryGetAudioType(pending.File, out var audioType))
        {
            FallbackNative(pending, "unsupported extension");
            return;
        }

        if (audioType == AudioType.WAV || audioType == AudioType.OGGVORBIS)
        {
            if (TryPlayFmodExternal(pending)) return;
            if (audioType == AudioType.WAV)
            {
                FallbackNative(pending, "fmod failed");
                return;
            }
        }

        try
        {
            var uri = new Uri(Path.GetFullPath(pending.File)).AbsoluteUri;
            var request = UnityWebRequest.Get(uri);
            request.disposeDownloadHandlerOnDispose = true;
            var operation = request.SendWebRequest();
            _active = new ActiveLoad(pending, request, operation, audioType);
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_REQUEST_START {pending.Id} {uri} mode=buffer-www");
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_REQUEST_FAIL {pending.Id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_REQUEST_FAIL_STACK {pending.Id}: {ex}");
            FallbackNative(pending, ex.GetType().Name);
        }
    }

    private bool TryPlayFmodExternal(PendingAudio pending)
    {
        try
        {
            StopFmodPlayback("replace");
            VoiceOverridePlugin.StopNativePlayback();

            var core = FMODUnity.RuntimeManager.CoreSystem;
            if (!TryGetFmodChannelGroup(pending.Id, out var group, out var groupName))
            {
                VoiceOverridePlugin.WriteLog($"FMOD_BUS_FAIL {pending.Id}: no channel group");
                return false;
            }

            var loadMode = VoiceOverridePlugin.UseFmodStreaming ? FMOD.MODE.CREATESTREAM : FMOD.MODE.CREATESAMPLE;
            var mode = FMOD.MODE.DEFAULT | FMOD.MODE.LOOP_OFF | FMOD.MODE._2D | loadMode;
            var sound = default(FMOD.Sound);
            var createResult = core.createSound(pending.File, mode, out sound);
            VoiceOverridePlugin.WriteLog($"FMOD_CREATE_SOUND {pending.Id} result={createResult} loadMode={(VoiceOverridePlugin.UseFmodStreaming ? "stream" : "sample")} mode={mode} file={pending.File}");
            if (createResult != FMOD.RESULT.OK) return false;

            uint lengthMs = 0;
            var lengthResult = sound.getLength(out lengthMs, FMOD.TIMEUNIT.MS);
            VoiceOverridePlugin.WriteLog($"FMOD_SOUND_INFO {pending.Id} lengthMs={lengthMs} lengthResult={lengthResult}");

            var channel = default(FMOD.Channel);
            var playResult = core.playSound(sound, group, true, out channel);
            if (playResult != FMOD.RESULT.OK)
            {
                VoiceOverridePlugin.WriteLog($"FMOD_PLAY_FAIL {pending.Id}: {playResult}");
                sound.release();
                return false;
            }

            var volumeResult = channel.setVolume(VoiceOverridePlugin.Volume);
            var pauseResult = channel.setPaused(false);
            bool playing = false;
            var playingResult = channel.isPlaying(out playing);
            VoiceOverridePlugin.WriteLog($"PLAYING_FMOD_AUDIO {pending.Id} bus={groupName} play={playResult} volume={volumeResult} pause={pauseResult} playing={playing} playingResult={playingResult} releaseAfterMs={Math.Max(1000, lengthMs) + 1000}");

            _fmodSound = sound;
            _fmodChannel = channel;
            _hasFmodSound = true;
            _hasFmodChannel = true;
            _fmodCurrentId = pending.Id;
            _fmodReleaseAtUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, lengthMs) + 1000);
            return playResult == FMOD.RESULT.OK;
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"FMOD_PLAY_EXCEPTION {pending.Id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"FMOD_PLAY_EXCEPTION_STACK {pending.Id}: {ex}");
            StopFmodPlayback("exception");
            return false;
        }
    }

    private static bool TryGetFmodChannelGroup(string id, out FMOD.ChannelGroup group, out string groupName)
    {
        foreach (var busPath in new[]
        {
            "bus:/",
            "bus:/VO",
            "bus:/Voice",
            "bus:/Dialogue",
            "bus:/Dialog",
            "bus:/SFX/VO",
            "bus:/SFX/Dialogue",
            "bus:/SFX"
        })
        {
            try
            {
                var bus = FMODUnity.RuntimeManager.GetBus(busPath);
                if (!bus.isValid())
                {
                    VoiceOverridePlugin.WriteLog($"FMOD_BUS_MISS {id} {busPath}: invalid");
                    continue;
                }

                group = default;
                var result = bus.getChannelGroup(out group);
                VoiceOverridePlugin.WriteLog($"FMOD_BUS_CANDIDATE {id} {busPath}: {result}");
                if (result == FMOD.RESULT.OK)
                {
                    groupName = busPath;
                    return true;
                }
            }
            catch (Exception ex)
            {
                VoiceOverridePlugin.WriteLog($"FMOD_BUS_MISS {id} {busPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            var core = FMODUnity.RuntimeManager.CoreSystem;
            group = default;
            var result = core.getMasterChannelGroup(out group);
            VoiceOverridePlugin.WriteLog($"FMOD_BUS_CANDIDATE {id} core-master: {result}");
            if (result == FMOD.RESULT.OK)
            {
                groupName = "core-master";
                return true;
            }
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"FMOD_BUS_MISS {id} core-master: {ex.GetType().Name}: {ex.Message}");
        }

        group = default;
        groupName = "";
        return false;
    }

    private bool TryPlayPcmWav(PendingAudio pending)
    {
        try
        {
            var clip = VoiceOverridePlugin.LoadWavClipWithSetData(pending.File, pending.Id);
            if (!VoiceOverridePlugin.IsUnityObjectAlive(clip))
            {
                VoiceOverridePlugin.WriteLog($"UNITY_PCM_CLIP_FAIL {pending.Id}: no clip");
                return false;
            }

            if (clip!.length <= 0f || clip.channels <= 0 || clip.frequency <= 0)
            {
                VoiceOverridePlugin.WriteLog($"UNITY_PCM_CLIP_INVALID {pending.Id} length={clip.length:0.000} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay}");
                return false;
            }

            return PlayPcmClip(pending, clip);
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_PCM_PLAY_FAIL {pending.Id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_PCM_PLAY_FAIL_STACK {pending.Id}: {ex}");
            return false;
        }
    }

    private void PollActive()
    {
        var active = _active;
        if (active == null) return;
        if (!active.Request.isDone)
        {
            if ((DateTime.UtcNow - active.StartedUtc).TotalSeconds > 10)
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_REQUEST_TIMEOUT {active.Pending.Id}");
                DisposeRequest(active.Request);
                _active = null;
                FallbackNative(active.Pending, "timeout");
            }
            return;
        }

        _active = null;
        try
        {
            var failure = GetRequestFailure(active.Request);
            if (failure != null)
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_REQUEST_FAIL {active.Pending.Id}: {failure}");
                FallbackNative(active.Pending, failure);
                return;
            }

            var bytes = GetDownloadedBytes(active.Request);
            var clip = CreateClipFromDownloadedData(active, out var clipMode);
            if (clip == null || !VoiceOverridePlugin.IsUnityObjectAlive(clip))
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_FAIL {active.Pending.Id}: no clip");
                FallbackNative(active.Pending, "no clip");
                return;
            }

            bool loadStarted = false;
            try { loadStarted = clip.LoadAudioData(); } catch (Exception ex) { VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_LOAD_START_FAIL {active.Pending.Id}: {ex.GetType().Name}: {ex.Message}"); }
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_CREATED {active.Pending.Id} mode={clipMode} bytes={bytes} length={clip.length:0.000} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay} loadStarted={loadStarted}");

            if (IsZeroPlaceholderClip(clip))
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_INVALID_IMMEDIATE {active.Pending.Id} mode={clipMode}");
                FallbackNative(active.Pending, "zero/invalid clip");
                DisposeRequest(active.Request);
                return;
            }

            _clipLoad = new ClipLoad(active.Pending, active.Request, clip);
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_PLAY_FAIL {active.Pending.Id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_PLAY_FAIL_STACK {active.Pending.Id}: {ex}");
            FallbackNative(active.Pending, ex.GetType().Name);
        }
    }

    private static AudioClip? CreateClipFromDownloadedData(ActiveLoad active, out string mode)
    {
        AudioClip? lastClip = null;
        mode = "none";
        foreach (var candidate in new[]
        {
            new ClipCreateMode("decompress", Stream: false, Compressed: false),
            new ClipCreateMode("compressed", Stream: false, Compressed: true),
            new ClipCreateMode("stream", Stream: true, Compressed: false),
            new ClipCreateMode("stream-compressed", Stream: true, Compressed: true),
        })
        {
            try
            {
                var clip = WebRequestWWW.InternalCreateAudioClipUsingDH(
                    active.Request.downloadHandler,
                    active.Request.url,
                    candidate.Stream,
                    candidate.Compressed,
                    active.AudioType);
                lastClip = clip;
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_VARIANT {active.Pending.Id} mode={candidate.Name} length={clip.length:0.000} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay}");
                if (!IsZeroPlaceholderClip(clip))
                {
                    mode = candidate.Name;
                    return clip;
                }
            }
            catch (Exception ex)
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_VARIANT_FAIL {active.Pending.Id} mode={candidate.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        mode = "zero-placeholder";
        return lastClip;
    }

    private void PollClipLoad()
    {
        var load = _clipLoad;
        if (load == null) return;

        try
        {
            if (!VoiceOverridePlugin.IsUnityObjectAlive(load.Clip))
            {
                FinishClipLoad(load, fallbackReason: "clip collected");
                return;
            }

            var state = load.Clip.loadState;
            if (IsPlayableClip(load.Clip))
            {
                if (PlayLoadedClip(load)) FinishClipLoad(load, fallbackReason: null, disposeRequest: false);
                else FinishClipLoad(load, fallbackReason: "play failed");
                return;
            }

            if (state == AudioDataLoadState.Failed)
            {
                FinishClipLoad(load, fallbackReason: "loadState Failed");
                return;
            }

            if ((DateTime.UtcNow - load.StartedUtc).TotalSeconds > 3)
            {
                VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_TIMEOUT {load.Pending.Id} length={load.Clip.length:0.000} channels={load.Clip.channels} hz={load.Clip.frequency} state={state} ready={load.Clip.isReadyToPlay}");
                FinishClipLoad(load, fallbackReason: "zero/invalid clip");
            }
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_POLL_FAIL {load.Pending.Id}: {ex.GetType().Name}: {ex.Message}");
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_CLIP_POLL_FAIL_STACK {load.Pending.Id}: {ex}");
            FinishClipLoad(load, fallbackReason: ex.GetType().Name);
        }
    }

    private bool PlayLoadedClip(ClipLoad load)
    {
        var source = VoiceOverridePlugin.EnsureAudioSource();
        if (!VoiceOverridePlugin.IsUnityObjectAlive(source))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_PLAY_FAIL {load.Pending.Id}: no AudioSource");
            return false;
        }

        if (source!.isPlaying) source.Stop();
        source.clip = load.Clip;
        source.volume = VoiceOverridePlugin.Volume;
        source.spatialBlend = 0f;
        source.playOnAwake = false;
        source.loop = false;
        source.Play();
        var playing = source.isPlaying;
        VoiceOverridePlugin.WriteLog($"PLAYING_UNITY_AUDIO {load.Pending.Id} length={load.Clip.length:0.000} channels={load.Clip.channels} hz={load.Clip.frequency} state={load.Clip.loadState} ready={load.Clip.isReadyToPlay} sourcePlaying={playing}");
        if (!playing)
        {
            source.clip = null;
            return false;
        }

        _playingRequest = load.Request;
        _playingClip = load.Clip;
        return true;
    }

    private bool PlayPcmClip(PendingAudio pending, AudioClip clip)
    {
        var source = VoiceOverridePlugin.EnsureAudioSource();
        if (!VoiceOverridePlugin.IsUnityObjectAlive(source))
        {
            VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_PLAY_FAIL {pending.Id}: no AudioSource");
            return false;
        }

        if (source!.isPlaying) source.Stop();
        source.clip = clip;
        source.volume = VoiceOverridePlugin.Volume;
        source.spatialBlend = 0f;
        source.playOnAwake = false;
        source.loop = false;
        source.Play();
        var playing = source.isPlaying;
        VoiceOverridePlugin.WriteLog($"PLAYING_UNITY_AUDIO {pending.Id} mode=pcm-setdata length={clip.length:0.000} channels={clip.channels} hz={clip.frequency} state={clip.loadState} ready={clip.isReadyToPlay} sourcePlaying={playing}");
        if (!playing)
        {
            source.clip = null;
            return false;
        }

        _playingRequest = null;
        _playingClip = clip;
        return true;
    }

    private void FinishClipLoad(ClipLoad load, string? fallbackReason, bool disposeRequest = true)
    {
        if (_clipLoad == load) _clipLoad = null;
        if (fallbackReason != null) FallbackNative(load.Pending, fallbackReason);
        if (disposeRequest) DisposeRequest(load.Request);
    }

    private static bool IsPlayableClip(AudioClip clip)
    {
        return clip.length > 0f
            && clip.channels > 0
            && clip.frequency > 0
            && clip.loadState == AudioDataLoadState.Loaded
            && clip.isReadyToPlay;
    }

    private static bool IsZeroPlaceholderClip(AudioClip clip)
    {
        return clip.length <= 0f
            && clip.channels <= 0
            && clip.frequency <= 0
            && clip.loadState == AudioDataLoadState.Unloaded;
    }

    private static ulong GetDownloadedBytes(UnityWebRequest request)
    {
        try { return request.downloadedBytes; } catch { return 0; }
    }

    private void StopCurrent(bool clearDelayed = true)
    {
        if (clearDelayed) _delayed.Clear();
        VoiceOverridePlugin.StopNativePlayback();
        StopFmodPlayback("stop-current");
        VoiceOverridePlugin.StopUnityAudioSourceIfAlive();
        if (_active != null)
        {
            DisposeRequest(_active.Request);
            _active = null;
        }
        if (_clipLoad != null)
        {
            DisposeRequest(_clipLoad.Request);
            _clipLoad = null;
        }
        DisposeRequest(_playingRequest);
        _playingRequest = null;
        _playingClip = null;
    }

    private void CleanupFinishedPlayback()
    {
        CleanupFmodPlayback();
        if (!VoiceOverridePlugin.IsUnityObjectAlive(_playingClip)) return;
        var source = VoiceOverridePlugin.EnsureAudioSource();
        if (VoiceOverridePlugin.IsUnityObjectAlive(source) && source!.isPlaying) return;
        DisposeRequest(_playingRequest);
        _playingRequest = null;
        _playingClip = null;
    }

    private void CleanupFmodPlayback()
    {
        if (!_hasFmodSound && !_hasFmodChannel) return;
        if (DateTime.UtcNow < _fmodReleaseAtUtc) return;

        bool playing = false;
        var result = FMOD.RESULT.ERR_INVALID_HANDLE;
        try
        {
            if (_hasFmodChannel) result = _fmodChannel.isPlaying(out playing);
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"FMOD_CLEANUP_POLL_FAIL {ex.GetType().Name}: {ex.Message}");
        }

        if (result == FMOD.RESULT.OK && playing) return;
        StopFmodPlayback($"cleanup id={_fmodCurrentId} result={result} playing={playing}");
    }

    private void StopFmodPlayback(string reason)
    {
        if (!_hasFmodSound && !_hasFmodChannel) return;

        try
        {
            if (_hasFmodChannel)
            {
                var stopResult = _fmodChannel.stop();
                VoiceOverridePlugin.WriteLog($"FMOD_CHANNEL_STOP {reason}: {stopResult}");
            }
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"FMOD_CHANNEL_STOP_FAIL {reason}: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (_hasFmodSound)
            {
                var releaseResult = _fmodSound.release();
                VoiceOverridePlugin.WriteLog($"FMOD_SOUND_RELEASE {reason}: {releaseResult}");
            }
        }
        catch (Exception ex)
        {
            VoiceOverridePlugin.WriteLog($"FMOD_SOUND_RELEASE_FAIL {reason}: {ex.GetType().Name}: {ex.Message}");
        }

        _fmodSound = default;
        _fmodChannel = default;
        _hasFmodSound = false;
        _hasFmodChannel = false;
        _fmodReleaseAtUtc = DateTime.MinValue;
        _fmodCurrentId = "";
    }

    private static bool TryGetAudioType(string file, out AudioType audioType)
    {
        var ext = Path.GetExtension(file);
        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            audioType = AudioType.WAV;
            return true;
        }
        if (string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase))
        {
            audioType = AudioType.OGGVORBIS;
            return true;
        }

        audioType = AudioType.UNKNOWN;
        return false;
    }

    private static string? GetRequestFailure(UnityWebRequest request)
    {
        try
        {
            if (request.result != UnityWebRequest.Result.Success)
            {
                return $"{request.result}: {request.error}";
            }
            return null;
        }
        catch
        {
            try
            {
                if (request.isNetworkError || request.isHttpError) return request.error;
                return null;
            }
            catch (Exception ex)
            {
                return $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private static void FallbackNative(PendingAudio pending, string reason)
    {
        if (!string.Equals(Path.GetExtension(pending.File), ".wav", StringComparison.OrdinalIgnoreCase)) return;
        VoiceOverridePlugin.WriteLog($"UNITY_AUDIO_NATIVE_FALLBACK {pending.Id}: {reason}");
        VoiceOverridePlugin.PlayWavNative(pending.File, pending.Id);
    }

    private static void DisposeRequest(UnityWebRequest? request)
    {
        if (request == null) return;
        try { request.Dispose(); } catch { }
    }

    private readonly record struct PendingAudio(string Id, string File);

    private readonly record struct DelayedAudio(PendingAudio Pending, DateTime DueUtc);

    private readonly record struct ClipCreateMode(string Name, bool Stream, bool Compressed);

    private sealed class ActiveLoad
    {
        public ActiveLoad(PendingAudio pending, UnityWebRequest request, UnityWebRequestAsyncOperation operation, AudioType audioType)
        {
            Pending = pending;
            Request = request;
            Operation = operation;
            AudioType = audioType;
            StartedUtc = DateTime.UtcNow;
        }

        public PendingAudio Pending { get; }
        public UnityWebRequest Request { get; }
        public UnityWebRequestAsyncOperation Operation { get; }
        public AudioType AudioType { get; }
        public DateTime StartedUtc { get; }
    }

    private sealed class ClipLoad
    {
        public ClipLoad(PendingAudio pending, UnityWebRequest request, AudioClip clip)
        {
            Pending = pending;
            Request = request;
            Clip = clip;
            StartedUtc = DateTime.UtcNow;
        }

        public PendingAudio Pending { get; }
        public UnityWebRequest Request { get; }
        public AudioClip Clip { get; }
        public DateTime StartedUtc { get; }
    }
}
