using System.Text.Json;
using LogAce.PluginApi;
using LogAce.Plugins.OsLog;
using Xunit;

public sealed class SourceConfigurationTests
{
    [Fact]
    public void Source_uses_only_the_public_API_and_legacy_defaults_round_trip_through_the_form()
    {
        var plugin = new OsLogSourcePlugin();
        Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "LogAce" or "Terminal.Gui" or "LogAce.PluginApi.TerminalGui");
        var expected = plugin.NormalizeConfiguration(plugin.CreateDefaultConfiguration());
        var form = Assert.IsType<PluginSourceFormRequest>(plugin.GetConfigurationUi(new(expected)));
        var actual = plugin.NormalizeConfiguration(form.InitialConfiguration!);
        Assert.True(JsonElement.DeepEquals(expected.Values, actual.Values));
        Assert.Equal(1, actual.Version);
        Assert.DoesNotContain(actual.Values.EnumerateObject(), property => property.Name.StartsWith('_'));
    }

    [Fact]
    public void Unknown_versions_and_wrong_JSON_types_fail_before_transport_or_discovery()
    {
        var plugin = new OsLogSourcePlugin();
        var defaults = plugin.CreateDefaultConfiguration();
        Assert.False(plugin.ValidateConfiguration(new(2, defaults.Values)).IsValid);
        var fields = defaults.Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        string field = fields.Keys.First();
        fields[field] = JsonSerializer.SerializeToElement(new[] { "wrong-type" });
        Assert.False(plugin.ValidateConfiguration(plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(fields)))).IsValid);
    }
}
