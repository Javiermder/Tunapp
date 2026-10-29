using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tunapp.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tunapp;

public sealed partial class CloudViewModel : ScreenViewModel, IAsyncDisposable
{
    private const string SessionKey = "tunapp.firebase.session.v1";
    private readonly IAppEnvironment environment;
    private readonly HttpClient http;
    private readonly FirebaseAuthClient auth;
    private DirectFarmService? cloud;
    private List<CloudBusiness> businesses = [];
    private CloudSnapshot? snapshot;
    private Guid? selectedCampaign;
    private bool initialized;
    private string section = "Resumen";
    [ObservableProperty] public partial string Notice { get; set; } = "Conecta a Internet para acceder a Tunapp.";
    private bool connected => environment.IsConnected;
    private bool connectionVisible;
    private DateTimeOffset lastSync;

    public CloudViewModel(IAppNavigation navigation, IAppEnvironment environment, HttpClient http, FirebaseAuthClient auth) : base(navigation)
    {
        Title = "Inicio"; this.environment = environment; this.http = http; this.auth = auth;
        auth.SessionChanged += PersistSessionAsync;
    }
    private Task PersistSessionAsync(FirebaseSession? session) => environment.WriteSessionAsync(SessionKey, session is null ? null : JsonSerializer.Serialize(session));
    public async ValueTask DisposeAsync()
    {
        auth.SessionChanged -= PersistSessionAsync;
        if (cloud is not null) await cloud.DisposeAsync();
    }
    public override async Task ActivateAsync()
    {
        if (!initialized)
        {
            initialized = true;
            try
            {
                var saved = await environment.ReadSessionAsync(SessionKey);
                if (saved is not null && JsonSerializer.Deserialize<FirebaseSession>(saved) is { } session)
                { await auth.RestoreAsync(session, refresh: false); await OpenCloudAsync(); }
            }
            catch (Exception ex) { Notice = ex is HttpRequestException or TaskCanceledException ? "No se pudo conectar con Firebase. Comprueba tu conexión." : ex.Message; }
        }
        Render(); await BackgroundSyncAsync();
    }
    public override void Refresh() => Render();
    [RelayCommand]
    private async Task ConnectionChangedAsync()
    {
        if (connected) { lastSync = default; await BackgroundSyncAsync(); }
        else await ShowConnectionAsync();
    }
    private async Task OpenCloudAsync()
    {
        if (cloud is not null) await cloud.DisposeAsync();
        cloud = null; snapshot = null; businesses = []; selectedCampaign = null;
        var session = auth.Session ?? throw new UnauthorizedAccessException("Inicia sesión.");
        cloud = await DirectFarmService.OpenAsync(Path.Combine(environment.DataDirectory, "accounts"), new FirestoreRest(http, auth, session.UserId, "tunapp-j27"), auth);
        
        await SyncAsync();
    }
    private void RefreshSelection()
    {
        if (cloud is null) return;
        var selected = snapshot?.Business.Id;
        businesses = cloud.Businesses;
        var business = businesses.FirstOrDefault(x => x.Id == selected) ?? businesses.FirstOrDefault();
        snapshot = business is null ? null : cloud.Snapshot(business.Id);
        if (snapshot is null) selectedCampaign = null;
        else if (!snapshot.Campaigns.Any(x => x.Id == selectedCampaign)) selectedCampaign = snapshot.Campaigns.FirstOrDefault()?.Id;
    }
    private async Task SyncAsync()
    {
        if (cloud is null) return;
        try
        {
            var outcome = await cloud.SyncAsync(); Notice = outcome.Message;
            
            if (!outcome.Connected) throw new IOException(outcome.Message);
            if (outcome.Connected) lastSync = DateTimeOffset.UtcNow;
        }
        
        finally { RefreshSelection(); }
    }
    [RelayCommand]
    private async Task BackgroundSyncAsync()
    {
        if (IsBusy || cloud is null || auth.Session is null) return;
        if (!connected)
        {
             Notice = "Sin conexión. Conecta a Internet para continuar.";
            await ShowConnectionAsync(); return;
        }
        if (connected && DateTimeOffset.UtcNow - lastSync < TimeSpan.FromSeconds(60)) return;
        IsBusy = true; var selected = (snapshot?.Business.Id, snapshot?.Business.Role);
        try
        {
            await SyncAsync();
            await HideConnectionAsync();
            Navigation.RefreshCurrent();
            // Mantener los campos que se estén rellenando. Si se pierde acceso, retirar la vista.
            if (selected != (snapshot?.Business.Id, snapshot?.Business.Role) || auth.Session is null || section == "Resumen") Render();
        }
        catch (Exception ex) {  Notice = ex.Message; await ShowConnectionAsync(); }
        finally { IsBusy = false; }
    }
    private async Task SaveAsync(Func<Task> save)
    {
        RequireConnection();
        try
        {
            await save(); RefreshSelection(); Notice = "Guardado en Firebase.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
             Notice = "No se pudo confirmar el guardado. Comprueba tu conexión y actualiza antes de reintentar.";
            await ShowConnectionAsync(); throw new IOException(Notice, ex);
        }
    }
    private void RequireConnection()
    {
        if (!connected)
            throw new IOException("Necesitas conexión a Internet para continuar.");
    }
    private async Task ShowConnectionAsync()
    {
        if (!connected) Notice = "Sin conexión. Conecta a Internet para continuar.";
        Render();
        if (connected) { await HideConnectionAsync(); return; }
        if (!Navigation.HasChildPage || connectionVisible) return;
        connectionVisible = true;
        try
        {
            await Navigation.ShowConnectionAsync(new ConnectionViewModel(Navigation,
                async () => { await SyncAsync(); if (!connected) throw new IOException(Notice); await HideConnectionAsync(); Render(); },
                async () => { await HideConnectionAsync(); await Navigation.PopToRootAsync(); Render(); }));
        }
        catch { connectionVisible = false; throw; }
    }
    private async Task HideConnectionAsync()
    {
        if (!connectionVisible) return;
        await Navigation.HideConnectionAsync(); connectionVisible = false;
    }
    private void Button(string title, Func<Task> action) => Action(title, async () =>
    {
        var previousAccess = (snapshot?.Business.Id, snapshot?.Business.Role);
        try { await action(); Render(); }
        catch
        {
            RefreshSelection();
            if (!connected || auth.Session is null || previousAccess != (snapshot?.Business.Id, snapshot?.Business.Role)) Render();
            throw;
        }
    });
    private static string RoleText(Role role) => role switch { Role.Administrator => "Administrador", Role.Operator => "Operario", _ => "Solo lectura" };
    private void Render()
    {
        Items.Clear();
        if (auth.Session is null)
        {
            Text("Tunapp", true);
            Text("Tu campo, siempre contigo.");
            Text("Accede a tu explotación", true);
            var email = Field("Correo electrónico", kind: FieldKind.Email);
            var password = Field("Contraseña", kind: FieldKind.Password);
            async Task Login(bool register)
            {
                if (register) await auth.SignUpAsync(email.Text ?? "", password.Text ?? "");
                else await auth.SignInAsync(email.Text ?? "", password.Text ?? "");
                password.Text = ""; await OpenCloudAsync();
                await auth.RefreshProfileAsync(); await SyncAsync();
            }
            Button("Iniciar sesión", () => Login(false)); Button("Crear mi cuenta", () => Login(true));
            Button("Recuperar contraseña", async () =>
            {
                await auth.ResetPasswordAsync(email.Text ?? "");
                await DisplayAlertAsync("Recuperar contraseña", "Si existe una cuenta con ese correo, recibirás las instrucciones.", "Aceptar");
            });
            return;
        }
        if (!connected)
        {
            Text("Conexión necesaria", true); Text(Notice);
            Button("Reintentar", async () => { if (cloud is null) await OpenCloudAsync(); else await SyncAsync(); await HideConnectionAsync(); });
            if (cloud?.Pending.Count > 0) { Text("Recuperar registros anteriores", true); PendingForm(); }
            Button("Cerrar sesión", async () => { await auth.SignOutAsync();  });
            return;
        }
        if (section == "Resumen" && cloud is not null && snapshot is not null) { Home(); return; }
        if (section != "Resumen")
        {
            Action("‹  Inicio", () => { section = "Resumen"; Render(); return Task.CompletedTask; });
        }
        if (section == "Cuenta")
        {
            Text("Mi cuenta", true); Text(auth.Session.Email);
            Button("Actualizar datos", SyncAsync);
            if (cloud?.Pending.Count > 0)
                Button("Revisar registros anteriores", () => { section = "Recuperación"; return Task.CompletedTask; });
            Button("Nueva explotación", async () =>
            {
                var form = new FormViewModel("Nueva explotación", Navigation); var name = form.Field("Nombre", placeholder: "Nombre de tu explotación"); var id = Guid.NewGuid();
                form.Submit("Crear explotación", async () => { await SaveAsync(() => cloud!.CreateBusinessAsync(new(id, name.Text ?? ""))); snapshot = cloud!.Snapshot(id); selectedCampaign = null; });
                await Navigation.PushAsync(form);
            });
        }
        if (section == "Cuenta") Button("Cerrar sesión", async () =>
        {
            await auth.SignOutAsync();
            if (cloud is not null) await cloud.DisposeAsync(); cloud = null; snapshot = null; businesses = []; selectedCampaign = null;
        });
        if (cloud is null) { Text(Notice); Button("Abrir mis datos", OpenCloudAsync); return; }
        if (section == "Recuperación") { PendingForm(); return; }
        if (section == "Cuenta" && !auth.Session.EmailVerified)
        {
            Text("Verifica tu correo para aceptar invitaciones de otras explotaciones.");
            Button("Enviar correo de verificación", async () => { await auth.SendVerificationEmailAsync(); await DisplayAlertAsync("Verificar correo", "Abre el enlace del correo recibido y después pulsa Ya he verificado mi correo.", "Aceptar"); });
            Button("Ya he verificado mi correo", async () => { await auth.RefreshProfileAsync(); await SyncAsync(); if (!auth.Session!.EmailVerified) await DisplayAlertAsync("Correo pendiente", "Firebase todavía no confirma la verificación.", "Aceptar"); });
        }
        foreach (var invitation in section == "Cuenta" ? cloud.Invitations : [])
        {
            Text($"Invitación: {invitation.FarmName} · {RoleText(invitation.Role)}");
            Button("Aceptar invitación", async () => { await cloud.AcceptInvitationAsync(invitation); await SyncAsync(); });
        }
        if (businesses.Count > 1 && section == "Cuenta")
        {
            var picker = Choice("Explotación", businesses, businesses.FirstOrDefault(x => x.Id == snapshot?.Business.Id));
            picker.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(FieldViewModel.SelectedItem) || IsBusy || picker.SelectedItem is not CloudBusiness selected || selected.Id == snapshot?.Business.Id) return;
                snapshot = cloud.Snapshot(selected.Id); selectedCampaign = snapshot?.Campaigns.FirstOrDefault()?.Id; Render();
            };
        }
        if (snapshot is null) { BusinessForm(); return; }
        if (section != "Cuenta") Home();
    }
    private async Task OpenSection(string destination)
    {
        try
        {
            RequireConnection();
            if (ManagementViewModels.Create(destination, Management(), Navigation) is { } page)
            {
                await Navigation.PushAsync(page);
            }
            else { section = destination; Render(); }
        }
        catch (Exception ex)
        {
            if (!connected)
            {  Notice = ex.Message; await ShowConnectionAsync(); }
            else await DisplayAlertAsync("No se pudo abrir", ex.Message, "Aceptar");
        }
    }
    private ManagementContext Management()
    {
        var service = cloud ?? throw new InvalidOperationException("Inicia sesión.");
        var business = snapshot?.Business.Id ?? throw new InvalidOperationException("Selecciona una explotación.");
        var account = auth.Session?.UserId;
        CloudSnapshot State()
        {
            RequireConnection();
            if (cloud != service || snapshot?.Business.Id != business || auth.Session?.UserId != account)
                throw new UnauthorizedAccessException("La cuenta o explotación seleccionada ha cambiado.");
            return service.Snapshot(business) ?? throw new UnauthorizedAccessException("Ya no tienes acceso a esta explotación.");
        }
        return new()
        {
            Role = () => State().Business.Role, Plots = () => State().Plots, Campaigns = () => State().Campaigns,
            Cooperatives = () => { State(); return service.Cooperatives(business); },
            Works = () => { State(); return selectedCampaign is { } campaign ? service.FieldWorks(business, campaign) : []; },
            Team = () => State().Members.Select(x => new TeamPerson(x.UserId, x.Email, x.Role, service.Owner(business) != x.UserId)).ToList(),
            ActiveCampaign = () => { State(); return selectedCampaign; },
            SavePlot = (input, create) => { State(); return SaveAsync(() => service.SavePlotAsync(business, input, create)); },
            CreateCampaign = async input => { State(); await SaveAsync(() => service.CreateCampaignAsync(business, input)); selectedCampaign = input.Id; },
            SelectCampaign = id =>
            {
                if (!State().Campaigns.Any(x => x.Id == id)) throw new UnauthorizedAccessException("Campaña no autorizada.");
                selectedCampaign = id; return Task.CompletedTask;
            },
            SetClosed = (id, closed) => { State(); return SaveAsync(() => service.SetClosedAsync(business, id, closed)); },
            AnnualPlots = () => State().CampaignPlots,
            Prices = () => { State(); return selectedCampaign is { } c ? service.CooperativePrices(business, c) : []; },
            SavePrice = (campaign, cooperative, quality, price) => { State(); return SaveAsync(() => service.SaveCooperativePriceAsync(business, campaign, cooperative, quality, price)); },
            CreateCooperative = input => { State(); return SaveAsync(() => service.CreateCooperativeAsync(business, input)); },
            Invite = async (email, role) => { State(); await service.InviteAsync(business, email, role); await SyncAsync(); },
            CancelInvitation = async email => { State(); await service.CancelInvitationAsync(business, email); },
            SetRole = async (id, role) => { var member = State().Members.Single(x => x.UserId == id); await service.SetMemberRoleAsync(business, member, role); await SyncAsync(); },
            AddWork = kind => { State(); return OpenFieldWork(kind); }
        };
    }
    private IReadOnlyList<FieldWork> ActiveWorks() => snapshot is { } state && selectedCampaign is { } campaign
        ? cloud!.FieldWorks(state.Business.Id, campaign) : [];
    private void Home()
    {
        var state = snapshot!;
        var name = auth.Session!.Email.Split('@')[0].Split('.', '_', '-')[0];
        name = string.IsNullOrWhiteSpace(name) ? "agricultor" : CultureInfo.GetCultureInfo("es-ES").TextInfo.ToTitleCase(name);
        var works = ActiveWorks(); var cooperatives = cloud!.Cooperatives(state.Business.Id);
        Items.Add(new DashboardItem(name, state.Campaigns.FirstOrDefault(x => x.Id == selectedCampaign)?.Name, state.Business.Name,
            [new("Fincas", $"{state.Plots.Count} en tu explotación", "icon_farm.png", "#E9EEDB", Command(() => OpenSection("Fincas"))),
             new("Cooperativas", $"{cooperatives.Count} registradas", "icon_cooperative.png", "#F6E9D9", Command(OpenCooperatives)),
             new("Campañas", $"{state.Campaigns.Count} temporadas", "icon_campaign.png", "#E4EBE1", Command(() => OpenSection("Campañas"))),
             new("Historial", "Cosechas y tratamientos", "icon_history.png", "#E9E7F1", Command(OpenHistory)),
             new("Equipo", "Usuarios y permisos", "icon_team.png", "#E2ECF0", Command(() => OpenSection("Usuarios"))),
             new("Mi cuenta", "Perfil y acceso", "icon_account.png", "#F1E8DF", Command(() => OpenSection("Cuenta")))],
            Command(() => OpenFieldWork(FieldWorkKind.Harvest)), Command(() => OpenFieldWork(FieldWorkKind.Treatment)),
            Command(() => OpenSection("Cuenta")), Command(() => OpenSection("Campañas")), Command(OpenHistory), works.Where(x => x.Date == DateOnly.FromDateTime(DateTime.Today))));
    }
    private async Task OpenFieldWork(FieldWorkKind kind)
    {
        if (snapshot is not { } state) return;
        try { RequireConnection(); state = snapshot ?? throw new UnauthorizedAccessException("Selecciona una explotación."); }
        catch (Exception ex) { Notice = ex.Message;  await ShowConnectionAsync(); return; }
        if (state.Business.Role < Role.Operator) { await DisplayAlertAsync("Solo lectura", "Un administrador puede darte permiso para registrar trabajos.", "Aceptar"); return; }
        var campaign = state.Campaigns.FirstOrDefault(x => x.Id == selectedCampaign);
        if (campaign is null || campaign.Status == CampaignStatus.Closed)
        {
            await Navigation.PushAsync(FieldWorkViewModel.Setup(Navigation, kind, "Elige una campaña abierta", "Así cada cosecha y tratamiento queda en su temporada.", "Ir a campañas",
                async () => { await Navigation.PopAsync(); await OpenSection("Campañas"); })); return;
        }
        if (state.Plots.Count == 0)
        {
            await Navigation.PushAsync(FieldWorkViewModel.Setup(Navigation, kind, "Añade tu primera finca", "Seleccionarás en qué finca has realizado el trabajo.", "Añadir finca",
                async () => { await Navigation.PopAsync(); await OpenSection("Fincas"); })); return;
        }
        var service = cloud!;
        var account = auth.Session?.UserId;
        Task SaveForCurrentAccount(Func<Task> action)
        {
            if (cloud != service || snapshot?.Business.Id != state.Business.Id || auth.Session?.UserId != account)
                throw new UnauthorizedAccessException("La cuenta o explotación seleccionada ha cambiado.");
            return SaveAsync(action);
        }
        await Navigation.PushAsync(new FieldWorkViewModel(Navigation, kind, campaign, state.Plots, service.Cooperatives(state.Business.Id),
            input => SaveForCurrentAccount(() => service.CreateFieldWorkAsync(state.Business.Id, input)),
            input => SaveForCurrentAccount(() => service.CreateCooperativeAsync(state.Business.Id, input))));
    }
    private async Task OpenCooperatives()
    {
        await OpenSection("Cooperativas"); await Task.CompletedTask;
    }
    private async Task OpenHistory()
    {
        if (snapshot is null) return;
        try { RequireConnection(); await Navigation.PushAsync(new HistoryViewModel(Management(), Navigation)); }
        catch (Exception ex) { Notice = ex.Message;  await ShowConnectionAsync(); }
    }
    private void BusinessForm()
    {
        Text("Nueva explotación compartida", true); var name = Field("Nombre de la explotación"); var id = Guid.NewGuid();
        Button("Crear explotación", async () =>
        {
            await SaveAsync(() => cloud!.CreateBusinessAsync(new(id, name.Text ?? "")));
            snapshot = cloud!.Snapshot(id); selectedCampaign = null;
        });
    }
    private static string Describe(FireDocument? document)
    {
        if(document is null)return "No existe en la nube.";
        var parts=new List<string>();
        foreach(var (key,label) in new[]{("name","Nombre"),("areaM2","Superficie (m²)"),("trees","Olivos"),("variety","Variedad"),("notes","Notas")})
            if(document.Fields[key] is JsonObject value)parts.Add($"{label}: {value.FirstOrDefault().Value}");
        if(document.Fields.ContainsKey("status"))parts.Add(document.Integer("status")==0 ? "Estado: Abierta" : "Estado: Cerrada");
        return string.Join("\n",parts);
    }
    private void PendingForm()
    {
        Text("Recuperación de datos",true); Text("Estos registros se crearon con la versión anterior. Se conservarán hasta confirmar su recuperación en Firebase.");
        foreach(var pending in cloud!.Pending)
        {
            Text($"{pending.Document().Fields["name"]?["stringValue"]?.ToString() ?? "Datos de campaña"} · {(pending.Error is null ? "Pendiente" : pending.Error)}");
            if(pending.ErrorKind=="conflict") Button("Revisar diferencias",async()=>
            {
                var remote=await cloud.ConflictAsync(pending.Sequence);
                await DisplayAlertAsync("Diferencias",$"EN ESTE MÓVIL\n{Describe(pending.Document())}\n\nEN LA NUBE\n{Describe(remote)}","Continuar");
                var choice=await DisplayActionSheetAsync("Resolver conflicto","Seguir pendiente",null,"Mantener mis datos","Usar datos de la nube");
                if(choice=="Mantener mis datos" && await DisplayAlertAsync("Confirmar cambios","Se enviarán tus datos sobre la versión de la nube que acabas de revisar. Si cambia de nuevo se pedirá otra revisión.","Confirmar","Cancelar")) {await cloud.ResolveAsync(pending.Sequence,remote,true);await SyncAsync();}
                else if(choice=="Usar datos de la nube" && await DisplayAlertAsync("Descartar cambio local","Se descartará este cambio pendiente y se usará la versión de la nube.","Confirmar","Cancelar"))await cloud.ResolveAsync(pending.Sequence,remote,false);
                RefreshSelection();
            });
            else if(pending.ErrorKind is not null) Button("Reintentar este cambio",async()=>{await cloud.RetryAsync(pending.Sequence);await SyncAsync();});
        }
        if(cloud.Pending.Count==0)Text("No hay cambios pendientes en este dispositivo.");
    }
}
