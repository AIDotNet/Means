namespace Means.Core;

/// <summary>
/// Domain exception carrying the S3-style error code and HTTP status expected by the protocol layer.
/// Infrastructure and application services throw this instead of depending on ASP.NET Core types.
/// </summary>
public sealed class MeansException : Exception
{
    public MeansException(string code, string message, int statusCode = 400)
        : this(code, message, statusCode, responseHeaders: null)
    {
    }

    public MeansException(string code, string message, int statusCode, IReadOnlyDictionary<string, string>? responseHeaders)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        ResponseHeaders = responseHeaders ?? new Dictionary<string, string>();
    }

    public string Code { get; }

    public int StatusCode { get; }

    /// <summary>
    /// Optional wire-level headers required by specific protocol errors.
    /// Domain code should use this sparingly; it exists for cases like HTTP 416 where
    /// Content-Range is part of the correct response contract.
    /// </summary>
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; }
}
