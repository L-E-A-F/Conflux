
namespace Conflux.Services;

public interface IAiProvider
{
    Task<string> SendAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}