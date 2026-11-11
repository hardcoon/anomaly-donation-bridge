using System.Globalization;
using System.IO.Pipes;
using System.Text;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.ProtocolTests;

internal static partial class Program
{
    private static Task TestVotingGameWireAsync()
    {
        var request = new VotingGamePresentation(Guid.NewGuid().ToString("N"), "Голосование", false, 1, "0.9",
            [new("0.8", "Отряд Долга"), new("1", "Отряд Свободы")]);
        var frames = VotingGameWire.Frames(request);
        Equal(4, frames.Count, "BEGIN, two options, END");
        var begin = frames[0].Split('\t').Select(PipeProtocol.PercentDecode).ToArray();
        Equal("1", begin[4], "zero-based frozen winner index"); Equal("0.9", begin[5], "pointer destination");
        Equal(64, begin[7].Length, "snapshot hash");
        Equal("done", IsType<VotingGameReply>(PipeProtocol.ParseInbound("VOTING_STATUS\t"+request.RoundId+"\tdone\tclosed")).State, "reply decoding");
        Throws<InvalidDataException>(() => VotingGameWire.Frames(request with { WinnerIndex = 4 }), "invalid winner rejected before send");
        Throws<InvalidDataException>(() => VotingGameWire.Frames(request with { Options = [new("0.9", "a"), new("0.8", "b")] }), "inconsistent shares rejected");
        Throws<InvalidDataException>(() => PipeProtocol.ParseInbound("VOTING_STATUS\t"+request.RoundId+"\texecuted\tbad"), "unknown presentation state rejected");
        var longTitle = VotingGameWire.Text(string.Concat(Enumerable.Repeat("Длинное название 🙂", 100)));
        var many = request with { WinnerIndex = 199, WinnerFraction = "0.999", Options = Enumerable.Range(1, 200)
            .Select(i => new VotingGameOption((i/200m).ToString("0.############################", CultureInfo.InvariantCulture), longTitle)).ToArray() };
        var large = VotingGameWire.Frames(many);
        Equal(202, large.Count, "large voting snapshot is streamed, not squeezed into one 16 KiB command");
        True(large.All(line => Encoding.UTF8.GetByteCount(line)+1 <= PipeProtocol.MaximumLineBytes), "all voting frames respect wire bound");
        True(Encoding.UTF8.GetByteCount(longTitle)<=640 && !longTitle.Any(char.IsControl), "captions are safe and UTF-8 bounded");
        var small = request with { WinnerIndex = 0, WinnerFraction = "0.000000000000000001", Options = [new("0.000000000000000002", "tiny"),new("1", "large")] };
        Equal(4, VotingGameWire.Frames(small).Count, "very small positive shares are not rounded to zero by the program");
        var hudRound = new VotingRound { IsActive = true, Phase = VotingPhase.Collecting, Target = 1000,
            Options = [new() { Command = "!долг", Amount = 300, Preset = new() { DisplayName = "Отряд Долга" } },
                new() { Command = "!свобода", Amount = 100, Preset = new() { DisplayName = "Отряд Свободы" } }] };
        var hudSettings = new VotingAutomation();
        var hudFields = VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow).Split('\t').Select(PipeProtocol.PercentDecode).ToArray();
        Equal("xy,0.02,0.35", hudFields[2], "default HUD normalised desktop coordinates");
        True(hudFields[3].Contains("40%") && hudFields[3].Contains("!долг  75%"), "game HUD carries a character bar and per-command proportions");
        True(!hudFields[3].Contains("Отряд") && !hudFields[3].Contains("\n\n"), "Existing HUD stays compact without template names");
        hudSettings.ShowGameHudTemplateNames = true;
        var namedLines = VotingGameHud.ContentLines(hudRound, hudSettings, DateTimeOffset.UtcNow);
        True(namedLines.TakeLast(5).SequenceEqual(new[] { "!долг  75%", "Отряд Долга", "", "!свобода  25%", "Отряд Свободы" }),
            "Template names must sit below their command/value, with spacing only between options");
        Equal(128, namedLines.Sum(VotingGameHud.RowHeight), "Desktop preview height includes a small 8px gap, not a full text row");
        hudSettings.ShowGameHudCommands = false;
        True(VotingGameHud.ContentLines(hudRound, hudSettings, DateTimeOffset.UtcNow).Count == 2,
            "Hiding commands must also hide template names and their gaps");
        hudSettings.ShowGameHudCommands = true; hudSettings.ShowGameHudTemplateNames = false;
        hudRound.TimeGoal = true; hudRound.Deadline = DateTimeOffset.UtcNow.AddMinutes(9);
        foreach (var phase in new[] { VotingPhase.Ready, VotingPhase.Failed, VotingPhase.Drawn })
        {
            hudRound.Phase = phase;
            var lines = VotingGameHud.ContentLines(hudRound, hudSettings, DateTimeOffset.UtcNow);
            True(!lines.Any(line => line.StartsWith("Осталось")) && lines.Contains("Голосование завершено"),
                "Closed OR vote never displays a still-running timer: " + phase);
        }
        hudRound.Phase = VotingPhase.Drawn; hudRound.DeliveryAccepted = true;
        Equal("hidden", VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow).Split('\t')[2],
            "Winner already delivered clears old game HUD regardless of remaining time");
        hudRound.Phase = VotingPhase.Collecting; hudRound.TimeGoal = false; hudRound.DeliveryAccepted = false;
        hudSettings.GameHudFormat = VotingHudFormat.Amount; hudSettings.GameHudX = .25m; hudSettings.GameHudY = .7m;
        hudFields = VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow).Split('\t').Select(PipeProtocol.PercentDecode).ToArray();
        True(hudFields[2] == "xy,0.25,0.7" && hudFields[3].Split('\n')[2].StartsWith("400 руб.") && !hudFields[3].Contains('%'), "amount is on a separate line below the character bar");
        hudSettings.ShowGameHud = false;
        Equal("hidden", VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow).Split('\t')[2], "disabled HUD clears its native window");
        hudSettings.ShowGameHud = true;
        hudRound.Options = Enumerable.Range(0, 200).Select(i => new VotingOption { Command = new string('x',80), Amount = 1, Preset = new() }).ToList();
        var hudLine = VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow);
        True(Encoding.UTF8.GetByteCount(hudLine) < PipeProtocol.MaximumLineBytes &&
            PipeProtocol.PercentDecode(hudLine.Split('\t')[3]).Split('\n').Length <= 11, "large voting lists stay compact and within a single bounded HUD frame");
        hudSettings.ShowGameHudTemplateNames = true;
        foreach (var option in hudRound.Options) option.Preset!.DisplayName = longTitle + "\nподмена строки\t";
        hudLine = VotingGameHud.Frame(hudRound, hudSettings, DateTimeOffset.UtcNow);
        var expandedPayload = PipeProtocol.PercentDecode(hudLine.Split('\t')[3]);
        var expandedLines = expandedPayload.Split('\n');
        True(Encoding.UTF8.GetByteCount(hudLine) + 1 <= PipeProtocol.MaximumLineBytes &&
            Encoding.UTF8.GetByteCount(expandedPayload) <= 3200 && expandedLines.Length <= 24 &&
            expandedLines.Count(line => line.Length == 0) == 5 && expandedLines.All(line => Encoding.UTF8.GetByteCount(line) <= 160 && !line.Any(char.IsControl)),
            "Six verbose Unicode template names must fit the native limits without injecting rows/control characters");
        return Task.CompletedTask;
    }
    private static async Task TestVotingGamePipeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var name = "ADB-voting-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeGameServer("test", name, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        server.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(client, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("HELLO\t1\ttest\t1234");
        True((await reader.ReadLineAsync(timeout.Token))!.StartsWith("WELCOME\t"), "presentation handshake");
        await writer.WriteLineAsync("CAPABILITIES\tunknown\ttest\tcorrelated_ping%3Bvoting_game_ui_v1%3Bvoting_hud_v1\t");
        await writer.WriteLineAsync("STATUS\twaiting\teffect_in_progress");
        await WaitUntilAsync(() => server.Capabilities.SupportsCapability(VotingGameWire.Capability), timeout.Token);
        var request = new VotingGamePresentation(Guid.NewGuid().ToString("N"), "Тест", true, 0, "0.4", [new("0.8", "A"),new("1", "B")]);
        var exchange = server.ExchangeVotingAsync(request, request.RoundId, token: timeout.Token);
        foreach (var frame in VotingGameWire.Frames(request)) Equal(frame, await reader.ReadLineAsync(timeout.Token), "transactional frame sequence");
        await writer.WriteLineAsync("VOTING_STATUS\t" + request.RoundId + "\tactive\tshowing");
        Equal("active", (await exchange).State, "shown acknowledgement");
        exchange = server.ExchangeVotingAsync(null, request.RoundId, token: timeout.Token);
        Equal("VOTING_QUERY\t"+request.RoundId, await reader.ReadLineAsync(timeout.Token), "query works while normal effects are blocked");
        await writer.WriteLineAsync("VOTING_STATUS\t"+request.RoundId+"\tdone\tclosed"); Equal("done", (await exchange).State, "closed acknowledgement");
        exchange = server.ExchangeVotingAsync(null, request.RoundId, cancel: true, token: timeout.Token);
        Equal("VOTING_CANCEL\t"+request.RoundId, await reader.ReadLineAsync(timeout.Token), "reset cancellation");
        await writer.WriteLineAsync("VOTING_STATUS\t"+request.RoundId+"\tdone\tcancelled"); Equal("done", (await exchange).State, "cancel acknowledged");
        var hud = VotingGameHud.Frame(new VotingRound { IsActive = true, Phase = VotingPhase.Collecting }, new VotingAutomation(), DateTimeOffset.UtcNow);
        await server.SendVotingHudAsync(hud, timeout.Token);
        Equal(hud, await reader.ReadLineAsync(timeout.Token), "nonmodal game progress uses the same live pipe without enqueueing an effect");
    }
}
