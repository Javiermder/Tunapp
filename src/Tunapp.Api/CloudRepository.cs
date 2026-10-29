using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Cloud.Firestore;
using Tunapp.Core;

namespace Tunapp.Api;

public sealed class CloudRepository(FirestoreDb db)
{
    private DocumentReference Business(Guid id) => db.Collection("businesses").Document(id.ToString("N"));
    private DocumentReference Member(Guid id, string uid) => Business(id).Collection("members").Document(uid);
    private static Dictionary<string, object> Payload<T>(T entity) => new() { ["json"] = JsonSerializer.Serialize(entity), ["updatedAt"] = FieldValue.ServerTimestamp };
    private static T Read<T>(DocumentSnapshot snapshot) => snapshot.Exists
        ? JsonSerializer.Deserialize<T>(snapshot.GetValue<string>("json"))!
        : throw new KeyNotFoundException("No se encontró el registro.");
    private static string Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160) throw new ArgumentException("Indica un nombre de entre 1 y 160 caracteres.");
        return value.Trim();
    }
    private static string NameKey(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
    private static void ValidatePlot(decimal? hectares, int trees, string variety, string notes)
    {
        if (hectares is <= 0 || trees < 0) throw new ArgumentException("Revisa la superficie y el número de olivos.");
        if (variety is null || notes is null || variety.Length > 200 || notes.Length > 10000) throw new ArgumentException("Revisa la variedad y las notas.");
    }
    private static void Id(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Identificador no válido."); }
    private static void RequireOpen(Campaign campaign)
    {
        if (campaign.Status != CampaignStatus.Open) throw new InvalidOperationException("La campaña está cerrada.");
    }
    private async Task<CloudMember> AuthorizeAsync(Guid business, string uid, Role minimum, Transaction? transaction = null)
    {
        Id(business);
        var reference = Member(business, uid);
        var snapshot = transaction is null ? await reference.GetSnapshotAsync() : await transaction.GetSnapshotAsync(reference);
        if (!snapshot.Exists) throw new UnauthorizedAccessException("No tienes acceso a esta explotación.");
        var member = Read<CloudMember>(snapshot);
        if (member.Role < minimum) throw new UnauthorizedAccessException("No tienes permiso para esta operación.");
        return member;
    }

    public async Task<List<CloudBusiness>> ListAsync(string uid)
    {
        // El índice de acceso se mantiene atómicamente con la membresía; nunca se lista toda la colección.
        var index = await db.Collection("users").Document(uid).Collection("businesses").GetSnapshotAsync();
        var result = new List<CloudBusiness>();
        foreach (var access in index.Documents)
        {
            var id = Guid.ParseExact(access.Id, "N");
            var member = await AuthorizeAsync(id, uid, Role.Reader);
            var business = await Business(id).GetSnapshotAsync();
            result.Add(new(id, business.GetValue<string>("name"), member.Role));
        }
        return result.OrderBy(x => x.Name).ToList();
    }

    public async Task CreateAsync(string uid, string email, BusinessInput input)
    {
        Id(input.Id); var name = Name(input.Name);
        await db.RunTransactionAsync(async tx =>
        {
            var existing = await tx.GetSnapshotAsync(Business(input.Id));
            if (existing.Exists)
            {
                var member = await AuthorizeAsync(input.Id, uid, Role.Administrator, tx);
                if (existing.GetValue<string>("name") != name) throw new InvalidOperationException("El identificador ya está en uso.");
                return; // Repetición de la misma creación: no duplica la explotación.
            }
            tx.Create(Business(input.Id), new Dictionary<string, object> { ["name"] = name, ["createdAt"] = FieldValue.ServerTimestamp });
            tx.Create(Member(input.Id, uid), Payload(new CloudMember(uid, email, Role.Administrator)));
            tx.Set(db.Collection("users").Document(uid).Collection("businesses").Document(input.Id.ToString("N")), new { active = true });
        });
    }

    public Task AddMemberAsync(Guid business, string actor, string target, string email, Role role)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentException("Permiso no válido.");
        return db.RunTransactionAsync(async tx =>
        {
            await AuthorizeAsync(business, actor, Role.Administrator, tx);
            var existing = await tx.GetSnapshotAsync(Member(business, target));
            if (existing.Exists) throw new InvalidOperationException("Este usuario ya pertenece a la explotación.");
            tx.Create(Member(business, target), Payload(new CloudMember(target, email, role)));
            tx.Set(db.Collection("users").Document(target).Collection("businesses").Document(business.ToString("N")), new { active = true });
            tx.Create(Business(business).Collection("audit").Document(), new { action = "member.add", actor, target, role = role.ToString(), at = FieldValue.ServerTimestamp });
        });
    }

    public async Task DemandAdministratorAsync(Guid business, string uid) => await AuthorizeAsync(business, uid, Role.Administrator);

    public async Task<CloudSnapshot> SnapshotAsync(Guid business, string uid)
    {
        // Lectura coherente con los permisos y todos los registros de esta explotación.
        return await db.RunTransactionAsync(async tx =>
        {
            var member = await AuthorizeAsync(business, uid, Role.Reader, tx);
            var root = await tx.GetSnapshotAsync(Business(business));
            var campaigns = await tx.GetSnapshotAsync(Business(business).Collection("campaigns"));
            var plots = await tx.GetSnapshotAsync(Business(business).Collection("plots"));
            var links = await tx.GetSnapshotAsync(Business(business).Collection("campaignPlots"));
            // Solo el administrador obtiene correos y permisos de los demás usuarios.
            var members = member.Role == Role.Administrator
                ? (await tx.GetSnapshotAsync(Business(business).Collection("members"))).Documents.Select(Read<CloudMember>).ToList()
                : new List<CloudMember> { member };
            return new CloudSnapshot(new(business, root.GetValue<string>("name"), member.Role),
                campaigns.Documents.Select(Read<Campaign>).OrderByDescending(x => x.Start).ToList(),
                plots.Documents.Select(Read<Plot>).OrderBy(x => x.Name).ToList(),
                links.Documents.Select(Read<CampaignPlot>).ToList(), members);
        });
    }

    public Task CreateCampaignAsync(Guid business, string uid, CampaignInput input)
    {
        Id(input.Id); var name = Name(input.Name);
        if (input.End < input.Start) throw new ArgumentException("La fecha final no puede ser anterior a la inicial.");
        var reference = Business(business).Collection("campaigns").Document(input.Id.ToString("N"));
        var uniqueName = Business(business).Collection("campaignNames").Document(NameKey(name));
        var campaign = new Campaign(input.Id, business, name, input.Start, input.End);
        return db.RunTransactionAsync(async tx =>
        {
            await AuthorizeAsync(business, uid, Role.Administrator, tx);
            var existing = await tx.GetSnapshotAsync(reference);
            var duplicate = await tx.GetSnapshotAsync(uniqueName);
            if (existing.Exists)
            {
                if (Read<Campaign>(existing) != campaign) throw new InvalidOperationException("El identificador de campaña ya está en uso.");
                return;
            }
            if (duplicate.Exists) throw new InvalidOperationException("Ya existe una campaña con ese nombre.");
            tx.Create(reference, Payload(campaign)); tx.Create(uniqueName, new { id = input.Id.ToString("N") });
        });
    }

    public Task SetClosedAsync(Guid business, string uid, Guid campaignId, bool closed) => db.RunTransactionAsync(async tx =>
    {
        await AuthorizeAsync(business, uid, Role.Administrator, tx);
        var reference = Business(business).Collection("campaigns").Document(campaignId.ToString("N"));
        var campaign = Read<Campaign>(await tx.GetSnapshotAsync(reference));
        tx.Set(reference, Payload(campaign with { Status = closed ? CampaignStatus.Closed : CampaignStatus.Open }));
        tx.Create(Business(business).Collection("audit").Document(), new { action = closed ? "campaign.close" : "campaign.reopen", actor = uid, campaign = campaignId.ToString("N"), at = FieldValue.ServerTimestamp });
    });

    public Task CreatePlotAsync(Guid business, string uid, PlotInput input)
    {
        Id(input.Id); var name = Name(input.Name); ValidatePlot(input.Hectares, input.OliveTrees, input.Variety, input.Notes);
        var reference = Business(business).Collection("plots").Document(input.Id.ToString("N"));
        var uniqueName = Business(business).Collection("plotNames").Document(NameKey(name));
        var plot = new Plot(input.Id, business, name, input.Hectares, input.OliveTrees, input.Variety.Trim(), input.Notes.Trim());
        return db.RunTransactionAsync(async tx =>
        {
            await AuthorizeAsync(business, uid, Role.Operator, tx);
            var existing = await tx.GetSnapshotAsync(reference);
            var duplicate = await tx.GetSnapshotAsync(uniqueName);
            if (existing.Exists)
            {
                if (Read<Plot>(existing) != plot) throw new InvalidOperationException("El identificador de finca ya está en uso.");
                return;
            }
            if (duplicate.Exists) throw new InvalidOperationException("Ya existe una finca con ese nombre.");
            tx.Create(reference, Payload(plot)); tx.Create(uniqueName, new { id = input.Id.ToString("N") });
        });
    }

    public Task LinkPlotAsync(Guid business, string uid, Guid campaignId, Guid plotId, CampaignPlotInput input)
    {
        ValidatePlot(input.Hectares, input.OliveTrees, input.Variety, input.Notes);
        return db.RunTransactionAsync(async tx =>
        {
            await AuthorizeAsync(business, uid, Role.Operator, tx);
            var campaign = Read<Campaign>(await tx.GetSnapshotAsync(Business(business).Collection("campaigns").Document(campaignId.ToString("N"))));
            RequireOpen(campaign);
            _ = Read<Plot>(await tx.GetSnapshotAsync(Business(business).Collection("plots").Document(plotId.ToString("N"))));
            var reference = Business(business).Collection("campaignPlots").Document($"{campaignId:N}_{plotId:N}");
            var existing = await tx.GetSnapshotAsync(reference);
            var link = new CampaignPlot(business, campaignId, plotId, input.Hectares, input.OliveTrees, input.Variety.Trim(), input.Notes.Trim());
            if (existing.Exists)
            {
                if (Read<CampaignPlot>(existing) != link) throw new InvalidOperationException("La finca ya está vinculada con otros datos. Actualiza antes de modificar.");
                return;
            }
            tx.Create(reference, Payload(link));
        });
    }
}
