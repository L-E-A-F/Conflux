
namespace Conflux.Models;

public sealed class ChatMessage
{
    public string Role { get; set; } = "assistant";
    public string Content { get; set; } = "";
    public string Provider { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}