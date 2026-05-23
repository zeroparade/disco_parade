using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;

[BepInPlugin("spore.zeroparades.voiceoverride", "ZERO PARADES Voice Override", "0.3.8")]
public class VoiceOverridePlugin : BasePlugin
{
    internal static VoiceOverridePlugin? Instance;
    internal static string LogPath = "";
    internal static string OverrideRoot = "";
    internal static float Volume = 1.0f;
    internal static bool OverrideEnabled = true;
    internal static bool OriginalVoiceEnabledForOverrides = false;
    internal static bool AllowOriginalOnOverrideFailure = true;
    private static readonly HashSet<string> _silentCardIds = new(StringComparer.Ordinal);
    private static bool _silentCardIndexLoaded;
    private static ConfigEntry<bool>? _overrideEnabledEntry;
    private static ConfigEntry<bool>? _originalVoiceEnabledEntry;
    private static ConfigEntry<bool>? _allowOriginalOnOverrideFailureEntry;
    private static ConfigEntry<string>? _overrideProfileEntry;
    private static ConfigEntry<string>? _maleOverrideRootEntry;
    private static ConfigEntry<string>? _femaleOverrideRootEntry;
    private static ConfigEntry<KeyCode>? _toggleOverrideKeyEntry;
    private static ConfigEntry<KeyCode>? _cycleProfileKeyEntry;
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
    private static string _cardShownAppearanceId = "";
    private static int _cardShownAppearanceStopGeneration = -1;
    private static bool _cardShownAppearanceQueuedOrPlayed;
    private static string _lastShownCardId = "";
    private static DateTime _lastShownCardUtc = DateTime.MinValue;
    private const int ImmediateDuplicateSuppressMs = 500;
    private const int CardShownFallbackDelayMs = 175;
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
        Directory.CreateDirectory(OverrideRoot);
        LogPath = Path.Combine(logDir, "voice-override.log");
        File.AppendAllText(LogPath, $"\n=== ZERO PARADES Voice Override v0.3.8 loaded {DateTime.Now:O} ===\n");
        WriteLog($"OverrideRoot={OverrideRoot}");
        WriteLog($"OPTIONS overrideEnabled={OverrideEnabled} originalVoiceWithOverride={OriginalVoiceEnabledForOverrides} allowOriginalOnFailure={AllowOriginalOnOverrideFailure} profile={_overrideProfile} toggleOverride={GetConfiguredKeyName(_toggleOverrideKeyEntry)} cycleProfile={GetConfiguredKeyName(_cycleProfileKeyEntry)}");
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
        WriteLog("PLUGIN_READY v0.3.8");
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
            "If true, external override WAV/OGG files are played when present.");
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
        _overrideProfileEntry = Config.Bind(
            "Profiles",
            "OverrideProfile",
            "male",
            "Active override profile. Supported values: male, female.");
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
        _toggleOverrideKeyEntry = Config.Bind(
            "Hotkeys",
            "ToggleOverrideKey",
            KeyCode.F1,
            "Runtime hotkey for toggling override playback. Set to None to disable.");
        _cycleProfileKeyEntry = Config.Bind(
            "Hotkeys",
            "CycleProfileKey",
            KeyCode.F2,
            "Runtime hotkey for switching between male and female override profiles. Set to None to disable.");

        if (_toggleOverrideKeyEntry.Value == KeyCode.F8) _toggleOverrideKeyEntry.Value = KeyCode.F1;
        if (_cycleProfileKeyEntry.Value == KeyCode.F10) _cycleProfileKeyEntry.Value = KeyCode.F2;

        OverrideEnabled = _overrideEnabledEntry.Value;
        OriginalVoiceEnabledForOverrides = _originalVoiceEnabledEntry.Value;
        AllowOriginalOnOverrideFailure = _allowOriginalOnOverrideFailureEntry.Value;
        _overrideProfile = NormalizeProfile(_overrideProfileEntry.Value);
        _overrideProfileEntry.Value = _overrideProfile;
    }

    private static string NormalizeProfile(string? profile)
    {
        if (string.Equals(profile, "female", StringComparison.OrdinalIgnoreCase)) return "female";
        return "male";
    }

    private static void ResolveOverrideRoot()
    {
        _overrideProfile = NormalizeProfile(_overrideProfileEntry?.Value);
        var configured = _overrideProfile == "female"
            ? _femaleOverrideRootEntry?.Value
            : _maleOverrideRootEntry?.Value;
        var fallback = _overrideProfile == "female" ? "voice-overrides-female" : "voice-overrides";
        OverrideRoot = ResolveBepInExPath(configured, fallback);
        try { Directory.CreateDirectory(OverrideRoot); } catch { }
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

    internal static string? FindOverrideFile(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var safe = id.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        foreach (var ext in new[] { ".wav", ".ogg" })
        {
            var direct = Path.Combine(OverrideRoot, safe + ext);
            if (File.Exists(direct)) return direct;
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(OverrideRoot, safe + ".wav", SearchOption.AllDirectories)) return f;
            foreach (var f in Directory.EnumerateFiles(OverrideRoot, safe + ".ogg", SearchOption.AllDirectories)) return f;
        }
        catch (Exception ex)
        {
            WriteLog($"ENUM_FAIL {id}: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static void LoadSilentCardIndex()
    {
        _silentCardIds.Clear();
        _silentCardIndexLoaded = false;

        var candidates = new[]
        {
            Path.Combine(OverrideRoot, "_silent-card-ids.txt"),
            Path.Combine(Paths.BepInExRootPath, "voice-overrides", "_silent-card-ids.txt"),
            Path.Combine(Paths.BepInExRootPath, "voice-overrides-silent-card-ids.txt"),
        }.Distinct().ToArray();

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
                return;
            }
            catch (Exception ex)
            {
                WriteLog($"SILENT_CARD_INDEX_LOAD_FAIL {path}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        WriteLog("SILENT_CARD_INDEX_MISSING fallback disabled");
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
        var file = FindOverrideFile(id);
        if (file == null)
        {
            WriteLog($"{source} NO_OVERRIDE {id}");
            return true;
        }

        if (!OverrideEnabled)
        {
            WriteLog($"{source} OVERRIDE_DISABLED_ALLOW_ORIGINAL {id}");
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
            WriteLog(OriginalVoiceEnabledForOverrides
                ? $"{source} ALLOW_ORIGINAL {id}"
                : $"{source} SKIP_ORIGINAL {id}");
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
            if (!OverrideEnabled) return;

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

            if (!IsSilentCardFallbackAllowed(id))
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

            var file = FindOverrideFile(id);
            if (file == null) return;

            _recentCardShownQueuesUtc[id] = now;
            _cardShownAppearanceQueuedOrPlayed = true;
            if (_runner != null && _runner.QueueDelayedUnityAudio(file, id, CardShownFallbackDelayMs))
            {
                WriteLog($"{source} DELAYED_OVERRIDE {id} {file}");
            }
            else if (string.Equals(Path.GetExtension(file), ".wav", StringComparison.OrdinalIgnoreCase))
            {
                WriteLog($"{source} DELAYED_FALLBACK_NATIVE {id} {file}");
                PlayWavNative(file, id);
                MarkImmediateOverride(id);
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
        if (WasKeyPressed(_toggleOverrideKeyEntry))
        {
            SetOverrideEnabled(!OverrideEnabled, "HOTKEY");
        }

        if (WasKeyPressed(_cycleProfileKeyEntry))
        {
            SetOverrideProfile(_overrideProfile == "female" ? "male" : "female", "HOTKEY");
        }
    }

    private static bool WasKeyPressed(ConfigEntry<KeyCode>? entry)
    {
        if (entry == null || entry.Value == KeyCode.None) return false;
        try { return Input.GetKeyDown(entry.Value); }
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

    private static void SetOverrideProfile(string profile, string source)
    {
        var normalized = NormalizeProfile(profile);
        if (_overrideProfileEntry != null) _overrideProfileEntry.Value = normalized;
        _overrideProfile = normalized;
        ResolveOverrideRoot();
        LoadSilentCardIndex();
        _runner?.StopPlaybackFromGame($"{source}_PROFILE_SWITCH");
        SaveConfig();
        WriteLog($"OPTION_OVERRIDE_PROFILE {normalized} root={OverrideRoot} source={source}");
        ShowToast($"Voice profile: {normalized.ToUpperInvariant()}");
    }

    internal static void ShowToast(string message)
    {
        _toastMessage = message;
        try { _toastUntilRealtime = Time.realtimeSinceStartup + 2.25f; }
        catch { _toastUntilRealtime = 0f; }
        WriteLog($"TOAST {message}");
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

            var width = Math.Min(520f, Math.Max(260f, Screen.width - 48f));
            const float height = 42f;
            var rect = new Rect((Screen.width - width) * 0.5f, 28f, width, height);
            GUI.Box(rect, _toastMessage);
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

            var mode = FMOD.MODE.DEFAULT | FMOD.MODE.LOOP_OFF | FMOD.MODE._2D | FMOD.MODE.CREATESAMPLE;
            var sound = default(FMOD.Sound);
            var createResult = core.createSound(pending.File, mode, out sound);
            VoiceOverridePlugin.WriteLog($"FMOD_CREATE_SOUND {pending.Id} result={createResult} mode={mode} file={pending.File}");
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
