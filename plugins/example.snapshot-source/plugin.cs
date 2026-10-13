using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Examples;

public sealed class TextSnapshotPlugin : ILogAceSourcePlugin
{
    public PluginMetadata Metadata { get; } = new("example.snapshot-source", "Text snapshot",
        "Open entered text as a temporary log snapshot.", PluginKind.Source,
        Version: "1.0.1", Capabilities: ["ProvideSourceConfigurationUi", "ReadsLocalFiles"]);

    public PluginSourcePresentation Presentation { get; } = new("Examples", ["text", "snapshot"]);

    public PluginSourceConfiguration CreateDefaultConfiguration() => new(1, PluginSourceJson.Object(
        new Dictionary<string, JsonElement> { ["text"] = PluginSourceJson.Value("INFO|Hello from a source plugin") }));

    public IPluginSourceUiRequest GetConfigurationUi(PluginSourceConfigurationContext context) =>
        new PluginSourceFormRequest(new("Open text snapshot",
            [new("text", "Log text", PluginSourceFieldKind.MultilineText) { Required = true }])
            { Description = "Enter the log lines to open in a temporary source." }, context.InitialConfiguration);

    public PluginSourceConfiguration NormalizeConfiguration(PluginSourceConfiguration configuration)
    {
        if (!configuration.Values.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String) return configuration;
        return new(configuration.Version, PluginSourceJson.Object(new Dictionary<string, JsonElement>
            { ["text"] = PluginSourceJson.Value(text.GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal)) }), configuration.TransientValues);
    }

    public PluginValidationResult ValidateConfiguration(PluginSourceConfiguration configuration) =>
        configuration.Version == 1 && configuration.Values.TryGetProperty("text", out JsonElement text)
            && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? PluginValidationResult.Success : PluginValidationResult.Error("Enter log text.", "text");

    public async ValueTask<ILogSource> OpenAsync(PluginSourceConfiguration configuration, PluginSourceOpenContext context)
    {
        string path = context.Host.CreateSpoolPath(Metadata.Id);
        try
        {
            await File.WriteAllTextAsync(path, configuration.Values.GetProperty("text").GetString(), context.CancellationToken);
            context.CancellationToken.ThrowIfCancellationRequested();
            return new LogSourceHandle(path, "example://text-snapshot", "Text snapshot", "raw", lifetime: new Cleanup(path));
        }
        catch { File.Delete(path); throw; }
    }

    private sealed class Cleanup(string path) : IDisposable
    {
        public void Dispose() => File.Delete(path);
    }
}
