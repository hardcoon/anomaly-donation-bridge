namespace AnomalyDonationBridge.App.Services;

public sealed record BridgeInstallationStatus(
    AddonInstallationStatus Addon,
    EffectScriptInstallationStatus Effects)
{
    public bool CanInstall =>
        (Addon.State is AddonInstallState.Current or AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable) &&
        Effects.SourceCount > 0 && (Addon.CanInstall || Effects.CanInstall);

    public string Message => Addon.Message + " " + Effects.Message;
}
