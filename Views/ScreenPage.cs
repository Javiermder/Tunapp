using System.Collections.Specialized;
using System.Windows.Input;

namespace Tunapp;

// Views only create controls, bind values/commands, and forward lifecycle events.
public class ScreenPage : ContentPage, IRefreshableRecords
{
    public ScreenViewModel ViewModel { get; }
    public ScreenPage(ScreenViewModel viewModel)
    {
        ViewModel = viewModel; BindingContext = viewModel; Title = viewModel.Title;
        BackgroundColor = AppDesign.Background;
        Content = new ScreenContent(viewModel);
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.ExecuteAsync(ViewModel.ActivateAsync);
    }
    protected override bool OnBackButtonPressed() => ViewModel.PreventBack || base.OnBackButtonPressed();
    void IRefreshableRecords.RefreshRecords() => ViewModel.Refresh();
}

internal sealed class ScreenContent : ContentView
{
    private readonly ScreenViewModel model;
    private readonly VerticalStackLayout body = new() { Padding = 22, Spacing = 14, MaximumWidthRequest = 760 };
    public ScreenContent(ScreenViewModel model)
    {
        this.model = model; BindingContext = model;
        if (model is IRecordsViewModel records) { Content = Records(records); return; }
        var error = new Label { TextColor = Color.FromArgb("#A33D30"), FontSize = 13 };
        error.SetBinding(Label.TextProperty, nameof(ScreenViewModel.Error));
        var status = new ActivityIndicator { Color = AppDesign.Ink };
        status.SetBinding(ActivityIndicator.IsRunningProperty, nameof(ScreenViewModel.IsBusy));
        status.SetBinding(IsVisibleProperty, nameof(ScreenViewModel.IsBusy));
        var content = new VerticalStackLayout { Spacing = 10 };
        content.Children.Add(body); content.Children.Add(error); content.Children.Add(status);
        if (model is FormViewModel form)
        {
            var submit = Button("", form.SaveCommand); submit.SetBinding(Microsoft.Maui.Controls.Button.TextProperty, nameof(FormViewModel.SubmitLabel));
            var cancel = Button("Cancelar", form.CancelCommand); cancel.BackgroundColor = Colors.Transparent; cancel.TextColor = AppDesign.Muted;
            body.Padding = new Thickness(22, 22, 22, 0);
            content.Children.Add(new VerticalStackLayout { Padding = new Thickness(22, 0, 22, 22), Children = { submit, cancel } });
        }
        Content = new ScrollView { Content = content };
        foreach (var item in model.Items) body.Children.Add(Item(item));
        model.Items.CollectionChanged += ItemsChanged;
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) body.Children.Clear();
        else if (e.NewItems is not null) foreach (var item in e.NewItems) body.Children.Add(Item(item));
    }
    private View Item(object item) => item switch
    {
        TextItem text => text.Heading
            ? new Label { Text = text.Text, FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink }
            : AppDesign.Card(new Label { Text = text.Text, FontSize = 15, TextColor = AppDesign.Muted, LineHeight = 1.25 }),
        ValueItem value => AppDesign.Card(new VerticalStackLayout { Spacing = 6, Children =
            { new Label { Text = value.Label, FontSize = 12, TextColor = AppDesign.Muted }, new Label { Text = value.Text, FontSize = 16, TextColor = AppDesign.Ink } } }),
        ActionItem action => Button(action.Title, action.Command),
        FieldViewModel field => Field(field),
        DashboardItem dashboard => DashboardView.Create(dashboard.Name, dashboard.Campaign, dashboard.Farm,
            dashboard.Tiles.Select(t => new DashboardTile(t.Title, t.Detail, t.Icon, t.Tint, () => Invoke(t.Command))).ToList(),
            () => Invoke(dashboard.Harvest), () => Invoke(dashboard.Treatment), () => Invoke(dashboard.Account),
            () => Invoke(dashboard.Campaigns), () => Invoke(dashboard.History), dashboard.Today),
        _ => throw new InvalidOperationException($"Elemento visual desconocido: {item.GetType().Name}")
    };
    private Button Button(string title, ICommand command)
    {
        var button = new Button { Text = title, Command = command, MinimumHeightRequest = 54, CornerRadius = 14, BackgroundColor = AppDesign.Ink, TextColor = Colors.White };
        button.SetBinding(IsEnabledProperty, new Binding(nameof(ScreenViewModel.IsBusy), converter: new NotConverter(), source: model));
        return button;
    }
    private static void Invoke(ICommand command) { if (command.CanExecute(null)) command.Execute(null); }
    private View Field(FieldViewModel field)
    {
        View control;
        switch (field.Kind)
        {
            case FieldKind.Choice:
                var picker = new Picker { Title = field.Placeholder ?? field.Label, ItemsSource = field.Options, TextColor = AppDesign.Ink };
                picker.SetBinding(Picker.SelectedItemProperty, new Binding(nameof(FieldViewModel.SelectedItem), BindingMode.TwoWay)); control = picker; break;
            case FieldKind.Date:
                var date = new DatePicker { Format = "dd/MM/yyyy", TextColor = AppDesign.Ink };
                date.SetBinding(DatePicker.DateProperty, new Binding(nameof(FieldViewModel.Date), BindingMode.TwoWay)); control = date; break;
            case FieldKind.Notes:
                var editor = new Editor { Placeholder = field.Placeholder, MaxLength = Math.Min(field.MaxLength, 10000), AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 100, TextColor = AppDesign.Ink };
                editor.SetBinding(Editor.TextProperty, new Binding(nameof(FieldViewModel.Text), BindingMode.TwoWay)); control = editor; break;
            default:
                var entry = new Entry { Placeholder = field.Placeholder, MaxLength = field.MaxLength, TextColor = AppDesign.Ink, MinimumHeightRequest = 46,
                    Keyboard = field.Kind == FieldKind.Email ? Keyboard.Email : field.Kind == FieldKind.Numeric ? Keyboard.Numeric : Keyboard.Default,
                    IsPassword = field.Kind == FieldKind.Password };
                entry.SetBinding(Entry.TextProperty, new Binding(nameof(FieldViewModel.Text), BindingMode.TwoWay)); control = entry; break;
        }
        control.BindingContext = field;
        control.SetBinding(IsEnabledProperty, new Binding(nameof(ScreenViewModel.IsBusy), converter: new NotConverter(), source: model));
        var layout = new VerticalStackLayout { Spacing = 4, Children = { new Label { Text = field.Label, FontSize = 12, TextColor = AppDesign.Muted }, control } };
        return AppDesign.Card(layout);
    }
    private View Records(IRecordsViewModel records)
    {
        var search = new SearchBar { Placeholder = $"Buscar {model.Title.ToLowerInvariant()}", TextColor = AppDesign.Ink, BackgroundColor = Colors.Transparent, MinimumHeightRequest = 50 };
        search.SetBinding(SearchBar.TextProperty, new Binding(nameof(IRecordsViewModel.SearchText), BindingMode.TwoWay));
        var add = Button("+ Añadir", records.AddRecordCommand); add.FontSize = 13;
        add.SetBinding(IsVisibleProperty, nameof(IRecordsViewModel.ShowAdd));
        var bar = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        var searchCard = AppDesign.Card(search); searchCard.Padding = 0; bar.Add(searchCard); bar.Add(add, 1);
        var subtitle = new Label { FontSize = 13, TextColor = AppDesign.Muted }; subtitle.SetBinding(Label.TextProperty, nameof(IRecordsViewModel.SubtitleText));
        var list = new CollectionView { ItemsSource = records.Rows, SelectionMode = SelectionMode.None,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) { ItemSpacing = 12 } };
        list.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink, MaxLines = 2 };
            title.SetBinding(Label.TextProperty, nameof(RecordRow.Title));
            var summary = new Label { FontSize = 13, TextColor = AppDesign.Muted, MaxLines = 2 }; summary.SetBinding(Label.TextProperty, nameof(RecordRow.Summary));
            var extra = new Label { FontSize = 12, TextColor = AppDesign.Ink }; extra.SetBinding(Label.TextProperty, nameof(RecordRow.Extra));
            var icon = new Image { HeightRequest = 26, WidthRequest = 26, VerticalOptions = LayoutOptions.Center }; icon.SetBinding(Image.SourceProperty, nameof(RecordRow.Icon));
            var grid = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 14 };
            grid.Add(icon); grid.Add(new VerticalStackLayout { Spacing = 5, Children = { title, summary, extra } }, 1);
            grid.Add(new Label { Text = "›", FontSize = 24, TextColor = AppDesign.Muted, VerticalOptions = LayoutOptions.Center }, 2);
            var card = AppDesign.Card(grid);
            var tap = new TapGestureRecognizer { Command = records.OpenRecordCommand }; tap.SetBinding(TapGestureRecognizer.CommandParameterProperty, "."); card.GestureRecognizers.Add(tap);
            SemanticProperties.SetDescription(card, "Abrir registro"); return card;
        });
        var emptyTitle = new Label { FontSize = 18, TextColor = AppDesign.Ink }; emptyTitle.SetBinding(Label.TextProperty, new Binding(nameof(IRecordsViewModel.EmptyTitle), source: model));
        var emptyMessage = new Label { FontSize = 13, TextColor = AppDesign.Muted }; emptyMessage.SetBinding(Label.TextProperty, new Binding(nameof(IRecordsViewModel.EmptyMessage), source: model));
        list.EmptyView = new VerticalStackLayout { Spacing = 8, Padding = 16, Children = { emptyTitle, emptyMessage } };
        var root = new Grid { Padding = 20, RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, RowSpacing = 18, MaximumWidthRequest = 760 };
        root.Add(new VerticalStackLayout { Spacing = 10, Children = { subtitle, bar } }); root.Add(list, 0, 1); return root;
    }
    private sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is not true;
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
