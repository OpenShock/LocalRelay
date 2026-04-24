namespace OpenShock.LocalRelay.Config;

public sealed class SerialConfig
{
    public bool AutoConnect { get; set; } = true;
    public string? Port { get; set; } = null;
    public ushort? Vid { get; set; } = null;
    public ushort? Pid { get; set; } = null;
}