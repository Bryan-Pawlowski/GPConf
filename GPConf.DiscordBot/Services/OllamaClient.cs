using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPConf.DiscordBot.Services;

/// <summary>
/// Thin wrapper around a local LLM's OpenAI-compatible /v1/chat/completions endpoint.
/// (Originally an Ollama /api/chat wrapper; switched to the OpenAI API because the
/// in-use "splash" endpoint is OpenAI-only — /api/chat returns 404 — and the same
/// request/response shape works against an Ollama server that proxies /v1.)
/// Config is read once from environment variables (same pattern as CONF_BOT_TOKEN in Program.cs).
/// </summary>
public sealed class OllamaClient
{
    // Long-lived singleton for the whole bot process — safe here since it's never
    // repeatedly created/disposed (the usual socket-exhaustion failure mode).
    //
    // 600s default: a 4-section race-weekend preview (the generate_and_post path)
    // needs ~250-300s of generation on the local 48GB M-series endpoint, so the old
    // 180s ceiling timed out mid-generation before the bot could even truncate. Short
    // calls (driver analysis, pick reveal) return in seconds, so a higher ceiling only
    // lengthens the wait on a genuinely hung/slow model. Still overridable at startup
    // via the OLLAMA_TIMEOUT_SECONDS env var below.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

    private readonly Uri _baseUri;
    private readonly string _model;

    public OllamaClient()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://100.123.43.8:11436";
        if (!host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = "http://" + host;
        _baseUri = new Uri(host);
        _model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "incoai/Qwen3.8-27B-Splash";

        if (int.TryParse(Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_SECONDS"), out var timeoutSeconds))
            Http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    /// <summary>Sends a single non-streaming chat request and returns the model's reply text.</summary>
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ChatRequest(
            Model: _model,
            Messages: [new ChatMessage("system", systemPrompt), new ChatMessage("user", userPrompt)],
            Stream: false,
            MaxTokens: 16384,
            Temperature: 0.7,
            TopP: 1.0);

        try
        {
            var content = await PostChatAsync(request, ct);
            if (string.IsNullOrWhiteSpace(content))
                throw new OllamaException("The model returned an empty response.");
            return content;
        }
        catch (OllamaException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException($"Couldn't reach the LLM endpoint at {_baseUri} ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new OllamaException($"The LLM endpoint at {_baseUri} timed out after {Http.Timeout.TotalSeconds:F0}s.", ex);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new OllamaException($"The LLM endpoint at {_baseUri} returned an unparseable response.", ex);
        }
    }

    // Posts the request to the OpenAI-compatible /v1/chat/completions endpoint and returns
    // the first choice's message content. An empty content field on a truncated/empty reply
    // surfaces as a null, which the caller reports as an empty response.
    private async Task<string?> PostChatAsync(ChatRequest request, CancellationToken ct)
    {
        // Post the explicitly-serialized body (rather than PostAsJsonAsync): vLLM rejected
        // the PostAsJsonAsync encoding with an opaque 400 while accepting identical JSON
        // posted as a StringContent body, so the serialization path is load-bearing.
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await Http.PostAsync(new Uri(_baseUri, "/v1/chat/completions"), content, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Surface the server's error body (vLLM returns JSON like {"error":{"message":...}});
            // EnsureSuccessStatusCode alone swallowed it and left every 4xx opaque.
            var err = await response.Content.ReadAsStringAsync(ct);
            if (err.Length > 1024) err = err[..1024];
            Console.WriteLine($"[OllamaClient] <- {(int)response.StatusCode} {response.ReasonPhrase}  error: {err}");
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.ReasonPhrase}: {err}");
        }
        var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        return parsed?.Choices?[^1]?.Message?.Content;
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        // Generous so the model can spend its reasoning tokens first and still have budget
        // left for the actual reply (Qwen3.8 burns several thousand tokens thinking before
        // it emits a single content token; a tight cap truncated every reply to empty).
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("top_p")] double TopP);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] ChatChoice[]? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);
}

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);
