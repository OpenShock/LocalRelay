namespace OpenShock.LocalRelay.Models.Serial;

public sealed record SerialPortInfo(string Port, string Name, ushort? Vid, ushort? Pid);