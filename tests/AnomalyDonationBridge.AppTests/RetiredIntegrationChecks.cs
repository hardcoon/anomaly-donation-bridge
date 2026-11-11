using System.IO;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class RetiredIntegrationChecks
{
    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppPaths(Path.Combine(temporary.Path, "data")));
        Check(!typeof(BridgeRuntime).Assembly.GetReferencedAssemblies().Any(a => a.Name!.Contains("DonatePay", StringComparison.OrdinalIgnoreCase)),
            "The application still references a retired provider assembly.");
        Check(!runtime.DonationAccountConfigured && !runtime.CanArmDonationProcessing,
            "A removed provider can still arm donation processing.");
        runtime.Settings.LastDonationId = "keep-da-checkpoint";
        runtime.Settings.LastDonateXDonationId = "keep-dx-checkpoint";
        var entries = 0; runtime.HistoryAdded += _ => entries++;
        var historical = new DonationEvent { Id = "donatepay:old-account:123", Amount = 10, Currency = "RUB", CreatedAt = DateTimeOffset.UtcNow };
        await runtime.ProcessDonationAsync(historical);
        await runtime.ProcessDonationAsync(historical);
        Check(entries == 1 && runtime.Settings.LastDonationId == "keep-da-checkpoint" &&
            runtime.Settings.LastDonateXDonationId == "keep-dx-checkpoint",
            "Old history was duplicated or polluted a current provider's checkpoint.");
        runtime.Secrets.DonateXExternalToken = "isolated-fake-token";
        runtime.Settings.DonateXUserId = "current"; runtime.Settings.DonateXAccountName = "channel";
        Check(runtime.DonationAccountConfigured && runtime.CanArmDonationProcessing, "DonateX-only processing no longer works.");
        runtime.Settings.Armed = true;
        await runtime.DisconnectDonationAlertsAsync(false);
        Check(runtime.Settings.Armed && runtime.Secrets.HasDonateXToken, "DA disconnect affected the current DonateX integration.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
