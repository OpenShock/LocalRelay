namespace OpenShock.LocalRelay.Config;

public sealed class OpenShockConf
{
    public Uri Backend { get; set; } = new("https://api.shocklink.net");
    public string Token { get; set; } = "";
}