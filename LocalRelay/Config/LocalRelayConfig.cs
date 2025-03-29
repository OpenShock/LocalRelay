namespace OpenShock.LocalRelay.Config;

public sealed class LocalRelayConfig
{
    public HubConfig Hub { get; set; } = new();
    
    public SerialConfig Serial { get; set; } = new();
}