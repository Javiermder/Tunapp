using Tunapp.Core;

namespace Tunapp;

public sealed class CampaignsViewModel(ManagementContext context, IAppNavigation navigation) : RecordsViewModel<Campaign>("Campañas", context, navigation)
{
    protected override IReadOnlyList<Campaign> Read() => Context.Campaigns();
    private static string Status(Campaign campaign) => campaign.Status == CampaignStatus.Open ? "Abierta" : "Cerrada";
    private static string Dates(Campaign campaign) => string.Join(" · ", new[] {
        campaign.Start is { } start ? $"Desde {start:dd/MM/yyyy}" : null,
        campaign.End is { } end ? $"Hasta {end:dd/MM/yyyy}" : null }.Where(x => x is not null));
    protected override string[] SearchValues(Campaign item) => [item.Name, Status(item), Dates(item), item.Id == Context.ActiveCampaign() ? "Activa" : ""];
    protected override bool CanAdd => Context.Role() == Role.Administrator;
    protected override RecordRow Card(Campaign item) => Summary(item.Name, string.Join(" · ", new[] { Status(item), Dates(item) }.Where(x => x.Length > 0)), "icon_campaign.png", item.Id == Context.ActiveCampaign() ? "Campaña activa" : null);
    protected override Task AddAsync()
    {
        var form = new FormViewModel("Añadir campaña", Navigation);
        var name = form.Field("Nombre", placeholder: "Ej. 2026/2027"); name.MaxLength = 160;
        var start = form.Date("Inicio (opcional)"); var end = form.Date("Fin (opcional)"); var id = Guid.NewGuid();
        form.Submit("Guardar campaña", () => Context.CreateCampaign(new(id, name.Text ?? "", start.Date is { } from ? DateOnly.FromDateTime(from) : null, end.Date is { } to ? DateOnly.FromDateTime(to) : null)));
        return Navigation.PushAsync(form);
    }
    protected override Task OpenAsync(Campaign item) => Navigation.PushAsync(new DetailViewModel("Campaña", Navigation, page =>
    {
        var campaign = Context.Campaigns().Single(x => x.Id == item.Id);
        page.Heading(campaign.Name); page.Value("Estado", Status(campaign)); page.Value("Fechas", Dates(campaign));
        if (campaign.Id == Context.ActiveCampaign()) page.Value("Selección", "Campaña activa");
        else page.Action("Usar esta campaña", async () => { await Context.SelectCampaign(campaign.Id); page.Reload(); });
        if (Context.Role() == Role.Administrator)
        {
            var closed = campaign.Status == CampaignStatus.Open;
            page.Action(closed ? "Cerrar campaña" : "Reabrir campaña", async () =>
            {
                if (!await page.Confirm("Cambiar estado", closed ? "Se conservarán los datos y se bloquearán nuevos trabajos en esta campaña." : "Se permitirán modificaciones de nuevo.", "Confirmar", "Cancelar")) return;
                await Context.SetClosed(campaign.Id, closed); page.Reload();
            });
        }
    }));
}
