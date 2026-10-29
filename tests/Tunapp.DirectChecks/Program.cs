using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Tunapp.Core;

const string project = "demo-tunapp-direct";
const string authHost = "http://127.0.0.1:9099";
const string fireHost = "http://127.0.0.1:8080";
// Solo emuladores, sin credenciales administrativas ni acceso al proyecto real.
var folder = Path.Combine(Path.GetTempPath(), "TunappDirectChecks", Guid.NewGuid().ToString("N"));
var network = new NetworkSwitch();
using var http = new HttpClient(network) { Timeout = TimeSpan.FromSeconds(15) };
var auth = new FirebaseAuthClient(http, "demo-key", authHost);
var mail = $"owner-{Guid.NewGuid():N}@example.test";
await auth.SignUpAsync(mail, "Aa12345!test");
var uid = auth.Session!.UserId;
var remote = new FirestoreRest(http, auth, uid, project, fireHost);
var opened = new List<DirectFarmService>();
async Task<DirectFarmService> Open(string device, FirestoreRest rest, FirebaseAuthClient identity)
{
    var service = await DirectFarmService.OpenAsync(Path.Combine(folder, device), rest, identity, onlineOnly: false); opened.Add(service); return service;
}
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine($"OK: {name}"); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or FirestoreDeniedException or InvalidOperationException)
    { Check(true, name); return; }
    throw new Exception("No se rechazó: " + name);
}
async Task<SyncOutcome> Sync(DirectFarmService service)
{
    var result = await service.SyncAsync();
    if (!result.Connected || service.Pending.Any(x => x.ErrorKind is not null))
        throw new Exception(result.Message + " " + string.Join(";", service.Pending.Select(x => x.Error)));
    return result;
}
try
{
    var first = await Open("device-a", remote, auth);
    var farm = Guid.NewGuid(); var campaign = Guid.NewGuid(); var plot = Guid.NewGuid();
    network.Offline = true;
    await first.CreateBusinessAsync(new(farm, "Familia sin cobertura"));
    await first.CreateCampaignAsync(farm, new(campaign, "2026/2027", new(2026, 10, 1), new(2027, 9, 30)));
    await first.SavePlotAsync(farm, new(plot, "Olivar", 2.5m, 250, "Picual", "Acceso sur"));
    await first.SaveCampaignPlotAsync(farm, campaign, plot, new(2.4m, 240, "Picual", "Datos de campaña"));
    Check(first.Pending.Count == 4 && first.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 240, "Crear explotación, campaña y finca sin conexión guarda datos y cola");
    var offline = await first.SyncAsync();
    Check(!offline.Connected && first.Pending.Count == 4, "Corte de red no pierde operaciones pendientes");
    await first.DisposeAsync(); opened.Remove(first);
    auth.Session!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
    await auth.RestoreAsync(auth.Session, refresh: false);
    first = await Open("device-a", remote, auth);
    Check(first.Pending.Count == 4 && first.Snapshot(farm)!.Plots.Single().Hectares == 2.5m, "Reinicio sin cobertura recupera datos con token caducado");
    network.Offline = false;
    await Sync(first);
    Check(first.Pending.Count == 0, "Reconexión renueva sesión y confirma toda la cola");
    var second = await Open("device-b", remote, auth);
    await Sync(second);
    Check(second.Snapshot(farm)!.Plots.Count == 1 && second.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 240, "Segundo dispositivo obtiene los mismos datos de Firestore");
    var next = Guid.NewGuid();
    await first.CreateCampaignAsync(farm, new(next, "2027/2028", new(2027, 10, 1), new(2028, 9, 30)));
    await Sync(first);
    Check(first.Snapshot(farm)!.Plots.Count == 1 && first.Snapshot(farm)!.CampaignPlots.All(x => x.CampaignId != next), "Campaña nueva vacía sin duplicar fincas ni movimientos");
    await first.SavePlotAsync(farm, new(plot, "Olivar", 2.6m, 260, "Picual", "Cambio con respuesta perdida"), create: false);
    network.LoseNextCommitResponse = true;
    var lost = await first.SyncAsync(); var commits = network.CommitCount;
    Check(!lost.Connected && first.Pending.Count == 1 && first.Pending[0].Attempted, "Respuesta perdida conserva el envío con su identidad");
    await Sync(first);
    Check(first.Pending.Count == 0 && network.CommitCount == commits, "Recibo remoto evita repetir una escritura ya confirmada en Firebase");
    await Sync(second);
    await first.SavePlotAsync(farm, new(plot, "Olivar", 2.7m, 270, "Picual", "Cambio A"), create: false);
    await second.SavePlotAsync(farm, new(plot, "Olivar", 2.8m, 280, "Picual", "Cambio B"), create: false);
    await Sync(second);
    await first.SyncAsync();
    Check(first.Pending.Single().ErrorKind == "conflict" && first.Snapshot(farm)!.Plots.Single().Hectares == 2.7m, "Edición concurrente se señala sin sobrescribir ni perder la versión local");
    var review = await first.ConflictAsync(first.Pending.Single().Sequence);
    Check(review!.Integer("areaM2") == 28000, "Revisión compara con la versión real del otro dispositivo");
    await first.ResolveAsync(first.Pending.Single().Sequence, review, keepLocal: true);
    await Sync(first); await Sync(second);
    Check(second.Snapshot(farm)!.Plots.Single().Hectares == 2.7m, "Confirmación explícita permite conservar la versión local");
    network.Offline = true;
    await first.SavePlotAsync(farm, new(plot, "Olivar", 2.9m, 290, "Picual", "Primera edición offline"), create: false);
    var identity = first.Pending.Single().OperationId;
    await first.SavePlotAsync(farm, new(plot, "Olivar", 3m, 300, "Picual", "Segunda edición offline"), create: false);
    Check(first.Pending.Count == 1 && first.Pending[0].OperationId == identity, "Ediciones locales aún no enviadas se consolidan sin duplicados");
    network.Offline = false; await Sync(first); await Sync(second);
    Check(second.Snapshot(farm)!.Plots.Single().OliveTrees == 300 && second.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 240, "Editar finca permanente conserva histórico anual");
    await first.SetClosedAsync(farm, campaign, true); await Sync(first);
    // El dispositivo B conserva el estado abierto anterior: las reglas deben decidir en el servidor.
    await second.SaveCampaignPlotAsync(farm, campaign, plot, new(2.3m, 230, "Picual", "Trabajo pendiente al cerrar"), create: false);
    await second.SyncAsync();
    Check(second.Pending.Single().ErrorKind == "permission" && second.Snapshot(farm)!.Campaigns.Single(x => x.Id == campaign).Status == CampaignStatus.Closed, "Cierre remoto bloquea una edición offline de otro dispositivo");
    await first.SetClosedAsync(farm, campaign, false); await Sync(first);
    await second.RetryAsync(second.Pending.Single().Sequence); await Sync(second);
    Check(second.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 230 && second.Pending.Count == 0, "Reabrir permite enviar el cambio conservado tras revisión");

    var guestAuth = new FirebaseAuthClient(http, "demo-key", authHost);
    var guestMail = $"guest-{Guid.NewGuid():N}@example.test";
    await guestAuth.SignUpAsync(guestMail, "Aa12345!test");
    var guestRemote = new FirestoreRest(http, guestAuth, guestAuth.Session!.UserId, project, fireHost);
    var guest = await Open("guest", guestRemote, guestAuth);
    await Sync(guest);
    Check(guest.Businesses.Count == 0, "Otra cuenta tiene caché independiente y no recibe datos ajenos");
    await Reject(async () => { await guestRemote.GetAsync($"farms/{farm:N}/plots/{plot:N}"); }, "Acceso directo a finca de otra explotación denegado por reglas");
    await first.InviteAsync(farm, guestMail, Role.Reader);
    await guest.SyncAsync(); Check(guest.Invitations.Count == 0, "Correo sin verificar no obtiene invitaciones");
    await guestAuth.SendVerificationEmailAsync();
    var codes = await http.GetFromJsonAsync<JsonObject>($"{authHost}/emulator/v1/projects/{project}/oobCodes");
    var code = codes!["oobCodes"]!.AsArray().Single(x => x!["email"]!.GetValue<string>() == guestMail && x["requestType"]!.GetValue<string>() == "VERIFY_EMAIL")!;
    using (var verified = await http.PostAsJsonAsync($"{authHost}/identitytoolkit.googleapis.com/v1/accounts:update?key=demo-key", new { oobCode = code["oobCode"]!.GetValue<string>() })) verified.EnsureSuccessStatusCode();
    await guestAuth.RefreshProfileAsync(); await Sync(guest);
    Check(guestAuth.Session!.EmailVerified && guest.Invitations.Count == 1, "Correo verificado obtiene únicamente su invitación");
    await guest.AcceptInvitationAsync(guest.Invitations.Single()); await Sync(guest);
    Check(guest.Businesses.Single().Role == Role.Reader && guest.Snapshot(farm)!.Plots.Single().OliveTrees == 300, "Familiar acepta invitación y consulta los datos compartidos");
    await Reject(() => guest.SavePlotAsync(farm, new(Guid.NewGuid(), "No permitida", 1, 1, "", "")), "Lector no puede guardar cambios locales agrícolas");
    await Sync(first);
    var guestMember = first.Snapshot(farm)!.Members.Single(x => x.UserId == guestAuth.Session.UserId);
    await first.SetMemberRoleAsync(farm, guestMember, Role.Operator); await Sync(guest);
    Check(guest.Businesses.Single().Role == Role.Operator, "Cambiar permisos actualiza el otro dispositivo");
    var personal = Guid.NewGuid();
    await guest.CreateBusinessAsync(new(personal, "Otra explotación propia")); await Sync(guest);
    await guest.SavePlotAsync(farm, new(Guid.NewGuid(), "Cambio aún sin enviar", 1, 10, "", ""));
    await first.SetMemberRoleAsync(farm, guestMember, null);
    network.DenyNextPath = $"/farms/{personal:N}";
    try { await guest.SyncAsync(); }
    catch (FirestoreDeniedException) { }
    Check(guest.Snapshot(farm) is null && guest.Pending.Count == 1, "Revocación confirmada se aplica aunque otra descarga falle después");
    await guest.SyncAsync();
    Check(guest.Businesses.Single().Id == personal && guest.Snapshot(farm) is null && guest.Pending.Count == 1, "Revocar acceso retira caché visible y conserva el cambio pendiente bloqueado");
    await Reject(() => first.SetMemberRoleAsync(farm, first.Snapshot(farm)!.Members.Single(x => x.UserId == uid), Role.Reader), "Propietario conserva siempre acceso administrativo");
    var previous = auth.Session!;
    await auth.RestoreAsync(guestAuth.Session!, refresh: false);
    await Reject(async () => await first.SyncAsync(), "Cambiar de cuenta no envía datos del usuario anterior");
    await auth.RestoreAsync(previous, refresh: false);
    await Reject(async () => { await CloudDatabase.OpenAsync(Path.Combine(folder, "device-a", DirectFarmService.DatabaseName(uid)), "otro-usuario"); }, "SQLite rechaza abrir la base de otra cuenta");
    var optionalCampaign = Guid.NewGuid(); var optionalPlot = Guid.NewGuid();
    await first.CreateCampaignAsync(farm, new(optionalCampaign, "Sin fechas", null, null));
    await first.SavePlotAsync(farm, new(optionalPlot, "Sin superficie", null, 0, "", ""));
    await first.SaveCampaignPlotAsync(farm, optionalCampaign, optionalPlot, new(null, 0, "", ""));
    await Sync(first); await Sync(second);
    var optionalState = second.Snapshot(farm)!;
    Check(optionalState.Campaigns.Single(x => x.Id == optionalCampaign).Start is null && optionalState.Campaigns.Single(x => x.Id == optionalCampaign).End is null, "Fechas vacias llegan al segundo dispositivo");
    Check(optionalState.Plots.Single(x => x.Id == optionalPlot).Hectares is null && optionalState.CampaignPlots.Single(x => x.CampaignId == optionalCampaign).Hectares is null, "Superficies vacias llegan al segundo dispositivo");
    Check(CampaignPlotsView.ForCampaign(optionalState.Plots, optionalState.CampaignPlots, farm, optionalCampaign).Count == optionalState.Plots.Count, "Todas las fincas aparecen en una campana nueva");
    var cooperative = Guid.NewGuid();
    network.Offline = true;
    await first.CreateCooperativeAsync(farm, new(cooperative, "Destino de cosechas", ""));
    var harvest = new FieldWorkInput(Guid.NewGuid(), optionalCampaign, optionalPlot, FieldWorkKind.Harvest,
        new(2026, 10, 27), "Aceituna", 123.456m, "kg", cooperative, "Recogida de hoy", "Árbol");
    await first.CreateFieldWorkAsync(farm, harvest);
    await first.CreateFieldWorkAsync(farm, harvest with { Id = Guid.NewGuid(), Kind = FieldWorkKind.Treatment, Description = "Abonado", Quantity = 2.5m, Unit = "L", CooperativeId = null, Quality = null, Cost = 12.34m });
    Check(first.FieldWorks(farm, optionalCampaign).Count == 2 && first.Pending.Count == 3, "Nuevos formularios guardan cooperativa, cosecha y tratamiento sin cobertura");
    network.Offline = false;
    network.LoseNextCommitResponse = true;
    await first.SyncAsync();
    await Sync(first); await Sync(second);
    Check(second.Cooperatives(farm).Single().Id == cooperative && second.FieldWorks(farm, optionalCampaign).Count == 2,
        "Cooperativa y trabajos llegan al segundo móvil sin duplicar tras perder una respuesta");
    Check(second.FieldWorks(farm, optionalCampaign).Single(x => x.Kind == FieldWorkKind.Harvest).Quantity == 123.456m && second.FieldWorks(farm, optionalCampaign).Single(x => x.Kind == FieldWorkKind.Harvest).Quality == "Árbol", "Cantidad de cosecha conserva tres decimales");
    Check(second.FieldWorks(farm, campaign).Count == 0, "Historial separado por campaña");
    await Reject(() => first.CreateFieldWorkAsync(farm, harvest with { Id = Guid.NewGuid(), PlotId = Guid.NewGuid() }), "Trabajo en finca ajena rechazado");
    await first.SaveCooperativePriceAsync(farm, optionalCampaign, cooperative, "Árbol", 0.7525m);
    await Sync(first); await Sync(second);
    Check(second.CooperativePrices(farm, optionalCampaign).Single().Price == 0.7525m, "Precio conserva cuatro decimales entre dispositivos");
    Check(second.FieldWorks(farm, optionalCampaign).Single(x => x.Kind == FieldWorkKind.Treatment).Cost == 12.34m, "Coste conserva centimos entre dispositivos");
    await first.SaveCooperativePriceAsync(farm, optionalCampaign, cooperative, "Árbol", 0);
    await Sync(first); await Sync(second);
    Check(second.CooperativePrices(farm, optionalCampaign).Single().Price == 0, "Editar precio sincroniza sin duplicados");
    Check(second.CooperativePrices(farm, campaign).Count == 0, "Precios aislados por campana");
    await first.SetClosedAsync(farm, optionalCampaign, true); await Sync(first);
    await Reject(() => first.CreateFieldWorkAsync(farm, harvest with { Id = Guid.NewGuid() }), "Campaña cerrada impide cosechas y tratamientos");
    // La app usa exclusivamente el modo conectado. Las comprobaciones anteriores
    // conservan cobertura de la recuperación de bases de versiones antiguas.
    var online = await DirectFarmService.OpenAsync(Path.Combine(folder, "online"), remote, auth);
    opened.Add(online);
    await Sync(online);
    var onlinePlot = Guid.NewGuid();
    network.Offline = true;
    try
    {
        await online.SavePlotAsync(farm, new(onlinePlot, "Solo conectado", null, 1, "", ""));
        throw new Exception("Se permitió guardar sin conexión.");
    }
    catch (HttpRequestException) { }
    Check(online.Pending.Count == 0 && online.Snapshot(farm)!.Plots.All(x => x.Id != onlinePlot),
        "Sin red no se crea registro local ni cola en el modo conectado");
    network.Offline = false;
    await online.SavePlotAsync(farm, new(onlinePlot, "Solo conectado", null, 1, "", ""));
    Check(online.Pending.Count == 0 && await remote.GetAsync($"farms/{farm:N}/plots/{onlinePlot:N}") is not null,
        "Guardar conectado confirma Firebase sin crear operaciones pendientes");
    await Sync(second);
    Check(second.Snapshot(farm)!.Plots.Any(x => x.Id == onlinePlot), "El guardado conectado llega al otro dispositivo");
    network.DenyNextPath = "documents:commit";
    await Reject(() => online.SavePlotAsync(farm, new(onlinePlot, "Rechazado", null, 99, "", ""), create: false),
        "Firebase rechaza el guardado conectado sin dar éxito");
    Check(online.Pending.Count == 0 && online.Snapshot(farm)!.Plots.Single(x => x.Id == onlinePlot).OliveTrees == 1,
        "Permiso denegado no modifica la vista ni crea cambios locales");
    network.LoseNextCommitResponse = true;
    try
    {
        await online.SavePlotAsync(farm, new(onlinePlot, "Respuesta incierta", null, 2, "", ""), create: false);
        throw new Exception("Se dio por confirmado un guardado sin respuesta.");
    }
    catch (HttpRequestException) { }
    Check(online.Pending.Count == 0 && online.Snapshot(farm)!.Plots.Single(x => x.Id == onlinePlot).OliveTrees == 1,
        "Respuesta perdida no muestra un guardado confirmado ni genera cola local");
    await Sync(online);
    Check(online.Snapshot(farm)!.Plots.Single(x => x.Id == onlinePlot).OliveTrees == 2,
        "Actualizar recupera de Firebase el resultado de un guardado con respuesta perdida");
    var onlineFarm = Guid.NewGuid();
    await online.CreateBusinessAsync(new(onlineFarm, "Nueva explotación conectada"));
    Check(online.Snapshot(onlineFarm)?.Business.Role == Role.Administrator && online.Pending.Count == 0,
        "Explotación y permisos se confirman juntos en Firebase");
    await Sync(second);
    await second.SavePlotAsync(farm, new(onlinePlot, "Cambio en otro dispositivo", null, 3, "", ""), create: false);
    await Sync(second);
    try
    {
        await online.SavePlotAsync(farm, new(onlinePlot, "Cambio obsoleto", null, 4, "", ""), create: false);
        throw new Exception("Se sobrescribió un cambio ajeno sin revisar.");
    }
    catch (FirestoreConflictException) { }
    Check(online.Pending.Count == 0, "Conflicto conectado no crea una edición local pendiente");
    await Sync(online);
    Check(online.Snapshot(farm)!.Plots.Single(x => x.Id == onlinePlot).OliveTrees == 3,
        "Tras un conflicto se conserva el cambio confirmado del otro dispositivo");
    var legacyPlot = Guid.NewGuid();
    network.Offline = true;
    await first.SavePlotAsync(farm, new(legacyPlot, "Registro anterior a la actualización", null, 5, "", ""));
    var upgraded = await DirectFarmService.OpenAsync(Path.Combine(folder, "device-a"), remote, auth);
    opened.Add(upgraded);
    Check(upgraded.Pending.Count == 1, "Actualizar al modo conectado conserva registros pendientes de versiones anteriores");
    network.Offline = false;
    var independentPlot = Guid.NewGuid();
    await using (var legacyDb = await CloudDatabase.OpenAsync(Path.Combine(folder, "device-a", DirectFarmService.DatabaseName(uid)), uid))
        await legacyDb.BlockAsync(upgraded.Pending.Single().Sequence, "permission", "Rechazo anterior de Firebase");
    var connectedWithPending = await upgraded.SyncAsync();
    Check(connectedWithPending.Connected && connectedWithPending.Pending == 1,
        "Un registro rechazado no se confunde con falta de conexión a Internet");
    await upgraded.SavePlotAsync(farm, new(independentPlot, "Registro independiente", null, 6, "", ""));
    Check(upgraded.Pending.Count == 1 && await remote.GetAsync($"farms/{farm:N}/plots/{independentPlot:N}") is not null,
        "Registros antiguos pendientes no bloquean nuevos guardados independientes con conexión");
    await upgraded.SavePlotAsync(farm, new(legacyPlot, "Edicion tras recuperar conexion", null, 9, "", ""), create: false);
    Check(upgraded.Pending.Count == 0 && (await remote.GetAsync($"farms/{farm:N}/plots/{legacyPlot:N}"))!.Integer("trees") == 9,
        "Editar recupera el registro pendiente y guarda la nueva version sin bloqueo");
    await Sync(first);
    await first.SavePlotAsync(farm, new(legacyPlot, "Cambio con respuesta perdida", null, 10, "", ""), create: false);
    network.LoseNextCommitResponse = true;
    await first.SyncAsync();
    await upgraded.DisposeAsync(); opened.Remove(upgraded);
    upgraded = await DirectFarmService.OpenAsync(Path.Combine(folder, "device-a"), remote, auth); opened.Add(upgraded);
    commits = network.CommitCount;
    await upgraded.SavePlotAsync(farm, new(legacyPlot, "Edicion despu?s del corte", null, 11, "", ""), create: false);
    Check(upgraded.Pending.Count == 0 && network.CommitCount == commits + 1 &&
        (await remote.GetAsync($"farms/{farm:N}/plots/{legacyPlot:N}"))!.Integer("trees") == 11,
        "Respuesta perdida se recupera por recibo antes de editar sin duplicar el envio anterior");
    await Sync(first);
    await first.SavePlotAsync(farm, new(legacyPlot, "Cambio local pendiente", null, 12, "", ""), create: false);
    await Sync(second);
    await second.SavePlotAsync(farm, new(legacyPlot, "Cambio ajeno confirmado", null, 13, "", ""), create: false);
    await Sync(second);
    await upgraded.DisposeAsync(); opened.Remove(upgraded);
    upgraded = await DirectFarmService.OpenAsync(Path.Combine(folder, "device-a"), remote, auth); opened.Add(upgraded);
    try
    {
        await upgraded.SavePlotAsync(farm, new(legacyPlot, "No sobrescribir conflicto", null, 14, "", ""), create: false);
        throw new Exception("Se sobrescribio un conflicto durante la recuperacion.");
    }
    catch (FirestoreConflictException) { }
    Check(upgraded.Pending.Single().ErrorKind == "conflict" &&
        upgraded.Snapshot(farm)!.Plots.Single(x => x.Id == legacyPlot).OliveTrees == 12 &&
        (await remote.GetAsync($"farms/{farm:N}/plots/{legacyPlot:N}"))!.Integer("trees") == 13,
        "Recuperar antes de editar conserva ambas versiones cuando existe un conflicto real");
    network.DenyNextPath = $"farms/{farm:N}/cooperatives";
    var partialAccess = await online.SyncAsync();
    Check(partialAccess.Connected,
        "Permiso denegado en una colección no se confunde con falta de conexión ni impide cargar el resto");
    Console.WriteLine($"{checks} comprobaciones directas correctas contra emuladores. SQLite real: {folder}");
}
finally
{
    foreach (var service in opened) await service.DisposeAsync();
}

sealed class NetworkSwitch : DelegatingHandler
{
    public bool Offline { get; set; }
    public bool LoseNextCommitResponse { get; set; }
    public int CommitCount { get; private set; }
    public string? DenyNextPath { get; set; }
    public NetworkSwitch() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("Sin red de prueba.");
        if (DenyNextPath is not null && request.RequestUri!.AbsolutePath.EndsWith(DenyNextPath, StringComparison.Ordinal))
        { DenyNextPath = null; return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden); }
        var commit = request.RequestUri!.AbsolutePath.EndsWith("documents:commit", StringComparison.Ordinal);
        if (commit) CommitCount++;
        var response = await base.SendAsync(request, cancellationToken);
        if (commit && LoseNextCommitResponse && response.IsSuccessStatusCode)
        { LoseNextCommitResponse = false; response.Dispose(); throw new HttpRequestException("Respuesta perdida tras guardar en el servidor."); }
        return response;
    }
}
