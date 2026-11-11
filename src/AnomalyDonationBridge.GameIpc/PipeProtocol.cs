using System.Globalization;
using System.Text;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.GameIpc;

public abstract record InboundMessage;
public sealed record HelloMessage(string AddonVersion, int GameProcessId) : InboundMessage;
public sealed record StatusMessage(bool Ready, string Reason) : InboundMessage;
public sealed record ResultMessage(GameExecutionResult Result) : InboundMessage;
public sealed record CapabilitiesMessage(CapabilitySet Capabilities) : InboundMessage;
public sealed record CatalogBeginMessage(CatalogRevision Revision, int ExpectedCount, string ContentHash,
    string MonsterCommunities = "") : InboundMessage;
public sealed record CatalogEntryMessage(CatalogRevision Revision, int Index, CatalogEntry Entry) : InboundMessage;
public sealed record CatalogEndMessage(CatalogRevision Revision, int ActualCount, string ContentHash) : InboundMessage;
public sealed record CatalogInfoMessage(CatalogRevision Revision, int EntryCount, string ContentHash) : InboundMessage;
public sealed record PongMessage(string ProbeId) : InboundMessage;

public static class PipeProtocol
{
    public const string PipeName = "AnomalyDonationBridge.v1";
    public const string ProtocolVersion = "1";
    public const string CatalogSchemaVersion = "1";
    public const int MaximumLineBytes = 16 * 1024;
    public const int MaximumCatalogEntries = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Welcome(string applicationVersion) =>
        CheckedLine($"WELCOME\t{ProtocolVersion}\t{PercentEncode(applicationVersion)}");

    public static string Ping() => "PING";
    public static string Ping(string probeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(probeId);
        return CheckedLine($"PING\t{PercentEncode(probeId)}");
    }
    public static string GetCatalog() => "GET_CATALOG";
    public static string GetCatalogInfo() => "GET_CATALOG_INFO";

    public static string Command(GameCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parameters = new Dictionary<string, string>(command.Parameters, StringComparer.OrdinalIgnoreCase);
        var builtInDynamicHandler = CatalogRevisionEnforcement.RequiresCatalog(command.ActionId);
        if (builtInDynamicHandler && command.CatalogRevision is null)
        {
            throw new InvalidDataException("Динамическая команда не содержит ревизию каталога.");
        }

        if (command.CatalogRevision is { } revision)
        {
            if (revision.IsEmpty)
            {
                throw new InvalidDataException("Динамическая команда не содержит ревизию каталога.");
            }

            if (parameters.TryGetValue(CatalogRevisionEnforcement.RevisionParameterKey, out var supplied) &&
                !string.Equals(supplied, revision.Value, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Параметр catalog_revision конфликтует с моделью команды.");
            }

            parameters[CatalogRevisionEnforcement.RevisionParameterKey] = revision.Value;
        }
        else if (parameters.ContainsKey(CatalogRevisionEnforcement.RevisionParameterKey))
        {
            throw new InvalidDataException("catalog_revision является зарезервированным параметром протокола.");
        }

        var structuredParameters = string.Join(";", parameters
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{PercentEncode(ValidateParameterKey(pair.Key))}={PercentEncode(pair.Value)}"));

        var fields = new List<string>
        {
            "COMMAND",
            PercentEncode(command.CommandId),
            PercentEncode(command.DonationId),
            PercentEncode(command.ActionId),
            command.AmountMinor.ToString(CultureInfo.InvariantCulture),
            PercentEncode(Money.NormalizeCurrency(command.Currency)),
            PercentEncode(structuredParameters)
        };

        var hasExecutionContext = !string.IsNullOrWhiteSpace(command.DonorName) ||
            !string.IsNullOrWhiteSpace(command.ActionName) ||
            command.BatchIndex != 1 || command.BatchSize != 1 ||
            command.SaveBeforeBatch || command.SaveAfterBatch ||
            !command.ShowGameEffectLog || command.ShowPdaEffectMessage || command.RequireSafeZoneCheck;
        if (hasExecutionContext)
        {
            if (command.EffectSaveRetentionCount is < 0 or > 10_000)
                throw new InvalidDataException("Лимит сохранений ADB должен быть от 0 до 10 000.");
            if (command.BatchSize is < 1 or > 10_000 ||
                command.BatchIndex < 1 || command.BatchIndex > command.BatchSize)
            {
                throw new InvalidDataException("Команда содержит недопустимые границы цепочки эффектов.");
            }
            fields.Add(PercentEncode(ValidateDisplayText(command.DonorName, 384, "имя донатчика")));
            fields.Add(PercentEncode(ValidateDisplayText(command.ActionName, 640, "название шаблона")));
            fields.Add(command.BatchIndex.ToString(CultureInfo.InvariantCulture));
            fields.Add(command.BatchSize.ToString(CultureInfo.InvariantCulture));
            fields.Add(((command.SaveBeforeBatch ? 1 : 0) |
                (command.SaveAfterBatch ? 2 : 0) |
                (command.ShowGameEffectLog ? 0 : 4) |
                (command.ShowPdaEffectMessage ? 8 : 0)).ToString(CultureInfo.InvariantCulture));
            if (command.SaveBeforeBatch || command.SaveAfterBatch || command.RequireSafeZoneCheck)
                fields.Add(command.EffectSaveRetentionCount.ToString(CultureInfo.InvariantCulture));
            // Older addons reject this optional shape rather than execute without protection.
            if (command.RequireSafeZoneCheck)
            {
                if (command.SafeZoneRevision.Length != 64 || command.SafeZoneRevision.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new InvalidDataException("Безопасная зона: отсутствует ревизия правил.");
                fields.Add(command.SafeZoneRevision.ToLowerInvariant());
                if (!string.IsNullOrEmpty(command.SourceHandlerId))
                {
                    if (command.SourceHandlerId.Length > 128 || !Validation.ActionId().IsMatch(command.SourceHandlerId))
                        throw new InvalidDataException("Безопасная зона: недопустимый исходный шаблон.");
                    fields.Add(PercentEncode(command.SourceHandlerId));
                }
            }
        }

        return CheckedLine(string.Join('\t', fields));
    }

    private static string ValidateDisplayText(string? value, int maximumUtf8Bytes, string fieldName)
    {
        value ??= string.Empty;
        if (value.Any(character => char.IsControl(character)))
        {
            throw new InvalidDataException($"Поле «{fieldName}» содержит управляющие символы.");
        }
        if (StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw new InvalidDataException($"Поле «{fieldName}» слишком длинное.");
        }
        return value;
    }

    public static InboundMessage ParseInbound(string line)
    {
        ValidateLineLength(line);
        var fields = line.Split('\t');
        if (fields.Length == 0 || string.IsNullOrEmpty(fields[0]))
        {
            throw new InvalidDataException("Получена пустая команда протокола.");
        }

        return fields[0] switch
        {
            "HELLO" => ParseHello(fields),
            "STATUS" => ParseStatus(fields),
            "RESULT" => ParseResult(fields),
            "VOTING_STATUS" => VotingGameWire.ParseReply(fields),
            "CAPABILITIES" => ParseCapabilities(fields),
            "CATALOG_BEGIN" => ParseCatalogBegin(fields),
            "CATALOG_ENTRY" => ParseCatalogEntry(fields),
            "CATALOG_END" => ParseCatalogEnd(fields),
            "CATALOG_INFO" => ParseCatalogInfo(fields),
            "PONG" when fields.Length is 1 or 2 =>
                new PongMessage(fields.Length == 2 ? PercentDecode(fields[1]) : string.Empty),
            _ => throw new InvalidDataException($"Неизвестная или повреждённая команда протокола: {fields[0]}")
        };
    }

    public static IReadOnlyDictionary<string, string> ParseStructuredParameters(string outerDecodedValue)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(outerDecodedValue))
        {
            return result;
        }

        foreach (var pair in outerDecodedValue.Split(';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidDataException("Повреждённое поле параметров команды.");
            }

            var key = ValidateParameterKey(PercentDecode(pair[..separator]));
            if (!result.TryAdd(key, PercentDecode(pair[(separator + 1)..])))
            {
                throw new InvalidDataException($"Параметр {key} передан повторно.");
            }
        }

        return result;
    }

    public static string PercentEncode(string? value)
    {
        var bytes = StrictUtf8.GetBytes(value ?? string.Empty);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            if ((valueByte >= (byte)'a' && valueByte <= (byte)'z') ||
                (valueByte >= (byte)'A' && valueByte <= (byte)'Z') ||
                (valueByte >= (byte)'0' && valueByte <= (byte)'9') ||
                valueByte is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                builder.Append((char)valueByte);
            }
            else
            {
                builder.Append('%');
                builder.Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    public static string PercentDecode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length ||
                    !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var decoded))
                {
                    throw new InvalidDataException("Повреждённая percent-последовательность.");
                }

                stream.WriteByte(decoded);
                index += 3;
                continue;
            }

            if (character > 0x7F)
            {
                throw new InvalidDataException("Неэкранированный не-ASCII символ в протоколе.");
            }

            stream.WriteByte((byte)character);
            index++;
        }

        try
        {
            return StrictUtf8.GetString(stream.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Повреждённая UTF-8 последовательность.", exception);
        }
    }

    public static string EngineProfileToken(EngineProfile profile) => profile switch
    {
        EngineProfile.Anomaly153Vanilla => "anomaly_1_5_3_vanilla",
        EngineProfile.XRayMonolithStandard => "xray_monolith_standard",
        EngineProfile.XRayMonolithMtTest => "xray_monolith_mt_test",
        _ => "unknown"
    };

    public static EngineProfile ParseEngineProfile(string token) => token switch
    {
        "anomaly_1_5_3_vanilla" => EngineProfile.Anomaly153Vanilla,
        "xray_monolith_standard" => EngineProfile.XRayMonolithStandard,
        "xray_monolith_mt_test" => EngineProfile.XRayMonolithMtTest,
        "unknown" => EngineProfile.Unknown,
        _ => throw new InvalidDataException($"Неизвестный профиль движка: {token}")
    };

    private static HelloMessage ParseHello(string[] fields)
    {
        if (fields.Length != 4 || fields[1] != ProtocolVersion ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            throw new InvalidDataException("Некорректное сообщение HELLO или несовместимая версия протокола.");
        }

        return new HelloMessage(PercentDecode(fields[2]), processId);
    }

    private static StatusMessage ParseStatus(string[] fields)
    {
        if (fields.Length != 3 || fields[1] is not ("ready" or "waiting"))
        {
            throw new InvalidDataException("Некорректное сообщение STATUS.");
        }

        return new StatusMessage(fields[1] == "ready", PercentDecode(fields[2]));
    }

    private static ResultMessage ParseResult(string[] fields)
    {
        if (fields.Length != 4)
        {
            throw new InvalidDataException("Некорректное сообщение RESULT.");
        }

        var status = fields[2] switch
        {
            "executed" => GameResultStatus.Executed,
            "deferred" => GameResultStatus.Deferred,
            "rejected" => GameResultStatus.Rejected,
            "uncertain" => GameResultStatus.Uncertain,
            _ => throw new InvalidDataException("Неизвестный статус RESULT.")
        };
        return new ResultMessage(new GameExecutionResult
        {
            CommandId = PercentDecode(fields[1]),
            Status = status,
            Reason = PercentDecode(fields[3]),
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private static CapabilitiesMessage ParseCapabilities(string[] fields)
    {
        if (fields.Length != 5)
        {
            throw new InvalidDataException("Некорректное сообщение CAPABILITIES.");
        }

        try
        {
            return new CapabilitiesMessage(CapabilitySet.Create(
                ParseEngineProfile(PercentDecode(fields[1])),
                PercentDecode(fields[2]),
                ParseIdList(fields[3]),
                ParseIdList(fields[4])));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Некорректное сообщение CAPABILITIES.", exception);
        }
    }

    private static CatalogBeginMessage ParseCatalogBegin(string[] fields)
    {
        var decodedHash = fields.Length is 4 or 5 ? PercentDecode(fields[3]) : string.Empty;
        if (fields.Length is not (4 or 5) || !CatalogRevision.TryParse(PercentDecode(fields[1]), out var revision) ||
            !TryParseCatalogCount(fields[2], out var count) || !CatalogSnapshot.IsValidContentHash(decodedHash))
        {
            throw new InvalidDataException("Некорректное сообщение CATALOG_BEGIN.");
        }

        var communities = fields.Length == 5 ? PercentDecode(fields[4]) : string.Empty;
        _ = MonsterCommunityCatalog.Parse(communities);
        return new CatalogBeginMessage(revision, count, decodedHash, communities);
    }

    private static CatalogEntryMessage ParseCatalogEntry(string[] fields)
    {
        if (fields.Length is not (11 or 12) || !CatalogRevision.TryParse(PercentDecode(fields[1]), out var revision) ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 ||
            fields[9] is not ("0" or "1"))
        {
            throw new InvalidDataException("Некорректное сообщение CATALOG_ENTRY.");
        }

        var entry = new CatalogEntry
        {
            SectionId = PercentDecode(fields[3]),
            Kind = ParseCatalogKind(PercentDecode(fields[4])),
            DisplayName = PercentDecode(fields[5]),
            Category = PercentDecode(fields[6]),
            Strength = ParseCatalogStrength(PercentDecode(fields[7])),
            Faction = PercentDecode(fields[8]),
            IsAvailable = fields[9] == "1",
            QuarantineReason = ParseQuarantineReason(PercentDecode(fields[10])),
            NativeClass = fields.Length == 12 ? PercentDecode(fields[11]) : string.Empty
        };
        entry.Validate();
        return new CatalogEntryMessage(revision, index, entry);
    }

    private static CatalogEndMessage ParseCatalogEnd(string[] fields)
    {
        var decodedHash = fields.Length == 4 ? PercentDecode(fields[3]) : string.Empty;
        if (fields.Length != 4 || !CatalogRevision.TryParse(PercentDecode(fields[1]), out var revision) ||
            !TryParseCatalogCount(fields[2], out var count) || !CatalogSnapshot.IsValidContentHash(decodedHash))
        {
            throw new InvalidDataException("Некорректное сообщение CATALOG_END.");
        }

        return new CatalogEndMessage(revision, count, decodedHash);
    }

    private static CatalogInfoMessage ParseCatalogInfo(string[] fields)
    {
        var decodedHash = fields.Length == 4 ? PercentDecode(fields[3]) : string.Empty;
        if (fields.Length != 4 || !CatalogRevision.TryParse(PercentDecode(fields[1]), out var revision) ||
            !TryParseCatalogCount(fields[2], out var count) || !CatalogSnapshot.IsValidContentHash(decodedHash))
        {
            throw new InvalidDataException("Некорректное сообщение CATALOG_INFO.");
        }

        return new CatalogInfoMessage(revision, count, decodedHash);
    }

    private static IReadOnlyList<string> ParseIdList(string encoded)
    {
        var decoded = PercentDecode(encoded);
        return string.IsNullOrEmpty(decoded)
            ? []
            : decoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TryParseCatalogCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) &&
        count is >= 0 and <= MaximumCatalogEntries;

    private static CatalogEntryKind ParseCatalogKind(string token) => token switch
    {
        "mutant" => CatalogEntryKind.Mutant,
        "npc" => CatalogEntryKind.Npc,
        "stalker_squad" => CatalogEntryKind.StalkerSquad,
        "mutant_squad" => CatalogEntryKind.MutantSquad,
        "item" => CatalogEntryKind.Item,
        "anomaly" => CatalogEntryKind.Anomaly,
        "faction" => CatalogEntryKind.Faction,
        "level" => CatalogEntryKind.Level,
        "helicopter" => CatalogEntryKind.Helicopter,
        "armed_vehicle" => CatalogEntryKind.ArmedVehicle,
        "npc_visual" => CatalogEntryKind.NpcVisual,
        "guitar_music" => CatalogEntryKind.GuitarMusic,
        _ => throw new InvalidDataException($"Неизвестный тип записи каталога: {token}")
    };

    private static CatalogStrength ParseCatalogStrength(string token) => token switch
    {
        "unclassified" => CatalogStrength.Unclassified,
        "lite" => CatalogStrength.Lite,
        "normal" => CatalogStrength.Normal,
        "strong" => CatalogStrength.Strong,
        _ => throw new InvalidDataException($"Неизвестная сила записи каталога: {token}")
    };

    private static QuarantineReason ParseQuarantineReason(string token) => token switch
    {
        "none" => QuarantineReason.None,
        "story_unique" => QuarantineReason.StoryOrUnique,
        "non_tradeable" => QuarantineReason.NonTradeable,
        "scripted" => QuarantineReason.Scripted,
        "invalid_structure" => QuarantineReason.InvalidStructure,
        "blacklisted" => QuarantineReason.Blacklisted,
        "missing_required_fields" => QuarantineReason.MissingRequiredFields,
        "unsupported_class" => QuarantineReason.UnsupportedClass,
        _ => throw new InvalidDataException($"Неизвестная причина карантина: {token}")
    };

    private static string CheckedLine(string line)
    {
        ValidateLineLength(line);
        return line;
    }

    private static void ValidateLineLength(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (StrictUtf8.GetByteCount(line) + 1 > MaximumLineBytes)
        {
            throw new InvalidDataException("Строка протокола превышает 16 КиБ.");
        }
    }

    private static string ValidateParameterKey(string key)
    {
        if (!Validation.ParameterKey().IsMatch(key))
        {
            throw new InvalidDataException($"Недопустимый ключ параметра: {key}");
        }

        return key;
    }
}
