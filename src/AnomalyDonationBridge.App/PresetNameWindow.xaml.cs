using System.Windows;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public partial class PresetNameWindow : Window
{
    public PresetNameWindow(string title, string initialValue, bool showCopyUserPresets = false, bool showCopyVotings = false)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        Title = title;
        PromptText.Text = title;
        NameBox.Text = initialValue;
        CopyUserPresetsBox.Visibility = showCopyUserPresets ? Visibility.Visible : Visibility.Collapsed;
        CopyUserPresetsBox.IsChecked = true;
        CopyVotingsBox.Visibility = CopyVotingsHint.Visibility = showCopyVotings ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string PresetName { get; private set; } = string.Empty;
    public bool CopyUserPresets => CopyUserPresetsBox.IsChecked == true;
    public bool CopyVotings => CopyVotingsBox.IsChecked == true;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validation.TryNormalizeUserPresetName(NameBox.Text, out var normalized))
        {
            ErrorText.Text = $"Введите название длиной от 1 до {Validation.UserPresetNameMaxLength} символов.";
            return;
        }
        PresetName = normalized;
        DialogResult = true;
    }
}
