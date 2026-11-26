using System.Net;

namespace LiaDesktop.Services;

/// <summary>Gemini REST failure with status + optional Retry-After seconds.</summary>
public sealed class GeminiHttpException : HttpRequestException
{
    public int Status { get; }
    public int? RetryAfterSeconds { get; }
    public string ResponseBody { get; }

    public GeminiHttpException(string message, int status, int? retryAfterSeconds, string body)
        : base(message, null, (HttpStatusCode)status)
    {
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
        ResponseBody = body;
    }
}
