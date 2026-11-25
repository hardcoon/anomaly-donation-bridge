using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed record CatalogRuntimeState(
    CapabilitySet Capabilities,
    CatalogSnapshot? Catalog,
    bool IsLive,
    string Status)
{
    public static CatalogRuntimeState Empty { get; } = new(
        CapabilitySet.Empty,
        null,
        false,
        "Каталог игры ещё не получен");

    public bool HasCatalog => Catalog is not null;
    public bool IsOfflineCache => Catalog is not null && !IsLive;

    internal bool SupportsTemplate(string handlerId) =>
        handlerId == DonationDispatcher.RepeatPastDonationHandlerId ||
        !IsLive || ReferenceEquals(Capabilities, CapabilitySet.Empty) ||
        Capabilities.SupportsHandler(handlerId);
}
