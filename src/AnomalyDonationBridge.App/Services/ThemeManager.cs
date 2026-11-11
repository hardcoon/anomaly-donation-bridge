using System.Windows;
using System.Windows.Media;

namespace AnomalyDonationBridge.App.Services;

internal static class ThemeManager
{
    private static readonly ThemeColor[] Palette =
    [
        new("BackgroundBrush", "#1C1E1D", "#F2F1EC"),
        new("SurfaceBrush", "#222523", "#FAFAF6"),
        new("PanelBrush", "#2A2D2B", "#EAECE5"),
        new("PanelAltBrush", "#343931", "#E1E5DB"),
        new("InputBrush", "#1C1E1D", "#FFFFFF"),
        new("BorderBrush", "#464B45", "#C8CDC1"),
        new("BorderStrongBrush", "#62695F", "#919A86"),
        new("TextBrush", "#EDECE5", "#252923"),
        new("MutedBrush", "#AFB5AB", "#515B4D"),
        new("SubtleBrush", "#9DA497", "#626E5A"),
        new("AccentBrush", "#D6B25F", "#926411"),
        new("AccentForegroundBrush", "#D6B25F", "#785417"),
        new("AccentTextBrush", "#252218", "#FFFFFF"),
        new("BlueBrush", "#9CAFC5", "#3E6284"),
        new("GoodBrush", "#A4BE88", "#49672B"),
        new("GoodDarkBrush", "#303D2B", "#E2EBD9"),
        new("BadBrush", "#EAA193", "#AB3E30"),
        new("BadDarkBrush", "#42302B", "#F5E1DC"),

        new("SelectionBrush", "#807B734D", "#807B8D60"),
        new("SelectionSurfaceBrush", "#4A5140", "#566B42"),
        new("SelectionTextBrush", "#F8F7EC", "#FFFFFF"),
        new("ComboSideBrush", "#2A2D2B", "#EAECE5"),
        new("ControlHoverBrush", "#3B4039", "#DFE4D6"),
        new("ControlHoverBorderBrush", "#88917A", "#758266"),
        new("ComboItemHoverBrush", "#343931", "#E7EBDF"),
        new("ComboItemSelectedBrush", "#4A5140", "#566B42"),
        new("ScrollTrackBrush", "#1C1E1D", "#ECEEE6"),
        new("ScrollThumbBrush", "#565E50", "#ADB7A3"),
        new("ScrollThumbHoverBrush", "#7A8470", "#839274"),
        new("SuccessTextBrush", "#D4E6C4", "#3D5429"),
        new("SuccessBorderBrush", "#61734F", "#8C9E7D"),
        new("DangerTextBrush", "#EDB5A9", "#862F23"),
        new("DangerBorderBrush", "#85554B", "#C89486"),
        new("SwitchTrackBrush", "#444A41", "#C7CDBF"),
        new("SwitchBorderBrush", "#636D59", "#9AA78A"),
        new("SwitchThumbBrush", "#C5CBBE", "#FFFFFF"),
        new("SwitchOnBrush", "#A38748", "#AD7E24"),
        new("SwitchOnThumbBrush", "#F9F1D8", "#FFFFFF"),
        new("TabHoverBrush", "#343931", "#E1E5DB"),
        new("DataGridHoverBrush", "#343931", "#E1E5DB"),
        new("DataGridSelectedBrush", "#4A5140", "#566B42"),

        new("StatusCardBrush", "#222523", "#FAFAF6"),
        new("ListWellBrush", "#1C1E1D", "#F2F1EC"),
        new("ActionRowEvenBrush", "#2A2D2B", "#FAFAF6"),
        new("ActionRowOddBrush", "#2A2D2B", "#FAFAF6"),
        new("ActionRowBorderBrush", "#464B45", "#C8CDC1"),
        new("ActionRowMarkerBrush", "#706344", "#BAA36E"),
        new("ActionExpanderBrush", "#222523", "#EAECE5"),
        new("ActionExpanderHoverBrush", "#343931", "#E1E5DB"),
        new("ActionExpanderOpenBrush", "#222523", "#EAECE5"),
        new("TriggerChipBrush", "#383328", "#F0E8D6"),
        new("CountBadgeBrush", "#343931", "#E1E5DB"),

        new("InfoPanelBrush", "#2B312D", "#E7ECE2"),
        new("InfoTextBrush", "#D4D7CE", "#45503D"),
        new("WarningPanelBrush", "#383328", "#F0E8D6"),
        new("WarningBorderBrush", "#655A42", "#C0A773"),
        new("WarningBadgeBrush", "#51452C", "#E9D9B3"),
        new("WarningTextBrush", "#E0CAA0", "#735620")
    ];

    // Yellow is the existing palette, byte-for-byte. New surfaces receive
    // only a 25% tint, matching the approved muted preview.
    private static readonly IReadOnlyDictionary<string, ThemeTint[]> Tints =
        new Dictionary<string, ThemeTint[]>(StringComparer.Ordinal)
        {
            ["red"] = [
                new("#231A1B", "#2A1E20", "#332528", "#423034", "#604349", "#D0A7A3", "#432C31", "#9B6364", "#BDB2B1"),
                new("#F3EEEE", "#FFFAFA", "#EEE5E5", "#E6DADA", "#C8B9B8", "#865B58", "#EFE0DF", "#AE8B85", "#716461")],
            ["green"] = [
                new("#19231C", "#1E2B22", "#29392D", "#354B3A", "#48644E", "#ADC0A4", "#304334", "#72966B", "#B0BCAE"),
                new("#EEF2ED", "#FBFDF9", "#E6ECE3", "#DCE5D8", "#BDCABA", "#526B4F", "#E0EADB", "#8DA285", "#657061")],
            ["blue"] = [
                new("#191F2B", "#202838", "#2A3548", "#35445C", "#495F7C", "#A8B9D0", "#2C3D58", "#6888AF", "#AFB9C9"),
                new("#EEF0F4", "#FAFBFF", "#E5E9F0", "#DCE2EC", "#BAC4D0", "#4D698A", "#DEE5F0", "#8A9DB6", "#626D7B")]
        };

    public static bool IsLightTheme { get; private set; }
    public static string ColorKey { get; private set; } = "yellow";
    public static event Action? ThemeChanged;

    public static string NormalizeColor(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "red" => "red", "green" => "green", "blue" => "blue", _ => "yellow"
    };

    public static void Apply(bool useLightTheme, string? themeColor = null)
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("WPF Application is not initialized.");
        if (!application.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The application theme must be changed on the UI thread.");
        }

        var selectedColor = NormalizeColor(themeColor);
        // Resolve and validate the complete palette before replacing any
        // resource. An invalid resource cannot leave a half-applied theme.
        var resolved = Palette.Select(entry => (Entry: entry, Color: ResolveColor(entry, useLightTheme, selectedColor))).ToArray();
        foreach (var (entry, _) in resolved)
        {
            var colorKey = entry.Key.EndsWith("Brush", StringComparison.Ordinal)
                ? entry.Key[..^"Brush".Length] + "Color"
                : throw new InvalidOperationException($"Theme resource {entry.Key} is not a brush key.");
            if (!application.Resources.Contains(colorKey))
            {
                throw new InvalidOperationException($"Theme color {colorKey} is missing.");
            }
        }
        foreach (var (entry, color) in resolved)
        {
            var colorKey = entry.Key[..^"Brush".Length] + "Color";
            application.Resources[colorKey] = color;
            application.Resources[entry.Key] = new SolidColorBrush(color);
        }

        IsLightTheme = useLightTheme;
        ColorKey = selectedColor;
        ThemeChanged?.Invoke();
    }

    private static Color ResolveColor(ThemeColor entry, bool light, string colorKey)
    {
        var original = Parse(light ? entry.Light : entry.Dark);
        if (!Tints.TryGetValue(colorKey, out var variants)) return original;
        var tint = variants[light ? 1 : 0];
        var accent = Parse(tint.Accent);
        if (entry.Key is "AccentBrush" or "AccentForegroundBrush") return accent;
        if (entry.Key is "SelectionSurfaceBrush" or "ComboItemSelectedBrush" or "DataGridSelectedBrush")
            return Blend(Parse(light ? "#56615D" : "#3A3E3D"), accent, 0.18);
        if (entry.Key == "SelectionBrush")
            return Color.FromArgb(128, accent.R, accent.G, accent.B);
        if (entry.Key == "SwitchOnBrush")
            return light ? accent : Blend(Parse("#4B4C49"), accent, 0.45);
        if (entry.Key == "SwitchOnThumbBrush") return Parse("#F7F7F2");
        var target = entry.Key switch
        {
            "BackgroundBrush" or "InputBrush" or "ScrollTrackBrush" or "ListWellBrush" => tint.Background,
            "SurfaceBrush" or "StatusCardBrush" or "ActionExpanderBrush" or "ActionExpanderOpenBrush" => tint.Surface,
            "PanelBrush" or "ComboSideBrush" or "ActionRowEvenBrush" or "ActionRowOddBrush" => tint.Panel,
            "PanelAltBrush" or "CountBadgeBrush" or "ControlHoverBrush" or "ComboItemHoverBrush" or
                "TabHoverBrush" or "DataGridHoverBrush" or "ActionExpanderHoverBrush" => tint.Alternate,
            "BorderBrush" or "BorderStrongBrush" or "ActionRowBorderBrush" or "ControlHoverBorderBrush" => tint.Border,
            "MutedBrush" or "SubtleBrush" => tint.Muted,
            "TriggerChipBrush" => tint.Chip,
            "ActionRowMarkerBrush" => tint.Marker,
            _ => null // Preserve all semantic success/error/warning/info colors.
        };
        return target is null ? original : Blend(original, Parse(target), 0.25);
    }

    private static Color Blend(Color from, Color to, double amount) =>
        Color.FromArgb(from.A,
            (byte)Math.Round(from.R + (to.R - from.R) * amount, MidpointRounding.AwayFromZero),
            (byte)Math.Round(from.G + (to.G - from.G) * amount, MidpointRounding.AwayFromZero),
            (byte)Math.Round(from.B + (to.B - from.B) * amount, MidpointRounding.AwayFromZero));

    private static Color Parse(string value) =>
        ColorConverter.ConvertFromString(value) is Color color
            ? color
            : throw new InvalidOperationException($"Invalid theme color {value}.");

    private sealed record ThemeColor(string Key, string Dark, string Light);
    private sealed record ThemeTint(string Background, string Surface, string Panel, string Alternate,
        string Border, string Accent, string Chip, string Marker, string Muted);
}
