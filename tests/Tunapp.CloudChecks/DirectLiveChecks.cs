using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Tunapp.Core;

internal static class DirectLiveChecks
{
    public static async Task RunAsync()
    {
        const string project = "tunapp-j27";
        var key = Environment.GetEnvironmentVariable("TUNAPP_FIREBASE_API_KEY") ?? throw new InvalidOperationException("Falta la configuración pública Firebase.");
        // El privilegio Admin solo se usa para verificar correos de prueba y limpiar sus datos.
        // TODAS las operaciones que se comprueban usan FirebaseAuthClient y FirestoreRest de la app.
        var credential = GoogleCredential.GetApplicationDefault();
        var adminApp = FirebaseApp.Create(new AppOptions { Credential = credential, ProjectId = project }, "direct-live-checks");
        var admin = FirebaseAuth.GetAuth(adminApp);
        var db = new FirestoreDbBuilder { ProjectId = project, Credential = credential }.Build();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var run = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), "TunappDirectLiveChecks", run);
        var users = new List<string>();
        var services = new List<DirectFarmService>();
        var farm = Guid.NewGuid();
        var root = db.Collection("farms").Document(farm.ToString("N"));
        if ((await root.GetSnapshotAsync()).Exists) throw new InvalidOperationException("Identificador de prueba ya existente.");
        var checks = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine($"OK: {name}"); }
        async Task Denied(Func<Task> action, string name)
        {
            try { await action(); }
            catch (FirestoreDeniedException) { Check(true, name); return; }
            throw new Exception("No se denegó: " + name);
        }
        async Task<FirebaseAuthClient> Account(string label)
        {
            var auth = new FirebaseAuthClient(http, key);
            await auth.SignUpAsync($"tunapp-direct-{run}-{label}@example.invalid", $"Aa9!{Guid.NewGuid():N}");
            users.Add(auth.Session!.UserId);
            return auth;
        }
        async Task<DirectFarmService> Open(string device, FirebaseAuthClient auth)
        {
            var service = await DirectFarmService.OpenAsync(Path.Combine(folder, device), new FirestoreRest(http, auth, auth.Session!.UserId, project), auth);
            services.Add(service); return service;
        }
        async Task Sync(DirectFarmService service)
        {
            var result = await service.SyncAsync();
            if (!result.Connected || service.Pending.Count != 0) throw new Exception(result.Message + " " + string.Join("; ", service.Pending.Select(x => x.Error)));
        }
        FirebaseAuthClient? guestAuth = null;
        try
        {
            var ownerAuth = await Account("owner");
            guestAuth = await Account("guest");
            var owner = await Open("owner-a", ownerAuth);
            await Sync(owner);
            Check(owner.Businesses.Count == 0, "Cuenta nueva no recibe explotaciones ajenas en Firebase real");
            var campaign = Guid.NewGuid(); var plot = Guid.NewGuid();
            await owner.CreateBusinessAsync(new(farm, $"Comprobación directa {run}"));
            await owner.CreateCampaignAsync(farm, new(campaign, "2026/2027", new(2026, 10, 1), new(2027, 9, 30)));
            await owner.SavePlotAsync(farm, new(plot, "Finca temporal de integración", 2.5m, 250, "Picual", "Se elimina al terminar"));
            await owner.SaveCampaignPlotAsync(farm, campaign, plot, new(2.4m, 240, "Picual", "Datos anuales temporales"));
            await Sync(owner);
            Check(owner.Pending.Count == 0, "Cliente Android C# guarda y confirma sus cambios directamente en Firestore real");
            var second = await Open("owner-b", ownerAuth); await Sync(second);
            Check(second.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 240 && second.Snapshot(farm)!.Plots.Single().OliveTrees == 250, "Otra caché de dispositivo descarga las mismas fincas y datos anuales");
            var guest = await Open("guest", guestAuth); await Sync(guest);
            var guestRemote = new FirestoreRest(http, guestAuth, guestAuth.Session!.UserId, project);
            await Denied(async () => { await guestRemote.GetAsync($"farms/{farm:N}/plots/{plot:N}"); }, "Usuario ajeno no puede leer documentos por su ruta directa");
            await owner.InviteAsync(farm, guestAuth.Session.Email, Role.Reader);
            await guest.SyncAsync();
            Check(guest.Invitations.Count == 0, "Correo sin verificar no recibe la invitación");
            await admin.UpdateUserAsync(new UserRecordArgs { Uid = guestAuth.Session.UserId, EmailVerified = true });
            await guestAuth.RefreshProfileAsync(); await Sync(guest);
            Check(guest.Invitations.Count == 1, "Destinatario verificado recibe su invitación real");
            await guest.AcceptInvitationAsync(guest.Invitations.Single()); await Sync(guest);
            Check(guest.Businesses.Single().Role == Role.Reader && guest.Snapshot(farm)!.Plots.Count == 1, "Familiar acepta y consulta la explotación compartida sin API propia");
            var remotePlot = (await guestRemote.GetAsync($"farms/{farm:N}/plots/{plot:N}"))!;
            var modified = remotePlot.Fields.Copy(); modified["trees"] = FireFields.Number(999);
            await Denied(() => guestRemote.CommitAsync([guestRemote.Write(new(remotePlot.Path, modified), false, remotePlot.UpdateTime)]), "Firestore rechaza escrituras de lector aunque se omita la validación local");
            await Sync(owner);
            var member = owner.Snapshot(farm)!.Members.Single(x => x.UserId == guestAuth.Session.UserId);
            await owner.SetMemberRoleAsync(farm, member, Role.Operator); await Sync(guest);
            Check(guest.Businesses.Single().Role == Role.Operator, "Cambio administrativo de permiso llega a la otra sesión");
            var rawMember = (await guestRemote.GetAsync($"farms/{farm:N}/members/{guestAuth.Session.UserId}"))!;
            var promote = rawMember.Fields.Copy(); promote["role"] = FireFields.Number(2);
            await Denied(() => guestRemote.CommitAsync([guestRemote.Write(new(rawMember.Path, promote), false, rawMember.UpdateTime)]), "Operario no puede elevarse a administrador mediante REST");
            await owner.SetClosedAsync(farm, campaign, true); await Sync(owner);
            var annual = (await guestRemote.GetAsync($"farms/{farm:N}/campaignPlots/{campaign:N}_{plot:N}"))!;
            var changedAnnual = annual.Fields.Copy(); changedAnnual["trees"] = FireFields.Number(239);
            await Denied(() => guestRemote.CommitAsync([guestRemote.Write(new(annual.Path, changedAnnual), false, annual.UpdateTime)]), "Campaña cerrada rechaza una edición directa en Firebase real");
            await owner.SetClosedAsync(farm, campaign, false); await Sync(owner);
            Check(owner.Snapshot(farm)!.CampaignPlots.Single().OliveTrees == 240, "Reapertura conserva todo el histórico anual");
            await owner.SetMemberRoleAsync(farm, member, null); await Sync(guest);
            Check(guest.Businesses.Count == 0 && guest.Snapshot(farm) is null, "Retirar acceso elimina la explotación de la caché visible del familiar");
            await Denied(async () => { await guestRemote.GetAsync($"farms/{farm:N}/plots/{plot:N}"); }, "Acceso revocado también se rechaza por REST");
            Console.WriteLine($"{checks} comprobaciones de conexión directa REAL correctas.");
        }
        finally
        {
            var errors = new List<Exception>();
            foreach (var service in services) { try { await service.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); } }
            try
            {
                var saved = await root.GetSnapshotAsync();
                if (saved.Exists)
                {
                    if (saved.GetValue<string>("name") != $"Comprobación directa {run}") throw new InvalidOperationException("No se elimina un recurso sin la marca exacta de esta prueba.");
                    await foreach (var collection in root.ListCollectionsAsync())
                        foreach (var document in (await collection.GetSnapshotAsync()).Documents) await document.Reference.DeleteAsync();
                    foreach (var uid in users) await db.Collection("users").Document(uid).Collection("farms").Document(farm.ToString("N")).DeleteAsync();
                    await root.DeleteAsync();
                }
                if (guestAuth?.Session is { } guestSession) await db.Collection("invitations").Document(guestSession.Email.ToLowerInvariant()).Collection("farms").Document(farm.ToString("N")).DeleteAsync();
            }
            catch (Exception ex) { errors.Add(ex); }
            foreach (var uid in users)
            {
                try
                {
                    var user = await admin.GetUserAsync(uid);
                    if (!user.Email.StartsWith($"tunapp-direct-{run}-", StringComparison.Ordinal)) throw new InvalidOperationException("Cuenta sin marca de esta prueba.");
                    await admin.DeleteUserAsync(uid);
                }
                catch (Exception ex) { errors.Add(ex); }
            }
            adminApp.Delete();
            if (errors.Count > 0) throw new AggregateException("No se completó la limpieza de pruebas.", errors);
            Console.WriteLine("Cuentas y documentos temporales de Firebase real eliminados.");
        }
    }
}
