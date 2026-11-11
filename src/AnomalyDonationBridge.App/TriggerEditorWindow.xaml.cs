using System.Windows;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public partial class TriggerEditorWindow : Window
{
    private readonly TriggerEditorViewModel _viewModel;
    private readonly CancellationTokenSource _closing = new();
    public TriggerEditorWindow(TriggerEditorViewModel viewModel)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        _viewModel = viewModel;
        DataContext = viewModel;
    }
    public TriggerEditorResult? Result { get; private set; }

    private async void CalculateAll_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.CalculateAllAsync(_closing.Token);
    }

    private async void CurrencySettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CurrencyCalculationSettingsWindow(_viewModel.CalculationCurrencies) { Owner = this };
        if (dialog.ShowDialog() == true)
            await _viewModel.SetCalculationCurrenciesAsync(dialog.SelectedCurrencies);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.Build();
        if (Result is not null) DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        _closing.Dispose();
        base.OnClosed(e);
    }
}
