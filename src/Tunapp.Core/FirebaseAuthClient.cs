using System.Net.Http.Json;
using System.Text.Json;

namespace Tunapp.Core;

// Clase (no record) para que ToString no revele tokens al registrar un objeto por accidente.
public sealed class FirebaseSession
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string IdToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public bool EmailVerified { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class FirebaseAuthClient(HttpClient http, string apiKey, string? emulatorHost = null)
{
    private readonly string identityUrl = Endpoint("identitytoolkit.googleapis.com", emulatorHost);
    private readonly string tokenUrl = Endpoint("securetoken.googleapis.com", emulatorHost);
    private static string Endpoint(string host, string? emulator)
    {
        if (emulator is null) return $"https://{host}/v1/";
        if (!Uri.TryCreate(emulator, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new ArgumentException("El emulador de autenticación debe ser local.");
        return $"{emulator.TrimEnd('/')}/{host}/v1/";
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    public FirebaseSession? Session { get; private set; }
    public event Func<FirebaseSession?, Task>? SessionChanged;
    public async Task RestoreAsync(FirebaseSession session, bool refresh = true)
    {
        if (string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.RefreshToken))
            throw new InvalidOperationException("Sesión guardada incompleta. Vuelve a iniciar sesión.");
        Session = session;
        try { if (refresh) await GetTokenAsync(); }
        catch { Session = null; throw; }
    }
    public Task SignInAsync(string email, string password) => AuthenticateAsync("signInWithPassword", email, password);
    public Task SignUpAsync(string email, string password) => AuthenticateAsync("signUp", email, password);
    private async Task AuthenticateAsync(string action, string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Indica correo y contraseña.");
        using var response = await http.PostAsJsonAsync($"{identityUrl}accounts:{action}?key={Uri.EscapeDataString(apiKey)}",
            new { email = email.Trim(), password, returnSecureToken = true });
        using var json = await ParseAsync(response);
        var root = json.RootElement;
        var session = new FirebaseSession
        {
            UserId = root.GetProperty("localId").GetString()!, Email = root.GetProperty("email").GetString()!,
            IdToken = root.GetProperty("idToken").GetString()!, RefreshToken = root.GetProperty("refreshToken").GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(int.Parse(root.GetProperty("expiresIn").GetString()!))
        };
        Session = session;
        if (SessionChanged is { } changed) await changed(session);
    }
    public async Task ResetPasswordAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Indica tu correo electrónico.");
        using var response = await http.PostAsJsonAsync($"{identityUrl}accounts:sendOobCode?key={Uri.EscapeDataString(apiKey)}",
            new { requestType = "PASSWORD_RESET", email = email.Trim() });
        using var json = await ParseAsync(response);
    }
    public async Task SendVerificationEmailAsync()
    {
        using var response = await http.PostAsJsonAsync($"{identityUrl}accounts:sendOobCode?key={Uri.EscapeDataString(apiKey)}",
            new { requestType = "VERIFY_EMAIL", idToken = await GetTokenAsync() });
        using var json = await ParseAsync(response);
    }
    public async Task RefreshProfileAsync()
    {
        using var response = await http.PostAsJsonAsync($"{identityUrl}accounts:lookup?key={Uri.EscapeDataString(apiKey)}", new { idToken = await GetTokenAsync() });
        using var json = await ParseAsync(response);
        var user = json.RootElement.GetProperty("users")[0];
        var session = Session ?? throw new UnauthorizedAccessException("Inicia sesión.");
        if (user.GetProperty("localId").GetString() != session.UserId) throw new UnauthorizedAccessException("La identidad ha cambiado.");
        session.EmailVerified = user.TryGetProperty("emailVerified", out var verified) && verified.GetBoolean();
        session.Email = user.GetProperty("email").GetString()!;
        await GetTokenAsync(forceRefresh: true);
    }
    public async Task<string> GetTokenAsync(bool forceRefresh = false)
    {
        await gate.WaitAsync();
        try
        {
            var session = Session ?? throw new UnauthorizedAccessException("Inicia sesión.");
            if (!forceRefresh && session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return session.IdToken;
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken });
            using var response = await http.PostAsync($"{tokenUrl}token?key={Uri.EscapeDataString(apiKey)}", content);
            JsonDocument json;
            try { json = await ParseAsync(response); }
            catch (UnauthorizedAccessException) { await SignOutAsync(); throw; }
            using (json)
            {
                var root = json.RootElement;
                var uid = root.GetProperty("user_id").GetString()!;
                if (uid != session.UserId) { await SignOutAsync(); throw new UnauthorizedAccessException("La identidad de la sesión ha cambiado."); }
                session.IdToken = root.GetProperty("id_token").GetString()!;
                session.RefreshToken = root.GetProperty("refresh_token").GetString()!;
                session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(int.Parse(root.GetProperty("expires_in").GetString()!));
                if (SessionChanged is { } changed) await changed(session);
                return session.IdToken;
            }
        }
        finally { gate.Release(); }
    }
    public async Task SignOutAsync()
    {
        Session = null;
        if (SessionChanged is { } changed) await changed(null);
    }
    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response)
    {
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (response.IsSuccessStatusCode) return json;
        var error = json.RootElement.TryGetProperty("error", out var item) && item.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
        json.Dispose();
        throw new UnauthorizedAccessException(error.Split(' ', ':')[0] switch
        {
            "EMAIL_EXISTS" => "Este correo ya tiene una cuenta. Inicia sesión.",
            "INVALID_EMAIL" => "El correo no es válido.",
            "WEAK_PASSWORD" => "La contraseña no cumple los requisitos de Firebase.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Demasiados intentos. Espera y vuelve a probar.",
            "OPERATION_NOT_ALLOWED" or "PASSWORD_LOGIN_DISABLED" => "Activa el acceso con correo y contraseña en Firebase.",
            "USER_DISABLED" => "La cuenta está deshabilitada.",
            _ => "No se pudo validar la cuenta. Revisa el correo y la contraseña o inicia sesión de nuevo."
        });
    }
}
