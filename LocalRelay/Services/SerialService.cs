using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenShock.LocalRelay.Models.Serial;

namespace OpenShock.LocalRelay.Services;

public sealed partial class SerialService : IDisposable
{
    public SerialPortInfo[] GetSerialPorts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetSerialPortsWindows();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return GetSerialPortsLinux();

        // Fallback: just port names, no VID/PID
        return SerialPort.GetPortNames()
            .Select(p => new SerialPortInfo(p, p, null, null))
            .ToArray();
    }

    public SerialPortInfo? FindPortByVidPid(ushort vid, ushort pid)
    {
        return GetSerialPorts().FirstOrDefault(p => p.Vid == vid && p.Pid == pid);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SerialPortInfo[] GetSerialPortsWindows()
    {
        // Only show currently connected ports
        var activePorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (activePorts.Count == 0) return [];

        var results = new List<SerialPortInfo>();
        var matchedPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey == null) return FallbackPorts();

            foreach (var vidPidName in usbKey.GetSubKeyNames())
            {
                var match = VidPidRegex().Match(vidPidName);
                if (!match.Success) continue;

                var vid = ushort.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber);
                var pid = ushort.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber);

                using var vidPidKey = usbKey.OpenSubKey(vidPidName);
                if (vidPidKey == null) continue;

                foreach (var instanceName in vidPidKey.GetSubKeyNames())
                {
                    using var instanceKey = vidPidKey.OpenSubKey(instanceName);
                    if (instanceKey == null) continue;

                    using var paramsKey = instanceKey.OpenSubKey("Device Parameters");
                    var portName = paramsKey?.GetValue("PortName")?.ToString();
                    if (portName == null || !activePorts.Contains(portName)) continue;

                    // Try to get the USB product descriptor from the parent device
                    var friendlyName = GetUsbProductName(vidPidName, instanceName)
                                       ?? instanceKey.GetValue("FriendlyName")?.ToString()
                                       ?? portName;

                    results.Add(new SerialPortInfo(portName, friendlyName, vid, pid));
                    matchedPorts.Add(portName);
                }
            }
        }
        catch
        {
            return FallbackPorts();
        }

        // Include any active ports that weren't found via USB registry (e.g. built-in COM ports)
        foreach (var port in activePorts)
        {
            if (!matchedPorts.Contains(port))
                results.Add(new SerialPortInfo(port, port, null, null));
        }

        return results.ToArray();
    }

    /// <summary>
    /// Reads the USB iProduct descriptor string via cfgmgr32 P/Invoke.
    /// This is the same value Chrome's Web Serial API shows.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? GetUsbProductName(string vidPidName, string instanceName)
    {
        var deviceInstanceId = $@"USB\{vidPidName}\{instanceName}";
        return CfgMgr32.GetBusReportedDeviceDesc(deviceInstanceId);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class CfgMgr32
    {
        // DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
        private static readonly DevPropKey DevPKeyBusReportedDeviceDesc = new(
            new Guid(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2), 4);

        private const uint CmLocateDevNodePhantom = 0x00000001;
        private const uint DevPropTypeString = 0x00000012;
        private const uint CrSuccess = 0;
        private const uint CrBufferSmall = 0x0000001A;

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey(Guid fmtid, uint pid)
        {
            public Guid Fmtid = fmtid;
            public uint Pid = pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
        private static extern uint CM_Locate_DevNode(out uint pdnDevInst, string pDeviceId, uint ulFlags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_PropertyW")]
        private static extern uint CM_Get_DevNode_Property(
            uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType,
            char[]? buffer, ref uint bufferSize, uint flags);

        public static string? GetBusReportedDeviceDesc(string deviceInstanceId)
        {
            try
            {
                if (CM_Locate_DevNode(out var devInst, deviceInstanceId, CmLocateDevNodePhantom) != CrSuccess)
                    return null;

                var propKey = DevPKeyBusReportedDeviceDesc;
                uint bufferSize = 0;

                // First call to get required buffer size
                var result = CM_Get_DevNode_Property(devInst, ref propKey, out _, null, ref bufferSize, 0);
                if (result != CrBufferSmall || bufferSize == 0)
                    return null;

                var buffer = new char[bufferSize];
                result = CM_Get_DevNode_Property(devInst, ref propKey, out var propType, buffer, ref bufferSize, 0);

                if (result != CrSuccess || propType != DevPropTypeString)
                    return null;

                // Buffer includes null terminator
                var name = new string(buffer, 0, (int)(bufferSize - 1));
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }
    }

    private static SerialPortInfo[] GetSerialPortsLinux()
    {
        var results = new List<SerialPortInfo>();

        try
        {
            var ttyClassPath = "/sys/class/tty";
            if (!Directory.Exists(ttyClassPath)) return FallbackPorts();

            foreach (var ttyDir in Directory.GetDirectories(ttyClassPath))
            {
                var deviceLink = Path.Combine(ttyDir, "device");
                if (!Directory.Exists(deviceLink)) continue;

                // Navigate up to the USB device directory
                var usbDeviceDir = Path.GetFullPath(Path.Combine(deviceLink, ".."));

                var vidPath = Path.Combine(usbDeviceDir, "idVendor");
                var pidPath = Path.Combine(usbDeviceDir, "idProduct");

                if (!File.Exists(vidPath) || !File.Exists(pidPath))
                {
                    // Try one more level up (for some device tree layouts)
                    usbDeviceDir = Path.GetFullPath(Path.Combine(usbDeviceDir, ".."));
                    vidPath = Path.Combine(usbDeviceDir, "idVendor");
                    pidPath = Path.Combine(usbDeviceDir, "idProduct");

                    if (!File.Exists(vidPath) || !File.Exists(pidPath))
                        continue;
                }

                var vidStr = File.ReadAllText(vidPath).Trim();
                var pidStr = File.ReadAllText(pidPath).Trim();

                if (!ushort.TryParse(vidStr, NumberStyles.HexNumber, null, out var vid) ||
                    !ushort.TryParse(pidStr, NumberStyles.HexNumber, null, out var pid))
                    continue;

                var ttyName = Path.GetFileName(ttyDir);
                var portName = $"/dev/{ttyName}";

                var productPath = Path.Combine(usbDeviceDir, "product");
                var friendlyName = File.Exists(productPath)
                    ? File.ReadAllText(productPath).Trim()
                    : portName;

                results.Add(new SerialPortInfo(portName, friendlyName, vid, pid));
            }
        }
        catch
        {
            return FallbackPorts();
        }

        return results.ToArray();
    }

    private static SerialPortInfo[] FallbackPorts()
    {
        return SerialPort.GetPortNames()
            .Select(p => new SerialPortInfo(p, p, null, null))
            .ToArray();
    }

    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4})&PID_(?<pid>[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    public void Dispose()
    {
    }
}