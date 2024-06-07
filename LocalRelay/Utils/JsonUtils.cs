using System.Text.Json;

namespace OpenShock.LocalRelay.Utils;

public static class JsonUtils
{
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}