using System;

namespace LogAce.Plugins.Coolify;

internal sealed record CoolifyOptions(string InstanceUrl, string TokenEnvironment, string ApplicationUuid,
    string ApplicationName, string Mode, string DeploymentUuid, string ServiceName, string Tail,
    bool Follow, int PollSeconds)
{
    internal Uri ApiBase => new(InstanceUrl.TrimEnd('/') + "/api/v1/");

    internal string DisplayPath => "coolify://" + Uri.EscapeDataString(InstanceUrl) + "/"
        + Uri.EscapeDataString(ApplicationUuid) + "/" + Mode
        + (Mode == "deployment" ? "/" + Uri.EscapeDataString(DeploymentUuid) : "")
        + (Mode == "runtime" && ServiceName.Length > 0 ? "/" + Uri.EscapeDataString(ServiceName) : "");

    internal string ShortName => (ApplicationName.Length > 0 ? ApplicationName : ApplicationUuid)
        + (Mode == "deployment" ? " · deployment" : " · runtime");
}
