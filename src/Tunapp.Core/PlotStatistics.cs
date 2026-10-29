namespace Tunapp.Core;

public sealed record CooperativePrice(Guid BusinessId, Guid CampaignId, Guid CooperativeId, string Quality, decimal Price);
public static class Money
{
    public static bool Valid(decimal value, int decimals) => value >= 0 && value <= 100000000 && decimal.Round(value, decimals) == value;
    public static void ValidatePrice(string quality, decimal price)
    {
        if (quality is not ("Árbol" or "Suelo") || !Valid(price, 4)) throw new ArgumentException("Indica un precio no negativo con hasta cuatro decimales.");
    }
}
public sealed record PlotStatistics(decimal Kilograms, decimal TreatmentCost, int UnknownCosts, decimal Income, decimal UnvaluedKilograms, int Trees, decimal? Hectares)
{
    public decimal? KilogramsPerTree => Trees > 0 ? Kilograms / Trees : null;
    public decimal? KilogramsPerHectare => Hectares > 0 ? Kilograms / Hectares : null;
    public decimal? CostPerTree => Trees > 0 ? TreatmentCost / Trees : null;
    public decimal? IncomePerTree => Trees > 0 ? Income / Trees : null;
    public static PlotStatistics Calculate(Plot plot, Guid campaign, IEnumerable<FieldWork> works, IEnumerable<CooperativePrice> prices)
    {
        var selected = works.Where(x => x.BusinessId == plot.BusinessId && x.CampaignId == campaign && x.PlotId == plot.Id).ToList();
        var treatments = selected.Where(x => x.Kind == FieldWorkKind.Treatment).ToList();
        decimal kg = 0, income = 0, missing = 0;
        foreach (var harvest in selected.Where(x => x.Kind == FieldWorkKind.Harvest && x.Unit == "kg"))
        {
            kg += harvest.Quantity;
            var price = prices.SingleOrDefault(x => x.BusinessId == plot.BusinessId && x.CampaignId == campaign && x.CooperativeId == harvest.CooperativeId && x.Quality == harvest.Quality);
            if (price is null) missing += harvest.Quantity; else income += harvest.Quantity * price.Price;
        }
        return new(kg, treatments.Sum(x => x.Cost ?? 0), treatments.Count(x => x.Cost is null), income, missing, plot.OliveTrees, plot.Hectares);
    }
}
