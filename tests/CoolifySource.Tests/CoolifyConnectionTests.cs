using System.Net;
using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.Coolify;
using Xunit;

namespace CoolifySource.Tests;

public sealed class CoolifyConnectionTests
{
    [Fact]
    public async Task Named_connections_store_only_metadata_and_resolve_the_correct_vault_token_after_restart()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags);
        using (await plugin.OpenAsync(Config("Production", "https://prod.example", "prod-private", remember: true), new(host))) { }
        using (await plugin.OpenAsync(Config("Staging", "https://staging.example", "stage-private", remember: true), new(host))) { }
        Assert.Equal(2, bags.Settings.Count);
        Assert.Equal(2, bags.Secrets.Count);
        Assert.All(bags.Settings.Values, setting =>
        {
            Assert.DoesNotContain("prod-private", setting.Value.GetRawText());
            Assert.DoesNotContain("stage-private", setting.Value.GetRawText());
        });
        var restarted = await Plugin(bags);
        var production = await restarted.ResolveConfigurationAsync(Config("Production", "https://prod.example", null));
        var staging = await restarted.ResolveConfigurationAsync(Config("Staging", "https://staging.example", null));
        Assert.Equal("prod-private", production.TransientValues!.Value.GetProperty("token").GetString());
        Assert.Equal("stage-private", staging.TransientValues!.Value.GetProperty("token").GetString());
        Assert.DoesNotContain("prod-private", JsonSerializer.Serialize(production));
        var changed = await restarted.ResolveConfigurationAsync(Config("Production", "https://untrusted.example", null));
        Assert.Null(changed.TransientValues);
        Assert.Equal(2, (await new CoolifyConnections(bags.Services).ListAsync(default)).Count);
    }

    [Fact]
    public async Task Temporary_tokens_do_not_need_a_vault_and_do_not_overwrite_an_existing_saved_token()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags);
        using (await plugin.OpenAsync(Config("Production", "https://prod.example", "saved-token", remember: true), new(host))) { }
        bags.Unavailable = true;
        using (await plugin.OpenAsync(Config("Production", "https://prod.example", "temporary-token", remember: false), new(host))) { }
        Assert.Equal("saved-token", Assert.Single(bags.Secrets).Value);
        Assert.DoesNotContain("temporary-token", Assert.Single(bags.Settings).Value.Value.GetRawText());
        var error = await Assert.ThrowsAsync<PluginStorageException>(async () => await plugin.ResolveConfigurationAsync(Config("Production", "https://prod.example", null)));
        Assert.Equal(PluginStorageError.Locked, error.Error);
        bags.Unavailable = false;
        var restored = await plugin.ResolveConfigurationAsync(Config("Production", "https://prod.example", null));
        Assert.Equal("saved-token", restored.TransientValues!.Value.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Failed_authentication_never_saves_a_profile_or_token()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags, HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<CoolifyApiException>(async () => await plugin.OpenAsync(Config("Production", "https://prod.example", "wrong-token", remember: true), new(host)));
        Assert.Empty(bags.Settings);
        Assert.Empty(bags.Secrets);
        Assert.Empty(Directory.GetFiles(host.Root));
    }

    [Fact]
    public async Task Unavailable_vault_never_falls_back_to_JSON_and_the_opened_source_is_cleaned_up()
    {
        var bags = new Bags { Unavailable = true };
        using var host = new Host();
        var plugin = await Plugin(bags);
        await Assert.ThrowsAsync<PluginStorageException>(async () => await plugin.OpenAsync(Config("Production", "https://prod.example", "private-token", remember: true), new(host)));
        Assert.Empty(bags.Settings);
        Assert.Empty(bags.Secrets);
        Assert.Empty(Directory.GetFiles(host.Root));
        using var temporary = await plugin.OpenAsync(Config("Production", "https://prod.example", "private-token", remember: false), new(host));
        Assert.DoesNotContain("private-token", Assert.Single(bags.Settings).Value.Value.GetRawText());
    }

    [Fact]
    public async Task Saved_connection_picker_clears_a_typed_token_and_preserves_the_saved_profile_fields()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags);
        using (await plugin.OpenAsync(Config("Staging", "https://staging.example", "stage-token", remember: true), new(host))) { }
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(Config("Production", "https://prod.example", "typed-prod-token"))));
        var ui = new UiHost();
        ui.Show = request =>
        {
            var browser = Assert.IsType<PluginSourceFormRequest>(request);
            string key = Assert.Single(browser.Form.Fields[0].Choices).Id;
            return ValueTask.FromResult(new PluginSourceConfigurationResult(PluginSourceConfigurationStatus.Confirmed,
                new(1, JsonSerializer.SerializeToElement(new { savedConnection = key }))));
        };
        var update = await form.Form.Actions.Single(action => action.Id == "connections").ExecuteAsync(new(Values(form.InitialConfiguration!), ui, default));
        Assert.Equal("Staging", update.Values["connectionName"].GetString());
        Assert.Equal("https://staging.example", update.Values["instanceUrl"].GetString());
        Assert.Equal("", update.Values["token"].GetString());
        Assert.False(update.Values.ContainsKey("hasStoredToken"));
        Assert.DoesNotContain("stage-token", JsonSerializer.Serialize(update.Values));
    }

    [Fact]
    public async Task Forget_action_deletes_the_vault_token_and_clears_remembered_state()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags);
        var configuration = Config("Production", "https://prod.example", "private-token", remember: true);
        using (await plugin.OpenAsync(configuration, new(host))) { }
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(configuration)));
        var update = await form.Form.Actions.Single(action => action.Id == "forgetToken").ExecuteAsync(new(Values(configuration), new UiHost(), default));
        Assert.Empty(bags.Secrets);
        Assert.False(update.Values["rememberToken"].GetBoolean());
        Assert.Equal("", update.Values["token"].GetString());
        var resolved = await plugin.ResolveConfigurationAsync(Config("Production", "https://prod.example", null));
        Assert.Null(resolved.TransientValues);
    }

    [Fact]
    public async Task Saved_token_is_available_for_application_discovery_before_an_application_is_selected()
    {
        var bags = new Bags();
        using var host = new Host();
        var plugin = await Plugin(bags);
        using (await plugin.OpenAsync(Config("Production", "https://prod.example", "private-token", remember: true), new(host))) { }
        var config = Config("Production", "https://prod.example", null);
        var values = Values(config).Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["applicationUuid"] = PluginSourceJson.Value("");
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(config)));
        var ui = new UiHost();
        var result = await form.Form.Actions.Single(action => action.Id == "applications").ExecuteAsync(new(new(values), ui, default));
        Assert.Empty(result.Diagnostics);
        Assert.True(ui.Shown);
    }

    private static async Task<CoolifySourcePlugin> Plugin(Bags bags, HttpStatusCode status = HttpStatusCode.OK)
    {
        var plugin = new CoolifySourcePlugin((options, token) => new CoolifyClient(options, token, new HttpClient(new Handler(status))));
        await plugin.InitializeAsync(bags.Services);
        return plugin;
    }

    private static PluginSourceConfiguration Config(string name, string url, string? token, bool remember = false)
    {
        var values = CoolifyConfiguration.Default().Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        values["connectionName"] = PluginSourceJson.Value(name);
        values["instanceUrl"] = PluginSourceJson.Value(url);
        values["tokenEnvironment"] = PluginSourceJson.Value("");
        values["applicationUuid"] = PluginSourceJson.Value("app-1");
        values["follow"] = PluginSourceJson.Value(false);
        values["rememberToken"] = PluginSourceJson.Value(remember);
        return new(1, PluginSourceJson.Object(values), token is null ? null : JsonSerializer.SerializeToElement(new { token }));
    }

    private static PluginSourceFormValues Values(PluginSourceConfiguration configuration)
    {
        var values = configuration.Values.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        if (configuration.TransientValues is { } transient)
        {
            foreach (var property in transient.EnumerateObject()) { values[property.Name] = property.Value; }
        }
        return new(values);
    }

    private sealed class Bags : IPluginSettingsBag, IPluginSecretsBag
    {
        internal Dictionary<string, PluginSettingsValue> Settings { get; } = new();
        internal Dictionary<string, string> Secrets { get; } = new();
        internal bool Unavailable;
        internal PluginHostServices Services => new(this, this);
        public string ProviderName => "Test vault";
        public ValueTask<IReadOnlyList<string>> GetKeysAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<string>>(Settings.Keys.ToArray());
        public ValueTask<PluginSettingsValue?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(Settings.GetValueOrDefault(key));
        public ValueTask SetAsync(string key, PluginSettingsValue value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Settings.Remove(key);
            return ValueTask.CompletedTask;
        }
        ValueTask<string?> IPluginSecretsBag.GetAsync(string key, CancellationToken cancellationToken)
        {
            if (Unavailable) { throw new PluginStorageException(PluginStorageError.Locked, "Test vault is locked."); }
            return ValueTask.FromResult(Secrets.GetValueOrDefault(key));
        }
        public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (Unavailable) { throw new PluginStorageException(PluginStorageError.Locked, "Test vault is locked."); }
            Secrets[key] = value;
            return ValueTask.CompletedTask;
        }
        ValueTask IPluginSecretsBag.DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Secrets.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            string body = request.RequestUri!.AbsolutePath.EndsWith("/applications")
                ? "[{\"uuid\":\"app-1\",\"name\":\"API\"}]" : "{\"logs\":\"output\\n\"}";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Host : IPluginSourceHost, IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "coolify-connections-" + Guid.NewGuid().ToString("N"));
        internal Host() => Directory.CreateDirectory(Root);
        public string CreateSpoolPath(string sourceId, string extension = ".log") => Path.Combine(Root, Guid.NewGuid() + extension);
        public string GetStatePath(string sourceId, string canonicalDisplayPath) => Path.Combine(Root, "state.las");
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class UiHost : IPluginSourceUiHost
    {
        internal bool Shown;
        internal Func<IPluginSourceUiRequest, ValueTask<PluginSourceConfigurationResult>> Show { get; set; } = _ => ValueTask.FromResult(new PluginSourceConfigurationResult(PluginSourceConfigurationStatus.Cancelled));
        public ValueTask<PluginSourceConfigurationResult> ShowAsync(IPluginSourceUiRequest request, CancellationToken cancellationToken = default)
        {
            Shown = true;
            return Show(request);
        }
        public ValueTask InvokeAsync(Action update, CancellationToken cancellationToken = default) { update(); return ValueTask.CompletedTask; }
        public void ReportProgress(string activity) { }
        public void ReportError(PluginDiagnostic diagnostic) { }
        public PluginSourceConfigurationResult ConfirmConfiguration(PluginSourceConfiguration configuration) => new(PluginSourceConfigurationStatus.Confirmed, configuration);
    }
}
