using System.Text.Json;
using System.Security.Cryptography;

namespace TidalMpvPlayer;

public sealed record DecryptionResult(
    byte[] AudioBuffer,
    string? ContentType = null);

public interface IAudioDecryptionProvider
{
    ValueTask<DecryptionResult> DecryptAsync(
        ReadOnlyMemory<byte> encryptedAudio,
        JsonElement manifest,
        CancellationToken cancellationToken);
}

public interface IStreamingAudioDecryptionProvider : IAudioDecryptionProvider
{
    byte[] DecryptChunk(
        ReadOnlyMemory<byte> encryptedAudio,
        JsonElement manifest,
        long byteOffset,
        CancellationToken cancellationToken);
}

public sealed class OldAesDecryptionProvider :
    IAudioDecryptionProvider,
    IStreamingAudioDecryptionProvider
{
    private readonly byte[] masterKey;

    public OldAesDecryptionProvider(string? masterKeyBase64 = null)
    {
        var configuredKey = masterKeyBase64 ?? BuildMasterKey.GetBase64();
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            masterKey = [];
            return;
        }

        try
        {
            masterKey = Convert.FromBase64String(configuredKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "The build master key must contain a base64-encoded AES key.",
                exception);
        }

        if (masterKey.Length is not 16 and not 24 and not 32)
            throw new InvalidOperationException(
                "The build master key must decode to a valid AES key length.");
    }

    public ValueTask<DecryptionResult> DecryptAsync(
        ReadOnlyMemory<byte> encryptedAudio,
        JsonElement manifest,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new DecryptionResult(
            DecryptChunk(encryptedAudio, manifest, 0, cancellationToken)));
    }

    public byte[] DecryptChunk(
        ReadOnlyMemory<byte> encryptedAudio,
        JsonElement manifest,
        long byteOffset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (byteOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        if (masterKey.Length == 0)
            throw new InvalidOperationException(
                "OLD_AES playback requires a master key embedded during the build.");
        if (!TryGetPropertyIgnoreCase(manifest, "keyId", out var keyIdElement) &&
            !TryGetPropertyIgnoreCase(manifest, "encryptionKey", out keyIdElement))
            throw new InvalidDataException("OLD_AES manifest is missing keyId/encryptionKey.");

        if (
            keyIdElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(keyIdElement.GetString()))
            throw new InvalidDataException("OLD_AES keyId/encryptionKey must be a non-empty base64 string.");

        byte[] token;
        try
        {
            token = Convert.FromBase64String(keyIdElement.GetString()!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("OLD_AES keyId is not valid base64.", exception);
        }

        if (token.Length <= 16 || (token.Length - 16) % 16 != 0)
            throw new InvalidDataException("OLD_AES keyId has an invalid encrypted token length.");

        var tokenIv = token.AsSpan(0, 16);
        var encryptedToken = token.AsSpan(16);
        byte[] decryptedToken;
        using (var aes = Aes.Create())
        {
            aes.Key = masterKey;
            aes.IV = tokenIv.ToArray();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var decryptor = aes.CreateDecryptor();
            try
            {
                decryptedToken = decryptor.TransformFinalBlock(
                    encryptedToken.ToArray(), 0, encryptedToken.Length);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("Could not decrypt the OLD_AES keyId token.", exception);
            }
        }

        if (decryptedToken.Length < 24)
            throw new InvalidDataException("Decrypted OLD_AES token does not contain a key and nonce.");

        var audioKey = decryptedToken.AsSpan(0, 16).ToArray();
        var counter = new byte[16];
        decryptedToken.AsSpan(16, 8).CopyTo(counter);
        return DecryptCtr(encryptedAudio.Span, audioKey, counter, byteOffset, cancellationToken);
    }

    private static byte[] DecryptCtr(
        ReadOnlySpan<byte> input,
        byte[] key,
        byte[] counter,
        long byteOffset,
        CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var output = new byte[input.Length];
        var counterBlock = new byte[16];
        var keyStream = new byte[16];
        AddCounter(counter, (ulong)(byteOffset / 16));
        var inputOffset = 0;
        var keyStreamOffset = (int)(byteOffset % 16);
        while (inputOffset < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counter.CopyTo(counterBlock, 0);
            _ = encryptor.TransformBlock(counterBlock, 0, 16, keyStream, 0);
            var count = Math.Min(16 - keyStreamOffset, input.Length - inputOffset);
            for (var i = 0; i < count; i++)
                output[inputOffset + i] =
                    (byte)(input[inputOffset + i] ^ keyStream[keyStreamOffset + i]);
            inputOffset += count;
            keyStreamOffset = 0;
            IncrementCounter(counter);
        }
        return output;
    }

    private static void AddCounter(byte[] counter, ulong blockCount)
    {
        for (var i = counter.Length - 1; i >= 0 && blockCount > 0; i--)
        {
            var sum = counter[i] + (blockCount & 0xff);
            counter[i] = (byte)sum;
            blockCount = (blockCount >> 8) + (sum >> 8);
        }
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

public sealed class AudioDecrypter
{
    private readonly IAudioDecryptionProvider decryptionProvider;

    public AudioDecrypter(IAudioDecryptionProvider? decryptionProvider = null)
    {
        this.decryptionProvider = decryptionProvider ?? new OldAesDecryptionProvider();
    }

    public Func<ReadOnlyMemory<byte>, long, CancellationToken, byte[]> CreateChunkDecryptor(
        JsonElement? manifest)
    {
        var manifestValue = GetManifest(manifest);
        var encryptionType = GetEncryptionType(manifestValue);
        if (encryptionType is null)
            throw new InvalidDataException(
                "Playback request is missing manifest encryptionType/keyId; refusing to pass the media through as clear audio.");
        if (encryptionType is not "NONE" and not "OLD_AES")
            throw new NotSupportedException(
                $"Unsupported manifest encryption type '{encryptionType}'.");

        if (encryptionType == "NONE")
            return (chunk, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return chunk.ToArray();
            };

        if (decryptionProvider is not IStreamingAudioDecryptionProvider streamingProvider)
            throw new NotSupportedException(
                "The configured decryption provider does not support streaming. Implement IStreamingAudioDecryptionProvider.");

        var manifestSnapshot = manifestValue.Clone();
        return (chunk, byteOffset, cancellationToken) =>
            streamingProvider.DecryptChunk(
                chunk,
                manifestSnapshot,
                byteOffset,
                cancellationToken);
    }

    private static JsonElement GetManifest(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value)
            return default;

        if (TryFindManifest(value, out var manifest))
            return manifest;

        return value;
    }

    private static bool TryFindManifest(JsonElement value, out JsonElement manifest)
    {
        if (HasProperty(value, "encryptionType") ||
            HasProperty(value, "keyId") ||
            HasProperty(value, "encryptionKey"))
        {
            manifest = value;
            return true;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                TryFindManifest(property.Value, out manifest))
                return true;

            if (property.Name.Equals("manifest", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                TryParseManifestString(property.Value.GetString(), out manifest))
                return true;
        }

        manifest = default;
        return false;
    }

    private static bool TryParseManifestString(string? text, out JsonElement manifest)
    {
        manifest = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var candidates = new List<string> { text };
        try
        {
            candidates.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text)));
        }
        catch (FormatException)
        {
            // The manifest may already be plain JSON rather than base64.
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    TryFindManifest(document.RootElement, out var found))
                {
                    manifest = found.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
                // Not a JSON encoded manifest; try the next representation.
            }
        }

        return false;
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetEncryptionType(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetProperty(manifest, "encryptionType", out var type) &&
            type.ValueKind == JsonValueKind.String)
            return type.GetString()?.Trim().ToUpperInvariant();

        // Some OLD_AES playback responses include keyId but omit the type label.
        if (TryGetProperty(manifest, "keyId", out var keyId) &&
            keyId.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(keyId.GetString()))
            return "OLD_AES";

        if (TryGetProperty(manifest, "encryptionKey", out var encryptionKey) &&
            encryptionKey.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(encryptionKey.GetString()))
            return "OLD_AES";

        return null;
    }
}

internal static class HttpClientProvider
{
    public static HttpClient Client { get; } = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
}
