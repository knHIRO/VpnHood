using VpnHood.Common.Messaging;

namespace VpnHood.Client.App;

public class AppState
{
    public required DateTime ConfigTime { get; init; }
    public required DateTime? ConnectRequestTime { get; init; }
    public required AppConnectionState ConnectionState { get; init; }
    public required string? LastError { get; init; }
    public required Guid? ActiveClientProfileId { get; init; }
    public required bool IsIdle { get; init; }
    public required bool LogExists { get; init; }
    public required Guid? LastActiveClientProfileId { get; init; }
    public required bool HasDiagnoseStarted { get; init; }
    public required bool HasDisconnectedByUser { get; init; }
    public required bool HasProblemDetected { get; init; }
    public required SessionStatus? SessionStatus { get; init; }
    public required Traffic Speed { get; init; }
    public required Traffic SessionTraffic { get; init; }
    public required Traffic AccountTraffic { get; init; } 
    public required IpGroup? ClientIpGroup { get; init; }
    public required bool IsWaitingForAd { get; init; }
    public required VersionStatus VersionStatus { get; init; }
    public required PublishInfo? LastPublishInfo { get; init; }
}