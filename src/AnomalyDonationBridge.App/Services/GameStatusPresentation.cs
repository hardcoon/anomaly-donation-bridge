using AnomalyDonationBridge.GameIpc;

namespace AnomalyDonationBridge.App.Services;

internal static class GameStatusPresentation
{
    public static string Connection(GameConnectionSnapshot snapshot)
    {
        var reason = DescribeReason(snapshot.Reason);
        if (!snapshot.Connected)
        {
            return reason;
        }

        if (!snapshot.Ready)
        {
            return $"Подключена · {reason}";
        }

        return string.Equals(snapshot.Reason, "ok", StringComparison.Ordinal)
            ? $"Готова · addon {snapshot.AddonVersion}"
            : $"Готова · addon {snapshot.AddonVersion} · {reason}";
    }

    public static string? CatalogFailure(string reason)
    {
        if (reason is not (
            "catalog_unavailable" or
            "catalog_api_unavailable" or
            "section_enumeration_unavailable" or
            "section_enumeration_failed" or
            "catalog_snapshot_failed" or
            "catalog_entry_limit_exceeded"))
        {
            return null;
        }

        return $"{DescribeReason(reason)} · код {reason}";
    }

    public static string DescribeReason(string reason) => reason switch
    {
        "ok" => "готово",
        "catalog_unavailable" => "каталог игры недоступен",
        "catalog_api_unavailable" => "движок не предоставляет необходимый API каталога",
        "section_enumeration_unavailable" => "движок не умеет перечислять секции игры",
        "section_enumeration_failed" => "ошибка обхода секций игры",
        "catalog_snapshot_failed" => "внутренняя ошибка построения каталога игры",
        "catalog_entry_limit_exceeded" => "в каталоге больше допустимых 100 000 секций",
        "game_loading" => "игра загружается",
        "level_unavailable" => "уровень ещё не загружен",
        "actor_unavailable" => "персонаж ещё не появился",
        "actor_dead" => "персонаж погиб",
        "actor_in_vehicle" => "персонаж находится в транспорте",
        "actor_in_btr" => "персонаж едет в БТР-напарнике",
        "actor_in_dialog" => "идёт диалог",
        "actor_sleeping" => "персонаж спит",
        "actor_in_cutscene" => "идёт кат-сцена",
        "effect_in_progress" => "выполняется интерактив — события ждут в очереди",
        "effect_state_unavailable" => "состояние интерактива недоступно — события ждут в очереди",
        "game_menu_open" => "открыто игровое меню",
        "game_paused" => "игра на паузе",
        "addon_update_required" => "требуется обновить игровой аддон",
        "safe_zone_config_unavailable" => "безопасная зона: файл правил недоступен — эффект ожидает",
        "safe_zone_config_invalid" => "безопасная зона: ошибка файла правил — эффект ожидает",
        "safe_zone_config_stale" => "безопасная зона: ожидание актуальных правил",
        "safe_zone_level_unavailable" => "безопасная зона: текущая локация ещё неизвестна",
        _ when reason.StartsWith("safe_zone_waiting:", StringComparison.Ordinal) =>
            "безопасная зона: ожидание выхода с локации " + reason["safe_zone_waiting:".Length..],
        "game_suspended" => "игровой цикл не отвечает — события ждут в очереди",
        "level_changing" => "смена уровня",
        _ when string.IsNullOrWhiteSpace(reason) => "состояние игры неизвестно",
        _ => reason
    };
}
