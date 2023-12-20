using System.Collections.Concurrent;
using System.Text.Json;
using Ga4.Ga4Tracking;
using Microsoft.Extensions.Logging;
using VpnHood.Common.JobController;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Net;
using VpnHood.Common.Utils;
using VpnHood.Server.Access.Configurations;
using VpnHood.Server.Access.Managers;
using VpnHood.Server.Access.Messaging;
using VpnHood.Server.Exceptions;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Factory;
using VpnHood.Tunneling.Messaging;
using VpnHood.Tunneling.Utils;

namespace VpnHood.Server;

public class SessionManager : IAsyncDisposable, IJob
{
    private readonly IAccessManager _accessManager;
    private readonly SocketFactory _socketFactory;
    private byte[] _serverSecret;
    private bool _disposed;

    public string ApiKey { get; private set; }
    public INetFilter NetFilter { get; }
    public JobSection JobSection { get; } = new(TimeSpan.FromMinutes(10));
    public Version ServerVersion { get; }
    public ConcurrentDictionary<ulong, Session> Sessions { get; } = new();
    public TrackingOptions TrackingOptions { get; set; } = new();
    public SessionOptions SessionOptions { get; set; } = new();
    public Ga4Tracker? GaTracker { get; }

    public byte[] ServerSecret
    {
        get => _serverSecret;
        set
        {
            ApiKey = HttpUtil.GetApiKey(value, TunnelDefaults.HttpPassCheck);
            _serverSecret = value;
        }
    }

    public SessionManager(IAccessManager accessManager,
        INetFilter netFilter,
        SocketFactory socketFactory,
        Ga4Tracker? gaTracker,
        Version serverVersion)
    {
        _accessManager = accessManager ?? throw new ArgumentNullException(nameof(accessManager));
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        GaTracker = gaTracker;
        _serverSecret = VhUtil.GenerateKey(128);
        ApiKey = HttpUtil.GetApiKey(_serverSecret, TunnelDefaults.HttpPassCheck);
        NetFilter = netFilter;
        ServerVersion = serverVersion;
        JobRunner.Default.Add(this);
    }

    public async Task SyncSessions()
    {
        // launch all syncs
        var syncTasks = Sessions.Values.Select(x => (x.SessionId, Task: x.Sync()));

        // wait for all
        foreach (var syncTask in syncTasks)
        {
            try
            {
                await syncTask.Task;
            }
            catch (Exception ex)
            {
                VhLogger.Instance.LogError(GeneralEventId.Session, ex,
                    "Error in syncing a session. SessionId: {SessionId}", syncTask.SessionId);
            }
        }
    }

    private async Task<Session> CreateSessionInternal(
        SessionResponseEx sessionResponseEx,
        IPEndPointPair ipEndPointPair,
        string requestId)
    {
        var extraData = sessionResponseEx.ExtraData != null
            ? VhUtil.JsonDeserialize<SessionExtraData>(sessionResponseEx.ExtraData)
            : new SessionExtraData { ProtocolVersion = 3 };

        var session = new Session(_accessManager, sessionResponseEx, NetFilter, _socketFactory, 
            SessionOptions, TrackingOptions, extraData);

        // add to sessions
        if (Sessions.TryAdd(session.SessionId, session))
            return session;

        session.SessionResponse.ErrorMessage = "Could not add session to collection.";
        session.SessionResponse.ErrorCode = SessionErrorCode.SessionError;
        await session.DisposeAsync();
        throw new ServerSessionException(ipEndPointPair.RemoteEndPoint, session,
            session.SessionResponse, requestId);

    }

    public async Task<SessionResponseEx> CreateSession(HelloRequest helloRequest, IPEndPointPair ipEndPointPair)
    {
        // validate the token
        VhLogger.Instance.Log(LogLevel.Trace, "Validating the request by the access server. TokenId: {TokenId}", VhLogger.FormatId(helloRequest.TokenId));
        var extraData = JsonSerializer.Serialize(new SessionExtraData { ProtocolVersion = helloRequest.ClientInfo.ProtocolVersion });
        var sessionResponseEx = await _accessManager.Session_Create(new SessionRequestEx(helloRequest, ipEndPointPair.LocalEndPoint)
        {
            HostEndPoint = ipEndPointPair.LocalEndPoint,
            ClientIp = ipEndPointPair.RemoteEndPoint.Address,
            ExtraData = extraData
        });
        sessionResponseEx.ExtraData = extraData; //extraData may not return by session creation

        // Access Error should not pass to the client in create session
        if (sessionResponseEx.ErrorCode is SessionErrorCode.AccessError)
            throw new ServerUnauthorizedAccessException(sessionResponseEx.ErrorMessage ?? "Access Error.", ipEndPointPair, helloRequest);

        if (sessionResponseEx.ErrorCode != SessionErrorCode.Ok)
            throw new ServerSessionException(ipEndPointPair.RemoteEndPoint, sessionResponseEx, helloRequest);

        // create the session and add it to list
        var session = await CreateSessionInternal(sessionResponseEx, ipEndPointPair, helloRequest.RequestId);

        // Anonymous Report to GA
        _ = GaTrackNewSession(helloRequest.ClientInfo);

        VhLogger.Instance.Log(LogLevel.Information, GeneralEventId.Session, $"New session has been created. SessionId: {VhLogger.FormatSessionId(session.SessionId)}");
        return sessionResponseEx;
    }

    private Task GaTrackNewSession(ClientInfo clientInfo)
    {
        if (GaTracker == null)
            return Task.CompletedTask;

        // track new session
        var serverVersion = ServerVersion.ToString(3);
        return GaTracker.Track(new Ga4TagEvent
        {
            EventName = Ga4TagEvents.PageView,
            Properties = new Dictionary<string, object>()
            {
                { "client_version", clientInfo.ClientVersion  },
                { "server_version", serverVersion  },
                { Ga4TagProperties.PageTitle , $"server_version/{serverVersion}"},
                { Ga4TagProperties.PageLocation, $"server_version/{serverVersion}"}
            }
        });
    }

    private async Task<Session> RecoverSession(RequestBase sessionRequest, IPEndPointPair ipEndPointPair)
    {
        using var recoverLock = await AsyncLock.LockAsync($"Recover_session_{sessionRequest.SessionId}");
        var session = GetSessionById(sessionRequest.SessionId);
        if (session != null)
            return session;

        // Get session from the access server
        VhLogger.Instance.LogTrace(GeneralEventId.Session,
            "Trying to recover a session from the access server. SessionId: {SessionId}",
            VhLogger.FormatSessionId(sessionRequest.SessionId));

        try
        {
            var sessionResponse = await _accessManager.Session_Get(sessionRequest.SessionId,
                ipEndPointPair.LocalEndPoint, ipEndPointPair.RemoteEndPoint.Address);

            // Check session key for recovery
            if (!sessionRequest.SessionKey.SequenceEqual(sessionResponse.SessionKey))
                throw new ServerUnauthorizedAccessException("Invalid SessionKey.", ipEndPointPair, sessionRequest.SessionId);

            // session is authorized, so we can pass any error to client
            if (sessionResponse.ErrorCode != SessionErrorCode.Ok)
                throw new ServerSessionException(ipEndPointPair.RemoteEndPoint, sessionResponse, sessionRequest);

            // create the session even if it contains error to prevent many calls
            session = await CreateSessionInternal(sessionResponse, ipEndPointPair, "recovery");
            VhLogger.Instance.LogInformation(GeneralEventId.Session, "Session has been recovered. SessionId: {SessionId}",
                VhLogger.FormatSessionId(sessionRequest.SessionId));

            return session;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogInformation(GeneralEventId.Session, "Could not recover a session. SessionId: {SessionId}",
                VhLogger.FormatSessionId(sessionRequest.SessionId));

            // Create a dead session if it is not created
            session = await CreateSessionInternal(new SessionResponseEx(SessionErrorCode.SessionError)
            {
                SessionId = sessionRequest.SessionId,
                SessionKey = sessionRequest.SessionKey,
                CreatedTime = DateTime.UtcNow,
                ErrorMessage = ex.Message,
            }, ipEndPointPair, "dead-recovery");
            await session.DisposeAsync();
            throw;
        }
    }

    internal async Task<Session> GetSession(RequestBase requestBase, IPEndPointPair ipEndPointPair)
    {
        //get session
        var session = GetSessionById(requestBase.SessionId);
        if (session != null)
        {
            if (!requestBase.SessionKey.SequenceEqual(session.SessionKey))
                throw new ServerUnauthorizedAccessException("Invalid session key.", ipEndPointPair, session);
        }
        // try to restore session if not found
        else
        {
            session = await RecoverSession(requestBase, ipEndPointPair);
        }

        if (session.SessionResponse.ErrorCode != SessionErrorCode.Ok)
            throw new ServerSessionException(ipEndPointPair.RemoteEndPoint, session, session.SessionResponse, requestBase.RequestId);

        // unexpected close
        if (session.IsDisposed)
            throw new ServerSessionException(ipEndPointPair.RemoteEndPoint, session,
                new SessionResponseBase(session.SessionResponse) { ErrorCode = SessionErrorCode.SessionClosed },
                requestBase.RequestId);

        return session;
    }

    public Task RunJob()
    {
        // anonymous heart_beat reporter
        _ = GaTracker?.Track(new Ga4TagEvent
        {
            EventName = "heartbeat",
            Properties = new Dictionary<string, object>()
            {
                { "session_count", Sessions.Count(x=>!x.Value.IsDisposed)  },
            }
        });

        // clean disposed sessions
        return Cleanup();
    }

    private readonly AsyncLock _cleanupLock = new();
    private async Task Cleanup()
    {
        using var cleaningLock = await _cleanupLock.LockAsync(TimeSpan.Zero);
        if (!cleaningLock.Succeeded)
            return;

        // find expired or dead sessions
        VhLogger.Instance.LogTrace("Cleaning up the expired sessions.");
        var minSessionActivityTime = FastDateTime.Now - SessionOptions.TimeoutValue;
        var timeoutSessions = Sessions
            .Where(x => x.Value.IsDisposed || x.Value.LastActivityTime < minSessionActivityTime)
            .ToArray();

        foreach (var session in timeoutSessions)
        {
            Sessions.Remove(session.Key, out _);
            await session.Value.DisposeAsync();
        }
    }

    public Session? GetSessionById(ulong sessionId)
    {
        Sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    ///     Close session in this server and AccessManager
    /// </summary>
    /// <param name="sessionId"></param>
    public async Task CloseSession(ulong sessionId)
    {
        // find in session
        if (Sessions.TryGetValue(sessionId, out var session))
            await session.Close();
    }

    private readonly AsyncLock _disposeLock = new();
    private ValueTask? _disposeTask;
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
            _disposeTask ??= DisposeAsyncCore();
        return _disposeTask.Value;
    }

    private async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;
        _disposed = true;

        await Task.WhenAll(Sessions.Values.Select(x => x.DisposeAsync().AsTask()));
    }
}