namespace OpenShock.LocalRelay.Config;

public sealed class LocalRelayConfig
{
    public OpenShockConf OpenShock { get; set; } = new();
    public AppConfig App { get; set; } = new();
    
    public HubConfig Hub { get; set; } = new();
    
    public SerialConfig Serial { get; set; } = new();
}