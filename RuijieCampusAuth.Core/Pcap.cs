using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RuijieCampusAuth.Core;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct PcapIf
{
    public IntPtr Next;
    [MarshalAs(UnmanagedType.LPStr)] public string Name;
    [MarshalAs(UnmanagedType.LPStr)] public string Description;
    public IntPtr Addresses;
    public uint Flags;
}

public sealed record PcapDeviceInfo(string Name, string Description);

public static class PcapNative
{
    private const string Dll = "wpcap.dll";

    static PcapNative()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(PcapNative).Assembly,
            (name, _assembly, _path) =>
            {
                if (name is not ("wpcap.dll" or "pcap"))
                {
                    return IntPtr.Zero;
                }
                foreach (var candidate in new[]
                         {
                             "wpcap.dll", "libpcap.so.1", "libpcap.so", "libpcap.dylib",
                         })
                {
                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }
                return IntPtr.Zero;
            });
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pcap_open_live(
        [MarshalAs(UnmanagedType.LPStr)] string device,
        int snaplen,
        int promisc,
        int toMs,
        StringBuilder errbuf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pcap_close(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pcap_next_ex(
        IntPtr handle,
        out IntPtr packetHeader,
        out IntPtr packetData);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pcap_sendpacket(IntPtr handle, byte[] buffer, int size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pcap_breakloop(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pcap_geterr(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pcap_findalldevs(out IntPtr devices, StringBuilder errbuf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pcap_freealldevs(IntPtr devices);

    public static List<PcapDeviceInfo> ListDevices()
    {
        var result = new List<PcapDeviceInfo>();
        var error = new StringBuilder(256);
        if (pcap_findalldevs(out var devices, error) != 0)
        {
            throw new InvalidOperationException(error.ToString());
        }
        try
        {
            var current = devices;
            while (current != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<PcapIf>(current);
                result.Add(new PcapDeviceInfo(
                    info.Name ?? "",
                    string.IsNullOrWhiteSpace(info.Description) ? info.Name ?? "" : info.Description));
                current = info.Next;
            }
        }
        finally
        {
            pcap_freealldevs(devices);
        }
        return result;
    }

    public static string Error(IntPtr handle)
    {
        var pointer = pcap_geterr(handle);
        return pointer == IntPtr.Zero ? "unknown pcap error" : Marshal.PtrToStringAnsi(pointer) ?? "";
    }
}

public sealed class PcapDevice : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Thread _reader;
    private volatile bool _stopping;

    public event Action<byte[]>? PacketReceived;
    public string DeviceName { get; }

    public PcapDevice(string deviceName, int readTimeoutMs = 100)
    {
        DeviceName = deviceName;
        var error = new StringBuilder(256);
        _handle = PcapNative.pcap_open_live(deviceName, 65536, 1, readTimeoutMs, error);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(error.ToString());
        }
        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "pcap-reader",
        };
        _reader.Start();
    }

    private void ReadLoop()
    {
        while (!_stopping)
        {
            var result = PcapNative.pcap_next_ex(_handle, out var headerPointer, out var dataPointer);
            if (result == 0)
            {
                continue;
            }
            if (result == -2)
            {
                break;
            }
            if (result < 0)
            {
                break;
            }
            // struct pcap_pkthdr starts with struct timeval, whose width
            // differs by platform: 4-byte long on Windows, 8-byte time_t on
            // 64-bit Unix. Reading it as a fixed 4-byte struct corrupts
            // caplen on Linux and leads to an out-of-bounds Marshal.Copy.
            var caplen = (uint)Marshal.ReadInt32(
                headerPointer,
                PacketLengthOffset());
            if (caplen == 0 || dataPointer == IntPtr.Zero)
            {
                continue;
            }
            var packet = new byte[caplen];
            Marshal.Copy(dataPointer, packet, 0, packet.Length);
            try
            {
                PacketReceived?.Invoke(packet);
            }
            catch
            {
                // A subscriber must never kill the receive loop.
            }
        }
    }

    private static int PacketLengthOffset()
    {
        if (OperatingSystem.IsWindows())
        {
            return 8; // two 4-byte longs
        }
        return IntPtr.Size == 8 ? 16 : 8; // timeval: time_t + suseconds_t
    }

    public void Send(byte[] frame)
    {
        if (_stopping)
        {
            throw new ObjectDisposedException(nameof(PcapDevice));
        }
        if (PcapNative.pcap_sendpacket(_handle, frame, frame.Length) != 0)
        {
            throw new InvalidOperationException(PcapNative.Error(_handle));
        }
    }

    public void SendBurst(IEnumerable<byte[]> frames)
    {
        foreach (var frame in frames)
        {
            Send(frame);
        }
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }
        _stopping = true;
        PcapNative.pcap_breakloop(_handle);
        if (_reader.IsAlive)
        {
            _reader.Join(2000);
        }
        PcapNative.pcap_close(_handle);
    }
}
