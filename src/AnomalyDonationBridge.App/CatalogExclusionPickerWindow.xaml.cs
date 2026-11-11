using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public partial class CatalogExclusionPickerWindow : Window
{
    private readonly ObservableCollection<CatalogExclusionCandidateViewModel> _rows;

    public CatalogExclusionPickerWindow(IEnumerable<CatalogEntry> candidates)
    {
        _rows = new ObservableCollection<CatalogExclusionCandidateViewModel>(
            candidates.Select(entry => new CatalogExclusionCandidateViewModel(entry)));
        RowsView = CollectionViewSource.GetDefaultView(_rows);
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        DataContext = this;
    }

    public ICollectionView RowsView { get; }
    public IReadOnlyList<CatalogEntry> SelectedEntries { get; private set; } = [];

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var search = (SearchBox.Text ?? string.Empty).Trim();
        RowsView.Filter = item => item is CatalogExclusionCandidateViewModel row &&
            (search.Length == 0 || row.Entry.SectionId.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             row.Entry.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             row.Entry.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
             row.KindLabel.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        RowsView.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        SelectedEntries = _rows.Where(row => row.IsSelected).Select(row => row.Entry).ToList();
        if (SelectedEntries.Count == 0)
        {
            AppDialogs.Info(this, "Anomaly Donation Bridge", "Отметьте хотя бы одну секцию.");
            return;
        }
        DialogResult = true;
    }
}

public sealed class CatalogExclusionCandidateViewModel : ObservableObject
{
    private bool _isSelected;
    public CatalogExclusionCandidateViewModel(CatalogEntry entry) => Entry = entry;
    public CatalogEntry Entry { get; }
    public string KindLabel => Entry.Kind switch
    {
        CatalogEntryKind.Mutant => "Мутант",
        CatalogEntryKind.Npc => "NPC",
        CatalogEntryKind.StalkerSquad => "Отряд NPC",
        CatalogEntryKind.MutantSquad => "Отряд мутантов",
        CatalogEntryKind.Item => "Предмет",
        CatalogEntryKind.Anomaly => "Аномалия",
        CatalogEntryKind.Faction => "Группировка",
        CatalogEntryKind.Level => "Локация",
        CatalogEntryKind.Helicopter => "Вертолёт",
        CatalogEntryKind.ArmedVehicle => "Вооружённый транспорт",
        CatalogEntryKind.NpcVisual => "Внешность NPC",
        CatalogEntryKind.GuitarMusic => "Гитарная композиция",
        _ => Entry.Kind.ToString()
    };
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
