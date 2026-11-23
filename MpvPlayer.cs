using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.ComponentModel;

namespace TidalMpvPlayer;

public sealed class MpvPlayer : IAsyncDisposable
{
    private const int InitialBufferSeconds = 10;
    private const double CrossfadePreparationLeadSeconds = InitialBufferSeconds + 3;
    private static readonly int[] VisualizerFrequencies =
    [
        20, 25, 31, 40, 50, 63, 80, 100, 125, 160,
        200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000
    ];
    private static readonly (string Name, int Frequency)[] EqualizerBands =
    [
        ("31Hz", 31),
        ("62Hz", 62),
        ("125Hz", 125),
        ("250Hz", 250),
        ("500Hz", 500),
        ("1kHz", 1000),
        ("2kHz", 2000),
        ("4kHz", 4000),
        ("8kHz", 8000),
        ("16kHz", 16000)
    ];

    private sealed class MpvDeck(Process process, MpvIpcClient ipc)
    {
        public Process Process { get; } = process;
        public MpvIpcClient Ipc { get; } = ipc;
        public Task ConnectionTask { get; set; } = Task.CompletedTask;
        public TaskCompletionSource<bool> FileLoaded { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PlaybackStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SampleRate { get; set; }
        public int OutputSampleRate { get; set; }
        public double[] VisualizerBandDb { get; } =
            Enumerable.Repeat(-100d, VisualizerFrequencies.Length).ToArray();
        public object VisualizerLock { get; } = new();
        public double OverallRmsDb { get; set; } = -100;
        public double DurationSeconds { get; set; }
        public string? MediaUrl { get; set; }
        public bool ExpectedExit { get; set; }
        public int DisposeStarted;
        public Task StandardOutputTask { get; set; } = Task.CompletedTask;
        public Task StandardErrorTask { get; set; } = Task.CompletedTask;

        public void ResetPlaybackStarted() =>
            PlaybackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetFileLoaded() =>
            FileLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PreloadedTrack(string sourceUrl, string mediaUrl)
    {
        public string SourceUrl { get; } = sourceUrl;
        public string MediaUrl { get; } = mediaUrl;
        public bool Appended { get; set; }
    }

    private sealed class CrossfadeSession(
        MpvDeck source,
        MpvDeck target,
        PreloadedTrack nextTrack,
        string? sourceUrl,
        string? sourceMediaUrl,
        double sourceVolume,
        double seconds,
        bool restoreExclusive,
        bool restoreBitPerfect,
        string mode)
    {
        public MpvDeck Source { get; } = source;
        public MpvDeck Target { get; } = target;
        public PreloadedTrack NextTrack { get; } = nextTrack;
        public string? SourceUrl { get; } = sourceUrl;
        public string? SourceMediaUrl { get; } = sourceMediaUrl;
        public double SourceVolume { get; } = sourceVolume;
        public double Seconds { get; } = seconds;
        public bool RestoreExclusive { get; } = restoreExclusive;
        public bool RestoreBitPerfect { get; } = restoreBitPerfect;
        public string Mode { get; } = mode;
        public double TargetVolume { get; set; } = sourceVolume;
        public double Progress { get; set; }
        public bool SourceEnded { get; set; }
        public bool IsPaused { get; set; }
        public bool Completed { get; set; }
    }

    private readonly string mpvPath;
    private readonly MpvProcessJob processJob = new();
    private MpvDeck activeDeck;
    private MpvDeck? transitionDeck;
    private readonly AudioDecrypter decrypter;
    private readonly Lock preloadLock = new();
    private readonly List<PreloadedTrack> preloadedTracks = [];
    private readonly StreamingMediaServer mediaServer = new();
    private CancellationTokenSource visualizerCts = new();
    private Task? visualizerTask;
    private readonly SemaphoreSlim transitionLock = new(1, 1);
    private CancellationTokenSource? crossfadeCts;
    private bool crossfadeEnabled;
    private double crossfadeSeconds;
    private string crossfadeMode = "shared";
    private bool crossfadeStarting;
    private bool crossfadeSourceEnded;
    private bool pauseRequested;
    private CrossfadeSession? currentCrossfade;
    private bool hasHeardAudio;
    private bool leadingSilenceDetected;
    private bool silenceTransitionStarted;
    private double[] equalizerGains = new double[EqualizerBands.Length];
    private bool equalizerEnabled = true;
    private volatile bool shuttingDown;

    private Process process => activeDeck.Process;
    private MpvIpcClient ipc => activeDeck.Ipc;
    private Task connectionTask => activeDeck.ConnectionTask;

    public double Volume { get; private set; } = 100;
    public double TimePosition { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool IsBuffering { get; private set; }
    public bool IsCrossfadeBoundaryPending { get; private set; }
    public bool IsExclusiveMode { get; private set; }
    public bool IsBitPerfect { get; private set; }
    public bool IsCrossfading => crossfadeStarting || transitionDeck is not null;
    public bool EqualizerEnabled => equalizerEnabled;
    public IReadOnlyDictionary<string, double> EqualizerGains =>
        EqualizerBands
            .Select((band, index) => new KeyValuePair<string, double>(band.Name, equalizerGains[index]))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    public string? CurrentDevice { get; private set; }
    public string? CurrentUrl { get; private set; }
    private string? CurrentMediaUrl { get; set; }
    public event Func<string, string, double, double, Task>? CrossfadeCompleted;
    public event Action<string, double, double, IReadOnlyList<AudioBandLevel>>? AudioBandsAvailable;
    private bool RestoreExclusiveAfterCrossfade { get; set; }
    private bool RestoreBitPerfectAfterCrossfade { get; set; }
    public event Action<JsonElement>? PropertyEvent;

    public MpvPlayer(string mpvPath, IAudioDecryptionProvider? decryptionProvider = null)
    {
        this.mpvPath = mpvPath;
        decrypter = new AudioDecrypter(decryptionProvider);
        activeDeck = StartDeck(mpvPath);
        activeDeck.ConnectionTask = ConnectAndObserveAsync(activeDeck, startVisualizer: true);
    }

    private MpvDeck StartDeck(string executablePath)
    {
        var ipcAddress = OperatingSystem.IsWindows()
            ? $"tidal-mpv-{Guid.NewGuid():N}"
            : Path.Combine(Path.GetTempPath(), $"tidal-mpv-{Guid.NewGuid():N}.sock");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("--ytdl=no");
        startInfo.ArgumentList.Add("--idle=yes");
        startInfo.ArgumentList.Add("--pause=yes");
        startInfo.ArgumentList.Add("--no-terminal");
        startInfo.ArgumentList.Add("--msg-level=all=warn,ffmpeg=info");
        startInfo.ArgumentList.Add("--gapless-audio=yes");
        startInfo.ArgumentList.Add("--hr-seek=yes");
        startInfo.ArgumentList.Add("--cache=yes");
        startInfo.ArgumentList.Add("--cache-secs=30");
        startInfo.ArgumentList.Add("--demuxer-readahead-secs=30");
        startInfo.ArgumentList.Add("--cache-pause-initial=yes");
        startInfo.ArgumentList.Add($"--cache-pause-wait={InitialBufferSeconds}");
        startInfo.ArgumentList.Add("--audio-display=no");
        startInfo.ArgumentList.Add(
            $"--af=lavfi=[{BuildAudioFilterGraph(equalizerGains, equalizerEnabled)}]");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows()
            ? $"--input-ipc-server=\\\\.\\pipe\\{ipcAddress}"
            : $"--input-ipc-server={ipcAddress}");
        var process = Process.Start(startInfo) ??
                      throw new InvalidOperationException($"Could not start MPV: {executablePath}");
        if (processJob.IsSupported && !processJob.TryAssign(process, out var jobError))
        {
            PlayerLog.Error(
                "Could not attach MPV to the player shutdown job; relying on normal disposal.",
                jobError!);
        }
        var deck = new MpvDeck(process, new MpvIpcClient(ipcAddress));
        PlayerLog.Info($"Started MPV process (pid {process.Id}).");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (shuttingDown || deck.ExpectedExit)
                PlayerLog.Info($"MPV process exited during shutdown (code {process.ExitCode}).");
            else
                PlayerLog.Error($"MPV process exited unexpectedly with code {process.ExitCode}.",
                    new InvalidOperationException("MPV exited while the player was running."));
        };
        deck.StandardOutputTask = Task.Run(() => ReadMpvStandardOutputAsync(deck));
        deck.StandardErrorTask = Task.Run(() => ReadMpvStandardErrorAsync(deck));
        deck.Ipc.EventReceived += eventElement => OnMpvEvent(deck, eventElement);
        return deck;
    }

    private static async Task ReadMpvStandardOutputAsync(MpvDeck deck)
    {
        double? pendingRmsDb = null;
        try
        {
            while (await deck.Process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("frame:", StringComparison.Ordinal))
                {
                    pendingRmsDb = null;
                }
                else if (line.StartsWith(
                             "lavfi.astats.Overall.RMS_level=",
                             StringComparison.Ordinal) &&
                         TryParseDecibels(
                             line["lavfi.astats.Overall.RMS_level=".Length..],
                             out var rmsDb))
                {
                    pendingRmsDb = rmsDb;
                }
                else if (line.StartsWith("band=", StringComparison.Ordinal) &&
                         pendingRmsDb is { } db &&
                         int.TryParse(
                             line["band=".Length..],
                             NumberStyles.Integer,
                             CultureInfo.InvariantCulture,
                             out var frequency))
                {
                    lock (deck.VisualizerLock)
                    {
                        if (frequency == 0)
                        {
                            deck.OverallRmsDb = db;
                        }
                        else
                        {
                            var bandIndex = Array.IndexOf(VisualizerFrequencies, frequency);
                            if (bandIndex >= 0)
                                deck.VisualizerBandDb[bandIndex] = db;
                        }
                    }
                    pendingRmsDb = null;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    PlayerLog.Info($"MPV stdout: {line}");
                }
            }
        }
        catch (IOException exception)
        {
            if (!deck.ExpectedExit)
                PlayerLog.Error("Error reading MPV visualizer data.", exception);
        }
    }

    private static async Task ReadMpvStandardErrorAsync(MpvDeck deck)
    {
        try
        {
            while (await deck.Process.StandardError.ReadLineAsync() is { } line)
                PlayerLog.Info($"MPV: {line}");
        }
        catch (IOException exception)
        {
            if (!deck.ExpectedExit)
                PlayerLog.Error("Error reading MPV stderr.", exception);
        }
    }

    private static bool TryParseDecibels(string text, out double decibels)
    {
        if (text.Equals("-inf", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("-infinity", StringComparison.OrdinalIgnoreCase))
        {
            decibels = -100;
            return true;
        }

        return double.TryParse(
                   text,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out decibels) &&
               double.IsFinite(decibels);
    }

    private async Task ConnectAndObserveAsync(MpvDeck deck, bool startVisualizer)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await deck.Ipc.ConnectAsync(CancellationToken.None);
                PlayerLog.Info("Connected to MPV IPC.");
                await deck.Ipc.SendCommandAsync(["observe_property", 1, "volume"], CancellationToken.None);
                await deck.Ipc.SendCommandAsync(["observe_property", 2, "time-pos"], CancellationToken.None);
                await deck.Ipc.SendCommandAsync(["observe_property", 3, "duration"], CancellationToken.None);
                await deck.Ipc.SendCommandAsync(["observe_property", 4, "pause"], CancellationToken.None);
                await deck.Ipc.SendCommandAsync(["observe_property", 5, "audio-params"], CancellationToken.None);
                await deck.Ipc.SendCommandAsync(["request_log_messages", "info"], CancellationToken.None);
                if (startVisualizer)
                    visualizerTask = EmitVisualizerAsync(visualizerCts.Token);
                return;
            }
            catch (Exception exception)
            {
                if (attempt == 49)
                    PlayerLog.Error("Could not connect to MPV IPC after 50 attempts.", exception);
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("MPV IPC did not become ready.");
    }

    private void OnMpvEvent(MpvDeck deck, JsonElement eventElement)
    {
        if (ReferenceEquals(deck, transitionDeck))
        {
            if (eventElement.TryGetProperty("event", out var transitionEvent) &&
                transitionEvent.GetString() == "file-loaded")
            {
                deck.FileLoaded.TrySetResult(true);
            }
            if (eventElement.TryGetProperty("event", out transitionEvent) &&
                transitionEvent.GetString() == "playback-restart")
            {
                deck.PlaybackStarted.TrySetResult(true);
            }
            if (eventElement.TryGetProperty("name", out var transitionName) &&
                transitionName.GetString() == "audio-params" &&
                eventElement.TryGetProperty("data", out var transitionParams) &&
                transitionParams.TryGetProperty("samplerate", out var transitionRate) &&
                transitionRate.TryGetInt32(out var transitionSampleRate))
            {
                deck.SampleRate = transitionSampleRate;
            }
            if (eventElement.TryGetProperty("name", out transitionName) &&
                transitionName.GetString() == "duration" &&
                eventElement.TryGetProperty("data", out var transitionDuration) &&
                transitionDuration.TryGetDouble(out var transitionDurationSeconds))
            {
                deck.DurationSeconds = transitionDurationSeconds;
                mediaServer.SetDuration(deck.MediaUrl, transitionDurationSeconds);
            }
            return;
        }
        if (IsCrossfadeBoundaryPending &&
            currentCrossfade is { } boundarySession &&
            ReferenceEquals(deck, boundarySession.Source))
        {
            return;
        }
        if (!ReferenceEquals(deck, activeDeck))
            return;

        string? endFileReason = null;
        var propertyEventRaised = false;
        if (eventElement.TryGetProperty("error", out var commandError) &&
            commandError.ValueKind == JsonValueKind.String &&
            commandError.GetString() != "success")
        {
            PlayerLog.Error($"MPV IPC command returned '{commandError.GetString()}': {eventElement}",
                new InvalidOperationException("MPV rejected an IPC command."));
        }

        if (eventElement.TryGetProperty("event", out var mpvEvent))
        {
            var eventName = mpvEvent.GetString();
            if (eventName == "file-loaded")
            {
                IsLoaded = true;
                deck.FileLoaded.TrySetResult(true);
                hasHeardAudio = false;
                leadingSilenceDetected = false;
                silenceTransitionStarted = false;
                PlayerLog.Info($"MPV loaded media: {eventElement}");
            }
            else if (eventName == "log-message")
            {
                var level = eventElement.TryGetProperty("level", out var levelElement)
                    ? levelElement.GetString()
                    : null;
                var text = eventElement.TryGetProperty("text", out var textElement)
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;
                if (text.Contains("silence_start", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("silence_end", StringComparison.OrdinalIgnoreCase))
                {
                    PlayerLog.Info($"MPV silence detector: {text}");
                }
                if (ReferenceEquals(deck, activeDeck) &&
                    text.Contains("silence_start", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSilenceStart(text);
                }
                else if (ReferenceEquals(deck, activeDeck) &&
                         text.Contains("silence_end", StringComparison.OrdinalIgnoreCase))
                {
                    HandleSilenceEnd();
                }
                if (level is "warn" or "error")
                {
                    PlayerLog.Info($"MPV {level}: {text}");
                    if (text.Contains("exclusive", StringComparison.OrdinalIgnoreCase) &&
                        (text.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
                    {
                        IsExclusiveMode = false;
                        IsBitPerfect = false;
                    }
                }
            }
            else if (eventName == "end-file")
            {
                endFileReason = eventElement.TryGetProperty("reason", out var reasonElement)
                    ? reasonElement.GetString()
                    : "unknown";
                if (crossfadeStarting || transitionDeck is not null)
                    PlayerLog.Info($"MPV ended during crossfade setup (reason: {endFileReason}).");
                if ((crossfadeStarting || transitionDeck is not null) && endFileReason == "eof")
                {
                    if (currentCrossfade is not null)
                        currentCrossfade.SourceEnded = true;
                    crossfadeSourceEnded = true;
                    PlayerLog.Info("Current track ended during crossfade.");
                    return;
                }

                PropertyEvent?.Invoke(eventElement);
                propertyEventRaised = true;
                IsLoaded = false;
                IsBuffering = false;
                if (endFileReason == "error")
                {
                    var detail = eventElement.TryGetProperty("file_error", out var fileError)
                        ? fileError.GetString() ?? "unknown file error"
                        : eventElement.ToString();
                    PlayerLog.Error($"MPV could not play the media: {detail}",
                        new InvalidDataException(detail));
                }
                else
                {
                    PlayerLog.Info($"MPV finished media (reason: {endFileReason}).");
                }
            }
            else if (eventName == "playback-restart")
            {
                IsPaused = false;
                deck.PlaybackStarted.TrySetResult(true);
                PlayerLog.Info($"MPV playback started: {eventElement}");
            }
        }

        var suppressCrossfadePauseSignal =
            (crossfadeStarting || currentCrossfade is not null) &&
            ReferenceEquals(deck, activeDeck) &&
            eventElement.TryGetProperty("event", out var pauseEvent) &&
            pauseEvent.GetString() == "property-change" &&
            eventElement.TryGetProperty("name", out var pauseName) &&
            pauseName.GetString() == "pause";
        if (!suppressCrossfadePauseSignal && !propertyEventRaised)
            PropertyEvent?.Invoke(eventElement);
        if (eventElement.TryGetProperty("event", out var completedEvent) &&
            completedEvent.GetString() == "end-file" &&
            endFileReason is "eof" or "error")
        {
            var finishedMediaUrl = CurrentMediaUrl;
            var nextTrack = DequeuePreloadedTrack();
            if (nextTrack is not null)
            {
                CurrentUrl = nextTrack.SourceUrl;
                CurrentMediaUrl = nextTrack.MediaUrl;
                deck.MediaUrl = nextTrack.MediaUrl;
                var manuallyLoadNext = !nextTrack.Appended;
                IsPaused = false;
                TimePosition = 0;
                Duration = 0;
                deck.DurationSeconds = 0;
                PlayerLog.Info("Advancing to a preloaded track.");
                if (manuallyLoadNext)
                    _ = LoadPreloadedTrackAsync(nextTrack, finishedMediaUrl);
            }
            else
            {
                CurrentUrl = null;
                CurrentMediaUrl = null;
                deck.MediaUrl = null;
            }
            mediaServer.Remove(finishedMediaUrl);
        }
        if (eventElement.TryGetProperty("name", out var name))
        {
            var propertyName = name.GetString();
            if (propertyName == "time-pos" &&
                eventElement.TryGetProperty("data", out var time) &&
                time.TryGetDouble(out var seconds))
            {
                TimePosition = seconds;
                if (seconds > 1 && !leadingSilenceDetected)
                    hasHeardAudio = true;
                TryStartCrossfade();
            }
            else if (propertyName == "duration" &&
                     eventElement.TryGetProperty("data", out var duration) &&
                     duration.TryGetDouble(out var durationSeconds))
            {
                Duration = durationSeconds;
                deck.DurationSeconds = durationSeconds;
                mediaServer.SetDuration(deck.MediaUrl, durationSeconds);
            }
            else if (propertyName == "audio-params" &&
                     eventElement.TryGetProperty("data", out var audioParams) &&
                     audioParams.TryGetProperty("samplerate", out var sampleRate) &&
                     sampleRate.TryGetInt32(out var parsedSampleRate))
            {
                deck.SampleRate = parsedSampleRate;
            }
            else if (propertyName == "pause" &&
                     eventElement.TryGetProperty("data", out var pause))
            {
                IsPaused = pause.GetBoolean();
            }
        }
        if (eventElement.TryGetProperty("name", out var volumeName) &&
            volumeName.GetString() == "volume" &&
            eventElement.TryGetProperty("data", out var data) &&
            data.TryGetDouble(out var volume))
        {
            Volume = volume;
        }

    }

    private async Task LoadPreloadedTrackAsync(PreloadedTrack track, string? finishedMediaUrl)
    {
        try
        {
            activeDeck.MediaUrl = track.MediaUrl;
            activeDeck.DurationSeconds = 0;
            activeDeck.ResetFileLoaded();
            activeDeck.ResetPlaybackStarted();
            await ipc.SendCommandAsync(["loadfile", track.MediaUrl, "replace"], CancellationToken.None);
            await WaitForInitialBufferAsync(activeDeck, CancellationToken.None);
            await ipc.SendCommandAsync(["set_property", "pause", false], CancellationToken.None);
            mediaServer.Remove(finishedMediaUrl);
            if (!crossfadeEnabled)
                await AppendPendingPreloadsAsync();
        }
        catch (Exception exception)
        {
            PlayerLog.Error("Could not load a preloaded track after the previous track ended.", exception);
            EmitAudioError($"Could not load the next preloaded track: {exception.Message}");
        }
    }

    private void HandleSilenceStart(string message)
    {
        if (!IsLoaded || IsPaused || Duration <= 0)
            return;

        var marker = message.IndexOf("silence_start:", StringComparison.OrdinalIgnoreCase);
        var startTime = marker >= 0 &&
                        double.TryParse(
                            message[(marker + "silence_start:".Length)..].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var parsed)
            ? parsed
            : TimePosition;
        if (leadingSilenceDetected)
            return;

        if (!hasHeardAudio && startTime <= 1)
        {
            leadingSilenceDetected = true;
            return;
        }

        if (hasHeardAudio && Duration - TimePosition <= 15 && !silenceTransitionStarted)
        {
            silenceTransitionStarted = true;
            if (crossfadeEnabled)
                _ = StartCrossfadeAsync();
            else
                _ = SkipSilentTailAsync();
        }
    }

    private void HandleSilenceEnd()
    {
        leadingSilenceDetected = false;
        hasHeardAudio = true;
    }

    private async Task SkipSilentTailAsync()
    {
        if (!await transitionLock.WaitAsync(0))
            return;

        PreloadedTrack? nextTrack = null;
        try
        {
            if (crossfadeEnabled || !IsLoaded || IsPaused)
                return;
            nextTrack = TakeNextPreloadedTrack();
            if (nextTrack is null)
                return;

            var oldUrl = CurrentUrl;
            var oldMediaUrl = CurrentMediaUrl;
            await ClearMpvPlaylistAsync();
            EmitSyntheticEvent("""{"event":"end-file","reason":"eof"}""");
            CurrentUrl = nextTrack.SourceUrl;
            CurrentMediaUrl = nextTrack.MediaUrl;
            TimePosition = 0;
            Duration = 0;
            IsLoaded = false;
            hasHeardAudio = false;
            leadingSilenceDetected = false;
            silenceTransitionStarted = false;
            await ipc.SendCommandAsync(["loadfile", nextTrack.MediaUrl, "replace"], CancellationToken.None);
            await ipc.SendCommandAsync(["set_property", "pause", false], CancellationToken.None);
            mediaServer.Remove(oldMediaUrl);

            if (!crossfadeEnabled)
                await AppendPendingPreloadsAsync();
            PlayerLog.Info("Skipped a silent track ending and started the preloaded track.");
        }
        catch (Exception exception)
        {
            if (nextTrack is not null)
                RequeuePreloadedTrack(nextTrack);
            PlayerLog.Error("Could not advance past a silent track ending.", exception);
        }
        finally
        {
            transitionLock.Release();
        }
    }

    private void TryStartCrossfade()
    {
        if (!crossfadeEnabled ||
            crossfadeSeconds <= 0 ||
            !IsLoaded ||
            IsPaused ||
            Duration <= 0 ||
            Duration - TimePosition > crossfadeSeconds + CrossfadePreparationLeadSeconds)
        {
            return;
        }

        _ = StartCrossfadeAsync();
    }

    private async Task StartCrossfadeAsync()
    {
        if (!await transitionLock.WaitAsync(0))
            return;

        var cancellation = new CancellationTokenSource();
        crossfadeCts = cancellation;
        var cancellationToken = cancellation.Token;
        crossfadeSourceEnded = false;
        crossfadeStarting = true;
        PreloadedTrack? nextTrack = null;
        MpvDeck? nextDeck = null;
        var source = activeDeck;
        var previousUrl = CurrentUrl;
        var previousMediaUrl = CurrentMediaUrl;
        var previousVolume = Volume;
        var previousExclusive = IsExclusiveMode;
        var previousBitPerfect = IsBitPerfect;
        var promoted = false;
        var resampleRate = 0;
        try
        {
            if (!crossfadeEnabled || !IsLoaded || IsPaused || CurrentUrl is null)
                return;
            nextTrack = TakeNextPreloadedTrack();
            if (nextTrack is null)
                return;
            if (crossfadeMode == "shared" && (previousExclusive || previousBitPerfect))
            {
                RequeuePreloadedTrack(nextTrack);
                nextTrack = null;
                PlayerLog.Error(
                    "Shared crossfade cannot run while exclusive or bit-perfect output is active.",
                    new InvalidOperationException("Crossfade mode requires shared output."));
                return;
            }

            var remaining = Math.Max(0.1, Duration - TimePosition);
            var fadeSeconds = Math.Min(crossfadeSeconds, remaining);
            if (crossfadeMode == "resample")
            {
                resampleRate = source.SampleRate > 0
                    ? source.SampleRate
                    : source.OutputSampleRate;
                await source.Ipc.SendCommandAsync(
                    ["set_property", "audio-exclusive", false],
                    CancellationToken.None);
                if (await GetDeckBooleanAsync(source, "audio-exclusive"))
                    throw new InvalidOperationException("MPV did not switch the current deck to shared output.");
                if (resampleRate > 0)
                {
                    await source.Ipc.SendCommandAsync(
                        ["set_property", "audio-samplerate", resampleRate],
                        CancellationToken.None);
                    source.OutputSampleRate = resampleRate;
                }
                IsExclusiveMode = false;
                IsBitPerfect = false;
            }

            nextDeck = StartDeck(mpvPath);
            nextDeck.MediaUrl = nextTrack.MediaUrl;
            transitionDeck = nextDeck;
            nextDeck.ConnectionTask = ConnectAndObserveAsync(nextDeck, startVisualizer: false);
            await nextDeck.ConnectionTask;
            if (CurrentDevice is not null)
                await nextDeck.Ipc.SendCommandAsync(
                    ["set_property", "audio-device", CurrentDevice],
                    CancellationToken.None);
            await nextDeck.Ipc.SendCommandAsync(
                ["set_property", "audio-exclusive", false],
                CancellationToken.None);
            if (await GetDeckBooleanAsync(nextDeck, "audio-exclusive"))
                throw new InvalidOperationException("MPV did not switch the incoming deck to shared output.");
            if (crossfadeMode == "resample" && resampleRate > 0)
            {
                await nextDeck.Ipc.SendCommandAsync(
                    ["set_property", "audio-samplerate", resampleRate],
                    CancellationToken.None);
                nextDeck.OutputSampleRate = resampleRate;
            }
            await nextDeck.Ipc.SendCommandAsync(
                ["set_property", "volume", 0],
                CancellationToken.None);
            await nextDeck.Ipc.SendCommandAsync(
                ["set_property", "pause", true],
                CancellationToken.None);
            await nextDeck.Ipc.SendCommandAsync(
                ["loadfile", nextTrack.MediaUrl, "replace"],
                CancellationToken.None);
            await nextDeck.FileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await nextDeck.Ipc.SendCommandAsync(
                ["seek", 0, "absolute"],
                CancellationToken.None);
            await WaitForInitialBufferAsync(nextDeck, cancellationToken);

            fadeSeconds = Math.Min(crossfadeSeconds, Math.Max(0.1, Duration - TimePosition));
            RestoreExclusiveAfterCrossfade = previousExclusive;
            RestoreBitPerfectAfterCrossfade = previousBitPerfect;
            currentCrossfade = new CrossfadeSession(
                source,
                nextDeck,
                nextTrack,
                previousUrl,
                previousMediaUrl,
                previousVolume,
                fadeSeconds,
                previousExclusive,
                previousBitPerfect,
                crossfadeMode);
            currentCrossfade.IsPaused = pauseRequested;
            crossfadeStarting = false;
            PlayerLog.Info(
                $"Preparing {crossfadeMode} crossfade ({fadeSeconds:0.##} seconds).");

            while (!crossfadeSourceEnded &&
                   Duration - TimePosition > crossfadeSeconds)
            {
                if (currentCrossfade?.IsPaused == true)
                    await Task.Delay(100, cancellationToken);
                else
                    await Task.Delay(20, cancellationToken);
            }

            var playbackStarted = nextDeck.PlaybackStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(120),
                cancellationToken);
            await nextDeck.Ipc.SendCommandWithoutResponseAsync(
                ["set_property", "pause", false],
                CancellationToken.None);

            fadeSeconds = Math.Min(
                crossfadeSeconds,
                Math.Max(0.1, Duration - TimePosition));
            if (crossfadeSourceEnded)
            {
                await playbackStarted;
                await nextDeck.Ipc.SendCommandWithoutResponseAsync(
                    ["set_property", "volume", currentCrossfade?.TargetVolume ?? previousVolume],
                    CancellationToken.None);
                await CompleteCrossfadeAsync(currentCrossfade);
                promoted = true;
                return;
            }
            PlayerLog.Info(
                $"Both tracks are playing; starting a {fadeSeconds:0.##}-second overlap.");
            var fadeClock = Stopwatch.StartNew();
            var previousTick = fadeClock.Elapsed;
            var elapsedFade = TimeSpan.Zero;
            while (elapsedFade.TotalSeconds < fadeSeconds)
            {
                if (currentCrossfade?.IsPaused == true)
                {
                    await Task.Delay(50, cancellationToken);
                    previousTick = fadeClock.Elapsed;
                    continue;
                }

                var now = fadeClock.Elapsed;
                elapsedFade += now - previousTick;
                previousTick = now;
                var progress = Math.Clamp(elapsedFade.TotalSeconds / fadeSeconds, 0, 1);
                if (currentCrossfade is not null)
                    currentCrossfade.Progress = progress;
                var targetVolume = currentCrossfade?.TargetVolume ?? previousVolume;
                var (sourceGain, targetGain) = GetCrossfadeGains(targetVolume, progress);
                await nextDeck.Ipc.SendCommandWithoutResponseAsync(
                    ["set_property", "volume", targetGain],
                    CancellationToken.None);
                await source.Ipc.SendCommandWithoutResponseAsync(
                    ["set_property", "volume", crossfadeSourceEnded ? 0 : sourceGain],
                    CancellationToken.None);
                if (progress < 1)
                    await Task.Delay(10, cancellationToken);
            }

            await playbackStarted;
            await source.Ipc.SendCommandWithoutResponseAsync(
                ["set_property", "volume", 0],
                CancellationToken.None);
            await nextDeck.Ipc.SendCommandWithoutResponseAsync(
                ["set_property", "volume", currentCrossfade?.TargetVolume ?? previousVolume],
                CancellationToken.None);
            await CompleteCrossfadeAsync(currentCrossfade);
            promoted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (nextTrack is not null && !promoted)
                RequeuePreloadedTrack(nextTrack);
            if (!promoted && IsCrossfadeBoundaryPending)
            {
                IsCrossfadeBoundaryPending = false;
                CurrentUrl = previousUrl;
                CurrentMediaUrl = previousMediaUrl;
                TimePosition = await GetDeckDoubleAsync(source, "time-pos") ?? TimePosition;
                Duration = await GetDeckDoubleAsync(source, "duration") ?? Duration;
                EmitSyntheticEvent("""{"event":"playback-restart"}""");
            }
            if (!promoted && ReferenceEquals(activeDeck, source))
                await RestoreCrossfadeSourceAsync(source, previousVolume, previousExclusive, previousBitPerfect);
        }
        catch (Exception exception)
        {
            promoted = ReferenceEquals(activeDeck, nextDeck);
            PlayerLog.Error("Crossfade failed.", exception);
            if (nextTrack is not null && !promoted)
                RequeuePreloadedTrack(nextTrack);
            if (!promoted && IsCrossfadeBoundaryPending)
            {
                IsCrossfadeBoundaryPending = false;
                CurrentUrl = previousUrl;
                CurrentMediaUrl = previousMediaUrl;
                TimePosition = await GetDeckDoubleAsync(source, "time-pos") ?? TimePosition;
                Duration = await GetDeckDoubleAsync(source, "duration") ?? Duration;
                EmitSyntheticEvent("""{"event":"playback-restart"}""");
            }
            if (!promoted && ReferenceEquals(activeDeck, source))
                await RestoreCrossfadeSourceAsync(source, previousVolume, previousExclusive, previousBitPerfect);
        }
        finally
        {
            if (!promoted)
            {
                transitionDeck = null;
                currentCrossfade = null;
                crossfadeSourceEnded = false;
                if (nextDeck is not null)
                    await DisposeDeckAsync(nextDeck);
            }
            crossfadeStarting = false;
            if (ReferenceEquals(crossfadeCts, cancellation))
            {
                crossfadeCts.Dispose();
                crossfadeCts = null;
            }
            transitionLock.Release();
        }
    }

    private static (double Source, double Target) GetCrossfadeGains(
        double targetVolume,
        double progress)
    {
        var angle = Math.Clamp(progress, 0, 1) * Math.PI / 2;
        return (
            targetVolume * Math.Cos(angle),
            targetVolume * Math.Sin(angle));
    }

    private static async Task FadeDeckVolumeAsync(MpvDeck deck, double start, double end)
    {
        const int steps = 8;
        const int stepDelayMilliseconds = 5;
        for (var step = 1; step <= steps; step++)
        {
            var progress = step / (double)steps;
            var easedProgress = 0.5 - 0.5 * Math.Cos(progress * Math.PI);
            var volume = start + (end - start) * easedProgress;
            await deck.Ipc.SendCommandWithoutResponseAsync(
                ["set_property", "volume", volume],
                CancellationToken.None);
            await Task.Delay(stepDelayMilliseconds);
        }

        await deck.Ipc.SendCommandAsync(
            ["set_property", "volume", end],
            CancellationToken.None);
    }

    private static async Task WaitForInitialBufferAsync(
        MpvDeck deck,
        CancellationToken cancellationToken)
    {
        await deck.FileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        var timer = Stopwatch.StartNew();
        var lastLog = TimeSpan.Zero;
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cachedSeconds = await GetDeckDoubleAsync(deck, "demuxer-cache-duration") ?? 0;
            var duration = deck.DurationSeconds > 0
                ? deck.DurationSeconds
                : await GetDeckDoubleAsync(deck, "duration") ?? 0;
            var requiredSeconds = duration > 0
                ? Math.Min(InitialBufferSeconds, duration)
                : InitialBufferSeconds;
            var toleranceSeconds = duration > 0 && duration <= InitialBufferSeconds
                ? 0.25
                : 0.1;
            if (cachedSeconds >= Math.Max(0, requiredSeconds - toleranceSeconds))
            {
                PlayerLog.Info(
                    $"Initial audio buffer ready ({cachedSeconds:0.##} seconds cached).");
                return;
            }

            if (timer.Elapsed - lastLog >= TimeSpan.FromSeconds(5))
            {
                PlayerLog.Info(
                    $"Waiting for initial audio buffer ({cachedSeconds:0.##}/{requiredSeconds:0.##} seconds).");
                lastLog = timer.Elapsed;
            }
            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("MPV did not buffer the initial 10 seconds of audio within 120 seconds.");
    }

    private async Task RestoreCrossfadeSourceAsync(
        MpvDeck source,
        double volume,
        bool exclusive,
        bool bitPerfect)
    {
        try
        {
            await source.Ipc.SendCommandAsync(["set_property", "volume", volume], CancellationToken.None);
            if (source.SampleRate > 0)
            {
                await source.Ipc.SendCommandAsync(
                    ["set_property", "audio-samplerate", source.SampleRate],
                    CancellationToken.None);
                source.OutputSampleRate = source.SampleRate;
            }
            if (exclusive)
                await source.Ipc.SendCommandAsync(
                    ["set_property", "audio-exclusive", true],
                    CancellationToken.None);
            if (bitPerfect)
            {
                await source.Ipc.SendCommandAsync(
                    ["set_property", "af", Array.Empty<object>()],
                    CancellationToken.None);
                await source.Ipc.SendCommandAsync(
                    ["set_property", "volume", 100],
                    CancellationToken.None);
            }
            IsExclusiveMode = exclusive;
            IsBitPerfect = bitPerfect;
            Volume = bitPerfect ? 100 : volume;
        }
        catch (Exception exception)
        {
            PlayerLog.Error("Could not restore audio output after crossfade failure.", exception);
        }
    }

    private async Task CompleteCrossfadeAsync(CrossfadeSession? session)
    {
        if (session is null || session.Completed)
            return;

        session.Completed = true;
        var oldDeck = session.Source;
        var newDeck = session.Target;
        await newDeck.Ipc.SendCommandAsync(
            ["set_property", "volume", session.TargetVolume],
            CancellationToken.None);
        await oldDeck.Ipc.SendCommandAsync(
            ["set_property", "volume", 0],
            CancellationToken.None);
        var oldUrl = session.SourceUrl ?? CurrentUrl ?? string.Empty;
        var incomingUrl = session.NextTrack.SourceUrl;
        var incomingPosition = await GetDeckDoubleAsync(newDeck, "time-pos") ?? 0;
        var incomingDuration = await GetDeckDoubleAsync(newDeck, "duration") ?? 0;
        IsCrossfadeBoundaryPending = true;
        CurrentUrl = incomingUrl;
        CurrentMediaUrl = session.NextTrack.MediaUrl;
        TimePosition = incomingPosition;
        Duration = incomingDuration;
        IsLoaded = true;
        IsPaused = false;
        IsBuffering = false;
        hasHeardAudio = false;
        leadingSilenceDetected = false;
        silenceTransitionStarted = false;
        if (CrossfadeCompleted is { } publishBoundary)
            await publishBoundary(oldUrl, incomingUrl, incomingPosition, incomingDuration);
        activeDeck = newDeck;
        transitionDeck = null;
        currentCrossfade = null;
        IsCrossfadeBoundaryPending = false;

        try
        {
            if (session.Mode == "resample")
            {
                await FadeDeckVolumeAsync(newDeck, session.TargetVolume, 0);
                var outputSampleRate = session.RestoreBitPerfect
                    ? 0
                    : newDeck.SampleRate;
                await newDeck.Ipc.SendCommandAsync(
                    ["set_property", "audio-samplerate", outputSampleRate],
                    CancellationToken.None);
                newDeck.OutputSampleRate = outputSampleRate;

                if (session.RestoreBitPerfect)
                {
                    await newDeck.Ipc.SendCommandAsync(
                        ["set_property", "af", Array.Empty<object>()],
                        CancellationToken.None);
                }

                if (session.RestoreExclusive)
                {
                    await newDeck.Ipc.SendCommandAsync(
                        ["set_property", "audio-exclusive", true],
                        CancellationToken.None);
                    IsExclusiveMode = await GetDeckBooleanAsync(newDeck, "audio-exclusive");
                    if (!IsExclusiveMode)
                        throw new InvalidOperationException("MPV did not restore exclusive output.");
                }
                else
                {
                    IsExclusiveMode = false;
                }

                if (session.RestoreBitPerfect)
                {
                    await newDeck.Ipc.SendCommandAsync(
                        ["set_property", "volume", 100],
                        CancellationToken.None);
                    Volume = 100;
                    IsBitPerfect = true;
                }
                else
                {
                    IsBitPerfect = false;
                    Volume = session.TargetVolume;
                    await FadeDeckVolumeAsync(newDeck, 0, session.TargetVolume);
                }
            }
            else
            {
                IsExclusiveMode = false;
                IsBitPerfect = false;
                Volume = session.TargetVolume;
            }
        }
        catch (Exception exception)
        {
            IsExclusiveMode = false;
            IsBitPerfect = false;
            Volume = session.TargetVolume;
            try
            {
                await newDeck.Ipc.SendCommandAsync(
                    ["set_property", "volume", session.TargetVolume],
                    CancellationToken.None);
            }
            catch (Exception restoreException)
            {
                PlayerLog.Error(
                    "Could not restore playback volume after an audio-mode switch failure.",
                    restoreException);
            }
            PlayerLog.Error("Could not restore the incoming track's output mode after crossfade.", exception);
            EmitAudioError($"Audio output mode restoration failed: {exception.Message}");
        }

        await DisposeDeckAsync(oldDeck);

        if (session.SourceMediaUrl is not null)
            mediaServer.Remove(session.SourceMediaUrl);
        EmitSyntheticEvent("""{"event":"playback-restart"}""");
        PlayerLog.Info("Crossfade completed; the incoming track is now active.");
    }

    private static async Task<double?> GetDeckDoubleAsync(MpvDeck deck, string property)
    {
        var response = await deck.Ipc.SendCommandForResultAsync(
            ["get_property", property],
            CancellationToken.None);
        EnsureMpvSuccess(response, $"get_property {property}");
        return response.TryGetProperty("data", out var data) && data.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static async Task<bool> GetDeckBooleanAsync(MpvDeck deck, string property)
    {
        var response = await deck.Ipc.SendCommandForResultAsync(
            ["get_property", property],
            CancellationToken.None);
        EnsureMpvSuccess(response, $"get_property {property}");
        return response.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.True;
    }

    private async Task EmitVisualizerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken);
            if (!IsLoaded || IsBitPerfect || CurrentUrl is not { } url)
                continue;

            var deck = activeDeck;
            double overallDb;
            double[] bandDb;
            lock (deck.VisualizerLock)
            {
                overallDb = deck.OverallRmsDb;
                bandDb = (double[])deck.VisualizerBandDb.Clone();
            }

            var bands = new AudioBandLevel[VisualizerFrequencies.Length];
            for (var index = 0; index < bands.Length; index++)
            {
                var db = bandDb[index];
                bands[index] = new AudioBandLevel(
                    VisualizerFrequencies[index],
                    DecibelsToLevel(db),
                    db);
            }

            AudioBandsAvailable?.Invoke(
                url,
                DecibelsToLevel(overallDb),
                overallDb,
                bands);
        }
    }

    private static double DecibelsToLevel(double decibels) =>
        decibels <= -100
            ? 0
            : Math.Clamp(Math.Pow(10, decibels / 20), 0, 1);

    public async Task LoadAsync(JsonElement? command)
    {
        var urlText = GetString(command, "url") ??
                      GetString(command, "uri") ??
                      GetString(command, "streamUrl");
        if (string.IsNullOrWhiteSpace(urlText))
            throw new ArgumentException("media.load requires url");

        var uri = new Uri(urlText, UriKind.Absolute);
        await connectionTask;
        await CancelCrossfadeAsync();
        await ConfigureAudioAsync(command);
        if (CurrentUrl is not null)
            await ClearPreloadsAsync();

        RemovePreload(urlText);
        var previousMediaUrl = CurrentMediaUrl;
        CurrentUrl = urlText;
        IsLoaded = false;
        IsPaused = true;
        IsBuffering = true;
        Duration = 0;
        TimePosition = 0;
        activeDeck.DurationSeconds = 0;
        var streamFormat = GetString(command, "streamFormat") ??
                           GetString(command, "format");
        var decryptChunk = decrypter.CreateChunkDecryptor(command);
        var mediaUrl = mediaServer.Add(
            uri,
            streamFormat,
            decryptChunk,
            GetDouble(command, "duration"));
        CurrentMediaUrl = mediaUrl;
        activeDeck.MediaUrl = mediaUrl;
        PlayerLog.Info($"Streaming media as '{streamFormat ?? "detected format"}' to MPV.");
        await ipc.SendCommandAsync(["set_property", "pause", true], CancellationToken.None);
        activeDeck.ResetFileLoaded();
        activeDeck.ResetPlaybackStarted();
        await ipc.SendCommandAsync(["loadfile", mediaUrl, "replace"], CancellationToken.None);
        mediaServer.Remove(previousMediaUrl);
        if (!crossfadeEnabled)
            await AppendPendingPreloadsAsync();
        PlayerLog.Info("MPV load command sent.");
    }

    public async Task PreloadAsync(JsonElement? command)
    {
        var urlText = GetString(command, "url") ??
                      GetString(command, "uri") ??
                      GetString(command, "streamUrl");
        if (string.IsNullOrWhiteSpace(urlText))
            throw new ArgumentException("media.preload requires url");

        var uri = new Uri(urlText, UriKind.Absolute);
        await connectionTask;
        lock (preloadLock)
        {
            if (preloadedTracks.Any(track => track.SourceUrl == urlText))
            {
                PlayerLog.Info("Preload already queued; skipping duplicate.");
                return;
            }
        }

        var streamFormat = GetString(command, "streamFormat") ?? GetString(command, "format");
        var decryptChunk = decrypter.CreateChunkDecryptor(command);
        var mediaUrl = mediaServer.Add(
            uri,
            streamFormat,
            decryptChunk,
            GetDouble(command, "duration"));
        var preloadedTrack = new PreloadedTrack(urlText, mediaUrl);
        lock (preloadLock)
            preloadedTracks.Add(preloadedTrack);

        if (CurrentUrl is not null && !crossfadeEnabled)
            await AppendTrackAsync(preloadedTrack);

        PlayerLog.Info("Preloaded streaming source without downloading the full track.");
    }

    public async Task CancelPreloadAsync()
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        await ClearPreloadsAsync();
        PlayerLog.Info("Cancelled queued preloads.");
    }

    private async Task AppendPendingPreloadsAsync()
    {
        while (true)
        {
            PreloadedTrack? track;
            lock (preloadLock)
                track = preloadedTracks.FirstOrDefault(candidate => !candidate.Appended);
            if (track is null)
                return;
            await AppendTrackAsync(track);
        }
    }

    private async Task AppendTrackAsync(PreloadedTrack track)
    {
        await ipc.SendCommandAsync(["loadfile", track.MediaUrl, "append"], CancellationToken.None);
        lock (preloadLock)
            track.Appended = true;
        PlayerLog.Info("Queued a preloaded track in MPV.");
    }

    private PreloadedTrack? DequeuePreloadedTrack()
    {
        lock (preloadLock)
        {
            if (preloadedTracks.Count == 0)
                return null;
            var track = preloadedTracks[0];
            preloadedTracks.RemoveAt(0);
            return track;
        }
    }

    private PreloadedTrack? TakeNextPreloadedTrack() => DequeuePreloadedTrack();

    private void RequeuePreloadedTrack(PreloadedTrack track)
    {
        lock (preloadLock)
            preloadedTracks.Insert(0, track);
    }

    private async Task ClearMpvPlaylistAsync()
    {
        PreloadedTrack[] tracks;
        lock (preloadLock)
            tracks = preloadedTracks.Where(track => track.Appended).ToArray();
        if (tracks.Length == 0)
            return;

        await ipc.SendCommandAsync(["playlist-clear"], CancellationToken.None);
        lock (preloadLock)
        {
            foreach (var track in tracks)
                track.Appended = false;
        }
    }

    private void EmitSyntheticEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        PropertyEvent?.Invoke(document.RootElement.Clone());
    }

    private void EmitAudioError(string message)
    {
        EmitSyntheticEvent(JsonSerializer.Serialize(new
        {
            @event = "log-message",
            level = "error",
            text = message
        }));
    }

    private async Task CancelCrossfadeAsync()
    {
        var cancellation = crossfadeCts;
        if (cancellation is not null)
            await cancellation.CancelAsync();

        await transitionLock.WaitAsync();
        try
        {
            if (transitionDeck is not null)
            {
                var deck = transitionDeck;
                transitionDeck = null;
                currentCrossfade = null;
                await DisposeDeckAsync(deck);
            }
        }
        finally
        {
            transitionLock.Release();
        }
    }

    private void RemovePreload(string sourceUrl)
    {
        PreloadedTrack? track;
        lock (preloadLock)
        {
            track = preloadedTracks.FirstOrDefault(candidate => candidate.SourceUrl == sourceUrl);
            if (track is not null)
                preloadedTracks.Remove(track);
        }

        if (track is not null)
            mediaServer.Remove(track.MediaUrl);
    }

    private async Task ClearPreloadsAsync()
    {
        PreloadedTrack[] tracks;
        lock (preloadLock)
        {
            tracks = preloadedTracks.ToArray();
            preloadedTracks.Clear();
        }

        if (tracks.Any(track => track.Appended))
            await ipc.SendCommandAsync(["playlist-clear"], CancellationToken.None);

        foreach (var track in tracks)
            mediaServer.Remove(track.MediaUrl);
    }

    public async Task ConfigureCrossfadeAsync(JsonElement? command)
    {
        await connectionTask;
        var enabled = GetBool(command, "enabled") ??
                      throw new ArgumentException("media.crossfade.configure requires enabled");
        var seconds = GetDouble(command, "seconds") ??
                      GetDouble(command, "duration") ??
                      crossfadeSeconds;
        if (seconds < 0 || seconds > 15)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Crossfade duration must be between 0 and 15 seconds.");
        if (enabled && seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Enabled crossfades require a duration greater than 0.");

        var mode = GetString(command, "mode") ?? crossfadeMode;
        mode = mode.Trim().ToLowerInvariant() switch
        {
            "shared" or "shared-only" => "shared",
            "resample" => "resample",
            _ => throw new ArgumentException($"Unsupported crossfade mode '{mode}'.")
        };
        if (enabled && mode == "shared" && (IsExclusiveMode || IsBitPerfect))
        {
            throw new InvalidOperationException(
                "Shared crossfades require shared audio output; disable exclusive and bit-perfect modes first.");
        }

        if (enabled)
            await UnqueuePreloadsAsync();

        crossfadeEnabled = enabled;
        crossfadeSeconds = seconds;
        crossfadeMode = mode;
        PlayerLog.Info(
            $"Crossfade configured: enabled={crossfadeEnabled}, seconds={crossfadeSeconds}, mode={crossfadeMode}.");

        if (!enabled)
            await AppendPendingPreloadsAsync();
    }

    private async Task UnqueuePreloadsAsync()
    {
        PreloadedTrack[] tracks;
        lock (preloadLock)
            tracks = preloadedTracks.Where(track => track.Appended).ToArray();

        if (tracks.Length == 0)
            return;

        await ipc.SendCommandAsync(["playlist-clear"], CancellationToken.None);
        lock (preloadLock)
        {
            foreach (var track in tracks)
                track.Appended = false;
        }
    }

    public async Task PlayAsync()
    {
        await connectionTask;
        if (CurrentUrl is null)
            return;
        pauseRequested = false;
        var playbackStarted = activeDeck.PlaybackStarted.Task;
        IsBuffering = true;
        try
        {
            await WaitForInitialBufferAsync(activeDeck, CancellationToken.None);
            await ipc.SendCommandAsync(["set_property", "pause", false], CancellationToken.None);
            await playbackStarted.WaitAsync(TimeSpan.FromSeconds(120));
        }
        finally
        {
            IsBuffering = false;
        }
        if (transitionDeck is not null)
            await transitionDeck.Ipc.SendCommandAsync(["set_property", "pause", false], CancellationToken.None);
        IsPaused = false;
        if (currentCrossfade is not null)
            currentCrossfade.IsPaused = false;
    }

    public async Task PauseAsync()
    {
        await connectionTask;
        pauseRequested = true;
        await ipc.SendCommandAsync(["set_property", "pause", true], CancellationToken.None);
        if (transitionDeck is not null)
            await transitionDeck.Ipc.SendCommandAsync(["set_property", "pause", true], CancellationToken.None);
        IsPaused = true;
        if (currentCrossfade is not null)
            currentCrossfade.IsPaused = true;
    }

    public async Task StopAsync()
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        await ClearPreloadsAsync();
        await ipc.SendCommandAsync(["stop"], CancellationToken.None);
        IsLoaded = false;
        IsBuffering = false;
        IsPaused = false;
        CurrentUrl = null;
        mediaServer.Remove(CurrentMediaUrl);
        CurrentMediaUrl = null;
    }

    public async Task SeekAsync(JsonElement? command)
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        var position = GetDouble(command, "position") ??
                       GetDouble(command, "seek") ??
                       GetDouble(command, "time");
        var percent = GetDouble(command, "percent");

        if (position is not null)
            await ipc.SendCommandAsync(["seek", position.Value, "absolute"], CancellationToken.None);
        else if (percent is not null)
            await ipc.SendCommandAsync(["seek", percent.Value, "absolute-percent"], CancellationToken.None);
    }

    public async Task SetVolumeAsync(JsonElement? command)
    {
        var volume = GetDouble(command, "volume") ??
                     GetDouble(command, "value");
        if (volume is null)
            throw new ArgumentException("media.volume requires volume");
        if (IsBitPerfect && volume.Value != 100)
            throw new InvalidOperationException("Software volume changes are disabled in bit-perfect mode.");

        var targetVolume = Math.Clamp(volume.Value, 0, 100);
        await connectionTask;
        if (currentCrossfade is { } session && transitionDeck is not null)
        {
            session.TargetVolume = targetVolume;
            var progress = session.Progress;
            var (sourceGain, incomingGain) = GetCrossfadeGains(targetVolume, progress);
            await session.Source.Ipc.SendCommandWithoutResponseAsync(
                [
                    "set_property",
                    "volume",
                    session.SourceEnded ? 0 : sourceGain
                ],
                CancellationToken.None);
            await session.Target.Ipc.SendCommandWithoutResponseAsync(
                ["set_property", "volume", incomingGain],
                CancellationToken.None);
        }
        else
        {
            await ipc.SendCommandAsync(["set_property", "volume", targetVolume], CancellationToken.None);
        }
        Volume = targetVolume;
    }

    public async Task ConfigureAudioAsync(JsonElement? command)
    {
        await connectionTask;
        await CancelCrossfadeAsync();

        var mode = GetAudioMode(command);
        var bitPerfect = GetBool(command, "bitPerfect") ??
                         GetBool(command, "bitperfect") ??
                         GetOptionBool(command, "bitPerfect") ??
                         GetOptionBool(command, "bitperfect") ??
                         mode.BitPerfect ??
                         IsBitPerfect;
        var exclusive = GetBool(command, "exclusiveMode") ??
                        GetBool(command, "exclusive") ??
                        GetOptionBool(command, "exclusiveMode") ??
                        GetOptionBool(command, "exclusive") ??
                        mode.Exclusive ??
                        IsExclusiveMode;
        if (bitPerfect)
            exclusive = true;

        if (crossfadeEnabled && crossfadeMode == "shared" && (exclusive || bitPerfect))
        {
            throw new InvalidOperationException(
                "The shared-only crossfade mode cannot be used with exclusive or bit-perfect output.");
        }

        if (bitPerfect && CurrentDevice is not null &&
            CurrentDevice != "auto" &&
            !CurrentDevice.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Bit-perfect mode requires the MPV WASAPI audio output.");
        }
        if (!OperatingSystem.IsWindows() && (exclusive || bitPerfect))
            throw new InvalidOperationException(
                "Exclusive and bit-perfect output modes are supported only on Windows WASAPI.");

        await ipc.SendCommandAsync(["set_property", "audio-exclusive", exclusive], CancellationToken.None);
        var actualExclusive = await GetPropertyBooleanAsync("audio-exclusive");
        if (actualExclusive != exclusive)
        {
            throw new InvalidOperationException(
                $"MPV did not accept the requested exclusive mode (reported {actualExclusive}).");
        }

        if (bitPerfect)
        {
            await ipc.SendCommandAsync(
                ["set_property", "af", Array.Empty<object>()],
                CancellationToken.None);
            await ipc.SendCommandAsync(["set_property", "audio-samplerate", 0], CancellationToken.None);
            activeDeck.OutputSampleRate = 0;
            await ipc.SendCommandAsync(["set_property", "volume", 100], CancellationToken.None);
            Volume = 100;
            IsBitPerfect = true;
        }
        else
        {
            await ipc.SendCommandAsync(["set_property", "audio-samplerate", 0], CancellationToken.None);
            activeDeck.OutputSampleRate = 0;
            await SetAudioFiltersAsync(activeDeck, bitPerfect: false, equalizerGains, equalizerEnabled);
            IsBitPerfect = false;
        }

        IsExclusiveMode = exclusive;
        PlayerLog.Info(
            $"Audio output configured: exclusive={IsExclusiveMode}, bitPerfect={IsBitPerfect}.");
    }

    public async Task ConfigureEqualizerAsync(JsonElement? command)
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        if (command is not { ValueKind: JsonValueKind.Object } root)
            throw new ArgumentException("media.equalizer.configure requires an object.");

        var updatedGains = (double[])equalizerGains.Clone();
        var hasUpdate = false;
        if (root.TryGetProperty("gains", out var gains))
        {
            if (gains.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("media.equalizer.configure gains must be an object.");

            foreach (var gain in gains.EnumerateObject())
            {
                var bandIndex = Array.FindIndex(
                    EqualizerBands,
                    band => string.Equals(band.Name, gain.Name, StringComparison.OrdinalIgnoreCase));
                if (bandIndex < 0)
                    throw new ArgumentException(
                        $"Unknown EQ band '{gain.Name}'. Use: {string.Join(", ", EqualizerBands.Select(band => band.Name))}.");
                if (gain.Value.ValueKind != JsonValueKind.Number ||
                    !gain.Value.TryGetDouble(out var value) ||
                    !double.IsFinite(value) ||
                    value is < -12 or > 12)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(command),
                        $"EQ gain for {EqualizerBands[bandIndex].Name} must be between -12 and 12 dB.");
                }

                updatedGains[bandIndex] = value;
                hasUpdate = true;
            }
        }

        var updatedEnabled = equalizerEnabled;
        if (root.TryGetProperty("enabled", out var enabled))
        {
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("media.equalizer.configure enabled must be a boolean.");
            updatedEnabled = enabled.GetBoolean();
            hasUpdate = true;
        }

        if (!hasUpdate)
            throw new ArgumentException("media.equalizer.configure requires gains or enabled.");

        await SetAudioFiltersAsync(activeDeck, IsBitPerfect, updatedGains, updatedEnabled);
        equalizerGains = updatedGains;
        equalizerEnabled = updatedEnabled;
        PlayerLog.Info(
            $"Equalizer configured: enabled={equalizerEnabled}, active={equalizerEnabled && !IsBitPerfect}.");
    }

    private static string BuildAudioFilterGraph(IReadOnlyList<double> gains, bool enabled)
    {
        var filters = new List<string> { "aformat=channel_layouts=stereo" };
        if (enabled)
        {
            for (var index = 0; index < EqualizerBands.Length; index++)
            {
                var gain = gains[index];
                if (gain == 0)
                    continue;

                filters.Add(
                    $"equalizer=f={EqualizerBands[index].Frequency}:t=o:w=1:g={gain.ToString("0.##", CultureInfo.InvariantCulture)}");
            }
        }

        var taps = string.Concat(
            Enumerable.Range(0, VisualizerFrequencies.Length)
                .Select(index => $"[tap{index}]"));
        var graph = new List<string>
        {
            $"[aid1]{string.Join(",", filters)},asplit={VisualizerFrequencies.Length + 1}[main]{taps}",
            "[main]asplit=2[program][rms]",
            "[program]silencedetect=noise=-52dB:d=0.65[out]",
            "[rms]aselect=expr='isnan(prev_selected_t)+gte(t-prev_selected_t,0.1)'," +
            "astats=metadata=1:reset=1:measure_overall=RMS_level:measure_perchannel=none," +
            "ametadata=mode=add:key=band:value=0," +
            "ametadata=mode=print:file=-:direct=1,anullsink"
        };

        for (var index = 0; index < VisualizerFrequencies.Length; index++)
        {
            var frequency = VisualizerFrequencies[index];
            graph.Add(
                $"[tap{index}]bandpass=f={frequency}:t=o:w=0.333," +
                "aselect=expr='isnan(prev_selected_t)+gte(t-prev_selected_t,0.1)'," +
                "astats=metadata=1:reset=1:measure_overall=RMS_level:measure_perchannel=none," +
                $"ametadata=mode=add:key=band:value={frequency}," +
                "ametadata=mode=print:file=-:direct=1,anullsink");
        }

        return string.Join(";", graph);
    }

    private static Task SetAudioFiltersAsync(
        MpvDeck deck,
        bool bitPerfect,
        IReadOnlyList<double> gains,
        bool enabled)
    {
        object[] filters = bitPerfect
            ? []
            :
            [
                new Dictionary<string, object?>
                {
                    ["name"] = "lavfi",
                    ["enabled"] = true,
                    ["params"] = new
                    {
                        graph = BuildAudioFilterGraph(gains, enabled)
                    }
                }
            ];
        return deck.Ipc.SendCommandAsync(
            ["set_property", "af", filters],
            CancellationToken.None);
    }

    public async Task SelectDeviceAsync(JsonElement? command)
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        var device = GetDeviceId(command);
        if (string.IsNullOrWhiteSpace(device))
            throw new ArgumentException("media.device.select requires device");

        var devices = await ListDevicesAsync();
        var selected = ResolveDevice(device, devices);
        var mode = GetAudioMode(command);
        var bitPerfect = GetBool(command, "bitPerfect") ??
                         GetBool(command, "bitperfect") ??
                         GetOptionBool(command, "bitPerfect") ??
                         GetOptionBool(command, "bitperfect") ??
                         mode.BitPerfect;
        if (bitPerfect == true && selected.Type != "wasapi" && selected.Type != "default")
            throw new InvalidOperationException("Bit-perfect mode requires a WASAPI audio device.");
        if (!OperatingSystem.IsWindows() &&
            (bitPerfect == true || mode.Exclusive == true))
        {
            throw new InvalidOperationException(
                "Exclusive and bit-perfect output modes are supported only on Windows WASAPI.");
        }
        if (crossfadeEnabled &&
            crossfadeMode == "shared" &&
            (mode.Exclusive == true || bitPerfect == true))
        {
            throw new InvalidOperationException(
                "The shared-only crossfade mode cannot be used with exclusive or bit-perfect output.");
        }

        PlayerLog.Info($"Selecting MPV audio device '{selected.Id}'.");
        try
        {
            await SetAndVerifyDeviceAsync(selected.Id);
        }
        catch (InvalidOperationException directFailure)
        {
            PlayerLog.Info(
                $"Direct switch to '{selected.Id}' failed; retrying after resetting MPV's audio device.");
            try
            {
                await ipc.SendCommandAsync(["set_property", "audio-device", "auto"], CancellationToken.None);
                await SetAndVerifyDeviceAsync(selected.Id);
            }
            catch (InvalidOperationException retryFailure)
            {
                throw new InvalidOperationException(
                    $"MPV could not switch to requested device '{selected.Id}' after refreshing its audio output.",
                    new AggregateException(directFailure, retryFailure));
            }
        }
        CurrentDevice = selected.Id;

        if (HasAudioConfiguration(command))
            await ConfigureAudioAsync(command);
    }

    private async Task SetAndVerifyDeviceAsync(string deviceId)
    {
        await ipc.SendCommandAsync(["set_property", "audio-device", deviceId], CancellationToken.None);
        var actualDevice = await GetPropertyStringAsync("audio-device");
        if (!string.Equals(actualDevice, deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"MPV did not switch to requested device '{deviceId}' (reported '{actualDevice ?? "unknown"}').");
    }

    public async Task ReleaseDeviceAsync()
    {
        await connectionTask;
        await CancelCrossfadeAsync();
        CurrentDevice = null;
        PlayerLog.Info("Releasing selected audio device; switching to auto.");
        await ipc.SendCommandAsync(["set_property", "audio-device", "auto"], CancellationToken.None);
    }

    public async Task SetMuteAsync(JsonElement? command)
    {
        await connectionTask;
        var mute = GetBool(command, "mute") ?? GetBool(command, "value");
        if (mute is not null)
        {
            await ipc.SendCommandAsync(["set_property", "mute", mute.Value], CancellationToken.None);
            if (transitionDeck is not null)
                await transitionDeck.Ipc.SendCommandAsync(
                    ["set_property", "mute", mute.Value],
                    CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = process.StartInfo.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--audio-device=help");
        startInfo.ArgumentList.Add("--idle=yes");

        using var deviceProcess = Process.Start(startInfo);
        if (deviceProcess is null)
            return [];
        if (processJob.IsSupported &&
            !processJob.TryAssign(deviceProcess, out var jobError))
        {
            PlayerLog.Error(
                "Could not attach the MPV device-list process to the shutdown job.",
                jobError!);
        }
        var outputTask = deviceProcess.StandardOutput.ReadToEndAsync();
        var errorTask = deviceProcess.StandardError.ReadToEndAsync();
        await Task.WhenAll(outputTask, errorTask, deviceProcess.WaitForExitAsync());
        var output = await outputTask;
        var errors = await errorTask;

        var devices = new List<DeviceInfo>();
        foreach (var line in (output + "\n" + errors).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("List of audio devices:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.StartsWith("'") && trimmed.Contains("'"))
            {
                var end = trimmed.IndexOf('\'', 1);
                if (end > 0)
                {
                    var id = trimmed[1..end];
                    var name = trimmed[(end + 1)..].Trim();
                    if (name.StartsWith("(") && name.EndsWith(")"))
                        name = name[1..^1];
                    var type = id == "auto"
                        ? "default"
                        : id.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase)
                            ? "wasapi"
                            : "other";
                    devices.Add(new DeviceInfo(id, name, type, type != "other"));
                }
            }
        }
        return devices;
    }

    public async Task SendCurrentStateAsync()
    {
        await connectionTask;
        await ipc.SendCommandAsync(["get_property", "time-pos"], CancellationToken.None);
        await ipc.SendCommandAsync(["get_property", "duration"], CancellationToken.None);
        await ipc.SendCommandAsync(["get_property", "volume"], CancellationToken.None);
    }

    private static string? GetString(JsonElement? root, string name)
    {
        if (root is not { } value || value.ValueKind != JsonValueKind.Object)
            return null;
        return value.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string? GetOptionString(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return options.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static bool? GetOptionBool(JsonElement? root, string name)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("options", out var options) ||
            options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return options.TryGetProperty(name, out var prop) &&
               prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;
    }

    private static (bool? Exclusive, bool? BitPerfect) GetAudioMode(JsonElement? root)
    {
        var mode = GetOptionString(root, "mode");
        if (mode is null)
            return (null, null);

        return mode.Trim().ToLowerInvariant() switch
        {
            "shared" or "normal" => (false, false),
            "exclusive" => (true, false),
            "bitperfect" or "bit-perfect" or "bit_perfect" => (true, true),
            _ => throw new ArgumentException($"Unsupported audio mode '{mode}'.")
        };
    }

    private static bool HasAudioConfiguration(JsonElement? command) =>
        GetBool(command, "exclusiveMode") is not null ||
        GetBool(command, "exclusive") is not null ||
        GetBool(command, "bitPerfect") is not null ||
        GetBool(command, "bitperfect") is not null ||
        GetOptionBool(command, "exclusiveMode") is not null ||
        GetOptionBool(command, "exclusive") is not null ||
        GetOptionBool(command, "bitPerfect") is not null ||
        GetOptionBool(command, "bitperfect") is not null ||
        GetOptionString(command, "mode") is not null;

    private static string? GetDeviceId(JsonElement? root)
    {
        var device = GetString(root, "device");
        if (device is not null)
            return device;

        if (root is { ValueKind: JsonValueKind.Object } value &&
            value.TryGetProperty("device", out var deviceElement) &&
            deviceElement.ValueKind == JsonValueKind.Object &&
            deviceElement.TryGetProperty("id", out var nestedId) &&
            nestedId.ValueKind == JsonValueKind.String)
        {
            return nestedId.GetString();
        }

        return GetString(root, "id");
    }

    private static DeviceInfo ResolveDevice(string requestedId, IReadOnlyList<DeviceInfo> devices)
    {
        var directMatch = devices.FirstOrDefault(device =>
            string.Equals(device.Id, requestedId, StringComparison.OrdinalIgnoreCase));
        if (directMatch is not null)
            return directMatch;

        var namedMatch = devices.FirstOrDefault(device =>
            string.Equals(device.Name, requestedId, StringComparison.OrdinalIgnoreCase));
        if (namedMatch is not null)
            return namedMatch;

        var openBrace = requestedId.LastIndexOf('{');
        var closeBrace = requestedId.IndexOf('}', openBrace + 1);
        if (openBrace >= 0 && closeBrace > openBrace &&
            Guid.TryParse(requestedId[(openBrace + 1)..closeBrace], out var guid))
        {
            var suffix = guid.ToString("B");
            var guidMatch = devices.FirstOrDefault(device =>
                device.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (guidMatch is not null)
                return guidMatch;
        }

        throw new ArgumentException($"Audio device '{requestedId}' is not in the current MPV device list.");
    }

    private async Task<string?> GetPropertyStringAsync(string property)
    {
        var response = await ipc.SendCommandForResultAsync(
            ["get_property", property],
            CancellationToken.None);
        EnsureMpvSuccess(response, $"get_property {property}");
        return response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : null;
    }

    private async Task<bool> GetPropertyBooleanAsync(string property)
    {
        var response = await ipc.SendCommandForResultAsync(
            ["get_property", property],
            CancellationToken.None);
        EnsureMpvSuccess(response, $"get_property {property}");
        return response.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.True;
    }

    private static void EnsureMpvSuccess(JsonElement response, string operation)
    {
        if (!response.TryGetProperty("error", out var error) ||
            error.GetString() != "success")
        {
            throw new InvalidOperationException(
                $"MPV failed to {operation}: {response}");
        }
    }

    private static async Task DisposeDeckAsync(MpvDeck deck)
    {
        if (Interlocked.Exchange(ref deck.DisposeStarted, 1) != 0)
            return;

        deck.ExpectedExit = true;
        try
        {
            if (!deck.Process.HasExited)
            {
                await deck.Ipc.SendCommandWithoutResponseAsync(["quit"], CancellationToken.None);
                await deck.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or TimeoutException)
        {
            PlayerLog.Info($"MPV pid {deck.Process.Id} did not exit gracefully: {exception.Message}");
        }

        try
        {
            try
            {
                await deck.Ipc.DisposeAsync();
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException)
            {
                PlayerLog.Info(
                    $"MPV pid {deck.Process.Id} IPC closed with an error: {exception.Message}");
            }

            if (!deck.Process.HasExited)
            {
                deck.Process.Kill(true);
                await deck.Process.WaitForExitAsync();
            }
        }
        catch (Win32Exception exception)
        {
            PlayerLog.Error($"Could not terminate MPV pid {deck.Process.Id}.", exception);
        }
        finally
        {
            try
            {
                await Task.WhenAll(deck.StandardOutputTask, deck.StandardErrorTask);
            }
            finally
            {
                deck.Process.Dispose();
            }
        }
    }

    private static double? GetDouble(JsonElement? root, string name)
    {
        if (root is not { } value || value.ValueKind != JsonValueKind.Object)
            return null;
        if (!value.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var number))
            return number;
        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out number))
            return number;
        return null;
    }

    private static bool? GetBool(JsonElement? root, string name)
    {
        if (root is not { } value || value.ValueKind != JsonValueKind.Object)
            return null;
        return value.TryGetProperty(name, out var prop) &&
               prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;
    }

    public async ValueTask DisposeAsync()
    {
        shuttingDown = true;
        try
        {
            await CancelCrossfadeAsync();
            await visualizerCts.CancelAsync();
            if (visualizerTask is not null)
            {
                try
                {
                    await visualizerTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            var remainingTransitionDeck = transitionDeck;
            transitionDeck = null;
            if (remainingTransitionDeck is not null &&
                !ReferenceEquals(remainingTransitionDeck, activeDeck))
            {
                await DisposeDeckAsync(remainingTransitionDeck);
            }

            await DisposeDeckAsync(activeDeck);
            await mediaServer.DisposeAsync();
        }
        finally
        {
            processJob.Dispose();
        }
    }
}

public sealed record DeviceInfo(
    string Id,
    string Name,
    string Type,
    bool ControllableVolume);

public sealed record AudioBandLevel(
    int Frequency,
    double Level,
    double Db);
