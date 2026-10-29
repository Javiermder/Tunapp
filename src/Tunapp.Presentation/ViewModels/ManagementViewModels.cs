namespace Tunapp;

public static class ManagementViewModels
{
    public static ScreenViewModel? Create(string section, ManagementContext context, IAppNavigation navigation) => section switch
    {
        "Fincas" => new PlotsViewModel(context, navigation),
        "Campañas" => new CampaignsViewModel(context, navigation),
        "Cooperativas" => new CooperativesViewModel(context, navigation),
        "Usuarios" or "Equipo" => new TeamViewModel(context, navigation),
        "Historial" => new HistoryViewModel(context, navigation),
        _ => null
    };
}
