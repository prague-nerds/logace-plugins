using System;
using System.IO;
using System.Threading;

namespace LogAce.Plugins.Journalctl;

/// <summary>Idempotent ownership of a completed snapshot's host-provided spool.</summary>
internal sealed class TemporaryFileLifetime(string path) : IDisposable
{
    private string? _path = path;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _path, null) is { } owned)
        {
            Delete(owned);
        }
    }

    internal static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
