using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.Tests;

internal static class RetiredIntegrationTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? Path.GetTempPath(),
            "retired-credentials-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root); paths.EnsureExists();
        // Create an old encrypted file without retaining retired properties in
        // the production SecretBundle DTO. All values are synthetic.
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["clientSecret"] = "keep-client-secret", ["accessToken"] = "keep-access",
            ["refreshToken"] = "keep-refresh", ["socketConnectionToken"] = "keep-socket",
            ["donateXExternalToken"] = "keep-donatex", ["accessTokenExpiresAt"] = DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
            ["donatePayApiKey"] = "retired-fake-key", ["hasDonatePayKey"] = true
        });
        var crypto = typeof(DpapiSecretStore).GetNestedType("Dpapi", BindingFlags.NonPublic)!;
        var entropy = Encoding.UTF8.GetBytes("AnomalyDonationBridge|secrets|v1");
        var encrypted = (byte[])crypto.GetMethod("Protect", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [legacy, entropy])!;
        await File.WriteAllTextAsync(paths.SecretsFile, Convert.ToBase64String(encrypted));
        Array.Clear(legacy); Array.Clear(encrypted);

        var store = new DpapiSecretStore(paths);
        var current = await store.LoadAsync();
        Check(current.ClientSecret == "keep-client-secret" && current.AccessToken == "keep-access" &&
            current.RefreshToken == "keep-refresh" && current.SocketConnectionToken == "keep-socket" &&
            current.DonateXExternalToken == "keep-donatex" && current.HasOAuthTokens && current.HasDonateXToken,
            "Removing the retired key changed another integration's credentials.");
        Check(current.AccessTokenExpiresAt == DateTimeOffset.Parse("2030-01-01T00:00:00Z"), "OAuth expiry changed during cleanup.");
        var migratedBytes = await File.ReadAllBytesAsync(paths.SecretsFile);
        var plain = (byte[])crypto.GetMethod("Unprotect", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
            [Convert.FromBase64String(Encoding.UTF8.GetString(migratedBytes)), entropy])!;
        try
        {
            using var document = JsonDocument.Parse(plain);
            Check(!document.RootElement.EnumerateObject().Any(p => p.Name.Contains("donatepay", StringComparison.OrdinalIgnoreCase)),
                "The retired key is still present in encrypted storage.");
        }
        finally { Array.Clear(plain); }
        await store.LoadAsync();
        Check(SHA256.HashData(migratedBytes).SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(paths.SecretsFile))),
            "Credential cleanup rewrites current files on every launch.");

        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"schemaVersion\":13,\"donatePayUserId\":\"retired\",\"lastDonatePayTransactionId\":123," +
            "\"donatePayPendingTransactionIds\":[120],\"donateXUserId\":\"current\",\"donateXAccountName\":\"channel\"}");
        var settingsStore = new JsonSettingsStore(paths);
        var settings = await settingsStore.LoadAsync();
        await settingsStore.SaveAsync(settings);
        Check(!File.ReadAllText(paths.SettingsFile).Contains("donatepay", StringComparison.OrdinalIgnoreCase) &&
            settings.DonateXUserId == "current" && settings.DonateXAccountName == "channel",
            "Old settings failed to load cleanly or changed the active provider.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
