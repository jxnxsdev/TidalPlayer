using TidalMpvPlayer;

try
{
    PlayerLog.Info($"Player starting. Log file: {PlayerLog.FilePath}");
    var mpvPath = FindMpvPath(args);
    PlayerLog.Info($"Using MPV executable: {mpvPath}");

    await using var player = new MpvPlayer(mpvPath);
    var server = new ProtocolServer(player);
    await server.RunAsync(Console.In, Console.Out);
    PlayerLog.Info("Player stopped normally.");
}
catch (Exception exception)
{
    PlayerLog.Error("Fatal player error.", exception);
    throw;
}

static string FindMpvPath(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--mpv-path")
        {
            var path = args[i + 1];
            if (File.Exists(path))
                return path;
            throw new FileNotFoundException("MPV executable was not found", path);
        }
    }

    return MpvRuntime.ExtractBundledRuntime();
}
