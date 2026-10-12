using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Simctl;
using Xunit;

public sealed class BrowseTests
{
    [Fact]
    public async Task Nested_chooser_filters_without_reloading_and_preserves_main_settings()
    {
        var plugin = new SimctlSourcePlugin();
        var initial = plugin.CreateDefaultConfiguration();
        var host = new Host(plugin, initial);
        int loads = 0;
        var update = await Browse.ChooseAsync(new(new(initial.Values.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value))), host, default),
            "deviceSelector", "deviceSelector", "Choose", plugin.NormalizeConfiguration, () =>
            {
                loads++;
                return [new("alpha", "Alpha"), new("beta", "Beta")];
            });
        Assert.Equal(1, loads);
        Assert.Equal("beta", update.Values["deviceSelector"].GetString());
        Assert.Empty(update.Diagnostics);

        foreach (var property in initial.Values.EnumerateObject().Where(p => p.Name != "deviceSelector"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, host.Selected!.Values.GetProperty(property.Name)));
        }
    }

    [Fact]
    public async Task Cancellation_and_empty_or_failed_discovery_preserve_all_main_values()
    {
        var plugin = new SimctlSourcePlugin();
        var values = new PluginSourceFormValues(plugin.CreateDefaultConfiguration().Values.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)));
        var host = new Host(plugin, plugin.CreateDefaultConfiguration());
        var empty = await Browse.ChooseAsync(new(values, host, default), "deviceSelector", "deviceSelector", "Choose", plugin.NormalizeConfiguration, () => []);
        Assert.Empty(empty.Values);
        Assert.NotEmpty(empty.Diagnostics);
        var failed = await Browse.ChooseAsync(new(values, host, default), "deviceSelector", "deviceSelector", "Choose", plugin.NormalizeConfiguration, () => throw new IOException("fixture discovery failed"));
        Assert.Empty(failed.Values);
        Assert.Contains("fixture discovery failed", failed.Diagnostics[0].Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Browse.ChooseAsync(new(values, host, cancellation.Token), "deviceSelector", "deviceSelector", "Choose", plugin.NormalizeConfiguration, () => throw new Exception("must not run")));
    }

    private sealed class Host(ILogAceSourcePlugin plugin, PluginSourceConfiguration initial) : IPluginSourceUiHost
    {
        internal PluginSourceConfiguration? Selected
        {
            get; private set;
        }

        public async ValueTask<PluginSourceConfigurationResult> ShowAsync(IPluginSourceUiRequest request, CancellationToken cancellationToken = default)
        {
            var form = Assert.IsType<PluginSourceFormRequest>(request);
            Assert.Equal("deviceSelector", form.Form.Fields[0].Id);
            var values = form.InitialConfiguration!.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            values["_browseSearch"] = PluginSourceJson.Value("Beta");
            var filtered = await form.Form.Actions.Single().ExecuteAsync(new(new(values), this, cancellationToken));
            Assert.Equal("beta", Assert.Single(filtered.Choices["deviceSelector"]).Id);
            Assert.Equal("beta", filtered.Values["deviceSelector"].GetString());
            values["deviceSelector"] = filtered.Values["deviceSelector"];
            var normalized = plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values)));
            Assert.False(normalized.Values.TryGetProperty("_browseSearch", out _));
            Selected = normalized;
            return new(PluginSourceConfigurationStatus.Confirmed, normalized);
        }

        public PluginSourceConfigurationResult ConfirmConfiguration(PluginSourceConfiguration configuration) => new(PluginSourceConfigurationStatus.Confirmed, plugin.NormalizeConfiguration(configuration));

        public ValueTask InvokeAsync(Action update, CancellationToken cancellationToken = default)
        {
            update();
            return ValueTask.CompletedTask;
        }

        public void ReportProgress(string activity)
        {
        }

        public void ReportError(PluginDiagnostic diagnostic)
        {
        }
    }
}
