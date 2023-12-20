using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using VpnHood.Common.Logging;
using VpnHood.Common.Utils;

namespace VpnHood.Client.Diagnosing;

public class DiagnoseUtil
{
    public static Task<Exception?> CheckHttps(Uri[] uris, int timeout)
    {
        var tasks = uris.Select(x => CheckHttps(x, timeout));
        return WhenAnySuccess(tasks.ToArray());
    }

    public static Task<Exception?> CheckUdp(IPEndPoint[] nsIpEndPoints, int timeout)
    {
        var tasks = nsIpEndPoints.Select(x => CheckUdp(x, timeout));
        return WhenAnySuccess(tasks.ToArray());
    }

    public static Task<Exception?> CheckPing(IPAddress[] ipAddresses, int timeout, bool anonymize = false)
    {
        var tasks = ipAddresses.Select(x => CheckPing(x, timeout, anonymize));
        return WhenAnySuccess(tasks.ToArray());
    }

    private static async Task<Exception?> WhenAnySuccess(Task<Exception?>[] tasks)
    {
        Exception? exception = null;
        while (tasks.Length > 0)
        {
            var task = await Task.WhenAny(tasks);
            exception = task.Result;
            if (task.Result == null)
                return null; //at least one task is success
            tasks = tasks.Where(x => x != task).ToArray();
        }

        return exception;
    }

    public static async Task<Exception?> CheckHttps(Uri uri, int timeout)
    {
        try
        {
            VhLogger.Instance.LogInformation(
                "HttpTest: {HttpTestStatus}, Url: {url}, Timeout: {timeout}...", 
                "Started", uri, timeout);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMilliseconds(timeout);
            var result = await httpClient.GetStringAsync(uri);
            if (result.Length < 100)
                throw new Exception("The http response data length is not expected!");

            VhLogger.Instance.LogInformation(
                "HttpTest: {HttpTestStatus}, Url: {url}.", 
                "Succeeded", uri);

            return null;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(
                "HttpTest: {HttpTestStatus}!, Url: {url}. Message: {ex.Message}",
                "Failed", uri, ex.Message);
            return ex;
        }
    }

    public static async Task<Exception?> CheckUdp(IPEndPoint nsIpEndPoint, int timeout)
    {
        using var udpClient = new UdpClient();
        const string dnsName = "www.google.com";
        try
        {
            VhLogger.Instance.LogInformation(
                "UdpTest: {UdpTestStatus}, DnsName: {DnsName}, NsServer: {NsServer}, Timeout: {Timeout}...",
                "Started", dnsName, nsIpEndPoint, timeout);

            var res = await GetHostEntry(dnsName, nsIpEndPoint, udpClient, timeout);
            if (res.AddressList.Length == 0)
                throw new Exception("Could not find any host!");

            VhLogger.Instance.LogInformation(
                "UdpTest: {UdpTestStatus}, DnsName: {DnsName}, NsServer: {NsServer}.",
                "Succeeded", dnsName, nsIpEndPoint);

            return null;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(ex,
                "UdpTest: {UdpTestStatus}!, DnsName: {DnsName}, NsServer: {NsServer}, Message: {Message}.",
                "Failed", dnsName, nsIpEndPoint, ex.Message);

            return ex;
        }
    }

    public static async Task<Exception?> CheckPing(IPAddress ipAddress, int timeout, bool anonymize = false)
    {
        var logIpAddress = anonymize ? VhLogger.Format(ipAddress) : ipAddress.ToString();

        try
        {
            using var ping = new Ping();
            VhLogger.Instance.LogInformation(
                "PingTest: {PingTestStatus}, RemoteAddress: {RemoteAddress}, Timeout: {Timeout}...",
                "Started", logIpAddress, timeout);

            var pingReply = await ping.SendPingAsync(ipAddress, timeout);
            if (pingReply.Status != IPStatus.Success)
                throw new Exception($"Status: {pingReply.Status}");

            VhLogger.Instance.LogInformation(
                "PingTest: {PingTestStatus}, RemoteAddress: {RemoteAddress}.",
                "Succeeded", logIpAddress);
            return null;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(ex,
                "PingTest: {PingTestStatus}!, RemoteAddress: {RemoteAddress}. Message: {Message}",
                "Failed", logIpAddress, ex.Message);
            return ex;
        }
    }

    public static async Task<IPHostEntry> GetHostEntry(string host, IPEndPoint dnsEndPoint,
        UdpClient? udpClient = null, int timeout = 5000)
    {
        // prepare  udpClient
        using var udpClientTemp = new UdpClient();
        udpClient ??= udpClientTemp;

        await using var ms = new MemoryStream();
        var rnd = new Random();
        //About the dns message:http://www.ietf.org/rfc/rfc1035.txt

        //Write message header.
        ms.Write(new byte[]
        {
            (byte) rnd.Next(0, 0xFF), (byte) rnd.Next(0, 0xFF),
            0x01,
            0x00,
            0x00, 0x01,
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00
        }, 0, 12);

        //Write the host to query.
        foreach (var block in host.Split('.'))
        {
            var data = Encoding.UTF8.GetBytes(block);
            ms.WriteByte((byte)data.Length);
            ms.Write(data, 0, data.Length);
        }

        ms.WriteByte(0); //The end of query, must be 0 (null string)

        //Query type:A
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);

        //Query class:IN
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);

        //send to dns server
        var buffer = ms.ToArray();
        udpClient.Client.SendTimeout = timeout;
        udpClient.Client.ReceiveTimeout = timeout;
        await udpClient.SendAsync(buffer, buffer.Length, dnsEndPoint);
        var receiveTask = await VhUtil.RunTask(udpClient.ReceiveAsync(), TimeSpan.FromMilliseconds(timeout));
        buffer = receiveTask.Buffer;

        //The response message has the same header and question structure, so we move index to the answer part directly.
        var index = (int)ms.Length;

        //Parse response records.
        void SkipName()
        {
            while (index < buffer.Length)
            {
                int length = buffer[index++];
                if (length == 0)
                    return;
                if (length > 191) return;
                index += length;
            }
        }

        var addresses = new List<IPAddress>();
        while (index < buffer.Length)
        {
            SkipName(); //Seems the name of record is useless in this case, so we just need to get the next index after name.
            var type = buffer[index += 2];
            index += 7; //Skip class and ttl

            var length = (buffer[index++] << 8) | buffer[index++]; //Get record data length

            if (type == 0x01) //A record
                if (length == 4) //Parse record data to ip v4, this is what we need.
                    addresses.Add(new IPAddress(new[]
                        {buffer[index], buffer[index + 1], buffer[index + 2], buffer[index + 3]}));
            index += length;
        }

        return new IPHostEntry { AddressList = addresses.ToArray() };
    }
}