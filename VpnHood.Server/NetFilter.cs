using System.Net;
using PacketDotNet;
using VpnHood.Common.Net;

namespace VpnHood.Server;

public class NetFilter : INetFilter
{
    private readonly IpRange[] _loopbackIpRange = IpNetwork.ToIpRange(IpNetwork.LoopbackNetworksV4.Concat(IpNetwork.LoopbackNetworksV6)).ToArray();
    private IpRange[] _sortedBlockedIpRanges = Array.Empty<IpRange>();

    public NetFilter()
    {
        BlockedIpRanges = _loopbackIpRange;
    }

    public IpRange[] BlockedIpRanges
    {
        get => _sortedBlockedIpRanges;
        set => _sortedBlockedIpRanges = value.Concat(_loopbackIpRange).Sort().ToArray();
    }

    public virtual bool IsIpAddressBlocked(IPAddress ipAddress)
    {
        return IpRange.IsInSortedRanges(BlockedIpRanges, ipAddress);
    }

    // ReSharper disable once ReturnTypeCanBeNotNullable
    public virtual IPPacket? ProcessRequest(IPPacket ipPacket)
    {
        return IsIpAddressBlocked(ipPacket.DestinationAddress) ? null : ipPacket;
    }

    // ReSharper disable once ReturnTypeCanBeNotNullable
    public virtual IPEndPoint? ProcessRequest(ProtocolType protocol, IPEndPoint requestEndPoint)
    {
        return IsIpAddressBlocked(requestEndPoint.Address) ? null : requestEndPoint;
    }

    public virtual IPPacket ProcessReply(IPPacket ipPacket)
    {
        return ipPacket;
    }
}