using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tunapp;

public sealed record RecordRow(object Item, string Title, string Summary, string Icon, string? Extra);

public interface IRecordsViewModel
{
    ObservableCollection<RecordRow> Rows { get; }
    string SearchText { get; set; }
    string? SubtitleText { get; }
    bool ShowAdd { get; }
    string EmptyTitle { get; }
    string EmptyMessage { get; }
    IAsyncRelayCommand<RecordRow> OpenRecordCommand { get; }
    IAsyncRelayCommand AddRecordCommand { get; }
}

public abstract partial class RecordsViewModel<T> : ScreenViewModel, IRecordsViewModel where T : class
{
    protected readonly ManagementContext Context;
    public ObservableCollection<RecordRow> Rows { get; } = [];
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string? SubtitleText { get; set; }
    [ObservableProperty] public partial bool ShowAdd { get; set; }
    [ObservableProperty] public partial string EmptyTitle { get; set; } = "";
    [ObservableProperty] public partial string EmptyMessage { get; set; } = "";
    protected RecordsViewModel(string title, ManagementContext context, IAppNavigation navigation) : base(navigation) { Title = title; Context = context; }
    partial void OnSearchTextChanged(string value) => Refresh();
    public override Task ActivateAsync() { Refresh(); return Task.CompletedTask; }
    protected abstract IReadOnlyList<T> Read();
    protected abstract string[] SearchValues(T item);
    protected abstract RecordRow Card(T item);
    protected abstract Task OpenAsync(T item);
    protected abstract Task AddAsync();
    protected abstract bool CanAdd { get; }
    protected virtual string? Subtitle => null;
    protected static RecordRow Summary(string title, string summary, string icon, string? extra = null) => new(null!, title, summary, icon, extra);
    [RelayCommand] private Task OpenRecordAsync(RecordRow row) => ExecuteAsync(() => OpenAsync((T)row.Item));
    [RelayCommand] private Task AddRecordAsync() => ExecuteAsync(async () =>
    {
        if (!CanAdd) throw new UnauthorizedAccessException("No tienes permiso para añadir registros.");
        await AddAsync();
    });
    public override void Refresh()
    {
        try
        {
            var all = Read();
            SubtitleText = Subtitle; ShowAdd = CanAdd;
            var matches = all.Where(item => RecordSearch.Matches(SearchText.Trim(), SearchValues(item))).ToList();
            Rows.Clear(); foreach (var item in matches) Rows.Add(Card(item) with { Item = item });
            EmptyTitle = all.Count == 0 ? $"No hay {Title.ToLowerInvariant()}" : "No se encontraron resultados";
            EmptyMessage = all.Count == 0 ? (CanAdd ? "Pulsa Añadir para crear el primer registro." : "Los registros aparecerán aquí.") : "Prueba con otra búsqueda.";
        }
        catch (Exception ex) { Rows.Clear(); ShowAdd = false; SubtitleText = null; EmptyTitle = "No se puede abrir el listado"; EmptyMessage = ex.Message; }
    }
}
