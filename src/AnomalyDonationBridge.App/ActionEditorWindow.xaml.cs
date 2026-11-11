using System.ComponentModel;
using System.Windows;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;

namespace AnomalyDonationBridge.App;

public partial class ActionEditorWindow : Window
{
    private readonly ActionEditorViewModel _viewModel;
    public ActionEditorWindow(ActionEditorViewModel viewModel, DataTemplate editorTemplate)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        EditorContent.ContentTemplate = editorTemplate;
        DarkSystemFrame.Attach(this);
        // Fit the work area (including taskbar) on smaller / scaled displays.
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height);
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync()) DialogResult = true;
    }
    private async void ResetActorBoost_Click(object sender, RoutedEventArgs e) => await _viewModel.ResetActorBoostAsync();
    private void SourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkContentElement { DataContext: TemplateSourceLink link }) _viewModel.OpenSource(link);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}
