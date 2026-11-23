using System.Text.Json;

namespace TidalMpvPlayer;

public sealed class ProtocolServer
{
    private readonly MpvPlayer player;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim outputLock = new(1, 1);
    private readonly Lock stateLock = new();
    private int messageSequence = 44;
    private string? lastEmittedState;
    private string? lastEmittedStateUrl;

    public ProtocolServer(MpvPlayer player)
    {
        this.player = player;
    }

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        PlayerLog.Info("JSON command loop started.");
        await EmitVersionAsync(output);
        player.PropertyEvent += eventElement => EmitMpvProperty(output, eventElement);
        player.AudioBandsAvailable += (url, level, db, bands) =>
            _ = EmitAudioBandsAsync(output, url, level, db, bands);
        player.CrossfadeCompleted += (oldUrl, incomingUrl, incomingTime, incomingDuration) =>
            EmitCrossfadeCompletedAsync(output, oldUrl, incomingUrl, incomingTime, incomingDuration);
        var heartbeatTask = EmitHeartbeatAsync(output, cts.Token);

        while (!cts.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cts.Token);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var commandName = document.RootElement.TryGetProperty("command", out var commandProp)
                    ? commandProp.GetString() ?? "<invalid>"
                    : "<missing>";
                PlayerLog.Info($"Received command: {commandName}.");
                await HandleAsync(document.RootElement, output);
            }
            catch (Exception ex)
            {
                PlayerLog.Error("Command processing failed.", ex);
                await WriteAsync(output, new
                {
                    signal = "error",
                    error = ex.Message
                });
            }
        }

        cts.Cancel();
        await heartbeatTask;
    }

    private async Task EmitHeartbeatAsync(TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var url = player.CurrentUrl;
                if (url is not null &&
                    !player.IsBuffering &&
                    !player.IsCrossfadeBoundaryPending)
                {
                    await WriteAsync(output, new
                    {
                        signal = "media.currenttime",
                        time = player.TimePosition,
                        url
                    });
                }
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(JsonElement command, TextWriter output)
    {
        if (!command.TryGetProperty("command", out var commandProp) ||
            commandProp.ValueKind != JsonValueKind.String)
        {
            await WriteAsync(output, new
            {
                signal = "error",
                error = "Missing command"
            });
            return;
        }

        var name = commandProp.GetString();
        switch (name)
        {
            case "media.load":
                await player.LoadAsync(command);
                await WriteStateAsync(output, "loading", player.CurrentUrl);
                break;
            case "media.preload":
                await player.PreloadAsync(command);
                await WriteAsync(output, new { signal = "media.state", state = "preloading" });
                break;
            case "media.cancelpreload":
                await player.CancelPreloadAsync();
                await WriteAsync(output, new { signal = "media.state", state = "cancelled" });
                break;
            case "media.crossfade.configure":
                await player.ConfigureCrossfadeAsync(command);
                break;
            case "media.recover":
            case "media.play":
                await player.PlayAsync();
                if (player.CurrentUrl is not null && !player.IsBuffering)
                    await WriteStateAsync(output, "active", player.CurrentUrl);
                break;
            case "media.pause":
                await player.PauseAsync();
                if (player.CurrentUrl is not null)
                    await WriteStateAsync(output, "paused", player.CurrentUrl);
                break;
            case "media.stop":
                var stoppedUrl = player.CurrentUrl;
                await player.StopAsync();
                await WriteStateAsync(output, "stopped", stoppedUrl);
                break;
            case "media.seek":
                await player.SeekAsync(command);
                break;
            case "media.volume":
                await player.SetVolumeAsync(command);
                await WriteAsync(output, new { signal = "media.volume", volume = player.Volume });
                break;
            case "media.volume.mute":
            case "media.mute":
                await player.SetMuteAsync(command);
                break;
            case "media.device.list":
                var devices = await player.ListDevicesAsync();
                await WriteAsync(output, new
                {
                    signal = "media.devices",
                    devices = devices.Select(d => new
                    {
                        id = ToTidalDeviceId(d.Id),
                        sinkId = ToTidalDeviceId(d.Id),
                        name = d.Name,
                        type = "default",
                        controllableVolume = d.ControllableVolume
                    })
                });
                break;
            case "media.device.select":
                await player.SelectDeviceAsync(command);
                break;
            case "media.audio.configure":
                await player.ConfigureAudioAsync(command);
                await WriteAsync(output, new
                {
                    signal = "media.audio.configured",
                    exclusive = player.IsExclusiveMode,
                    bitPerfect = player.IsBitPerfect,
                    volume = player.Volume
                });
                break;
            case "media.equalizer.configure":
                await player.ConfigureEqualizerAsync(command);
                await WriteAsync(output, new
                {
                    signal = "media.equalizer.configured",
                    enabled = player.EqualizerEnabled,
                    active = player.EqualizerEnabled && !player.IsBitPerfect,
                    gains = player.EqualizerGains
                });
                break;
            case "media.device.release":
                await player.ReleaseDeviceAsync();
                await WriteAsync(output, new { signal = "media.device.released" });
                break;
            case "media.state":
            case "media.status":
                await WriteStateAsync(
                    output,
                    player.IsBuffering ? "loading" :
                        !player.IsLoaded ? "idle" :
                        player.IsPaused ? "paused" : "active",
                    player.CurrentUrl,
                    force: true);
                if (player.CurrentUrl is not null)
                {
                    if (!player.IsBuffering)
                    {
                        await WriteAsync(output, new
                        {
                            signal = "media.currenttime",
                            time = player.TimePosition,
                            url = player.CurrentUrl
                        });
                    }
                    await WriteAsync(output, new
                    {
                        signal = "media.duration",
                        duration = player.Duration,
                        url = player.CurrentUrl
                    });
                }
                break;
            default:
                await WriteAsync(output, new
                {
                    signal = "CommandNack",
                    command = name
                });
                break;
        }
    }

    private async Task EmitVersionAsync(TextWriter output)
    {
        await WriteAsync(output, new
        {
            signal = "version",
            version = "1.0.0"
        });
    }

    private void EmitMpvProperty(TextWriter output, JsonElement eventElement)
    {
        try
        {
            if (!eventElement.TryGetProperty("event", out var eventProp))
                return;

            if (eventProp.GetString() == "playback-restart")
            {
                if (player.CurrentUrl is not null)
                {
                    _ = WriteStateAsync(
                        output,
                        player.IsPaused ? "paused" : "active",
                        player.CurrentUrl);
                }
            }

            else if (eventProp.GetString() == "log-message")
            {
                var level = eventElement.TryGetProperty("level", out var logLevel)
                    ? logLevel.GetString()
                    : null;
                var text = eventElement.TryGetProperty("text", out var logText)
                    ? logText.GetString() ?? string.Empty
                    : string.Empty;
                if (level is "warn" or "error" &&
                    (text.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("wasapi", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("exclusive", StringComparison.OrdinalIgnoreCase)))
                {
                    _ = WriteAsync(output, new
                    {
                        signal = "media.audio.error",
                        level,
                        message = text
                    });
                }
            }
            else if (eventProp.GetString() == "property-change")
            {
                var name = eventElement.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString()
                    : null;

                if (name == "duration" &&
                         eventElement.TryGetProperty("data", out var duration))
                {
                    _ = WriteAsync(output, new
                    {
                        signal = "media.duration",
                        duration = duration.TryGetDouble(out var seconds) ? seconds : 0,
                        url = player.CurrentUrl
                    });
                }
                else if (name == "pause" &&
                         eventElement.TryGetProperty("data", out var paused))
                {
                    if (player.CurrentUrl is not null &&
                        player.IsLoaded &&
                        !player.IsBuffering)
                    {
                        _ = WriteStateAsync(
                            output,
                            paused.GetBoolean() ? "paused" : "active",
                            player.CurrentUrl);
                    }
                }
                else if (name == "volume" &&
                         eventElement.TryGetProperty("data", out var volume) &&
                         !player.IsCrossfading)
                {
                    _ = WriteAsync(output, new
                    {
                        signal = "media.volume",
                        volume = volume.TryGetDouble(out var level) ? level : 0,
                        url = player.CurrentUrl
                    });
                }
                else if (name == "audio-params" &&
                         eventElement.TryGetProperty("data", out var audioParams))
                {
                    _ = WriteAsync(output, new
                    {
                        signal = "media.audio.format",
                        audioParams
                    });
                }
            }
            else if (eventProp.GetString() == "end-file")
            {
                var reason = eventElement.TryGetProperty("reason", out var endReason)
                    ? endReason.GetString()
                    : null;
                var url = player.CurrentUrl;
                if (reason == "error")
                {
                    var message = eventElement.TryGetProperty("file_error", out var fileError)
                        ? fileError.GetString() ?? "MPV could not play the media."
                        : "MPV could not play the media.";
                    _ = WriteAsync(output, new
                    {
                        signal = "media.error",
                        error = message,
                        url
                    });
                }
                else if (reason == "eof")
                {
                    _ = WriteStateAsync(output, "completed", url, force: true);
                }
            }
        }
        catch
        {
        }
    }

    private async Task EmitAudioBandsAsync(
        TextWriter output,
        string url,
        double level,
        double db,
        IReadOnlyList<AudioBandLevel> bands)
    {
        await WriteAsync(output, new
        {
            signal = "media.audio.level",
            level,
            db
        });
        await WriteAsync(output, new
        {
            signal = "media.audio.bands",
            url,
            bands = bands.Select(band => new
            {
                frequency = band.Frequency,
                level = band.Level,
                db = band.Db
            })
        });
    }

    private async Task WriteAsync(TextWriter output, object value)
    {
        await outputLock.WaitAsync();
        try
        {
            await WriteMessageAsync(output, value);
            await output.FlushAsync();
        }
        finally
        {
            outputLock.Release();
        }
    }

    private async Task EmitCrossfadeCompletedAsync(
        TextWriter output,
        string oldUrl,
        string incomingUrl,
        double incomingTime,
        double incomingDuration)
    {
        lock (stateLock)
        {
            lastEmittedState = "active";
            lastEmittedStateUrl = incomingUrl;
        }

        await outputLock.WaitAsync();
        try
        {
            await WriteMessageAsync(output, new
            {
                signal = "media.state",
                state = "completed",
                url = oldUrl
            });
            await WriteMessageAsync(output, new
            {
                signal = "media.state",
                state = "loading",
                url = incomingUrl
            });
            await WriteMessageAsync(output, new
            {
                signal = "media.duration",
                duration = incomingDuration,
                url = incomingUrl
            });
            await WriteMessageAsync(output, new
            {
                signal = "media.state",
                state = "active",
                url = incomingUrl
            });
            await WriteMessageAsync(output, new
            {
                signal = "media.currenttime",
                time = incomingTime,
                url = incomingUrl
            });
            await output.FlushAsync();
        }
        finally
        {
            outputLock.Release();
        }
    }

    private async Task WriteMessageAsync(TextWriter output, object value)
    {
        var sequence = Interlocked.Increment(ref messageSequence);
        var json = JsonSerializer.Serialize(value);
        await output.WriteLineAsync($"{sequence}:{json}");
    }

    private async Task WriteStateAsync(
        TextWriter output,
        string state,
        string? url,
        bool force = false)
    {
        lock (stateLock)
        {
            if (!force &&
                state == lastEmittedState &&
                string.Equals(url, lastEmittedStateUrl, StringComparison.Ordinal))
            {
                return;
            }
            lastEmittedState = state;
            lastEmittedStateUrl = url;
        }

        await WriteAsync(output, new
        {
            signal = "media.state",
            state,
            url
        });
    }

    private static string ToTidalDeviceId(string mpvId)
    {
        const string wasapiPrefix = "wasapi/";
        const string tidalWasapiPrefix = "{0.0.0.00000000}.";
        if (!mpvId.StartsWith(wasapiPrefix, StringComparison.OrdinalIgnoreCase))
            return mpvId;

        var deviceId = mpvId[wasapiPrefix.Length..];
        return deviceId.StartsWith(tidalWasapiPrefix, StringComparison.OrdinalIgnoreCase)
            ? deviceId
            : tidalWasapiPrefix + deviceId;
    }
}
