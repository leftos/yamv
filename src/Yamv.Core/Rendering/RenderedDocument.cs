namespace Yamv.Core.Rendering;

/// <summary>The HTML for one document, plus what the viewer has to prepare before showing it.</summary>
/// <param name="Html">The rendered HTML body.</param>
/// <param name="Title">The plain text of the first level-1 heading, or null when the document has none.</param>
/// <param name="HasMermaid">Whether the document contains a mermaid diagram.</param>
/// <param name="HasMath">Whether the document contains math for the math renderer.</param>
public sealed record RenderedDocument(string Html, string? Title, bool HasMermaid, bool HasMath);
