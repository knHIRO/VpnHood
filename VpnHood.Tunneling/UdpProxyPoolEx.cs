using System.Net;
using PacketDotNet;
using VpnHood.Common.Collections;
using VpnHood.Tunneling.Factory;
using VpnHood.Tunneling.Exceptions;
using VpnHood.Common.JobController;
using VpnHood.Common.Logging;

namespace VpnHood.Tunneling;


public class UdpProxyPoolEx : IPacketProxyPool, IJob
{
    private readonly IPacketProxyReceiver _packetProxyReceiver;
    private readonly ISocketFactory _socketFactory;
    private readonly TimeoutDictionary<string, UdpProxyEx> _connectionMap;
    private readonly List<UdpProxyEx> _udpProxies = [];
    private readonly TimeoutDictionary<IPEndPoint, TimeoutItem<bool>> _remoteEndPoints;
    private readonly EventReporter _maxWorkerEventReporter;
    private readonly TimeSpan _udpTimeout;
    private readonly int _maxLocalEndPointCount;
    private bool _disposed;

    public int RemoteEndPointCount => _remoteEndPoints.Count;
    public int ClientCount { get { lock (_udpProxies) return _udpProxies.Count; } }
    public JobSection JobSection { get; } = new();

    public UdpProxyPoolEx(IPacketProxyReceiver packetProxyReceiver, ISocketFactory socketFactory, 
        TimeSpan? udpTimeout, int? maxLocalEndPointCount, LogScope? logScope = null)
    {
        udpTimeout ??= TimeSpan.FromSeconds(120);
        _packetProxyReceiver = packetProxyReceiver;
        _socketFactory = socketFactory;
        _maxLocalEndPointCount = maxLocalEndPointCount ?? int.MaxValue;
        _connectionMap = new TimeoutDictionary<string, UdpProxyEx>(udpTimeout);
        _remoteEndPoints = new TimeoutDictionary<IPEndPoint, TimeoutItem<bool>>(udpTimeout);
        _udpTimeout = udpTimeout.Value;
        _maxWorkerEventReporter = new EventReporter(VhLogger.Instance, 
            "Session has reached to Maximum local UDP ports.", GeneralEventId.NetProtect, logScope: logScope);

        JobSection.Interval = udpTimeout.Value;
        JobRunner.Default.Add(this);
    }

    public Task SendPacket(IPPacket ipPacket)
    {
        // send packet via proxy
        var udpPacket = PacketUtil.ExtractUdp(ipPacket);
        bool? noFragment = ipPacket.Protocol == ProtocolType.IPv6 && ipPacket is IPv4Packet ipV4Packet
            ? (ipV4Packet.FragmentFlags & 0x2) != 0
            : null;

        var sourceEndPoint = new IPEndPoint(ipPacket.SourceAddress, udpPacket.SourcePort);
        var destinationEndPoint = new IPEndPoint(ipPacket.DestinationAddress, udpPacket.DestinationPort);
        var addressFamily = ipPacket.SourceAddress.AddressFamily;
        var isNewRemoteEndPoint = false;
        var isNewLocalEndPoint = false;

        // find the proxy for the connection (source-destination)
        var connectionKey = $"{sourceEndPoint}:{destinationEndPoint}";
        var udpProxy = _connectionMap.GetOrAdd(connectionKey, _ =>
        {
            // add the remote endpoint
            _remoteEndPoints.GetOrAdd(destinationEndPoint, (_) =>
            {
                isNewRemoteEndPoint = true;
                return new TimeoutItem<bool>(true);
            });
            if (isNewRemoteEndPoint)
                _packetProxyReceiver.OnNewRemoteEndPoint(ProtocolType.Udp, destinationEndPoint);

            // Find or create a worker that does not use the RemoteEndPoint
            lock (_udpProxies)
            {
                var newUdpProxy = _udpProxies.FirstOrDefault(x =>
                    x.AddressFamily == addressFamily &&
                    !x.DestinationEndPointMap.TryGetValue(destinationEndPoint, out var _));

                if (newUdpProxy == null)
                {
                    // check WorkerMaxCount
                    if (_udpProxies.Count >= _maxLocalEndPointCount)
                    {
                        _maxWorkerEventReporter.Raise();
                        throw new UdpClientQuotaException(_udpProxies.Count);
                    }

                    newUdpProxy = new UdpProxyEx(_packetProxyReceiver, _socketFactory.CreateUdpClient(addressFamily), addressFamily, _udpTimeout);
                    _udpProxies.Add(newUdpProxy);
                    isNewLocalEndPoint = true;
                }

                // Add destinationEndPoint; a newUdpWorker can not map a destinationEndPoint to more than one source port
                newUdpProxy.DestinationEndPointMap.TryAdd(destinationEndPoint, new TimeoutItem<IPEndPoint>(sourceEndPoint));
                return newUdpProxy;
            }
        });

        // Raise new endpoint
        if (isNewLocalEndPoint || isNewRemoteEndPoint)
            _packetProxyReceiver.OnNewEndPoint(ProtocolType.Udp,
                udpProxy.LocalEndPoint, destinationEndPoint, isNewLocalEndPoint, isNewRemoteEndPoint);

        var dgram = udpPacket.PayloadData ?? Array.Empty<byte>();
        return udpProxy.SendPacket(destinationEndPoint, dgram, noFragment);
    }

    public Task RunJob()
    {
        // remove useless workers
        lock (_udpProxies)
            TimeoutItemUtil.CleanupTimeoutList(_udpProxies, _udpTimeout);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock(_udpProxies)
            _udpProxies.ForEach(udpWorker => udpWorker.Dispose());

        _connectionMap.Dispose();
        _remoteEndPoints.Dispose();
        _maxWorkerEventReporter.Dispose();
    }
}