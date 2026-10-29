using Microsoft.Maui.Controls.Shapes;
namespace Tunapp;

internal static class AppDesign
{
    public static Color Ink => Color.FromArgb("#233C30");
    public static Color Muted => Color.FromArgb("#758176");
    public static Color Background => Color.FromArgb("#F7F8F2");
    public static void Tap(View view, Action open, string description)
    {
        SemanticProperties.SetDescription(view, description);
        var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => open(); view.GestureRecognizers.Add(tap);
    }
    public static View Empty(string title, string description, string icon)
    {
        var stack = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(8, 12) };
        stack.Children.Add(new Image { Source = icon, HeightRequest = 30, WidthRequest = 30, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(new Label { Text = title, FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Ink, HorizontalTextAlignment = TextAlignment.Center });
        stack.Children.Add(new Label { Text = description, FontSize = 13, TextColor = Muted, HorizontalTextAlignment = TextAlignment.Center, LineHeight = 1.3 });
        return Card(stack);
    }
    public static Border Card(View content) => new()
    {
        Content = content, BackgroundColor = Colors.White, Stroke = Color.FromArgb("#E2EAE5"),
        StrokeShape = new RoundRectangle { CornerRadius = 16 }, Padding = 16
    };
    public static View Input(string label, Entry entry)
    {
        entry.BackgroundColor = Colors.Transparent;
        entry.PlaceholderColor = Color.FromArgb("#89988F");
        entry.FontSize = 16;
        var stack = new VerticalStackLayout { Spacing = 2 };
        stack.Children.Add(new Label { Text = label, FontSize = 12, TextColor = Color.FromArgb("#52665C") });
        stack.Children.Add(entry);
        var card = Card(stack); card.Padding = new Thickness(14, 8); return card;
    }
}
