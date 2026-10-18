
using Conflux.Models;

namespace Conflux.Services;

public sealed class AiRouter
{
    public async Task<(string Answer, string Provider)> SendAsync(
        AppSettings settings,
        RoutingProfile route,
        string prompt,
        Action<string>? statusChanged = null,
        CancellationToken cancellationToken = default)
    {
        var primary = FindProvider(settings, route.PrimaryProvider);

        var secondary = FindProvider(
            settings, route.SecondaryProvider);

        if (primary is null)
            throw new InvalidOperationException(
                $"Primary provider '{route.PrimaryProvider}' " +
                "is missing or disabled.");

        string answer;
        string usedProvider;

        try
        {
            statusChanged?.Invoke(
                $"Sending request to {primary.Name}...");

            answer = await CallAsync(
                primary, prompt, cancellationToken);

            usedProvider = primary.Name;
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            if (secondary is null ||
                secondary.Name.Equals(
                    primary.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{primary.Name} failed and no usable backup " +
                    $"is configured.\n\n{ex.Message}", ex);
            }

            statusChanged?.Invoke(
                $"{primary.Name} failed. Switching to " +
                $"{secondary.Name}...\nReason: {ex.Message}");

            answer = await CallAsync(
                secondary, prompt, cancellationToken);

            usedProvider = secondary.Name;
        }

        if (route.EnableReview &&
            secondary is not null &&
            !secondary.Name.Equals(
                usedProvider, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                statusChanged?.Invoke(
                    $"{usedProvider} responded. " +
                    $"{secondary.Name} is reviewing the result...");

                var reviewPrompt =
                    "Review the following answer for factual errors, " +
                    "logic problems, omissions, and potential bugs. " +
                    "If it is code, check correctness and edge cases. " +
                    "Do not blindly agree. Give a concise review, " +
                    "then provide a corrected answer only if needed.\n\n" +
                    "ORIGINAL TASK:\n" + prompt +
                    "\n\nPROPOSED ANSWER:\n" + answer;

                var review = await CallAsync(
                    secondary, reviewPrompt, cancellationToken);

                answer += "\n\n---\nINDEPENDENT REVIEW (" +
                          secondary.Name + ")\n\n" + review;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                answer += "\n\n---\nReview unavailable: " +
                          ex.Message;
            }
        }

        return (answer, usedProvider);
    }

    private static AiProvider? FindProvider(
        AppSettings settings, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return settings.Providers.FirstOrDefault(p =>
            p.Enabled && p.Name.Equals(
                name, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<string> CallAsync(
        AiProvider provider,
        string prompt,
        CancellationToken cancellationToken)
    {
        IAiProvider client =
            new OpenAiCompatibleProvider(provider);

        return client.SendAsync(
            "You are an AI assistant collaborating in Conflux. " +
            "Be accurate, state uncertainty, and return complete " +
            "code when explicitly requested.",
            prompt,
            cancellationToken);
    }
}