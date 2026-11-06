using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zyrox.Services;

namespace Zyrox.Pages;

public interface IPage
{
    void OnShown();
}

internal static class Ui
{
    public static Border Card(UIElement child, Thickness? margin = null) => new()
    {
        Style = (Style)Application.Current.FindResource("Card"),
        Margin = margin ?? new Thickness(0, 0, 0, 10),
        Child = child
    };

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 15,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static TextBlock Muted(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.FindResource("Muted")
    };

    public static Button Button(string text, RoutedEventHandler? onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(primary ? "PlayButton" : "GhostButton"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (primary)
        {
            button.FontSize = 13;
            button.Width = 110;
            button.Height = 36;
        }
        if (onClick != null)
            button.Click += onClick;
        return button;
    }

    public static WrapPanel ProfileLinks(LauncherState state, GameProfile profile)
    {
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (page, label, glyph) in new[] { ("mods", "Mods", "\uEA86"), ("worlds", "Worlds", "\uE774"), ("servers", "Servers", "\uE968"), ("gallery", "Gallery", "\uEB9F") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = content,
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0)
            };
            button.Click += (_, _) =>
            {
                state.SelectedProfile = profile;
                state.Navigate(page);
            };
            links.Children.Add(button);
        }
        return links;
    }

    public static Border IconFrame(Image image) => new()
    {
        Width = 48,
        Height = 48,
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.FindResource("Field"),
        Margin = new Thickness(0, 0, 14, 0),
        VerticalAlignment = VerticalAlignment.Center,
        ClipToBounds = true,
        Child = image
    };
}
