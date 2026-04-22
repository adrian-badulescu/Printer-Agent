using System.Text.Json;

namespace PrinterAgent.Application.Storage;

/// <summary>
/// Citire relaxată pentru <c>agent.json</c> (comentarii sau virgule finale adăugate manual).
/// </summary>
public static class AgentJsonDocumentOptions
{
    public static readonly JsonDocumentOptions ForRead = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
