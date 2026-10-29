namespace Tunapp;

public sealed class CloudPage : ScreenPage
{
    private readonly CloudViewModel model;
    private readonly IDispatcherTimer timer;
    public CloudPage(CloudViewModel model) : base(model)
    {
        this.model = model;
        Shell.SetNavBarIsVisible(this, false);
        timer = Dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(60);
        timer.Tick += async (_, _) => await model.BackgroundSyncCommand.ExecuteAsync(null);
        Connectivity.Current.ConnectivityChanged += ConnectivityChanged;
    }
    protected override void OnAppearing() { base.OnAppearing(); timer.Start(); }
    private void ConnectivityChanged(object? sender, ConnectivityChangedEventArgs args) =>
        Dispatcher.Dispatch(async () => await model.ConnectionChangedCommand.ExecuteAsync(null));
}
