using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TidalMpvPlayer;

public sealed class StreamingMediaServer : IAsyncDisposable
{
    private sealed class MediaSource(
        Uri sourceUri,
        string contentType,
        Func<ReadOnlyMemory<byte>, long, CancellationToken, byte[]> decryptChunk)
    {
        public Uri SourceUri { get; } = sourceUri;
        public string ContentType { get; } = contentType;
        public Func<ReadOnlyMemory<byte>, long, CancellationToken, byte[]> DecryptChunk { get; } =
            decryptChunk;
        public double? DurationSeconds { get; set; }
        public long? ContentLength { get; set; }
    }

    private const int InitialProbeBytes = 256 * 1024;
    private const int MaximumCachedSeconds = 30;

    private readonly TcpListener listener;
    private readonly Dictionary<string, MediaSource> media = new(StringComparer.Ordinal);
    private readonly Lock mediaLock = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptTask;
    private readonly string baseUrl;

    public StreamingMediaServer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        baseUrl = $"http://127.0.0.1:{port}/";
        acceptTask = AcceptAsync(cts.Token);
    }

    public string Add(
        Uri sourceUri,
        string? format,
        Func<ReadOnlyMemory<byte>, long, CancellationToken, byte[]> decryptChunk,
        double? durationSeconds = null)
    {
        var (extension, contentType) = ResolveFormat(sourceUri, format);
        var id = Guid.NewGuid().ToString("N") + extension;
        lock (mediaLock)
            media[id] = new MediaSource(sourceUri, contentType, decryptChunk)
            {
                DurationSeconds = durationSeconds
            };
        return baseUrl + id;
    }

    public void SetDuration(string? url, double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        var id = uri.AbsolutePath.Trim('/');
        lock (mediaLock)
        {
            if (media.TryGetValue(id, out var source))
                source.DurationSeconds = durationSeconds;
        }
    }

    public void Remove(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        var id = uri.AbsolutePath.Trim('/');
        lock (mediaLock)
            media.Remove(id);
    }

    private static (string Extension, string ContentType) ResolveFormat(
        Uri sourceUri,
        string? format)
    {
        var extension = format?.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
            extension = Path.GetExtension(sourceUri.AbsolutePath).TrimStart('.').ToLowerInvariant();

        return extension switch
        {
            "flac" => (".flac", "audio/flac"),
            "m4a" or "mp4" => (".m4a", "audio/mp4"),
            "mp3" => (".mp3", "audio/mpeg"),
            "aac" => (".aac", "audio/aac"),
            "ogg" or "opus" => (".ogg", "audio/ogg"),
            "wav" => (".wav", "audio/wav"),
            _ => (".bin", "application/octet-stream")
        };
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                PlayerLog.Error("Could not accept a local MPV media connection.", exception);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                var parts = requestLine.Split(' ', 3);
                if (parts.Length < 2)
                    return;

                string? rangeHeader = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
                {
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                        rangeHeader = line.Split(':', 2)[1].Trim();
                }

                var path = parts[1].Split('?', 2)[0].Trim('/');
                MediaSource? source;
                lock (mediaLock)
                    media.TryGetValue(path, out source);
                if (source is null)
                {
                    await WriteTextAsync(
                        stream,
                        "HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\n",
                        cancellationToken);
                    return;
                }

                await ProxyRequestAsync(
                    stream,
                    parts[0],
                    rangeHeader,
                    source,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception exception)
            {
                PlayerLog.Error(
                    "Streaming a media response to MPV failed.",
                    new IOException(exception.GetType().Name));
            }
        }
    }

    private static async Task ProxyRequestAsync(
        Stream clientStream,
        string method,
        string? rangeHeader,
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var isHead = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        var requestedRange = ParseRange(rangeHeader);
        var effectiveRangeHeader = rangeHeader;
        if (!isHead)
        {
            var totalLength = await GetContentLengthAsync(source, cancellationToken);
            if (totalLength is { } total && total > 0)
            {
                var chunkBytes = GetChunkByteCount(source, total);
                var start = requestedRange?.Start ?? 0;
                var end = requestedRange?.End;
                if (requestedRange?.SuffixLength is { } suffix)
                {
                    start = Math.Max(0, total - suffix);
                    end = total - 1;
                    if (suffix > chunkBytes)
                        start = Math.Max(0, total - chunkBytes);
                }
                else if (end is null || end.Value - start + 1 > chunkBytes)
                {
                    end = Math.Min(total - 1, start + chunkBytes - 1);
                }

                if (requestedRange is null ||
                    end != requestedRange.Value.End ||
                    requestedRange.Value.SuffixLength is not null)
                {
                    effectiveRangeHeader = $"bytes={start}-{end}";
                }
            }
            else if (requestedRange is null || requestedRange.Value.End is null)
            {
                var start = requestedRange?.Start ?? 0;
                effectiveRangeHeader = $"bytes={start}-{start + InitialProbeBytes - 1}";
            }
        }

        using var request = new HttpRequestMessage(
            isHead ? HttpMethod.Head : HttpMethod.Get,
            source.SourceUri);
        if (!string.IsNullOrWhiteSpace(effectiveRangeHeader))
            request.Headers.TryAddWithoutValidation("Range", effectiveRangeHeader);

        using var response = await HttpClientProvider.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseCode = response.StatusCode;
        var responseContentRange = response.Content.Headers.ContentRange;
        var responseLength = response.Content.Headers.ContentLength;
        var effectiveRange = ParseRange(effectiveRangeHeader);
        var outputOffset = responseContentRange?.From ?? 0;
        var outputRange = responseContentRange?.ToString();
        var outputStatus = (int)responseCode;
        var skipBytes = 0L;
        long? maxBytesToServe = null;

        if (isHead)
        {
            outputStatus = (int)HttpStatusCode.OK;
            responseLength = responseContentRange?.Length ?? responseLength ?? source.ContentLength;
            outputRange = null;
        }
        else if (response.IsSuccessStatusCode &&
                 responseCode != HttpStatusCode.PartialContent &&
                 effectiveRange is { } range)
        {
            var totalLength = responseLength ?? source.ContentLength;
            var start = range.SuffixLength is { } suffix && totalLength is { } total
                ? Math.Max(0, total - suffix)
                : range.Start;
            var end = range.SuffixLength is not null
                ? totalLength is { } suffixTotal ? suffixTotal - 1 : null
                : range.End ?? (totalLength is { } length ? length - 1 : null);
            skipBytes = start;
            outputOffset = start;
            outputStatus = (int)HttpStatusCode.PartialContent;
            if (end is { } rangeEnd && totalLength is { } fullLength)
            {
                end = Math.Min(rangeEnd, fullLength - 1);
                maxBytesToServe = Math.Max(0, end.Value - start + 1);
                responseLength = maxBytesToServe;
                outputRange = $"bytes {start}-{end}/{fullLength}";
            }
            else
            {
                outputRange = null;
            }
        }
        else if (responseCode == HttpStatusCode.PartialContent &&
                 effectiveRange is { End: { } rangeEnd } &&
                 responseContentRange?.From is { } rangeStart)
        {
            outputOffset = rangeStart;
            maxBytesToServe = Math.Max(0, rangeEnd - rangeStart + 1);
            responseLength = maxBytesToServe;
        }

        if (responseContentRange?.Length is { } contentLength)
            source.ContentLength = contentLength;
        else if (response.Content.Headers.ContentLength is { } knownLength &&
                 responseCode != HttpStatusCode.PartialContent)
            source.ContentLength = knownLength;

        await WriteHeadersAsync(
            clientStream,
            outputStatus,
            outputStatus == (int)responseCode
                ? response.ReasonPhrase
                : ReasonPhrase(outputStatus),
            response.Content.Headers.ContentType?.ToString() ?? source.ContentType,
            responseLength,
            outputRange,
            cancellationToken);

        if (isHead)
            return;

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await responseStream.CopyToAsync(clientStream, cancellationToken);
            return;
        }
        if (skipBytes > 0)
            await SkipBytesAsync(responseStream, skipBytes, cancellationToken);
        await CopyDecryptedAsync(
            responseStream,
            clientStream,
            source,
            outputOffset,
            cancellationToken,
            maxBytesToServe);
    }

    private static async Task<long?> GetContentLengthAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        if (source.ContentLength is { } cachedLength)
            return cachedLength;

        long? length;
        using (var request = new HttpRequestMessage(HttpMethod.Head, source.SourceUri))
        using (var response = await HttpClientProvider.Client.SendAsync(
                   request,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            length = response.IsSuccessStatusCode
                ? response.Content.Headers.ContentRange?.Length ??
                  response.Content.Headers.ContentLength
                : null;
        }

        if (length is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.SourceUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await HttpClientProvider.Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            length = response.IsSuccessStatusCode
                ? response.Content.Headers.ContentRange?.Length ??
                  response.Content.Headers.ContentLength
                : null;
        }

        source.ContentLength = length;
        return length;
    }

    private static long GetChunkByteCount(MediaSource source, long totalLength)
    {
        if (totalLength <= 64 * 1024)
            return totalLength;
        if (source.DurationSeconds is not { } duration || duration <= 0)
            return Math.Min(totalLength, InitialProbeBytes);

        var averageBytesPerSecond = totalLength / duration;
        var chunkBytes = (long)Math.Ceiling(averageBytesPerSecond * MaximumCachedSeconds);
        return Math.Clamp(chunkBytes, 64 * 1024, totalLength);
    }

    private static (long Start, long? End, long? SuffixLength)? ParseRange(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return null;

        var bounds = header[6..].Split('-', 2);
        if (bounds.Length != 2)
            return null;
        if (bounds[0].Length == 0)
        {
            return long.TryParse(bounds[1], out var suffix) && suffix > 0
                ? (0L, (long?)null, (long?)suffix)
                : null;
        }

        if (!long.TryParse(bounds[0], out var start) || start < 0)
            return null;
        long? end = long.TryParse(bounds[1], out var parsedEnd) ? parsedEnd : null;
        return (start, end, null);
    }

    private static async Task SkipBytesAsync(
        Stream stream,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)),
                cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The media server ended before the requested byte range.");
            count -= read;
        }
    }

    private static async Task CopyDecryptedAsync(
        Stream sourceStream,
        Stream destinationStream,
        MediaSource source,
        long byteOffset,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        var buffer = new byte[64 * 1024];
        while (maxBytes is null || maxBytes > 0)
        {
            var readLimit = maxBytes is { } remaining
                ? (int)Math.Min(buffer.Length, remaining)
                : buffer.Length;
            var read = await sourceStream.ReadAsync(buffer.AsMemory(0, readLimit), cancellationToken);
            if (read == 0)
                return;

            var clear = source.DecryptChunk(
                buffer.AsMemory(0, read),
                byteOffset,
                cancellationToken);
            await destinationStream.WriteAsync(clear, cancellationToken);
            byteOffset += read;
            if (maxBytes is not null)
                maxBytes -= read;
        }
    }

    private static async Task WriteHeadersAsync(
        Stream stream,
        int statusCode,
        string? reason,
        string contentType,
        long? contentLength,
        string? contentRange,
        CancellationToken cancellationToken)
    {
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ")
            .Append(statusCode)
            .Append(' ')
            .Append(reason ?? ReasonPhrase(statusCode))
            .Append("\r\n")
            .Append("Content-Type: ")
            .Append(contentType)
            .Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n");
        if (contentLength is { } length)
            headers.Append("Content-Length: ").Append(length).Append("\r\n");
        if (contentRange is not null)
            headers.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        headers.Append("Connection: close\r\n\r\n");
        await WriteTextAsync(stream, headers.ToString(), cancellationToken);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        206 => "Partial Content",
        404 => "Not Found",
        416 => "Range Not Satisfiable",
        502 => "Bad Gateway",
        _ => "Error"
    };

    private static async Task WriteTextAsync(
        Stream stream,
        string text,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        listener.Stop();
        try
        {
            await acceptTask;
        }
        catch (OperationCanceledException)
        {
        }
        cts.Dispose();
        lock (mediaLock)
            media.Clear();
    }
}
