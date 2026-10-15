using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using BgmHotkey;

internal static class ProjectRegressionHarness
{
    private static Assembly Player;
    private static string Root;
    private static string Library;
    private static int Checks;
    private static readonly List<object> Events = new List<object>();
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Type T(string name) { return Player.GetType("BgmHotkey." + name, true); }
    private static object Call(Type type, object target, string method, params object[] args)
    {
        MethodInfo candidate = null;
        foreach (MethodInfo info in type.GetMethods(Flags))
            if (info.Name == method && info.GetParameters().Length == args.Length) { candidate = info; break; }
        if (candidate == null) throw new MissingMethodException(type.FullName, method);
        return candidate.Invoke(target, args);
    }
    private static object Field(object value, string name) { return value.GetType().GetField(name, Flags).GetValue(value); }
    private static object Property(object value, string name) { return value.GetType().GetProperty(name, Flags).GetValue(value, null); }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Checks++;
        Console.WriteLine("PASS: " + message);
    }
    private static AppSettings EmptySettings()
    {
        AppSettings settings = new AppSettings { Tracks = new SavedTrack[0], Bindings = new TrackBinding[0] };
        settings.Normalize();
        return settings;
    }
    private static object BareForm(AppSettings settings)
    {
        object form = FormatterServices.GetUninitializedObject(T("MainMenuForm"));
        T("MainMenuForm").GetField("_settings", Flags).SetValue(form, settings);
        T("MainMenuForm").GetField("_shuffleBag", Flags).SetValue(form, Activator.CreateInstance(T("RandomShuffleBag"), true));
        return form;
    }
    private static string Hash(string file)
    {
        using (SHA256 sha = SHA256.Create()) using (Stream stream = File.OpenRead(file))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void WriteWave(string file, double amplitude)
    {
        int frames = 44100 * 2;
        using (BinaryWriter writer = new BinaryWriter(File.Create(file)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 4);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
            writer.Write(44100); writer.Write(176400); writer.Write((short)4); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * 4);
            for (int i = 0; i < frames; i++)
            {
                short sample = (short)Math.Round(Math.Sin(i * 2 * Math.PI * 440 / 44100.0) * amplitude);
                writer.Write(sample); writer.Write(sample);
            }
        }
    }
    private static void SettingsChecks()
    {
        AppSettings defaults = new AppSettings();
        defaults.Normalize();
        Check(defaults.Tracks.Length == 0 && defaults.Bindings.Length == 0, "new settings start with no bundled personal tracks or shortcut bindings");

        AppSettings settings = EmptySettings();
        settings.Tracks = new SavedTrack[] { new SavedTrack { Id = "review", FileName = "Review-valid.wav", Name = "Review" } };
        settings.Scenes[0].Tracks = new SceneTrack[] { new SceneTrack { TrackId = "review", StartSeconds = Double.NaN, EndSeconds = -1 } };
        settings.Scenes[0].Bindings = new TrackBinding[] { new TrackBinding { TrackId = "review", Key = "I" } };
        settings.Scenes[0].RandomKey = "I";
        settings.Scenes[0].RandomMode = "bad";
        settings.Normalize();
        Check(settings.Scenes[0].RandomKey == "" && settings.Scenes[0].RandomMode == "normal" && settings.Scenes[0].Tracks[0].StartSeconds == 0 && settings.Scenes[0].Tracks[0].EndSeconds == null, "settings normalize invalid anchors, colliding keys, and old/invalid random mode");
        ScenePreset copy = settings.CreateScene("Review", true);
        copy.Tracks[0].StartSeconds = 1;
        Check(settings.Scenes[0].Tracks[0].StartSeconds == 0, "scene copy has independent anchor entries");
        Type store = T("SettingsStore");
        object[] save = new object[] { settings, "" };
        Check((bool)Call(store, null, "TrySave", save), "atomic settings save succeeds in disposable TEST_BUILD data");
        string file = Path.Combine(Root, "test-data", "settings.xml");
        File.Copy(file, file + ".bak", true);
        File.WriteAllText(file, "<broken-main>");
        AppSettings recovered = (AppSettings)Call(store, null, "Load");
        Check(recovered.Tracks.Length == 1 && recovered.Scenes.Length == 2, "corrupt primary settings load the valid backup");
        save = new object[] { recovered, "" };
        Check((bool)Call(store, null, "TrySave", save), "save after backup recovery succeeds");
        Check(File.ReadAllText(file + ".bak").IndexOf("<AppSettings", StringComparison.Ordinal) >= 0, "saving a backup-recovered configuration preserves the valid backup");
        File.WriteAllText(file, "<broken-again>");
        AppSettings recoveredAgain = (AppSettings)Call(store, null, "Load");
        Check(recoveredAgain.Tracks.Length == 1 && recoveredAgain.Scenes.Length == 2, "a second primary corruption still recovers from the preserved backup");
    }
    private static void ShuffleChecks()
    {
        Type bagType = T("RandomShuffleBag");
        object bag = Activator.CreateInstance(bagType, true);
        string[] pool = { "a.wav", "b.wav", "c.wav" };
        string previous = null;
        for (int round = 0; round < 100; round++)
        {
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < 3; i++)
            {
                string next = (string)Call(bagType, bag, "Next", "scene", pool);
                if (!seen.Add(next) || (i == 0 && next == previous)) throw new InvalidOperationException("shuffle no-repeat rule failed");
                previous = next;
            }
        }
        Check(true, "100 shuffle rounds: no repeat within round or at adjacent-round boundary");
        AppSettings settings = EmptySettings();
        settings.ActiveRandomFiles = new string[] { "Review-repair.wav" };
        File.WriteAllText(Path.Combine(Library, "Review-repair.wav"), "review repair placeholder");
        object form = BareForm(settings);
        bag = T("MainMenuForm").GetField("_shuffleBag", Flags).GetValue(form);
        Call(bagType, bag, "Next", settings.ActiveSceneId, settings.ActiveRandomFiles);
        Call(bagType, bag, "MarkFailed", settings.ActiveSceneId, "Review-repair.wav");
        File.AppendAllText(Path.Combine(Library, "Review-repair.wav"), " repaired");
        Call(T("MainMenuForm"), form, "RefreshWebFiles");
        Call(bagType, bag, "Refresh", settings.ActiveSceneId);
        string retry = (string)Call(bagType, bag, "Next", settings.ActiveSceneId, settings.ActiveRandomFiles);
        Check(retry == "Review-repair.wav", "manual library refresh clears a repaired file's failure and retries it");
    }
    private static void CacheChecks()
    {
        string name = "Review-cache.wav", file = Path.Combine(Library, name);
        WriteWave(file, 3000);
        FileInfo initialInfo = new FileInfo(file);
        string originalHash = Hash(file);
        AppSettings settings = EmptySettings();
        settings.LoudnessBaselineReady = true; settings.LoudnessBaselineLufs = -18;
        string version = (string)T("LoudnessAnalyzer").GetField("AlgorithmVersion").GetRawConstantValue();
        settings.SetAnalysis(new LoudnessAnalysis { FileName = name, Fingerprint = originalHash, AlgorithmVersion = version, IsValidated = true, TargetLufs = -18, IntegratedLufs = -30, PeakDbfs = -20, GainDb = 11, FileLength = initialInfo.Length, LastWriteTimeUtcTicks = initialInfo.LastWriteTimeUtc.Ticks });
        WriteWave(file, 24000);
        Check(Hash(file) != originalHash && settings.FindAnalysis(name) == null, "same-name replacement invalidates a cache when file metadata changes");
        object hashOperation = Activator.CreateInstance(T("SongImportOperation"), true);
        Guid operationId = (Guid)Field(hashOperation, "Id");
        string taskDirectory = Path.Combine(Root, "test-data", "import-work", "job_" + operationId.ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        object hashResult = Call(T("AudioImportValidator"), null, "Fingerprint", file, new FileInfo(file).Length, taskDirectory, 1, hashOperation);
        Check((bool)Field(hashResult, "Success") && (string)Field(hashResult, "Sha256") == Hash(file), "audio hashing runs in a cancellable, bounded child process");
        Call(T("EncryptedAudioConverter"), null, "CleanupTaskDirectory", operationId);
        object form = BareForm(settings);
        LoudnessAnalysis refreshed = (LoudnessAnalysis)Call(T("MainMenuForm"), form, "AnalyzeOrReuse", name, -18.0, settings.LoudnessAnalyses);
        double localMeasurement = (double)Field(Call(T("LoudnessAnalyzer"), null, "Analyze", file, -18.0), "IntegratedLufs");
        Check(refreshed != null && refreshed.Fingerprint == Hash(file) && Math.Abs(refreshed.IntegratedLufs - localMeasurement) < 0.01, "cache miss remeasures the replacement locally and stores its new fingerprint");
    }

    private static void SessionAndRouteChecks()
    {
        Type engine = T("AudioEngine");
        object active = FormatterServices.GetUninitializedObject(T("Mp3StreamPlayer"));
        object other = FormatterServices.GetUninitializedObject(T("Mp3StreamPlayer"));
        Check((bool)Call(engine, null, "CanCompleteTrackStart", active, active, 9, 9, true), "current accepted MP3 start passes the session gate");
        Check(!(bool)Call(engine, null, "CanCompleteTrackStart", active, active, 9, 10, true) &&
              !(bool)Call(engine, null, "CanCompleteTrackStart", active, other, 9, 9, true) &&
              !(bool)Call(engine, null, "CanCompleteTrackStart", active, active, 9, 9, false), "stale session, stale player, and stopped loading state reject late MP3 callbacks");
        Check((bool)Call(engine, null, "RequiresPcmHeadphoneOutput", true, false) &&
              !(bool)Call(engine, null, "RequiresPcmHeadphoneOutput", true, true) &&
              !(bool)Call(engine, null, "RequiresPcmHeadphoneOutput", false, false), "device reconfiguration restores PCM headphones only for an active non-direct session");

        List<string> commands = new List<string>();
        Type directType = T("DirectMp3Player");
        FieldInfo commandOverride = directType.GetField("CommandOverrideForTests", Flags);
        commandOverride.SetValue(null, new Func<string, int>(delegate(string command) { commands.Add(command); return 0; }));
        object direct = null;
        try
        {
            direct = Activator.CreateInstance(directType, Flags, null, new object[] { "isolated-test.mp3", 50, 1.0 }, null);
            Check(!commands.Exists(delegate(string command) { return command.StartsWith("play ", StringComparison.OrdinalIgnoreCase); }), "preparing an MP3 opens and configures it without starting playback");
            Check(!(bool)Call(engine, null, "StartDirectPlaybackIfAccepted", direct, false, 0.0, (double?)null) &&
                  !commands.Exists(delegate(string command) { return command.StartsWith("play ", StringComparison.OrdinalIgnoreCase); }), "a cancelled or stale MP3 session cannot issue a native play command");
            Check((bool)Call(engine, null, "StartDirectPlaybackIfAccepted", direct, true, 0.0, (double?)null), "an accepted MP3 session may begin native playback");
            Call(directType, direct, "Start", 0.0, (double?)null);
            int playCommands = 0; foreach (string command in commands) if (command.StartsWith("play ", StringComparison.OrdinalIgnoreCase)) playCommands++;
            Check(playCommands == 1, "an accepted MP3 player starts at most once");
        }
        finally
        {
            if (direct is IDisposable) ((IDisposable)direct).Dispose();
            commandOverride.SetValue(null, null);
        }

        object form = FormatterServices.GetUninitializedObject(T("MainMenuForm"));
        T("MainMenuForm").GetField("_keyCaptureGate", Flags).SetValue(form, new object());
        T("MainMenuForm").GetField("_keyCaptureReasons", Flags).SetValue(form, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Call(T("MainMenuForm"), form, "SetKeyCapture", "anchor-editor", true);
        Call(T("MainMenuForm"), form, "SetKeyCapture", "pack-preview", true);
        Call(T("MainMenuForm"), form, "SetKeyCapture", "pack-preview", false);
        Check((bool)Field(form, "_capturingKey"), "closing one modal leaves another active keyboard-capture reason intact");
        Call(T("MainMenuForm"), form, "SetKeyCapture", "anchor-editor", false);
        Check(!(bool)Field(form, "_capturingKey"), "keyboard shortcuts resume after the final capture reason closes");
    }
    private static void OnComplete(object sender, object value) { lock (Events) Events.Add(value); }
    private static object NewTrack(string id, string file, double start, double? end)
    {
        return Activator.CreateInstance(T("TrackDefinition"), Flags, null, new object[] { id, id, file, start, end, 0.0 }, null);
    }
    private static object NewEngine(AppSettings settings)
    {
        return Activator.CreateInstance(T("AudioEngine"), Flags, null, new object[] { settings, null, true }, null);
    }
    private static object Snapshot(object engine) { return Call(T("AudioEngine"), engine, "GetPlaybackSnapshot"); }
    private static void WaitPlaying(object engine)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            object s = Snapshot(engine);
            if ((string)Field(s, "State") == "playing") return;
            if (!String.IsNullOrWhiteSpace((string)Field(s, "Error"))) throw new InvalidOperationException((string)Field(s, "Error"));
            Thread.Sleep(10);
        }
        throw new TimeoutException("TEST_BUILD playback did not become ready");
    }
    private static void Drain(object engine)
    {
        for (int i = 0; i < 1500; i++)
        {
            object[] args = { new short[1764], 0, 0 };
            Call(T("AudioEngine"), engine, "ReadMusic", args);
            if ((string)Field(Snapshot(engine), "State") == "stopped") return;
            Thread.Sleep(1);
        }
        throw new TimeoutException("TEST_BUILD playback did not complete");
    }
    private static void PlaybackChecks()
    {
        object engine = NewEngine(EmptySettings());
        try
        {
            EventInfo eventInfo = T("AudioEngine").GetEvent("PlaybackCompleted");
            eventInfo.AddEventHandler(engine, Delegate.CreateDelegate(eventInfo.EventHandlerType, typeof(ProjectRegressionHarness).GetMethod("OnComplete", BindingFlags.Static | BindingFlags.NonPublic)));
            object track = NewTrack("review", "Review-valid.wav", .5, 1.25);
            Check((bool)Call(T("AudioEngine"), engine, "TryStartFullTrack", track), "full-track start accepted with hardware suppressed");
            WaitPlaying(engine);
            object s = Snapshot(engine);
            Check((double)Field(s, "StartSeconds") == 0 && Field(s, "EndSeconds") == null, "manual/full-track playback ignores saved shortcut anchors");
            Drain(engine); Thread.Sleep(40);
            Check(Events.Count == 1 && Convert.ToString(Field(Events[0], "Reason")) == "Natural", "natural completion raises one identified completion event");
            Call(T("AudioEngine"), engine, "ToggleTrack", track, true);
            WaitPlaying(engine); s = Snapshot(engine);
            int session = (int)Field(s, "SessionId"), generation = (int)Field(s, "Generation");
            Check((double)Field(s, "StartSeconds") == .5 && (double)Field(s, "EndSeconds") == 1.25, "shortcut playback applies saved range");
            Check((bool)Call(T("AudioEngine"), engine, "Seek", session, generation, 1.5), "current generation seek accepted");
            Check(!(bool)Call(T("AudioEngine"), engine, "Seek", session, generation, 1.0), "stale seek generation rejected");
            WaitPlaying(engine); s = Snapshot(engine);
            Check((double)Field(s, "StartSeconds") == 0 && Field(s, "EndSeconds") == null && (double)Property(track, "StartSeconds") == .5, "seek expands current session to full song and preserves saved anchors");
            Call(T("AudioEngine"), engine, "StopActiveTrack"); Thread.Sleep(40);
            Check(Events.Count == 1, "explicit stop does not raise natural completion");
            Check((bool)Call(T("AudioEngine"), engine, "TryStartPreview", track, .2, (double?).6), "draft preview start accepted");
            WaitPlaying(engine); s = Snapshot(engine);
            Check((string)Field(s, "PlaybackMode") == "preview" && !(bool)Field(s, "CanSeek"), "preview snapshot is distinct and cannot seek through the UI");
            Drain(engine); Thread.Sleep(40);
            Check(Events.Count == 2 && (string)Field(Events[1], "PlaybackMode") == "preview", "preview completion preserves its mode identity");
            for (int i = 0; i < 20; i++)
            {
                Call(T("AudioEngine"), engine, "TryStartFullTrack", track);
                Call(T("AudioEngine"), engine, "StopActiveTrack");
            }
            Call(T("AudioEngine"), engine, "TryStartFullTrack", track); WaitPlaying(engine); Thread.Sleep(50);
            Check((string)Field(Snapshot(engine), "State") == "playing", "20 rapid load/stop cycles do not let stale WAV completion overwrite the new session");
        }
        finally { ((IDisposable)engine).Dispose(); }
        object limiter = Activator.CreateInstance(T("FinalMixLimiter"), true);
        short[] music = new short[1764], mic = new short[1764], output = new short[1764];
        for (int i = 0; i < music.Length; i++) { music[i] = 32767; mic[i] = 32767; }
        object[] mixArgs = { music, mic, output, 100, 3.5, 0 };
        Call(T("FinalMixLimiter"), limiter, "Mix", mixArgs);
        int peak = 0; foreach (short value in output) peak = Math.Max(peak, Math.Abs((int)value));
        Check(peak <= 29204, "final virtual mix stays inside its documented -1 dBFS sample ceiling");
    }
    private static void WritePack(string target, string audioName, string wave, bool badHash, double start, string extraEntry)
    {
        string version = (string)T("LoudnessAnalyzer").GetField("AlgorithmVersion").GetRawConstantValue();
        object manifest = new {
            Version = 1, SceneName = "Review Pack", RandomMode = "continuous", RandomKey = "I",
            Assets = new[] { new { Id = "a", FileName = audioName, DisplayName = "Review", Sha256 = badHash ? new string('f', 64) : Hash(wave) } },
            Tracks = new[] { new { Id = "t", AssetId = "a", Name = "Review", Key = "I", StartSeconds = start, EndSeconds = (double?)null } },
            RandomAssetIds = new[] { "a" }, Loudness = new[] { new { AssetId = "a", IntegratedLufs = -60.0, PeakDbfs = -60.0, GainDb = 11.0, TargetLufs = -18.0, AlgorithmVersion = version } }
        };
        using (ZipArchive zip = new ZipArchive(File.Create(target), ZipArchiveMode.Create))
        {
            using (StreamWriter w = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) w.Write(new JavaScriptSerializer().Serialize(manifest));
            using (Stream source = File.OpenRead(wave)) using (Stream dest = zip.CreateEntry("audio/" + audioName).Open()) source.CopyTo(dest);
            if (extraEntry != null) using (StreamWriter w = new StreamWriter(zip.CreateEntry(extraEntry).Open())) w.Write("review");
        }
    }
    private static bool PrepareRejected(string file)
    {
        try { object preview = Call(T("SoundPackService"), null, "PrepareImport", file); Call(T("SoundPackService"), null, "CancelPrepared", Field(preview, "Token")); return false; }
        catch (TargetInvocationException ex) { return ex.InnerException is InvalidDataException; }
    }
    private static void PackChecks()
    {
        Type service = T("SoundPackService");
        string wave = Path.Combine(Library, "Review-valid.wav"), pack = Path.Combine(Root, "review-pack.wbgm");
        WritePack(pack, "Review-valid.wav", wave, false, 9, null);
        object preview = Call(service, null, "PrepareImport", pack);
        string token = (string)Field(preview, "Token");
        object plan = Call(service, null, "BuildImportPlan", token, new SavedTrack[0], new string[0]);
        ScenePreset scene = (ScenePreset)Field(plan, "Scene");
        Check(scene.RandomMode == "continuous" && scene.Tracks[0].StartSeconds == 0 && scene.RandomKey == "", "pack preserves continuous mode, resets out-of-range anchor, and clears random key collision");
        LoudnessAnalysis[] analyses = (LoudnessAnalysis[])Field(plan, "NewAnalyses");
        object measured = Call(T("LoudnessAnalyzer"), null, "Analyze", wave, -18.0);
        double actualLufs = (double)Field(measured, "IntegratedLufs");
        object form = BareForm(EmptySettings());
        Check(analyses.Length == 0, "sound-pack loudness claims are never imported as validated measurements");
        LoudnessAnalysis reused = (LoudnessAnalysis)Call(T("MainMenuForm"), form, "AnalyzeOrReuse", analyses.Length == 0 ? "Review-valid.wav" : analyses[0].FileName, -18.0, analyses);
        Check(reused != null && Math.Abs(reused.IntegratedLufs - actualLufs) < 0.01 && reused.GainDb <= 0,
            "first import is measured locally and cannot turn a fake -60 LUFS claim into positive gain");
        Call(service, null, "CompleteImport", token);
        WritePack(pack, "Review-valid.wav", wave, true, 0, null);
        Check(PrepareRejected(pack), "pack rejects a file hash mismatch");
        WritePack(pack, "Review-valid.wav", wave, false, 0, "../escape.wav");
        Check(PrepareRejected(pack), "pack rejects path traversal before extraction");
        WritePack(pack, "Review-valid.wav", wave, false, 0, "audio/Review-valid.wav");
        Check(PrepareRejected(pack), "pack rejects duplicate ZIP entries");
        WritePack(pack, "Review-valid.wav", wave, false, 0, null);
        preview = Call(service, null, "PrepareImport", pack); token = (string)Field(preview, "Token");
        File.Delete(wave);
        plan = Call(service, null, "BuildImportPlan", token, new SavedTrack[0], new string[0]);
        Check(File.Exists(wave), "pack copies missing audio into disposable library");
        Call(service, null, "RollbackFiles", plan);
        Check(!File.Exists(wave), "pack rollback removes only newly created matching audio");
        Call(service, null, "CompleteImport", token);
    }
    private static int Main(string[] args)
    {
        try
        {
            Player = Assembly.LoadFrom(args[0]);
            Root = AppDomain.CurrentDomain.BaseDirectory;
            if (Root.IndexOf("local-only" + Path.DirectorySeparatorChar + "test-build", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("review harness must run inside isolated test-build");
            Library = Path.Combine(Root, "bgm"); Directory.CreateDirectory(Library);
            WriteWave(Path.Combine(Library, "Review-valid.wav"), 12000);
            SettingsChecks(); ShuffleChecks(); CacheChecks(); PlaybackChecks(); PackChecks();
            SessionAndRouteChecks();
            Console.WriteLine("Completed " + Checks + " regression assertions. Hardware outputs were suppressed; no installer or hook was started.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
