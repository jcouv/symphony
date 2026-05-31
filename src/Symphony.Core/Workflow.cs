using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Symphony.Core;

public sealed class WorkflowException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class WorkflowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder().Build();

    public string SelectPath(string? explicitPath = null, string? currentDirectory = null)
    {
        var path = string.IsNullOrWhiteSpace(explicitPath)
            ? System.IO.Path.Combine(currentDirectory ?? Environment.CurrentDirectory, "WORKFLOW.md")
            : explicitPath;

        return System.IO.Path.GetFullPath(path);
    }

    public async Task<WorkflowDefinition> LoadAsync(
        string? explicitPath = null,
        string? currentDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var path = SelectPath(explicitPath, currentDirectory);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowException("missing_workflow_file", $"Unable to read workflow file: {path}", ex);
        }

        var (frontMatter, body) = SplitFrontMatter(text);
        IReadOnlyDictionary<string, object?> config = new Dictionary<string, object?>();
        if (frontMatter is not null)
        {
            try
            {
                var parsed = string.IsNullOrWhiteSpace(frontMatter)
                    ? new Dictionary<object, object?>()
                    : _deserializer.Deserialize<object?>(frontMatter);
                if (parsed is null)
                {
                    config = new Dictionary<string, object?>();
                }
                else if (NormalizeYamlValue(parsed) is IReadOnlyDictionary<string, object?> map)
                {
                    config = map;
                }
                else
                {
                    throw new WorkflowException(
                        "workflow_front_matter_not_a_map",
                        "Workflow YAML front matter must decode to a map/object.");
                }
            }
            catch (WorkflowException)
            {
                throw;
            }
            catch (YamlException ex)
            {
                throw new WorkflowException("workflow_parse_error", "Workflow YAML front matter could not be parsed.", ex);
            }
        }

        return new WorkflowDefinition(config, body.Trim(), path, DateTimeOffset.UtcNow);
    }

    private static (string? FrontMatter, string Body) SplitFrontMatter(string text)
    {
        using var reader = new StringReader(text);
        var first = reader.ReadLine();
        if (first != "---")
        {
            return (null, text);
        }

        var yaml = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == "---")
            {
                return (yaml.ToString(), reader.ReadToEnd());
            }

            yaml.AppendLine(line);
        }

        throw new WorkflowException("workflow_parse_error", "Workflow YAML front matter is missing the closing --- marker.");
    }

    private static object? NormalizeYamlValue(object? value)
    {
        return value switch
        {
            null => null,
            IDictionary<object, object?> dict => dict.ToDictionary(
                pair => Convert.ToString(pair.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                pair => NormalizeYamlValue(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            IEnumerable<object?> list => list.Select(NormalizeYamlValue).ToList(),
            _ => value
        };
    }
}

public sealed class WorkflowReloader
{
    private readonly WorkflowLoader _loader;
    private readonly string _path;
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private DateTime _lastWriteUtc;
    private long _lastSize;

    public WorkflowReloader(WorkflowLoader loader, string path, TimeSpan? debounce = null)
    {
        _loader = loader;
        _path = Path.GetFullPath(path);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
    }

    public WorkflowDefinition? Current { get; private set; }

    public Exception? LastReloadError { get; private set; }

    public event EventHandler<WorkflowDefinition>? Reloaded;

    public async Task<WorkflowDefinition> LoadInitialAsync(CancellationToken cancellationToken = default)
    {
        Current = await _loader.LoadAsync(_path, cancellationToken: cancellationToken).ConfigureAwait(false);
        RememberStamp();
        return Current;
    }

    public void Start()
    {
        var directory = Path.GetDirectoryName(_path) ?? ".";
        var file = Path.GetFileName(_path);
        _watcher = new FileSystemWatcher(directory, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName
        };
        _watcher.Changed += (_, _) => _ = ReloadIfChangedAsync();
        _watcher.Created += (_, _) => _ = ReloadIfChangedAsync();
        _watcher.Renamed += (_, _) => _ = ReloadIfChangedAsync();
        _watcher.EnableRaisingEvents = true;
    }

    public async Task ReloadIfChangedAsync(CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                LastReloadError = new WorkflowException("missing_workflow_file", $"Workflow file is missing: {_path}");
                return;
            }

            if (Current is not null && info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _lastSize)
            {
                return;
            }

            var reloaded = await _loader.LoadAsync(_path, cancellationToken: cancellationToken).ConfigureAwait(false);
            Current = reloaded;
            LastReloadError = null;
            RememberStamp();
            Reloaded?.Invoke(this, reloaded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastReloadError = ex;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private void RememberStamp()
    {
        var info = new FileInfo(_path);
        _lastWriteUtc = info.Exists ? info.LastWriteTimeUtc : default;
        _lastSize = info.Exists ? info.Length : 0;
    }
}
