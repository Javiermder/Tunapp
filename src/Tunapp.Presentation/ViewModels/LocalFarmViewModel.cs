using System.Globalization;
using Tunapp.Core;

namespace Tunapp;

public sealed class LocalFarmViewModel : ScreenViewModel
{
    private readonly FarmService store;
    private bool loaded;
    private string section = "Resumen";
    public LocalFarmViewModel(FarmService store, IAppNavigation navigation) : base(navigation) { this.store = store; Title = "En este móvil"; }
    public override async Task ActivateAsync()
    {
        if (loaded) { Render(); return; }
        try { await store.LoadAsync(); loaded = true; Render(); }
        catch (Exception) { Items.Clear(); Text("No se pudieron abrir los datos locales. Conserva los archivos tunapp-v1.json y .bak; no se han sobrescrito."); }
    }
    public override void Refresh() => Render();
    private void ActionButton(string title, Func<Task> action) => Action(title, async () => { await action(); Render(); });
    private void BusinessForm()
    {
        Text("Nueva explotación", true);
        var name = Field("Nombre de la explotación");
        var admin = Field("Tu nombre (administrador local)");
        ActionButton("Crear explotación en este dispositivo", () => store.CreateBusinessAsync(name.Text ?? "", admin.Text ?? ""));
    }
    private async Task OpenBusinessForm()
    {
        var form = new FormViewModel("Nueva explotación", Navigation);
        var name = form.Field("Nombre", placeholder: "Nombre de tu explotación");
        var admin = form.Field("Tu nombre", placeholder: "Nombre del administrador");
        form.Submit("Crear explotación", () => store.CreateBusinessAsync(name.Text ?? "", admin.Text ?? ""));
        await Navigation.PushAsync(form);
    }
    private void Render()
    {
        Items.Clear();
        if (store.Business is null) { BusinessForm(); return; }
        if (section == "Resumen") { Home(); return; }
        Action("‹  Inicio", () => { section = "Resumen"; Render(); return Task.CompletedTask; });
        var businesses = store.Businesses.ToList();
        if (section == "Cuenta" && businesses.Count > 1)
        {
            var picker = Choice("Explotación", businesses, store.Business);
            picker.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(FieldViewModel.SelectedItem) && picker.SelectedItem is FarmBusiness selected && selected.Id != store.Business?.Id)
                    await ExecuteAsync(async () => { await store.SelectBusinessAsync(selected.Id); Render(); });
            };
        }
        Text("Mi cuenta", true);
        Text("Estos datos se guardan solo en este m\u00f3vil. Para sincronizar con Firebase, vuelve al inicio e inicia sesi\u00f3n.");
        ActionButton("Volver al inicio de Tunapp", async () => await Navigation.PopAsync());
        ActionButton("Nueva explotaci\u00f3n", OpenBusinessForm);
    }
    private async Task OpenSection(string destination)
    {
        try
        {
            if (ManagementViewModels.Create(destination, Management(), Navigation) is { } page)
            {
                await Navigation.PushAsync(page);
            }
            else { section = destination; Render(); }
        }
        catch (Exception ex) { await DisplayAlertAsync("No se pudo abrir", ex.Message, "Aceptar"); }
    }
    private ManagementContext Management()
    {
        var business = store.Business?.Id ?? throw new InvalidOperationException("Selecciona una explotación.");
        T Read<T>(Func<T> read)
        {
            if (store.Business?.Id != business) throw new UnauthorizedAccessException("La explotación seleccionada ha cambiado.");
            return read();
        }
        Task Change(Func<Task> save) => Read(save);
        return new()
        {
            Role = () => Read(() => store.CurrentRole), Plots = () => Read(() => store.Plots), Campaigns = () => Read(() => store.Campaigns),
            Cooperatives = () => Read(() => store.Cooperatives), Works = () => Read(() => store.FieldWorks),
            Team = () => Read(() => store.Members.Select(x => new TeamPerson(x.UserId.ToString(), x.Name, x.Role, false)).ToList()),
            ActiveCampaign = () => Read(() => store.SelectedCampaign?.Id),
            SavePlot = (input, create) => Change(() => create
                ? store.CreatePlotAsync(input.Name, input.Hectares, input.OliveTrees, input.Variety, input.Notes)
                : store.UpdatePlotAsync(input.Id, input.Name, input.Hectares, input.OliveTrees, input.Variety, input.Notes)),
            CreateCampaign = input => Change(() => store.CreateCampaignAsync(input.Name, input.Start, input.End)),
            SelectCampaign = id => Change(() => store.SelectCampaignAsync(id)), SetClosed = (id, closed) => Change(() => store.SetCampaignClosedAsync(id, closed)),
            AnnualPlots = () => Read(() => store.CampaignPlots), Prices = () => Read(() => store.CooperativePrices),
            SavePrice = (campaign, cooperative, quality, price) => Change(() => store.SaveCooperativePriceAsync(campaign, cooperative, quality, price)),
            CreateCooperative = input => Change(() => store.CreateCooperativeAsync(input)), AddWork = kind => Change(() => OpenFieldWork(kind))
        };
    }
    private void Home()
    {
        var name = store.Members.FirstOrDefault()?.Name ?? "agricultor";
        if (string.IsNullOrWhiteSpace(name)) name = "agricultor";
        Items.Add(new DashboardItem(name, store.SelectedCampaign?.Name, $"{store.Business!.Name} · En este móvil",
            [new("Fincas", $"{store.Plots.Count} en tu explotación", "icon_farm.png", "#E9EEDB", Command(() => OpenSection("Fincas"))),
             new("Cooperativas", $"{store.Cooperatives.Count} registradas", "icon_cooperative.png", "#F6E9D9", Command(() => OpenSection("Cooperativas"))),
             new("Campañas", $"{store.Campaigns.Count} temporadas", "icon_campaign.png", "#E4EBE1", Command(() => OpenSection("Campañas"))),
             new("Historial", "Cosechas y tratamientos", "icon_history.png", "#E9E7F1", Command(OpenHistory)),
             new("Equipo", "Usuarios y permisos", "icon_team.png", "#E2ECF0", Command(() => OpenSection("Usuarios"))),
             new("Mi cuenta", "Datos del dispositivo", "icon_account.png", "#F1E8DF", Command(() => OpenSection("Cuenta")))],
            Command(() => OpenFieldWork(FieldWorkKind.Harvest)), Command(() => OpenFieldWork(FieldWorkKind.Treatment)),
            Command(() => OpenSection("Cuenta")), Command(() => OpenSection("Campañas")), Command(OpenHistory), store.FieldWorks.Where(x => x.Date == DateOnly.FromDateTime(DateTime.Today))));
    }
    private async Task OpenFieldWork(FieldWorkKind kind)
    {
        var campaign = store.SelectedCampaign;
        if (campaign is null || campaign.Status == CampaignStatus.Closed)
        {
            await Navigation.PushAsync(FieldWorkViewModel.Setup(Navigation, kind, "Elige una campaña abierta", "Así cada cosecha y tratamiento queda en su temporada.", "Ir a campañas",
                async () => { await Navigation.PopAsync(); await OpenSection("Campañas"); })); return;
        }
        if (store.Plots.Count == 0)
        {
            await Navigation.PushAsync(FieldWorkViewModel.Setup(Navigation, kind, "Añade tu primera finca", "Seleccionarás en qué finca has realizado el trabajo.", "Añadir finca",
                async () => { await Navigation.PopAsync(); await OpenSection("Fincas"); })); return;
        }
        await Navigation.PushAsync(new FieldWorkViewModel(Navigation, kind, campaign, store.Plots, store.Cooperatives, store.CreateFieldWorkAsync, store.CreateCooperativeAsync));
    }
    private async Task OpenHistory() => await Navigation.PushAsync(new HistoryViewModel(Management(), Navigation));
}
