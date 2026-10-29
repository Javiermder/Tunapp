using System.Globalization;
using Tunapp.Core;

namespace Tunapp;

public sealed class FieldWorkViewModel : FormViewModel
{
    public FieldViewModel Plot { get; }
    public FieldViewModel WorkDate { get; }
    public FieldViewModel Description { get; }
    public FieldViewModel Quantity { get; }
    public FieldViewModel Unit { get; }
    public FieldViewModel Cooperative { get; }
    public FieldViewModel Quality { get; }
    public FieldViewModel Cost { get; }
    public FieldViewModel WorkNotes { get; }
    // A stable id survives validation/network retries, preserving idempotent saves.
    private readonly Guid id = Guid.NewGuid();
    public static ScreenViewModel Setup(IAppNavigation navigation, FieldWorkKind kind, string title, string description, string action, Func<Task> configure)
    {
        var screen = new DetailViewModel(kind == FieldWorkKind.Harvest ? "Añadir cosecha" : "Añadir tratamiento", navigation, page =>
        { page.Heading(title); page.Text(description); page.Action(action, configure); });
        return screen;
    }
    public FieldWorkViewModel(IAppNavigation navigation, FieldWorkKind kind, Campaign campaign, IReadOnlyList<Plot> plots,
        IReadOnlyList<Cooperative> cooperatives, Func<FieldWorkInput, Task> save, Func<CooperativeInput, Task>? createCooperative = null)
        : base(kind == FieldWorkKind.Harvest ? "Añadir cosecha" : "Añadir tratamiento", navigation)
    {
        var harvest = kind == FieldWorkKind.Harvest;
        Heading(harvest ? "Una nueva cosecha" : "Cuida tu olivar"); Text($"Campaña {campaign.Name}");
        Plot = Choice("Finca", plots); Plot.Placeholder = "Selecciona una finca";
        WorkDate = Date("Fecha"); WorkDate.Date = DateTime.Today;
        Description = Field(harvest ? "Cultivo" : "Tratamiento o producto", harvest ? "Aceituna" : "", harvest ? "Ej. Aceituna picual" : "Ej. Abonado / nombre del producto"); Description.MaxLength = 160;
        Quantity = Field(harvest ? "Cantidad cosechada (kg)" : "Cantidad aplicada", placeholder: "Ej. 250,5", kind: FieldKind.Numeric);
        Unit = new FieldViewModel("Unidad", FieldKind.Choice); Unit.SetOptions(new[] { "L", "kg", "g", "mL" }); Unit.SelectedIndex = 0;
        if (!harvest) Items.Add(Unit);
        Cooperative = new FieldViewModel("Cooperativa (opcional)", FieldKind.Choice) { Placeholder = "Sin cooperativa" }; Cooperative.SetOptions(cooperatives);
        Quality = new FieldViewModel("Calidad", FieldKind.Choice) { Placeholder = "Selecciona la calidad" }; Quality.SetOptions(new[] { "Árbol", "Suelo" });
        if (harvest)
        {
            Items.Add(Quality); Items.Add(Cooperative);
            if (createCooperative is not null) Action("Añadir cooperativa", async () =>
            {
                var form = new FormViewModel("Añadir cooperativa", navigation);
                var name = form.Field("Nombre", placeholder: "Nombre de la cooperativa"); name.MaxLength = 160;
                var newId = Guid.NewGuid();
                form.Submit("Guardar cooperativa", async () =>
                {
                    await createCooperative(new(newId, name.Text ?? "", ""));
                    var added = new Cooperative(newId, campaign.BusinessId, (name.Text ?? "").Trim(), "");
                    Cooperative.SetOptions(Cooperative.Options.Cast<Cooperative>().Append(added).OrderBy(x => x.Name).ToList());
                    Cooperative.SelectedItem = added;
                });
                await Navigation.PushAsync(form);
            });
            Action("Quitar cooperativa", () => { Cooperative.SelectedItem = null; return Task.CompletedTask; });
        }
        Cost = new FieldViewModel("Coste total (euros, opcional)", FieldKind.Numeric) { Placeholder = "Ej. 125,50" };
        if (!harvest) Items.Add(Cost);
        WorkNotes = Notes(); WorkNotes.MaxLength = 10000;
        Submit(harvest ? "Guardar cosecha" : "Guardar tratamiento", async () =>
        {
            if (Plot.SelectedItem is not Plot selected) throw new ArgumentException("Selecciona una finca.");
            if (WorkDate.Date is not { } day) throw new ArgumentException("Indica la fecha del trabajo.");
            if (!decimal.TryParse(Quantity.Text, NumberStyles.Number, CultureInfo.GetCultureInfo("es-ES"), out var value)) throw new ArgumentException("Indica una cantidad válida, por ejemplo 250,5.");
            if (harvest && Quality.SelectedItem is null) throw new ArgumentException("Selecciona la calidad: Árbol o Suelo.");
            var input = new FieldWorkInput(id, campaign.Id, selected.Id, kind, DateOnly.FromDateTime(day), Description.Text ?? "", value,
                harvest ? "kg" : Unit.SelectedItem?.ToString() ?? "", harvest ? (Cooperative.SelectedItem as Cooperative)?.Id : null,
                WorkNotes.Text ?? "", harvest ? Quality.SelectedItem?.ToString() : null, harvest ? null : Area(Cost));
            FieldWorkValidation.Validate(input);
            await save(input);
        });
    }
}
