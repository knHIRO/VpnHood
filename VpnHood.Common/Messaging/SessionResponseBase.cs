using System.Net;
using System.Text.Json.Serialization;
using VpnHood.Common.Converters;

namespace VpnHood.Common.Messaging;

public class SessionResponseBase
{
    [JsonConstructor]
    public SessionResponseBase(SessionErrorCode errorCode)
    {
        ErrorCode = errorCode;
    }

    public SessionResponseBase(SessionResponseBase obj)
    {
        ErrorCode = obj.ErrorCode;
        ErrorMessage = obj.ErrorMessage;
        AccessUsage = obj.AccessUsage;
        SuppressedBy = obj.SuppressedBy;
        RedirectHostEndPoint = obj.RedirectHostEndPoint;
    }

    public SessionErrorCode ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public AccessUsage? AccessUsage { get; set; }
    public SessionSuppressType SuppressedBy { get; set; }

    [JsonConverter(typeof(IPEndPointConverter))]
    public IPEndPoint? RedirectHostEndPoint { get; set; }
}