using System.Security.Cryptography;
using System.Text.Json;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.Services;

public sealed record EffectScriptInstallationStatus(
    int SourceCount,
    int InstalledCount,
    bool CanInstall,
    string Message);

/// <summary>
/// Copies self-describing effect scripts into one explicitly selected game/MO2
/// root. It never deletes user-owned scripts or overwrites a file not previously
/// installed by this manager.
/// </summary>
public sealed class EffectScriptManager
{
    private readonly AppPaths _paths;
    private readonly FilePresetLoader _loader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public EffectScriptManager(AppPaths paths, FilePresetLoader loader)
    {
        _paths = paths;
        _loader = loader;
    }

    public Task<EffectScriptInstallationStatus> InspectAsync(string targetRoot) =>
        Task.Run(() => Inspect(targetRoot));

    public async Task<EffectScriptInstallationStatus> InstallAllAsync(
        string targetRoot, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => InstallAll(targetRoot, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task PreflightAsync(string targetRoot, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (PrepareCopies(targetRoot, cancellationToken).Count == 0)
                throw new InvalidOperationException("В папке effects нет шаблонов для установки.");
        }, cancellationToken);

    private EffectScriptInstallationStatus Inspect(string targetRoot)
    {
        if (!Directory.Exists(targetRoot))
            return new EffectScriptInstallationStatus(0, 0, false, "Папка для установки шаблонов не найдена.");
        var result = _loader.Load(null);
        var installed = _loader.Load(targetRoot).Presets.Count;
        var message = $"Шаблонов в папке: {result.SourcePaths.Count}. Установлено: {installed}.";
        if (result.Warnings.Count > 0)
            message += " Ошибки: " + string.Join("; ", result.Warnings.Take(3));
        return new EffectScriptInstallationStatus(
            result.SourcePaths.Count, installed,
            result.SourcePaths.Count > installed && result.Warnings.Count == 0,
            message);
    }

    private EffectScriptInstallationStatus InstallAll(string targetRoot, CancellationToken cancellationToken)
    {
        var root = NormalizeExistingRoot(targetRoot);
        var copies = PrepareCopies(root, cancellationToken);
        if (copies.Count == 0)
            return new EffectScriptInstallationStatus(0, 0, false, "В папке шаблонов нет файлов для установки.");

        var backupRoot = Path.Combine(_paths.EffectBackupsDirectory,
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        foreach (var copy in copies.Where(copy => !HashEquals(copy.ExistingHash, copy.SourceHash)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (copy.ExistingHash is not null)
            {
                Directory.CreateDirectory(backupRoot);
                File.Copy(copy.Target, Path.Combine(backupRoot, copy.Name), false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(copy.Target)!);
            var temporary = copy.Target + ".adb.tmp";
            try
            {
                File.Copy(copy.Source, temporary, true);
                if (!HashEquals(HashFile(temporary), copy.SourceHash))
                    throw new IOException($"SHA-256 изменился при копировании {copy.Name}.");
                File.Move(temporary, copy.Target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        SaveReceipt(new EffectInstallationReceipt
        {
            TargetRoot = root,
            Files = copies.ToDictionary(copy => copy.Name, copy => copy.SourceHash,
                StringComparer.OrdinalIgnoreCase)
        });
        return new EffectScriptInstallationStatus(copies.Count, copies.Count, false,
            $"Установлено шаблонов: {copies.Count}.");
    }

    private List<EffectCopy> PrepareCopies(string targetRoot, CancellationToken cancellationToken)
    {
        var root = NormalizeExistingRoot(targetRoot);
        var source = _loader.Load(null);
        if (source.Warnings.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, source.Warnings));

        var receipt = LoadReceipt();
        var managedFiles = string.Equals(receipt?.TargetRoot, root, StringComparison.OrdinalIgnoreCase)
            ? receipt!.Files
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var copies = new List<EffectCopy>();
        foreach (var path in source.SourcePaths.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var target = Path.Combine(root, "gamedata", "scripts", name);
            var sourceHash = HashFile(path);
            var existingHash = File.Exists(target) ? HashFile(target) : null;
            if (existingHash is not null && !HashEquals(existingHash, sourceHash) &&
                (!managedFiles.TryGetValue(name, out var previousHash) || !HashEquals(existingHash, previousHash)))
            {
                throw new InvalidOperationException(
                    $"{name}: установленный файл отличается от известной версии; чужой файл не будет перезаписан.");
            }
            copies.Add(new EffectCopy(path, target, name, sourceHash, existingHash));
        }
        return copies;
    }

    private static string NormalizeExistingRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException("Укажите существующую папку игры или мода MO2.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HashEquals(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private EffectInstallationReceipt? LoadReceipt()
    {
        if (!File.Exists(_paths.EffectInstallationReceiptFile)) return null;
        var receipt = JsonSerializer.Deserialize<EffectInstallationReceipt>(
            File.ReadAllText(_paths.EffectInstallationReceiptFile), JsonOptions);
        if (receipt?.Files is null) return null;
        return receipt;
    }

    private void SaveReceipt(EffectInstallationReceipt receipt)
    {
        _paths.EnsureExists();
        var temporary = _paths.EffectInstallationReceiptFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, JsonOptions));
            File.Move(temporary, _paths.EffectInstallationReceiptFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record EffectCopy(
        string Source, string Target, string Name, string SourceHash, string? ExistingHash);

    private sealed class EffectInstallationReceipt
    {
        public string TargetRoot { get; set; } = string.Empty;
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
