using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Vela.Registry.Server;

internal sealed class RegistryAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    RegistrySettings settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RegistryApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (settings.KeyHash is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? suppliedKey = null;
        foreach (var header in new[] { "X-Api-Key", "X-Vela-ApiKey", "Authorization" })
        {
            if (!Request.Headers.TryGetValue(header, out var values))
            {
                continue;
            }

            if (suppliedKey is not null || values.Count != 1 || string.IsNullOrEmpty(values[0]))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid publisher credentials."));
            }

            var value = values[0]!;
            if (header == "Authorization")
            {
                if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(AuthenticateResult.Fail("Invalid publisher credentials."));
                }

                value = value[7..];
            }

            suppliedKey = value;
        }

        if (suppliedKey is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if ((!Request.IsHttps && !settings.AllowInsecureHttp) || suppliedKey.Length is < 32 or > 512)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid publisher credentials or transport."));
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, settings.KeyHash))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid publisher credentials."));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "configured-publisher"), new Claim("registry:publish", "all")], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
