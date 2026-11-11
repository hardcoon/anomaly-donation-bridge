using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.App.Services;

public sealed record ExtensionActionOrigin(
    string ExtensionId,
    string ExtensionName,
    string ManifestPath)
{
    public string DisplayLabel => $"Расширение: {ExtensionName} ({ExtensionId})";
}

public sealed record ExtensionManifestError(string ManifestPath, string Message);

public sealed record ExtensionLoadResult(
    IReadOnlyList<ActionDefinition> Actions,
    IReadOnlyDictionary<string, ExtensionActionOrigin> Origins,
    IReadOnlyList<ExtensionManifestError> Errors);

/// <summary>
/// Loads declarative action metadata only. It never loads assemblies, invokes
/// scripts, edits the game, or grants a handler support that the live addon did
/// not advertise through CAPABILITIES.
/// </summary>
public sealed class ExtensionManifestLoader
{
    public const int SchemaVersion = 1;
    public const string ProtocolVersion = "1";
    public const int MaxActionsPerManifest = 100;
    public const int MaxTotalActions = 64;
    private static readonly Regex ExtensionIdFormat = new(
        "^[a-z][a-z0-9_-]{0,31}(?:\\.[a-z][a-z0-9_-]{0,31})+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Compiled);
    private static readonly Regex HandlerIdFormat = new(
        "^[a-z][a-z0-9_-]{0,31}(?:\\.[a-z][a-z0-9_-]{0,31}){2,}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Compiled);
    private static readonly Regex ScriptPathFormat = new(
        "^gamedata/scripts/[a-z0-9_.-]+\\.script$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;

    public ExtensionManifestLoader(AppPaths paths) => _paths = paths;

    public ExtensionLoadResult Load(IEnumerable<string> builtInHandlerIds)
    {
        var builtIns = builtInHandlerIds.ToHashSet(StringComparer.Ordinal);
        var actions = new List<ActionDefinition>();
        var origins = new Dictionary<string, ExtensionActionOrigin>(StringComparer.Ordinal);
        var errors = new List<ExtensionManifestError>();
        if (!Directory.Exists(_paths.ExtensionsDirectory))
        {
            return new ExtensionLoadResult(actions, origins, errors);
        }

        IEnumerable<string> manifests;
        try
        {
            manifests = Directory.EnumerateFiles(
                    _paths.ExtensionsDirectory,
                    "*.adb-extension.json",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ExtensionManifestError(_paths.ExtensionsDirectory, exception.Message));
            return new ExtensionLoadResult(actions, origins, errors);
        }

        foreach (var manifestPath in manifests)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ExtensionManifest>(
                    File.ReadAllText(manifestPath),
                    JsonOptions) ?? throw new InvalidDataException("Manifest пуст.");
                ValidateHeader(manifest);
                var manifestActions = manifest.Actions!
                    .Select((declared, index) => BuildAction(manifest, declared, index))
                    .ToList();
                if (manifestActions.Select(action => action.HandlerId).Distinct(StringComparer.Ordinal).Count() != manifestActions.Count)
                {
                    throw new InvalidDataException("Manifest повторно объявляет один handlerId.");
                }
                foreach (var action in manifestActions)
                {
                    if (builtIns.Contains(action.HandlerId))
                    {
                        throw new InvalidDataException(
                            $"Расширение не может переопределить встроенный обработчик {action.HandlerId}.");
                    }
                    if (origins.ContainsKey(action.HandlerId))
                    {
                        throw new InvalidDataException(
                            $"Обработчик {action.HandlerId} уже объявлен другим расширением.");
                    }
                }
                var remainingCapacity = MaxTotalActions - actions.Count;
                if (manifestActions.Count > remainingCapacity)
                {
                    throw new InvalidDataException(
                        $"Manifest объявляет {manifestActions.Count} действий, но общий лимит декларативных расширений — {MaxTotalActions}; доступно {remainingCapacity}. Manifest отклонён целиком.");
                }
                var origin = new ExtensionActionOrigin(
                        manifest.ExtensionId!,
                        manifest.DisplayName!,
                        manifestPath);
                foreach (var action in manifestActions)
                {
                    actions.Add(action);
                    origins.Add(action.HandlerId, origin);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new ExtensionManifestError(manifestPath, exception.Message));
            }
        }
        return new ExtensionLoadResult(actions, origins, errors);
    }

    private static void ValidateHeader(ExtensionManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Неподдерживаемая schemaVersion {manifest.SchemaVersion}; требуется {SchemaVersion}.");
        }
        if (!string.Equals(manifest.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Неподдерживаемая protocolVersion {manifest.ProtocolVersion}; требуется {ProtocolVersion}.");
        }
        var extensionId = (manifest.ExtensionId ?? string.Empty).Trim();
        if (!ExtensionIdFormat.IsMatch(extensionId))
        {
            throw new InvalidDataException(
                "extensionId должен быть namespaced ID из двух или более сегментов, например vendor.modpack.");
        }
        var displayName = (manifest.DisplayName ?? string.Empty).Trim();
        if (displayName.Length is 0 or > 128 || displayName.Any(char.IsControl))
        {
            throw new InvalidDataException("У расширения отсутствует допустимое displayName.");
        }
        if (manifest.Actions is null || manifest.Actions.Count is 0 ||
            manifest.Actions.Count > MaxActionsPerManifest)
        {
            throw new InvalidDataException(
                $"Manifest должен объявлять от 1 до {MaxActionsPerManifest} действий.");
        }

        manifest.ExtensionId = extensionId;
        manifest.DisplayName = displayName;
    }

    private static ActionDefinition BuildAction(
        ExtensionManifest manifest,
        ExtensionActionManifest? declared,
        int declaredIndex)
    {
        if (declared is null)
        {
            throw new InvalidDataException($"Элемент actions[{declaredIndex}] не может быть null.");
        }

        var handlerId = (declared.HandlerId ?? string.Empty).Trim().ToLowerInvariant();
        var requiredPrefix = manifest.ExtensionId! + ".";
        if (!HandlerIdFormat.IsMatch(handlerId) ||
            !handlerId.StartsWith(requiredPrefix, StringComparison.Ordinal) ||
            handlerId.Length > 128)
        {
            throw new InvalidDataException(
                $"handlerId {handlerId} должен начинаться с {requiredPrefix}, иметь непустой namespaced-сегмент обработчика и содержать только сегменты a-z, 0-9, '_' или '-'.");
        }

        var scriptPath = NormalizeScriptPath(declared.ScriptRelativePath);
        var filePrefix = manifest.ExtensionId!.Replace('.', '_').Replace('-', '_') + "_";
        if (!Path.GetFileName(scriptPath).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Имя scriptRelativePath должно начинаться с namespaced-префикса {filePrefix}.");
        }
        var displayName = (declared.DisplayName ?? string.Empty).Trim();
        var description = (declared.Description ?? string.Empty).Trim();
        if (displayName.Length is 0 or > 128 || displayName.Any(char.IsControl) ||
            description.Length > 1024)
        {
            throw new InvalidDataException($"У обработчика {handlerId} недопустимые displayName/description.");
        }
        if (declared.Parameters is null)
        {
            throw new InvalidDataException($"У обработчика {handlerId} parameters не может быть null.");
        }
        if (declared.Parameters.Count > 32)
        {
            throw new InvalidDataException($"У обработчика {handlerId} больше 32 параметров.");
        }

        var definitions = declared.Parameters
            .Select((parameter, index) => BuildParameter(handlerId, parameter, index))
            .ToList();
        if (definitions.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count)
        {
            throw new InvalidDataException($"У обработчика {handlerId} повторяются ключи параметров.");
        }
        return new ActionDefinition
        {
            Id = handlerId,
            HandlerId = handlerId,
            DisplayName = displayName,
            Description = description,
            ScriptRelativePath = scriptPath.Replace('/', Path.DirectorySeparatorChar),
            IsActive = false,
            CooldownSeconds = Math.Clamp(declared.CooldownSeconds, 0, 86_400),
            ParameterDefinitions = definitions,
            Parameters = definitions.ToDictionary(
                definition => definition.Key,
                definition => definition.DefaultValue,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ParameterDefinition BuildParameter(
        string handlerId,
        ExtensionParameterManifest? parameter,
        int parameterIndex)
    {
        if (parameter is null)
        {
            throw new InvalidDataException(
                $"Элемент parameters[{parameterIndex}] обработчика {handlerId} не может быть null.");
        }

        var key = (parameter.Key ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(key, CatalogRevisionEnforcement.RevisionParameterKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Параметр {handlerId}/{key} зарезервирован протоколом и не может объявляться расширением.");
        }
        var displayName = (parameter.DisplayName ?? string.Empty).Trim();
        if (!Validation.ParameterKey().IsMatch(key) || key.Length > 64 ||
            displayName.Length is 0 or > 128 || displayName.Any(char.IsControl))
        {
            throw new InvalidDataException($"У обработчика {handlerId} недопустимый параметр {key}.");
        }
        if (!Enum.IsDefined(parameter.Kind) ||
            parameter.CatalogKind is { } catalogKind && !Enum.IsDefined(catalogKind))
        {
            throw new InvalidDataException($"У параметра {handlerId}/{key} неизвестный kind/catalogKind.");
        }
        if (parameter.Options is null)
        {
            throw new InvalidDataException($"У параметра {handlerId}/{key} options не может быть null.");
        }
        var options = parameter.Options
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var catalogCategories = (parameter.CatalogCategories ?? [])
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (options.Count > 500 || options.Any(value => value.Length > 256 || value.Any(char.IsControl)))
        {
            throw new InvalidDataException($"Параметр {handlerId}/{key} содержит недопустимые options.");
        }
        if (catalogCategories.Count > 32 || catalogCategories.Any(value =>
                value.Length > 256 || value.Any(char.IsControl)))
        {
            throw new InvalidDataException(
                $"Параметр {handlerId}/{key} содержит недопустимые catalogCategories.");
        }
        if (parameter.CatalogKind is null && catalogCategories.Count > 0)
        {
            throw new InvalidDataException(
                $"Параметр {handlerId}/{key} задаёт catalogCategories без catalogKind.");
        }
        if (parameter.CatalogKind is not null &&
            parameter.Kind is not (ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice))
        {
            throw new InvalidDataException($"Параметр {handlerId}/{key} несовместим с catalogKind.");
        }
        if (parameter.CatalogKind is null &&
            parameter.Kind is ParameterKind.Choice or ParameterKind.ItemChoice or ParameterKind.MultiChoice &&
            options.Count == 0)
        {
            throw new InvalidDataException($"Параметру {handlerId}/{key} требуется непустой options.");
        }
        var definition = new ParameterDefinition
        {
            Key = key,
            DisplayName = displayName,
            Kind = parameter.Kind,
            CatalogKind = parameter.CatalogKind,
            CatalogCategories = catalogCategories,
            Minimum = parameter.Minimum,
            Maximum = parameter.Maximum,
            Options = parameter.CatalogKind is null ? options : [],
            DefaultValue = parameter.DefaultValue ?? string.Empty
        };
        if (parameter.CatalogKind is null && definition.Validate(definition.DefaultValue) is { } error)
        {
            throw new InvalidDataException($"Параметр {handlerId}/{key}: {error}");
        }
        return definition;
    }

    private static string NormalizeScriptPath(string? path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (!ScriptPathFormat.IsMatch(normalized) || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"Недопустимый scriptRelativePath {path}; разрешены только namespaced .script внутри gamedata/scripts.");
        }
        return normalized;
    }
}

public sealed class ExtensionManifest
{
    public int SchemaVersion { get; set; }
    public string? ProtocolVersion { get; set; } = string.Empty;
    public string? ExtensionId { get; set; } = string.Empty;
    public string? DisplayName { get; set; } = string.Empty;
    public List<ExtensionActionManifest?>? Actions { get; set; } = [];
}

public sealed class ExtensionActionManifest
{
    public string? HandlerId { get; set; } = string.Empty;
    public string? DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; } = string.Empty;
    public string? ScriptRelativePath { get; set; } = string.Empty;
    public int CooldownSeconds { get; set; } = 5;
    public List<ExtensionParameterManifest?>? Parameters { get; set; } = [];
}

public sealed class ExtensionParameterManifest
{
    public string? Key { get; set; } = string.Empty;
    public string? DisplayName { get; set; } = string.Empty;
    public ParameterKind Kind { get; set; }
    public CatalogEntryKind? CatalogKind { get; set; }
    public List<string?>? CatalogCategories { get; set; } = [];
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string?>? Options { get; set; } = [];
    public string? DefaultValue { get; set; } = string.Empty;
}
