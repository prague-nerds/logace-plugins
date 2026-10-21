using LogAce.PluginApi;
namespace LogAce.Columns.bots_ip;
using System.Net.Http.Headers;
using System.Text;
// Each file is replaced only after the complete download has been parsed successfully.
// The last usable snapshot remains available to readers throughout an update.
internal sealed class BotDatabaseStore<T> where T : class
{
    internal const int MaximumDownloadBytes = 8 * 1024 * 1024;
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly string _path;
    private readonly string _sourceUrl;
    private readonly Func<string, T> _parse;
    private readonly Func<T, int> _count;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private Lazy<Snapshot> _snapshot;

    public BotDatabaseStore(string path, string sourceUrl, Func<string, T> parse, Func<T, int> count, HttpClient? httpClient = null)
    {
        _path = path;
        _sourceUrl = sourceUrl;
        _parse = parse;
        _count = count;
        _httpClient = httpClient ?? DefaultHttpClient;
        _snapshot = new Lazy<Snapshot>(Load);
    }

    public void Reload() => Volatile.Write(ref _snapshot, new Lazy<Snapshot>(Load));

    public T? Database => Volatile.Read(ref _snapshot).Value.Database;

    public PluginDataStatus Status
    {
        get
        {
            Snapshot snapshot = Volatile.Read(ref _snapshot).Value;
            return new PluginDataStatus(snapshot.Database is null ? 0 : _count(snapshot.Database), snapshot.DownloadedAt, snapshot.Error);
        }
    }

    public async Task UpdateAsync(CancellationToken cancellationToken = default, IProgress<PluginDataProgress>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        cancellationToken = timeout.Token;
        await _updateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            progress?.Report(new(PluginDataDownloadStage.Connecting));
            using var request = new HttpRequestMessage(HttpMethod.Get, _sourceUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LogAce", "1.0"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The bot database download is too large.");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var payload = new MemoryStream();
            long? totalBytes = response.Content.Headers.ContentLength;
            progress?.Report(new(PluginDataDownloadStage.Downloading, 0, totalBytes));
            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (payload.Length + bytesRead > MaximumDownloadBytes)
                {
                    throw new InvalidDataException("The bot database download is too large.");
                }

                payload.Write(buffer, 0, bytesRead);
                progress?.Report(new(PluginDataDownloadStage.Downloading, payload.Length, totalBytes));
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(PluginDataDownloadStage.Validating, payload.Length, totalBytes));
            string text = new UTF8Encoding(false, true).GetString(payload.GetBuffer(), 0, checked((int)payload.Length)).TrimStart('\uFEFF');
            T database = _parse(text);
            if (_count(database) == 0)
            {
                throw new InvalidDataException("The bot database is empty.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(PluginDataDownloadStage.Saving, payload.Length, totalBytes));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, overwrite: true);
            var snapshot = new Snapshot(database, new DateTimeOffset(File.GetLastWriteTimeUtc(_path)), null);
            Volatile.Write(ref _snapshot, new Lazy<Snapshot>(() => snapshot));
            progress?.Report(new(PluginDataDownloadStage.Completed, payload.Length, totalBytes));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _updateLock.Release();
        }
    }

    private Snapshot Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Snapshot(null, null, null);
            }

            if (new FileInfo(_path).Length > MaximumDownloadBytes)
            {
                throw new InvalidDataException("The saved bot database is too large.");
            }

            T database = _parse(File.ReadAllText(_path));
            if (_count(database) == 0)
            {
                throw new InvalidDataException("The saved bot database is empty.");
            }

            return new Snapshot(database, new DateTimeOffset(File.GetLastWriteTimeUtc(_path)), null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            return new Snapshot(null, null, exception.Message);
        }
    }

    private sealed record Snapshot(T? Database, DateTimeOffset? DownloadedAt, string? Error);
}
