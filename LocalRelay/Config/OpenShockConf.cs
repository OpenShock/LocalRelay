namespace OpenShock.LocalRelay.Config;

public sealed class OpenShockConf
{
    public Uri Backend { get; set; } = new("https://api.openshock.app");
    public string Token { get; set; } = "";
}