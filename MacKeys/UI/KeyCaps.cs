using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacKeys.UI;

/// <summary>
/// Renders "⌘ ⇧ 4 · ⌘ ⇧ 5" as little keyboard keys. Tokens are split on spaces;
/// "·", "/", "+" and lowercase words ("clique") are drawn as plain text between keys.
/// </summary>
internal sealed class KeyCaps : StackPanel
{
    public static readonly DependencyProperty KeysProperty = DependencyProperty.Register(
        nameof(Keys), typeof(string), typeof(KeyCaps),
        new PropertyMetadata("", (d, _) => ((KeyCaps)d).Rebuild()));

    public string Keys
    {
        get => (string)GetValue(KeysProperty);
        set => SetValue(KeysProperty, value);
    }

    /// <summary>Larger caps for the cheat sheet and onboarding.</summary>
    public bool Large { get; set; }

    public KeyCaps()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
    }

    private void Rebuild()
    {
        Children.Clear();
        double size = Large ? 14 : 12;
        foreach (var token in Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool isText = token is "·" or "/" or "+" || (token.Length > 1 && char.IsLower(token[0]));
            if (isText)
            {
                var text = new TextBlock
                {
                    Text = token,
                    FontSize = size,
                    Margin = new Thickness(3, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
                Children.Add(text);
                continue;
            }

            var cap = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1, 1, 1, 2),
                MinWidth = Large ? 28 : 22,
                Padding = new Thickness(Large ? 7 : 5, Large ? 2 : 1, Large ? 7 : 5, Large ? 2 : 1),
                Margin = new Thickness(0, 0, 3, 0),
                Child = new TextBlock
                {
                    Text = token,
                    FontSize = size,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                },
            };
            cap.SetResourceReference(Border.BackgroundProperty, "Bg.KeyCap");
            cap.SetResourceReference(Border.BorderBrushProperty, "Border.KeyCap");
            Children.Add(cap);
        }
    }
}
