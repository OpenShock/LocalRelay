namespace OpenShock.LocalRelay.Config;

public sealed class SerialConfig
{
    public bool AutoSelect { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public string? Port { get; set; } = null;
}