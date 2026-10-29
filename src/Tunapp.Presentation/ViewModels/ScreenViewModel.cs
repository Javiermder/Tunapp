using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tunapp;

public interface IAppNavigation
{
    Task PushAsync(ScreenViewModel screen);
    Task PopAsync();
    Task PopToRootAsync();
    Task ShowConnectionAsync(ScreenViewModel screen);
    Task HideConnectionAsync();
    bool HasChildPage { get; }
    void RefreshCurrent();
    Task AlertAsync(string title, string message, string accept);
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
    Task<string?> ChooseAsync(string title, string cancel, params string[] options);
}

public interface IAppEnvironment
{
    string DataDirectory { get; }
    bool IsConnected { get; }
    Task<string?> ReadSessionAsync(string key);
    Task WriteSessionAsync(string key, string? value);
}

public sealed record TextItem(string Text, bool Heading = false);
public sealed record ValueItem(string Label, string Text);
public sealed record ActionItem(string Title, ICommand Command);
public sealed record DashboardTileModel(string Title, string Detail, string Icon, string Tint, ICommand Command);
public sealed record DashboardItem(string Name, string? Campaign, string Farm,
    IReadOnlyList<DashboardTileModel> Tiles, ICommand Harvest, ICommand Treatment, ICommand Account,
    ICommand Campaigns, ICommand History, IEnumerable<Core.FieldWork> Today);

public enum FieldKind { Text, Notes, Date, Choice, Numeric, Email, Password }

public partial class FieldViewModel(string label, FieldKind kind = FieldKind.Text) : ObservableObject
{
    public string Label { get; } = label;
    public FieldKind Kind { get; } = kind;
    public string? Placeholder { get; set; }
    public int MaxLength { get; set; } = int.MaxValue;
    public ObservableCollection<object> Options { get; } = [];
    [ObservableProperty] public partial string? Text { get; set; }
    [ObservableProperty] public partial DateTime? Date { get; set; }
    [ObservableProperty] public partial object? SelectedItem { get; set; }
    public int SelectedIndex { get => Options.IndexOf(SelectedItem!); set => SelectedItem = value >= 0 && value < Options.Count ? Options[value] : null; }
    public void SetOptions(IEnumerable<object> options) { Options.Clear(); foreach (var item in options) Options.Add(item); }
}

public abstract partial class ScreenViewModel(IAppNavigation navigation) : ObservableObject
{
    protected IAppNavigation Navigation { get; } = navigation;
    public string Title { get; protected set; } = "";
    public ObservableCollection<object> Items { get; } = [];
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    partial void OnIsBusyChanged(bool value) => BusyChanged();
    protected virtual void BusyChanged() { }
    public virtual Task ActivateAsync() => Task.CompletedTask;
    public virtual void Refresh() { }
    public virtual bool PreventBack => IsBusy;
    public virtual bool IsConnectionScreen => false;
    public void Heading(string text) => Items.Add(new TextItem(text, true));
    public void Text(string text, bool heading = false) => Items.Add(new TextItem(text, heading));
    public void Value(string label, string? text) { if (!string.IsNullOrWhiteSpace(text)) Items.Add(new ValueItem(label, text)); }
    public FieldViewModel Field(string label, string? value = null, string? placeholder = null, FieldKind kind = FieldKind.Text)
    {
        var field = new FieldViewModel(label, kind) { Text = value, Placeholder = placeholder ?? label };
        Items.Add(field); return field;
    }
    public FieldViewModel Choice(string label, IEnumerable<object> options, object? selected = null)
    {
        var field = new FieldViewModel(label, FieldKind.Choice) { SelectedItem = selected };
        field.SetOptions(options); Items.Add(field); return field;
    }
    public FieldViewModel Notes(string? value = null) => Field("Notas (opcional)", value, "Algo que quieras recordar…", FieldKind.Notes);
    public FieldViewModel Date(string label)
    {
        var field = new FieldViewModel(label, FieldKind.Date); Items.Add(field); return field;
    }
    public void Action(string title, Func<Task> action) => Items.Add(new ActionItem(title, Command(action)));
    protected ICommand Command(Func<Task> action) => new AsyncRelayCommand(() => ExecuteAsync(action));
    // One execution gate per screen, shared by every command, including dynamic actions.
    public async Task ExecuteAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Error = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; await Navigation.AlertAsync("No se pudo completar", ex.Message, "Aceptar"); }
        finally { IsBusy = false; }
    }
    protected Task DisplayAlertAsync(string title, string message, string accept) => Navigation.AlertAsync(title, message, accept);
    protected Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) => Navigation.ConfirmAsync(title, message, accept, cancel);
    protected Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] options) => Navigation.ChooseAsync(title, cancel, options);
}

public partial class FormViewModel(string title, IAppNavigation navigation) : ScreenViewModel(navigation)
{
    private Func<Task>? save;
    private string submitLabel = "Guardar";
    public string SubmitLabel => IsBusy ? "Guardando…" : submitLabel;
    public bool CanSubmit => !IsBusy;
    public void Submit(string label, Func<Task> action) { Title = title; submitLabel = label; save = action; }
    protected override void BusyChanged() { OnPropertyChanged(nameof(SubmitLabel)); SaveCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true; Error = null;
        try { await (save ?? throw new InvalidOperationException("No se ha configurado el guardado."))(); await Navigation.PopAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanSubmit))] private Task CancelAsync() => Navigation.PopAsync();
    public static decimal? Area(FieldViewModel input)
    {
        if (string.IsNullOrWhiteSpace(input.Text)) return null;
        return decimal.TryParse(input.Text, NumberStyles.Number, CultureInfo.GetCultureInfo("es-ES"), out var value)
            ? value : throw new ArgumentException("Indica una superficie válida, por ejemplo 2,5.");
    }
    public static int Trees(FieldViewModel input) => int.TryParse(input.Text, out var value) ? value : throw new ArgumentException("Indica un número entero de olivos.");
}

public sealed class DetailViewModel : ScreenViewModel
{
    private readonly Action<DetailViewModel> render;
    public DetailViewModel(string title, IAppNavigation navigation, Action<DetailViewModel> render) : base(navigation) { Title = title; this.render = render; }
    public override Task ActivateAsync() { Reload(); return Task.CompletedTask; }
    public override void Refresh() => Reload();
    public void Reload()
    {
        Items.Clear();
        try { render(this); }
        catch (Exception ex) { Items.Clear(); Value("No se puede abrir este registro", ex.Message); }
    }
    public IAppNavigation Navigator => Navigation;
    public Task Confirm(string title, string message, string accept) => Navigation.AlertAsync(title, message, accept);
    public Task<bool> Confirm(string title, string message, string accept, string cancel) => Navigation.ConfirmAsync(title, message, accept, cancel);
}
