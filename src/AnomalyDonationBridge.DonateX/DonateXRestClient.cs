using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.DonateX;

public sealed class DonateXRestClient
{
    private readonly HttpClient _http;
    private readonly IDonateXApiRateLimiter _rateLimiter;

    public DonateXRestClient(HttpClient http, IDonateXApiRateLimiter rateLimiter)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<DonateXProfile> GetProfileAsync(
        string externalToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            DonateXEndpoints.Profile, externalToken, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var profile = await response.Content.ReadFromJsonAsync<DonateXProfile>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("DonateX вернул пустой профиль.");
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Username))
            throw new InvalidDataException("DonateX вернул неполные данные профиля.");
        return profile;
    }

    public async Task<IReadOnlyList<DonationEvent>> GetDonationsAsync(
        string externalToken,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var endpoint = $"{DonateXEndpoints.Donations}?skip={Math.Max(0, skip)}&take={Math.Clamp(take, 1, 100)}&sortOrder=NewestFirst";
        using var response = await SendAuthenticatedAsync(
            endpoint, externalToken, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<List<DonateXDonation>>(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        var result = new List<DonationEvent>(payload.Count);
        foreach (var item in payload)
        {
            try { result.Add(item.ToDonationEvent()); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException) { }
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        string endpoint,
        string externalToken,
        CancellationToken cancellationToken)
    {
        var token = DonateXToken.Normalize(externalToken);
        if (token.Length == 0)
            throw new InvalidOperationException("Внешний токен DonateX не указан.");

        // DonateX documents both query names for an external token while its
        // endpoint examples use Bearer. Deployments have not always accepted
        // the same form on every endpoint, so retry only an authentication
        // failure with the other documented forms.
        for (var mode = 0; mode < 3; mode++)
        {
            using var request = CreateRequest(endpoint, token, mode);
            var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not System.Net.HttpStatusCode.Unauthorized and
                not System.Net.HttpStatusCode.Forbidden || mode == 2)
                return response;
            response.Dispose();
        }

        throw new InvalidOperationException("DonateX authentication attempts were exhausted.");
    }

    private static HttpRequestMessage CreateRequest(string endpoint, string token, int mode)
    {
        HttpRequestMessage request;
        if (mode < 2)
        {
            var separator = endpoint.Contains('?') ? '&' : '?';
            var parameter = mode == 0 ? "token" : "access_token";
            request = new HttpRequestMessage(HttpMethod.Get,
                endpoint + separator + parameter + "=" + Uri.EscapeDataString(token));
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AnomalyDonationBridge/1.0");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response) => response.IsSuccessStatusCode
        ? Task.CompletedTask
        : Task.FromException(new HttpRequestException(
            response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "DonateX отклонил API-ключ. Скопируйте его кнопкой в кабинете Настройки → API; если ошибка повторяется, перевыпустите ключ."
                : $"DonateX API вернул {(int)response.StatusCode} {response.ReasonPhrase}",
            null, response.StatusCode));
}
