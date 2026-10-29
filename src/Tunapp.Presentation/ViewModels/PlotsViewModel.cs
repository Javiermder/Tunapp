using System.Globalization;
using Tunapp.Core;

namespace Tunapp;

public sealed class PlotsViewModel(ManagementContext context, IAppNavigation navigation) : RecordsViewModel<Plot>("Fincas", context, navigation)
{
    protected override IReadOnlyList<Plot> Read() => Context.CampaignPlots();
    protected override string[] SearchValues(Plot item) => [item.Name, item.Variety, item.Notes];
    protected override bool CanAdd => Context.Role() >= Role.Operator;
    private static string Info(Plot plot) => string.Join(" · ", new[] {
        plot.Hectares is { } area ? $"{area:0.####} ha" : null, $"{plot.OliveTrees} olivos", plot.Variety
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
    protected override string? Subtitle => Context.CurrentCampaign is { } campaign ? $"Campaña {campaign.Name}  ·  {Context.CampaignPlots().Sum(x => (long)x.OliveTrees):N0} olivos" : "Selecciona una campaña para ver los kilos recogidos.";
    private decimal Harvested(Plot plot) => Context.CurrentCampaign is { } campaign
        ? Context.Works().Where(x => x.BusinessId == plot.BusinessId && x.CampaignId == campaign.Id
            && x.PlotId == plot.Id && x.Kind == FieldWorkKind.Harvest && x.Unit == "kg").Sum(x => x.Quantity)
        : 0;
    protected override RecordRow Card(Plot item) => Summary(item.Name, Info(item), "icon_farm.png",
        Context.CurrentCampaign is null ? null : $"{Harvested(item):0.###} kg recogidos");
    protected override Task OpenAsync(Plot item) => Navigation.PushAsync(new DetailViewModel("Finca", Navigation, page =>
    {
        var plot = Context.CampaignPlots().Single(x => x.Id == item.Id);
        page.Heading(plot.Name); page.Value("Datos de la finca", Info(plot)); page.Value("Notas", plot.Notes);
        if (Context.CurrentCampaign is { } campaign)
        {
            page.Value("Campaña activa", campaign.Name);
            var stats = PlotStatistics.Calculate(plot, campaign.Id, Context.Works(), Context.Prices());
            static string Ratio(decimal? value, string unit) => value is { } v ? $"{v:0.###} {unit}" : "Sin datos";
            var incomePartial = stats.UnvaluedKilograms > 0 ? " (parciales)" : "";
            var costPartial = stats.UnknownCosts > 0 ? " (parcial)" : "";
            page.Value("Total recogido", $"{stats.Kilograms:0.###} kg");
            page.Value("Kilos por olivo", Ratio(stats.KilogramsPerTree, "kg/olivo"));
            page.Value("Kilos por hectárea", Ratio(stats.KilogramsPerHectare, "kg/ha"));
            page.Value("Coste de tratamientos" + costPartial, $"{stats.TreatmentCost:0.00} €");
            page.Value("Coste por olivo" + costPartial, Ratio(stats.CostPerTree, "€/olivo"));
            if (stats.UnknownCosts > 0) page.Value("Tratamientos sin coste indicado", stats.UnknownCosts.ToString());
            page.Value("Ingresos estimados" + incomePartial, $"{stats.Income:0.00} €");
            page.Value("Ingresos por olivo" + incomePartial, Ratio(stats.IncomePerTree, "€/olivo"));
            page.Value("Kilos pendientes de valorar", $"{stats.UnvaluedKilograms:0.###} kg");
            page.Value("Saldo estimado antes de otros gastos" + (incomePartial.Length > 0 || costPartial.Length > 0 ? " (parcial)" : ""), $"{stats.Income - stats.TreatmentCost:0.00} €");
            page.Value("Estimaciones", "Los importes no representan liquidaciones de la cooperativa.");
            var works = Context.Works().Where(x => x.BusinessId == plot.BusinessId && x.CampaignId == campaign.Id && x.PlotId == plot.Id).ToList();
            foreach (var group in works.Where(x => x.Kind == FieldWorkKind.Harvest).GroupBy(x => new { x.CooperativeId, x.Quality }))
            {
                var coop = Context.Cooperatives().FirstOrDefault(x => x.Id == group.Key.CooperativeId)?.Name ?? "Sin cooperativa";
                page.Value($"{coop} / {group.Key.Quality ?? "Sin calidad indicada"}", $"{group.Sum(x => x.Quantity):0.###} kg");
            }
            foreach (var treatment in works.Where(x => x.Kind == FieldWorkKind.Treatment).OrderByDescending(x => x.Date))
                page.Value($"{treatment.Date:dd/MM/yyyy} - {treatment.Description}", $"{treatment.Quantity:0.###} {treatment.Unit} / " + (treatment.Cost is { } c ? $"{c:0.00} €" : "Sin coste indicado"));
        }
        if (Context.Role() >= Role.Operator) page.Action("Editar finca", () => page.Navigator.PushAsync(Editor(Context.Plots().Single(x => x.Id == plot.Id))));
    }));
    protected override Task AddAsync() => Navigation.PushAsync(Editor(null));
    public FormViewModel Editor(Plot? plot)
    {
        var form = new FormViewModel(plot is null ? "Añadir finca" : "Editar finca", Navigation);
        var name = form.Field("Nombre", plot?.Name, "Ej. La loma"); name.MaxLength = 160;
        var area = form.Field("Superficie (ha, opcional)", plot?.Hectares?.ToString(CultureInfo.GetCultureInfo("es-ES")), "Ej. 2,5", FieldKind.Numeric);
        var trees = form.Field("Número de olivos", plot?.OliveTrees.ToString(), "Ej. 315", FieldKind.Numeric);
        var variety = form.Field("Variedad", plot?.Variety, "Ej. Hojiblanca"); variety.MaxLength = 200;
        var notes = form.Notes(plot?.Notes); var id = plot?.Id ?? Guid.NewGuid();
        form.Submit("Guardar finca", () => Context.SavePlot(new(id, name.Text ?? "", FormViewModel.Area(area), FormViewModel.Trees(trees), variety.Text ?? "", notes.Text ?? ""), plot is null));
        return form;
    }
}
