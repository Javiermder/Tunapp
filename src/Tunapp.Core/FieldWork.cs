namespace Tunapp.Core;

public enum FieldWorkKind { Harvest, Treatment }
public sealed record Cooperative(Guid Id, Guid BusinessId, string Name, string Notes)
{
    public override string ToString() => Name;
}
public sealed record FieldWork(Guid Id, Guid BusinessId, Guid CampaignId, Guid PlotId,
    FieldWorkKind Kind, DateOnly Date, string Description, decimal Quantity, string Unit,
    Guid? CooperativeId, string Notes, string? Quality = null, decimal? Cost = null);
public sealed record FieldWorkInput(Guid Id, Guid CampaignId, Guid PlotId, FieldWorkKind Kind,
    DateOnly Date, string Description, decimal Quantity, string Unit, Guid? CooperativeId, string Notes, string? Quality = null, decimal? Cost = null);
public sealed record CooperativeInput(Guid Id, string Name, string Notes);

public static class FieldWorkValidation
{
    public static void Validate(FieldWorkInput input)
    {
        if (input.Id == Guid.Empty || input.CampaignId == Guid.Empty || input.PlotId == Guid.Empty || !Enum.IsDefined(input.Kind))
            throw new ArgumentException("Selecciona una campaña y una finca.");
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 160)
            throw new ArgumentException(input.Kind == FieldWorkKind.Harvest ? "Indica el cultivo cosechado." : "Indica el tratamiento o producto.");
        if (input.Quantity <= 0 || input.Quantity > 100000000 || input.Quantity * 1000 != decimal.Truncate(input.Quantity * 1000))
            throw new ArgumentException("Indica una cantidad positiva con hasta tres decimales.");
        if (input.Unit is not ("kg" or "L" or "g" or "mL") || input.Kind == FieldWorkKind.Harvest && input.Unit != "kg")
            throw new ArgumentException("Selecciona una unidad válida.");
        if (input.Kind == FieldWorkKind.Treatment && input.CooperativeId is not null)
            throw new ArgumentException("La cooperativa solo corresponde a una cosecha.");
        if (input.Quality is not null && (input.Kind != FieldWorkKind.Harvest || input.Quality is not ("Árbol" or "Suelo")))
            throw new ArgumentException("Selecciona una calidad válida: Árbol o Suelo.");
        if (input.Cost is { } cost && (input.Kind != FieldWorkKind.Treatment || !Money.Valid(cost, 2)))
            throw new ArgumentException("Indica un coste no negativo con hasta dos decimales.");
        if (input.Notes is null || input.Notes.Length > 10000) throw new ArgumentException("Las notas son demasiado largas.");
    }
}

public sealed record HarvestTotals(decimal Total, decimal Tree, decimal Ground, decimal Unspecified)
{
    public static HarvestTotals ForCooperative(IEnumerable<FieldWork> works, Guid business, Guid campaign, Guid cooperative)
    {
        var harvests = works.Where(x => x.BusinessId == business && x.CampaignId == campaign
            && x.CooperativeId == cooperative && x.Kind == FieldWorkKind.Harvest).ToList();
        return new(harvests.Sum(x => x.Quantity), harvests.Where(x => x.Quality == "Árbol").Sum(x => x.Quantity),
            harvests.Where(x => x.Quality == "Suelo").Sum(x => x.Quantity), harvests.Where(x => x.Quality == null).Sum(x => x.Quantity));
    }
}
