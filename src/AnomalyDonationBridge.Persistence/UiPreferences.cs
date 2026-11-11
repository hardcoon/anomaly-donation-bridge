using System.Text.Json;

namespace AnomalyDonationBridge.Persistence;

// Presentation preferences are deliberately separate from editable donation rules.
public sealed record UiPreferences
{
    public string ThemeColor { get; init; } = "yellow";
    // null preserves the existing settings.json light/dark choice until the
    // user explicitly chooses a background in the new presentation picker.
    public bool? UseLightTheme { get; init; }
    // null keeps every supported currency enabled for existing installations.
    // An explicit empty list disables automatic calculation, not manual prices.
    public List<string>? AutomaticPriceCurrencies { get; init; }
    public bool ShowTemplateDescriptions { get; init; } = true;
    public bool VotingSettingsPanelCollapsed { get; init; }
    public string ActiveCategory { get; init; } = "all";
    public string ActivePlayerSubcategory { get; init; } = "all";
    public string PresetCategory { get; init; } = "all";
    public string PresetPlayerSubcategory { get; init; } = "all";
    public int ActiveColumns { get; init; } = 2;
    public bool AutomaticActiveColumns { get; init; }
    public bool CompactActiveActions { get; init; }
    public Dictionary<string, Dictionary<string, string>> ActivePriceGroupNamesByProfile { get; init; } =
        new(StringComparer.Ordinal);
    public int PresetColumns { get; init; } = 2;
    public bool AutomaticPresetColumns { get; init; }
    public bool CompactPresets { get; init; }
    public string UserPresetCategory { get; init; } = "all";
    public string UserPresetPlayerSubcategory { get; init; } = "all";
    public int UserPresetColumns { get; init; } = 2;
    public bool AutomaticUserPresetColumns { get; init; }
    public bool CompactUserPresets { get; init; }
}

public sealed class JsonUiPreferencesStore(AppPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(paths.RootDirectory, "ui-preferences.json");

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<UiPreferences>(stream, JsonDefaults.Options, cancellationToken).ConfigureAwait(false) ?? new();
        }
        catch (JsonException) { return new(); } // A broken view preference must not block donation rules.
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            paths.EnsureExists();
            var temporary = FilePath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { _gate.Release(); }
    }
}
