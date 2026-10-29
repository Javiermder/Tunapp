using System.Globalization;
using Tunapp.Core;

namespace Tunapp;

internal sealed record DashboardTile(string Title, string Detail, string Icon, string Tint, Action Open);

internal static class DashboardView
{
    internal static View Create(string name, string? campaign, string farm, IReadOnlyList<DashboardTile> tiles,
        Action harvest, Action treatment, Action account, Action campaigns, Action history, IEnumerable<FieldWork> today)
    {
        var layout = new VerticalStackLayout { Spacing = 24 };
        var top = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        var greeting = new VerticalStackLayout { Spacing = 4 };
        greeting.Children.Add(new Label { Text = "TUNAPP", FontSize = 11, CharacterSpacing = 3, TextColor = AppDesign.Muted, FontAttributes = FontAttributes.Bold });
        greeting.Children.Add(new Label { Text = $"Hola, {name}", FontSize = 30, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink });
        greeting.Children.Add(new Label { Text = DateTime.Today.ToString("dddd, d 'de' MMMM", CultureInfo.GetCultureInfo("es-ES")), FontSize = 13, TextColor = AppDesign.Muted });
        top.Add(greeting);
        var avatar = new Border { WidthRequest = 46, HeightRequest = 46, BackgroundColor = Color.FromArgb("#E4EBDD"), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 }, VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = name[..1].ToUpperInvariant(), FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, TextColor = AppDesign.Ink } };
        AppDesign.Tap(avatar, account, "Mi cuenta"); top.Add(avatar, 1); layout.Children.Add(top);

        var context = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 12 };
        var contextText = new VerticalStackLayout { Spacing = 4 };
        contextText.Children.Add(new Label { Text = "CAMPAÑA ACTIVA", FontSize = 10, CharacterSpacing = 1.5, TextColor = AppDesign.Muted, FontAttributes = FontAttributes.Bold });
        contextText.Children.Add(new Label { Text = campaign ?? "Selecciona una campaña", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink });
        if (!string.IsNullOrWhiteSpace(farm)) contextText.Children.Add(new Label { Text = farm, FontSize = 12, TextColor = AppDesign.Muted });
        context.Add(contextText); context.Add(new Label { Text = "›", FontSize = 26, TextColor = AppDesign.Muted, VerticalOptions = LayoutOptions.Center }, 1);
        var contextCard = AppDesign.Card(context); AppDesign.Tap(contextCard, campaigns, "Cambiar campaña activa"); layout.Children.Add(contextCard);

        var prompt = new VerticalStackLayout { Spacing = 14 };
        prompt.Children.Add(new Label { Text = "¿Qué hemos hecho hoy?", FontSize = 23, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink });
        prompt.Children.Add(new Label { Text = "Cada jornada cuenta. Guarda la de hoy.", FontSize = 13, TextColor = AppDesign.Muted });
        var actions = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10 };
        actions.Add(QuickAction("Añadir vale", "icon_harvest_light.png", "#214D3A", Colors.White, harvest));
        actions.Add(QuickAction("Añadir tratamiento", "icon_treatment.png", "#FFFFFF", AppDesign.Ink, treatment), 1);
        prompt.Children.Add(actions);
        var hero = AppDesign.Card(prompt); hero.BackgroundColor = Color.FromArgb("#E9EEDB"); hero.StrokeThickness = 0; hero.Padding = 20; layout.Children.Add(hero);

        var section = new VerticalStackLayout { Spacing = 14 };
        section.Children.Add(new Label { Text = "Tu explotación", FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink });
        var grid = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 14, RowSpacing = 14 };
        for (var i = 0; i < tiles.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new(GridLength.Auto));
            var tile = tiles[i];
            var content = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, RowSpacing = 4 };
            content.Add(new Border { WidthRequest = 42, HeightRequest = 42, Padding = 10, StrokeThickness = 0, BackgroundColor = Color.FromArgb(tile.Tint),
                HorizontalOptions = LayoutOptions.Start, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 13 },
                Content = new Image { Source = tile.Icon, WidthRequest = 22, HeightRequest = 22 } });
            content.Add(new Label { Text = tile.Title, FontSize = 17, TextColor = AppDesign.Ink, FontAttributes = FontAttributes.Bold }, 0, 2);
            content.Add(new Label { Text = tile.Detail, FontSize = 12, TextColor = AppDesign.Muted, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 }, 0, 3);
            var card = AppDesign.Card(content); card.HeightRequest = 154; card.Padding = 18;
            card.SizeChanged += (_, _) => { if (card.Width > 0 && Math.Abs(card.HeightRequest - card.Width) > 1) card.HeightRequest = card.Width; };
            AppDesign.Tap(card, tile.Open, $"Abrir {tile.Title}"); grid.Add(card, i % 2, i / 2);
        }
        section.Children.Add(grid); layout.Children.Add(section);

        var activity = new VerticalStackLayout { Spacing = 12 };
        var activityTitle = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        activityTitle.Add(new Label { Text = "Hoy en el campo", FontSize = 19, FontAttributes = FontAttributes.Bold, TextColor = AppDesign.Ink });
        var seeAll = new Label { Text = "Ver todo  ›", FontSize = 13, TextColor = AppDesign.Ink, VerticalOptions = LayoutOptions.Center }; AppDesign.Tap(seeAll, history, "Ver historial"); activityTitle.Add(seeAll, 1);
        activity.Children.Add(activityTitle);
        var entries = today.ToList();
        if (entries.Count == 0)
            activity.Children.Add(AppDesign.Empty("El día está por escribir", "Aquí verás las cosechas y tratamientos que registres hoy.", "icon_history.png"));
        else foreach (var work in entries.Take(3)) activity.Children.Add(AppDesign.Card(new Label { Text = $"{(work.Kind == FieldWorkKind.Harvest ? "Cosecha" : "Tratamiento")} · {work.Description}\n{work.Quantity:N2} {work.Unit}", FontSize = 14, TextColor = AppDesign.Ink, LineHeight = 1.4 }));
        layout.Children.Add(activity);
        return layout;
    }
    private static View QuickAction(string title, string icon, string color, Color text, Action open)
    {
        var content = new VerticalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center };
        content.Children.Add(new Image { Source = icon, HeightRequest = 24, WidthRequest = 24, HorizontalOptions = LayoutOptions.Start });
        content.Children.Add(new Label { Text = title, FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = text, MaxLines = 2 });
        var card = AppDesign.Card(content); card.BackgroundColor = Color.FromArgb(color); card.StrokeThickness = 0; card.Padding = 14; card.MinimumHeightRequest = 100;
        AppDesign.Tap(card, open, title); return card;
    }
}
