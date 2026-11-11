using System.Text.Json.Serialization;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.DonateX;

public static class DonateXEndpoints
{
    public const string ApiBase = "https://donatex.gg/api";
    public const string Profile = ApiBase + "/v1/user/me";
    public const string Donations = ApiBase + "/v1/donations";
    public const string DonationsHub = ApiBase + "/public-donations-hub";
    public const string ApiDocumentation = "https://donatex.gg/api-docs";
    public const string Cabinet = "https://donatex.gg/streamer/settings";
}

public static class DonateXToken
{
    public static string Normalize(string? value)
    {
        var token = string.Concat((value ?? string.Empty).Where(character =>
            character != (char)0x200B && character != (char)0x200C &&
            character != (char)0x200D && character != (char)0x2060 &&
            character != (char)0xFEFF)).Trim();
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') ||
             (token[0] == '\'' && token[^1] == '\'')))
            token = token[1..^1].Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        if (token.Length >= 2 &&
            ((token[0] == '"' && token[^1] == '"') ||
             (token[0] == '\'' && token[^1] == '\'')))
            token = token[1..^1].Trim();

        // Clipboard text copied from styled web controls can contain invisible
        // Unicode separators. They are not part of a DonateX token and make an
        // otherwise valid key fail with 401.
        return string.Concat(token.Where(character => !char.IsWhiteSpace(character)));
    }
}

public sealed class DonateXProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

public sealed class DonateXDonation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "RUB";

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("isTest")]
    public bool IsTest { get; set; }

    public DonationEvent ToDonationEvent()
    {
        var id = (Id ?? string.Empty).Trim();
        if (id.Length == 0) throw new InvalidDataException("DonateX прислал донат без ID.");
        var currency = Money.NormalizeCurrency(Currency);
        return new DonationEvent
        {
            Id = "donatex:" + id,
            // Leave an absent name empty so the shared dispatcher can apply
            // the user's configured anonymous-donor label consistently.
            Username = Username?.Trim() ?? string.Empty,
            Message = Message ?? string.Empty,
            Amount = Amount,
            Currency = currency,
            CreatedAt = Timestamp == default ? DateTimeOffset.UtcNow : Timestamp,
            ReceivedAt = DateTimeOffset.UtcNow
        };
    }
}

public sealed record DonateXConnectionState(bool Connected, string Message);
