using System.Windows;
using AnomalyDonationBridge.App.Services;

namespace AnomalyDonationBridge.App;

public partial class DonorLaunchWindow : Window
{
    public DonorLaunchWindow(string templateName)
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        TemplateName.Text = templateName;
        DonorNameBox.TextChanged += (_, _) => LaunchButton.IsEnabled = !string.IsNullOrWhiteSpace(DonorNameBox.Text);
        LaunchButton.IsEnabled = false;
        Loaded += (_, _) => DonorNameBox.Focus();
    }

    public DonorLaunchInput? Result { get; private set; }

    internal bool TryReadInput(out DonorLaunchInput? input)
    {
        try
        {
            input = DonorLaunchInput.Create(DonorNameBox.Text, MessageBox.Text);
            ErrorText.Text = string.Empty;
            return true;
        }
        catch (ArgumentException error)
        {
            input = null;
            ErrorText.Text = error.Message;
            return false;
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadInput(out var input)) return;
        Result = input;
        DialogResult = true;
    }
}
