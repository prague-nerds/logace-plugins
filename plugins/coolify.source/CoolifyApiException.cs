using System;
using System.IO;

namespace LogAce.Plugins.Coolify;

internal sealed class CoolifyApiException(string message, TimeSpan? retryAfter = null) : IOException(message)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}
