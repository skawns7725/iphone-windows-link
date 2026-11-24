using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace PhotoReceiver;

public sealed class UploadSessions(string destination) : IDisposable
{
    private readonly MemoryCache sessions = new(new MemoryCacheOptions());

    private sealed class PendingFile(UploadFile metadata)
    {
        public UploadFile Metadata { get; } = metadata;
        public string Token { get; } = NewId();
        public SemaphoreSlim Gate { get; } = new(1);
        public bool Saved { get; set; }
    }

    private sealed class Session(Dictionary<string, PendingFile> files, IPAddress sender)
    {
        public Dictionary<string, PendingFile> Files { get; } = files;
        public IPAddress Sender { get; } = sender;
        public bool Canceled { get; set; }
    }

    public static bool HasValidMetadata(PrepareRequest request) =>
        !string.IsNullOrWhiteSpace(request.Info?.Alias) &&
        request.Files is not null &&
        request.Files.All(entry => entry.Value is { Size: >= 0 } file &&
            !string.IsNullOrWhiteSpace(entry.Key) && file.Id == entry.Key &&
            !string.IsNullOrWhiteSpace(file.FileName) &&
            (file.Sha256 is null || (file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit))));

    public PrepareResponse Prepare(Dictionary<string, UploadFile?> metadata, IPAddress sender)
    {
        var id = NewId();
        var files = metadata.ToDictionary(entry => entry.Key, entry => new PendingFile(entry.Value!));
        sessions.Set(id, new Session(files, sender), new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(1)
        });
        return new PrepareResponse(id, files.ToDictionary(entry => entry.Key, entry => entry.Value.Token));
    }

    public bool Cancel(string sessionId, IPAddress? sender)
    {
        if (!sessions.TryGetValue<Session>(sessionId, out var session) || session is null ||
            !session.Sender.Equals(sender))
            return false;

        lock (session)
        {
            session.Canceled = true;
            sessions.Remove(sessionId);
        }
        return true;
    }

    public async Task<IResult> UploadAsync(string sessionId, string fileId, string token, HttpContext context)
    {
        if (!sessions.TryGetValue<Session>(sessionId, out var session) || session is null ||
            !session.Sender.Equals(context.Connection.RemoteIpAddress) ||
            !session.Files.TryGetValue(fileId, out var file) ||
            !string.Equals(file.Token, token, StringComparison.Ordinal))
            return Results.StatusCode(403);

        var cancellationToken = context.RequestAborted;
        await file.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (session)
            {
                if (session.Canceled)
                    return Results.StatusCode(403);
                if (file.Saved)
                    return Results.Ok();
            }

            var expectedSize = file.Metadata.Size!.Value;
            if (context.Request.ContentLength is long contentLength && contentLength != expectedSize)
                return Results.UnprocessableEntity();

            var started = Stopwatch.StartNew();
            var tempPath = Path.Combine(destination, $".{NewId()}.part");
            try
            {
                if (!await ReceiveAsync(context.Request.Body, tempPath, expectedSize,
                    file.Metadata.Sha256, cancellationToken))
                    return Results.UnprocessableEntity();

                string savedName;
                lock (session)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (session.Canceled)
                        return Results.StatusCode(403);
                    savedName = MoveIntoPlace(tempPath, file.Metadata.FileName);
                    file.Saved = true;
                }
                Console.WriteLine($"{savedName} | {expectedSize:N0} bytes | {started.Elapsed.TotalSeconds:F3} s");
                return Results.Ok();
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
        finally
        {
            file.Gate.Release();
        }
    }

    private static async Task<bool> ReceiveAsync(Stream source, string tempPath, long expectedSize,
        string? expectedHash, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);
        using var hash = expectedHash is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var remaining = expectedSize;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                if (count > remaining)
                    return false;
                remaining -= count;
                hash?.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            return remaining == 0 && (hash is null ||
                string.Equals(expectedHash, Convert.ToHexString(hash.GetHashAndReset()),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string MoveIntoPlace(string tempPath, string requestedName)
    {
        var leaf = Path.GetFileName(requestedName.Replace('\\', '/'));
        const string invalid = "<>:\"/\\|?*";
        var name = string.Concat(leaf.EnumerateRunes().Take(55).Select(rune =>
            rune.Value < 32 || (rune.IsAscii && invalid.Contains((char)rune.Value))
                ? "_" : rune.ToString())).Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "photo";

        var firstComponent = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        if (firstComponent is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (firstComponent.Length == 4 && (firstComponent.StartsWith("COM", StringComparison.Ordinal) ||
                firstComponent.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(firstComponent[3])))
            name = "_" + name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 0; ; suffix++)
        {
            var candidate = suffix == 0 ? name : $"{stem} ({suffix}){extension}";
            var path = Path.Combine(destination, candidate);
            try
            {
                File.Move(tempPath, path);
                return candidate;
            }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public void Dispose() => sessions.Dispose();
}
