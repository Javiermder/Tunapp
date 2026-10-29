namespace Tunapp;

public sealed class ConnectionViewModel : ScreenViewModel
{
    public override bool IsConnectionScreen => true;
    public override bool PreventBack => true;
    public ConnectionViewModel(IAppNavigation navigation, Func<Task> retry, Func<Task> home) : base(navigation)
    {
        Title = "Conexión necesaria";
        Heading("Sin conexión"); Text("Conecta a Internet para continuar. Tus datos están en Firebase.");
        Action("Reintentar", retry); Action("Volver al inicio", home);
    }
}
