using Microsoft.Extensions.Logging;
using PacketDotNet;
using VpnHood.Common.JobController;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Utils;
using VpnHood.Tunneling.Channels;
using VpnHood.Tunneling.DatagramMessaging;

namespace VpnHood.Tunneling;

public class Tunnel : IJob, IAsyncDisposable
{
    private readonly object _channelListLock = new();
    private const int MaxQueueLength = 100;
    private const int MtuNoFragment = TunnelDefaults.MtuWithoutFragmentation;
    private const int MtuWithFragment = TunnelDefaults.MtuWithFragmentation;
    private readonly Queue<IPPacket> _packetQueue = new();
    private readonly SemaphoreSlim _packetSentEvent = new(0);
    private readonly SemaphoreSlim _packetSenderSemaphore = new(0);
    private readonly HashSet<StreamProxyChannel> _streamProxyChannels = [];
    private readonly List<IDatagramChannel> _datagramChannels = [];
    private readonly Timer _speedMonitorTimer;
    private bool _disposed;
    private int _maxDatagramChannelCount;
    private Traffic _lastTraffic = new();
    private readonly Traffic _trafficUsage = new();
    private readonly TimeSpan _datagramPacketTimeout = TimeSpan.FromSeconds(100);
    private DateTime _lastSpeedUpdateTime = FastDateTime.Now;
    private readonly TimeSpan _speedTestThreshold = TimeSpan.FromSeconds(2);
    public event EventHandler<ChannelPacketReceivedEventArgs>? OnPacketReceived;
    public Traffic Speed { get; } = new();
    public DateTime LastActivityTime { get; private set; } = FastDateTime.Now;
    public JobSection JobSection { get; } = new();

    public Tunnel(TunnelOptions? options = null)
    {
        options ??= new TunnelOptions();
        _maxDatagramChannelCount = options.MaxDatagramChannelCount;
        _speedMonitorTimer = new Timer(_ => UpdateSpeed(), null, TimeSpan.Zero, _speedTestThreshold);
        JobRunner.Default.Add(this);
    }

    public int StreamProxyChannelCount
    {
        get
        {
            lock (_channelListLock)
                return _streamProxyChannels.Count;
        }
    }

    public int DatagramChannelCount
    {
        get
        {
            lock (_channelListLock)
                return _datagramChannels.Count;
        }
    }

    public bool IsUdpMode => UdpChannel != null;
    public UdpChannel? UdpChannel
    {
        get
        {
            lock (_channelListLock)
                return (UdpChannel?)_datagramChannels.FirstOrDefault(x => x is UdpChannel);
        }
    }

    public Traffic Traffic
    {
        get
        {
            lock (_channelListLock)
            {
                return new Traffic
                {
                    Sent = _trafficUsage.Sent + _streamProxyChannels.Sum(x => x.Traffic.Sent) + _datagramChannels.Sum(x => x.Traffic.Sent),
                    Received = _trafficUsage.Received + _streamProxyChannels.Sum(x => x.Traffic.Received) + _datagramChannels.Sum(x => x.Traffic.Received)
                };
            }
        }
    }

    public int MaxDatagramChannelCount
    {
        get => _maxDatagramChannelCount;
        set
        {
            if (_maxDatagramChannelCount < 1)
                throw new ArgumentException("Value must equals or greater than 1", nameof(MaxDatagramChannelCount));
            _maxDatagramChannelCount = value;
        }
    }

    private void UpdateSpeed()
    {
        if (_disposed)
            return;

        if (FastDateTime.Now - _lastSpeedUpdateTime < _speedTestThreshold)
            return;

        var traffic = Traffic;
        var trafficChanged = _lastTraffic != traffic;
        var duration = (FastDateTime.Now - _lastSpeedUpdateTime).TotalSeconds;

        Speed.Sent = (long)((traffic.Sent - _lastTraffic.Sent) / duration);
        Speed.Received = (long)((traffic.Received - _lastTraffic.Received) / duration);
        _lastSpeedUpdateTime = FastDateTime.Now;
        _lastTraffic = traffic.Clone();
        if (trafficChanged)
            LastActivityTime = FastDateTime.Now;
    }

    private bool IsChannelExists(IChannel channel)
    {
        lock (_channelListLock)
        {
            return channel is IDatagramChannel
                ? _datagramChannels.Contains(channel)
                : _streamProxyChannels.Contains(channel);
        }
    }

    public void AddChannel(IDatagramChannel datagramChannel)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Tunnel));

        //should not be called in lock; its behaviour is unexpected
        datagramChannel.OnPacketReceived += Channel_OnPacketReceived;
        datagramChannel.Start();

        // add to channel list
        lock (_channelListLock)
        {
            if (_datagramChannels.Contains(datagramChannel))
                throw new Exception("the DatagramChannel already exists in the collection.");

            _datagramChannels.Add(datagramChannel);
            VhLogger.Instance.LogInformation(GeneralEventId.DatagramChannel,
                "A DatagramChannel has been added. ChannelId: {ChannelId}, ChannelCount: {ChannelCount}, ChannelType: {ChannelType}",
                datagramChannel.ChannelId, _datagramChannels.Count, datagramChannel.GetType().Name);

            // remove additional Datagram channels
            while (_datagramChannels.Count > MaxDatagramChannelCount)
            {
                VhLogger.Instance.LogInformation(GeneralEventId.DatagramChannel,
                    "Removing an exceeded DatagramChannel. ChannelId: {ChannelId}, ChannelCount: {ChannelCount}",
                    datagramChannel.ChannelId, _datagramChannels.Count);

                RemoveChannel(_datagramChannels[0]);
            }

            // UdpChannels and StreamChannels can not be added together
            foreach (var channel in _datagramChannels.Where(x => x.IsStream != datagramChannel.IsStream).ToArray())
                RemoveChannel(channel);
        }

        //  SendPacketTask after starting the channel and must be outside the lock
        _ = SendPacketTask(datagramChannel);
    }

    public void AddChannel(StreamProxyChannel channel)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Tunnel));

        // should not be called in lock; its behaviour is unexpected
        channel.Start();

        // add channel
        lock (_channelListLock)
            if (!_streamProxyChannels.Add(channel))
                throw new Exception($"Could not add {channel.GetType()}. ChannelId: {channel.ChannelId}");

        VhLogger.Instance.LogInformation(GeneralEventId.StreamProxyChannel,
            "A StreamProxyChannel has been added. ChannelId: {ChannelId}, ChannelCount: {ChannelCount}",
            channel.ChannelId, StreamProxyChannelCount);
    }

    private void RemoveChannel(IChannel channel)
    {
        if (!IsChannelExists(channel))
            return; // channel already removed or does not exist

        lock (_channelListLock)
        {
            if (channel is IDatagramChannel datagramChannel)
            {
                _datagramChannels.Remove(datagramChannel);
                VhLogger.Instance.LogInformation(GeneralEventId.DatagramChannel,
                    "A DatagramChannel has been removed. Channel: {Channel}, ChannelId: {ChannelId}, " +
                    "ChannelCount: {ChannelCount}, Connected: {Connected}",
                    VhLogger.FormatType(channel), channel.ChannelId, _datagramChannels.Count, channel.Connected);
            }
            else if (channel is StreamProxyChannel streamProxyChannel)
            {
                _streamProxyChannels.Remove(streamProxyChannel);
                VhLogger.Instance.LogInformation(GeneralEventId.StreamProxyChannel,
                    "A StreamProxyChannel has been removed. Channel: {Channel}, ChannelId: {ChannelId}, " +
                    "ChannelCount: {ChannelCount}, Connected: {Connected}",
                    VhLogger.FormatType(channel), channel.ChannelId, _streamProxyChannels.Count, channel.Connected);
            }
            else
                throw new ArgumentOutOfRangeException(nameof(channel), "Unknown Channel.");
        }

        // clean up channel
        _trafficUsage.Add(channel.Traffic);
        channel.DisposeAsync();
    }

    private void Channel_OnPacketReceived(object sender, ChannelPacketReceivedEventArgs e)
    {
        if (_disposed)
            return;

        if (VhLogger.IsDiagnoseMode)
            PacketUtil.LogPackets(e.IpPackets, $"Packets received from a channel. ChannelId: {e.Channel.ChannelId}");

        // check datagram message
        // performance critical; don't create another array by linq
        if (e.IpPackets.Any(DatagramMessageHandler.IsDatagramMessage))
            e = new ChannelPacketReceivedEventArgs(
                e.IpPackets.Where(x => !DatagramMessageHandler.IsDatagramMessage(x)).ToArray(), e.Channel);

        try
        {
            OnPacketReceived?.Invoke(sender, e);
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogError(GeneralEventId.DatagramChannel, ex, "Packets dropped! Error in processing channel received packets.");
        }
    }

    public Task SendPacket(IPPacket ipPacket)
    {
        return SendPackets(new[] { ipPacket });
    }

    public async Task SendPackets(IEnumerable<IPPacket> ipPackets)
    {
        var dateTime = FastDateTime.Now;
        if (_disposed) throw new ObjectDisposedException(nameof(Tunnel));

        // waiting for a space in the packetQueue; the Inconsistently is not important. synchronization may lead to deadlock
        // ReSharper disable once InconsistentlySynchronizedField
        while (_packetQueue.Count > MaxQueueLength)
        {
            var releaseCount = DatagramChannelCount - _packetSenderSemaphore.CurrentCount;
            if (releaseCount > 0)
                _packetSenderSemaphore.Release(releaseCount); // there is some packet
            await _packetSentEvent.WaitAsync(1000); //Wait 1000 to prevent deadlock.
            if (_disposed) return;

            // check timeout
            if (FastDateTime.Now - dateTime > _datagramPacketTimeout)
                throw new TimeoutException("Could not send datagram packets.");
        }

        // add all packets to the queue
        lock (_packetQueue)
        {
            // ReSharper disable once PossibleMultipleEnumeration
            foreach (var ipPacket in ipPackets)
                _packetQueue.Enqueue(ipPacket);

            var releaseCount = DatagramChannelCount - _packetSenderSemaphore.CurrentCount;
            if (releaseCount > 0)
                _packetSenderSemaphore.Release(releaseCount); // there are some packets! 
        }

        // ReSharper disable once PossibleMultipleEnumeration
        if (VhLogger.IsDiagnoseMode)
            PacketUtil.LogPackets(ipPackets, "Packet sent to tunnel queue.");
    }

    private async Task SendPacketTask(IDatagramChannel channel)
    {
        var packets = new List<IPPacket>();

        // ** Warning: This is one of the most busy loop in the app. Performance is critical!
        try
        {
            // ReSharper disable once MergeIntoPattern
            while (channel.Connected && !_disposed)
            {
                if (_disposed)
                    return;

                //only one thread can dequeue packets to let send buffer with sequential packets
                // dequeue available packets and add them to list in favor of buffer size
                lock (_packetQueue)
                {
                    var size = 0;
                    packets.Clear();
                    while (_packetQueue.TryPeek(out var ipPacket))
                    {
                        if (ipPacket == null) throw new Exception("Null packet should not be in the queue.");
                        var packetSize = ipPacket.TotalPacketLength;

                        // drop packet if it is larger than _mtuWithFragment
                        if (packetSize > MtuWithFragment)
                        {
                            VhLogger.Instance.LogWarning($"Packet dropped! There is no channel to support this fragmented packet. Fragmented MTU: {MtuWithFragment}, PacketLength: {ipPacket.TotalLength}, Packet: {PacketUtil.Format(ipPacket)}");
                            _packetQueue.TryDequeue(out ipPacket);
                            continue;
                        }

                        // drop packet if it is larger than _mtuNoFragment
                        if (packetSize > MtuNoFragment && ipPacket is IPv4Packet { FragmentFlags: 2 })
                        {
                            VhLogger.Instance.LogWarning($"Packet dropped! There is no channel to support this non fragmented packet. NoFragmented MTU: {MtuNoFragment}, Packet: {PacketUtil.Format(ipPacket)}");
                            _packetQueue.TryDequeue(out ipPacket);
                            var replyPacket = PacketUtil.CreatePacketTooBigReply(ipPacket, MtuNoFragment);
                            OnPacketReceived?.Invoke(this, new ChannelPacketReceivedEventArgs(new[] { replyPacket }, channel));
                            continue;
                        }

                        // just send this packet if it is bigger than _mtuNoFragment and there is no more packet in the buffer
                        // packets should be empty to decrease the chance of missing the other packets by this packet
                        if (packetSize > MtuNoFragment && !packets.Any() && _packetQueue.TryDequeue(out ipPacket))
                        {
                            packets.Add(ipPacket);
                            break;
                        }

                        // send other packets if this packet makes the buffer too big
                        if (packetSize + size > MtuNoFragment)
                            break;

                        size += packetSize;
                        if (_packetQueue.TryDequeue(out ipPacket))
                            packets.Add(ipPacket);
                    }
                }

                // send selected packets
                if (packets.Count > 0)
                {
                    _packetSenderSemaphore.Release(); // lets the other do the rest (if any)
                    _packetSentEvent.Release();

                    try
                    {
                        await channel.SendPacket(packets.ToArray());
                    }
                    catch
                    {
                        if (!_disposed)
                            _ = SendPackets(packets); //resend packets

                        if (!channel.Connected && !_disposed)
                            throw; // this channel has error
                    }
                }
                // wait for next new packets
                else
                {
                    await _packetSenderSemaphore.WaitAsync();
                }
            } // while
        }
        catch (Exception ex)
        {
            VhLogger.LogError(GeneralEventId.DatagramChannel, ex,
                "Could not send some packets via a channel. ChannelId: {ChannelId}, PacketCount: {PacketCount}",
                channel.ChannelId, packets.Count);
        }

        // make sure to remove the channel
        try
        {
            RemoveChannel(channel);
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogError(ex, "Could not remove a datagram channel. ChannelId: {ChannelId}", channel.ChannelId);
        }

        // lets the others do the rest of the job (if any)
        _packetSenderSemaphore.Release();
        _packetSentEvent.Release();
    }

    public Task RunJob()
    {
        // remove disconnected channels
        lock (_channelListLock)
        {
            foreach (var channel in _streamProxyChannels.Where(x => !x.Connected).ToArray())
                RemoveChannel(channel);

            foreach (var channel in _datagramChannels.Where(x => !x.Connected).ToArray())
                RemoveChannel(channel);
        }

        return Task.CompletedTask;
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

        // make sure to call RemoveChannel to perform proper clean up such as setting _sentByteCount and _receivedByteCount 
        var disposeTasks = new List<Task>();
        lock (_channelListLock)
        {
            disposeTasks.AddRange(_streamProxyChannels.Select(channel => channel.DisposeAsync(false).AsTask()));
            disposeTasks.AddRange(_datagramChannels.Select(channel => channel.DisposeAsync(false).AsTask()));
        }

        // Stop speed monitor
        await _speedMonitorTimer.DisposeAsync();
        Speed.Sent = 0;
        Speed.Received = 0;

        // release worker threads (make sure to release all semaphores)
        _packetSenderSemaphore.Release(MaxDatagramChannelCount * 10);
        _packetSentEvent.Release();

        // dispose all channels
        await Task.WhenAll(disposeTasks);
    }
}