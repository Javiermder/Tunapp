namespace Tunapp;

internal sealed class MauiNavigationService : IAppNavigation
{
    private Page? connectionPage;
    private INavigation Navigation => Shell.Current.Navigation;
    private Page Current => Navigation.ModalStack.LastOrDefault() ?? Navigation.NavigationStack.LastOrDefault() ?? Shell.Current.CurrentPage;
    public bool HasChildPage => Navigation.NavigationStack.Count > 1;
    public Task PushAsync(ScreenViewModel screen) => Navigation.PushAsync(new ScreenPage(screen));
    public Task PopAsync() => Navigation.PopAsync();
    public Task PopToRootAsync() => Navigation.PopToRootAsync();
    public async Task ShowConnectionAsync(ScreenViewModel screen)
    {
        if (connectionPage is not null) return;
        connectionPage = new ScreenPage(screen);
        try { await Navigation.PushModalAsync(connectionPage); }
        catch { connectionPage = null; throw; }
    }
    public async Task HideConnectionAsync()
    {
        if (connectionPage is null) return;
        if (Navigation.ModalStack.LastOrDefault() == connectionPage) await Navigation.PopModalAsync();
        connectionPage = null;
    }
    public void RefreshCurrent() { if (Current is IRefreshableRecords records) records.RefreshRecords(); }
    public Task AlertAsync(string title, string message, string accept) => Current.DisplayAlertAsync(title, message, accept);
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Current.DisplayAlertAsync(title, message, accept, cancel);
    public async Task<string?> ChooseAsync(string title, string cancel, params string[] options) => await Current.DisplayActionSheetAsync(title, cancel, null, options);
}
