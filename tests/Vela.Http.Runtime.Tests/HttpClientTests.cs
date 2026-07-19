using Vela.Http;
using Xunit;

namespace Vela.Http.Runtime.Tests;

public sealed class HttpClientTests
{
    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    public void ValidateHttpUrl_RejectsNonHttpSchemes(string url)
    {
        var exception = Assert.Throws<ArgumentException>(() => VelaHttpClient.ValidateHttpUrl(url));
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://localhost:8443/api")]
    public void ValidateHttpUrl_AcceptsHttpAndHttps(string url)
    {
        VelaHttpClient.ValidateHttpUrl(url);
    }

    [Fact]
    public void Get_RejectsInvalidScheme()
    {
        Assert.Throws<ArgumentException>(() => VelaHttpClient.Get("ftp://example.com"));
    }

    [Fact(Skip = "Requires network access to httpbin.org")]
    public void Get_HttpsIntegration()
    {
        var body = VelaHttpClient.Get("https://httpbin.org/get", timeoutMs: 15000);
        Assert.Contains("origin", body, StringComparison.Ordinal);
    }
}
