using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Symphony.Core;

public sealed class PromptRenderException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class StrictPromptRenderer
{
    private static readonly Regex Interpolation = new(@"{{\s*(?<expr>[^}]+?)\s*}}", RegexOptions.Compiled);

    public string Render(string template, Issue issue, int? attempt)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            template = "You are working on a local issue.";
        }

        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["issue"] = ToTemplateDictionary(issue),
            ["attempt"] = attempt
        };

        try
        {
            return Interpolation.Replace(template, match =>
            {
                var expression = match.Groups["expr"].Value.Trim();
                if (expression.Contains('|', StringComparison.Ordinal))
                {
                    throw new PromptRenderException(
                        "template_render_error",
                        $"Unknown filters are not supported in strict prompt rendering: {expression}");
                }

                var value = ResolveExpression(context, expression);
                return value switch
                {
                    null => string.Empty,
                    string text => text,
                    _ => JsonSerializer.Serialize(value, JsonOptions)
                };
            });
        }
        catch (PromptRenderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PromptRenderException("template_render_error", ex.Message);
        }
    }

    private static object? ResolveExpression(IReadOnlyDictionary<string, object?> context, string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new PromptRenderException("template_parse_error", "Empty interpolation expression.");
        }

        var parts = expression.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !context.TryGetValue(parts[0], out var current))
        {
            throw new PromptRenderException("template_render_error", $"Unknown variable: {parts.FirstOrDefault() ?? expression}");
        }

        foreach (var part in parts.Skip(1))
        {
            current = current switch
            {
                IReadOnlyDictionary<string, object?> map when map.TryGetValue(part, out var value) => value,
                IDictionary<string, object?> map when map.TryGetValue(part, out var value) => value,
                _ => throw new PromptRenderException("template_render_error", $"Unknown variable: {expression}")
            };
        }

        return current;
    }

    private static IReadOnlyDictionary<string, object?> ToTemplateDictionary(Issue issue)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = issue.Id,
            ["identifier"] = issue.Identifier,
            ["title"] = issue.Title,
            ["description"] = issue.Description,
            ["priority"] = issue.Priority,
            ["state"] = issue.State,
            ["branch_name"] = issue.BranchName,
            ["url"] = issue.Url,
            ["labels"] = issue.Labels,
            ["blocked_by"] = issue.BlockedBy.Select(blocker => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = blocker.Id,
                ["identifier"] = blocker.Identifier,
                ["state"] = blocker.State
            }).ToList(),
            ["created_at"] = issue.CreatedAt,
            ["updated_at"] = issue.UpdatedAt
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
