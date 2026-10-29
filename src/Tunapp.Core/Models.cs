namespace Tunapp.Core;

public enum Role { Reader, Operator, Administrator }
public enum CampaignStatus { Open, Closed }
public sealed record FarmBusiness(Guid Id, string Name);
public sealed record Membership(Guid BusinessId, Guid UserId, string Name, Role Role);
public sealed record Campaign(Guid Id, Guid BusinessId, string Name, DateOnly? Start, DateOnly? End,
    CampaignStatus Status = CampaignStatus.Open)
{
    public override string ToString() => $"{Name} · {(Status == CampaignStatus.Open ? "Abierta" : "Cerrada")}";
}
public sealed record Plot(Guid Id, Guid BusinessId, string Name, decimal? Hectares, int OliveTrees,
    string Variety, string Notes)
{
    public override string ToString() => Name;
}
// Los datos de la finca que pueden cambiar se guardan por campaña.
public sealed record CampaignPlot(Guid BusinessId, Guid CampaignId, Guid PlotId, decimal? Hectares,
    int OliveTrees, string Variety, string Notes);
// Identificador estable para la futura entrega idempotente al servidor. No implica sincronización real.
public sealed record PendingChange(Guid Id, Guid BusinessId, string Entity, Guid EntityId, DateTimeOffset CreatedAt);
public sealed class LocalData
{
    public int SchemaVersion { get; set; } = 1;
    public Guid DeviceUserId { get; set; } = Guid.NewGuid();
    public Guid? SelectedBusinessId { get; set; }
    public Guid? SelectedCampaignId { get; set; }
    public List<FarmBusiness> Businesses { get; set; } = [];
    public List<Membership> Members { get; set; } = [];
    public List<Campaign> Campaigns { get; set; } = [];
    public List<Plot> Plots { get; set; } = [];
    public List<CampaignPlot> CampaignPlots { get; set; } = [];
    public List<PendingChange> PendingChanges { get; set; } = [];
    public List<Cooperative> Cooperatives { get; set; } = [];
    public List<CooperativePrice> CooperativePrices { get; set; } = [];
    public List<FieldWork> FieldWorks { get; set; } = [];
}

public static class CampaignPlotsView
{
    // La finca es permanente; solo se materializan sus datos anuales al editarlos.
    public static List<CampaignPlot> ForCampaign(IEnumerable<Plot> plots, IEnumerable<CampaignPlot> saved, Guid business, Guid campaign)
    {
        var annual = saved.Where(x => x.BusinessId == business && x.CampaignId == campaign).ToDictionary(x => x.PlotId);
        return plots.Where(x => x.BusinessId == business).Select(p => annual.GetValueOrDefault(p.Id)
            ?? new CampaignPlot(business, campaign, p.Id, p.Hectares, p.OliveTrees, p.Variety, p.Notes)).ToList();
    }
}
