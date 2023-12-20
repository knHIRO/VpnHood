using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VpnHood.Client;
using VpnHood.Client.App;
using VpnHood.Client.Device;
using VpnHood.Client.Diagnosing;
using VpnHood.Common;
using VpnHood.Common.Converters;
using VpnHood.Common.JobController;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Net;
using VpnHood.Common.Utils;
using VpnHood.Server;
using VpnHood.Server.Access.Configurations;
using VpnHood.Server.Access.Managers;
using VpnHood.Server.Access.Managers.File;
using VpnHood.Server.Access.Messaging;
using VpnHood.Test.Factory;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Factory;
using ProtocolType = PacketDotNet.ProtocolType;

namespace VpnHood.Test;

internal static class TestHelper
{
    private const int DefaultTimeout = 30000;
    public static readonly Uri TEST_HttpsUri1 = new("https://www.quad9.net/");
    public static readonly Uri TEST_HttpsUri2 = new("https://www.google.com/");
    public static readonly IPEndPoint TEST_NsEndPoint1 = IPEndPoint.Parse("1.1.1.1:53");
    public static readonly IPEndPoint TEST_NsEndPoint2 = IPEndPoint.Parse("1.0.0.1:53");
    public static readonly IPEndPoint TEST_TcpEndPoint1 = IPEndPoint.Parse("198.18.0.1:80");
    public static readonly IPEndPoint TEST_TcpEndPoint2 = IPEndPoint.Parse("198.18.0.2:80");
    public static readonly IPEndPoint TEST_HttpsEndPoint1 = IPEndPoint.Parse("198.18.0.1:3030");
    public static readonly IPEndPoint TEST_HttpsEndPoint2 = IPEndPoint.Parse("198.18.0.2:3030");
    public static readonly IPEndPoint TEST_UdpV4EndPoint1 = IPEndPoint.Parse("198.18.10.1:63100");
    public static readonly IPEndPoint TEST_UdpV4EndPoint2 = IPEndPoint.Parse("198.18.10.2:63101");
    public static readonly IPEndPoint TEST_UdpV6EndPoint1 = IPEndPoint.Parse("[2001:4860:4866::2223]:63100");
    public static readonly IPEndPoint TEST_UdpV6EndPoint2 = IPEndPoint.Parse("[2001:4860:4866::2223]:63101");
    public static readonly IPAddress TEST_PingV4Address1 = IPAddress.Parse("198.18.20.1");
    public static readonly IPAddress TEST_PingV4Address2 = IPAddress.Parse("198.18.20.2");
    public static readonly IPAddress TEST_PingV6Address1 = IPAddress.Parse("2001:4860:4866::2200");

    public static readonly Uri TEST_InvalidUri = new("https://DBBC5764-D452-468F-8301-4B315507318F.zz");
    public static readonly IPAddress TEST_InvalidIp = IPAddress.Parse("198.18.255.1");
    public static readonly IPEndPoint TEST_InvalidEp = IPEndPointConverter.Parse("198.18.255.2:9999");
    public static TestWebServer WebServer { get; private set; } = default!;
    public static TestNetFilter NetFilter { get; private set; } = default!;

    private static int _accessItemIndex;

    public static string WorkingPath { get; } = Path.Combine(Path.GetTempPath(), "_test_vpnhood");

    public static string CreateNewFolder(string namePart)
    {
        var folder = Path.Combine(WorkingPath, $"{namePart}_{Guid.NewGuid()}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static void Cleanup()
    {
        try
        {
            if (Directory.Exists(WorkingPath))
                Directory.Delete(WorkingPath, true);
        }
        catch
        {
            // ignored
        }
    }

    public static Task WaitForClientStateAsync(VpnHoodApp app, AppConnectionState connectionSate, int timeout = 5000)
    {
        return VhTestUtil.AssertEqualsWait(connectionSate, () => app.State.ConnectionState, "App state didn't reach the expected value.", timeout);
    }

    public static Task WaitForClientStateAsync(VpnHoodClient client, ClientState clientState, int timeout = 6000)
    {
        return VhTestUtil.AssertEqualsWait(clientState, () => client.State, "Client state didn't reach the expected value.", timeout);
    }

    private static Task<PingReply> SendPing(Ping? ping = null, IPAddress? ipAddress = null, int timeout = DefaultTimeout)
    {
        using var pingT = new Ping();
        ping ??= pingT;
        var buffer = new byte[1024];
        new Random().NextBytes(buffer);
        return ping.SendPingAsync(ipAddress ?? TEST_PingV4Address1, timeout, buffer);
    }

    private static async Task<bool> SendHttpGet(HttpClient? httpClient = default, Uri? uri = default,
        int timeout = DefaultTimeout)
    {
        uri ??= TEST_HttpsUri1;

        using var httpClientT = new HttpClient(new HttpClientHandler
        {
            CheckCertificateRevocationList = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });

        httpClient ??= httpClientT;
        var cancellationTokenSource = new CancellationTokenSource(timeout);

        var requestMessage = new HttpRequestMessage(HttpMethod.Get, uri);

        // fix TLS host; it may map by NetFilter.ProcessRequest
        if (IPEndPoint.TryParse(requestMessage.RequestUri!.Authority, out var ipEndPoint))
            requestMessage.Headers.Host = NetFilter.ProcessRequest(ProtocolType.Tcp, ipEndPoint)!.Address.ToString();

        var response = await httpClient.SendAsync(requestMessage, cancellationTokenSource.Token);
        var res = await response.Content.ReadAsStringAsync(cancellationTokenSource.Token);
        return res.Length > 100;
    }

    public static async Task Test_Ping(Ping? ping = default, IPAddress? ipAddress = default, int timeout = DefaultTimeout)
    {
        var pingReply = await SendPing(ping, ipAddress, timeout);
        if (pingReply.Status != IPStatus.Success)
            throw new PingException($"Ping failed. Status: {pingReply.Status}");
    }

    public static void Test_Dns(UdpClient? udpClient = null, IPEndPoint? nsEndPoint = default, int timeout = 3000)
    {
        var hostEntry = DiagnoseUtil
            .GetHostEntry("www.google.com", nsEndPoint ?? TEST_NsEndPoint1, udpClient, timeout).Result;
        Assert.IsNotNull(hostEntry);
        Assert.IsTrue(hostEntry.AddressList.Length > 0);
    }

    public static Task Test_Udp(int timeout = DefaultTimeout)
    {
        return Test_Udp(TEST_UdpV4EndPoint1, timeout);
    }

    public static async Task Test_Udp(IPEndPoint udpEndPoint, int timeout = DefaultTimeout)
    {
        if (udpEndPoint.AddressFamily == AddressFamily.InterNetwork)
        {
            using var udpClientIpV4 = new UdpClient(AddressFamily.InterNetwork);
            await Test_Udp(udpClientIpV4, udpEndPoint, timeout);
        }

        else if (udpEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            using var udpClientIpV6 = new UdpClient(AddressFamily.InterNetworkV6);
            await Test_Udp(udpClientIpV6, udpEndPoint, timeout);
        }
    }

    public static async Task Test_Udp(UdpClient udpClient, IPEndPoint udpEndPoint, int timeout = DefaultTimeout)
    {
        var buffer = new byte[1024];
        new Random().NextBytes(buffer);
        var sentBytes = await udpClient.SendAsync(buffer, udpEndPoint, new CancellationTokenSource(timeout).Token);
        Assert.AreEqual(buffer.Length, sentBytes);

        var res = await udpClient.ReceiveAsync(new CancellationTokenSource(timeout).Token);
        CollectionAssert.AreEquivalent(buffer, res.Buffer);
    }

    public static async Task<bool> Test_Https(HttpClient? httpClient = default, Uri? uri = default,
        int timeout = DefaultTimeout, bool throwError = true)
    {
        if (throwError)
        {
            Assert.IsTrue(await SendHttpGet(httpClient, uri, timeout), "Https get doesn't work!");
            return true;
        }

        try
        {
            return await SendHttpGet(httpClient, uri, timeout);
        }
        catch
        {
            return false;
        }

    }

    public static IPAddress[] TestIpAddresses
    {
        get
        {
            var addresses = new List<IPAddress>
            {
                TEST_NsEndPoint1.Address,
                TEST_NsEndPoint2.Address,
                TEST_PingV4Address1,
                TEST_PingV4Address2,
                TEST_PingV6Address1,
                TEST_TcpEndPoint1.Address,
                TEST_TcpEndPoint2.Address,
                TEST_InvalidIp,
                TEST_UdpV4EndPoint1.Address,
                TEST_UdpV4EndPoint2.Address,
                new ClientOptions().TcpProxyCatcherAddressIpV4
            };
            addresses.AddRange(Dns.GetHostAddresses(TEST_HttpsUri1.Host));
            addresses.AddRange(Dns.GetHostAddresses(TEST_HttpsUri2.Host));
            return addresses.ToArray();
        }
    }

    public static Token CreateAccessToken(FileAccessManager fileAccessManager, IPEndPoint[]? hostEndPoints = null,
        int maxClientCount = 1, int maxTrafficByteCount = 0, DateTime? expirationTime = null)
    {
        return fileAccessManager.AccessItem_Create(
            hostEndPoints ?? fileAccessManager.ServerConfig.TcpEndPointsValue,
            tokenName: $"Test Server {++_accessItemIndex}",
            maxClientCount: maxClientCount,
            maxTrafficByteCount: maxTrafficByteCount,
            expirationTime: expirationTime
        ).Token;
    }

    public static Token CreateAccessToken(VpnHoodServer server,
        int maxClientCount = 1, int maxTrafficByteCount = 0, DateTime? expirationTime = null)
    {
        var testAccessManager = (TestAccessManager)server.AccessManager;
        var fileAccessManager = (FileAccessManager)testAccessManager.BaseAccessManager;
        return CreateAccessToken(fileAccessManager, null, maxClientCount, maxTrafficByteCount, expirationTime);
    }

    public static FileAccessManager CreateFileAccessManager(FileAccessManagerOptions? options = null, string? storagePath = null)
    {
        storagePath ??= Path.Combine(WorkingPath, $"AccessManager_{Guid.NewGuid()}");
        options ??= CreateFileAccessManagerOptions();
        return new FileAccessManager(storagePath, options);
    }

    public static FileAccessManagerOptions CreateFileAccessManagerOptions()
    {
        var options = new FileAccessManagerOptions
        {
            TcpEndPoints = new[] { VhUtil.GetFreeTcpEndPoint(IPAddress.Loopback) },
            UdpEndPoints = new[] { new IPEndPoint(IPAddress.Loopback, 0) },
            TrackingOptions = new TrackingOptions
            {
                TrackClientIp = true,
                TrackDestinationIp = true,
                TrackDestinationPort = true,
                TrackLocalPort = true
            },
            SessionOptions =
            {
                SyncCacheSize = 50,
                SyncInterval = TimeSpan.FromMilliseconds(100)
            },
            LogAnonymizer = false
        };
        return options;
    }

    public static VpnHoodServer CreateServer(IAccessManager? accessManager = null, bool autoStart = true, TimeSpan? configureInterval = null)
    {
        return CreateServer(accessManager, null, autoStart, configureInterval);
    }

    public static VpnHoodServer CreateServer(FileAccessManagerOptions? options, bool autoStart = true, TimeSpan? configureInterval = null)
    {
        return CreateServer(null, options, autoStart, configureInterval);
    }

    private static VpnHoodServer CreateServer(IAccessManager? accessManager, FileAccessManagerOptions? fileAccessManagerOptions, bool autoStart,
        TimeSpan? configureInterval = null)
    {
        if (accessManager != null && fileAccessManagerOptions != null)
            throw new InvalidOperationException($"Could not set both {nameof(accessManager)} and {nameof(fileAccessManagerOptions)}.");

        var autoDisposeAccessManager = false;
        if (accessManager == null)
        {
            accessManager = new TestAccessManager(CreateFileAccessManager(fileAccessManagerOptions));
            autoDisposeAccessManager = true;
        }

        // ser server options
        var serverOptions = new ServerOptions
        {
            SocketFactory = new TestSocketFactory(),
            ConfigureInterval = configureInterval ?? new ServerOptions().ConfigureInterval,
            AutoDisposeAccessManager = autoDisposeAccessManager,
            StoragePath = WorkingPath,
            NetFilter = NetFilter,
            PublicIpDiscovery = false, //it slows down our tests
        };

        // Create server
        var server = new VpnHoodServer(accessManager, serverOptions);
        if (autoStart)
        {
            server.Start().Wait();
            Assert.AreEqual(ServerState.Ready, server.State);
        }

        return server;
    }

    public static IDevice CreateDevice(TestDeviceOptions? options = default)
    {
        return new TestDevice(options);
    }

    public static IPacketCapture CreatePacketCapture(TestDeviceOptions? options = default)
    {
        return CreateDevice(options).CreatePacketCapture().Result;
    }

    public static ClientOptions CreateClientOptions(bool useUdp = false)
    {
        return new ClientOptions
        {
            MaxDatagramChannelCount = 1,
            UseUdpChannel = useUdp
        };
    }

    public static VpnHoodClient CreateClient(Token token,
        IPacketCapture? packetCapture = default,
        TestDeviceOptions? deviceOptions = default,
        Guid? clientId = default,
        bool autoConnect = true,
        ClientOptions? options = default,
        bool throwConnectException = true)
    {
        packetCapture ??= CreatePacketCapture(deviceOptions);
        clientId ??= Guid.NewGuid();
        options ??= CreateClientOptions();
        if (options.ConnectTimeout == new ClientOptions().ConnectTimeout) options.ConnectTimeout = TimeSpan.FromSeconds(3);
        options.PacketCaptureIncludeIpRanges = TestIpAddresses.Select(x => new IpRange(x)).ToArray();
        options.ExcludeLocalNetwork = false;

        var client = new VpnHoodClient(
            packetCapture,
            clientId.Value,
            token,
            options);

        // test starting the client
        try
        {
            if (autoConnect)
                Task.Run(() => client.Connect(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            if (throwConnectException)
                throw;
        }

        return client;
    }

    public static VpnHoodConnect CreateClientConnect(Token token,
        IPacketCapture? packetCapture = default,
        TestDeviceOptions? deviceOptions = default,
        Guid? clientId = default,
        bool autoConnect = true,
        ClientOptions? clientOptions = default,
        ConnectOptions? connectOptions = default)
    {
        clientOptions ??= new ClientOptions();
        packetCapture ??= CreatePacketCapture(deviceOptions);
        clientId ??= Guid.NewGuid();
        if (clientOptions.SessionTimeout == new ClientOptions().SessionTimeout)
            clientOptions.SessionTimeout = TimeSpan.FromSeconds(2); //overwrite default timeout
        clientOptions.SocketFactory = new SocketFactory();
        clientOptions.PacketCaptureIncludeIpRanges = TestIpAddresses.Select(x => new IpRange(x)).ToArray();
        clientOptions.ExcludeLocalNetwork = false;

        var clientConnect = new VpnHoodConnect(
            packetCapture,
            clientId.Value,
            token,
            clientOptions,
            connectOptions);

        // test starting the client
        if (autoConnect)
            clientConnect.Connect().Wait();

        return clientConnect;
    }

    public static AppOptions CreateClientAppOptions()
    {
        var appOptions = new AppOptions
        {
            AppDataFolderPath = Path.Combine(WorkingPath, "AppData_" + Guid.NewGuid()),
            SessionTimeout = TimeSpan.FromSeconds(2),
            LoadCountryIpGroups = false,
        };
        return appOptions;
    }

    public static VpnHoodApp CreateClientApp(TestDeviceOptions? deviceOptions = default, AppOptions? appOptions = default)
    {
        //create app
        appOptions ??= CreateClientAppOptions();

        var testAppProvider = new TestAppProvider(deviceOptions);
        var clientApp = VpnHoodApp.Init(testAppProvider, appOptions);
        clientApp.Diagnoser.HttpTimeout = 2000;
        clientApp.Diagnoser.NsTimeout = 2000;
        clientApp.UserSettings.PacketCaptureIncludeIpRanges = TestIpAddresses.Select(x => new IpRange(x)).ToArray();
        clientApp.TcpTimeout = TimeSpan.FromSeconds(2);
        clientApp.UserSettings.Logging.LogAnonymous = false;
        clientApp.UserSettings.Logging.LogVerbose = true;

        return clientApp;
    }

    public static SessionRequestEx CreateSessionRequestEx(Token token, Guid? clientId = null)
    {
        clientId ??= Guid.NewGuid();

        return new SessionRequestEx("access:" + Guid.NewGuid(),
            token.TokenId,
            new ClientInfo { ClientId = clientId.Value },
            hostEndPoint: token.HostEndPoints!.First(),
            encryptedClientId: VhUtil.EncryptClientId(clientId.Value, token.Secret));
    }

    public static bool IgnoreCertificateValidationCallback(object sender, 
        X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        _ = sender;
        _ = certificate;
        _ = chain;
        _ = sslPolicyErrors;
        return true;
    }


    private static bool _isInit;
    internal static void Init()
    {
        if (_isInit) return;
        _isInit = true;

        TunnelDefaults.TcpGracefulTimeout = TimeSpan.FromSeconds(10);
        VhLogger.Instance = VhLogger.CreateConsoleLogger(true);
        VhLogger.IsDiagnoseMode = true;
        VhLogger.IsAnonymousMode = false;
        WebServer = TestWebServer.Create();
        NetFilter = new TestNetFilter();
        NetFilter.Init(new[]
        {
            Tuple.Create(ProtocolType.Tcp, TEST_TcpEndPoint1, WebServer.HttpV4EndPoint1),
            Tuple.Create(ProtocolType.Tcp, TEST_TcpEndPoint2, WebServer.HttpV4EndPoint2),
            Tuple.Create(ProtocolType.Tcp, TEST_HttpsEndPoint1, WebServer.HttpsV4EndPoint1),
            Tuple.Create(ProtocolType.Tcp, TEST_HttpsEndPoint2, WebServer.HttpsV4EndPoint2),
            Tuple.Create(ProtocolType.Udp, TEST_UdpV4EndPoint1, WebServer.UdpV4EndPoint1),
            Tuple.Create(ProtocolType.Udp, TEST_UdpV4EndPoint2, WebServer.UdpV4EndPoint2),
            Tuple.Create(ProtocolType.Udp, TEST_UdpV6EndPoint1, WebServer.UdpV6EndPoint1),
            Tuple.Create(ProtocolType.Udp, TEST_UdpV6EndPoint2, WebServer.UdpV6EndPoint2),
            Tuple.Create(ProtocolType.Icmp, new IPEndPoint(TEST_PingV4Address1, 0), IPEndPoint.Parse("127.0.0.1:0")),
            Tuple.Create(ProtocolType.Icmp, new IPEndPoint(TEST_PingV4Address2, 0), IPEndPoint.Parse("127.0.0.2:0")),
            Tuple.Create(ProtocolType.IcmpV6, new IPEndPoint(TEST_PingV6Address1, 0), IPEndPoint.Parse("[::1]:0")),
        });
        FastDateTime.Precision = TimeSpan.FromMilliseconds(1);
        JobRunner.Default.Interval = TimeSpan.FromMilliseconds(200);
        JobSection.DefaultInterval = TimeSpan.FromMilliseconds(200);
    }
}