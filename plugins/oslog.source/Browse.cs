using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LogAce.PluginApi;

namespace LogAce.Plugins.OsLog;

/// <summary>
/// Discovery runs on a cancellable worker. The host owns nested modal dispatch.
/// All main settings travel with the chooser because confirmation validates the
/// owning source; only the confirmed selection and its metadata return to the form.
/// </summary>
internal static class Browse
{
    internal static async ValueTask<PluginSourceFormUpdate> ChooseAsync(PluginSourceFormActionContext context,
        string sourceField, string formField, string title,
        Func<PluginSourceConfiguration, PluginSourceConfiguration> normalize, Func<IReadOnlyList<BrowseOption>> load)
    {
        IReadOnlyList<BrowseOption> options;

        try
        {
            options = await SourceRuntime.DiscoverAsync(context.CancellationToken, load).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException)
        {
            return new()
            {
                Diagnostics = [new(error.Message + " Enter a known value or retry Browse.", formField)]
            };
        }

        if (options.Count == 0)
        {
            return new()
            {
                Diagnostics = [new("No choices found. Enter a known value or retry Browse.", formField)]
            };
        }

        var initial = normalize(new(1, PluginSourceJson.Object(context.Values.Values)));
        var values = Configuration.Values(initial);
        string? selected = values.GetValueOrDefault(sourceField).ValueKind == JsonValueKind.String
            ? values[sourceField].GetString() : null;
        values[sourceField] = PluginSourceJson.Value(options.Any(option => option.Id == selected) ? selected : options[0].Id);

        PluginSourceFormUpdate Refresh(PluginSourceFormActionContext refresh)
        {
            string search = refresh.Values.GetString("_browseSearch")?.Trim() ?? "";
            var filtered = options.Where(option => option.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || option.Id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new()
            {
                Choices = new Dictionary<string, IReadOnlyList<PluginSourceChoice>> { [sourceField] = filtered.Select(option => new PluginSourceChoice(option.Id, option.Label)).ToArray() },
                Values = new Dictionary<string, JsonElement> { [sourceField] = PluginSourceJson.Value(filtered.Any(option => option.Id == refresh.Values.GetString(sourceField)) ? refresh.Values.GetString(sourceField) : filtered.FirstOrDefault()?.Id) },
                Diagnostics = filtered.Length == 0 ? [new("No matching choices. Change Search and apply it again.", sourceField)] : []
            };
        }

        var request = new PluginSourceFormRequest(new(title,
        [
            new(sourceField,"Choose",PluginSourceFieldKind.Choice) { Required=true, Choices=options.Select(option=>new PluginSourceChoice(option.Id,option.Label)).ToArray() },
            new("_browseSearch","Search")
        ])
        {
            ConfirmLabel = "_Use selection",
            Description = "Select a value. Change Search, then Apply search to filter this list.",
            Actions = [new("filter", "_Apply search", action => ValueTask.FromResult(Refresh(action)), sourceField)]
        }, new(1, PluginSourceJson.Object(values)));
        var result = await context.Host.ShowAsync(request, context.CancellationToken).ConfigureAwait(false);

        if (result.Status != PluginSourceConfigurationStatus.Confirmed || result.Configuration is not { } configuration)
        {
            return new();
        }

        string? id = configuration.Values.GetProperty(sourceField).GetString();
        var option = options.FirstOrDefault(item => item.Id == id);

        if (option is null)
        {
            return new()
            {
                Diagnostics = [new("The selected value is unavailable. Retry Browse.", formField)]
            };
        }

        var updates = new Dictionary<string, JsonElement> { [formField] = PluginSourceJson.Value(option.Id) };

        foreach (var pair in option.Updates ?? new Dictionary<string, JsonElement>())
        {
            updates[pair.Key] = pair.Value;
        }

        return new()
        {
            Values = updates
        };
    }
}
