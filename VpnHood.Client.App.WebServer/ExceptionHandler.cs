using System.Net;
using System.Net.Mime;
using EmbedIO;
using VpnHood.Common.Client;
using VpnHood.Common.Exceptions;

namespace VpnHood.Client.App.WebServer;

internal static class ExceptionHandler
{
    public static Task DataResponseForException(IHttpContext context, Exception ex)
    {
        // set correct https status code depends on exception
        if (NotExistsException.Is(ex)) context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        else if (AlreadyExistsException.Is(ex)) context.Response.StatusCode = (int)HttpStatusCode.Conflict;
        else if (ex is UnauthorizedAccessException) context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        else context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.ContentType = MediaTypeNames.Application.Json;


        // create portable exception
        var apiError = new ApiError(ex);
        throw new HttpException(HttpStatusCode.BadRequest, apiError.Message, apiError);
    }

    public static Task DataResponseForHttpException(IHttpContext context, IHttpException httpException)
    {
        if (httpException.DataObject is ApiError)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return ResponseSerializer.Json(context, httpException.DataObject);
        }

        return context.SendStandardHtmlAsync(context.Response.StatusCode);
    }
}