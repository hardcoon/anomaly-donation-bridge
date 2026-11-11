using System.Globalization;

namespace AnomalyDonationBridge.App.ViewModels;

public sealed class TraderStockRowViewModel : ObservableObject
{
    private readonly ParameterValueViewModel _quantity;
    private static readonly (string Key, string Label, int Maximum)[] Rows =
    [
        ("weapons", "Оружие", 20), ("ammo", "Патроны (пачек на оружие)", 50),
        ("helmets", "Шлемы", 10), ("armor", "Броня", 10), ("artifacts", "Артефакты", 20),
        ("medicine", "Медицина", 30), ("food", "Еда", 30), ("devices", "Электроника и приборы", 20)
    ];
    private TraderStockRowViewModel(string label, int maximum, ParameterValueViewModel enabled, ParameterValueViewModel quantity)
    {
        Label = label; Maximum = maximum; Enabled = enabled; _quantity = quantity;
        _quantity.PropertyChanged += (_, _) => RaisePropertyChanged(nameof(Count));
    }
    public string Label { get; }
    public int Maximum { get; }
    public ParameterValueViewModel Enabled { get; }
    public int Count
    {
        get => int.TryParse(_quantity.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 1;
        set => _quantity.Value = Math.Clamp(value, 1, Maximum).ToString(CultureInfo.InvariantCulture);
    }
    public static bool IsStockKey(string key) => Rows.Any(row => key == row.Key + "_enabled" || key == row.Key + "_count");
    public static IEnumerable<TraderStockRowViewModel> Create(IEnumerable<ParameterValueViewModel> parameters)
    {
        var map = parameters.ToDictionary(parameter => parameter.Definition.Key);
        foreach (var row in Rows)
            if (map.TryGetValue(row.Key + "_enabled", out var enabled) && map.TryGetValue(row.Key + "_count", out var count))
                yield return new(row.Label, row.Maximum, enabled, count);
    }
}
