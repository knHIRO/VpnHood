using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using EmbedIO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VpnHood.Client;
using VpnHood.Client.App;
using VpnHood.Common;
using VpnHood.Common.Logging;
using VpnHood.Common.Net;
using VpnHood.Common.Utils;

namespace VpnHood.Test.Tests;

[TestClass]
public class ClientAppTest : TestBase
{
    private int _lastSupportId;

    private Token CreateToken()
    {
        var randomId = Guid.NewGuid();
        return new Token(randomId.ToByteArray(),
            randomId.ToByteArray(),
            randomId.ToString())
        {
            Name = "Default Test Server",
            SupportId = _lastSupportId++,
            HostEndPoints = new[] { IPEndPoint.Parse("127.0.0.1:443") },
            HostPort = 443,
            TokenId = randomId
        };
    }

    [TestMethod]
    public async Task Load_country_ip_groups()
    {
        // ************
        // *** TEST ***: 
        await using var app1 = TestHelper.CreateClientApp();
        var ipGroups = await app1.GetIpGroups();
        Assert.IsFalse(ipGroups.Any(x=>x.IpGroupId=="us"), 
            "Countries should not be extracted in test due to performance.");
        await app1.DisposeAsync();

        // ************
        // *** TEST ***: 
        var appOptions = TestHelper.CreateClientAppOptions();
        appOptions.LoadCountryIpGroups = true;
        await using var app2 = TestHelper.CreateClientApp(appOptions: appOptions);
        var ipGroups2 = await app2.GetIpGroups();
        Assert.IsTrue(ipGroups2.Any(x => x.IpGroupId == "us"),
            "Countries has not been extracted.");
    }


    [TestMethod]
    public async Task Add_remove_clientProfiles()
    {
        await using var app = TestHelper.CreateClientApp();

        // ************
        // *** TEST ***: AddAccessKey should add a clientProfile
        var token1 = CreateToken();
        var clientProfile1 = app.ClientProfileStore.AddAccessKey(token1.ToAccessKey());
        Assert.AreEqual(1, app.ClientProfileStore.ClientProfiles.Count(x => x.TokenId == token1.TokenId),
            "ClientProfile is not added");
        Assert.AreEqual(token1.TokenId, clientProfile1.TokenId,
            "invalid tokenId has been assigned to clientProfile");

        // ************
        // *** TEST ***: AddAccessKey with new accessKey should add another clientProfile
        var token2 = CreateToken();
        app.ClientProfileStore.AddAccessKey(token2.ToAccessKey());
        Assert.AreEqual(1, app.ClientProfileStore.ClientProfiles.Count(x => x.TokenId == token2.TokenId),
            "ClientProfile is not added");

        // ************
        // *** TEST ***: AddAccessKey by same accessKey should just update token
        token1.Name = "Token 1000";
        app.ClientProfileStore.AddAccessKey(token1.ToAccessKey());
        Assert.AreEqual(token1.Name, app.ClientProfileStore.GetToken(token1.TokenId).Name);

        // ************
        // *** TEST ***: SetClientProfile throw KeyNotFoundException exception if tokenId does not exist
        try
        {
            app.ClientProfileStore.SetClientProfile(new ClientProfile
            {
                Name = "Hi",
                ClientProfileId = Guid.NewGuid(),
                TokenId = Guid.NewGuid()
            });
            Assert.Fail("KeyNotFoundException exception was expected!");
        }
        catch (KeyNotFoundException)
        {
        }

        // ************
        // *** TEST ***: SetClientProfile should update the old node if ClientProfileId already exists
        app.ClientProfileStore.SetClientProfile(new ClientProfile
        {
            Name = "Hi2",
            ClientProfileId = clientProfile1.ClientProfileId,
            TokenId = clientProfile1.TokenId
        });
        Assert.AreEqual("Hi2",
            app.ClientProfileStore.ClientProfiles.First(x => x.ClientProfileId == clientProfile1.ClientProfileId)
                .Name);

        // ************
        // *** TEST ***: SetClientProfile should add new ClientProfile if ClientProfileId is new even with used tokenId
        var clientProfiles = app.ClientProfileStore.ClientProfiles;
        var clientProfileId3 = Guid.NewGuid();
        app.ClientProfileStore.SetClientProfile(new ClientProfile
        {
            Name = "Test-03",
            ClientProfileId = clientProfileId3,
            TokenId = clientProfile1.TokenId
        });
        Assert.AreEqual(clientProfiles.Length + 1, app.ClientProfileStore.ClientProfiles.Length,
            "ClientProfile has not been added!");

        // ************
        // *** TEST ***: RemoveClientProfile should not remove token when other clientProfile still use the token
        app.ClientProfileStore.RemoveClientProfile(clientProfileId3);
        Assert.AreEqual(clientProfiles.Length, app.ClientProfileStore.ClientProfiles.Length,
            "ClientProfile has not been removed!");
        Assert.IsNotNull(app.ClientProfileStore.GetToken(clientProfile1.TokenId));

        // ************
        // *** TEST ***: RemoveClientProfile should remove token when no clientProfile using it
        clientProfiles = app.ClientProfileStore.ClientProfiles;
        app.ClientProfileStore.RemoveClientProfile(clientProfile1.ClientProfileId);
        Assert.AreEqual(clientProfiles.Length - 1, app.ClientProfileStore.ClientProfiles.Length,
            "ClientProfile has not been removed!");
        try
        {
            app.ClientProfileStore.GetToken(clientProfile1.TokenId);
            Assert.Fail("KeyNotFoundException exception was expected!");
        }
        catch (KeyNotFoundException)
        {
        }

        // ************
        // *** TEST ***: ClientProfileItems
        Assert.AreEqual(app.ClientProfileStore.ClientProfiles.Length,
            app.ClientProfileStore.ClientProfileItems.Length, "ClientProfileItems has invalid length!");
    }

    [TestMethod]
    public async Task Token_secret_should_not_be_extracted()
    {
        await using var app = TestHelper.CreateClientApp();

        // ************
        // *** TEST ***: AddClientProfile should not return then secret
        var token = CreateToken();
        var clientProfile = app.ClientProfileStore.AddAccessKey(token.ToAccessKey());

        // ************
        // *** TEST ***: GetToken should not return then secret
        var token2 = app.ClientProfileStore.GetToken(clientProfile.TokenId);
        Assert.IsTrue(VhUtil.IsNullOrEmpty(token2.Secret), "token should not have secret");

        // ************
        // *** TEST ***: ClientProfileItems should not return then secret
        var clientProfiles = app.ClientProfileStore.ClientProfileItems;
        Assert.IsTrue(clientProfiles.All(x => VhUtil.IsNullOrEmpty(x.Token.Secret)), "token should not have secret");
    }

    [TestMethod]
    public async Task Save_load_clientProfiles()
    {
        await using var app = TestHelper.CreateClientApp();

        var token1 = CreateToken();
        var clientProfile1 = app.ClientProfileStore.AddAccessKey(token1.ToAccessKey());

        var token2 = CreateToken();
        var clientProfile2 = app.ClientProfileStore.AddAccessKey(token2.ToAccessKey());

        var clientProfiles = app.ClientProfileStore.ClientProfiles;
        await app.DisposeAsync();

        var appOptions = TestHelper.CreateClientAppOptions();
        appOptions.AppDataFolderPath = app.AppDataFolderPath;
        await using var app2 = TestHelper.CreateClientApp(appOptions: appOptions);
        Assert.AreEqual(clientProfiles.Length, app2.ClientProfileStore.ClientProfiles.Length,
            "ClientProfiles count are not same!");
        Assert.IsNotNull(
            app2.ClientProfileStore.ClientProfiles.First(x => x.ClientProfileId == clientProfile1.ClientProfileId));
        Assert.IsNotNull(
            app2.ClientProfileStore.ClientProfiles.First(x => x.ClientProfileId == clientProfile2.ClientProfileId));
        Assert.IsNotNull(app2.ClientProfileStore.GetToken(token1.TokenId));
        Assert.IsNotNull(app2.ClientProfileStore.GetToken(token2.TokenId));
    }

    [TestMethod]
    public async Task State_Diagnose_info()
    {
        // create server
        await using var server = TestHelper.CreateServer();
        var token = TestHelper.CreateAccessToken(server);

        // create app
        await using var app = TestHelper.CreateClientApp();
        var clientProfile1 = app.ClientProfileStore.AddAccessKey(token.ToAccessKey());

        // ************
        // Test: With diagnose
        _ = app.Connect(clientProfile1.ClientProfileId, true);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected, 10000);
        app.ClearLastError(); // should not affect
        await app.Disconnect(true);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.None);

        Assert.IsTrue(app.State.LogExists);
        Assert.IsTrue(app.State.HasDiagnoseStarted);
        Assert.IsTrue(app.State.HasDisconnectedByUser);
        Assert.IsTrue(app.State.HasProblemDetected);
        Assert.IsTrue(app.State.IsIdle);

        app.ClearLastError();
        Assert.IsFalse(app.State.HasDiagnoseStarted);
        Assert.IsFalse(app.State.HasDisconnectedByUser);
        Assert.IsFalse(app.State.HasProblemDetected);

        // ************
        // Test: Without diagnose
        // ReSharper disable once RedundantAssignment
        _ = app.Connect(clientProfile1.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);
        await app.Disconnect(true);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.None);

        Assert.IsFalse(app.State.LogExists);
        Assert.IsFalse(app.State.HasDiagnoseStarted);
        Assert.IsTrue(app.State.HasDisconnectedByUser);
        Assert.IsTrue(app.State.HasProblemDetected); //no data
        Assert.IsTrue(app.State.IsIdle);
    }

    [TestMethod]
    public async Task State_Error_InConnecting()
    {
        // create server
        await using var server = TestHelper.CreateServer();
        var token = TestHelper.CreateAccessToken(server);
        token.HostEndPoints = new[] { IPEndPoint.Parse("10.10.10.99:443") };

        // create app
        await using var app = TestHelper.CreateClientApp();
        var clientProfile = app.ClientProfileStore.AddAccessKey(token.ToAccessKey());

        try
        {
            await app.Connect(clientProfile.ClientProfileId);
        }
        catch
        {
            // ignored
        }

        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.None);
        Assert.IsFalse(app.State.LogExists);
        Assert.IsFalse(app.State.HasDiagnoseStarted);
        Assert.IsTrue(app.State.HasProblemDetected);
        Assert.IsNotNull(app.State.LastError);
    }

    [TestMethod]
    public async Task Set_DnsServer_to_packetCapture()
    {
        // Create Server
        await using var server = TestHelper.CreateServer();
        var token = TestHelper.CreateAccessToken(server);

        // create app
        using var packetCapture = TestHelper.CreatePacketCapture(new TestDeviceOptions { IsDnsServerSupported = true });
        Assert.IsTrue(packetCapture.DnsServers == null || packetCapture.DnsServers.Length == 0);

        await using var client = TestHelper.CreateClient(token, packetCapture);
        await TestHelper.WaitForClientStateAsync(client, ClientState.Connected);

        Assert.IsTrue(packetCapture.DnsServers is { Length: > 0 });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task IpFilters(bool usePassthru, bool isDnsServerSupported)
    {
        var testDns =
            !isDnsServerSupported; //dns will work as normal UDP when DnsServerSupported, otherwise it should be redirected

        // Create Server
        await using var server = TestHelper.CreateServer();
        var token = TestHelper.CreateAccessToken(server);

        // create app
        var deviceOptions = new TestDeviceOptions
        {
            CanSendPacketToOutbound = usePassthru,
            IsDnsServerSupported = isDnsServerSupported,
            CaptureDnsAddresses = TestHelper.TestIpAddresses.ToArray()
        };

        await using var app = TestHelper.CreateClientApp(deviceOptions: deviceOptions);
        var clientProfile = app.ClientProfileStore.AddAccessKey(token.ToAccessKey());
        var ipList = (await Dns.GetHostAddressesAsync(TestHelper.TEST_HttpsUri1.Host))
            .Select(x => new IpRange(x))
            .Concat(new[]
            {
                new IpRange(TestHelper.TEST_PingV4Address1),
                new IpRange(TestHelper.TEST_NsEndPoint1.Address),
                new IpRange(TestHelper.TEST_UdpV4EndPoint1.Address),
                new IpRange(TestHelper.TEST_UdpV6EndPoint1.Address),
            });

        // ************
        // *** TEST ***: Test Include ip filter
        app.UserSettings.CustomIpRanges = ipList.ToArray();
        app.UserSettings.IpGroupFilters = new[] { "custom" };
        app.UserSettings.IpGroupFiltersMode = FilterMode.Include;
        await app.Connect(clientProfile.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);
        await TestHelper.Test_Ping(ipAddress: TestHelper.TEST_PingV4Address1);

        await IpFilters_TestInclude(app, testPing: usePassthru, testUdp: true, testDns: testDns);
        await app.Disconnect();

        // ************
        // *** TEST ***: Test Exclude ip filters
        app.UserSettings.IpGroupFiltersMode = FilterMode.Exclude;
        await app.Connect(clientProfile.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);

        await IpFilters_TestExclude(app, testPing: usePassthru, testUdp: true, testDns: testDns);
    }

    public static async Task IpFilters_TestInclude(VpnHoodApp app, bool testUdp, bool testPing, bool testDns)
    {
        // TCP
        var oldReceivedByteCount = app.State.SessionTraffic.Received;
        await TestHelper.Test_Https(uri: TestHelper.TEST_HttpsUri1);
        Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

        // TCP
        oldReceivedByteCount = app.State.SessionTraffic.Received;
        await TestHelper.Test_Https(uri: TestHelper.TEST_HttpsUri2);
        Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

        if (testPing)
        {
            // ping
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            await TestHelper.Test_Ping(ipAddress: TestHelper.TEST_PingV4Address1);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            // ping
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            try
            {
                await TestHelper.Test_Ping(ipAddress: TestHelper.TEST_PingV4Address2, timeout: 1000);
                Assert.Fail("Exception expected as server should not exists.");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(nameof(PingException), ex.GetType().Name);
            }

            Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }

        if (testUdp)
        {
            // UDP
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            await TestHelper.Test_Udp(TestHelper.TEST_UdpV4EndPoint1);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            // UDP
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            try
            {
                await TestHelper.Test_Udp(TestHelper.TEST_UdpV4EndPoint2, timeout: 1000);
                Assert.Fail("Exception expected as server should not exists.");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(nameof(OperationCanceledException), ex.GetType().Name);
            }

            Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }

        // DNS should always use tunnel regarding of any exclude or include option
        if (testDns)
        {
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            TestHelper.Test_Dns(nsEndPoint: TestHelper.TEST_NsEndPoint1);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            oldReceivedByteCount = app.State.SessionTraffic.Received;
            TestHelper.Test_Dns(nsEndPoint: TestHelper.TEST_NsEndPoint2);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }
    }

    public static async Task IpFilters_TestExclude(VpnHoodApp app, bool testUdp, bool testPing, bool testDns)
    {
        // TCP
        var oldReceivedByteCount = app.State.SessionTraffic.Received;
        await TestHelper.Test_Https(uri: TestHelper.TEST_HttpsUri1);
        Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

        // TCP
        oldReceivedByteCount = app.State.SessionTraffic.Received;
        await TestHelper.Test_Https(uri: TestHelper.TEST_HttpsUri2);
        Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

        if (testPing)
        {
            // ping
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            try
            {
                await TestHelper.Test_Ping(ipAddress: TestHelper.TEST_PingV4Address1, timeout: 1000);
                Assert.Fail("Exception expected as server should not exists.");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(nameof(PingException), ex.GetType().Name);
            }
            Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            // ping
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            await TestHelper.Test_Ping(ipAddress: TestHelper.TEST_PingV4Address2);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }

        if (testUdp)
        {
            // UDP
            VhLogger.Instance.LogTrace("Testing UDP include...");
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            try
            {
                await TestHelper.Test_Udp(udpEndPoint: TestHelper.TEST_UdpV4EndPoint1, timeout: 1000);
                Assert.Fail("Exception expected as server should not exists.");
            }
            catch (Exception ex)
            {
                Assert.AreEqual(nameof(OperationCanceledException), ex.GetType().Name);
            }
            Assert.AreEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            // UDP
            VhLogger.Instance.LogTrace("Testing UDP exclude...");
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            await TestHelper.Test_Udp(TestHelper.TEST_UdpV4EndPoint2);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }

        // DNS should always use tunnel regarding of any exclude or include option
        if (testDns)
        {
            oldReceivedByteCount = app.State.SessionTraffic.Received;
            TestHelper.Test_Dns(nsEndPoint: TestHelper.TEST_NsEndPoint1);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);

            oldReceivedByteCount = app.State.SessionTraffic.Received;
            TestHelper.Test_Dns(nsEndPoint: TestHelper.TEST_NsEndPoint2);
            Assert.AreNotEqual(oldReceivedByteCount, app.State.SessionTraffic.Received);
        }
    }

    [TestMethod]
    public async Task State_Connected_Disconnected_successfully()
    {
        // create server
        await using var server = TestHelper.CreateServer();
        var token = TestHelper.CreateAccessToken(server);

        // create app
        await using var app = TestHelper.CreateClientApp();
        var clientProfile = app.ClientProfileStore.AddAccessKey(token.ToAccessKey());

        _ = app.Connect(clientProfile.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);

        // get data through tunnel
        await TestHelper.Test_Https();

        Assert.IsFalse(app.State.LogExists);
        Assert.IsFalse(app.State.HasDiagnoseStarted);
        Assert.IsFalse(app.State.HasProblemDetected);
        Assert.IsNull(app.State.LastError);
        Assert.IsFalse(app.State.IsIdle);

        // test disconnect
        await app.Disconnect();
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.None);
    }

    [TestMethod]
    public async Task Get_token_from_tokenLink()
    {
        // create server
        using var fileAccessManager = TestHelper.CreateFileAccessManager();
        using var testAccessManager = new TestAccessManager(fileAccessManager);
        await using var server = TestHelper.CreateServer(testAccessManager);

        var token1 = TestHelper.CreateAccessToken(server);
        var token2 = TestHelper.CreateAccessToken(server);

        //create web server and set token url to it
        var endPoint = VhUtil.GetFreeTcpEndPoint(IPAddress.Loopback);
        using var webServer = new WebServer(endPoint.Port);
        token1.Url = $"http://{endPoint}/accesskey";

        // update token1 in web server
        var isTokenRetrieved = false;
        webServer.WithAction("/accesskey", HttpVerbs.Get, context =>
        {
            isTokenRetrieved = true;
            return context.SendStringAsync(token2.ToAccessKey(), "text/json", Encoding.UTF8);
        });
        webServer.Start();

        // connect
        await using var app = TestHelper.CreateClientApp();
        var clientProfile = app.ClientProfileStore.AddAccessKey(token1.ToAccessKey());
        app.ClientProfileStore.UpdateTokenFromUrl(token1).Wait();
        _ = app.Connect(clientProfile.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);
        Assert.AreEqual(AppConnectionState.Connected, app.State.ConnectionState);
        Assert.IsTrue(isTokenRetrieved);
    }

    [TestMethod]
    public async Task Change_server_while_connected()
    {
        await using var server1 = TestHelper.CreateServer();
        await using var server2 = TestHelper.CreateServer();

        var token1 = TestHelper.CreateAccessToken(server1);
        var token2 = TestHelper.CreateAccessToken(server2);

        // connect
        await using var app = TestHelper.CreateClientApp();
        var clientProfile1 = app.ClientProfileStore.AddAccessKey(token1.ToAccessKey());
        var clientProfile2 = app.ClientProfileStore.AddAccessKey(token2.ToAccessKey());

        await app.Connect(clientProfile1.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);

        await app.Connect(clientProfile2.ClientProfileId);
        await TestHelper.WaitForClientStateAsync(app, AppConnectionState.Connected);

        Assert.AreEqual(AppConnectionState.Connected, app.State.ConnectionState,
            "Client connection has not been changed!");
    }
}