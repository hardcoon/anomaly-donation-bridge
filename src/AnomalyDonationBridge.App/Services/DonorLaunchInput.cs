namespace AnomalyDonationBridge.App.Services;

public sealed record DonorLaunchInput(string DonorName, string Message)
{
    public const int NameMaxLength = 64;
    public const int MessageMaxLength = 4096;

    public static DonorLaunchInput Create(string? donorName, string? message)
    {
        var name = donorName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > NameMaxLength || name.Any(char.IsControl))
            throw new ArgumentException($"Введите ник донатера от 1 до {NameMaxLength} символов без переносов строки.");
        message ??= string.Empty;
        if (message.Length > MessageMaxLength)
            throw new ArgumentException($"Текст описания не должен превышать {MessageMaxLength} символов.");
        return new(name, message);
    }
}
