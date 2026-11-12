using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public enum AddonInstallState
{
    PackageUnavailable,
    InvalidPackage,
    NotInstalled,
    Current,
    UpdateAvailable,
    Conflict
}

public sealed record AddonInstallationStatus(
    AddonInstallState State,
    string Message,
    string PackageVersion = "",
    string InstalledVersion = "")
{
    public bool CanInstall => State is AddonInstallState.NotInstalled or AddonInstallState.UpdateAvailable;
}

public sealed class AddonPackageManager
{
    public const string ProductId = "AnomalyDonationBridge";
    public const string ProtocolVersion = "1";
    public const string PipeName = "AnomalyDonationBridge.v1";
    public const string ShippedAddonVersion = "1.0.182";

    private static readonly Regex ManagedPath = new(
        "^(?:gamedata/scripts/anomaly_donation_bridge_[a-z0-9_]+\\.script|" +
        "gamedata/configs/misc/task/tm_anomaly_donation_bridge\\.ltx|" +
        "gamedata/configs/mod_system_anomaly_donation_bridge\\.ltx|" +
        "gamedata/configs/models/weapons/anomaly_donation_bridge_stationary_mgun\\.ltx|" +
        "gamedata/meshes/anomaly_donation_bridge/stationary_mgun(?:_r(?:020|040|060|090|120))?\\.ogf|" +
        "gamedata/meshes/anomaly_donation_bridge/btr_olive\\.ogf|" +
        "gamedata/textures/anomaly_donation_bridge/btr_olive\\.dds|" +
        "gamedata/configs/mod_system_anomaly_donation_bridge_mutant_v2\\.ltx|" +
        "gamedata/configs/mod_system_zzzzzzzz_adb_mutant_v2_relations\\.ltx|" +
        "gamedata/configs/ui/anomaly_donation_bridge_mutant_v2\\.xml|" +
        "gamedata/configs/ui/anomaly_donation_bridge_voting\\.xml|" +
        "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_mutant_v2\\.xml|" +
        "gamedata/textures/ui/anomaly_donation_bridge_mutant_v2\\.dds|" +
        "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_burer_form\\.xml|" +
        "gamedata/textures/ui/anomaly_donation_bridge_burer_form\\.dds|" +
        "gamedata/configs/gameplay/dialogs_anomaly_donation_bridge\\.xml|" +
        "gamedata/configs/scripts/anomaly_donation_bridge_wandering_trader\\.ltx|" +
        "gamedata/configs/misc/trade/anomaly_donation_bridge_wandering_trader\\.ltx|" +
        "gamedata/configs/scripts/anomaly_donation_bridge_annoying_fan\\.ltx|" +
        "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_roulette\\.xml|" +
        "gamedata/configs/ui/textures_descr/anomaly_donation_bridge_voting\\.xml|" +
        "gamedata/configs/text/rus/st_anomaly_donation_bridge_roulette\\.xml|" +
        "gamedata/textures/ui/anomaly_donation_bridge_roulette_atlas\\.dds|" +
        "gamedata/textures/ui/anomaly_donation_bridge_voting_material\\.dds|" +
        "gamedata/sounds/ui/anomaly_donation_bridge_roulette_(?:coins|tick)\\.ogg)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;
    private readonly string _packageRoot;
    private readonly string _expectedAddonVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AddonPackageManager(
        AppPaths paths,
        string? packageRoot = null,
        string? expectedAddonVersion = null)
    {
        _paths = paths;
        _packageRoot = Path.GetFullPath(packageRoot ?? FindPackageRoot());
        _expectedAddonVersion = expectedAddonVersion ?? ShippedAddonVersion;
    }

    public async Task<AddonInstallationStatus> InspectAsync(
        string gameRoot,
        CancellationToken cancellationToken = default,
        CatalogSnapshot? mutantCatalog = null)
    {
        try
        {
            var package = await LoadAndVerifyPackageAsync(cancellationToken).ConfigureAwait(false);
            var receipt = await LoadReceiptAsync(cancellationToken).ConfigureAwait(false);
            package = await WithMutantVariantsAsync(gameRoot, package, receipt, mutantCatalog, cancellationToken);
            return await InspectAsync(gameRoot, package, receipt, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            return new AddonInstallationStatus(AddonInstallState.PackageUnavailable, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new AddonInstallationStatus(AddonInstallState.InvalidPackage, exception.Message);
        }
    }

    public async Task<AddonInstallationStatus> InstallOrUpdateAsync(
        string gameRoot,
        CancellationToken cancellationToken = default,
        CatalogSnapshot? mutantCatalog = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var package = await LoadAndVerifyPackageAsync(cancellationToken).ConfigureAwait(false);
            var receipt = await LoadReceiptAsync(cancellationToken).ConfigureAwait(false);
            package = await WithMutantVariantsAsync(gameRoot, package, receipt, mutantCatalog, cancellationToken);
            var status = await InspectAsync(gameRoot, package, receipt, cancellationToken).ConfigureAwait(false);
            if (status.State == AddonInstallState.Current)
            {
                return status;
            }
            if (!status.CanInstall)
            {
                throw new InvalidOperationException(status.Message);
            }

            var normalizedGameRoot = NormalizeGameRoot(gameRoot);
            var previousFiles = ReceiptMatchesGame(receipt, normalizedGameRoot)
                ? receipt!.Files
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backupRoot = Path.Combine(
                _paths.AddonBackupsDirectory,
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));

            // Snapshot the complete managed write set before the first replacement.
            // A failed install restores all changed files, not just the current one.
            var originals = package.Manifest.Files.Select(f => NormalizeManagedPath(f.Path))
                .Concat(previousFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p => p, p => File.Exists(ResolveUnderRoot(normalizedGameRoot, p))
                    ? File.ReadAllBytes(ResolveUnderRoot(normalizedGameRoot, p)) : null,
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var file in package.Manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = NormalizeManagedPath(file.Path);
                    var source = ResolveUnderRoot(_packageRoot, relativePath);
                    var target = ResolveUnderRoot(normalizedGameRoot, relativePath);
                    if (File.Exists(target))
                    {
                        var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
                        if (HashEquals(currentHash, file.Sha256)) continue;
                        Backup(target, ResolveUnderRoot(backupRoot, relativePath));
                    }

                    var directory = Path.GetDirectoryName(target)
                        ?? throw new InvalidDataException($"Не удалось определить папку для {relativePath}.");
                    Directory.CreateDirectory(directory);
                    var temporary = target + ".adb.tmp";
                    try
                    {
                        if (package.Generated.TryGetValue(relativePath, out var generated))
                            await File.WriteAllBytesAsync(temporary, generated, cancellationToken).ConfigureAwait(false);
                        else File.Copy(source, temporary, true);
                        var copiedHash = await Sha256Async(temporary, cancellationToken).ConfigureAwait(false);
                        if (!HashEquals(copiedHash, file.Sha256))
                        {
                            throw new IOException($"Контрольная сумма изменилась при копировании {relativePath}.");
                        }
                        File.Move(temporary, target, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }

                var currentPaths = package.Manifest.Files
                    .Select(file => NormalizeManagedPath(file.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var previous in previousFiles.Where(pair => !currentPaths.Contains(pair.Key)))
                {
                    var relativePath = NormalizeManagedPath(previous.Key);
                    // Existing saves may contain a native visual name for these
                    // retired profiles. They are no longer shipped or selectable.
                    if (IsLegacyMgunVisual(relativePath)) continue;
                    var target = ResolveUnderRoot(normalizedGameRoot, relativePath);
                    if (!File.Exists(target)) continue;
                    var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
                    if (!HashEquals(currentHash, previous.Value))
                    {
                        throw new InvalidOperationException(
                            $"Устаревший файл {relativePath} изменён вне приложения и не будет удалён.");
                    }
                    Backup(target, ResolveUnderRoot(backupRoot, relativePath));
                    File.Delete(target);
                }

                var newReceipt = new AddonInstallationReceipt
                {
                    GameRoot = normalizedGameRoot,
                    AddonVersion = package.Manifest.AddonVersion,
                    InstalledAt = DateTimeOffset.UtcNow,
                    Files = package.Manifest.Files.ToDictionary(
                        file => NormalizeManagedPath(file.Path),
                        file => NormalizeHash(file.Sha256),
                        StringComparer.OrdinalIgnoreCase)
                };
                await SaveReceiptAsync(newReceipt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var original in originals)
                {
                    var target = ResolveUnderRoot(normalizedGameRoot, original.Key);
                    if (original.Value is null) { if (File.Exists(target)) File.Delete(target); }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, original.Value);
                    }
                }
                throw;
            }
            return new AddonInstallationStatus(
                AddonInstallState.Current,
                $"Аддон {package.Manifest.AddonVersion} установлен и проверен.",
                package.Manifest.AddonVersion,
                package.Manifest.AddonVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AddonInstallationStatus> InspectAsync(
        string gameRoot,
        VerifiedAddonPackage package,
        AddonInstallationReceipt? receipt,
        CancellationToken cancellationToken)
    {
        var normalizedGameRoot = NormalizeGameRoot(gameRoot);
        var receiptMatches = ReceiptMatchesGame(receipt, normalizedGameRoot);
        var missing = 0;
        var outdated = 0;
        var obsolete = 0;
        var conflicts = new List<string>();
        var packagePaths = package.Manifest.Files
            .Select(file => NormalizeManagedPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in package.Manifest.Files)
        {
            var relativePath = NormalizeManagedPath(file.Path);
            var target = ResolveUnderRoot(normalizedGameRoot, relativePath);
            if (!File.Exists(target))
            {
                missing++;
                continue;
            }

            var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
            if (HashEquals(currentHash, file.Sha256)) continue;
            if (receiptMatches && receipt!.Files.TryGetValue(relativePath, out var previousHash) &&
                (HashEquals(currentHash, previousHash) || IsKnownStandaloneHotfix(relativePath, currentHash)))
            {
                outdated++;
                continue;
            }
            conflicts.Add(relativePath);
        }

        if (receiptMatches)
        {
            foreach (var previous in receipt!.Files.Where(pair => !packagePaths.Contains(pair.Key)))
            {
                var relativePath = NormalizeManagedPath(previous.Key);
                if (IsLegacyMgunVisual(relativePath)) continue;
                var target = ResolveUnderRoot(normalizedGameRoot, relativePath);
                if (!File.Exists(target)) continue;
                var currentHash = await Sha256Async(target, cancellationToken).ConfigureAwait(false);
                if (!HashEquals(currentHash, previous.Value))
                {
                    conflicts.Add(relativePath);
                }
                else
                {
                    obsolete++;
                }
            }
        }

        var scriptsRoot = Path.Combine(normalizedGameRoot, "gamedata", "scripts");
        if (Directory.Exists(scriptsRoot))
        {
            foreach (var candidate in Directory.EnumerateFiles(
                         scriptsRoot,
                         "anomaly_donation_bridge_*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(Path.GetExtension(candidate), ".script", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(normalizedGameRoot, candidate).Replace('\\', '/');
                var trackedByPreviousManifest = receiptMatches && receipt!.Files.ContainsKey(relativePath);
                // Optional one-file effects are installed and checked by the
                // separate effect manager. They are not core package conflicts.
                var optionalEffect = Path.GetFileName(candidate).StartsWith(
                    "anomaly_donation_bridge_effect_", StringComparison.OrdinalIgnoreCase);
                if (!packagePaths.Contains(relativePath) && !trackedByPreviousManifest && !optionalEffect)
                {
                    conflicts.Add(relativePath);
                }
            }
        }

        if (conflicts.Count > 0)
        {
            var distinctConflicts = conflicts
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            return new AddonInstallationStatus(
                AddonInstallState.Conflict,
                "Установка остановлена: обнаружены чужие или вручную изменённые файлы: " +
                string.Join(", ", distinctConflicts),
                package.Manifest.AddonVersion,
                receiptMatches ? receipt!.AddonVersion : string.Empty);
        }
        if (missing == package.Manifest.Files.Count && !receiptMatches)
        {
            return new AddonInstallationStatus(
                AddonInstallState.NotInstalled,
                $"Аддон не установлен. Доступна версия {package.Manifest.AddonVersion}.",
                package.Manifest.AddonVersion);
        }
        if (missing > 0 || outdated > 0 || obsolete > 0 || !receiptMatches ||
            !string.Equals(receipt!.AddonVersion, package.Manifest.AddonVersion, StringComparison.Ordinal))
        {
            return new AddonInstallationStatus(
                AddonInstallState.UpdateAvailable,
                $"Требуется установка/обновление: отсутствует {missing}, устарело {outdated}, будет удалено {obsolete} файлов.",
                package.Manifest.AddonVersion,
                receiptMatches ? receipt!.AddonVersion : string.Empty);
        }
        return new AddonInstallationStatus(
            AddonInstallState.Current,
            $"Аддон {package.Manifest.AddonVersion} установлен; SHA-256 совпадают.",
            package.Manifest.AddonVersion,
            receipt.AddonVersion);
    }

    private async Task<VerifiedAddonPackage> LoadAndVerifyPackageAsync(CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_packageRoot, "addon-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("В поставке приложения отсутствует addon-package\\addon-manifest.json.", manifestPath);
        }
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<AddonPackageManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Manifest аддона пуст.");
        if (manifest.SchemaVersion != 1 || !string.Equals(manifest.Product, ProductId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.PipeName, PipeName, StringComparison.Ordinal) ||
            !string.Equals(manifest.AddonVersion, _expectedAddonVersion, StringComparison.Ordinal) ||
            manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException(
                $"Manifest аддона несовместим с этой версией приложения: требуется addonVersion {_expectedAddonVersion}.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("Manifest аддона содержит null вместо описания файла.");
            }
            var relativePath = NormalizeManagedPath(file.Path);
            if (!unique.Add(relativePath)) throw new InvalidDataException($"Повтор файла в manifest: {relativePath}.");
            var expectedHash = NormalizeHash(file.Sha256);
            var source = ResolveUnderRoot(_packageRoot, relativePath);
            if (!File.Exists(source)) throw new FileNotFoundException($"Файл пакета отсутствует: {relativePath}.", source);
            var actualHash = await Sha256Async(source, cancellationToken).ConfigureAwait(false);
            if (!HashEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException($"SHA-256 пакета не совпадает: {relativePath}.");
            }
        }
        return new VerifiedAddonPackage(manifest);
    }

    private async Task<AddonInstallationReceipt?> LoadReceiptAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.AddonInstallationReceiptFile)) return null;
        try
        {
            await using var stream = new FileStream(_paths.AddonInstallationReceiptFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var receipt = await JsonSerializer.DeserializeAsync<AddonInstallationReceipt>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (receipt is not { SchemaVersion: 1 } ||
                string.IsNullOrWhiteSpace(receipt.GameRoot) ||
                string.IsNullOrWhiteSpace(receipt.AddonVersion) ||
                receipt.Files is null)
            {
                return null;
            }
            foreach (var file in receipt.Files)
            {
                _ = NormalizeManagedPath(file.Key);
                _ = NormalizeHash(file.Value);
            }
            return receipt;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task SaveReceiptAsync(AddonInstallationReceipt receipt, CancellationToken cancellationToken)
    {
        _paths.EnsureExists();
        var temporary = _paths.AddonInstallationReceiptFile + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, receipt, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _paths.AddonInstallationReceiptFile, true);
    }

    private static bool ReceiptMatchesGame(AddonInstallationReceipt? receipt, string normalizedGameRoot)
    {
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.GameRoot) || receipt.Files is null) return false;
        try
        {
            var receiptRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(receipt.GameRoot));
            return string.Equals(receiptRoot, normalizedGameRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeGameRoot(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) throw new InvalidDataException("Не выбрана папка игры.");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        if (!File.Exists(Path.Combine(normalized, "fsgame.ltx")) || !Directory.Exists(Path.Combine(normalized, "bin")))
        {
            throw new InvalidDataException("Выбранная папка не похожа на корень S.T.A.L.K.E.R. Anomaly.");
        }
        return normalized;
    }

    // One-file ADB hotfixes were installed manually after 1.3.19, so the old
    // receipt still contains release hashes. Only these exact owned bytes may
    // migrate through the normal backed-up installer; arbitrary edits remain
    // conflicts.
    private static bool IsKnownStandaloneHotfix(string path, string hash) =>
        (path.ToLowerInvariant(), NormalizeHash(hash)) switch
        {
            ("gamedata/scripts/anomaly_donation_bridge_npc_visuals.script",
                "AFCF38C21006F4A0EC37C0D0B83426893149AA2AB1C75C558BC49A08339B58A2") => true,
            ("gamedata/scripts/anomaly_donation_bridge_btr_ai.script",
                "E6173F310808A347275AC5112CB8C597F0E4DB926DC139E4C122EAC03EA16BB0" or
                "CF2B47C3D558EF6003F2453E639B1887858FEF6E32B6FA1EB107576866D1DFE4") => true,
            _ => false
        };

    private static bool IsLegacyMgunVisual(string path) => path is
        "gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r020.ogf" or
        "gamedata/meshes/anomaly_donation_bridge/stationary_mgun_r040.ogf";

    private static string NormalizeManagedPath(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (!ManagedPath.IsMatch(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Manifest пытается управлять недопустимым путём: {path}.");
        }
        return normalized;
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Путь выходит за разрешённую папку: {relativePath}.");
        }
        return fullPath;
    }

    private static void Backup(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.Ordinal);

    private static string NormalizeHash(string? hash)
    {
        var normalized = (hash ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Manifest содержит недопустимую SHA-256 сумму.");
        }
        return normalized;
    }

    public static string ResolveTemplateSource(string relativePath)
    {
        if (!TemplateSourceLinks.For("hardcoon.adb.btr_companion").Any(link => link.RelativePath == relativePath))
            throw new InvalidOperationException("Недопустимый путь исходного файла.");
        var file = ResolveUnderRoot(FindPackageRoot(), relativePath);
        if (!File.Exists(file)) throw new FileNotFoundException("Файл отсутствует в addon-package: " + relativePath, file);
        return file;
    }

    private static string FindPackageRoot()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "addon-package");
        if (File.Exists(Path.Combine(shipped, "addon-manifest.json"))) return shipped;

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            var development = Path.Combine(cursor.FullName, "game-addon");
            if (File.Exists(Path.Combine(development, "addon-manifest.json"))) return development;
        }
        return shipped;
    }

    private async Task<VerifiedAddonPackage> WithMutantVariantsAsync(string gameRoot,
        VerifiedAddonPackage package, AddonInstallationReceipt? receipt, CatalogSnapshot? catalog,
        CancellationToken cancellationToken)
    {
        if (!package.Manifest.Files.Any(f => f.Path == MutantCompanionVariants.UiPath)) return package;
        var root = NormalizeGameRoot(gameRoot);
        var canPrepare = !string.IsNullOrEmpty(catalog?.MonsterCommunities);
        foreach (var path in new[] { MutantCompanionVariants.RelativePath, MutantCompanionVariants.RelationsPath })
        {
            var target = ResolveUnderRoot(root, path);
            byte[] bytes;
            // Preserve BOTH definitions when no usable catalog is available.
            // Never remove a species that existing savegame objects still use.
            if (!canPrepare && ReceiptMatchesGame(receipt, root) &&
                receipt!.Files.TryGetValue(path, out var hash) && File.Exists(target) &&
                HashEquals(await Sha256Async(target, cancellationToken), hash))
                bytes = await File.ReadAllBytesAsync(target, cancellationToken);
            else if (path == MutantCompanionVariants.RelativePath) bytes = MutantCompanionVariants.Generate(catalog);
            else
            {
                var relations = MutantCompanionVariants.GenerateRelations(catalog);
                if (relations is null) continue;
                bytes = relations;
            }
            package.Manifest.Files.Add(new AddonPackageFile
            {
                Path = path,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
            package.Generated[path] = bytes;
        }
        return package;
    }

    private sealed record VerifiedAddonPackage(AddonPackageManifest Manifest)
    {
        public Dictionary<string, byte[]> Generated { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class AddonPackageManifest
{
    public int SchemaVersion { get; set; }
    public string Product { get; set; } = string.Empty;
    public string AddonVersion { get; set; } = string.Empty;
    public string ProtocolVersion { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public List<AddonPackageFile> Files { get; set; } = [];
}

public sealed class AddonPackageFile
{
    public string Path { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class AddonInstallationReceipt
{
    public int SchemaVersion { get; set; } = 1;
    public string GameRoot { get; set; } = string.Empty;
    public string AddonVersion { get; set; } = string.Empty;
    public DateTimeOffset InstalledAt { get; set; }
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
