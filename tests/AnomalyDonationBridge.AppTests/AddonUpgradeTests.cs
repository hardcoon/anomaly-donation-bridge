using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Integration check against the real previous package and separately shipped
// hotfix bytes. All installs and intentional corruptions stay in temporary games.
internal static class AddonUpgradeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Visual = "gamedata/scripts/anomaly_donation_bridge_npc_visuals.script";
    private const string Btr = "gamedata/scripts/anomaly_donation_bridge_btr_ai.script";

    public static async Task RunAsync(string previousPackage, string hotfixes)
    {
        var oldManifest = await ReadManifest(previousPackage);
        var newPackage = Path.Combine(AppContext.BaseDirectory, "addon-package");
        var newManifest = await ReadManifest(newPackage);
        var scenarios = new[]
        {
            Array.Empty<string>(),
            new[] { "hotfix-dux-wildcard" },
            new[] { "hotfix-dux-wildcard", "hotfix-btr-native-api-diagnostic" },
            new[] { "hotfix-dux-wildcard", "hotfix-btr-gamma-input-fallback" }
        };
        foreach (var scenario in scenarios)
        {
            using var temporary = new Program.TemporaryDirectory();
            var game = Path.Combine(temporary.Path, "game");
            Directory.CreateDirectory(Path.Combine(game, "bin"));
            await File.WriteAllTextAsync(Path.Combine(game, "fsgame.ltx"), "$game_data$=true|true|$fs_root$|gamedata\\");
            var paths = new AppPaths(Path.Combine(temporary.Path, "data"));
            var oldManager = new AddonPackageManager(paths, previousPackage, oldManifest.AddonVersion);
            Check((await oldManager.InstallOrUpdateAsync(game)).State == AddonInstallState.Current, "Baseline install failed.");

            var replaced = new Dictionary<string, byte[]>();
            foreach (var hotfix in scenario)
            {
                var relative = hotfix == "hotfix-dux-wildcard" ? Visual : Btr;
                var bytes = await File.ReadAllBytesAsync(Path.Combine(hotfixes, hotfix, Path.GetFileName(relative)));
                replaced[relative] = bytes;
                await File.WriteAllBytesAsync(Path.Combine(game, relative), bytes);
            }

            var manager = new AddonPackageManager(paths, newPackage);
            var status = await manager.InspectAsync(game);
            Check(status.State == AddonInstallState.UpdateAvailable && status.CanInstall,
                "Update button is blocked: " + status.Message);

            // An extra edit of a recognized hotfix must still stop installation.
            var visualPath = Path.Combine(game, Visual);
            var originalVisual = await File.ReadAllBytesAsync(visualPath);
            await File.AppendAllTextAsync(visualPath, "\n-- unknown manual edit\n");
            Check((await manager.InspectAsync(game)).State == AddonInstallState.Conflict, "Unknown edit was trusted.");
            var blocked = false;
            try { await manager.InstallOrUpdateAsync(game); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked && (await File.ReadAllTextAsync(visualPath)).Contains("unknown manual edit"),
                "A blocked update changed the user's file.");
            await File.WriteAllBytesAsync(visualPath, originalVisual);

            // Recognized bytes at the wrong managed path are not a known hotfix.
            var btrPath = Path.Combine(game, Btr);
            var originalBtr = await File.ReadAllBytesAsync(btrPath);
            await File.WriteAllBytesAsync(btrPath, originalVisual);
            Check((await manager.InspectAsync(game)).State == AddonInstallState.Conflict, "Hotfix trust was not bound to its path.");
            await File.WriteAllBytesAsync(btrPath, originalBtr);

            // A receipt for another installation cannot authorize replacement.
            var receiptBytes = await File.ReadAllBytesAsync(paths.AddonInstallationReceiptFile);
            var receipt = JsonSerializer.Deserialize<AddonInstallationReceipt>(receiptBytes, Json)!;
            receipt.GameRoot = Path.Combine(temporary.Path, "other-game");
            await File.WriteAllTextAsync(paths.AddonInstallationReceiptFile, JsonSerializer.Serialize(receipt, Json));
            Check((await manager.InspectAsync(game)).State == AddonInstallState.Conflict, "Foreign receipt was accepted.");
            await File.WriteAllBytesAsync(paths.AddonInstallationReceiptFile, receiptBytes);

            Check((await manager.InstallOrUpdateAsync(game)).State == AddonInstallState.Current, "Upgrade did not complete.");
            foreach (var file in newManifest.Files)
            {
                var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(game, file.Path))));
                Check(actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase), "Installed bytes differ: " + file.Path);
            }
            foreach (var old in replaced)
            {
                var backups = Directory.EnumerateFiles(paths.AddonBackupsDirectory, Path.GetFileName(old.Key), SearchOption.AllDirectories);
                Check(backups.Any(path => File.ReadAllBytes(path).SequenceEqual(old.Value)), "Exact hotfix backup missing: " + old.Key);
            }
            Check((await manager.InspectAsync(game)).State == AddonInstallState.Current, "Upgrade remains incorrectly outdated.");
            Check((await manager.InstallOrUpdateAsync(game)).State == AddonInstallState.Current, "Repeated update is not idempotent.");
            Console.WriteLine("PASS real package upgrade: " + (scenario.Length == 0 ? "release 1.3.19" : string.Join(" + ", scenario)));
        }
        Console.WriteLine("PASS upgrade backups, exact path/hash checks, unknown edits, foreign receipts and repeated update.");
    }

    private static async Task<AddonPackageManifest> ReadManifest(string root) =>
        JsonSerializer.Deserialize<AddonPackageManifest>(await File.ReadAllTextAsync(Path.Combine(root, "addon-manifest.json")), Json)
        ?? throw new InvalidDataException("Missing package manifest.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
