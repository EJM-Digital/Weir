using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Weir.ArrRecorder;

/// <summary>A call the recorder itself makes to set the app up, which is not part of any recording.</summary>
internal sealed class ArrApi : IDisposable
{
    private readonly HttpClient _http;

    public ArrApi(string baseUrl, string apiKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    public Task<JsonNode?> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<JsonNode?> PostAsync(string path, JsonNode body) => SendAsync(HttpMethod.Post, path, body);

    public Task<JsonNode?> PutAsync(string path, JsonNode body) => SendAsync(HttpMethod.Put, path, body);

    public Task<JsonNode?> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new RecorderException($"{method} {path} answered {(int)response.StatusCode}: {Excerpt(text)}");
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    internal static string Excerpt(string text) => text.Length <= 600 ? text : text[..600] + "...";
}

/// <summary>A step of the session could not be done; the message says which call and what the app answered.</summary>
internal sealed class RecorderException : Exception
{
    public RecorderException(string message)
        : base(message)
    {
    }
}

/// <summary>Waits for something the apps do in the background.</summary>
internal static class Wait
{
    /// <summary>Polls <paramref name="ready"/> until it is true, and says what was being waited for when it never is.</summary>
    public static async Task UntilAsync(string what, TimeSpan limit, Func<Task<bool>> ready)
    {
        var until = DateTime.UtcNow + limit;
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (await ready().ConfigureAwait(false))
                {
                    return;
                }

                last = null;
            }
            catch (Exception exception) when (exception is HttpRequestException or RecorderException or TaskCanceledException)
            {
                last = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        throw new RecorderException($"Gave up waiting for {what} after {limit.TotalSeconds:0} seconds." + (last is null ? string.Empty : $" The last attempt failed: {last.Message}"));
    }
}
