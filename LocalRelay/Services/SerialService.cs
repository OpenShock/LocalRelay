using System.IO.Ports;

namespace OpenShock.LocalRelay.Services;

public sealed class SerialService : IDisposable
{
    
    public SerialService()
    {
        
    }

    public string[] GetSerialPorts()
    {
        return SerialPort.GetPortNames();
    }
    
    public void Dispose()
    {
           
    }
}