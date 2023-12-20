using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using VpnHood.Common;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Utils;
using VpnHood.Server.Access.Configurations;
using VpnHood.Server.Access.Messaging;

namespace VpnHood.Server.Access.Managers.File;

public class FileAccessManager : IAccessManager
{
    private const string FileExtToken = ".token";
    private const string FileExtUsage = ".usage";
    private readonly string _sslCertificatesPassword;
    public ServerConfig ServerConfig { get; }
    public string StoragePath { get; }
    public FileAccessManagerSessionController SessionController { get; }
    public string CertsFolderPath => Path.Combine(StoragePath, "certificates");
    public X509Certificate2 DefaultCert { get; }
    public ServerStatus? ServerStatus { get; private set; }
    public ServerInfo? ServerInfo { get; private set; }
    public bool IsMaintenanceMode => false; //this server never goes into maintenance mode

    public FileAccessManager(string storagePath, FileAccessManagerOptions options)
    {
        using var scope = VhLogger.Instance.BeginScope("FileAccessManager");

        StoragePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        ServerConfig = options;
        _sslCertificatesPassword = options.SslCertificatesPassword ?? "";
        SessionController = new FileAccessManagerSessionController();
        Directory.CreateDirectory(StoragePath);

        var defaultCertFile = Path.Combine(CertsFolderPath, "default.pfx");
        DefaultCert = System.IO.File.Exists(defaultCertFile)
            ? new X509Certificate2(defaultCertFile, _sslCertificatesPassword, X509KeyStorageFlags.Exportable)
            : CreateSelfSignedCertificate(defaultCertFile, _sslCertificatesPassword);

        // get or create server secret
        ServerConfig.ServerSecret ??= LoadServerSecret();
    }

    public byte[] LoadServerSecret()
    {
        var serverSecretFile = Path.Combine(CertsFolderPath, "secret");
        if (!System.IO.File.Exists(serverSecretFile))
            System.IO.File.WriteAllText(serverSecretFile, Convert.ToBase64String(VhUtil.GenerateKey(128)));

        return Convert.FromBase64String(System.IO.File.ReadAllText(serverSecretFile));
    }

    public Task<ServerCommand> Server_UpdateStatus(ServerStatus serverStatus)
    {
        ServerStatus = serverStatus;
        return Task.FromResult(new ServerCommand(ServerConfig.ConfigCode));
    }

    public Task<ServerConfig> Server_Configure(ServerInfo serverInfo)
    {
        ServerInfo = serverInfo;
        ServerStatus = serverInfo.Status;

        // update UdpEndPoints if they are not configured 
        var udpEndPoints = ServerConfig.UdpEndPointsValue.ToArray();
        foreach (var udpEndPoint in udpEndPoints.Where(x => x.Port == 0))
        {
            udpEndPoint.Port = udpEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                ? serverInfo.FreeUdpPortV6 : serverInfo.FreeUdpPortV4;
        }
        ServerConfig.UdpEndPoints = udpEndPoints.Where(x => x.Port != 0).ToArray();

        return Task.FromResult(ServerConfig);
    }

    public Task<byte[]> GetSslCertificateData(IPEndPoint hostEndPoint)
    {
        var cert = GetSslCertificate(hostEndPoint, true).Export(X509ContentType.Pfx);
        return Task.FromResult(cert);
    }

    public async Task<SessionResponseEx> Session_Create(SessionRequestEx sessionRequestEx)
    {
        var accessItem = await AccessItem_Read(sessionRequestEx.TokenId);
        if (accessItem == null)
            return new SessionResponseEx(SessionErrorCode.AccessError) { ErrorMessage = "Token does not exist." };

        var ret = SessionController.CreateSession(sessionRequestEx, accessItem);

        // set endpoints
        ret.TcpEndPoints = new[] { sessionRequestEx.HostEndPoint };
        ret.UdpEndPoints = ServerConfig.UdpEndPointsValue
            .Where(x => x.AddressFamily == sessionRequestEx.HostEndPoint.AddressFamily)
            .Select(x => new IPEndPoint(sessionRequestEx.HostEndPoint.Address, x.Port))
            .ToArray();

        return ret;
    }

    public async Task<SessionResponseEx> Session_Get(ulong sessionId, IPEndPoint hostEndPoint, IPAddress? clientIp)
    {
        _ = hostEndPoint;
        _ = clientIp;

        // find token
        var tokenId = SessionController.TokenIdFromSessionId(sessionId);
        if (tokenId == null)
            return new SessionResponseEx(SessionErrorCode.AccessError)
            {
                SessionId = sessionId,
                ErrorMessage = "Session does not exist."
            };

        // read accessItem
        var accessItem = await AccessItem_Read(tokenId.Value);
        if (accessItem == null)
            return new SessionResponseEx(SessionErrorCode.AccessError)
            {
                SessionId = sessionId,
                ErrorMessage = "Token does not exist."
            };

        // read usage
        return SessionController.GetSession(sessionId, accessItem, hostEndPoint);
    }

    public Task<SessionResponseBase> Session_AddUsage(ulong sessionId, Traffic traffic)
    {
        return Session_AddUsage(sessionId, traffic, false);
    }

    public Task<SessionResponseBase> Session_Close(ulong sessionId, Traffic traffic)
    {
        return Session_AddUsage(sessionId, traffic, true);
    }

    private async Task<SessionResponseBase> Session_AddUsage(ulong sessionId, Traffic traffic, bool closeSession)
    {
        // find token
        var tokenId = SessionController.TokenIdFromSessionId(sessionId);
        if (tokenId == null)
            return new SessionResponseBase(SessionErrorCode.AccessError) { ErrorMessage = "Token does not exist." };

        // read accessItem
        var accessItem = await AccessItem_Read(tokenId.Value);
        if (accessItem == null)
            return new SessionResponseBase(SessionErrorCode.AccessError) { ErrorMessage = "Token does not exist." };

        accessItem.AccessUsage.Traffic += traffic;
        await WriteAccessItemUsage(accessItem);

        if (closeSession)
            SessionController.CloseSession(sessionId);

        var res = SessionController.GetSession(sessionId, accessItem, null);
        var ret = new SessionResponseBase(res.ErrorCode)
        {
            AccessUsage = res.AccessUsage,
            ErrorMessage = res.ErrorMessage,
            SuppressedBy = res.SuppressedBy
        };

        return ret;
    }

    public void Dispose()
    {
        SessionController.Dispose();
    }

    private string GetAccessItemFileName(Guid tokenId)
    {
        return Path.Combine(StoragePath, tokenId + FileExtToken);
    }

    private string GetUsageFileName(Guid tokenId)
    {
        return Path.Combine(StoragePath, tokenId + FileExtUsage);
    }

    public string GetCertFilePath(IPEndPoint ipEndPoint)
    {
        return Path.Combine(CertsFolderPath, ipEndPoint.ToString().Replace(":", "-") + ".pfx");
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string certFilePath, string password)
    {
        VhLogger.Instance.LogInformation($"Creating Certificate file: {certFilePath}");
        var certificate = CertificateUtil.CreateSelfSigned();
        var buf = certificate.Export(X509ContentType.Pfx, password);
        Directory.CreateDirectory(Path.GetDirectoryName(certFilePath)!);
        System.IO.File.WriteAllBytes(certFilePath, buf);
        return new X509Certificate2(certFilePath, password, X509KeyStorageFlags.Exportable);
    }

    public AccessItem[] AccessItem_LoadAll()
    {
        var files = Directory.GetFiles(StoragePath, "*" + FileExtToken);
        return files.Select(x => AccessItem_Read(Guid.Parse(Path.GetFileNameWithoutExtension(x))).Result!)
            .ToArray();
    }

    public AccessItem AccessItem_Create(IPEndPoint[] publicEndPoints,
        int maxClientCount = 1,
        string? tokenName = null,
        int maxTrafficByteCount = 0,
        DateTime? expirationTime = null,
        bool isValidHostName = false,
        int hostPort = 443)
    {
        // generate key
        var aes = Aes.Create();
        aes.KeySize = 128;
        aes.GenerateKey();

        // create AccessItem
        var accessItem = new AccessItem
        {
            MaxTraffic = maxTrafficByteCount,
            MaxClientCount = maxClientCount,
            ExpirationTime = expirationTime,
            Token = new Token(aes.Key,
                DefaultCert.GetCertHash(),
                DefaultCert.GetNameInfo(X509NameType.DnsName, false) ??
                throw new Exception("Certificate must have a subject!"))
            {
                Name = tokenName,
                HostPort = hostPort,
                HostEndPoints = publicEndPoints,
                TokenId = Guid.NewGuid(),
                SupportId = 0,
                IsValidHostName = isValidHostName
            }
        };

        var token = accessItem.Token;

        // Write accessItem
        System.IO.File.WriteAllText(GetAccessItemFileName(token.TokenId), JsonSerializer.Serialize(accessItem));

        // build default usage
        ReadAccessItemUsage(accessItem).Wait();
        WriteAccessItemUsage(accessItem).Wait();

        return accessItem;
    }

    public async Task AccessItem_Delete(Guid tokenId)
    {
        // remove index
        _ = await AccessItem_Read(tokenId)
            ?? throw new KeyNotFoundException("Could not find tokenId");

        // delete files
        if (System.IO.File.Exists(GetUsageFileName(tokenId)))
            System.IO.File.Delete(GetUsageFileName(tokenId));
        if (System.IO.File.Exists(GetAccessItemFileName(tokenId)))
            System.IO.File.Delete(GetAccessItemFileName(tokenId));
    }

    public async Task<AccessItem?> AccessItem_Read(Guid tokenId)
    {
        // read access item
        var fileName = GetAccessItemFileName(tokenId);
        using var fileLock = await AsyncLock.LockAsync(fileName);
        if (!System.IO.File.Exists(fileName))
            return null;

        var json = await System.IO.File.ReadAllTextAsync(fileName);
        var accessItem = VhUtil.JsonDeserialize<AccessItem>(json);
        await ReadAccessItemUsage(accessItem);
        return accessItem;
    }

    private async Task ReadAccessItemUsage(AccessItem accessItem)
    {
        // read usageItem
        accessItem.AccessUsage = new AccessUsage
        {
            ExpirationTime = accessItem.ExpirationTime,
            MaxClientCount = accessItem.MaxClientCount,
            MaxTraffic = accessItem.MaxTraffic,
            ActiveClientCount = 0
        };

        // update usage
        try
        {
            var fileName = GetUsageFileName(accessItem.Token.TokenId);
            using var fileLock = await AsyncLock.LockAsync(fileName);
            if (System.IO.File.Exists(fileName))
            {
                var json = await System.IO.File.ReadAllTextAsync(fileName);
                var accessItemUsage = JsonSerializer.Deserialize<AccessItemUsage>(json) ?? new AccessItemUsage();
                accessItem.AccessUsage.Traffic = new Traffic { Sent = accessItemUsage.SentTraffic, Received = accessItemUsage.ReceivedTraffic };
            }
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(
                $"Error in reading AccessUsage of token: {accessItem.Token.TokenId}, Message: {ex.Message}");
        }
    }

    private async Task WriteAccessItemUsage(AccessItem accessItem)
    {
        // write token info
        var accessItemUsage = new AccessItemUsage
        {
            ReceivedTraffic = accessItem.AccessUsage.Traffic.Received,
            SentTraffic = accessItem.AccessUsage.Traffic.Sent
        };
        var json = JsonSerializer.Serialize(accessItemUsage);

        // write accessItem
        var fileName = GetUsageFileName(accessItem.Token.TokenId);
        using var fileLock = await AsyncLock.LockAsync(fileName);
        await System.IO.File.WriteAllTextAsync(fileName, json);
    }

    private X509Certificate2 GetSslCertificate(IPEndPoint hostEndPoint, bool returnDefaultIfNotFound)
    {
        var certFilePath = GetCertFilePath(hostEndPoint);
        if (returnDefaultIfNotFound && !System.IO.File.Exists(certFilePath))
            return DefaultCert;
        return new X509Certificate2(certFilePath, _sslCertificatesPassword, X509KeyStorageFlags.Exportable);
    }

    public class AccessItem
    {
        public DateTime? ExpirationTime { get; set; }
        public int MaxClientCount { get; set; }
        public long MaxTraffic { get; set; }
        public Token Token { get; set; } = null!;

        [JsonIgnore] public AccessUsage AccessUsage { get; set; } = new();
    }

    private class AccessItemUsage
    {
        public long SentTraffic { get; init; }
        public long ReceivedTraffic { get; init; }
    }
}