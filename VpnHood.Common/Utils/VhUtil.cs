using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VpnHood.Common.Utils;

public static class VhUtil
{
    public static bool IsConnectionRefusedException(Exception ex)
    {
        return
            ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ||
            ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
    }

    public static bool IsSocketClosedException(Exception ex)
    {
        return ex is ObjectDisposedException or IOException or SocketException;
    }

    public static IPEndPoint GetFreeTcpEndPoint(IPAddress ipAddress, int defaultPort = 0)
    {
        try
        {
            // check recommended port
            var listener = new TcpListener(ipAddress, defaultPort);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return new IPEndPoint(ipAddress, port);
        }
        catch when (defaultPort != 0)
        {
            return GetFreeTcpEndPoint(ipAddress);
        }
    }

    public static IPEndPoint GetFreeUdpEndPoint(IPAddress ipAddress, int defaultPort = 0)
    {
        try
        {
            // check recommended port
            using var udpClient = new UdpClient(new IPEndPoint(ipAddress, defaultPort));
            var port = ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
            return new IPEndPoint(ipAddress, port);
        }
        catch when (defaultPort != 0)
        {
            return GetFreeUdpEndPoint(ipAddress);
        }
    }

    public static void DirectoryCopy(string sourcePath, string destinationPath, bool recursive)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(sourcePath);

        if (!dir.Exists)
            throw new DirectoryNotFoundException(
                "Source directory does not exist or could not be found: "
                + sourcePath);

        var dirs = dir.GetDirectories();

        // If the destination directory doesn't exist, create it.       
        Directory.CreateDirectory(destinationPath);

        // Get the files in the directory and copy them to the new location.
        var files = dir.GetFiles();
        foreach (var file in files)
        {
            var tempPath = Path.Combine(destinationPath, file.Name);
            file.CopyTo(tempPath, false);
        }

        // If copying subdirectories, copy them and their contents to new location.
        if (recursive)
            foreach (var item in dirs)
            {
                var tempPath = Path.Combine(destinationPath, item.Name);
                DirectoryCopy(item.FullName, tempPath, recursive);
            }
    }

    public static T[] SafeToArray<T>(object lockObject, IEnumerable<T> collection)
    {
        lock (lockObject)
            return collection.ToArray();
    }

    public static async Task<T> RunTask<T>(Task<T> task, TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        await RunTask((Task)task, timeout, cancellationToken);
        return await task;
    }

    public static async Task RunTask(Task task, TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        if (timeout == TimeSpan.Zero)
            timeout = TimeSpan.FromMilliseconds(-1);

        var timeoutTask = Task.Delay(timeout, cancellationToken);
        await Task.WhenAny(task, timeoutTask);

        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutTask.IsCompleted)
            throw new TimeoutException();

        await task;
    }

    public static bool IsNullOrEmpty<T>([NotNullWhen(false)] T[]? array)
    {
        return array == null || array.Length == 0;
    }

    public static IEnumerable<string> ParseArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            yield break;

        var sb = new StringBuilder();
        var inQuote = false;
        foreach (var c in commandLine)
        {
            if (c == '"' && !inQuote)
            {
                inQuote = true;
                continue;
            }

            if (c != '"' && !(char.IsWhiteSpace(c) && !inQuote))
            {
                sb.Append(c);
                continue;
            }

            if (sb.Length > 0)
            {
                var result = sb.ToString();
                sb.Clear();
                inQuote = false;
                yield return result;
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    public static byte[] GenerateKey()
    {
        return GenerateKey(128);
    }

    public static byte[] GenerateKey(int keySizeInBit)
    {
        using var aes = Aes.Create();
        aes.KeySize = keySizeInBit;
        aes.GenerateKey();
        return aes.Key;
    }

    public static T JsonDeserialize<T>(string json, JsonSerializerOptions? options = null)
    {
        return JsonSerializer.Deserialize<T>(json, options) ??
               throw new InvalidDataException($"{typeof(T)} could not be deserialized!");
    }

    public static T JsonClone<T>(object obj, JsonSerializerOptions? options = null)
    {
        var json = JsonSerializer.Serialize(obj, options);
        return JsonDeserialize<T>(json, options);
    }

    public static byte[] EncryptClientId(Guid clientId, byte[] key)
    {
        // Validate request by shared secret
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Key = key;
        aes.IV = new byte[key.Length];
        aes.Padding = PaddingMode.None;

        using var cryptor = aes.CreateEncryptor();
        return cryptor.TransformFinalBlock(clientId.ToByteArray(), 0, clientId.ToByteArray().Length);
    }

    public static string GetStringMd5(string value)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToString(hash).Replace("-", "");
    }

    public static string RedactIpAddress(IPAddress ipAddress)
    {
        var addressBytes = ipAddress.GetAddressBytes();

        if (ipAddress.AddressFamily == AddressFamily.InterNetwork &&
            !ipAddress.Equals(IPAddress.Any) &&
            !ipAddress.Equals(IPAddress.Loopback))
            return $"{addressBytes[0]}.*.*.{addressBytes[3]}";

        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
            !ipAddress.Equals(IPAddress.IPv6Any) &&
            !ipAddress.Equals(IPAddress.IPv6Loopback))
            return $"{addressBytes[0]:x2}{addressBytes[1]:x2}:***:{addressBytes[14]:x2}{addressBytes[15]:x2}";

        return ipAddress.ToString();
    }

    public static string FormatBytes(long size, bool use1024 = true)
    {
        var kb = use1024 ? (long)1024 : 1000;
        var mb = kb * kb;
        var gb = mb * kb;
        var tb = gb * kb;

        if (size >= tb) // Terabyte
            return (size / tb).ToString("0.## ") + "TB";

        if (size >= gb) // Gigabyte
            return (size / gb).ToString("0.# ") + "GB";

        if (size >= mb) // Megabyte
            return (size / mb).ToString("0 ") + "MB";

        if (size >= kb) // Kilobyte
            return (size / kb).ToString("0 ") + "KB";

        if (size > 0) // Kilobyte
            return size.ToString("0 ") + "B";

        // Byte
        return size.ToString("0");
    }

    [SuppressMessage("ReSharper", "PossibleLossOfFraction")]
    public static string FormatBits(long bytes)
    {
        bytes *= 8; //convertTo bit

        // Get absolute value
        if (bytes >= 0x40000000) // Gigabyte
            return ((double)(bytes / 0x40000000)).ToString("0.# ") + "Gbps";

        if (bytes >= 0x100000) // Megabyte
            return ((double)(bytes / 0x100000)).ToString("0 ") + "Mbps";

        if (bytes >= 1024) // Kilobyte
            return ((double)(bytes / 1024)).ToString("0 ") + "Kbps";

        if (bytes > 0) // Kilobyte
            return ((double)bytes).ToString("0 ") + "bps";

        // Byte
        return bytes.ToString("0");
    }

    public static bool IsInfinite(TimeSpan timeSpan)
    {
        return timeSpan == TimeSpan.MaxValue || timeSpan == Timeout.InfiniteTimeSpan;
    }

    public static ValueTask DisposeAsync(IAsyncDisposable? channel)
    {
        return channel?.DisposeAsync() ?? default;
    }

    public static void ConfigTcpClient(TcpClient tcpClient, int? sendBufferSize, int? receiveBufferSize, bool? reuseAddress = null)
    {
        tcpClient.NoDelay = true;
        if (sendBufferSize != null) tcpClient.SendBufferSize = sendBufferSize.Value;
        if (receiveBufferSize != null) tcpClient.ReceiveBufferSize = receiveBufferSize.Value;
        if (reuseAddress != null) tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, reuseAddress.Value);
    }

    public static bool IsTcpClientHealthy(TcpClient tcpClient)
    {
        try
        {
            // Check if the TcpClient is connected
            if (!tcpClient.Connected)
                return false;

            // Check if the underlying socket is connected
            var socket = tcpClient.Client;
            var healthy = tcpClient.Connected && socket.Connected && !tcpClient.Client.Poll(1, SelectMode.SelectError);

            return healthy;
        }
        catch (Exception)
        {
            // An error occurred while checking the TcpClient
            return false;
        }
    }
}
