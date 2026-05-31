using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Symphony.Core;

public interface ITrackerClient
{
    Task<IReadOnlyList<Issue>> FetchCandidateIssuesAsync(ServiceConfig config, CancellationToken cancellationToken);

    Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(ServiceConfig config, IReadOnlyList<string> stateNames, CancellationToken cancellationToken);

    Task<IReadOnlyList<Issue>> FetchIssueStatesByIdsAsync(ServiceConfig config, IReadOnlyList<string> issueIds, CancellationToken cancellationToken);
}

public sealed class TrackerException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class LocalIssueTrackerClient : ITrackerClient
{
    private static readonly Regex Heading = new(@"^\s*#\s+(?<title>.+?)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private readonly IDeserializer _deserializer = new DeserializerBuilder().Build();

    public Task<IReadOnlyList<Issue>> FetchCandidateIssuesAsync(ServiceConfig config, CancellationToken cancellationToken)
        => FetchIssuesByStatesAsync(config, config.Tracker.ActiveStates, cancellationToken);

    public Task<IReadOnlyList<Issue>> FetchIssuesByStatesAsync(
        ServiceConfig config,
        IReadOnlyList<string> stateNames,
        CancellationToken cancellationToken)
    {
        if (stateNames.Count == 0 || !Directory.Exists(config.Tracker.IssuesRoot))
        {
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var states = stateNames.Select(IssueState.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = EnumerateIssueFiles(config.Tracker.IssuesRoot)
            .Where(file => states.Contains(IssueState.Normalize(StateFromPath(config.Tracker.IssuesRoot, file))))
            .Select(file => ReadIssue(config.Tracker.IssuesRoot, file))
            .ToList();

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    public Task<IReadOnlyList<Issue>> FetchIssueStatesByIdsAsync(
        ServiceConfig config,
        IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken)
    {
        if (issueIds.Count == 0 || !Directory.Exists(config.Tracker.IssuesRoot))
        {
            return Task.FromResult<IReadOnlyList<Issue>>([]);
        }

        var wanted = issueIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = EnumerateIssueFiles(config.Tracker.IssuesRoot)
            .Select(file => ReadIssue(config.Tracker.IssuesRoot, file))
            .Where(issue => wanted.Contains(issue.Id))
            .ToList();

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    private Issue ReadIssue(string issuesRoot, string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var (metadata, body) = SplitMetadata(text);
        var identifier = Path.GetFileNameWithoutExtension(path);
        var title = String(metadata, "title")
            ?? FirstHeading(body)
            ?? HumanizeIdentifier(identifier);
        var info = new FileInfo(path);

        return new Issue
        {
            Id = identifier,
            Identifier = identifier,
            Title = title,
            Description = StripFirstHeading(body).Trim(),
            Priority = Int(metadata, "priority"),
            State = StateFromPath(issuesRoot, path),
            BranchName = String(metadata, "branch_name"),
            Url = new Uri(path).AbsoluteUri,
            Labels = StringList(metadata, "labels").Select(label => label.ToLowerInvariant()).ToList(),
            BlockedBy = Blockers(metadata),
            CreatedAt = info.CreationTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(info.CreationTimeUtc),
            UpdatedAt = info.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(info.LastWriteTimeUtc)
        };
    }

    private static IEnumerable<string> EnumerateIssueFiles(string issuesRoot)
        => Directory.EnumerateFiles(issuesRoot, "*.md", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(issuesRoot, path).StartsWith("workspaces" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static string StateFromPath(string issuesRoot, string issuePath)
    {
        var relative = Path.GetRelativePath(issuesRoot, Path.GetDirectoryName(issuePath) ?? issuesRoot);
        return relative == "." ? "inbox" : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }

    private (IReadOnlyDictionary<string, object?> Metadata, string Body) SplitMetadata(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine() != "---")
        {
            return (new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), text);
        }

        var yaml = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == "---")
            {
                var parsed = string.IsNullOrWhiteSpace(yaml.ToString())
                    ? new Dictionary<object, object?>()
                    : _deserializer.Deserialize<Dictionary<object, object?>>(yaml.ToString());
                return (parsed.ToDictionary(pair => pair.Key.ToString() ?? "", pair => NormalizeYamlValue(pair.Value), StringComparer.OrdinalIgnoreCase), reader.ReadToEnd());
            }

            yaml.AppendLine(line);
        }

        return (new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), text);
    }

    private static object? NormalizeYamlValue(object? value)
    {
        return value switch
        {
            IDictionary<object, object?> map => map.ToDictionary(pair => pair.Key.ToString() ?? "", pair => NormalizeYamlValue(pair.Value), StringComparer.OrdinalIgnoreCase),
            IEnumerable<object?> list => list.Select(NormalizeYamlValue).ToList(),
            _ => value
        };
    }

    private static string? FirstHeading(string body) => Heading.Match(body) is { Success: true } match ? match.Groups["title"].Value : null;

    private static string StripFirstHeading(string body) => Heading.Replace(body, "", 1);

    private static string HumanizeIdentifier(string identifier)
        => string.Join(' ', identifier.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            is { Length: > 0 } title ? char.ToUpperInvariant(title[0]) + title[1..] : identifier;

    private static string? String(IReadOnlyDictionary<string, object?> metadata, string key)
        => metadata.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? Int(IReadOnlyDictionary<string, object?> metadata, string key)
        => metadata.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : null;

    private static IReadOnlyList<string> StringList(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is IEnumerable<object?> list)
        {
            return list.Select(item => item?.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList();
        }

        return [value.ToString() ?? ""];
    }

    private static IReadOnlyList<BlockerRef> Blockers(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue("blocked_by", out var value) || value is null)
        {
            return [];
        }

        if (value is IEnumerable<object?> list)
        {
            return list.Select(item => new BlockerRef(null, item?.ToString(), null)).ToList();
        }

        return [new BlockerRef(null, value.ToString(), null)];
    }
}
