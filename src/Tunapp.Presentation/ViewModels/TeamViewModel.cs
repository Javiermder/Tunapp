using Tunapp.Core;

namespace Tunapp;

public sealed class TeamViewModel(ManagementContext context, IAppNavigation navigation) : RecordsViewModel<TeamPerson>("Equipo", context, navigation)
{
    protected override IReadOnlyList<TeamPerson> Read() => Context.Team();
    protected override string[] SearchValues(TeamPerson item) => [item.Name, ManagementContext.RoleName(item.Role)];
    protected override bool CanAdd => Context.Invite is not null && Context.Role() == Role.Administrator;
    protected override RecordRow Card(TeamPerson item) => Summary(item.Name, ManagementContext.RoleName(item.Role), "icon_team.png");
    protected override Task AddAsync()
    {
        var form = new FormViewModel("Invitar usuario", Navigation);
        var email = form.Field("Correo electrónico", placeholder: "nombre@ejemplo.com", kind: FieldKind.Email); email.MaxLength = 320;
        var role = form.Choice("Permiso", new[] { "Solo lectura", "Operario", "Administrador" }); role.SelectedIndex = 0;
        form.Text("El usuario verá la invitación cuando entre con ese correo verificado. Necesitas conexión para enviarla.");
        form.Submit("Enviar invitación", async () =>
        {
            if (Context.Invite is null || Context.Role() != Role.Administrator) throw new UnauthorizedAccessException("No tienes permiso para invitar usuarios.");
            await Context.Invite(email.Text ?? "", (Role)role.SelectedIndex);
        });
        if (Context.CancelInvitation is not null)
        {
            form.Action("Cancelar invitación pendiente de este correo", async () =>
            {
                if (string.IsNullOrWhiteSpace(email.Text)) throw new ArgumentException("Indica el correo de la invitación.");
                if (await Navigation.ConfirmAsync("Cancelar invitación", $"¿Cancelar la invitación de {email.Text}?", "Confirmar", "Volver"))
                {
                    await Context.CancelInvitation(email.Text);
                    await Navigation.AlertAsync("Invitación cancelada", "Puedes enviar una nueva invitación a este correo.", "Aceptar");
                }
            });
        }
        return Navigation.PushAsync(form);
    }
    protected override Task OpenAsync(TeamPerson item) => Navigation.PushAsync(new DetailViewModel("Usuario", Navigation, page =>
    {
        var person = Context.Team().Single(x => x.Id == item.Id);
        page.Heading(person.Name); page.Value("Permiso", ManagementContext.RoleName(person.Role));
        if (!person.CanManage || Context.SetRole is null || Context.Role() != Role.Administrator) return;
        page.Action("Cambiar permiso", async () =>
        {
            var form = new FormViewModel("Cambiar permiso", Navigation);
            var permission = form.Choice("Permiso", new[] { "Solo lectura", "Operario", "Administrador" }); permission.SelectedIndex = (int)person.Role;
            form.Submit("Guardar permiso", async () =>
            {
                if (await Navigation.ConfirmAsync("Cambiar permiso", $"¿Cambiar el permiso de {person.Name} a {permission.SelectedItem}?", "Confirmar", "Cancelar"))
                    await Context.SetRole(person.Id, (Role)permission.SelectedIndex);
                else throw new OperationCanceledException("No se ha cambiado el permiso.");
            });
            await page.Navigator.PushAsync(form);
        });
        page.Action("Retirar acceso", async () =>
        {
            if (!await page.Confirm("Retirar acceso", $"¿Retirar el acceso de {person.Name}?", "Retirar", "Cancelar")) return;
            await Context.SetRole(person.Id, null); await page.Navigator.PopAsync();
        });
    }));
}
