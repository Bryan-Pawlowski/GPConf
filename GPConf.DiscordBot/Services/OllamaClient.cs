using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// Thin wrapper around a locally-hosted Ollama server's /api/chat endpoint.
/// Config is read once from environment variables (same pattern as CONF_BOT_TOKEN in Program.cs).
/// </summary>
public sealed class OllamaClient
{
    // Long-lived singleton for the whole bot process — safe here since it's never
    // repeatedly created/disposed (the usual socket-exhaustion failure mode).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly Uri _baseUri;
    private readonly string _model;

    public OllamaClient()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "scout:11434";
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;
        _baseUri = new Uri(host);
        _model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "muse-glimmer:30b-mlx";

        if (int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_SECONDS"), out var timeoutSeconds))
            Http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    /// <summary>Sends a single non-streaming chat request and returns the model's reply text.</summary>
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ChatRequest(
            _model,
            [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)],
            Stream: false,
            Think: false); // this model has "thinking" capability and rambles at length if left on

        try
        {
            var response = await Http.PostAsJsonAsync(new Uri(_baseUri, "/api/chat"), request, ct);
            response.EnsureSuccessStatusCode();
            var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
            var content = parsed?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
                throw new OllamaException("Ollama returned an empty response.");
            return content;
        }
        catch (OllamaException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException($"Couldn't reach Ollama at {_baseUri} ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new OllamaException($"Ollama at {_baseUri} timed out after {Http.Timeout.TotalSeconds:F0}s.", ex);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new OllamaException($"Ollama at {_baseUri} returned an unparseable response.", ex);
        }
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("think")] bool Think);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message);
}

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);
