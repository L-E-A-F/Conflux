
namespace Conflux.Models;

public sealed class AiProvider
{
    public string Name { get; set; } = "GPT";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public override string ToString() => Name;
}