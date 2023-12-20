using VpnHood.Common.Net;

namespace VpnHood.Client.App.Settings;

public class UserSettings
{
    private static readonly ClientOptions DefaultClientOptions = new(); 

    public AppLogSettings Logging { get; set; } = new();
    public string CultureName { get; set; } = "en";
    public Guid? DefaultClientProfileId { get; set; }
    public int MaxReconnectCount { get; set; } = 3;
    public int MaxDatagramChannelCount { get; set; } = DefaultClientOptions.MaxDatagramChannelCount;
    public bool TunnelClientCountry { get; set; } = true;
    public string[]? IpGroupFilters { get; set; }
    public FilterMode IpGroupFiltersMode { get; set; } = FilterMode.All;
    public IpRange[]? CustomIpRanges { get; set; }
    public string[]? AppFilters { get; set; }
    public FilterMode AppFiltersMode { get; set; } = FilterMode.All;
    public bool UseUdpChannel { get; set; } = DefaultClientOptions.UseUdpChannel;
    public bool DropUdpPackets { get; set; } = DefaultClientOptions.DropUdpPackets;
    public bool ExcludeLocalNetwork { get; set; } = DefaultClientOptions.ExcludeLocalNetwork;
    public IpRange[] PacketCaptureIncludeIpRanges { get; set; } = IpNetwork.All.ToIpRanges().ToArray();
    public IpRange[]? PacketCaptureExcludeIpRanges { get; set; } = IpNetwork.None.ToIpRanges().ToArray();
    public bool AllowAnonymousTracker { get; set; } = DefaultClientOptions.AllowAnonymousTracker;
}