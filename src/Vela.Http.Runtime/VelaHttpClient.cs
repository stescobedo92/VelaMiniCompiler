using System.Net.Http.Headers;
using System.Text;

namespace Vela.Http;

/// <summary>HTTP response with status code and body.</summary>
public sealed record VelaHttpResponse(int StatusCode, string Body);

/// <summary>TLS-capable HTTP client for structured GET/POST requests.</summary>
public static class VelaHttpClient
{
    /// <summary>Performs an HTTP GET and returns the response body.</summary>
    public static string Get(string url, int timeoutMs = 30000) =>
        GetWithHeaders(url, timeoutMs).Body;

    /// <summary>Performs an HTTP POST and returns the response body.</summary>
    public static string Post(string url, string body, string contentType = "application/json", int timeoutMs = 30000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ValidateHttpUrl(url);

        using var client = CreateClient(timeoutMs);
        using var content = new StringContent(body, Encoding.UTF8, contentType);
        using var response = client.PostAsync(url, content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>Performs an HTTP GET and returns the status code and body.</summary>
    public static VelaHttpResponse GetWithHeaders(string url, int timeoutMs = 30000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ValidateHttpUrl(url);

        using var client = CreateClient(timeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return new VelaHttpResponse((int)response.StatusCode, body);
    }

    /// <summary>Validates that <paramref name="url"/> uses http or https.</summary>
    public static void ValidateHttpUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"URL '{url}' is not a valid absolute URI.", nameof(url));
        }

        if (uri.Scheme is not "http" and not "https")
        {
            throw new ArgumentException($"URL scheme '{uri.Scheme}' is not supported. Only http and https are allowed.", nameof(url));
        }
    }

    private static HttpClient CreateClient(int timeoutMs)
    {
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be positive.");
        }

        return new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
    }
}
