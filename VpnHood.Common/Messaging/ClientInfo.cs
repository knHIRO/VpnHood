namespace VpnHood.Common.Messaging;

public class ClientInfo
{
    public Guid ClientId { get; set; }
    // ReSharper disable once UnusedMember.Global
    public string? UserToken { get; set; }
    public string ClientVersion { get; set; } = null!;
    public int ProtocolVersion { get; set; }
    public string UserAgent { get; set; } = null!;

    public override string ToString()
    {
        return $"{nameof(ClientId)}={ClientId}";
    }
}