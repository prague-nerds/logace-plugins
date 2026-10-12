using System.Collections.Generic;
using System.Text.Json;

namespace LogAce.Plugins.OsLog;

/// <summary>A stable discovery ID and display label, plus metadata updated with selection.</summary>
internal sealed record BrowseOption(string Id, string Label, IReadOnlyDictionary<string, JsonElement>? Updates = null);
