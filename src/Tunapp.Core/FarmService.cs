using System.Text.Json;

namespace Tunapp.Core;

/// <summary>Almacén de un dispositivo. La identidad local no es una autenticación de servidor.</summary>
public sealed class FarmService(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LocalData data = new();
    public FarmBusiness? Business => data.Businesses.SingleOrDefault(x => x.Id == data.SelectedBusinessId);
    public Campaign? SelectedCampaign => Campaigns.SingleOrDefault(x => x.Id == data.SelectedCampaignId);
    public IReadOnlyList<FarmBusiness> Businesses => data.Businesses.Where(b => data.Members.Any(m => m.BusinessId == b.Id && m.UserId == data.DeviceUserId)).ToList();
    public IReadOnlyList<Campaign> Campaigns => data.Campaigns.Where(x => x.BusinessId == Business?.Id).OrderByDescending(x => x.Start).ToList();
    public IReadOnlyList<Plot> Plots => data.Plots.Where(x => x.BusinessId == Business?.Id).OrderBy(x => x.Name).ToList();
    public IReadOnlyList<CampaignPlot> CampaignPlots => SelectedCampaign is { } campaign ? CampaignPlotsView.ForCampaign(Plots, data.CampaignPlots, campaign.BusinessId, campaign.Id) : [];
    public IReadOnlyList<Membership> Members => data.Members.Where(x => x.BusinessId == Business?.Id).ToList();
    public int PendingCount => data.PendingChanges.Count(x => x.BusinessId == Business?.Id);
    public Role CurrentRole => data.Members.SingleOrDefault(x => x.BusinessId == Business?.Id && x.UserId == data.DeviceUserId)?.Role ?? Role.Reader;
    public IReadOnlyList<Cooperative> Cooperatives => data.Cooperatives.Where(x => x.BusinessId == Business?.Id).OrderBy(x => x.Name).ToList();
    public IReadOnlyList<FieldWork> FieldWorks => data.FieldWorks.Where(x => x.BusinessId == Business?.Id && x.CampaignId == SelectedCampaign?.Id).OrderByDescending(x => x.Date).ToList();

    public IReadOnlyList<CooperativePrice> CooperativePrices => data.CooperativePrices.Where(x => x.BusinessId == Business?.Id && x.CampaignId == SelectedCampaign?.Id).ToList();
    public Task SaveCooperativePriceAsync(Guid campaignId, Guid cooperativeId, string quality, decimal price) => WriteAsync(() =>
    {
        var campaign = RequireOpen();
        Money.ValidatePrice(quality, price);
        if (campaign.Id != campaignId || !Cooperatives.Any(x => x.Id == cooperativeId)) throw new UnauthorizedAccessException("Cooperativa o campa?a no autorizada.");
        data.CooperativePrices.RemoveAll(x => x.BusinessId == campaign.BusinessId && x.CampaignId == campaignId && x.CooperativeId == cooperativeId && x.Quality == quality);
        data.CooperativePrices.Add(new(campaign.BusinessId, campaignId, cooperativeId, quality, price));
        Track(campaign.BusinessId, "CooperativePrice", cooperativeId);
    });
    public Task CreateCooperativeAsync(CooperativeInput input) => WriteAsync(() =>
    {
        var business = Require(Role.Operator);
        var name = Required(input.Name, "el nombre de la cooperativa");
        if (input.Id == Guid.Empty || name.Length > 160 || input.Notes is null || input.Notes.Length > 10000)
            throw new ArgumentException("Revisa los datos de la cooperativa.");
        if (Cooperatives.Any(x => x.Id == input.Id || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ya existe esa cooperativa.");
        data.Cooperatives.Add(new(input.Id, business, name, input.Notes.Trim()));
        Track(business, "Cooperative", input.Id);
    });
    public Task CreateFieldWorkAsync(FieldWorkInput input) => WriteAsync(() =>
    {
        var campaign = RequireOpen();
        FieldWorkValidation.Validate(input);
        if (input.CampaignId != campaign.Id || !Plots.Any(x => x.Id == input.PlotId))
            throw new UnauthorizedAccessException("La finca o campaña no pertenece a la selección activa.");
        if (input.CooperativeId is { } cooperative && !Cooperatives.Any(x => x.Id == cooperative))
            throw new UnauthorizedAccessException("La cooperativa no pertenece a esta explotación.");
        if (data.FieldWorks.Any(x => x.Id == input.Id)) throw new ArgumentException("Este registro ya existe.");
        data.FieldWorks.Add(new(input.Id, campaign.BusinessId, campaign.Id, input.PlotId, input.Kind,
            input.Date, input.Description.Trim(), input.Quantity, input.Unit, input.CooperativeId, input.Notes.Trim(), input.Quality, input.Cost));
        Track(campaign.BusinessId, "FieldWork", input.Id);
    });

    public async Task LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return;
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<LocalData>(stream) ?? throw new InvalidDataException("Archivo local vacío.");
            if (loaded.SchemaVersion != 1) throw new InvalidDataException("Versión de datos no compatible. Conserva el archivo original.");
            data = loaded;
            if (data.SelectedBusinessId is { } selected && !Businesses.Any(x => x.Id == selected))
                throw new InvalidDataException("La explotación seleccionada no tiene un usuario autorizado.");
        }
        finally { gate.Release(); }
    }

    private async Task WriteAsync(Action change)
    {
        await gate.WaitAsync();
        var previous = JsonSerializer.Serialize(data);
        try
        {
            change();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, data);
                await stream.FlushAsync();
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        catch { data = JsonSerializer.Deserialize<LocalData>(previous)!; throw; }
        finally { gate.Release(); }
    }

    private Guid Require(Role minimum = Role.Reader)
    {
        var business = Business ?? throw new InvalidOperationException("Selecciona una explotación.");
        var member = data.Members.SingleOrDefault(x => x.BusinessId == business.Id && x.UserId == data.DeviceUserId);
        if (member is null || member.Role < minimum) throw new UnauthorizedAccessException("No tienes permiso para esta operación.");
        return business.Id;
    }
    private Campaign RequireOpen()
    {
        Require(Role.Operator);
        var campaign = SelectedCampaign ?? throw new InvalidOperationException("Selecciona una campaña.");
        if (campaign.Status == CampaignStatus.Closed) throw new InvalidOperationException("La campaña está cerrada. Un administrador puede reabrirla.");
        return campaign;
    }
    private void Track(Guid businessId, string entity, Guid id) => data.PendingChanges.Add(new(Guid.NewGuid(), businessId, entity, id, DateTimeOffset.UtcNow));
    private static string Required(string value, string label) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Indica {label}.") : value.Trim();

    public Task CreateBusinessAsync(string name, string administratorName) => WriteAsync(() =>
    {
        name = Required(name, "el nombre de la explotación");
        administratorName = Required(administratorName, "tu nombre");
        var id = Guid.NewGuid();
        data.Businesses.Add(new(id, name));
        data.Members.Add(new(id, data.DeviceUserId, administratorName, Role.Administrator));
        data.SelectedBusinessId = id;
        data.SelectedCampaignId = null;
        Track(id, "Business", id);
    });
    public Task SelectBusinessAsync(Guid id) => WriteAsync(() =>
    {
        if (!Businesses.Any(x => x.Id == id)) throw new UnauthorizedAccessException("Explotación no autorizada.");
        data.SelectedBusinessId = id;
        data.SelectedCampaignId = null;
    });
    public Task SelectCampaignAsync(Guid id) => WriteAsync(() =>
    {
        Require();
        if (!Campaigns.Any(x => x.Id == id)) throw new UnauthorizedAccessException("La campaña no pertenece a esta explotación.");
        data.SelectedCampaignId = id;
    });
    public Task CreateCampaignAsync(string name, DateOnly? start, DateOnly? end) => WriteAsync(() =>
    {
        var business = Require(Role.Administrator);
        name = Required(name, "el nombre de campaña");
        if (end < start) throw new ArgumentException("El fin debe ser igual o posterior al inicio.");
        if (Campaigns.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ya existe una campaña con ese nombre.");
        var id = Guid.NewGuid();
        data.Campaigns.Add(new(id, business, name, start, end));
        data.SelectedCampaignId = id;
        Track(business, "Campaign", id);
    });
    public Task SetCampaignClosedAsync(bool closed) => SetCampaignClosedAsync(SelectedCampaign?.Id ?? Guid.Empty, closed);
    public Task SetCampaignClosedAsync(Guid campaignId, bool closed) => WriteAsync(() =>
    {
        var business = Require(Role.Administrator);
        var campaign = Campaigns.SingleOrDefault(x => x.Id == campaignId) ?? throw new InvalidOperationException("Selecciona una campaña de esta explotación.");
        data.Campaigns[data.Campaigns.IndexOf(campaign)] = campaign with { Status = closed ? CampaignStatus.Closed : CampaignStatus.Open };
        Track(business, "Campaign", campaign.Id);
    });
    public Task CreatePlotAsync(string name, decimal? hectares, int trees, string variety, string notes) => WriteAsync(() =>
    {
        var business = Require(Role.Operator);
        name = Required(name, "el nombre de la finca");
        if (hectares is <= 0 || trees < 0) throw new ArgumentException("La superficie debe ser positiva y los olivos no pueden ser negativos.");
        if (Plots.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ya existe una finca con ese nombre.");
        var id = Guid.NewGuid();
        data.Plots.Add(new(id, business, name, hectares, trees, variety.Trim(), notes.Trim()));
        Track(business, "Plot", id);
    });
    public Task UpdatePlotAsync(Guid plotId, string name, decimal? hectares, int trees, string variety, string notes) => WriteAsync(() =>
    {
        var business = Require(Role.Operator);
        var plot = Plots.SingleOrDefault(x => x.Id == plotId) ?? throw new UnauthorizedAccessException("La finca no pertenece a esta explotación.");
        name = Required(name, "el nombre de la finca");
        if (hectares is <= 0 || trees < 0) throw new ArgumentException("Revisa superficie y número de olivos.");
        if (Plots.Any(x => x.Id != plotId && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ya existe una finca con ese nombre.");
        data.Plots[data.Plots.IndexOf(plot)] = plot with { Name = name, Hectares = hectares, OliveTrees = trees, Variety = variety.Trim(), Notes = notes.Trim() };
        Track(business, "Plot", plotId);
    });
    public Task UpdateCampaignPlotAsync(Guid plotId, decimal? hectares, int trees, string variety, string notes) => WriteAsync(() =>
    {
        var campaign = RequireOpen();
        var link = CampaignPlots.SingleOrDefault(x => x.PlotId == plotId) ?? throw new UnauthorizedAccessException("La finca no está vinculada a esta campaña.");
        if (hectares is <= 0 || trees < 0) throw new ArgumentException("Revisa superficie y número de olivos.");
        var updated = link with { Hectares = hectares, OliveTrees = trees, Variety = variety.Trim(), Notes = notes.Trim() };
        var index = data.CampaignPlots.IndexOf(link);
        if (index < 0) data.CampaignPlots.Add(updated); else data.CampaignPlots[index] = updated;
        Track(campaign.BusinessId, "CampaignPlot", plotId);
    });
    public Task LinkPlotAsync(Guid plotId, decimal? hectares, int trees, string variety, string notes) => WriteAsync(() =>
    {
        var campaign = RequireOpen();
        if (!Plots.Any(x => x.Id == plotId)) throw new UnauthorizedAccessException("La finca no pertenece a esta explotación.");
        if (hectares is <= 0 || trees < 0) throw new ArgumentException("Revisa superficie y número de olivos.");
        if (data.CampaignPlots.Any(x => x.CampaignId == campaign.Id && x.PlotId == plotId)) throw new ArgumentException("La finca ya está vinculada a esta campaña.");
        data.CampaignPlots.Add(new(campaign.BusinessId, campaign.Id, plotId, hectares, trees, variety.Trim(), notes.Trim()));
        Track(campaign.BusinessId, "CampaignPlot", plotId);
    });
}
