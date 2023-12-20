using System.Net;
using VpnHood.Common.Client;
using VpnHood.Common.Exceptions;

namespace VpnHood.Common.Utils;

public static class VhTestUtil
{
    public class AssertException : Exception
    {
        public AssertException(string? message = null)
            : base(message)
        {
        }

        public AssertException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public static async Task<bool> WaitForValue<TValue>(object? expectedValue, Func<TValue?> valueFactory, int timeout = 5000)
    {
        const int waitTime = 100;
        for (var elapsed = 0; elapsed < timeout; elapsed += waitTime)
        {
            if (Equals(expectedValue, valueFactory()))
                return true;

            await Task.Delay(waitTime);
        }

        return false;
    }

    public static async Task<bool> WaitForValue<TValue>(object? expectedValue, Func<Task<TValue?>> valueFactory, int timeout = 5000)
    {
        const int waitTime = 100;
        for (var elapsed = 0; elapsed < timeout; elapsed += waitTime)
        {
            if (Equals(expectedValue, await valueFactory()))
                return true;

            await Task.Delay(waitTime);
        }

        return false;
    }

    private static void AssertEquals(object? expected, object? actual, string? message)
    {
        message ??= "Unexpected Value";
        if (!Equals(expected, actual))
            throw new AssertException($"{message}. Expected: {expected}, Actual: {actual}");
    }

    public static async Task AssertEqualsWait<TValue>(object? expectedValue, Func<TValue?> valueFactory,
        string? message = null, int timeout = 5000)
    {
        await WaitForValue(expectedValue, valueFactory, timeout);
        AssertEquals(expectedValue, valueFactory(), message);
    }

    public static async Task AssertEqualsWait<TValue>(object? expectedValue, Func<Task<TValue?>> valueFactory,
        string? message = null, int timeout = 5000)
    {
        await WaitForValue(expectedValue, valueFactory, timeout);
        AssertEquals(expectedValue, await valueFactory(), message);
    }

    public static Task AssertApiException(HttpStatusCode expectedStatusCode, Task task, string? message = null)
    {
        return AssertApiException((int)expectedStatusCode, task, message);
    }

    private static void AssertExceptionContains(Exception ex, string? contains)
    {
        if (contains != null && !ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"Actual error message does not contain \"{contains}\".");
    }

    public static async Task AssertApiException(int expectedStatusCode, Task task,
        string? message = null, string? contains = null)
    {
        try
        {
            await task;
            throw new AssertException($"Expected {expectedStatusCode} but the actual was OK. {message}");
        }
        catch (ApiException ex)
        {
            if (ex.StatusCode != expectedStatusCode)
                throw new Exception($"Expected {expectedStatusCode} but the actual was {ex.StatusCode}. {message}");

            AssertExceptionContains(ex, contains);
        }

    }

    public static Task AssertApiException<T>(Task task, string? message = null, string? contains = null)
    {
        return AssertApiException(typeof(T).Name, task, message, contains);
    }

    public static async Task AssertApiException(string expectedExceptionType, Task task,
        string? message = null, string? contains = null)
    {
        try
        {
            await task;
            throw new AssertException($"Expected {expectedExceptionType} exception but was OK. {message}");
        }
        catch (ApiException ex)
        {
            if (ex.ExceptionTypeName != expectedExceptionType)
                throw new AssertException($"Expected {expectedExceptionType} but was {ex.ExceptionTypeName}. {message}");

            AssertExceptionContains(ex, contains);
        }
        catch (Exception ex) when (ex is not AssertException)
        {
            if (ex.GetType().Name != expectedExceptionType)
                throw new AssertException($"Expected {expectedExceptionType} but was {ex.GetType().Name}. {message}", ex);

            AssertExceptionContains(ex, contains);
        }
    }

    public static async Task AssertNotExistsException(Task task, string? message = null, string? contains = null)
    {
        try
        {
            await task;
            throw new AssertException($"Expected kind of {nameof(NotExistsException)} but was OK. {message}");
        }
        catch (ApiException ex)
        {
            if (ex.ExceptionTypeName != nameof(NotExistsException))
                throw new AssertException($"Expected {nameof(NotExistsException)} but was {ex.ExceptionTypeName}. {message}");

            AssertExceptionContains(ex, contains);
        }
        catch (Exception ex) when (ex is not AssertException)
        {
            if (!NotExistsException.Is(ex))
                throw new AssertException($"Expected kind of {nameof(NotExistsException)} but was {ex.GetType().Name}. {message}", ex);

            AssertExceptionContains(ex, contains);
        }
    }

    public static async Task AssertAlreadyExistsException(Task task, string? message = null, string? contains = null)
    {
        try
        {
            await task;
            throw new AssertException($"Expected kind of {nameof(AlreadyExistsException)} but was OK. {message}");
        }
        catch (ApiException ex)
        {
            if (ex.ExceptionTypeName != nameof(AlreadyExistsException))
                throw new AssertException($"Expected {nameof(AlreadyExistsException)} but was {ex.ExceptionTypeName}. {message}");

            AssertExceptionContains(ex, contains);
        }
        catch (Exception ex) when (ex is not AssertException)
        {
            if (!AlreadyExistsException.Is(ex))
                throw new AssertException($"Expected kind of {nameof(AlreadyExistsException)} but was {ex.GetType().Name}. {message}", ex);

            AssertExceptionContains(ex, contains);
        }
    }
}