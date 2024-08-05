namespace OpenShock.LocalRelay.Config;

public sealed class HubConfig
{
    public Guid? Hub { get; set; } = null;
    public bool AutoConnect { get; set; } = true;
}