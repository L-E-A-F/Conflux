
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Conflux.Models;

namespace Conflux.Services;

public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private readonly AiProvider _provider;

    public OpenAiCompatibleProvider(AiProvider provider)
    {
        _provider = provider;
    }

    public async Task<string> SendAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_provider.BaseUrl))
            throw new InvalidOperationException("The provider URL is empty.");

        if (string.IsNullOrWhiteSpace(_provider.Model))
            throw new InvalidOperationException(
                $"The model name for {_provider.Name} is empty.");

        if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            throw new InvalidOperationException(
                $"No API key is configured for {_provider.Name}.");

        var baseUrl = _provider.BaseUrl.TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";

        var payload = new
        {
            model = _provider.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.4
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, endpoint);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _provider.ApiKey);

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string detail = responseBody;

            try
            {
                using var errorJson = JsonDocument.Parse(responseBody);
                if (errorJson.RootElement.TryGetProperty(
                    "error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out var message))
                    {
                        detail = message.GetString() ?? responseBody;
                    }
                    else
                    {
                        detail = error.ToString();
                    }
                }
            }
            catch (JsonException)
            {
                // Preserve the raw response when it is not JSON.
            }

            throw new HttpRequestException(
                $"{_provider.Name} returned HTTP " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {detail}",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);

        if (!document.RootElement.TryGetProperty(
            "choices", out var choices) ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"{_provider.Name} returned no response choices.");
        }

        var messageElement = choices[0].GetProperty("message");

        if (!messageElement.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException(
                $"{_provider.Name} returned no message content.");
        }

        // Some compatible APIs return content as a string; others
        // return an array of content blocks.
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    parts.Add(text.GetString() ?? "");
            }

            return string.Join("", parts);
        }

        throw new InvalidOperationException(
            $"{_provider.Name} returned an unsupported content format.");
    }
}