using System.Net;
using System.Net.Sockets;
using Ga4.Ga4Tracking;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using VpnHood.Client.ConnectorServices;
using VpnHood.Client.Device;
using VpnHood.Client.Exceptions;
using VpnHood.Common;
using VpnHood.Common.Exceptions;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Net;
using VpnHood.Common.Utils;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Channels;
using VpnHood.Tunneling.ClientStreams;
using VpnHood.Tunneling.Messaging;
using PacketReceivedEventArgs = VpnHood.Client.Device.PacketReceivedEventArgs;
using ProtocolType = PacketDotNet.ProtocolType;

namespace VpnHood.Client;

public class VpnHoodClient : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private readonly bool _autoDisposePacketCapture;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ClientProxyManager _proxyManager;
    private readonly Dictionary<IPAddress, bool> _includeIps = new();
    private readonly int _maxDatagramChannelCount;
    private readonly IPacketCapture _packetCapture;
    private readonly SendingPackets _sendingPacket = new();
    private readonly ClientHost _clientHost;
    private readonly SemaphoreSlim _datagramChannelsSemaphore = new(1, 1);
    private readonly IPAddress? _dnsServerIpV4;
    private readonly IPAddress? _dnsServerIpV6;
    private readonly IIpRangeProvider? _ipRangeProvider;
    private readonly TimeSpan _minTcpDatagramLifespan;
    private readonly TimeSpan _maxTcpDatagramLifespan;
    private readonly ConnectorService _connectorService;
    private readonly bool _allowAnonymousTracker;
    private readonly string? _appGa4MeasurementId;
    private Traffic _helloTraffic = new();
    private ClientUsageTracker? _clientUsageTracker;
    private bool IsTcpDatagramLifespanSupported => ServerVersion?.Build >= 345; //will be deprecated
    private DateTime? _lastConnectionErrorTime;
    private byte[]? _sessionKey;
    private byte[]? _serverKey;
    private bool _useUdpChannel;
    private ClientState _state = ClientState.None;
    private int ProtocolVersion { get; }

    internal Nat Nat { get; }
    internal Tunnel Tunnel { get; }
    internal ClientSocketFactory SocketFactory { get; }

    public event EventHandler? StateChanged;
    public Version? ServerVersion { get; private set; }
    public IPAddress? PublicAddress { get; private set; }
    public bool IsIpV6Supported { get; private set; }
    public TimeSpan SessionTimeout { get; set; }
    public Token Token { get; }
    public Guid ClientId { get; }
    public ulong SessionId { get; private set; }
    public IPAddress[] DnsServers { get; }
    public SessionStatus SessionStatus { get; private set; } = new();
    public Version Version { get; }
    public bool ExcludeLocalNetwork { get; }
    public IpRange[] IncludeIpRanges { get; private set; } = IpNetwork.All.ToIpRanges().ToArray();
    public IpRange[] PacketCaptureIncludeIpRanges { get; private set; }
    public string UserAgent { get; }
    public IPEndPoint? HostTcpEndPoint { get; private set; }
    public IPEndPoint? HostUdpEndPoint { get; private set; }
    public IPEndPoint[]? HostTcpEndPoints { get; private set; }
    public IPEndPoint[]? HostUdpEndPoints { get; private set; }
    public bool DropUdpPackets { get; set; }
    public ClientStat Stat { get; }
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException($"{nameof(SessionKey)} has not been initialized.");


    public VpnHoodClient(IPacketCapture packetCapture, Guid clientId, Token token, ClientOptions options)
    {
        if (options.TcpProxyCatcherAddressIpV4 == null)
            throw new ArgumentNullException(nameof(options.TcpProxyCatcherAddressIpV4));

        if (options.TcpProxyCatcherAddressIpV6 == null)
            throw new ArgumentNullException(nameof(options.TcpProxyCatcherAddressIpV6));

        if (!VhUtil.IsInfinite(_maxTcpDatagramLifespan) && _maxTcpDatagramLifespan < _minTcpDatagramLifespan)
            throw new ArgumentNullException(nameof(options.MaxTcpDatagramTimespan), $"{nameof(options.MaxTcpDatagramTimespan)} must be bigger or equal than {nameof(options.MinTcpDatagramTimespan)}.");

        SocketFactory = new ClientSocketFactory(packetCapture, options.SocketFactory ?? throw new ArgumentNullException(nameof(options.SocketFactory)));
        DnsServers = options.DnsServers ?? throw new ArgumentNullException(nameof(options.DnsServers));
        _allowAnonymousTracker = options.AllowAnonymousTracker;
        _dnsServerIpV4 = DnsServers.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        _dnsServerIpV6 = DnsServers.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetworkV6);
        _minTcpDatagramLifespan = options.MinTcpDatagramTimespan;
        _maxTcpDatagramLifespan = options.MaxTcpDatagramTimespan;
        _packetCapture = packetCapture;
        _autoDisposePacketCapture = options.AutoDisposePacketCapture;
        _maxDatagramChannelCount = options.MaxDatagramChannelCount;
        _proxyManager = new ClientProxyManager(packetCapture, SocketFactory, new ProxyManagerOptions());
        _ipRangeProvider = options.IpRangeProvider;
        _appGa4MeasurementId = options.AppGa4MeasurementId;
        _connectorService = new ConnectorService(SocketFactory, options.ConnectTimeout);
        _useUdpChannel = options.UseUdpChannel;
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Version = options.Version ?? throw new ArgumentNullException(nameof(Version));
        UserAgent = options.UserAgent ?? throw new ArgumentNullException(nameof(UserAgent));
        ProtocolVersion = 4;
        ClientId = clientId;
        SessionTimeout = options.SessionTimeout;
        ExcludeLocalNetwork = options.ExcludeLocalNetwork;
        PacketCaptureIncludeIpRanges = options.PacketCaptureIncludeIpRanges;
        DropUdpPackets = options.DropUdpPackets;
        Nat = new Nat(true);

        // init packetCapture cancellation
        packetCapture.OnStopped += PacketCapture_OnStopped;
        packetCapture.OnPacketReceivedFromInbound += PacketCapture_OnPacketReceivedFromInbound;

        // create tunnel
        Tunnel = new Tunnel();
        Tunnel.OnPacketReceived += Tunnel_OnPacketReceived;

        // create proxy host
        _clientHost = new ClientHost(this, options.TcpProxyCatcherAddressIpV4, options.TcpProxyCatcherAddressIpV6);

        // Create simple disposable objects
        _cancellationTokenSource = new CancellationTokenSource();
        Stat = new ClientStat(this);

#if DEBUG
        if (options.ProtocolVersion != 0) ProtocolVersion = options.ProtocolVersion;
#endif
    }

    public ClientState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value; //must set before raising the event; 
            VhLogger.Instance.LogInformation("Client state is changed. NewState: {NewState}", State);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool UseUdpChannel
    {
        get => _useUdpChannel;
        set
        {
            if (_useUdpChannel == value) return;
            _useUdpChannel = value;
            _ = ManageDatagramChannels(_cancellationTokenSource.Token);
        }
    }

    private void PacketCapture_OnStopped(object sender, EventArgs e)
    {
        VhLogger.Instance.LogTrace("Device has been stopped!");
        _ = DisposeAsync(false);
    }

    internal async Task AddPassthruTcpStream(IClientStream orgTcpClientStream, IPEndPoint hostEndPoint, string channelId,
        CancellationToken cancellationToken)
    {
        // set timeout
        using var cancellationTokenSource = new CancellationTokenSource(_connectorService.RequestTimeout);
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, cancellationToken);
        cancellationToken = linkedCancellationTokenSource.Token;

        // connect to host
        var tcpClient = SocketFactory.CreateTcpClient(hostEndPoint.AddressFamily);
        await VhUtil.RunTask(tcpClient.ConnectAsync(hostEndPoint.Address, hostEndPoint.Port), cancellationToken: cancellationToken);

        // create and add the channel
        var bypassChannel = new StreamProxyChannel(channelId, orgTcpClientStream,
            new TcpClientStream(tcpClient, tcpClient.GetStream(), channelId + ":host"));

        try { _proxyManager.AddChannel(bypassChannel); }
        catch { await bypassChannel.DisposeAsync(); throw; }
    }

    public async Task Connect()
    {
        using var scope = VhLogger.Instance.BeginScope("Client");
        if (_disposed) throw new ObjectDisposedException(nameof(VpnHoodClient));

        if (State != ClientState.None)
            throw new Exception("Connection is already in progress.");

        // report config
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        VhLogger.Instance.LogInformation(
            "UseUdpChannel: {UseUdpChannel}, DropUdpPackets: {DropUdpPackets}, ExcludeLocalNetwork: {ExcludeLocalNetwork}, " +
            "MinWorkerThreads: {WorkerThreads}, CompletionPortThreads: {CompletionPortThreads}",
            UseUdpChannel, DropUdpPackets, ExcludeLocalNetwork, workerThreads, completionPortThreads);

        // report version
        VhLogger.Instance.LogInformation("ClientVersion: {ClientVersion}, ClientProtocolVersion: {ClientProtocolVersion}, ClientId: {ClientId}",
            Version, ProtocolVersion, VhLogger.FormatId(ClientId));

        // Starting
        State = ClientState.Connecting;
        SessionStatus = new SessionStatus();

        // Connect
        try
        {
            // Init hostEndPoint
            _connectorService.EndPointInfo = new ConnectorEndPointInfo
            {
                HostName = Token.HostName,
                TcpEndPoint = await Token.ResolveHostEndPointAsync(),
                CertificateHash = Token.CertificateHash
            };
            HostTcpEndPoint = await Token.ResolveHostEndPointAsync();

            // Establish first connection and create a session
            await ConnectInternal(_cancellationTokenSource.Token);

            // Create Tcp Proxy Host
            _clientHost.Start();

            // Preparing device;
            if (!_packetCapture.Started) //make sure it is not a shared packet capture
            {
                ConfigPacketFilter(HostTcpEndPoint);
                _packetCapture.StartCapture();
            }

            // disable IncludeIpRanges if it contains all networks
            if (IncludeIpRanges.ToIpNetworks().IsAll())
                IncludeIpRanges = Array.Empty<IpRange>();

            State = ClientState.Connected;
        }
        catch (Exception ex)
        {
            _ = DisposeAsync(ex);
            throw;
        }
    }

    private void ConfigPacketFilter(IPEndPoint hostEndPoint)
    {
        // DnsServer
        if (_packetCapture.IsDnsServersSupported)
            _packetCapture.DnsServers = DnsServers;

        // Built-in IpV6 support
        if (_packetCapture.IsAddIpV6AddressSupported)
            _packetCapture.AddIpV6Address = true; //lets block ipV6 if not supported

        // Start with user PacketCaptureIncludeIpRanges
        var includeIpRanges = (IEnumerable<IpRange>)PacketCaptureIncludeIpRanges;

        // exclude server if ProtectSocket is not supported to prevent loop
        if (!_packetCapture.CanProtectSocket)
            includeIpRanges = includeIpRanges.Exclude(new[] { new IpRange(hostEndPoint.Address) });

        // exclude local networks
        if (ExcludeLocalNetwork)
            includeIpRanges = includeIpRanges.Exclude(IpNetwork.LocalNetworks.ToIpRanges());

        // Make sure CatcherAddress is included
        includeIpRanges = includeIpRanges.Concat(new[]
        {
            new IpRange(_clientHost.CatcherAddressIpV4),
            new IpRange(_clientHost.CatcherAddressIpV6)
        });

        _packetCapture.IncludeNetworks = includeIpRanges.Sort().ToIpNetworks().ToArray(); //sort and unify
        VhLogger.Instance.LogInformation($"PacketCapture Include Networks: {string.Join(", ", _packetCapture.IncludeNetworks.Select(x => x.ToString()))}");
    }

    // WARNING: Performance Critical!
    private void Tunnel_OnPacketReceived(object sender, ChannelPacketReceivedEventArgs e)
    {
        // manually manage DNS reply if DNS does not supported by _packetCapture
        if (!_packetCapture.IsDnsServersSupported)
            foreach (var ipPacket in e.IpPackets)
                UpdateDnsRequest(ipPacket, false);

        _packetCapture.SendPacketToInbound(e.IpPackets);
    }

    // WARNING: Performance Critical!
    private void PacketCapture_OnPacketReceivedFromInbound(object sender, PacketReceivedEventArgs e)
    {
        if (_disposed)
            return;

        try
        {
            lock (_sendingPacket) // this method should not be called in multi-thread, if so we need to allocate the list per call
            {
                _sendingPacket.Clear(); // prevent reallocation in this intensive event
                var tunnelPackets = _sendingPacket.TunnelPackets;
                var tcpHostPackets = _sendingPacket.TcpHostPackets;
                var passthruPackets = _sendingPacket.PassthruPackets;
                var proxyPackets = _sendingPacket.ProxyPackets;
                var droppedPackets = _sendingPacket.DroppedPackets;
                foreach (var ipPacket in e.IpPackets)
                {
                    if (_disposed) return;
                    var isIpV6 = ipPacket.DestinationAddress.AddressFamily == AddressFamily.InterNetworkV6;
                    var udpPacket = ipPacket.Protocol == ProtocolType.Udp ? ipPacket.Extract<UdpPacket>() : null;
                    var isDnsPacket = udpPacket?.DestinationPort == 53;

                    // DNS packet must go through tunnel even if it is not in range
                    if (isDnsPacket)
                    {
                        // Drop IPv6 if not support
                        if (isIpV6 && !IsIpV6Supported)
                        {
                            droppedPackets.Add(ipPacket);
                        }
                        else
                        {
                            if (!_packetCapture.IsDnsServersSupported)
                                UpdateDnsRequest(ipPacket, true);

                            tunnelPackets.Add(ipPacket);
                        }
                    }

                    else if (_packetCapture.CanSendPacketToOutbound)
                    {
                        if (!IsInIpRange(ipPacket.DestinationAddress))
                            passthruPackets.Add(ipPacket);

                        // Drop IPv6 if not support
                        else if (isIpV6 && !IsIpV6Supported)
                            droppedPackets.Add(ipPacket);

                        // Check IPv6 control message such as Solicitations
                        else if (IsIcmpControlMessage(ipPacket))
                            droppedPackets.Add(ipPacket);

                        else if (ipPacket.Protocol == ProtocolType.Tcp)
                            tcpHostPackets.Add(ipPacket);

                        else if (ipPacket.Protocol is ProtocolType.Icmp or ProtocolType.IcmpV6)
                            tunnelPackets.Add(ipPacket);

                        else if (ipPacket.Protocol is ProtocolType.Udp && !DropUdpPackets)
                            tunnelPackets.Add(ipPacket);

                        else
                            droppedPackets.Add(ipPacket);
                    }
                    else
                    {
                        // Drop IPv6 if not support
                        if (isIpV6 && !IsIpV6Supported)
                            droppedPackets.Add(ipPacket);

                        // Check IPv6 control message such as Solicitations
                        else if (IsIcmpControlMessage(ipPacket))
                            droppedPackets.Add(ipPacket);

                        else if (ipPacket.Protocol == ProtocolType.Tcp)
                            tcpHostPackets.Add(ipPacket);

                        // ICMP packet must go through tunnel because PingProxy does not support protect socket
                        else if (ipPacket.Protocol is ProtocolType.Icmp or ProtocolType.IcmpV6)
                            tunnelPackets.Add(ipPacket);

                        // Udp
                        else if (ipPacket.Protocol == ProtocolType.Udp && !DropUdpPackets)
                        {
                            if (IsInIpRange(ipPacket.DestinationAddress))
                                tunnelPackets.Add(ipPacket);
                            else
                                proxyPackets.Add(ipPacket);
                        }

                        else
                            droppedPackets.Add(ipPacket);

                    }
                }

                // send packets
                if (tunnelPackets.Count > 0 && ShouldManageDatagramChannels) _ = ManageDatagramChannels(_cancellationTokenSource.Token);
                if (tunnelPackets.Count > 0) Tunnel.SendPackets(tunnelPackets).Wait(_cancellationTokenSource.Token);
                if (passthruPackets.Count > 0) _packetCapture.SendPacketToOutbound(passthruPackets.ToArray());
                if (proxyPackets.Count > 0) _proxyManager.SendPackets(proxyPackets).Wait(_cancellationTokenSource.Token);
                if (tcpHostPackets.Count > 0) _packetCapture.SendPacketToInbound(_clientHost.ProcessOutgoingPacket(tcpHostPackets));
            }
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogError(ex, "Could not process packet the capture packets.");
        }
    }

    private static bool IsIcmpControlMessage(IPPacket ipPacket)
    {
        // IPv4
        if (ipPacket is { Version: IPVersion.IPv4, Protocol: ProtocolType.Icmp })
        {
            var icmpPacket = ipPacket.Extract<IcmpV4Packet>();
            return icmpPacket.TypeCode != IcmpV4TypeCode.EchoRequest; // drop all other Icmp but echo
        }

        // IPv6
        if (ipPacket is { Version: IPVersion.IPv6, Protocol: ProtocolType.IcmpV6 })
        {
            var icmpPacket = ipPacket.Extract<IcmpV6Packet>();
            if (icmpPacket.Type == IcmpV6Type.EchoRequest)
                return false;

            if (icmpPacket.Type == IcmpV6Type.NeighborSolicitation)
            {
                //_packetCapture.SendPacketToInbound(PacketUtil.CreateIcmpV6NeighborAdvertisement(ipPacket));
            }

            else if (icmpPacket.Type == IcmpV6Type.RouterSolicitation)
            {
                //_packetCapture.SendPacketToInbound(PacketUtil.CreateIcmpV6RouterAdvertisement(ipPacket));
            }

            return true; // drop all other Icmp but echo
        }

        return false;
    }

    public bool IsInIpRange(IPAddress ipAddress)
    {
        // all IPs are included if there is no filter
        if (VhUtil.IsNullOrEmpty(IncludeIpRanges))
            return true;

        // check tcp-loopback
        if (ipAddress.Equals(_clientHost.CatcherAddressIpV4) ||
            ipAddress.Equals(_clientHost.CatcherAddressIpV6))
            return true;

        // check the cache
        if (_includeIps.TryGetValue(ipAddress, out var isInRange))
            return isInRange;

        // check include
        isInRange = IpRange.IsInSortedRanges(IncludeIpRanges, ipAddress);

        // cache the result
        // we really don't need to keep that much ips in the cache
        if (_includeIps.Count > 0xFFFF)
        {
            VhLogger.Instance.LogInformation("Clearing IP filter cache!");
            _includeIps.Clear();
        }

        _includeIps.Add(ipAddress, isInRange);
        return isInRange;
    }

    // ReSharper disable once UnusedMethodReturnValue.Local
    private bool UpdateDnsRequest(IPPacket ipPacket, bool outgoing)
    {
        if (ipPacket is null) throw new ArgumentNullException(nameof(ipPacket));
        if (ipPacket.Protocol != ProtocolType.Udp) return false;

        // find dns server
        var dnsServer = ipPacket.Version == IPVersion.IPv4 ? _dnsServerIpV4 : _dnsServerIpV6;
        if (dnsServer == null)
        {
            VhLogger.Instance.LogWarning("There is no DNS server for this Address Family. AddressFamily : {AddressFamily}",
                ipPacket.DestinationAddress.AddressFamily);
            return false;
        }

        // manage DNS outgoing packet if requested DNS is not VPN DNS
        if (outgoing && !ipPacket.DestinationAddress.Equals(dnsServer))
        {
            var udpPacket = PacketUtil.ExtractUdp(ipPacket);
            if (udpPacket.DestinationPort == 53) //53 is DNS port
            {
                VhLogger.Instance.Log(LogLevel.Information, GeneralEventId.Dns,
                    $"DNS request from {VhLogger.Format(ipPacket.SourceAddress)}:{udpPacket.SourcePort} to {VhLogger.Format(ipPacket.DestinationAddress)}, Map to: {VhLogger.Format(dnsServer)}");
                udpPacket.SourcePort = Nat.GetOrAdd(ipPacket).NatId;
                ipPacket.DestinationAddress = dnsServer;
                PacketUtil.UpdateIpPacket(ipPacket);
                return true;
            }
        }

        // manage DNS incoming packet from VPN DNS
        else if (!outgoing && ipPacket.SourceAddress.Equals(dnsServer))
        {
            var udpPacket = PacketUtil.ExtractUdp(ipPacket);
            var natItem = (NatItemEx?)Nat.Resolve(ipPacket.Version, ProtocolType.Udp, udpPacket.DestinationPort);
            if (natItem != null)
            {
                VhLogger.Instance.Log(LogLevel.Information, GeneralEventId.Dns,
                    $"DNS reply to {VhLogger.Format(natItem.SourceAddress)}:{natItem.SourcePort}");
                ipPacket.SourceAddress = natItem.DestinationAddress;
                udpPacket.DestinationPort = natItem.SourcePort;
                PacketUtil.UpdateIpPacket(ipPacket);
                return true;
            }
        }

        return false;
    }

    private bool ShouldManageDatagramChannels => UseUdpChannel != Tunnel.IsUdpMode || Tunnel.DatagramChannelCount < _maxDatagramChannelCount;

    private async Task ManageDatagramChannels(CancellationToken cancellationToken)
    {
        if (_disposed || !await _datagramChannelsSemaphore.WaitAsync(0, cancellationToken))
            return;

        if (!ShouldManageDatagramChannels)
            return; 

        try
        {
            // make sure only one UdpChannel exists for DatagramChannels if UseUdpChannel is on
            if (UseUdpChannel)
                await AddUdpChannel(); 
            else
                await AddTcpDatagramChannel(cancellationToken);
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            VhLogger.LogError(GeneralEventId.DatagramChannel, ex, "Could not Manage DatagramChannels.");
        }
        finally
        {
            _datagramChannelsSemaphore.Release();
        }
    }

    private async Task AddUdpChannel()
    {
        if (HostTcpEndPoint == null) throw new InvalidOperationException($"{nameof(HostTcpEndPoint)} is not initialized!");
        if (VhUtil.IsNullOrEmpty(_serverKey)) throw new Exception("ServerSecret has not been set.");
        if (VhUtil.IsNullOrEmpty(_sessionKey)) throw new Exception("Server UdpKey has not been set.");
        if (HostUdpEndPoint == null)
        {
            UseUdpChannel = false;
            throw new Exception("Server does not serve any UDP endpoint.");
        }

        var udpClient = SocketFactory.CreateUdpClient(HostTcpEndPoint.AddressFamily);
        var udpChannel = new UdpChannel(SessionId, _sessionKey, false, _connectorService.ServerProtocolVersion);
        try
        {
            var udpChannelTransmitter = new ClientUdpChannelTransmitter(udpChannel, udpClient, _serverKey);
            udpChannel.SetRemote(udpChannelTransmitter, HostUdpEndPoint);
            Tunnel.AddChannel(udpChannel);
        }
        catch
        {
            udpClient.Dispose();
            await udpChannel.DisposeAsync();
            UseUdpChannel = false;
            throw;
        }
    }

    private async Task ConnectInternal(CancellationToken cancellationToken, bool redirecting = false)
    {
        try
        {
            // send hello request
            var clientInfo = new ClientInfo
            {
                ClientId = ClientId,
                ClientVersion = Version.ToString(3),
                ProtocolVersion = ProtocolVersion,
                UserAgent = UserAgent
            };

            var request = new HelloRequest(Guid.NewGuid() + ":client", Token.TokenId, clientInfo,
                VhUtil.EncryptClientId(clientInfo.ClientId, Token.Secret));

            await using var requestResult = await SendRequest<HelloSessionResponse>(request, cancellationToken);
            if (requestResult.Response.ServerProtocolVersion < 4)
                throw new SessionException(SessionErrorCode.UnsupportedServer, "This server is outdated and does not support this client!");

            // initialize the connector
            _connectorService.Init(
                requestResult.Response.ServerProtocolVersion,
                requestResult.Response.RequestTimeout,
                requestResult.Response.TcpReuseTimeout,
                requestResult.Response.ServerSecret);

            var sessionResponse = requestResult.Response;

            // log response
            VhLogger.Instance.LogInformation(GeneralEventId.Session,
                "Hurray! Client has been connected! " +
                $"SessionId: {VhLogger.FormatId(sessionResponse.SessionId)}, " +
                $"ServerVersion: {sessionResponse.ServerVersion}, " +
                $"ServerProtocolVersion: {sessionResponse.ServerProtocolVersion}, " +
                $"ClientIp: {VhLogger.Format(sessionResponse.ClientPublicAddress)}");

            // usage tracker
            if (_allowAnonymousTracker)
            {
                // Anonymous server usage tracker
                if (!string.IsNullOrEmpty(sessionResponse.GaMeasurementId))
                {
                    var ga4Tracking = new Ga4Tracker
                    {
                        SessionCount = 1,
                        MeasurementId = sessionResponse.GaMeasurementId,
                        ApiSecret = string.Empty,
                        ClientId = ClientId.ToString(),
                        SessionId = SessionId.ToString(),
                        UserAgent = UserAgent
                    };

                    var useProperties = new Dictionary<string, object> { { "client_version", Version.ToString(3) } };
                    _ = ga4Tracking.Track(new Ga4TagEvent { EventName = Ga4TagEvents.SessionStart }, useProperties);
                }

                // Anonymous app usage tracker
                if (!string.IsNullOrEmpty(_appGa4MeasurementId))
                    _clientUsageTracker = new ClientUsageTracker(Stat, Version, new Ga4Tracker
                    {
                        MeasurementId = _appGa4MeasurementId,
                        SessionCount = 1,
                        ApiSecret = string.Empty,
                        ClientId = ClientId.ToString(),
                        SessionId = SessionId.ToString(),
                        UserAgent = UserAgent
                    });
            }

            // get session id
            SessionId = sessionResponse.SessionId != 0 ? sessionResponse.SessionId : throw new Exception("Invalid SessionId!");
            _sessionKey = sessionResponse.SessionKey;
            _serverKey = sessionResponse.ServerSecret;
            _helloTraffic = sessionResponse.AccessUsage?.Traffic ?? new Traffic();
            SessionStatus.SuppressedTo = sessionResponse.SuppressedTo;
            PublicAddress = sessionResponse.ClientPublicAddress;
            ServerVersion = Version.Parse(sessionResponse.ServerVersion);
            IsIpV6Supported = sessionResponse.IsIpV6Supported;
            HostUdpEndPoint = sessionResponse.UdpEndPoints.FirstOrDefault(x => x.AddressFamily == HostTcpEndPoint?.AddressFamily);

            // set endpoints
            HostTcpEndPoints = sessionResponse.TcpEndPoints;
            HostUdpEndPoints = sessionResponse.UdpEndPoints;

            // PacketCaptureIpRanges
            if (!VhUtil.IsNullOrEmpty(sessionResponse.PacketCaptureIncludeIpRanges))
                PacketCaptureIncludeIpRanges = PacketCaptureIncludeIpRanges.Intersect(sessionResponse.PacketCaptureIncludeIpRanges).ToArray();

            // IncludeIpRanges
            if (!VhUtil.IsNullOrEmpty(sessionResponse.IncludeIpRanges) && !sessionResponse.IncludeIpRanges.ToIpNetworks().IsAll())
                IncludeIpRanges = IncludeIpRanges.Intersect(sessionResponse.IncludeIpRanges).ToArray();

            // Get IncludeIpRange for clientIp
            var filterIpRanges = _ipRangeProvider != null ? await _ipRangeProvider.GetIncludeIpRanges(sessionResponse.ClientPublicAddress) : null;
            if (!VhUtil.IsNullOrEmpty(filterIpRanges))
                IncludeIpRanges = IncludeIpRanges.Intersect(filterIpRanges).ToArray();

            // Preparing tunnel
            Tunnel.MaxDatagramChannelCount = sessionResponse.MaxDatagramChannelCount != 0
                ? Tunnel.MaxDatagramChannelCount = Math.Min(_maxDatagramChannelCount, sessionResponse.MaxDatagramChannelCount)
                : _maxDatagramChannelCount;

            // report Suppressed
            if (sessionResponse.SuppressedTo == SessionSuppressType.YourSelf)
                VhLogger.Instance.LogWarning("You suppressed a session of yourself!");

            else if (sessionResponse.SuppressedTo == SessionSuppressType.Other)
                VhLogger.Instance.LogWarning("You suppressed a session of another client!");

            _ = ManageDatagramChannels(cancellationToken);

        }
        catch (RedirectHostException ex) when (!redirecting)
        {
            if (_connectorService.EndPointInfo == null)
                throw new InvalidOperationException($"{nameof(ConnectorService.EndPointInfo)} is not initialized.");

            _connectorService.EndPointInfo.TcpEndPoint = ex.RedirectHostEndPoint;
            HostTcpEndPoint = ex.RedirectHostEndPoint;
            await ConnectInternal(_cancellationTokenSource.Token, true);
        }
    }

    private async Task AddTcpDatagramChannel(CancellationToken cancellationToken)
    {
        // Create and send the Request Message
        var request = new TcpDatagramChannelRequest(Guid.NewGuid() + ":client", SessionId, SessionKey);
        var requestResult = await SendRequest<SessionResponseBase>(request, cancellationToken);
        StreamDatagramChannel? channel = null;
        try
        {
            // find timespan
            var lifespan = !VhUtil.IsInfinite(_maxTcpDatagramLifespan) && IsTcpDatagramLifespanSupported
                ? TimeSpan.FromSeconds(new Random().Next((int)_minTcpDatagramLifespan.TotalSeconds, (int)_maxTcpDatagramLifespan.TotalSeconds))
                : Timeout.InfiniteTimeSpan;

            // add the new channel
            channel = new StreamDatagramChannel(requestResult.ClientStream, request.RequestId, lifespan);
            Tunnel.AddChannel(channel);
        }
        catch
        {
            if (channel != null) await channel.DisposeAsync();
            await requestResult.DisposeAsync();
            throw;
        }
    }

    internal async Task<ConnectorRequestResult<T>> SendRequest<T>(ClientRequest request, CancellationToken cancellationToken)
        where T : SessionResponseBase
    {
        IClientStream? clientStream = null;

        try
        {
            // log this request
            var requestCode = (RequestCode)request.RequestCode;
            var eventId = requestCode switch
            {
                RequestCode.Hello => GeneralEventId.Session,
                RequestCode.TcpDatagramChannel => GeneralEventId.DatagramChannel,
                RequestCode.StreamProxyChannel => GeneralEventId.StreamProxyChannel,
                _ => GeneralEventId.Tcp
            };

            // create a connection and send the request 
            var requestResult = await _connectorService.SendRequest<T>(eventId, request, cancellationToken);
            var response = requestResult.Response;
            clientStream = requestResult.ClientStream;

            // set SessionStatus
            if (response.AccessUsage != null)
                SessionStatus.AccessUsage = response.AccessUsage;

            // client is disposed meanwhile
            if (_disposed)
                throw new ObjectDisposedException(VhLogger.FormatType(this));

            if (response.ErrorCode == SessionErrorCode.RedirectHost) throw new RedirectHostException(response);
            if (response.ErrorCode == SessionErrorCode.Maintenance) throw new MaintenanceException();
            if (response.ErrorCode != SessionErrorCode.Ok) throw new SessionException(response);

            _lastConnectionErrorTime = null;
            State = ClientState.Connected;
            return requestResult;
        }
        catch (SessionException ex) when (ex.SessionResponseBase.ErrorCode is SessionErrorCode.GeneralError or SessionErrorCode.RedirectHost)
        {
            // GeneralError and RedirectHost mean that the request accepted by server but there is an error for that request
            _lastConnectionErrorTime = null;
            if (clientStream != null) await clientStream.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            // set connecting state if it could not establish any connection
            if (!_disposed && State == ClientState.Connected)
                State = ClientState.Connecting;

            // dispose by session timeout or known exception
            if (clientStream != null) await clientStream.DisposeAsync();
            _lastConnectionErrorTime ??= FastDateTime.Now;
            if (ex is SessionException or UnauthorizedAccessException || FastDateTime.Now - _lastConnectionErrorTime.Value > SessionTimeout)
                _ = DisposeAsync(ex);

            throw;
        }
    }

    private async Task SendByeRequest(CancellationToken cancellationToken)
    {
        try
        {
            await using var requestResult = await _connectorService.SendRequest<SessionResponseBase>(GeneralEventId.Session,
                new ByeRequest(Guid.NewGuid() + ":client", SessionId, SessionKey),
                cancellationToken);
        }
        catch (Exception ex)
        {
            VhLogger.LogError(GeneralEventId.Session, ex, "Could not send the bye request.");
        }
    }

    private ValueTask DisposeAsync(Exception ex)
    {
        if (_disposed) return default;

        VhLogger.Instance.LogError(GeneralEventId.Session, ex, "Disposing...");

        // set SessionStatus error code if not set yet
        if (SessionStatus.ErrorCode == SessionErrorCode.Ok)
        {

            if (ex is SessionException sessionException)
            {
                SessionStatus.ErrorCode = sessionException.SessionResponseBase.ErrorCode;
                SessionStatus.ErrorMessage = sessionException.SessionResponseBase.ErrorMessage;
                SessionStatus.SuppressedBy = sessionException.SessionResponseBase.SuppressedBy;
                if (sessionException.SessionResponseBase.AccessUsage != null) //update AccessUsage if exists
                {
                    SessionStatus.AccessUsage = sessionException.SessionResponseBase.AccessUsage;
                    SessionStatus.AccessUsage.Traffic = _helloTraffic; // let calculate it on client
                }
            }
            else
            {
                SessionStatus.ErrorCode = SessionErrorCode.GeneralError;
                SessionStatus.ErrorMessage = ex.Message;
            }
        }

        return DisposeAsync(false);
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    private readonly AsyncLock _disposeLock = new();
    private ValueTask? _disposeTask;
    public ValueTask DisposeAsync()
    {
        return DisposeAsync(false);
    }

    public ValueTask DisposeAsync(bool waitForBye)
    {
        lock (_disposeLock)
            _disposeTask ??= DisposeAsyncCore(waitForBye);
        return _disposeTask.Value;
    }

    private async ValueTask DisposeAsyncCore(bool waitForBye)
    {
        // shutdown
        VhLogger.Instance.LogTrace("Shutting down...");

        _disposed = true;
        _cancellationTokenSource.Cancel();
        var wasConnected = State is ClientState.Connecting or ClientState.Connected;
        State = ClientState.Disconnecting;

        // log suppressedBy
        if (SessionStatus.SuppressedBy == SessionSuppressType.YourSelf)
            VhLogger.Instance.LogWarning("You suppressed by a session of yourself!");

        else if (SessionStatus.SuppressedBy == SessionSuppressType.Other)
            VhLogger.Instance.LogWarning("You suppressed a session of another client!");

        // disposing PacketCapture. Must be at end for graceful shutdown
        _packetCapture.OnStopped -= PacketCapture_OnStopped;
        _packetCapture.OnPacketReceivedFromInbound -= PacketCapture_OnPacketReceivedFromInbound;
        if (_autoDisposePacketCapture)
        {
            VhLogger.Instance.LogTrace("Disposing the PacketCapture...");
            _packetCapture.Dispose();
        }

        var finalizeTask = Finalize(wasConnected);
        if (waitForBye)
            await finalizeTask;
    }

    private async Task Finalize(bool wasConnected)
    {
        // Anonymous usage tracker
        _clientUsageTracker?.DisposeAsync();

        VhLogger.Instance.LogTrace("Disposing ClientHost...");
        await _clientHost.DisposeAsync();

        // Tunnel
        VhLogger.Instance.LogTrace("Disposing Tunnel...");
        Tunnel.OnPacketReceived -= Tunnel_OnPacketReceived;
        await Tunnel.DisposeAsync();

        VhLogger.Instance.LogTrace("Disposing ProxyManager...");
        await _proxyManager.DisposeAsync();

        // dispose NAT
        VhLogger.Instance.LogTrace("Disposing Nat...");
        Nat.Dispose();

        // Sending Bye
        if (wasConnected && SessionId != 0 && SessionStatus.ErrorCode == SessionErrorCode.Ok)
        {
            using var cancellationTokenSource = new CancellationTokenSource(TunnelDefaults.TcpGracefulTimeout);
            await SendByeRequest(cancellationTokenSource.Token);
        }

        // dispose ConnectorService
        VhLogger.Instance.LogTrace("Disposing ConnectorService...");
        await _connectorService.DisposeAsync();

        State = ClientState.Disposed;
        VhLogger.Instance.LogInformation("Bye Bye!");

    }

    private class SendingPackets
    {
        public readonly List<IPPacket> PassthruPackets = [];
        public readonly List<IPPacket> ProxyPackets = [];
        public readonly List<IPPacket> TcpHostPackets = [];
        public readonly List<IPPacket> TunnelPackets = [];
        public readonly List<IPPacket> DroppedPackets = [];

        public void Clear()
        {
            TunnelPackets.Clear();
            PassthruPackets.Clear();
            TcpHostPackets.Clear();
            ProxyPackets.Clear();
            DroppedPackets.Clear();
        }
    }

    public class ClientStat
    {
        private readonly VpnHoodClient _client;
        public ConnectorStat ConnectorStat => _client._connectorService.Stat;
        public Traffic Speed => _client.Tunnel.Speed;
        public Traffic SessionTraffic => _client.Tunnel.Traffic;
        public Traffic AccountTraffic => _client._helloTraffic + SessionTraffic;
        public int DatagramChannelCount => _client.Tunnel.DatagramChannelCount;
        public bool IsUdpMode => _client.Tunnel.IsUdpMode;

        internal ClientStat(VpnHoodClient vpnHoodClient)
        {
            _client = vpnHoodClient;
        }
    }
}