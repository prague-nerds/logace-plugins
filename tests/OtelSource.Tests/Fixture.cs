using System.Text.Json;
using LogAce.PluginApi;

/// <summary>Each fixture owns its temp paths and restores PATH; no cloud/device is contacted.</summary>
internal sealed class Fixture : IPluginSourceHost, IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "source-test-" + Guid.NewGuid().ToString("N"));

    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    internal List<string> Spools { get; } = [];

    internal Fixture()
    {
        Directory.CreateDirectory(DirectoryPath);
    }

    internal void Cli(string executable, string script)
    {
        string path = Path.Combine(DirectoryPath, executable);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", DirectoryPath + Path.PathSeparator + _path);
    }

    public string CreateSpoolPath(string id, string extension = ".log")
    {
        string path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + extension);
        Spools.Add(path);
        return path;
    }

    public string GetStatePath(string id, string path) => Path.Combine(DirectoryPath, "unused.las");

    internal static PluginSourceConfiguration Configure(ILogAceSourcePlugin plugin, params (string Key, object? Value)[] fields)
    {
        var values = plugin.CreateDefaultConfiguration().Values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        foreach (var pair in fields)
        {
            values[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
        }

        return plugin.NormalizeConfiguration(new(1, PluginSourceJson.Object(values)));
    }

    internal static async Task<string> WaitForAsync(string path, string text)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!deadline.IsCancellationRequested)
        {
            string contents = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";

            if (contents.Contains(text))
            {
                return contents;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Source did not write expected data: " + text);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Directory.Delete(DirectoryPath, true);
    }
}
