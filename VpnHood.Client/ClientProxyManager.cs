using PacketDotNet;
using VpnHood.Client.Device;
using VpnHood.Common.Logging;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Factory;

namespace VpnHood.Client;

internal class ClientProxyManager : ProxyManager
{
    private readonly IPacketCapture _packetCapture;

    public ClientProxyManager(IPacketCapture packetCapture, ISocketFactory socketFactory, 
        ProxyManagerOptions options) : base(socketFactory, options)
    {
        _packetCapture = packetCapture;
    }

    // PacketCapture can not protect Ping so PingProxy does not work
    protected override bool IsPingSupported => false;

    public override Task OnPacketReceived(IPPacket ipPacket)
    {
        if (VhLogger.IsDiagnoseMode)
            PacketUtil.LogPacket(ipPacket, "Delegating packet to host via proxy.");

        _packetCapture.SendPacketToInbound(ipPacket);
        return Task.FromResult(0);
    }

}