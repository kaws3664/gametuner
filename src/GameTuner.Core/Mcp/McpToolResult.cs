using System.Text.Json.Nodes;

namespace GameTuner.Core.Mcp;

public sealed class McpToolResult
{
    public bool IsError { get; init; }
    public string Text { get; init; } = "";
    public JsonNode? StructuredContent { get; init; }
    public string? ErrorCode { get; init; }
    public string RawJson { get; init; } = "";
}
