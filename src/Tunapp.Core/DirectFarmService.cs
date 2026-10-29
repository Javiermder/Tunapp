using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Tunapp.Core;

public sealed record FarmInvitation(string FarmId, string FarmName, string Email, Role Role);
public sealed record SyncOutcome(bool Connected, int Pending, string Message);

public sealed class DirectFarmService : IAsyncDisposable
{
    private readonly CloudDatabase database;
    private readonly FirestoreRest remote;
    private readonly FirebaseAuthClient auth;
    private readonly string uid;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool onlineOnly = true;
    private Dictionary<string, FireDocument> documents = [];
    public IReadOnlyList<PendingFireWrite> Pending { get; private set; } = [];
    private DirectFarmService(CloudDatabase database, FirestoreRest remote, FirebaseAuthClient auth)
    { this.database = database; this.remote = remote; this.auth = auth; uid = remote.AccountId; }
    public static string DatabaseName(string uid) => "cloud-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))) + ".db3";
    public static async Task<DirectFarmService> OpenAsync(string folder, FirestoreRest remote, FirebaseAuthClient auth, bool onlineOnly = true)
    {
        if (auth.Session?.UserId != remote.AccountId) throw new UnauthorizedAccessException("La cuenta no coincide.");
        var service = new DirectFarmService(await CloudDatabase.OpenAsync(Path.Combine(folder, DatabaseName(remote.AccountId)), remote.AccountId), remote, auth);
        service.onlineOnly = onlineOnly;
        await service.ReloadAsync(); return service;
    }
    private async Task ReloadAsync()
    {
        documents = (await database.DocumentsAsync()).ToDictionary(x => x.Path, x => x.Document());
        Pending = await database.PendingAsync();
    }
    private static string Farm(Guid id) => $"farms/{id:N}";
    private FireDocument? Doc(string path) => documents.GetValueOrDefault(path);
    private List<FireDocument> Collection(string path) => documents.Values.Where(x => x.Path.StartsWith(path + "/", StringComparison.Ordinal) && x.Path.Count(c => c == '/') == path.Count(c => c == '/') + 1).ToList();
    public List<CloudBusiness> Businesses => Collection("farms").Where(x => Doc($"{x.Path}/members/{uid}") is not null)
        .Select(x => new CloudBusiness(Guid.ParseExact(x.Path.Split('/')[1], "N"), x.String("name"), (Role)Doc($"{x.Path}/members/{uid}")!.Integer("role"))).OrderBy(x => x.Name).ToList();
    public List<FarmInvitation> Invitations => Collection($"invitations/{auth.Session?.Email.Trim().ToLowerInvariant()}/farms")
        .Select(x => new FarmInvitation(x.String("farmId"), x.String("farmName"), x.String("email"), (Role)x.Integer("role"))).ToList();
    public string? Owner(Guid business) => Doc(Farm(business))?.String("ownerUid");
    public List<Cooperative> Cooperatives(Guid business)
    {
        Require(business, Role.Reader);
        return Collection(Farm(business) + "/cooperatives").Select(x => new Cooperative(
            Guid.ParseExact(x.Path.Split('/')[3], "N"), business, x.String("name"), x.String("notes"))).OrderBy(x => x.Name).ToList();
    }
    public List<FieldWork> FieldWorks(Guid business, Guid campaign)
    {
        var state = Require(business, Role.Reader);
        if (!state.Campaigns.Any(x => x.Id == campaign)) throw new UnauthorizedAccessException("Campaña no autorizada.");
        return Collection(Farm(business) + "/fieldWorks").Where(x => x.String("campaignId") == campaign.ToString("N"))
            .Select(x => new FieldWork(Guid.ParseExact(x.Path.Split('/')[3], "N"), business, campaign,
                Guid.ParseExact(x.String("plotId"), "N"), (FieldWorkKind)x.Integer("kind"), x.Date("date")!.Value,
                x.String("description"), x.Integer("quantityMilli") / 1000m, x.String("unit"),
                x.Fields["cooperativeId"]?["stringValue"] is null ? null : Guid.ParseExact(x.String("cooperativeId"), "N"), x.String("notes"), x.Fields["quality"]?["stringValue"]?.GetValue<string>(), x.Fields["costCents"] is null ? null : x.Integer("costCents") / 100m))
            .OrderByDescending(x => x.Date).ToList();
    }
    public List<CooperativePrice> CooperativePrices(Guid business, Guid campaign)
    {
        var state = Require(business, Role.Reader);
        if (!state.Campaigns.Any(x => x.Id == campaign)) throw new UnauthorizedAccessException("Campaña no autorizada.");
        return Collection(Farm(business) + "/cooperativePrices").Where(x => x.String("campaignId") == campaign.ToString("N"))
            .Select(x => new CooperativePrice(business, campaign, Guid.ParseExact(x.String("cooperativeId"), "N"), x.String("quality"), x.Integer("priceTenThousandths") / 10000m)).ToList();
    }
    public Task SaveCooperativePriceAsync(Guid business, Guid campaign, Guid cooperative, string quality, decimal price) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Operator);
        Money.ValidatePrice(quality, price);
        var selected = state.Campaigns.SingleOrDefault(x => x.Id == campaign) ?? throw new UnauthorizedAccessException("Campaña no autorizada.");
        if (selected.Status != CampaignStatus.Open) throw new InvalidOperationException("La campaña está cerrada.");
        if (!Cooperatives(business).Any(x => x.Id == cooperative)) throw new UnauthorizedAccessException("Cooperativa no autorizada.");
        var path = $"{Farm(business)}/cooperativePrices/{campaign:N}_{cooperative:N}_{(quality == "Árbol" ? "tree" : "ground")}";
        var existing = Doc(path);
        await QueueAsync(business, path, Meta(new JsonObject {
            ["campaignId"] = FireFields.Text(campaign.ToString("N")), ["cooperativeId"] = FireFields.Text(cooperative.ToString("N")),
            ["quality"] = FireFields.Text(quality), ["priceTenThousandths"] = FireFields.Number((long)(price * 10000))
        }, existing), "price.save", existing is null);
    });
    public Task CreateCooperativeAsync(Guid business, CooperativeInput input) => ChangeAsync(async () =>
    {
        Require(business, Role.Operator); var name = Name(input.Name);
        if (input.Id == Guid.Empty || input.Notes is null || input.Notes.Length > 10000) throw new ArgumentException("Revisa los datos de la cooperativa.");
        if (Cooperatives(business).Any(x => x.Id == input.Id || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Ya existe esa cooperativa.");
        await QueueAsync(business, $"{Farm(business)}/cooperatives/{input.Id:N}",
            Meta(new JsonObject { ["name"] = FireFields.Text(name), ["notes"] = FireFields.Text(input.Notes.Trim()) }), "cooperative.create", true);
    });
    public Task CreateFieldWorkAsync(Guid business, FieldWorkInput input) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Operator); FieldWorkValidation.Validate(input);
        var campaign = state.Campaigns.SingleOrDefault(x => x.Id == input.CampaignId) ?? throw new UnauthorizedAccessException("Campaña no autorizada.");
        if (campaign.Status != CampaignStatus.Open) throw new InvalidOperationException("La campaña está cerrada.");
        if (!state.Plots.Any(x => x.Id == input.PlotId)) throw new UnauthorizedAccessException("Finca no autorizada.");
        if (input.CooperativeId is { } cooperative && !Cooperatives(business).Any(x => x.Id == cooperative)) throw new UnauthorizedAccessException("Cooperativa no autorizada.");
        var path = $"{Farm(business)}/fieldWorks/{input.Id:N}";
        if (Doc(path) is not null) throw new ArgumentException("Este registro ya existe.");
        var fields = Meta(new JsonObject
        {
            ["campaignId"] = FireFields.Text(input.CampaignId.ToString("N")), ["plotId"] = FireFields.Text(input.PlotId.ToString("N")),
            ["kind"] = FireFields.Number((int)input.Kind), ["date"] = FireFields.OptionalDate(input.Date),
            ["description"] = FireFields.Text(input.Description.Trim()), ["quantityMilli"] = FireFields.Number((long)(input.Quantity * 1000)),
            ["unit"] = FireFields.Text(input.Unit), ["cooperativeId"] = input.CooperativeId is { } id ? FireFields.Text(id.ToString("N")) : new JsonObject { ["nullValue"] = null },
            ["notes"] = FireFields.Text(input.Notes.Trim())
        });
        if (input.Cost is { } cost) fields["costCents"] = FireFields.Number((long)(cost * 100));
        if (input.Quality is not null) fields["quality"] = FireFields.Text(input.Quality);
        await QueueAsync(business, path, fields, "fieldwork.create", true);
    });
    public CloudSnapshot? Snapshot(Guid business)
    {
        var selected = Businesses.FirstOrDefault(x => x.Id == business);
        if (selected is null) return null;
        var root = Farm(business);
        return new(selected,
            Collection(root + "/campaigns").Select(x => new Campaign(Guid.ParseExact(x.Path.Split('/')[3], "N"), business, x.String("name"), x.Date("start"), x.Date("end"), (CampaignStatus)x.Integer("status"))).OrderByDescending(x => x.Start).ToList(),
            Collection(root + "/plots").Select(x => new Plot(Guid.ParseExact(x.Path.Split('/')[3], "N"), business, x.String("name"), x.Fields["areaM2"]?["integerValue"] is null ? null : x.Integer("areaM2") / 10000m, (int)x.Integer("trees"), x.String("variety"), x.String("notes"))).OrderBy(x => x.Name).ToList(),
            Collection(root + "/campaignPlots").Select(x => new CampaignPlot(business, Guid.ParseExact(x.String("campaignId"), "N"), Guid.ParseExact(x.String("plotId"), "N"), x.Fields["areaM2"]?["integerValue"] is null ? null : x.Integer("areaM2") / 10000m, (int)x.Integer("trees"), x.String("variety"), x.String("notes"))).ToList(),
            Collection(root + "/members").Select(x => new CloudMember(x.String("uid"), x.String("email"), (Role)x.Integer("role"))).ToList());
    }
    private void Account()
    {
        if (auth.Session?.UserId != uid) throw new UnauthorizedAccessException("Inicia sesión con la cuenta propietaria de estos datos locales.");
    }
    private CloudSnapshot Require(Guid business, Role role)
    {
        Account(); var snapshot = Snapshot(business) ?? throw new UnauthorizedAccessException("No tienes acceso a esta explotación.");
        if (snapshot.Business.Role < role) throw new UnauthorizedAccessException("No tienes permiso para esta operación.");
        return snapshot;
    }
    private static string Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160) throw new ArgumentException("Indica un nombre de entre 1 y 160 caracteres.");
        return name.Trim();
    }
    private static long? Area(decimal? hectares)
    {
        if (hectares is null) return null;
        if (hectares <= 0 || hectares.Value * 10000m != decimal.Truncate(hectares.Value * 10000m) || hectares > 100000000m)
            throw new ArgumentException("Indica una superficie positiva con un máximo de cuatro decimales en hectáreas.");
        return (long)(hectares.Value * 10000m);
    }
    private static JsonObject PlotFields(decimal? hectares, int trees, string variety, string notes)
    {
        if (trees < 0 || variety is null || notes is null || variety.Length > 200 || notes.Length > 10000) throw new ArgumentException("Revisa olivos, variedad y notas.");
        return new() { ["areaM2"] = Area(hectares) is { } area ? FireFields.Number(area) : new JsonObject { ["nullValue"] = null }, ["trees"] = FireFields.Number(trees), ["variety"] = FireFields.Text(variety.Trim()), ["notes"] = FireFields.Text(notes.Trim()) };
    }
    private JsonObject Meta(JsonObject fields, FireDocument? existing = null)
    {
        fields["createdBy"] = existing?.Fields["createdBy"]?.DeepClone() ?? FireFields.Text(uid);
        fields["createdAt"] = existing?.Fields["createdAt"]?.DeepClone() ?? FireFields.Time(DateTimeOffset.UtcNow);
        fields["updatedAt"] = FireFields.Time(DateTimeOffset.UtcNow); return fields;
    }
    private JsonObject MemberFields(Role role) => new()
    {
        ["uid"] = FireFields.Text(uid), ["email"] = FireFields.Text(auth.Session!.Email.Trim().ToLowerInvariant()), ["role"] = FireFields.Number((int)role),
        ["createdAt"] = FireFields.Time(DateTimeOffset.UtcNow), ["updatedAt"] = FireFields.Time(DateTimeOffset.UtcNow)
    };
    private JsonObject IndexFields(string farm) => new() { ["farmId"] = FireFields.Text(farm), ["createdAt"] = FireFields.Time(DateTimeOffset.UtcNow) };
    private async Task QueueAsync(Guid business, string path, JsonObject fields, string kind, bool create, IReadOnlyList<FireDocument>? extra = null)
    {
        var existing = Doc(path);
        var write = new PendingFireWrite
        {
            OperationId = Guid.NewGuid().ToString("N"), FarmId = business.ToString("N"), Path = path, Fields = fields.ToJsonString(), Kind = kind,
            Create = create, ExpectedUpdateTime = existing?.UpdateTime
        };
        if (onlineOnly)
        {
            var previous = Pending.Where(x => x.Path == path || (x.Kind == "farm.create" && x.FarmId == business.ToString("N"))).ToList();
            foreach (var pending in previous)
            {
                if (pending.ErrorKind == "conflict")
                    throw new InvalidOperationException("Este registro tiene un conflicto con Firebase. Revisa el cambio pendiente antes de editarlo.");
                await database.MarkAttemptedAsync(pending.Sequence);
                try { await SendAsync(pending, CancellationToken.None); }
                catch (FirestoreConflictException ex)
                { await database.BlockAsync(pending.Sequence, "conflict", ex.Message); throw; }
                catch (FirestoreDeniedException ex)
                { await database.BlockAsync(pending.Sequence, "permission", ex.Message); throw; }
                finally { await ReloadAsync(); }
            }
            if (previous.Count > 0)
            {
                // La recuperacion puede confirmar una creacion o una edicion anterior.
                // Usar su version confirmada como precondicion del nuevo cambio.
                var confirmed = Doc(path);
                write.ExpectedUpdateTime = confirmed?.UpdateTime;
                write.Fields = Meta(fields, confirmed).ToJsonString();
            }
            // No guardar ni mostrar el cambio hasta que Firebase lo confirme.
            await SendAsync(write, CancellationToken.None);
            if (extra is not null)
            {
                var confirmed = new List<FireDocument>();
                foreach (var document in extra)
                    confirmed.Add(await remote.GetAsync(document.Path) ?? throw new IOException("No se pudo confirmar el guardado en Firebase."));
                await database.MergeAsync(confirmed, "", deleteMissing: false);
            }
        }
        else await database.QueueAsync(write, extra);
        await ReloadAsync();
    }
    private async Task ChangeAsync(Func<Task> action)
    {
        await gate.WaitAsync();
        try
        {
            Account();
            if (onlineOnly) await remote.ListAsync($"users/{uid}/farms");
            await action();
        }
        finally { gate.Release(); }
    }
    public Task CreateBusinessAsync(BusinessInput input) => ChangeAsync(async () =>
    {
        if (input.Id == Guid.Empty || Doc(Farm(input.Id)) is not null) throw new ArgumentException("Identificador de explotación no válido.");
        var fields = Meta(new JsonObject { ["name"] = FireFields.Text(Name(input.Name)), ["ownerUid"] = FireFields.Text(uid) });
        await QueueAsync(input.Id, Farm(input.Id), fields, "farm.create", true,
            [new($"{Farm(input.Id)}/members/{uid}", MemberFields(Role.Administrator)), new($"users/{uid}/farms/{input.Id:N}", IndexFields(input.Id.ToString("N")))]);
    });
    public Task CreateCampaignAsync(Guid business, CampaignInput input) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Administrator); var name = Name(input.Name);
        if (input.Id == Guid.Empty || state.Campaigns.Any(x => x.Id == input.Id || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ya existe esa campaña.");
        if (input.End < input.Start) throw new ArgumentException("La fecha final no puede ser anterior a la inicial.");
        var fields = Meta(new JsonObject { ["name"] = FireFields.Text(name), ["start"] = FireFields.OptionalDate(input.Start), ["end"] = FireFields.OptionalDate(input.End), ["status"] = FireFields.Number(0) });
        await QueueAsync(business, $"{Farm(business)}/campaigns/{input.Id:N}", fields, "campaign.create", true);
    });
    public Task SetClosedAsync(Guid business, Guid campaign, bool closed) => ChangeAsync(async () =>
    {
        Require(business, Role.Administrator);
        var path = $"{Farm(business)}/campaigns/{campaign:N}"; var existing = Doc(path) ?? throw new ArgumentException("Campaña no encontrada.");
        if (Pending.Any(x => x.Path == path && x.Create)) throw new InvalidOperationException("Sincroniza la campaña nueva antes de cerrarla.");
        var fields = existing.Fields.Copy(); fields["status"] = FireFields.Number(closed ? 1 : 0); Meta(fields, existing);
        await QueueAsync(business, path, fields, "campaign.status", false);
    });
    public Task SavePlotAsync(Guid business, PlotInput input, bool create = true) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Operator); var name = Name(input.Name);
        if (input.Id == Guid.Empty || state.Plots.Any(x => x.Id != input.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Ya existe esa finca.");
        var path = $"{Farm(business)}/plots/{input.Id:N}"; var existing = Doc(path);
        if (create == (existing is not null)) throw new ArgumentException("Revisa la finca que estás guardando.");
        var fields = PlotFields(input.Hectares, input.OliveTrees, input.Variety, input.Notes); fields["name"] = FireFields.Text(name); Meta(fields, existing);
        await QueueAsync(business, path, fields, create ? "plot.create" : "plot.update", create);
    });
    public Task SaveCampaignPlotAsync(Guid business, Guid campaignId, Guid plotId, CampaignPlotInput input, bool create = true) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Operator);
        var campaign = state.Campaigns.SingleOrDefault(x => x.Id == campaignId) ?? throw new ArgumentException("Campaña no encontrada.");
        if (campaign.Status != CampaignStatus.Open) throw new InvalidOperationException("La campaña está cerrada.");
        if (!state.Plots.Any(x => x.Id == plotId)) throw new UnauthorizedAccessException("La finca no pertenece a esta explotación.");
        var path = $"{Farm(business)}/campaignPlots/{campaignId:N}_{plotId:N}"; var existing = Doc(path);
        if (create == (existing is not null)) throw new ArgumentException("Revisa el vínculo de la finca con la campaña.");
        var fields = PlotFields(input.Hectares, input.OliveTrees, input.Variety, input.Notes);
        fields["campaignId"] = FireFields.Text(campaignId.ToString("N")); fields["plotId"] = FireFields.Text(plotId.ToString("N")); Meta(fields, existing);
        await QueueAsync(business, path, fields, create ? "link.create" : "link.update", create);
    });
    private static string Hash(PendingFireWrite write) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(write.Fields)));
    private async Task SendAsync(PendingFireWrite pending, CancellationToken cancellation)
    {
        FireDocument? receipt = null;
        var canCheckReceipt = pending.Kind != "farm.create" || await remote.GetAsync($"users/{uid}/farms/{pending.FarmId}", cancellation) is not null;
        if (canCheckReceipt) receipt = await remote.GetAsync($"farms/{pending.FarmId}/operations/{pending.OperationId}", cancellation);
        if (receipt is not null && (receipt.String("uid") != uid || receipt.String("target") != pending.Path || receipt.String("hash") != Hash(pending))) throw new InvalidDataException("El recibo de sincronización no coincide. Conserva los cambios locales.");
        if (receipt is null)
        {
            var writes = new List<JsonObject> { remote.Write(pending.Document(), pending.Create, pending.ExpectedUpdateTime) };
            if (pending.Kind == "farm.create")
            {
                writes.Add(remote.Write(new($"farms/{pending.FarmId}/members/{uid}", MemberFields(Role.Administrator)), true));
                writes.Add(remote.Write(new($"users/{uid}/farms/{pending.FarmId}", IndexFields(pending.FarmId)), true));
            }
            writes.Add(remote.Write(new($"farms/{pending.FarmId}/operations/{pending.OperationId}", new JsonObject
            { ["uid"] = FireFields.Text(uid), ["target"] = FireFields.Text(pending.Path), ["hash"] = FireFields.Text(Hash(pending)), ["createdAt"] = FireFields.Time(DateTimeOffset.UtcNow) }), true));
            await remote.CommitAsync(writes, cancellation);
        }
        var saved = await remote.GetAsync(pending.Path, cancellation) ?? throw new IOException("No se pudo confirmar el registro guardado.");
        await database.AcknowledgeAsync(pending, saved);
    }
    public async Task<SyncOutcome> SyncAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            Account();
            foreach (var group in (await database.PendingAsync()).GroupBy(x => x.FarmId))
            {
                foreach (var pending in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (pending.ErrorKind is not null) break;
                    await database.MarkAttemptedAsync(pending.Sequence);
                    try { await SendAsync(pending, cancellation); }
                    catch (FirestoreConflictException ex) { await database.BlockAsync(pending.Sequence, "conflict", ex.Message); break; }
                    catch (FirestoreDeniedException ex) { await database.BlockAsync(pending.Sequence, "permission", ex.Message); break; }
                }
            }
            var indexes = await remote.ListAsync($"users/{uid}/farms", cancellation);
            var newFeaturesAvailable = true;
            var pendingCreations = (await database.PendingAsync()).Where(x => x.Kind == "farm.create").Select(x => x.FarmId).ToHashSet();
            var remoteIds = indexes.Select(x => x.String("farmId")).ToHashSet();
            foreach (var cached in Businesses.Where(x => !remoteIds.Contains(x.Id.ToString("N")) && !pendingCreations.Contains(x.Id.ToString("N"))))
                await database.RevokeAsync(cached.Id.ToString("N"), uid);
            await database.MergeAsync(indexes, $"users/{uid}/farms/");
            foreach (var index in indexes)
            {
                var id = index.String("farmId");
                var membership = await remote.GetAsync($"farms/{id}/members/{uid}", cancellation);
                if (membership is null) { await database.RevokeAsync(id, uid); continue; }
                var root = await remote.GetAsync($"farms/{id}", cancellation);
                if (root is null) { await database.RevokeAsync(id, uid); continue; }
                var all = new List<FireDocument> { root };
                all.AddRange(await remote.ListAsync($"farms/{id}/campaigns", cancellation));
                all.AddRange(await remote.ListAsync($"farms/{id}/plots", cancellation));
                all.AddRange(await remote.ListAsync($"farms/{id}/campaignPlots", cancellation));
                // Conservar la sincronización existente mientras se publican las reglas nuevas.
                foreach (var collection in new[] { "cooperatives", "fieldWorks", "cooperativePrices" })
                {
                    var path = $"farms/{id}/{collection}";
                    try { all.AddRange(await remote.ListAsync(path, cancellation)); }
                    catch (FirestoreDeniedException) { newFeaturesAvailable = false; all.AddRange(Collection(path)); }
                }
                if (membership.Integer("role") == (int)Role.Administrator) all.AddRange(await remote.ListAsync($"farms/{id}/members", cancellation));
                else all.Add(membership);
                await database.MergeAsync(all, $"farms/{id}");
            }
            if (auth.Session?.EmailVerified == true)
            {
                var path = $"invitations/{auth.Session.Email.Trim().ToLowerInvariant()}/farms";
                await database.MergeAsync(await remote.ListAsync(path, cancellation), path + "/");
            }
            await ReloadAsync();
            return new(true, Pending.Count, Pending.Any(x => x.ErrorKind is not null) ? "Hay cambios que requieren revisión. Se conservan en este móvil."
                : !newFeaturesAvailable ? "Datos anteriores sincronizados. Para sincronizar cosechas, tratamientos y cooperativas hay que actualizar las reglas de Firebase."
                : Pending.Count == 0 ? "Sincronizado con Firebase." : "Hay cambios guardados en el móvil pendientes de envío.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            await ReloadAsync(); return new(false, Pending.Count, "No se puede conectar con Firebase. Comprueba tu conexión e inténtalo de nuevo.");
        }
        finally
        {
            // Aplicar también revocaciones ya confirmadas si una descarga posterior falla.
            try { await ReloadAsync(); }
            finally { gate.Release(); }
        }
    }
    public Task InviteAsync(Guid business, string email, Role role) => ChangeAsync(async () =>
    {
        var state = Require(business, Role.Administrator);
        email = email.Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Contains('/') || !Enum.IsDefined(role)) throw new ArgumentException("Indica un correo y un permiso válidos.");
        if (state.Members.Any(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Este usuario ya pertenece a la explotación.");
        var path = $"invitations/{email}/farms/{business:N}";
        var existing = await remote.GetAsync(path);
        if (existing is not null) throw new InvalidOperationException("Ya hay una invitación para este correo. Puedes cancelarla antes de cambiar el permiso.");
        var fields = new JsonObject
        {
            ["farmId"] = FireFields.Text(business.ToString("N")), ["farmName"] = FireFields.Text(state.Business.Name), ["email"] = FireFields.Text(email),
            ["role"] = FireFields.Number((int)role), ["createdBy"] = FireFields.Text(uid), ["createdAt"] = FireFields.Time(DateTimeOffset.UtcNow)
        };
        await remote.CommitAsync([remote.Write(new(path, fields), true)]);
    });
    public Task CancelInvitationAsync(Guid business, string email) => ChangeAsync(async () =>
    {
        Require(business, Role.Administrator);
        var invitation = await remote.GetAsync($"invitations/{email.Trim().ToLowerInvariant()}/farms/{business:N}");
        if (invitation is not null) await remote.CommitAsync([remote.Delete(invitation)]);
    });
    public Task AcceptInvitationAsync(FarmInvitation invitation) => ChangeAsync(async () =>
    {
        await auth.RefreshProfileAsync();
        if (auth.Session?.EmailVerified != true) throw new InvalidOperationException("Verifica primero tu correo electrónico.");
        var email = auth.Session.Email.Trim().ToLowerInvariant();
        if (email != invitation.Email) throw new UnauthorizedAccessException("La invitación pertenece a otro correo.");
        var source = await remote.GetAsync($"invitations/{email}/farms/{invitation.FarmId}");
        if (source is null) throw new InvalidOperationException("La invitación ya no está disponible. Sincroniza para actualizar.");
        var member = await remote.GetAsync($"farms/{invitation.FarmId}/members/{uid}");
        if (member is not null) throw new InvalidOperationException("Ya perteneces a esta explotación. El administrador puede cambiar tus permisos.");
        await remote.CommitAsync([
            remote.Write(new($"farms/{invitation.FarmId}/members/{uid}", MemberFields((Role)source.Integer("role"))), true),
            remote.Write(new($"users/{uid}/farms/{invitation.FarmId}", IndexFields(invitation.FarmId)), true),
            remote.Delete(source)
        ]);
    });
    public Task SetMemberRoleAsync(Guid business, CloudMember member, Role? role) => ChangeAsync(async () =>
    {
        Require(business, Role.Administrator);
        if (Owner(business) == member.UserId) throw new InvalidOperationException("El propietario debe conservar el permiso de administrador.");
        var target = await remote.GetAsync($"{Farm(business)}/members/{member.UserId}") ?? throw new InvalidOperationException("Este usuario ya no pertenece a la explotación.");
        if (role is null)
        {
            var index = await remote.GetAsync($"users/{member.UserId}/farms/{business:N}");
            var writes = new List<JsonObject> { remote.Delete(target) }; if (index is not null) writes.Add(remote.Delete(index));
            await remote.CommitAsync(writes);
        }
        else
        {
            if (!Enum.IsDefined(role.Value)) throw new ArgumentException("Permiso no válido.");
            var fields = target.Fields.Copy(); fields["role"] = FireFields.Number((int)role.Value); fields["updatedAt"] = FireFields.Time(DateTimeOffset.UtcNow);
            await remote.CommitAsync([remote.Write(new(target.Path, fields), false, target.UpdateTime)]);
        }
    });
    public Task RetryAsync(long sequence) => ChangeAsync(async () => { await database.RetryAsync(sequence); await ReloadAsync(); });
    public async Task<FireDocument?> ConflictAsync(long sequence)
    {
        Account(); var pending = Pending.Single(x => x.Sequence == sequence);
        return await remote.GetAsync(pending.Path);
    }
    public Task ResolveAsync(long sequence, FireDocument? reviewedRemote, bool keepLocal) => ChangeAsync(async () =>
    {
        var pending = Pending.Single(x => x.Sequence == sequence);
        if (!keepLocal && pending.Create && Pending.Any(x => x.FarmId == pending.FarmId && x.Sequence != sequence)) throw new InvalidOperationException("Hay cambios pendientes que pueden depender de este registro. Resuélvelos antes de descartar la creación.");
        await database.ResolveAsync(pending, reviewedRemote, keepLocal);
        if (!keepLocal && pending.Kind == "farm.create") await database.RevokeAsync(pending.FarmId, uid);
        await ReloadAsync();
    });
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await database.DisposeAsync(); documents.Clear(); Pending = []; }
        finally { gate.Release(); }
    }
}
