using System.Net;
using VpnHood.Common.Net;
using VpnHood.Tunneling.Factory;

namespace VpnHood.Client;

public class ClientOptions
{
    /// <summary>
    ///     a never used IPv4 that must be outside the local network
    /// </summary>
    public IPAddress TcpProxyCatcherAddressIpV4 { get; set; } = IPAddress.Parse("11.0.0.0");

    /// <summary>
    ///     a never used IPv6 ip that must be outside the machine
    /// </summary>
    public IPAddress TcpProxyCatcherAddressIpV6 { get; set; } = IPAddress.Parse("2000::");

    public IPAddress[] DnsServers { get; set; } = {
        IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"),
        IPAddress.Parse("2001:4860:4860::8888"), IPAddress.Parse("2001:4860:4860::8844")
    };

    public bool AutoDisposePacketCapture { get; set; } = true;
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.MaxValue;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public Version Version { get; set; } = typeof(ClientOptions).Assembly.GetName().Version;
    public bool UseUdpChannel { get; set; } 
    public bool ExcludeLocalNetwork { get; set; } = true;
    public IIpRangeProvider? IpRangeProvider { get; set; }
    public IpRange[] PacketCaptureIncludeIpRanges { get; set; } = IpNetwork.All.ToIpRanges().ToArray();
    public SocketFactory SocketFactory { get; set; } = new();
    public int MaxDatagramChannelCount { get; set; } = 4;
    public string UserAgent { get; set; } = Environment.OSVersion.ToString();
    public TimeSpan MinTcpDatagramTimespan { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxTcpDatagramTimespan { get; set; } = TimeSpan.FromMinutes(10);
    public bool AllowAnonymousTracker { get; set; } = true;
    public bool DropUdpPackets { get; set; }
    public string? AppGa4MeasurementId { get; set; }

#if DEBUG
    public int ProtocolVersion { get; set; }
#endif
}