namespace AnomalyDonationBridge.App.Services;

public sealed record TemplateSourceLink(string Label, string RelativePath)
{
    public string FileName => System.IO.Path.GetFileName(RelativePath);
}

public static class TemplateSourceLinks
{
    public static IReadOnlyList<TemplateSourceLink> For(string handlerId) => handlerId == "hardcoon.adb.btr_companion"
        ? Btr : Array.Empty<TemplateSourceLink>();

    private static readonly TemplateSourceLink[] Btr =
    [
        new("ИИ — логика:", "gamedata/scripts/anomaly_donation_bridge_btr_ai.script"),
        new("Точка спавна и маршрут:", "gamedata/scripts/anomaly_donation_bridge_vehicle_navigation.script"),
        new("Логика управления:", "gamedata/scripts/anomaly_donation_bridge_vehicle_controls.script")
    ];
}
