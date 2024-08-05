using System.Text.Json.Serialization;
using OpenShock.Serialization.Types;

namespace OpenShock.LocalRelay.Models.Serial;

public sealed class RfTransmit
{
    public required ushort Id { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ShockerModelType Model { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ShockerCommandType Type { get; set; }
    public required byte Intensity { get; set; }
    public required ushort DurationMs { get; set; }
}

/*

> help rftransmit
rftransmit <json>
  Transmit a RF command
  
  Arguments:
    <json> must be a JSON object with the following fields:
      model      (string) Model of the shocker                    ("caixianlin", "petrainer")
      id         (number) ID of the shocker                       (0-65535)
      type       (string) Type of the command                     ("shock", "vibrate", "sound", "stop")
      intensity  (number) Intensity of the command                (0-255)
      durationMs (number) Duration of the command in milliseconds (0-65535)
      
  Example:
    rftransmit {"model":"caixianlin","id":12345,"type":"vibrate","intensity":99,"durationMs":500}

*/