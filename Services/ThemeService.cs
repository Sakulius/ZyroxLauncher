using System.Windows;
using System.Windows.Media;

namespace Zyrox.Services;

public record ThemeChoice(string Id, string Label, Color Accent, Color Hover)
{
    public override string ToString() => Label;
}

public static class ThemeService
{
    public const string AutoId = "auto";

    public static readonly ThemeChoice Green = Create("green", "Green", 0x57, 0xB8, 0x47);
    public static readonly ThemeChoice Blue = Create("blue", "Blue", 0x4F, 0xA3, 0xF7);
    public static readonly ThemeChoice Purple = Create("purple", "Purple", 0xA9, 0x8B, 0xFA);
    public static readonly ThemeChoice Orange = Create("orange", "Orange", 0xF5, 0xA3, 0x42);
    public static readonly ThemeChoice Red = Create("red", "Red", 0xF2, 0x71, 0x6B);
    public static readonly ThemeChoice Pink = Create("pink", "Pink", 0xF2, 0x7F, 0xC0);
    public static readonly ThemeChoice Cyan = Create("cyan", "Teal", 0x3F, 0xCB, 0xD6);

    public static readonly IReadOnlyList<ThemeChoice> Choices =
    [
        new ThemeChoice(AutoId, "Automatic by time of day", Green.Accent, Green.Hover),
        Green, Blue, Purple, Orange, Red, Pink, Cyan
    ];

    public static ThemeChoice ForHour(int hour) => hour switch
    {
        >= 6 and < 11 => Orange,
        >= 11 and < 17 => Green,
        >= 17 and < 21 => Pink,
        _ => Blue
    };

    public static void Apply(string id)
    {
        var theme = id == AutoId
            ? ForHour(DateTime.Now.Hour)
            : Choices.FirstOrDefault(c => c.Id == id && c.Id != AutoId) ?? Green;
        SetColor("Accent", theme.Accent);
        SetColor("AccentHover", theme.Hover);
    }

    private static void SetColor(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static ThemeChoice Create(string id, string label, byte r, byte g, byte b)
    {
        const int lift = 22;
        return new ThemeChoice(id, label, Color.FromRgb(r, g, b),
            Color.FromRgb((byte)Math.Min(255, r + lift), (byte)Math.Min(255, g + lift), (byte)Math.Min(255, b + lift)));
    }
}
