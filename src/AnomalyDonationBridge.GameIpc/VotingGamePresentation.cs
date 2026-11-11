using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.GameIpc;

public sealed record VotingGameOption(string EndFraction, string Title);
public sealed record VotingGameReply(string RoundId, string State, string Reason) : InboundMessage;

public sealed record VotingGamePresentation(string RoundId, string Title, bool AutomaticEffect,
    int WinnerIndex, string WinnerFraction, IReadOnlyList<VotingGameOption> Options)
{
    public static VotingGamePresentation Create(VotingRound source)
    {
        var round = source.ResultSnapshot();
        var options = round.Options.Where(option => option.Enabled && option.Amount > 0 && option.Preset is not null).ToArray();
        var total = options.Sum(option => option.Amount);
        var winner = Array.FindIndex(options, option => option.Id == round.WinnerOptionId);
        if (round.Phase != VotingPhase.Drawn || total <= 0 || options.Length is < 1 or > 200 || winner < 0)
            throw new InvalidDataException("Нет корректного сохранённого результата голосования.");
        decimal cumulative = 0;
        return new VotingGamePresentation(round.Id, VotingGameWire.Text(round.Title), round.GamePresentationAutomaticEffect,
            winner, round.WinnerFraction.ToString("0.############################", CultureInfo.InvariantCulture), options.Select(option =>
            {
                cumulative += option.Amount;
                return new VotingGameOption((cumulative / total).ToString("0.############################", CultureInfo.InvariantCulture),
                    VotingGameWire.Text(option.Preset!.DisplayName));
            }).ToArray());
    }
}

public static class VotingGameWire
{
    public const string Capability = "voting_game_ui_v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static string Text(string value)
    {
        // Captions are inert, bounded display text, never script/commands.
        var clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in clean.EnumerateRunes())
        { if (bytes + rune.Utf8SequenceLength > 640) break; result.Append(rune); bytes += rune.Utf8SequenceLength; }
        return result.Length > 0 ? result.ToString() : "Голосование";
    }
    public static string Id(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("Некорректный ID игровой рулетки.");
    public static string Line(params string[] fields)
    {
        var line = string.Join('\t', fields.Select(PipeProtocol.PercentEncode));
        if (Utf8.GetByteCount(line) + 1 > PipeProtocol.MaximumLineBytes) throw new InvalidDataException("Слишком длинная строка игровой рулетки.");
        return line;
    }
    public static string Canonical(VotingGamePresentation request)
    {
        Id(request.RoundId);
        if (request.Options.Count is < 1 or > 200 || request.WinnerIndex < 0 || request.WinnerIndex >= request.Options.Count ||
            request.Title != Text(request.Title) || !Fraction(request.WinnerFraction, out var winning))
            throw new InvalidDataException("Некорректный снимок игровой рулетки.");
        var value = new StringBuilder().Append(request.Title).Append('\n').Append(request.AutomaticEffect ? '1' : '0').Append('\n')
            .Append(request.WinnerIndex.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(request.WinnerFraction).Append('\n')
            .Append(request.Options.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        decimal previous = 0;
        for (var index = 0; index < request.Options.Count; index++)
        {
            var option = request.Options[index];
            if (option.Title != Text(option.Title) || !Fraction(option.EndFraction, out var end) || end <= previous ||
                index == request.WinnerIndex && (winning < previous || winning > end))
                throw new InvalidDataException("Повреждены доли игровой рулетки.");
            value.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(option.EndFraction).Append('\t').Append(option.Title).Append('\n');
            previous = end;
        }
        if (previous != 1) throw new InvalidDataException("Доли игровой рулетки не составляют 100%.");
        return value.ToString();
    }
    private static bool Fraction(string value, out decimal number) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out number) && number is >= 0 and <= 1 && value.Length <= 32;
    public static IReadOnlyList<string> Frames(VotingGamePresentation request)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(Canonical(request)))).ToLowerInvariant();
        var lines = new List<string> { Line("VOTING_BEGIN", request.RoundId, request.Title, request.AutomaticEffect ? "1" : "0",
            request.WinnerIndex.ToString(CultureInfo.InvariantCulture), request.WinnerFraction, request.Options.Count.ToString(CultureInfo.InvariantCulture), hash) };
        for (var index = 0; index < request.Options.Count; index++)
            lines.Add(Line("VOTING_OPTION", request.RoundId, index.ToString(CultureInfo.InvariantCulture), request.Options[index].EndFraction, request.Options[index].Title));
        lines.Add(Line("VOTING_END", request.RoundId, hash));
        return lines;
    }
    public static VotingGameReply ParseReply(string[] fields)
    {
        if (fields.Length != 4) throw new InvalidDataException("Некорректный статус игровой рулетки.");
        var id = Id(PipeProtocol.PercentDecode(fields[1])); var state = PipeProtocol.PercentDecode(fields[2]);
        var reason = PipeProtocol.PercentDecode(fields[3]);
        if (state is not ("unknown" or "active" or "done" or "busy" or "rejected") || reason.Length > 160 || reason.Any(char.IsControl))
            throw new InvalidDataException("Некорректный статус игровой рулетки.");
        return new(id, state, reason);
    }
}

public sealed partial class NamedPipeGameServer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<VotingGameReply>> _votingReplies = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _votingExchangeGate = new(1, 1);
    public async Task<VotingGameReply> ExchangeVotingAsync(VotingGamePresentation? request, string roundId,
        bool cancel = false, CancellationToken token = default)
    {
        VotingGameWire.Id(roundId);
        if (request is not null && request.RoundId != roundId) throw new InvalidDataException("ID снимка не совпадает с запросом.");
        // Preflight the complete snapshot before any BEGIN byte, including 200-option cases.
        var frames = request is not null ? VotingGameWire.Frames(request) :
            new[] { VotingGameWire.Line(cancel ? "VOTING_CANCEL" : "VOTING_QUERY", roundId) };
        await _votingExchangeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            NamedPipeServerStream pipe;
            lock (_stateGate)
            {
                if (!_capabilities.SupportsCapability(VotingGameWire.Capability))
                    throw new InvalidOperationException("Для игровой рулетки обновите аддон и перезапустите игру.");
                pipe = _activePipe is { IsConnected: true } connected ? connected :
                    throw new GameNotReadyBeforeSendException("Игра не подключена.");
            }
            var reply = new TaskCompletionSource<VotingGameReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_votingReplies.TryAdd(roundId, reply)) throw new InvalidOperationException("Рулетка уже ожидает ответ.");
            try
            {
                foreach (var line in frames) await WriteLineAsync(pipe, line, token).ConfigureAwait(false);
                return await reply.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
            }
            finally { _votingReplies.TryRemove(roundId, out _); }
        }
        finally { _votingExchangeGate.Release(); }
    }
}
