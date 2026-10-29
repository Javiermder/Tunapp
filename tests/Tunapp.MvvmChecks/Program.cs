using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using Tunapp;
using Tunapp.Core;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine($"OK {message}"); }
async Task Click(ScreenViewModel screen, string title)
{
    var item = screen.Items.OfType<ActionItem>().Single(x => x.Title == title);
    await ((IAsyncRelayCommand)item.Command).ExecuteAsync(null);
}
var nav = new FakeNavigation();
var form = new FormViewModel("Prueba", nav);
var field = form.Field("Nombre", "Sin perder");
var notified = false; field.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(field.Text);
field.Text = "Conservar"; Check(notified, "Los campos notifican cambios para el binding");
var block = new TaskCompletionSource(); var writes = 0;
form.Submit("Guardar", async () => { writes++; await block.Task; });
var saving = form.SaveCommand.ExecuteAsync(null);
Check(form.IsBusy && !form.SaveCommand.CanExecute(null) && !form.CancelCommand.CanExecute(null), "Guardar desactiva comandos y bloquea volver");
await form.SaveCommand.ExecuteAsync(null);
Check(writes == 1, "Doble pulsación no duplica el guardado");
block.SetResult(); await saving;
Check(!form.IsBusy && nav.Pops == 1 && field.Text == "Conservar", "Guardado correcto vuelve una sola vez");
form.Submit("Guardar", () => throw new IOException("Sin conexión"));
await form.SaveCommand.ExecuteAsync(null);
Check(form.Error == "Sin conexión" && field.Text == "Conservar" && nav.Pops == 1 && form.SaveCommand.CanExecute(null), "Fallo conserva campos, muestra error y permite reintentar");
form.Submit("Guardar", () => throw new OperationCanceledException());
await form.SaveCommand.ExecuteAsync(null);
Check(form.Error is null && nav.Pops == 1 && !form.IsBusy, "Cancelar confirmación conserva formulario sin guardar");

var business = Guid.NewGuid(); var campaign = new Campaign(Guid.NewGuid(), business, "2026/2027", null, null);
var plot = new Plot(Guid.NewGuid(), business, "La loma", null, 315, "Hojiblanca", "");
var coop = new Cooperative(Guid.NewGuid(), business, "Cooperativa", "");
FieldWorkInput? saved = null; var attempts = new List<Guid>(); var fail = true;
var work = new FieldWorkViewModel(nav, FieldWorkKind.Harvest, campaign, [plot], [coop], input =>
{
    attempts.Add(input.Id); if (fail) throw new IOException("No confirmado"); saved = input; return Task.CompletedTask;
});
work.Quantity.Text = "250,5";
await work.SaveCommand.ExecuteAsync(null);
Check(work.Error == "Selecciona una finca." && attempts.Count == 0, "Cosecha exige finca antes de enviar");
work.Plot.SelectedItem = plot;
await work.SaveCommand.ExecuteAsync(null);
Check(work.Error!.Contains("calidad") && attempts.Count == 0, "Cosecha exige calidad antes de enviar");
work.Quality.SelectedItem = "Árbol"; work.Cooperative.SelectedItem = coop;
await work.SaveCommand.ExecuteAsync(null);
Check(work.Error == "No confirmado" && work.Quantity.Text == "250,5", "Fallo remoto conserva los datos del vale");
fail = false; await work.SaveCommand.ExecuteAsync(null);
Check(saved is { Quantity: 250.5m, Unit: "kg", Quality: "Árbol" } && saved.CampaignId == campaign.Id && saved.PlotId == plot.Id && saved.CooperativeId == coop.Id, "Cosecha envía campaña, finca, calidad y cantidad española");
Check(attempts.Count == 2 && attempts[0] == attempts[1], "Reintento mantiene identificador idempotente");
await Click(work, "Quitar cooperativa"); Check(work.Cooperative.SelectedItem is null, "Cooperativa opcional se puede quitar");
var treatment = new FieldWorkViewModel(nav, FieldWorkKind.Treatment, campaign, [plot], [], input => { saved = input; return Task.CompletedTask; });
treatment.Plot.SelectedItem = plot; treatment.Description.Text = "Abonado"; treatment.Quantity.Text = "2,5"; treatment.Cost.Text = "125,50";
await treatment.SaveCommand.ExecuteAsync(null);
Check(saved is { Kind: FieldWorkKind.Treatment, Unit: "L", Quantity: 2.5m, Cost: 125.50m, CooperativeId: null, Quality: null }, "Tratamiento conserva coste, unidad y ausencia de calidad/cooperativa");

var role = Role.Administrator; var revoked = false; var closedCalls = 0; CampaignInput? createdCampaign = null;
var works = new List<FieldWork>
{
    new(Guid.NewGuid(), business, campaign.Id, plot.Id, FieldWorkKind.Harvest, new(2026,10,28), "Aceituna", 10, "kg", coop.Id, "", "Árbol"),
    new(Guid.NewGuid(), business, campaign.Id, plot.Id, FieldWorkKind.Treatment, new(2026,10,28), "Abonado", 100, "kg", null, ""),
    new(Guid.NewGuid(), Guid.NewGuid(), campaign.Id, plot.Id, FieldWorkKind.Harvest, new(2026,10,28), "Otra explotación", 999, "kg", coop.Id, "", "Árbol"),
    new(Guid.NewGuid(), business, Guid.NewGuid(), plot.Id, FieldWorkKind.Harvest, new(2026,10,28), "Otra campaña", 888, "kg", coop.Id, "", "Árbol")
};
var context = new ManagementContext
{
    Role = () => role, Plots = () => revoked ? throw new UnauthorizedAccessException("Acceso retirado") : [plot], Campaigns = () => [campaign], Cooperatives = () => [coop],
    AnnualPlots = () => [new(business, campaign.Id, plot.Id, 2.5m, 400, "Picual", "Anual")], Prices = () => [], Works = () => works,
    ActiveCampaign = () => campaign.Id, Team = () => [], SavePlot = (_, _) => Task.CompletedTask,
    CreateCampaign = input => { createdCampaign = input; return Task.CompletedTask; }, SelectCampaign = _ => Task.CompletedTask,
    SetClosed = (_, _) => { closedCalls++; return Task.CompletedTask; }, CreateCooperative = _ => Task.CompletedTask,
    SavePrice = (_, _, _, _) => Task.CompletedTask, AddWork = _ => Task.CompletedTask
};
var plots = new PlotsViewModel(context, nav); await plots.ActivateAsync();
Check(plots.Rows.Single().Summary.Contains("400") && plots.Rows.Single().Summary.Contains("Picual"), "Fincas proyectan valores de la campaña activa");
Check(plots.Rows.Single().Extra == "10 kg recogidos", "Kilos excluyen tratamientos, otras campañas y otras explotaciones");
plots.SearchText = "picual"; Check(plots.Rows.Count == 1, "Búsqueda usa variedad anual");
plots.SearchText = "inexistente"; Check(plots.Rows.Count == 0 && plots.EmptyTitle == "No se encontraron resultados", "Listado distingue búsqueda sin coincidencias");
role = Role.Reader; plots.Refresh(); var pushed = nav.Stack.Count; await plots.AddRecordCommand.ExecuteAsync(null);
Check(!plots.ShowAdd && nav.Stack.Count == pushed, "Lector no puede abrir creación aunque invoque el comando");
role = Role.Administrator; plots.SearchText = ""; plots.Refresh(); revoked = true; plots.Refresh();
Check(plots.Rows.Count == 0 && !plots.ShowAdd && plots.EmptyMessage == "Acceso retirado", "Revocación retira datos y acciones visibles"); revoked = false;
var campaigns = new CampaignsViewModel(context, nav); await campaigns.ActivateAsync();
await campaigns.AddRecordCommand.ExecuteAsync(null);
var campaignForm = (FormViewModel)nav.Stack.Last(); campaignForm.Items.OfType<FieldViewModel>().First().Text = "2027/2028";
await campaignForm.SaveCommand.ExecuteAsync(null);
Check(createdCampaign is { Name: "2027/2028", Start: null, End: null }, "Crear campaña conserva fechas opcionales vacías");
await campaigns.OpenRecordCommand.ExecuteAsync(campaigns.Rows.Single()); var detail = nav.Stack.Last(); await detail.ActivateAsync();
nav.Confirmation = false; await Click(detail, "Cerrar campaña"); Check(closedCalls == 0, "Cancelar cierre no modifica campaña");
nav.Confirmation = true; await Click(detail, "Cerrar campaña"); Check(closedCalls == 1, "Confirmar cierre llama al servicio una sola vez");

var environment = new FakeEnvironment(); var handler = new RejectLoginHandler(); var http = new HttpClient(handler);
var auth = new FirebaseAuthClient(http, "public-test-key"); var cloud = new CloudViewModel(nav, environment, http, auth);
await cloud.ActivateAsync(); var loginFields = cloud.Items.OfType<FieldViewModel>().ToList();
loginFields[0].Text = "test@example.invalid"; loginFields[1].Text = "invalid-password";
await Click(cloud, "Iniciar sesión");
Check(handler.Requests == 1 && loginFields[0].Text == "test@example.invalid" && loginFields[1].Text == "invalid-password" && !cloud.IsBusy, "Error de acceso conserva formulario y libera comandos sin tocar Firebase real");
Check(environment.Writes == 0 && auth.Session is null, "Acceso fallido no guarda sesión");
Check(typeof(CloudViewModel).Assembly.GetReferencedAssemblies().All(x => !x.Name!.Contains("Maui")), "ViewModels no dependen de controles ni SDK MAUI");

var remote = new FarmHandler(business, campaign, plot, coop);
using var cloudHttp = new HttpClient(remote);
var accountEnvironment = new FakeEnvironment
{
    DataDirectory = Path.Combine(Path.GetTempPath(), "TunappMvvmChecks", Guid.NewGuid().ToString("N")),
    SavedSession = JsonSerializer.Serialize(new FirebaseSession { UserId = "mvvm-user", Email = "mvvm@example.invalid", IdToken = "test-token", RefreshToken = "test-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) })
};
Directory.CreateDirectory(accountEnvironment.DataDirectory);
var accountNav = new FakeNavigation();
await using (var account = new CloudViewModel(accountNav, accountEnvironment, cloudHttp, new FirebaseAuthClient(cloudHttp, "public-test-key")))
{
    await account.ActivateAsync();
    var dashboard = account.Items.OfType<DashboardItem>().SingleOrDefault() ?? throw new Exception($"No se cargó el dashboard: {account.Notice}");
    Check(dashboard.Farm == "Explotación de prueba", "Restauración de sesión carga la explotación mediante los servicios existentes");
    await ((IAsyncRelayCommand)dashboard.Harvest).ExecuteAsync(null);
    var draft = (FieldWorkViewModel)accountNav.Stack.Last(); draft.Quantity.Text = "37,5";
    accountEnvironment.IsConnected = false;
    await account.ConnectionChangedCommand.ExecuteAsync(null);
    Check(accountNav.ConnectionShown == 1 && account.Items.OfType<TextItem>().Any(x => x.Text == "Conexión necesaria"), "Pérdida de conexión bloquea inicio y pantalla hija");
    var beforeReconnect = remote.Requests;
    accountEnvironment.IsConnected = true;
    await account.ConnectionChangedCommand.ExecuteAsync(null);
    Check(remote.Requests > beforeReconnect && accountNav.ConnectionHidden == 1, "Reconexión sincroniza y retira bloqueo incluso antes de 60 segundos");
    Check(draft.Quantity.Text == "37,5", "Sincronización en segundo plano conserva el formulario abierto");
    remote.Revoked = true;
    await account.ConnectionChangedCommand.ExecuteAsync(null);
    Check(!account.Items.OfType<DashboardItem>().Any(), "Revocación remota retira el dashboard de la explotación");
    draft.Plot.SelectedItem = plot; draft.Quality.SelectedItem = "Árbol";
    await draft.SaveCommand.ExecuteAsync(null);
    Check(draft.Error!.Contains("ha cambiado") && remote.Writes == 0, "Formulario anterior no escribe tras perder el acceso a la explotación");
}
// Only this unique test-owned directory is removed.
Directory.Delete(accountEnvironment.DataDirectory, true);
Console.WriteLine($"{checks} comprobaciones MVVM correctas.");

sealed class FakeNavigation : IAppNavigation
{
    public List<ScreenViewModel> Stack { get; } = [];
    public int Pops { get; private set; }
    public int ConnectionShown { get; private set; }
    public int ConnectionHidden { get; private set; }
    public bool Confirmation { get; set; } = true;
    public bool HasChildPage => Stack.Count > 0;
    public Task PushAsync(ScreenViewModel screen) { Stack.Add(screen); return Task.CompletedTask; }
    public Task PopAsync() { Pops++; if (Stack.Count > 0) Stack.RemoveAt(Stack.Count - 1); return Task.CompletedTask; }
    public Task PopToRootAsync() { Stack.Clear(); return Task.CompletedTask; }
    public Task ShowConnectionAsync(ScreenViewModel screen) { ConnectionShown++; return Task.CompletedTask; }
    public Task HideConnectionAsync() { ConnectionHidden++; return Task.CompletedTask; }
    public void RefreshCurrent() { }
    public Task AlertAsync(string title, string message, string accept) => Task.CompletedTask;
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(Confirmation);
    public Task<string?> ChooseAsync(string title, string cancel, params string[] options) => Task.FromResult<string?>(cancel);
}
sealed class FakeEnvironment : IAppEnvironment
{
    public string DataDirectory { get; set; } = Path.GetTempPath();
    public bool IsConnected { get; set; } = true;
    public string? SavedSession { get; set; }
    public int Writes { get; private set; }
    public Task<string?> ReadSessionAsync(string key) => Task.FromResult(SavedSession);
    public Task WriteSessionAsync(string key, string? value) { Writes++; return Task.CompletedTask; }
}
sealed class FarmHandler(Guid business, Campaign campaign, Plot plot, Cooperative cooperative) : HttpMessageHandler
{
    public bool Revoked { get; set; }
    public int Requests { get; private set; }
    public int Writes { get; private set; }
    private readonly string farm = $"farms/{business:N}";
    private JsonObject Document(string path, JsonObject fields) => new()
    {
        ["name"] = $"projects/tunapp-j27/databases/(default)/documents/{path}", ["fields"] = fields, ["updateTime"] = "2026-10-28T00:00:00Z"
    };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.Method != HttpMethod.Get) { Writes++; throw new Exception("Esta comprobación no debe escribir datos remotos."); }
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).Split("/documents/")[1];
        JsonNode result = path switch
        {
            "users/mvvm-user/farms" => new JsonObject { ["documents"] = Revoked ? new JsonArray() : new JsonArray(Document($"users/mvvm-user/farms/{business:N}", new() { ["farmId"] = FireFields.Text(business.ToString("N")) })) },
            var p when p == farm => Document(farm, new() { ["name"] = FireFields.Text("Explotación de prueba"), ["ownerUid"] = FireFields.Text("mvvm-user") }),
            var p when p == farm + "/members/mvvm-user" => Member(),
            var p when p == farm + "/members" => new JsonObject { ["documents"] = new JsonArray(Member()) },
            var p when p == farm + "/campaigns" => new JsonObject { ["documents"] = new JsonArray(Document($"{farm}/campaigns/{campaign.Id:N}", new() { ["name"] = FireFields.Text(campaign.Name), ["status"] = FireFields.Number(0) })) },
            var p when p == farm + "/plots" => new JsonObject { ["documents"] = new JsonArray(Document($"{farm}/plots/{plot.Id:N}", new() { ["name"] = FireFields.Text(plot.Name), ["trees"] = FireFields.Number(plot.OliveTrees), ["variety"] = FireFields.Text(plot.Variety), ["notes"] = FireFields.Text(plot.Notes) })) },
            var p when p == farm + "/cooperatives" => new JsonObject { ["documents"] = new JsonArray(Document($"{farm}/cooperatives/{cooperative.Id:N}", new() { ["name"] = FireFields.Text(cooperative.Name), ["notes"] = FireFields.Text("") })) },
            _ => new JsonObject { ["documents"] = new JsonArray() }
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") });
    }
    private JsonObject Member() => Document(farm + "/members/mvvm-user", new() { ["uid"] = FireFields.Text("mvvm-user"), ["role"] = FireFields.Number(2), ["email"] = FireFields.Text("mvvm@example.invalid") });
}
sealed class RejectLoginHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":{\"message\":\"INVALID_LOGIN_CREDENTIALS\"}}", Encoding.UTF8, "application/json") });
    }
}
