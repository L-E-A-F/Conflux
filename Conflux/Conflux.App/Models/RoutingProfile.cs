
namespace Conflux.Models;

public sealed class RoutingProfile
{
    public string TaskType { get; set; } = "General";
    public string PrimaryProvider { get; set; } = "GPT";
    public string SecondaryProvider { get; set; } = "";
    public bool EnableReview { get; set; }
}