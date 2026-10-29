using Tunapp.Core;

namespace Tunapp;

public sealed class HistoryViewModel(ManagementContext context, IAppNavigation navigation) : RecordsViewModel<FieldWork>("Historial", context, navigation)
{
    protected override IReadOnlyList<FieldWork> Read() => Context.Works();
    protected override string? Subtitle => Context.CurrentCampaign is { } campaign ? $"Campaña {campaign.Name}" : "Selecciona una campaña en Inicio.";
    private string PlotName(FieldWork item) => Context.Plots().FirstOrDefault(x => x.Id == item.PlotId)?.Name ?? "Finca";
    private static string Kind(FieldWork item) => item.Kind == FieldWorkKind.Harvest ? "Cosecha" : "Tratamiento";
    private string CooperativeName(FieldWork item) => Context.Cooperatives().FirstOrDefault(x => x.Id == item.CooperativeId)?.Name ?? "Sin cooperativa";
    protected override string[] SearchValues(FieldWork item) => [item.Description, PlotName(item), Kind(item), CooperativeName(item), item.Quality ?? "", item.Date.ToString("dd/MM/yyyy"), item.Notes];
    protected override bool CanAdd => Context.Role() >= Role.Operator && Context.CurrentCampaign?.Status == CampaignStatus.Open;
    protected override RecordRow Card(FieldWork item) => Summary($"{Kind(item)} · {item.Description}", $"{PlotName(item)} · {item.Quantity:0.###} {item.Unit}" + (item.Kind == FieldWorkKind.Harvest ? $" · {CooperativeName(item)} · {item.Quality ?? "Sin calidad"}" : ""),
        item.Kind == FieldWorkKind.Harvest ? "icon_harvest.png" : "icon_treatment.png", item.Date.ToString("dd/MM/yyyy"));
    protected override async Task AddAsync()
    {
        var kind = await DisplayActionSheetAsync("Añadir registro", "Cancelar", null, "Cosecha", "Tratamiento");
        if (kind == "Cosecha") await Context.AddWork(FieldWorkKind.Harvest);
        else if (kind == "Tratamiento") await Context.AddWork(FieldWorkKind.Treatment);
    }
    protected override Task OpenAsync(FieldWork item) => Navigation.PushAsync(new DetailViewModel(Kind(item), Navigation, page =>
    {
        var work = Context.Works().Single(x => x.Id == item.Id);
        page.Heading(work.Description); page.Value("Finca", PlotName(work));
        page.Value("Campaña", Context.CurrentCampaign?.Name); page.Value("Fecha", work.Date.ToString("dd/MM/yyyy"));
        page.Value("Cantidad", $"{work.Quantity:0.###} {work.Unit}");
        if (work.Kind == FieldWorkKind.Harvest) page.Value("Calidad", work.Quality ?? "Sin indicar");
        page.Value("Cooperativa", Context.Cooperatives().FirstOrDefault(x => x.Id == work.CooperativeId)?.Name); page.Value("Notas", work.Notes);
    }));
}
