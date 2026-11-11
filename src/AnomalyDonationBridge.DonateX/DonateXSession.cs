using AnomalyDonationBridge.Core;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.DonateX;

public sealed class DonateXSession : IAsyncDisposable
{
    private readonly string _externalToken;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;

    public DonateXSession(string externalToken)
    {
        _externalToken = DonateXToken.Normalize(externalToken);
        if (_externalToken.Length == 0)
            throw new ArgumentException("Внешний токен DonateX не указан.", nameof(externalToken));
    }

    public event Func<DonationEvent, Task>? DonationReceived;
    public event Action<DonateXConnectionState>? ConnectionChanged;

    public void Start() => _runTask ??= Task.Run(() => ReconnectLoopAsync(_lifetime.Token));

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var connection = BuildConnection();
            connection.Closed += _ =>
            {
                closed.TrySetResult();
                return Task.CompletedTask;
            };
            connection.On<DonateXDonation>("DonationCreated", async payload =>
            {
                try
                {
                    var donation = payload.ToDonationEvent();
                    if (DonationReceived is { } handler) await handler(donation).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
                {
                    ConnectionChanged?.Invoke(new DonateXConnectionState(true,
                        "DonateX прислал некорректное событие: " + SafeMessage(exception)));
                }
            });

            try
            {
                ConnectionChanged?.Invoke(new DonateXConnectionState(false, "Подключение к DonateX…"));
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
                ConnectionChanged?.Invoke(new DonateXConnectionState(true, "DonateX подключён"));
                await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException("DonateX закрыл соединение SignalR.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                attempt++;
                // DonateX permits at most two new hub connections per second. The first retry
                // therefore starts no sooner than two seconds after the failed attempt.
                var delaySeconds = Math.Min(60, Math.Max(2, Math.Pow(2, Math.Min(attempt, 6))));
                ConnectionChanged?.Invoke(new DonateXConnectionState(false,
                    $"DonateX: переподключение через {delaySeconds:0} с: {SafeMessage(exception)}"));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }
        ConnectionChanged?.Invoke(new DonateXConnectionState(false, "DonateX отключён"));
    }

    private HubConnection BuildConnection()
    {
        var endpoint = DonateXEndpoints.DonationsHub +
            "?access_token=" + Uri.EscapeDataString(_externalToken);
        return new HubConnectionBuilder()
            .WithUrl(endpoint)
            .Build();
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        message = Regex.Replace(message, "(?i)(access_token|token)=([^&\\s]+)", "$1=[REDACTED]");
        return message.Length > 180 ? message[..180] : message;
    }
}
