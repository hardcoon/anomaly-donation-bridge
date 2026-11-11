using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed record FilePresetLoadResult(
    IReadOnlyList<ActionDefinition> Presets,
    IReadOnlyDictionary<string, string> SourcePaths,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a small, declarative header from each local Lua script. A script is a
/// visible preset only when an identical file is installed in the selected game
/// or MO2 mod directory. Lua is never executed by the desktop application.
/// </summary>
public sealed class FilePresetLoader
{
    public const string HeaderStart = "--[[ADB_PRESET_V1\n";
    public const string HeaderEnd = "\nADB_PRESET_END]]";
    private const int MaximumScriptBytes = 512_000;
    private const int MaximumHeaderCharacters = 16_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "npc_mutants", "simulation", "player_state", "metamorphosis", "inventory", "world"
    };
    private static readonly Regex EffectFileName = new(
        "^anomaly_donation_bridge_effect_[a-z0-9_]+\\.script$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _effectsRoot;

    public FilePresetLoader(string? effectsRoot = null)
    {
        _effectsRoot = Path.GetFullPath(effectsRoot ?? Path.Combine(AppContext.BaseDirectory, "effects"));
    }

    public string EffectsDirectory => Path.Combine(_effectsRoot, "gamedata", "scripts");

    public string Fingerprint(string? gamePath, string? mo2ModPath)
    {
        try
        {
            var files = Directory.Exists(EffectsDirectory)
                ? Directory.EnumerateFiles(EffectsDirectory, "*.script", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            var parts = new List<string> { gamePath ?? string.Empty, mo2ModPath ?? string.Empty };
            foreach (var file in files)
            {
                var source = new FileInfo(file);
                parts.Add($"{source.Name}:{source.Length}:{source.LastWriteTimeUtc.Ticks}");
                foreach (var root in new[] { gamePath, mo2ModPath })
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    var installed = new FileInfo(Path.Combine(root, "gamedata", "scripts", source.Name));
                    parts.Add(installed.Exists
                        ? $"{root}:{installed.Length}:{installed.LastWriteTimeUtc.Ticks}"
                        : $"{root}:missing");
                }
            }
            return string.Join('|', parts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"scan_error:{exception.GetType().Name}:{exception.Message}";
        }
    }

    public FilePresetLoadResult Load(string? gamePath, string? mo2ModPath = null)
    {
        var presets = new List<ActionDefinition>();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        if (!Directory.Exists(EffectsDirectory)) return new FilePresetLoadResult(presets, sources, warnings);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(EffectsDirectory, "*.script", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Не удалось прочитать папку эффектов: {exception.Message}");
            return new FilePresetLoadResult(presets, sources, warnings);
        }

        foreach (var path in files)
        {
            try
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Ссылка на файл вне папки эффектов запрещена.");
                if (file.Length is <= 0 or > MaximumScriptBytes)
                    throw new InvalidDataException("Размер Lua-файла должен быть от 1 до 512000 байт.");
                if (!EffectFileName.IsMatch(file.Name))
                    throw new InvalidDataException("Имя Lua-файла должно начинаться с anomaly_donation_bridge_effect_.");
                var script = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
                if (!script.StartsWith(HeaderStart, StringComparison.Ordinal))
                    throw new InvalidDataException("Отсутствует заголовок ADB_PRESET_V1 в начале файла.");
                var end = script.IndexOf(HeaderEnd, HeaderStart.Length, StringComparison.Ordinal);
                if (end < 0 || end - HeaderStart.Length > MaximumHeaderCharacters)
                    throw new InvalidDataException("Заголовок шаблона отсутствует или слишком велик.");
                var header = JsonSerializer.Deserialize<FilePresetHeader>(
                    script.AsSpan(HeaderStart.Length, end - HeaderStart.Length), JsonOptions)
                    ?? throw new InvalidDataException("Пустой заголовок шаблона.");
                var preset = BuildPreset(header, Path.GetFileName(path));
                if (sources.ContainsKey(preset.HandlerId))
                    throw new InvalidDataException($"Повторный handlerId {preset.HandlerId}.");

                sources.Add(preset.HandlerId, path);
                if (IsInstalled(path, gamePath, mo2ModPath)) presets.Add(preset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                warnings.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return new FilePresetLoadResult(presets, sources, warnings);
    }

    private static bool IsInstalled(string source, string? gamePath, string? mo2ModPath)
    {
        foreach (var root in new[] { gamePath, mo2ModPath })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var installed = Path.Combine(Path.GetFullPath(root), "gamedata", "scripts", Path.GetFileName(source));
                if (!File.Exists(installed)) continue;
                using var sourceStream = File.OpenRead(source);
                using var installedStream = File.OpenRead(installed);
                if (sourceStream.Length != installedStream.Length) continue;
                if (SHA256.HashData(sourceStream).AsSpan().SequenceEqual(SHA256.HashData(installedStream)))
                    return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An inaccessible or malformed target is not proof of installation.
            }
        }
        return false;
    }

    private static ActionDefinition BuildPreset(FilePresetHeader header, string fileName)
    {
        if (header.SchemaVersion != 1)
            throw new InvalidDataException("Требуется schemaVersion 1.");
        var id = (header.HandlerId ?? string.Empty).Trim();
        if (!Validation.ActionId().IsMatch(id) || id.Length > 128)
            throw new InvalidDataException("Недопустимый handlerId.");
        var name = (header.DisplayName ?? string.Empty).Trim();
        var description = (header.Description ?? string.Empty).Trim();
        if (name.Length is 0 or > 128 || name.Any(char.IsControl) ||
            description.Length > 1024 || description.Any(c => char.IsControl(c) && c is not ('\r' or '\n')))
            throw new InvalidDataException("Недопустимые название или описание.");
        if (!Categories.Contains(header.Category ?? string.Empty))
            throw new InvalidDataException("Неизвестная категория шаблона.");
        if (header.CooldownSeconds is < 0 or > 86_400)
            throw new InvalidDataException("Недопустимый cooldownSeconds.");
        var definitions = header.ParameterDefinitions ?? throw new InvalidDataException("Не заданы parameterDefinitions.");
        if (definitions.Count > 32 || definitions.Any(definition => definition is null))
            throw new InvalidDataException("Недопустимый список параметров.");
        if (definitions.Select(definition => definition.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count ||
            definitions.Any(definition => definition.Key is null ||
                !Validation.ParameterKey().IsMatch(definition.Key) ||
                string.Equals(definition.Key, CatalogRevisionEnforcement.RevisionParameterKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Повторный или недопустимый ключ параметра.");
        var values = header.Parameters ?? throw new InvalidDataException("Не заданы parameters.");
        if (values.Any(pair => pair.Key is null || pair.Value is null) ||
            header.ItemSpawns?.Any(entry => entry is null) == true ||
            header.SpawnGroups?.Any(entry => entry is null) == true)
            throw new InvalidDataException("Пустое значение в заголовке шаблона.");
        if (values.Keys.Any(key => definitions.All(definition => !string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Значение для необъявленного параметра.");
        foreach (var definition in definitions)
        {
            if (definition.DisplayName is null || definition.DefaultValue is null ||
                definition.Options is null || definition.OptionGroups is null ||
                definition.OptionLabels is null || definition.CatalogCategories is null || definition.CatalogNativeClasses is null ||
                definition.OptionGroups.Any(group => group.Value is null))
                throw new InvalidDataException($"Неполное описание параметра {definition.Key}.");
            if (!Enum.IsDefined(definition.Kind) || definition.CatalogKind is { } kind && !Enum.IsDefined(kind))
                throw new InvalidDataException($"Неизвестный тип параметра {definition.Key}.");
            if (definition.CatalogCategories.Count > 32 ||
                definition.CatalogCategories.Any(category => string.IsNullOrWhiteSpace(category) ||
                    category.Length > 256 || category.Any(char.IsControl)) ||
                definition.CatalogKind is null && definition.CatalogCategories.Count > 0)
                throw new InvalidDataException(
                    $"Недопустимый фильтр каталога у параметра {definition.Key}.");
            if (definition.CatalogNativeClasses.Count > 32 ||
                definition.CatalogNativeClasses.Any(nativeClass => string.IsNullOrWhiteSpace(nativeClass) ||
                    nativeClass.Length > 32 || nativeClass.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) ||
                definition.CatalogKind is null && definition.CatalogNativeClasses.Count > 0)
                throw new InvalidDataException($"Недопустимый фильтр нативного класса у параметра {definition.Key}.");
            var value = values.TryGetValue(definition.Key, out var saved) ? saved : definition.DefaultValue;
            if (definition.CatalogKind is null &&
                !(id == ActiveTemplateSelection.HandlerId && ActiveTemplateSelection.IsParameter(definition) && string.IsNullOrEmpty(value)) &&
                definition.Validate(value) is { } error)
                throw new InvalidDataException(error);
        }

        return new ActionDefinition
        {
            Id = id,
            HandlerId = id,
            DisplayName = name,
            Description = description,
            CategoryId = header.Category!,
            ScriptRelativePath = Path.Combine("gamedata", "scripts", fileName),
            CooldownSeconds = header.CooldownSeconds,
            ParameterDefinitions = definitions,
            Parameters = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
            ItemSpawns = header.ItemSpawns ?? [],
            SpawnGroups = header.SpawnGroups ?? []
        };
    }
}

public sealed class FilePresetHeader
{
    public int SchemaVersion { get; set; }
    public string? HandlerId { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public int CooldownSeconds { get; set; } = 3;
    public List<ParameterDefinition>? ParameterDefinitions { get; set; } = [];
    public Dictionary<string, string>? Parameters { get; set; } = [];
    public List<ItemSpawnEntry>? ItemSpawns { get; set; } = [];
    public List<SpawnGroupEntry>? SpawnGroups { get; set; } = [];
}
