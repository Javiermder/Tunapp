using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Tunapp.Core;

// Integración opcional contra Firebase REAL. Crea recursos efímeros y los elimina en finally.
// Ejecutar solo de forma explícita; nunca como parte de build ni del arranque de la app.
if (!args.Contains("--live")) throw new ArgumentException("Esta prueba requiere --live y credenciales del servidor.");
if (args.Contains("--direct")) { await DirectLiveChecks.RunAsync(); return; }
var key = Environment.GetEnvironmentVariable("TUNAPP_FIREBASE_API_KEY") ?? throw new InvalidOperationException("Falta TUNAPP_FIREBASE_API_KEY.");
const string project = "tunapp-j27";
var credential = GoogleCredential.GetApplicationDefault();
var app = FirebaseApp.Create(new AppOptions { Credential = credential, ProjectId = project }, "cloud-checks");
var admin = FirebaseAuth.GetAuth(app);
var db = new FirestoreDbBuilder { ProjectId = project, Credential = credential }.Build();
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5188"), Timeout = TimeSpan.FromSeconds(45) };
var run = Guid.NewGuid().ToString("N");
var users = new List<string>();
var businesses = new List<Guid>();
var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine($"OK: {name}"); checks++; }
async Task<(string Uid, string Email, string Token)> Account(string label)
{
    var uid = $"tunapp-check-{run}-{label}";
    var email = $"tunapp-check-{run}-{label}@example.invalid";
    await admin.CreateUserAsync(new UserRecordArgs { Uid = uid, Email = email, EmailVerified = true }); users.Add(uid);
    var customToken = await admin.CreateCustomTokenAsync(uid);
    using var response = await http.PostAsJsonAsync($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={key}", new { token = customToken, returnSecureToken = true });
    if (!response.IsSuccessStatusCode) throw new Exception("No se pudo obtener la sesión de prueba en Firebase.");
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return (uid, email, json.RootElement.GetProperty("idToken").GetString()!);
}
async Task<HttpResponseMessage> Request(HttpMethod method, string path, string? token, object? body = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (body is not null) request.Content = JsonContent.Create(body);
    return await http.SendAsync(request);
}
async Task Status(HttpMethod method, string path, string? token, object? body, HttpStatusCode expected, string name)
{
    using var response = await Request(method, path, token, body);
    if (response.StatusCode != expected)
    {
        // Los endpoints de datos devuelven solo un mensaje de error, nunca tokens.
        throw new Exception($"{name}: se esperaba {(int)expected}, recibido {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    Check(true, name);
}
async Task<T> Get<T>(string path, string token)
{
    using var response = await Request(HttpMethod.Get, path, token);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<T>())!;
}
async Task<Guid> Business(string token, string name)
{
    var id = Guid.NewGuid();
    if ((await db.Collection("businesses").Document(id.ToString("N")).GetSnapshotAsync()).Exists) throw new Exception("El identificador de prueba ya existe.");
    businesses.Add(id);
    await Status(HttpMethod.Post, "/api/businesses", token, new BusinessInput(id, $"Comprobación {run} {name}"), HttpStatusCode.NoContent, "Crear explotación en Firestore");
    return id;
}
try
{
    if (args.Contains("--auth-only"))
    {
        var email = $"tunapp-check-{run}-password@example.invalid";
        var password = $"Aa9!{Guid.NewGuid():N}";
        var client = new FirebaseAuthClient(http, key);
        await client.SignUpAsync(email, password);
        users.Add(client.Session!.UserId);
        var uid = client.Session.UserId;
        Check(client.Session.Email == email, "Registro con correo y contraseña en Firebase real");
        await client.SignOutAsync();
        Check(client.Session is null, "Cerrar sesión borra credenciales en memoria");
        await client.SignInAsync(email, password);
        Check(client.Session!.UserId == uid, "Iniciar sesión recupera la misma cuenta");
        client.Session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var refreshed = await client.GetTokenAsync();
        Check(client.Session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30), "Renovación real de sesión caducada");
        await Status(HttpMethod.Get, "/api/me", refreshed, null, HttpStatusCode.OK, "API valida la sesión real del cliente MAUI");
        await client.SignOutAsync();
        Console.WriteLine($"{checks} comprobaciones de acceso REAL correctas.");
        return;
    }
    await Status(HttpMethod.Get, "/api/businesses", null, null, HttpStatusCode.Unauthorized, "API exige autenticación");
    await Status(HttpMethod.Get, "/api/me", "invalid-test-token", null, HttpStatusCode.Unauthorized, "API rechaza tokens falsos");
    var owner = await Account("admin"); var reader = await Account("reader"); var outsider = await Account("outsider"); var worker = await Account("worker");
    Check((await Get<List<CloudBusiness>>("/api/businesses", owner.Token)).Count == 0, "Cuenta nueva sin datos ajenos");
    var first = await Business(owner.Token, "A"); var second = await Business(outsider.Token, "B");
    var firstPath = $"/api/businesses/{first}";
    var campaign = Guid.NewGuid(); var next = Guid.NewGuid(); var plot = Guid.NewGuid(); var extraPlot = Guid.NewGuid();
    var input = new CampaignInput(campaign, "2026/2027", new(2026, 10, 1), new(2027, 9, 30));
    await Status(HttpMethod.Post, firstPath + "/campaigns", owner.Token, input, HttpStatusCode.NoContent, "Crear campaña en la nube");
    await Status(HttpMethod.Post, firstPath + "/campaigns", owner.Token, input, HttpStatusCode.NoContent, "Repetir creación sin duplicar");
    await Status(HttpMethod.Post, firstPath + "/campaigns", owner.Token, input with { Id = Guid.NewGuid() }, HttpStatusCode.Conflict, "Nombre de campaña duplicado rechazado");
    await Status(HttpMethod.Post, firstPath + "/plots", owner.Token, new PlotInput(plot, "Finca de prueba", 2.5m, 250, "Picual", "Solo prueba efímera"), HttpStatusCode.NoContent, "Guardar finca permanente");
    var annual = new CampaignPlotInput(2.4m, 240, "Picual", "Datos anuales");
    await Status(HttpMethod.Post, $"{firstPath}/campaigns/{campaign}/plots/{plot}", owner.Token, annual, HttpStatusCode.NoContent, "Vincular finca a campaña");
    await Status(HttpMethod.Post, $"{firstPath}/campaigns/{campaign}/plots/{plot}", owner.Token, annual, HttpStatusCode.NoContent, "Repetir vínculo sin duplicar");
    await Status(HttpMethod.Post, firstPath + "/campaigns", owner.Token, new CampaignInput(next, "2027/2028", new(2027, 10, 1), new(2028, 9, 30)), HttpStatusCode.NoContent, "Crear siguiente campaña");
    var snapshot = await Get<CloudSnapshot>(firstPath, owner.Token);
    Check(snapshot.Plots.Count == 1 && snapshot.CampaignPlots.Count == 1 && snapshot.CampaignPlots.All(x => x.CampaignId == campaign), "Campaña nueva vacía y finca permanente conservada");
    Check(snapshot.Plots.Single().OliveTrees == 250 && snapshot.CampaignPlots.Single().OliveTrees == 240, "Separación de datos anuales y permanentes");
    await Status(HttpMethod.Get, firstPath, outsider.Token, null, HttpStatusCode.Forbidden, "Usuario ajeno no puede leer explotación");
    await Status(HttpMethod.Post, firstPath + "/members", owner.Token, new MemberInput(reader.Email, Role.Reader), HttpStatusCode.NoContent, "Administrador añade familiar lector");
    Check((await Get<List<CloudBusiness>>("/api/businesses", reader.Token)).Single().Id == first, "Familiar ve únicamente su explotación compartida");
    Check((await Get<CloudSnapshot>(firstPath, reader.Token)).Plots.Count == 1, "Familiar lee las mismas fincas desde otra sesión");
    await Status(HttpMethod.Post, firstPath + "/plots", reader.Token, new PlotInput(extraPlot, "Prohibida", 1, 1, "", ""), HttpStatusCode.Forbidden, "Lector no modifica fincas");
    await Status(HttpMethod.Post, firstPath + "/members", reader.Token, new MemberInput(outsider.Email, Role.Administrator), HttpStatusCode.Forbidden, "Lector no añade usuarios ni eleva permisos");
    await Status(HttpMethod.Put, $"{firstPath}/campaigns/{campaign}/status", reader.Token, new CloseCampaignInput(true), HttpStatusCode.Forbidden, "Lector no cierra campañas");
    await Status(HttpMethod.Post, firstPath + "/members", owner.Token, new MemberInput(worker.Email, Role.Operator), HttpStatusCode.NoContent, "Administrador añade operario");
    await Status(HttpMethod.Post, firstPath + "/plots", worker.Token, new PlotInput(extraPlot, "Segunda finca", 1, 100, "", ""), HttpStatusCode.NoContent, "Operario registra fincas");
    await Status(HttpMethod.Post, firstPath + "/members", worker.Token, new MemberInput(outsider.Email, Role.Reader), HttpStatusCode.Forbidden, "Operario no administra usuarios");
    await Status(HttpMethod.Put, $"{firstPath}/campaigns/{campaign}/status", owner.Token, new CloseCampaignInput(true), HttpStatusCode.NoContent, "Administrador cierra campaña");
    await Status(HttpMethod.Post, $"{firstPath}/campaigns/{campaign}/plots/{extraPlot}", worker.Token, annual, HttpStatusCode.Conflict, "Campaña cerrada bloquea nuevas actuaciones");
    await Status(HttpMethod.Put, $"{firstPath}/campaigns/{campaign}/status", worker.Token, new CloseCampaignInput(false), HttpStatusCode.Forbidden, "Operario no reabre campañas");
    await Status(HttpMethod.Put, $"{firstPath}/campaigns/{campaign}/status", owner.Token, new CloseCampaignInput(false), HttpStatusCode.NoContent, "Administrador reabre campaña");
    await Status(HttpMethod.Post, $"/api/businesses/{second}/campaigns/{next}/plots/{plot}", outsider.Token, annual, HttpStatusCode.NotFound, "Referencias a otra explotación rechazadas");
    Check((await Get<CloudSnapshot>(firstPath, owner.Token)).CampaignPlots.Count == 1, "Cierre y reapertura conservan histórico");
    Console.WriteLine($"{checks} comprobaciones de integración REAL correctas.");
}
finally
{
    var errors = new List<Exception>();
    foreach (var id in businesses)
    {
        try
        {
            var root = db.Collection("businesses").Document(id.ToString("N"));
            var document = await root.GetSnapshotAsync();
            if (!document.Exists) continue;
            if (!document.GetValue<string>("name").StartsWith($"Comprobación {run} ")) throw new Exception("Se bloqueó la limpieza: recurso sin marca de esta prueba.");
            await foreach (var collection in root.ListCollectionsAsync())
                foreach (var child in (await collection.GetSnapshotAsync()).Documents) await child.Reference.DeleteAsync();
            foreach (var uid in users) await db.Collection("users").Document(uid).Collection("businesses").Document(id.ToString("N")).DeleteAsync();
            await root.DeleteAsync();
        }
        catch (Exception ex) { errors.Add(ex); }
    }
    foreach (var uid in users)
    {
        try { await admin.DeleteUserAsync(uid); }
        catch (Exception ex) { errors.Add(ex); }
    }
    app.Delete();
    if (errors.Count > 0) throw new AggregateException("No se completó la limpieza de recursos de prueba.", errors);
    Console.WriteLine("Cuentas y datos temporales eliminados.");
}
