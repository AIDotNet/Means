using System.Net;
using System.Xml.Linq;

namespace Means;

/// <summary>
/// S3-style XML error returned by Means.
/// </summary>
public sealed class MeansError : Exception
{
    public MeansError(
        string code,
        string message,
        HttpStatusCode statusCode,
        string? requestId = null,
        string? resource = null,
        string? hostId = null,
        string? responseBody = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        RequestId = requestId;
        Resource = resource;
        HostId = hostId;
        ResponseBody = responseBody;
    }

    public string Code { get; }

    public HttpStatusCode StatusCode { get; }

    public string? RequestId { get; }

    public string? Resource { get; }

    public string? HostId { get; }

    public string? ResponseBody { get; }

    public override string ToString()
    {
        return $"{Code}: {Message} (HTTP {(int)StatusCode})";
    }

    internal static async Task<MeansError> FromResponseAsync(HttpResponseMessage response)
    {
        var body = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        var requestId = HeaderValue(response, "x-amz-request-id");
        var hostId = HeaderValue(response, "x-amz-id-2");
        var code = response.StatusCode.ToString();
        var message = response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
        string? resource = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var document = XDocument.Parse(body);
                var root = document.Root;
                if (root is not null && string.Equals(root.Name.LocalName, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    code = ElementValue(root, "Code") ?? code;
                    message = ElementValue(root, "Message") ?? message;
                    requestId = ElementValue(root, "RequestId") ?? requestId;
                    hostId = ElementValue(root, "HostId") ?? hostId;
                    resource = ElementValue(root, "Resource");
                }
            }
            catch (Exception) when (!System.Diagnostics.Debugger.IsAttached)
            {
                message = body;
            }
        }

        return new MeansError(code, message, response.StatusCode, requestId, resource, hostId, body);
    }

    private static string? ElementValue(XElement root, string name)
    {
        return root.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;
    }
}
