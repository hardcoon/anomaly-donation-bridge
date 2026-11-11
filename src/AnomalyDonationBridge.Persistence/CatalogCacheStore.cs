using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Persistence;

public sealed class CatalogCacheDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string GameRoot { get; set; } = string.Empty;
    public DateTimeOffset CachedAt { get; set; }
    public EngineProfile EngineProfile { get; set; }
    public string EngineVersion { get; set; } = string.Empty;
    public List<string> CapabilityIds { get; set; } = [];
    public List<string> SupportedHandlerIds { get; set; } = [];
    public string Revision { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public List<CatalogEntry> Entries { get; set; } = [];
    public string MonsterCommunities { get; set; } = string.Empty;

    public CatalogSnapshot ToSnapshot()
    {
        if (string.IsNullOrWhiteSpace(Revision) || string.IsNullOrWhiteSpace(ContentHash) || Entries is null)
        {
            throw new InvalidDataException("Офлайн-кэш каталога не содержит обязательные поля.");
        }
        if (Entries.Any(entry => entry is null))
        {
            throw new InvalidDataException("Офлайн-кэш каталога содержит пустую запись.");
        }
        return new CatalogSnapshot(
            new CatalogRevision(Revision),
            ContentHash,
            Entries,
            ReceivedAt, MonsterCommunities);
    }

    public CapabilitySet ToCapabilities()
    {
        if (!Enum.IsDefined(EngineProfile) || CapabilityIds is null || SupportedHandlerIds is null)
        {
            throw new InvalidDataException("Офлайн-кэш содержит недопустимый профиль возможностей.");
        }
        return CapabilitySet.Create(
            EngineProfile,
            EngineVersion,
            CapabilityIds,
            SupportedHandlerIds);
    }

    public static CatalogCacheDocument Create(
        string gameRoot,
        CapabilitySet capabilities,
        CatalogSnapshot snapshot) => new()
    {
        GameRoot = NormalizeGameRoot(gameRoot),
        CachedAt = DateTimeOffset.UtcNow,
        EngineProfile = capabilities.Profile,
        EngineVersion = capabilities.EngineVersion,
        CapabilityIds = capabilities.CapabilityIds.OrderBy(value => value, StringComparer.Ordinal).ToList(),
        SupportedHandlerIds = capabilities.SupportedHandlerIds.OrderBy(value => value, StringComparer.Ordinal).ToList(),
        Revision = snapshot.Revision.Value,
        ContentHash = snapshot.ContentHash,
        ReceivedAt = snapshot.ReceivedAt,
        Entries = snapshot.Entries.ToList(),
        MonsterCommunities = snapshot.MonsterCommunities
    };

    internal static string NormalizeGameRoot(string? gameRoot) =>
        string.IsNullOrWhiteSpace(gameRoot)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
}

/// <summary>
/// Persists the last complete catalog for offline browsing. Loading alone never
/// makes it live; the current game must confirm count and SHA-256 first.
/// </summary>
public sealed class JsonCatalogCacheStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonCatalogCacheStore(AppPaths paths) => _paths = paths;

    public async Task<CatalogCacheDocument?> LoadAsync(
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.CatalogCacheFile)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                _paths.CatalogCacheFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<CatalogCacheDocument>(
                stream,
                JsonDefaults.Options,
                cancellationToken).ConfigureAwait(false);
            if (document is not { SchemaVersion: 1 }) return null;
            if (!string.Equals(
                    CatalogCacheDocument.NormalizeGameRoot(document.GameRoot),
                    CatalogCacheDocument.NormalizeGameRoot(gameRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Validate all untrusted cache data before exposing it to the UI.
            _ = document.ToCapabilities();
            _ = document.ToSnapshot();
            return document;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or
            NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string gameRoot,
        CapabilitySet capabilities,
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(snapshot);
        var document = CatalogCacheDocument.Create(gameRoot, capabilities, snapshot);
        _paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = _paths.CatalogCacheFile + ".tmp";
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _paths.CatalogCacheFile, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
