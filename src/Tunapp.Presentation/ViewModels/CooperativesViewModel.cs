using Tunapp.Core;

namespace Tunapp;

public sealed class CooperativesViewModel(ManagementContext context, IAppNavigation navigation) : RecordsViewModel<Cooperative>("Cooperativas", context, navigation)
{
    protected override IReadOnlyList<Cooperative> Read() => Context.Cooperatives();
    protected override string[] SearchValues(Cooperative item) => [item.Name, item.Notes];
    protected override bool CanAdd => Context.Role() >= Role.Operator;
    protected override string? Subtitle => Context.CurrentCampaign is { } campaign ? $"Campaña {campaign.Name}" : "Selecciona una campaña para ver los totales.";
    private HarvestTotals Totals(Cooperative item) => Context.CurrentCampaign is { } campaign
        ? HarvestTotals.ForCooperative(Context.Works(), item.BusinessId, campaign.Id, item.Id) : new(0, 0, 0, 0);
    protected override RecordRow Card(Cooperative item) => Summary(item.Name,
        Context.CurrentCampaign is null ? item.Notes.ReplaceLineEndings(" ") : $"{Totals(item).Total:0.###} kg entregados", "icon_cooperative.png");
    protected override Task OpenAsync(Cooperative item) => Navigation.PushAsync(new DetailViewModel("Cooperativa", Navigation, page =>
    {
        var cooperative = Context.Cooperatives().Single(x => x.Id == item.Id);
        page.Heading(cooperative.Name);
        if (Context.CurrentCampaign is { } campaign)
        {
            var totals = Totals(cooperative);
            page.Value("Campaña", campaign.Name);
            foreach (var quality in new[] { "Árbol", "Suelo" })
            {
                var price = Context.Prices().SingleOrDefault(x => x.BusinessId == cooperative.BusinessId && x.CampaignId == campaign.Id && x.CooperativeId == cooperative.Id && x.Quality == quality);
                page.Value($"Precio {quality}", price is null ? "Sin precio indicado" : $"{price.Price:0.####} €/kg");
                if (Context.Role() >= Role.Operator && campaign.Status == CampaignStatus.Open)
                    page.Action($"Guardar precio {quality}", () =>
                    {
                        var form = new FormViewModel($"Precio {quality} - {campaign.Name}", Navigation);
                        var value = form.Field("Precio (euros/kg)", price?.Price.ToString(System.Globalization.CultureInfo.GetCultureInfo("es-ES")), "Ej. 0,7525", FieldKind.Numeric);
                        form.Submit("Guardar precio", () => Context.SavePrice(campaign.Id, cooperative.Id, quality, FormViewModel.Area(value) ?? throw new ArgumentException("Indica el precio.")));
                        return page.Navigator.PushAsync(form);
                    });
            }
            page.Value("Total entregado", $"{totals.Total:0.###} kg");
            page.Value("Árbol", $"{totals.Tree:0.###} kg");
            page.Value("Suelo", $"{totals.Ground:0.###} kg");
            page.Value("Sin calidad indicada", $"{totals.Unspecified:0.###} kg");
        }
        page.Value("Notas", cooperative.Notes);
    }));
    protected override Task AddAsync()
    {
        var form = new FormViewModel("Añadir cooperativa", Navigation);
        var name = form.Field("Nombre", placeholder: "Nombre de la cooperativa"); name.MaxLength = 160;
        var notes = form.Notes(); var id = Guid.NewGuid();
        form.Submit("Guardar cooperativa", () => Context.CreateCooperative(new(id, name.Text ?? "", notes.Text ?? "")));
        return Navigation.PushAsync(form);
    }
}
