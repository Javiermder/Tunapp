using Tunapp.Core;

namespace Tunapp;



// Adaptadores de los dos almacenes; los listados y formularios comparten la misma interfaz.
public sealed record TeamPerson(string Id, string Name, Role Role, bool CanManage);
public sealed class ManagementContext
{
    public required Func<IReadOnlyList<CooperativePrice>> Prices { get; init; }
    public required Func<IReadOnlyList<CampaignPlot>> AnnualPlots { get; init; }
    public required Func<Guid, Guid, string, decimal, Task> SavePrice { get; init; }
    public IReadOnlyList<Plot> CampaignPlots() => CurrentCampaign is null ? Plots() : Plots().Select(p => AnnualPlots().FirstOrDefault(a => a.BusinessId == p.BusinessId && a.CampaignId == CurrentCampaign.Id && a.PlotId == p.Id) is { } a
        ? p with { Hectares = a.Hectares, OliveTrees = a.OliveTrees, Variety = a.Variety, Notes = a.Notes } : p).ToList();
    public required Func<Role> Role { get; init; }
    public required Func<IReadOnlyList<Plot>> Plots { get; init; }
    public required Func<IReadOnlyList<Campaign>> Campaigns { get; init; }
    public required Func<IReadOnlyList<Cooperative>> Cooperatives { get; init; }
    public required Func<IReadOnlyList<TeamPerson>> Team { get; init; }
    public required Func<IReadOnlyList<FieldWork>> Works { get; init; }
    public required Func<Guid?> ActiveCampaign { get; init; }
    public required Func<PlotInput, bool, Task> SavePlot { get; init; }
    public required Func<CampaignInput, Task> CreateCampaign { get; init; }
    public required Func<Guid, Task> SelectCampaign { get; init; }
    public required Func<Guid, bool, Task> SetClosed { get; init; }
    public required Func<CooperativeInput, Task> CreateCooperative { get; init; }
    public Func<string, Role, Task>? Invite { get; init; }
    public Func<string, Task>? CancelInvitation { get; init; }
    public Func<string, Role?, Task>? SetRole { get; init; }
    public required Func<FieldWorkKind, Task> AddWork { get; init; }
    public Campaign? CurrentCampaign => Campaigns().FirstOrDefault(x => x.Id == ActiveCampaign());
    public static string RoleName(Role role) => role switch { Core.Role.Administrator => "Administrador", Core.Role.Operator => "Operario", _ => "Solo lectura" };
}
