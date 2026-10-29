using System.Security.Claims;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication;
using Tunapp.Api;
using Tunapp.Core;

var builder = WebApplication.CreateBuilder(args);
var projectId = builder.Configuration["Firebase:ProjectId"] ?? throw new InvalidOperationException("Falta Firebase:ProjectId.");
// Credenciales de servidor mediante GOOGLE_APPLICATION_CREDENTIALS o identidad del alojamiento.
// Nunca copiar el JSON de servicio al proyecto MAUI ni a wwwroot.
var credential = GoogleCredential.GetApplicationDefault();
var firebase = FirebaseApp.Create(new AppOptions { Credential = credential, ProjectId = projectId });
builder.Services.AddSingleton(FirebaseAuth.GetAuth(firebase));
builder.Services.AddSingleton(new FirestoreDbBuilder { ProjectId = projectId, Credential = credential }.Build());
builder.Services.AddSingleton<CloudRepository>();
builder.Services.AddAuthentication("Firebase").AddScheme<AuthenticationSchemeOptions, FirebaseAuthenticationHandler>("Firebase", _ => { });
builder.Services.AddAuthorization();
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or RpcException or FirebaseAuthException)
    {
        var (status, message) = ex switch
        {
            UnauthorizedAccessException => (403, ex.Message),
            KeyNotFoundException => (404, ex.Message),
            ArgumentException => (400, ex.Message),
            InvalidOperationException => (409, ex.Message),
            _ => (503, "No se pudo acceder a Firebase. Inténtalo de nuevo.")
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ApiError(message));
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { application = "Tunapp", projectId, dataApiAvailable = true }));
app.MapGet("/health/ready", async (FirestoreDb db, CancellationToken cancellation) =>
{
    // Solo una lectura: no crea datos ni usuarios de prueba en la nube.
    await db.Document("_diagnostics/connection").GetSnapshotAsync(cancellation);
    return Results.Ok(new { firestore = "connected", projectId });
});
var api = app.MapGroup("/api").RequireAuthorization();
static string Uid(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;
api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { userId = Uid(user), email = user.FindFirstValue(ClaimTypes.Email), projectId }));
api.MapGet("/businesses", async (ClaimsPrincipal user, CloudRepository repo) => await repo.ListAsync(Uid(user)));
api.MapPost("/businesses", async (BusinessInput input, ClaimsPrincipal user, CloudRepository repo) =>
{
    await repo.CreateAsync(Uid(user), user.FindFirstValue(ClaimTypes.Email) ?? "", input);
    return Results.NoContent();
});
api.MapGet("/businesses/{business:guid}", async (Guid business, ClaimsPrincipal user, CloudRepository repo) => await repo.SnapshotAsync(business, Uid(user)));
api.MapPost("/businesses/{business:guid}/members", async (Guid business, MemberInput input, ClaimsPrincipal user, CloudRepository repo, FirebaseAuth auth) =>
{
    // Comprobar permisos ANTES de buscar por correo para no filtrar cuentas a terceros.
    await repo.DemandAdministratorAsync(business, Uid(user));
    UserRecord target;
    try { target = await auth.GetUserByEmailAsync(input.Email.Trim()); }
    catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
    { return Results.BadRequest(new ApiError("Ese usuario debe registrarse primero en Tunapp.")); }
    await repo.AddMemberAsync(business, Uid(user), target.Uid, target.Email, input.Role);
    return Results.NoContent();
});
api.MapPost("/businesses/{business:guid}/campaigns", async (Guid business, CampaignInput input, ClaimsPrincipal user, CloudRepository repo) =>
{
    await repo.CreateCampaignAsync(business, Uid(user), input); return Results.NoContent();
});
api.MapPut("/businesses/{business:guid}/campaigns/{campaign:guid}/status", async (Guid business, Guid campaign, CloseCampaignInput input, ClaimsPrincipal user, CloudRepository repo) =>
{
    await repo.SetClosedAsync(business, Uid(user), campaign, input.Closed); return Results.NoContent();
});
api.MapPost("/businesses/{business:guid}/plots", async (Guid business, PlotInput input, ClaimsPrincipal user, CloudRepository repo) =>
{
    await repo.CreatePlotAsync(business, Uid(user), input); return Results.NoContent();
});
api.MapPost("/businesses/{business:guid}/campaigns/{campaign:guid}/plots/{plot:guid}", async (Guid business, Guid campaign, Guid plot, CampaignPlotInput input, ClaimsPrincipal user, CloudRepository repo) =>
{
    await repo.LinkPlotAsync(business, Uid(user), campaign, plot, input); return Results.NoContent();
});
app.Run();
