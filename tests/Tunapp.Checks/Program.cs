using System.Text.Json;
using Tunapp.Core;

var folder = Path.Combine(Path.GetTempPath(), "TunappChecks", Guid.NewGuid().ToString());
var path = Path.Combine(folder, "state.json");
var service = new FarmService(path);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; Console.WriteLine($"OK: {name}"); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { Check(true, name); return; }
    throw new Exception($"No se rechazó: {name}");
}
await service.LoadAsync();
await service.CreateBusinessAsync("Familia", "Administrador");
var first = service.Business!.Id;
await service.CreateCampaignAsync("2026/2027", new(2026, 10, 1), new(2027, 9, 30));
var campaign = service.SelectedCampaign!.Id;
await service.CreatePlotAsync("Olivar", 2.5m, 250, "Picual", "Acceso sur");
var plot = service.Plots.Single();
await service.LinkPlotAsync(plot.Id, 2.4m, 245, "Picual", "Datos de este año");
Check(service.CampaignPlots.Single().OliveTrees == 245 && plot.OliveTrees == 250, "Datos anuales separados de finca permanente");
await Reject(() => service.LinkPlotAsync(plot.Id, 2, 200, "", ""), "Vínculo duplicado");
await service.SetCampaignClosedAsync(true);
await Reject(() => service.LinkPlotAsync(plot.Id, 2, 200, "", ""), "Campaña cerrada impide cambios");
await service.SetCampaignClosedAsync(false);
Check(service.CampaignPlots.Count == 1, "Reabrir conserva los datos");
await service.CreateCampaignAsync("2027/2028", new(2027, 10, 1), new(2028, 9, 30));
Check(service.CampaignPlots.Count == 1 && service.Plots.Count == 1, "Nueva campaña muestra las fincas existentes");
await Reject(() => service.CreateCampaignAsync("2027/2028", new(2027, 1, 1), new(2028, 1, 1)), "Nombre de campaña duplicado");
await Reject(() => service.CreateCampaignAsync("Fechas", new(2028, 1, 1), new(2027, 1, 1)), "Fechas inválidas");
await service.CreateBusinessAsync("Otra explotación", "Administrador");
Check(service.Plots.Count == 0 && service.Campaigns.Count == 0 && service.CampaignPlots.Count == 0, "Aislamiento de listados entre explotaciones");
await Reject(() => service.SelectCampaignAsync(campaign), "Campaña de otra explotación");
await Reject(() => service.SelectBusinessAsync(Guid.NewGuid()), "Explotación no autorizada");
await service.CreateCampaignAsync("Otra", new(2026, 1, 1), new(2026, 12, 31));
await Reject(() => service.LinkPlotAsync(plot.Id, 2, 200, "", ""), "Finca de otra explotación");
await service.SelectBusinessAsync(first);
await service.SelectCampaignAsync(campaign);
var reloaded = new FarmService(path);
await reloaded.LoadAsync();
Check(reloaded.SelectedCampaign?.Id == campaign && reloaded.CampaignPlots.Count == 1 && reloaded.PendingCount > 0, "Persistencia y cola local tras reinicio");
Check(File.Exists(path + ".bak"), "Copia anterior conservada");
// Cambiar el rol en una copia permite comprobar el servicio sin simular autenticación.
var data = JsonSerializer.Deserialize<LocalData>(await File.ReadAllTextAsync(path))!;
var membership = data.Members.Single(x => x.BusinessId == first);
data.Members[data.Members.IndexOf(membership)] = membership with { Role = Role.Reader };
var readerPath = Path.Combine(folder, "reader.json");
await File.WriteAllTextAsync(readerPath, JsonSerializer.Serialize(data));
var reader = new FarmService(readerPath); await reader.LoadAsync();
await Reject(() => reader.CreatePlotAsync("Prohibida", 1, 1, "", ""), "Lector no puede crear fincas");
await Reject(() => reader.SetCampaignClosedAsync(true), "Lector no puede cerrar ni reabrir campañas");
var before = await File.ReadAllTextAsync(readerPath);
await Reject(() => reader.CreateCampaignAsync("Prohibida", new(2026, 1, 1), new(2027, 1, 1)), "Lector no puede crear campañas");
Check(before == await File.ReadAllTextAsync(readerPath), "Operación rechazada no altera archivo");
await service.UpdatePlotAsync(plot.Id, "Olivar corregido", 3, 300, "Picual", "Nueva nota");
Check(service.CampaignPlots.Single().OliveTrees == 245, "Editar finca permanente conserva histórico anual");
await service.UpdateCampaignPlotAsync(plot.Id, 2.3m, 240, "Picual", "Revisión");
Check(service.CampaignPlots.Single().OliveTrees == 240 && service.Plots.Single().OliveTrees == 300, "Editar datos anuales conserva finca permanente");
await service.SetCampaignClosedAsync(true);
await Reject(() => service.UpdateCampaignPlotAsync(plot.Id, 2, 200, "", ""), "Cierre bloquea edición de datos anuales");
// Una ruta de destino imposible provoca fallo de guardado y debe revertir la memoria.
var blockedPath = Path.Combine(folder, "blocked"); Directory.CreateDirectory(blockedPath);
var failing = new FarmService(blockedPath);
try { await failing.CreateBusinessAsync("No guardada", "Admin"); throw new Exception("Se esperaba fallo de disco"); }
catch (IOException) { Check(failing.Businesses.Count == 0 && failing.Business is null, "Fallo de disco revierte estado en memoria"); }

await service.SelectBusinessAsync(first);
await service.CreateCampaignAsync("Sin fechas", null, null);
Check(service.SelectedCampaign!.Start is null && service.SelectedCampaign.End is null, "Fechas opcionales");
await service.CreatePlotAsync("Sin superficie", null, 0, "", "");
var optional = service.Plots.Single(x => x.Name == "Sin superficie");
Check(optional.Hectares is null && service.CampaignPlots.Count == service.Plots.Count, "Superficie opcional y finca visible en la campana activa");
await service.UpdateCampaignPlotAsync(plot.Id, null, 12, "", "Solo esta campana");
await service.SelectCampaignAsync(campaign);
Check(service.CampaignPlots.Single(x => x.PlotId == plot.Id).OliveTrees != 12, "Editar campana no cambia el historico");
var restored = new FarmService(path); await restored.LoadAsync();
Check(restored.Plots.Single(x => x.Id == optional.Id).Hectares is null, "Superficie vacia persiste tras reiniciar");
await service.SelectCampaignAsync(service.Campaigns.Single(x => x.Name == "Sin fechas").Id);
var coop = Guid.NewGuid();
await service.CreateCooperativeAsync(new(coop, "Cooperativa de prueba", ""));
var harvest = new FieldWorkInput(Guid.NewGuid(), service.SelectedCampaign!.Id, plot.Id, FieldWorkKind.Harvest,
    new(2026, 10, 27), "Aceituna", 125.5m, "kg", coop, "Entrada sur");
await service.CreateFieldWorkAsync(harvest);
await service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Kind = FieldWorkKind.Treatment, Description = "Abonado", Quantity = 2, Unit = "L", CooperativeId = null });
Check(service.FieldWorks.Count == 2 && service.FieldWorks.Single(x => x.Kind == FieldWorkKind.Harvest).Quantity == 125.5m, "Cosecha y tratamiento se guardan en la campaña activa");
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Quantity = 0 }), "Cantidad inválida rechazada");
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), PlotId = Guid.NewGuid() }), "Finca ajena rechazada en cosecha");
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), CooperativeId = Guid.NewGuid() }), "Cooperativa ajena rechazada");
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), CampaignId = campaign }), "No se guarda trabajo en otra campaña");
await service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Quality = "Árbol", Quantity = 10.125m });
await service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Quality = "Suelo", Quantity = 20.25m });
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Quality = "Otra" }), "Calidad invalida rechazada");
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid(), Kind = FieldWorkKind.Treatment, CooperativeId = null, Quality = "Suelo" }), "Tratamiento rechaza calidad");
var totalsReloaded = new FarmService(path); await totalsReloaded.LoadAsync();
var scoped = totalsReloaded.FieldWorks.ToList();
var basis = scoped.First(x => x.Kind == FieldWorkKind.Harvest);
scoped.Add(basis with { Id = Guid.NewGuid(), BusinessId = Guid.NewGuid(), Quantity = 999 });
scoped.Add(basis with { Id = Guid.NewGuid(), CampaignId = Guid.NewGuid(), Quantity = 999 });
scoped.Add(basis with { Id = Guid.NewGuid(), CooperativeId = Guid.NewGuid(), Quantity = 999 });
var totals = HarvestTotals.ForCooperative(scoped, basis.BusinessId, basis.CampaignId, coop);
Check(totals == new HarvestTotals(155.875m, 10.125m, 20.25m, 125.5m), "Totales persistidos por cooperativa y calidad excluyen tratamientos y registros ajenos");
await service.SetCampaignClosedAsync(true);
await Reject(() => service.CreateFieldWorkAsync(harvest with { Id = Guid.NewGuid() }), "Campaña cerrada rechaza nuevos trabajos");
var workReloaded = new FarmService(path); await workReloaded.LoadAsync();
Check(workReloaded.FieldWorks.Count == 4 && workReloaded.Cooperatives.Single().Id == coop, "Cosechas, tratamientos y cooperativas persisten tras reiniciar");
await service.SelectCampaignAsync(campaign);
Check(service.FieldWorks.Count == 0, "Cambiar de campaña cambia el historial");
await service.CreateBusinessAsync("Aislada", "Admin");
Check(service.FieldWorks.Count == 0 && service.Cooperatives.Count == 0, "Historial y cooperativas aislados por explotación");
await service.SelectBusinessAsync(first);
await service.SelectCampaignAsync(campaign);
var anotherCampaign = service.Campaigns.First(x => x.Id != campaign);
await service.SetCampaignClosedAsync(anotherCampaign.Id, false);
Check(service.SelectedCampaign?.Id == campaign && service.Campaigns.Single(x => x.Id == anotherCampaign.Id).Status == CampaignStatus.Open,
    "Detalle de otra campaña cambia su estado sin cambiar la selección activa");
await Reject(() => service.SetCampaignClosedAsync(Guid.NewGuid(), false), "Detalle no modifica campañas ajenas");
using var annualBefore = JsonDocument.Parse(await File.ReadAllTextAsync(path));
var historic = annualBefore.RootElement.GetProperty("CampaignPlots").GetRawText();
await service.UpdatePlotAsync(plot.Id, "Ficha común", null, 315, "Hojiblanca", "Notas comunes");
foreach (var season in service.Campaigns.ToList())
{
    await service.SelectCampaignAsync(season.Id);
    var common = service.Plots.Single(x => x.Id == plot.Id);
    Check(common.Name == "Ficha común" && common.Hectares is null && common.OliveTrees == 315 && common.Variety == "Hojiblanca",
        "La ficha común no depende de la campaña seleccionada: " + season.Name);
}
using var annualAfter = JsonDocument.Parse(await File.ReadAllTextAsync(path));
Check(annualAfter.RootElement.GetProperty("CampaignPlots").GetRawText() == historic, "Editar ficha común no borra ni sobrescribe datos anuales antiguos");
Check(reader.CurrentRole == Role.Reader && service.CurrentRole == Role.Administrator, "Los listados reciben el permiso del usuario local actual");
await Reject(() => reader.SetCampaignClosedAsync(reader.SelectedCampaign!.Id, false), "Nuevo cierre desde detalle mantiene permisos de lector");
Check(Tunapp.RecordSearch.Matches("  CÁENA ", ["Caena", "Hojiblanca", ""]) && Tunapp.RecordSearch.Matches("CAMPAÑA", ["Campana 2026"]), "Búsqueda ignora mayúsculas, tildes y espacios exteriores");
Check(Tunapp.RecordSearch.Matches("acceso", ["Caena", "Hojiblanca", "Acceso sur"]) && !Tunapp.RecordSearch.Matches("inexistente", ["Caena", "Hojiblanca", "Acceso sur"]), "Búsqueda usa notas y distingue búsquedas sin resultados");
Check(Tunapp.RecordSearch.Matches("", ["Caena"]) && !Tunapp.RecordSearch.Matches("caena", []), "Búsqueda vacía conserva registros sin inventar filas");
var statsService = new FarmService(Path.Combine(folder, "prices.json"));
await statsService.LoadAsync();
await statsService.CreateBusinessAsync("Estadisticas", "Admin");
await statsService.CreateCampaignAsync("Actual", null, null);
await statsService.CreatePlotAsync("Finca", 2, 100, "", "");
var sp = statsService.Plots.Single(); var sc = statsService.SelectedCampaign!.Id;
var co = Guid.NewGuid();
await statsService.CreateCooperativeAsync(new(co, "Coop", ""));
await statsService.SaveCooperativePriceAsync(sc, co, "Árbol", 0.75m);
await statsService.SaveCooperativePriceAsync(sc, co, "Suelo", 0.5m);
FieldWorkInput Input(FieldWorkKind kind, decimal quantity, string? quality = null, decimal? cost = null) => new(Guid.NewGuid(), sc, sp.Id, kind, new(2026,10,28), "Trabajo", quantity, "kg", kind == FieldWorkKind.Harvest ? co : null, "", quality, cost);
await statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Harvest, 100, "Árbol"));
await statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Harvest, 50, "Suelo"));
await statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Harvest, 25));
await statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Treatment, 1, cost:20));
await statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Treatment, 1));
var st = PlotStatistics.Calculate(sp, sc, statsService.FieldWorks, statsService.CooperativePrices);
Check(st.Kilograms == 175 && st.KilogramsPerTree == 1.75m && st.KilogramsPerHectare == 87.5m, "Produccion y promedios");
Check(st.Income == 100 && st.UnvaluedKilograms == 25 && st.IncomePerTree == 1, "Ingresos por calidad y kilos sin valorar");
Check(st.TreatmentCost == 20 && st.CostPerTree == 0.2m && st.UnknownCosts == 1, "Costes parciales de tratamientos");
var empty = PlotStatistics.Calculate(sp with { OliveTrees = 0, Hectares = null }, sc, [], []);
Check(empty.KilogramsPerTree is null && empty.KilogramsPerHectare is null, "Sin division por cero");
var otherCoop = Guid.NewGuid();
var extra = new FieldWork(Guid.NewGuid(), sp.BusinessId, sc, sp.Id, FieldWorkKind.Harvest, new(2026,10,28), "Aceituna", 10, "kg", otherCoop, "", "Suelo");
var mixed = PlotStatistics.Calculate(sp, sc, statsService.FieldWorks.Append(extra), statsService.CooperativePrices.Append(new(sp.BusinessId, sc, otherCoop, "Suelo", 2)));
Check(mixed.Income == 120 && mixed.Kilograms == 185, "Entregas a varias cooperativas aplican su propio precio");
Check(PlotStatistics.Calculate(sp with { Id = Guid.NewGuid() }, sc, statsService.FieldWorks, statsService.CooperativePrices).TreatmentCost == 0, "Estadisticas aisladas por finca");
await statsService.SaveCooperativePriceAsync(sc, co, "Árbol", 0);
Check(PlotStatistics.Calculate(sp, sc, statsService.FieldWorks, statsService.CooperativePrices).Income == 25, "Precio cero y cambio actualizan estimacion");
await Reject(() => statsService.SaveCooperativePriceAsync(sc, co, "Suelo", 0.12345m), "Precio rechaza exceso de decimales");
await Reject(() => statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Treatment, 1, cost:-1)), "Coste negativo rechazado");
await Reject(() => statsService.CreateFieldWorkAsync(Input(FieldWorkKind.Treatment, 1, cost:1.001m)), "Coste rechaza exceso de decimales");
await statsService.SetCampaignClosedAsync(true);
await Reject(() => statsService.SaveCooperativePriceAsync(sc, co, "Suelo", 1), "Campana cerrada bloquea precios");
var reloadedStats = new FarmService(Path.Combine(folder, "prices.json")); await reloadedStats.LoadAsync();
Check(reloadedStats.CooperativePrices.Count == 2 && reloadedStats.FieldWorks.Single(x => x.Cost != null).Cost == 20, "Precios y costes persisten tras reinicio");
Check(PlotStatistics.Calculate(sp with { BusinessId = Guid.NewGuid() }, sc, statsService.FieldWorks, statsService.CooperativePrices).Kilograms == 0, "Estadisticas aisladas por empresa");
Check(PlotStatistics.Calculate(sp, Guid.NewGuid(), statsService.FieldWorks, statsService.CooperativePrices).Income == 0, "Estadisticas aisladas por campana");
Console.WriteLine($"{checks} comprobaciones correctas. Archivos temporales: {folder}");
