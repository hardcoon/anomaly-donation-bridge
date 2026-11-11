using System.Windows;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class CurrencyCalculationSettingsWindow : Window
{
    public CurrencyCalculationSettingsWindow(IEnumerable<string> selectedCurrencies)
    {
        var selected = DonationCurrencyPriceCalculator.NormalizeCurrencies(selectedCurrencies);
        Options = DonationCurrencyPriceCalculator.SupportedCurrencies
            .Select(code => new CurrencyCalculationOption(code, selected.Contains(code, StringComparer.Ordinal)))
            .ToList();
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        DataContext = this;
    }

    public IReadOnlyList<CurrencyCalculationOption> Options { get; }
    public IReadOnlyList<string> SelectedCurrencies { get; private set; } = [];

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedCurrencies = Options.Where(option => option.IsSelected).Select(option => option.Code).ToArray();
        DialogResult = true;
    }
}

public sealed class CurrencyCalculationOption(string code, bool selected) : ObservableObject
{
    private bool _isSelected = selected;
    public string Code { get; } = code;
    public string Label => Code switch
    {
        "RUB" => "RUB · Рубль", "USD" => "USD · Доллар",
        "EUR" => "EUR · Евро", "BYN" => "BYN · Бел. рубль",
        "KZT" => "KZT · Тенге", "UAH" => "UAH · Гривна",
        "BRL" => "BRL · Реал", "TRY" => "TRY · Лира", _ => Code
    };
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
