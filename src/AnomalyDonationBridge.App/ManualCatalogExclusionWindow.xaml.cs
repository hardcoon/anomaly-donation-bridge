using System.Windows;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App;

public partial class ManualCatalogExclusionWindow : Window
{
    public ManualCatalogExclusionWindow()
    {
        InitializeComponent();
        DarkSystemFrame.Attach(this);
        KindBox.ItemsSource = new[]
        {
            new KindOption(CatalogEntryKind.Item, "Предмет"),
            new KindOption(CatalogEntryKind.Npc, "NPC"),
            new KindOption(CatalogEntryKind.Mutant, "Мутант"),
            new KindOption(CatalogEntryKind.StalkerSquad, "Отряд NPC"),
            new KindOption(CatalogEntryKind.MutantSquad, "Отряд мутантов"),
            new KindOption(CatalogEntryKind.Anomaly, "Аномалия"),
            new KindOption(CatalogEntryKind.Level, "Локация"),
            new KindOption(CatalogEntryKind.Helicopter, "Вертолёт"),
            new KindOption(CatalogEntryKind.ArmedVehicle, "Вооружённый транспорт"),
            new KindOption(CatalogEntryKind.GuitarMusic, "Гитарная композиция"),
            new KindOption(CatalogEntryKind.Faction, "Группировка")
        };
        KindBox.SelectedIndex = 0;
        Loaded += (_, _) => SectionIdBox.Focus();
    }

    public CatalogEntryKind SelectedKind => KindBox.SelectedValue is CatalogEntryKind kind
        ? kind
        : CatalogEntryKind.Item;
    public string SectionId => SectionIdBox.Text.Trim();
    public string DisplayName => DisplayNameBox.Text.Trim();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (SectionId.Length == 0)
        {
            AppDialogs.Info(this, "Anomaly Donation Bridge", "Введите ID секции.");
            SectionIdBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private sealed record KindOption(CatalogEntryKind Kind, string Label);
}
