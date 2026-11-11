using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var inverted = value is not true;
        return targetType == typeof(Visibility) ? (inverted ? Visibility.Visible : Visibility.Collapsed) : inverted;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool boolean && !boolean;
}

public sealed class ResponsiveItemWidthConverter : IValueConverter
{
    public double MinimumColumnWidth { get; set; } = 750;

    public double CalculateItemWidth(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return double.NaN;

        var columns = availableWidth >= MinimumColumnWidth * 2 ? 2 : 1;
        return Math.Floor(availableWidth / columns);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width ? CalculateItemWidth(width) : double.NaN;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ParameterColumnWidthConverter : IMultiValueConverter
{
    public double CalculateItemWidth(double availableWidth, int columns)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0) return double.NaN;
        columns = Math.Clamp(columns, 1, 3);
        return Math.Floor(availableWidth / columns);
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double number ? number : double.NaN;
        var columns = values.Length > 1 && values[1] is int count ? count : 1;
        return CalculateItemWidth(width, columns);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class MinimumThumbViewportConverter : IMultiValueConverter
{
    public double MinimumThumbLength { get; set; } = 64;

    public double CalculateViewportSize(double viewportSize, double minimum, double maximum, double scrollBarLength)
    {
        if (!double.IsFinite(viewportSize) || !double.IsFinite(minimum) ||
            !double.IsFinite(maximum) || !double.IsFinite(scrollBarLength)) return viewportSize;

        var range = Math.Max(0, maximum - minimum);
        var trackLength = Math.Max(0, scrollBarLength - 2);
        var minimumThumbLength = Math.Min(MinimumThumbLength, trackLength);
        if (range <= 0 || trackLength <= minimumThumbLength) return viewportSize;

        var minimumViewport = minimumThumbLength * range / (trackLength - minimumThumbLength);
        return Math.Max(viewportSize, minimumViewport);
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not double viewportSize ||
            values[1] is not double minimum || values[2] is not double maximum ||
            values[3] is not double scrollBarLength) return Binding.DoNothing;

        return CalculateViewportSize(viewportSize, minimum, maximum, scrollBarLength);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FriendlyValueConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["weak"] = "слабый",
        ["medium"] = "средний",
        ["strong"] = "сильный",
        ["dog"] = "собака",
        ["boar"] = "кабан",
        ["flesh"] = "плоть",
        ["tushkano"] = "тушкан",
        ["pseudodog"] = "псевдособака",
        ["psy_dog"] = "пси-собака",
        ["bloodsucker"] = "кровосос",
        ["snork"] = "снорк",
        ["burer"] = "бюрер",
        ["controller"] = "контролёр",
        ["poltergeist"] = "полтергейст",
        ["chimera"] = "химера",
        ["pseudogiant"] = "псевдогигант",
        ["gravitational"] = "гравитационная",
        ["electric"] = "электрическая",
        ["thermal"] = "термическая",
        ["acidic"] = "кислотная",
        ["stalker"] = "сталкеры",
        ["bandit"] = "бандиты",
        ["duty"] = "Долг",
        ["freedom"] = "Свобода",
        ["military"] = "военные",
        ["monolith"] = "Монолит",
        ["killer"] = "наёмники",
        ["outfit"] = "броню",
        ["helmet"] = "шлем",
        ["both"] = "броню и шлем",
        ["bandage"] = "бинт",
        ["medkit"] = "аптечка",
        ["antirad"] = "антирад",
        ["bread"] = "хлеб",
        ["conserva"] = "консервы",
        ["vodka"] = "водка",
        ["ammo_9x18_fmj"] = "патроны 9×18 FMJ",
        ["ammo_5.45x39_fmj"] = "патроны 5,45×39 FMJ",
        ["ammo_12x70_buck"] = "картечь 12×70",
        ["weapons"] = "Оружие",
        ["ammo_explosives"] = "Патроны",
        ["armor"] = "Броня и шлемы",
        ["weapon_addons"] = "Оружейные дополнения",
        ["electronics"] = "Электроника и приборы",
        ["artefacts"] = "Артефакты",
        ["medicine"] = "Медицина",
        ["food"] = "Еда и напитки",
        ["repair_tools"] = "Ремонт и инструменты"
    };

    public static string Display(object? value) => value switch
    {
        TriggerComparator.Exact => "= точная сумма",
        TriggerComparator.GreaterOrEqual => "≥ сумма или больше",
        QueueMode.Skip => "Сразу пропустить",
        QueueMode.WaitForDuration => "Ждать N минут",
        QueueMode.WaitIndefinitely => "Ждать без ограничения",
        DispatchState.Queued => "В очереди",
        DispatchState.Sent => "Отправлено",
        DispatchState.Executed => "Выполнено",
        DispatchState.Deferred => "Отложено",
        DispatchState.Rejected => "Отклонено",
        DispatchState.Uncertain => "Не подтверждено",
        DispatchState.Skipped => "Пропущено",
        DispatchState.Cancelled => "Удалено из очереди",
        DispatchState.Expired => "Истекло",
        DispatchState.Duplicate => "Повтор",
        DispatchState.NoRule => "Нет правила",
        string text when Labels.TryGetValue(text, out var label) => label,
        string text when text.Contains('_') => text.Replace('_', ' '),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Display(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HistoryStatusMarkerConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DispatchState.Rejected or DispatchState.Uncertain or DispatchState.Expired => "!",
        DispatchState.Executed => "✓",
        DispatchState.Queued or DispatchState.Deferred => "…",
        DispatchState.Sent => "↗",
        _ => "–"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
