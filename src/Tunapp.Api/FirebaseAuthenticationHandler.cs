using System.Security.Claims;
using System.Text.Encodings.Web;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Tunapp.Api;

public sealed class FirebaseAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, FirebaseAuth auth) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Se requiere un token de Firebase.");
        try
        {
            var token = await auth.VerifyIdTokenAsync(header[7..].Trim(), true, Context.RequestAborted);
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, token.Uid) };
            if (token.Claims.TryGetValue("email", out var email) && email is string address)
                claims.Add(new(ClaimTypes.Email, address));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (Exception ex) when (ex is FirebaseAuthException or ArgumentException)
        {
            // Nunca registrar el token, la contraseña ni el contenido de las credenciales.
            return AuthenticateResult.Fail("Sesión inválida o caducada.");
        }
    }
}
