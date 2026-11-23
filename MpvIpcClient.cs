using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TidalMpvPlayer;

public sealed class MpvIpcClient : IAsyncDisposable
{
    private readonly string pipeName;
    private Stream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private readonly Lock sendLock = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingRequests = new();
    private readonly CancellationTokenSource cts = new();
    private Task? readerTask;
    private int nextRequestId;

    public event Action<JsonElement>? EventReceived;

    public MpvIpcClient(string pipeName)
    {
        this.pipeName = pipeName;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var namedPipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await namedPipe.ConnectAsync(cancellationToken);
            pipe = namedPipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(pipeName), cancellationToken);
            pipe = new NetworkStream(socket, ownsSocket: true);
        }

        reader = new StreamReader(pipe!, Encoding.UTF8, leaveOpen: true);
        writer = new StreamWriter(pipe!, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        readerTask = Task.Run(() => ReadEventsAsync(cts.Token), cts.Token);
    }

    public Task SendAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request);
        lock (sendLock)
            writer!.WriteLine(json);
        return Task.CompletedTask;
    }

    public async Task SendCommandAsync(
        IReadOnlyList<object?> command,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandForResultAsync(command, cancellationToken);
        if (response.TryGetProperty("error", out var error) &&
            error.GetString() != "success")
        {
            throw new InvalidOperationException(
                $"MPV rejected command '{command[0]}': {error.GetString()}");
        }
    }

    public Task SendCommandWithoutResponseAsync(
        IReadOnlyList<object?> command,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.SerializeToElement(new { command });
        return SendAsync(request, cancellationToken);
    }

    public async Task<JsonElement> SendCommandForResultAsync(
        IReadOnlyList<object?> command,
        CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(requestId, completion))
            throw new InvalidOperationException("Could not register MPV IPC request.");

        try
        {
            var request = JsonSerializer.SerializeToElement(new
            {
                command,
                request_id = requestId
            });
            await SendAsync(request, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            pendingRequests.TryRemove(requestId, out _);
        }
    }

    public async Task SendRequestAsync(
        IReadOnlyList<object?> command,
        int requestId,
        CancellationToken cancellationToken)
    {
        await SendAsync(JsonSerializer.SerializeToElement(new
        {
            command,
            request_id = requestId
        }), cancellationToken);
    }

    private async Task ReadEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader!.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                JsonDocument? document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException exception)
                {
                    PlayerLog.Error("Received invalid JSON from MPV IPC.", exception);
                    continue;
                }
                using (document)
                {
                    var message = document.RootElement.Clone();
                    if (message.TryGetProperty("request_id", out var requestIdElement) &&
                        requestIdElement.TryGetInt32(out var requestId) &&
                        pendingRequests.TryGetValue(requestId, out var completion))
                    {
                        completion.TrySetResult(message);
                    }
                    EventReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException exception)
        {
            PlayerLog.Error("MPV IPC connection ended with an I/O error.", exception);
        }
        finally
        {
            foreach (var completion in pendingRequests.Values)
                completion.TrySetException(new IOException("MPV IPC connection closed."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        try
        {
            try
            {
                reader?.Dispose();
            }
            catch (IOException exception)
            {
                PlayerLog.Info($"MPV IPC reader closed with an I/O error: {exception.Message}");
            }

            try
            {
                writer?.Dispose();
            }
            catch (IOException exception)
            {
                PlayerLog.Info($"MPV IPC writer closed with an I/O error: {exception.Message}");
            }
        }
        finally
        {
            pipe?.Dispose();
            cts.Dispose();
        }

        if (readerTask is not null)
        {
            try
            {
                await readerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
