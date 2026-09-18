using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Iris.Ai;
using Iris.Ai.Json;
using Iris.Ai.Utils;
using Iris.CodingAgent.Config;
using Iris.CodingAgent.Utils;

namespace Iris.CodingAgent.Core;

public sealed class NewSessionOptions
{
    public string? Id { get; init; }
    public string? ParentSession { get; init; }
}

public sealed class SessionTreeNode
{
    public required SessionEntry Entry { get; init; }
    public List<SessionTreeNode> Children { get; } = [];
    public string? Label { get; set; }
    public string? LabelTimestamp { get; set; }
}

public sealed record SessionContextModel(string Provider, string ModelId);

public sealed class SessionContext
{
    public List<Message> Messages { get; init; } = [];
    public string ThinkingLevel { get; init; } = "off";
    public SessionContextModel? Model { get; init; }
}

public sealed class SessionInfo
{
    public required string Path { get; init; }
    public required string Id { get; init; }
    /// <summary>Working directory where the session was started. Empty for old sessions.</summary>
    public string Cwd { get; init; } = "";
    public string? Name { get; init; }
    public string? ParentSessionPath { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public int MessageCount { get; init; }
    public string FirstMessage { get; init; } = "";
    public string AllMessagesText { get; init; } = "";
}

/// <summary>
/// Manages conversation sessions as append-only trees stored in JSONL files.
/// </summary>
public sealed partial class SessionManager
{
    public const int CurrentSessionVersion = 3;

    private string _sessionId = "";
    private string? _sessionFile;
    private readonly string _sessionDir;
    private readonly string _cwd;
    private readonly bool _persist;
    private bool _flushed;
    private List<FileEntry> _fileEntries = [];
    private readonly Dictionary<string, SessionEntry> _byId = new();
    private readonly Dictionary<string, string> _labelsById = new();
    private readonly Dictionary<string, string> _labelTimestampsById = new();
    private string? _leafId;

    static SessionManager() => CodingAgentMessages.Register();

    private SessionManager(string cwd, string sessionDir, string? sessionFile, bool persist, NewSessionOptions? options = null, List<FileEntry>? preloaded = null)
    {
        _cwd = PathUtils.ResolvePath(cwd);
        _sessionDir = string.IsNullOrEmpty(sessionDir) ? "" : PathUtils.NormalizePath(sessionDir);
        _persist = persist;
        if (persist && _sessionDir.Length > 0) Directory.CreateDirectory(_sessionDir);

        if (sessionFile is not null) SetSessionFileCore(sessionFile, preloaded);
        else if (preloaded is { Count: > 0 }) LoadEntries(preloaded, options);
        else NewSession(options);
    }

    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$")]
    private static partial Regex ValidSessionId();

    public static void AssertValidSessionId(string id)
    {
        if (!ValidSessionId().IsMatch(id))
        {
            throw new ArgumentException("Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character");
        }
    }

    private static string CreateSessionId() => UuidV7.New();

    /// <summary>Unique short id: 8 hex chars, collision-checked.</summary>
    private static string GenerateId(Func<string, bool> exists)
    {
        for (var i = 0; i < 100; i++)
        {
            var id = Guid.NewGuid().ToString()[..8];
            if (!exists(id)) return id;
        }
        return Guid.NewGuid().ToString();
    }

    public static string SerializeEntry(FileEntry entry) => JsonSerializer.Serialize(entry, typeof(FileEntry), IrisJson.Options);

    public static FileEntry? ParseEntryLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonNode.Parse(line) is JsonObject obj ? FileEntryJsonConverter.FromObject(obj) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- migrations ----

    private static List<FileEntry> MigrateRaw(List<JsonObject> raw)
    {
        var header = raw.FirstOrDefault(e => IrisJson.GetString(e["type"]) == "session");
        var version = IrisJson.GetLong(header?["version"]) ?? 1;
        if (version >= CurrentSessionVersion) return raw.Select(o => FileEntryJsonConverter.FromObject(o)).ToList();

        if (version < 2)
        {
            var ids = new HashSet<string>();
            string? prevId = null;
            foreach (var entry in raw)
            {
                if (IrisJson.GetString(entry["type"]) == "session")
                {
                    entry["version"] = 2;
                    continue;
                }
                var id = GenerateId(ids.Contains);
                ids.Add(id);
                entry["id"] = id;
                entry["parentId"] = prevId;
                prevId = id;
            }
            foreach (var entry in raw)
            {
                if (IrisJson.GetString(entry["type"]) != "compaction") continue;
                if (IrisJson.GetLong(entry["firstKeptEntryIndex"]) is { } index)
                {
                    if (index >= 0 && index < raw.Count && IrisJson.GetString(raw[(int)index]["type"]) != "session")
                    {
                        entry["firstKeptEntryId"] = raw[(int)index]["id"]?.DeepClone();
                    }
                    entry.Remove("firstKeptEntryIndex");
                }
            }
        }
        if (version < 3)
        {
            foreach (var entry in raw)
            {
                if (IrisJson.GetString(entry["type"]) == "session")
                {
                    entry["version"] = 3;
                    continue;
                }
                if (IrisJson.GetString(entry["type"]) == "message" && entry["message"] is JsonObject message && IrisJson.GetString(message["role"]) == "hookMessage")
                {
                    message["role"] = "custom";
                }
            }
        }
        return raw.Select(o => FileEntryJsonConverter.FromObject(o)).ToList();
    }

    /// <summary>Parse file content into raw JSON objects (malformed lines skipped).</summary>
    private static List<JsonObject> ParseRawLines(IEnumerable<string> lines)
    {
        var result = new List<JsonObject>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj) result.Add(obj);
            }
            catch (JsonException)
            {
            }
        }
        return result;
    }

    public static List<FileEntry> ParseSessionEntries(string content) =>
        ParseRawLines(content.Trim().Split('\n')).Select(o => FileEntryJsonConverter.FromObject(o)).ToList();

    /// <summary>Load raw entries from a file, validating the header and repairing a missing trailing newline.</summary>
    private static (List<JsonObject> Entries, bool NeedsVersionMigration) LoadRawFromFile(string filePath)
    {
        var resolved = PathUtils.NormalizePath(filePath);
        if (!File.Exists(resolved)) return ([], false);
        var content = File.ReadAllText(resolved, Encoding.UTF8);
        var raw = ParseRawLines(content.Split('\n'));
        if (raw.Count == 0) return (raw, false);
        var header = raw[0];
        if (IrisJson.GetString(header["type"]) != "session" || IrisJson.GetString(header["id"]) is null) return ([], false);
        if (content.Length > 0 && !content.EndsWith('\n'))
        {
            var lastLine = content[(content.LastIndexOf('\n') + 1)..];
            if (lastLine.Length > 0) File.AppendAllText(resolved, "\n");
        }
        var version = IrisJson.GetLong(header["version"]) ?? 1;
        return (raw, version < CurrentSessionVersion);
    }

    public static List<FileEntry> LoadEntriesFromFile(string filePath) =>
        MigrateRaw(LoadRawFromFile(filePath).Entries);

    public static CompactionEntry? GetLatestCompactionEntry(IReadOnlyList<SessionEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is CompactionEntry compaction) return compaction;
        }
        return null;
    }

    private static List<SessionEntry> BuildSessionPath(IReadOnlyList<SessionEntry> entries, string? leafId, bool leafSpecified, IReadOnlyDictionary<string, SessionEntry>? byId)
    {
        var index = byId ?? entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.Last());
        if (leafSpecified && leafId is null) return [];
        SessionEntry? leaf = null;
        if (leafId is not null) index.TryGetValue(leafId, out leaf);
        leaf ??= entries.Count > 0 ? entries[^1] : null;
        if (leaf is null) return [];
        var path = new List<SessionEntry>();
        var visited = new HashSet<string>();
        var current = leaf;
        while (current is not null && visited.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is not null && index.TryGetValue(current.ParentId, out var parent) ? parent : null;
        }
        path.Reverse();
        return path;
    }

    private static (string ThinkingLevel, SessionContextModel? Model) GetSessionContextSettings(IEnumerable<SessionEntry> path)
    {
        var thinkingLevel = "off";
        SessionContextModel? model = null;
        foreach (var entry in path)
        {
            switch (entry)
            {
                case ThinkingLevelChangeEntry t:
                    thinkingLevel = t.ThinkingLevel;
                    break;
                case ModelChangeEntry m:
                    model = new SessionContextModel(m.Provider, m.ModelId);
                    break;
                case SessionMessageEntry { Message: AssistantMessage a }:
                    model = new SessionContextModel(a.Provider, a.Model);
                    break;
            }
        }
        return (thinkingLevel, model);
    }

    /// <summary>Project one session entry into LLM/runtime messages.</summary>
    public static List<Message> SessionEntryToContextMessages(SessionEntry entry) => entry switch
    {
        SessionMessageEntry m => [m.Message],
        CustomMessageEntry c => [CodingAgentMessages.CreateCustomMessage(c.CustomType, c.Content, c.Display, c.Details, c.Timestamp)],
        BranchSummaryEntry b when !string.IsNullOrEmpty(b.Summary) => [CodingAgentMessages.CreateBranchSummaryMessage(b.Summary, b.FromId, b.Timestamp)],
        CompactionEntry c => [CodingAgentMessages.CreateCompactionSummaryMessage(c.Summary, c.TokensBefore, c.Timestamp)],
        _ => [],
    };

    /// <summary>Build the active, compaction-aware entry list for the given leaf.</summary>
    public static List<SessionEntry> BuildContextEntries(IReadOnlyList<SessionEntry> entries, string? leafId, bool leafSpecified = true, IReadOnlyDictionary<string, SessionEntry>? byId = null)
    {
        var path = BuildSessionPath(entries, leafId, leafSpecified, byId);
        var compaction = path.OfType<CompactionEntry>().LastOrDefault();
        if (compaction is null) return path;
        var compactionIdx = path.FindIndex(e => e.Id == compaction.Id);
        if (compactionIdx < 0) return path;

        var context = new List<SessionEntry> { compaction };
        var foundFirstKept = false;
        for (var i = 0; i < compactionIdx; i++)
        {
            if (path[i].Id == compaction.FirstKeptEntryId) foundFirstKept = true;
            if (foundFirstKept) context.Add(path[i]);
        }
        context.AddRange(path.Skip(compactionIdx + 1));
        return context;
    }

    public static SessionContext BuildSessionContext(IReadOnlyList<SessionEntry> entries, string? leafId, bool leafSpecified = true, IReadOnlyDictionary<string, SessionEntry>? byId = null)
    {
        var path = BuildSessionPath(entries, leafId, leafSpecified, byId);
        var (thinkingLevel, model) = GetSessionContextSettings(path);
        var messages = BuildContextEntries(entries, leafId, leafSpecified, byId).SelectMany(SessionEntryToContextMessages).ToList();
        return new SessionContext { Messages = messages, ThinkingLevel = thinkingLevel, Model = model };
    }

    private static string GetDefaultSessionDirPath(string cwd, string? agentDir = null)
    {
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var resolvedAgentDir = PathUtils.ResolvePath(agentDir ?? AppConfig.AgentDir);
        var trimmed = resolvedCwd.Length > 0 && (resolvedCwd[0] == '/' || resolvedCwd[0] == '\\') ? resolvedCwd[1..] : resolvedCwd;
        var safe = $"--{Regex.Replace(trimmed, @"[/\\:]", "-")}--";
        return Path.Combine(resolvedAgentDir, "sessions", safe);
    }

    public static string GetDefaultSessionDir(string cwd, string? agentDir = null)
    {
        var dir = GetDefaultSessionDirPath(cwd, agentDir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SessionHeader? ReadSessionHeader(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        long scanned = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            scanned += line.Length + 1;
            if (scanned > 1024 * 1024) return null;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var entry = ParseEntryLine(line);
            if (entry is null) continue;
            return entry is SessionHeader header && header.Id.Length > 0 ? header : null;
        }
        return null;
    }

    private static SessionHeader? ReadSessionHeaderForDiscovery(string filePath)
    {
        try
        {
            return ReadSessionHeader(filePath);
        }
        catch
        {
            return null;
        }
    }

    private static bool SessionCwdMatches(string? cwd, string resolvedCwd) =>
        !string.IsNullOrEmpty(cwd) && string.Equals(PathUtils.ResolvePath(cwd), resolvedCwd, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string? FindMostRecentSession(string sessionDir, string? cwd = null)
    {
        var dir = PathUtils.NormalizePath(sessionDir);
        var resolvedCwd = cwd is null ? null : PathUtils.ResolvePath(cwd);
        try
        {
            return Directory.EnumerateFiles(dir, "*.jsonl")
                .Select(path => (Path: path, Header: ReadSessionHeaderForDiscovery(path)))
                .Where(f => f.Header is not null && (resolvedCwd is null || SessionCwdMatches(f.Header.Cwd, resolvedCwd)))
                .Select(f => (f.Path, MTime: File.GetLastWriteTimeUtc(f.Path)))
                .OrderByDescending(f => f.MTime)
                .Select(f => f.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractTextContent(Message message) => message switch
    {
        UserMessage u => u.Content.Text ?? string.Join(" ", u.Content.Blocks!.OfType<TextContent>().Select(b => b.Text)),
        AssistantMessage a => string.Join(" ", a.Content.OfType<TextContent>().Select(b => b.Text)),
        _ => "",
    };

    private static SessionInfo? BuildSessionInfo(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            SessionHeader? header = null;
            var messageCount = 0;
            var firstMessage = "";
            var allMessages = new List<string>();
            string? name = null;
            long? lastActivity = null;

            foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
            {
                var entry = ParseEntryLine(line);
                if (entry is null) continue;
                if (header is null)
                {
                    if (entry is not SessionHeader h) return null;
                    header = h;
                    continue;
                }
                if (entry is SessionInfoEntry sessionInfo) name = string.IsNullOrWhiteSpace(sessionInfo.Name) ? null : sessionInfo.Name.Trim();
                if (entry is not SessionMessageEntry messageEntry) continue;
                messageCount++;
                var message = messageEntry.Message;
                if (message is not (UserMessage or AssistantMessage)) continue;
                var activity = message.Timestamp > 0 ? message.Timestamp : CodingAgentMessages.ParseTimestamp(messageEntry.Timestamp);
                if (activity > 0) lastActivity = Math.Max(lastActivity ?? 0, activity);
                var text = ExtractTextContent(message);
                if (text.Length == 0) continue;
                allMessages.Add(text);
                if (firstMessage.Length == 0 && message is UserMessage) firstMessage = text;
            }
            if (header is null) return null;

            var headerTime = CodingAgentMessages.ParseTimestamp(header.Timestamp);
            var modified = lastActivity is > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastActivity.Value)
                : headerTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(headerTime) : new DateTimeOffset(info.LastWriteTimeUtc);
            return new SessionInfo
            {
                Path = filePath,
                Id = header.Id,
                Cwd = header.Cwd ?? "",
                Name = name,
                ParentSessionPath = header.ParentSession,
                Created = DateTimeOffset.FromUnixTimeMilliseconds(headerTime),
                Modified = modified,
                MessageCount = messageCount,
                FirstMessage = firstMessage.Length > 0 ? firstMessage : "(no messages)",
                AllMessagesText = string.Join(" ", allMessages),
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<SessionInfo>> BuildSessionInfosAsync(IReadOnlyList<string> files, Action? onLoaded)
    {
        var results = new SessionInfo?[files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = 10 }, (index, _) =>
        {
            results[index] = BuildSessionInfo(files[index]);
            onLoaded?.Invoke();
            return ValueTask.CompletedTask;
        });
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    // ---- instance ----

    public void SetSessionFile(string sessionFile) => SetSessionFileCore(sessionFile, null);

    private void SetSessionFileCore(string sessionFile, List<FileEntry>? preloaded)
    {
        _sessionFile = PathUtils.ResolvePath(sessionFile);
        if (File.Exists(_sessionFile))
        {
            List<FileEntry> entries;
            var needsRewrite = false;
            if (preloaded is not null)
            {
                entries = preloaded;
            }
            else
            {
                var (raw, needsMigration) = LoadRawFromFile(_sessionFile);
                entries = MigrateRaw(raw);
                needsRewrite = needsMigration;
            }

            if (entries.Count == 0)
            {
                var explicitPath = _sessionFile;
                if (new FileInfo(explicitPath).Length > 0) throw new InvalidOperationException($"Session file is not a valid {AppConfig.AppName} session: {explicitPath}");
                NewSession();
                _sessionFile = explicitPath;
                RewriteFile();
                _flushed = true;
                return;
            }

            LoadEntries(entries, null, needsRewrite);
            _flushed = true;
        }
        else
        {
            var explicitPath = _sessionFile;
            NewSession();
            _sessionFile = explicitPath;
        }
    }

    public string? NewSession(NewSessionOptions? options = null)
    {
        if (options?.Id is not null) AssertValidSessionId(options.Id);
        _sessionId = options?.Id ?? CreateSessionId();
        var timestamp = NowIso();
        var header = new SessionHeader
        {
            Version = CurrentSessionVersion,
            Id = _sessionId,
            Timestamp = timestamp,
            Cwd = _cwd,
            ParentSession = options?.ParentSession,
        };
        _fileEntries = [header];
        _byId.Clear();
        _labelsById.Clear();
        _labelTimestampsById.Clear();
        _leafId = null;
        _flushed = false;
        if (_persist)
        {
            var fileTimestamp = timestamp.Replace(':', '-').Replace('.', '-');
            _sessionFile = Path.Combine(_sessionDir, $"{fileTimestamp}_{_sessionId}.jsonl");
        }
        return _sessionFile;
    }

    private void LoadEntries(List<FileEntry> entries, NewSessionOptions? options, bool rewrite = false)
    {
        var header = entries.OfType<SessionHeader>().FirstOrDefault();
        if (header is not null)
        {
            _fileEntries = entries;
            _sessionId = header.Id;
            if (rewrite) RewriteFile();
        }
        else
        {
            NewSession(options);
            _fileEntries.AddRange(entries);
        }
        BuildIndex();
    }

    private void BuildIndex()
    {
        _byId.Clear();
        _labelsById.Clear();
        _labelTimestampsById.Clear();
        _leafId = null;
        foreach (var entry in _fileEntries.OfType<SessionEntry>())
        {
            _byId[entry.Id] = entry;
            _leafId = entry.Id;
            if (entry is LabelEntry label)
            {
                if (!string.IsNullOrEmpty(label.Label))
                {
                    _labelsById[label.TargetId] = label.Label;
                    _labelTimestampsById[label.TargetId] = label.Timestamp;
                }
                else
                {
                    _labelsById.Remove(label.TargetId);
                    _labelTimestampsById.Remove(label.TargetId);
                }
            }
        }
    }

    private void RewriteFile()
    {
        if (!_persist || _sessionFile is null) return;
        var sb = new StringBuilder();
        foreach (var entry in _fileEntries) sb.Append(SerializeEntry(entry)).Append('\n');
        File.WriteAllText(_sessionFile, sb.ToString(), new UTF8Encoding(false));
    }

    public bool IsPersisted => _persist;

    public string Cwd => _cwd;

    public string SessionDir => _sessionDir;

    public bool UsesDefaultSessionDir => _sessionDir == GetDefaultSessionDirPath(_cwd);

    public string SessionId => _sessionId;

    public string? SessionFile => _sessionFile;

    private readonly object _writeGate = new();

    internal void Persist(SessionEntry entry)
    {
        if (!_persist || _sessionFile is null) return;
        lock (_writeGate)
        {
            var hasAssistant = _fileEntries.Any(e => e is SessionMessageEntry { Message: AssistantMessage });
            if (!hasAssistant)
            {
                if (_flushed) File.AppendAllText(_sessionFile, SerializeEntry(entry) + "\n", new UTF8Encoding(false));
                else _flushed = false;
                return;
            }

            if (!_flushed)
            {
                var sb = new StringBuilder();
                foreach (var e in _fileEntries) sb.Append(SerializeEntry(e)).Append('\n');
                using (var stream = new FileStream(_sessionFile, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(sb.ToString());
                }
                _flushed = true;
            }
            else
            {
                File.AppendAllText(_sessionFile, SerializeEntry(entry) + "\n", new UTF8Encoding(false));
            }
        }
    }

    private void AppendEntry(SessionEntry entry)
    {
        _fileEntries.Add(entry);
        _byId[entry.Id] = entry;
        _leafId = entry.Id;
        Persist(entry);
    }

    private string NextId() => GenerateId(_byId.ContainsKey);

    /// <summary>Append a message as child of the current leaf.</summary>
    public string AppendMessage(Message message)
    {
        if (message is CompactionSummaryMessage or BranchSummaryMessage)
            throw new InvalidOperationException("Use AppendCompaction/BranchWithSummary for summary messages");
        var entry = new SessionMessageEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), Message = message };
        AppendEntry(entry);
        return entry.Id;
    }

    public string AppendThinkingLevelChange(string thinkingLevel)
    {
        var entry = new ThinkingLevelChangeEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), ThinkingLevel = thinkingLevel };
        AppendEntry(entry);
        return entry.Id;
    }

    public string AppendModelChange(string provider, string modelId)
    {
        var entry = new ModelChangeEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), Provider = provider, ModelId = modelId };
        AppendEntry(entry);
        return entry.Id;
    }

    public string AppendCompaction(string summary, string firstKeptEntryId, long tokensBefore, JsonNode? details = null, bool? fromHook = null, Usage? usage = null)
    {
        var entry = new CompactionEntry
        {
            Id = NextId(), ParentId = _leafId, Timestamp = NowIso(),
            Summary = summary, FirstKeptEntryId = firstKeptEntryId, TokensBefore = tokensBefore, Details = details, Usage = usage, FromHook = fromHook,
        };
        AppendEntry(entry);
        return entry.Id;
    }

    public string AppendCustomEntry(string customType, JsonNode? data = null)
    {
        var entry = new CustomEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), CustomType = customType, Data = data };
        AppendEntry(entry);
        return entry.Id;
    }

    [GeneratedRegex("[\\r\\n]+")]
    private static partial Regex NewLines();

    public string AppendSessionInfo(string name)
    {
        var entry = new SessionInfoEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), Name = NewLines().Replace(name, " ").Trim() };
        AppendEntry(entry);
        return entry.Id;
    }

    public string? SessionName
    {
        get
        {
            for (var i = _fileEntries.Count - 1; i >= 0; i--)
            {
                if (_fileEntries[i] is SessionInfoEntry info) return string.IsNullOrWhiteSpace(info.Name) ? null : info.Name.Trim();
            }
            return null;
        }
    }

    public string AppendCustomMessageEntry(string customType, UserContent content, bool display, JsonNode? details = null)
    {
        var entry = new CustomMessageEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), CustomType = customType, Content = content, Display = display, Details = details };
        AppendEntry(entry);
        return entry.Id;
    }

    // ---- tree traversal ----

    public string? LeafId => _leafId;

    public SessionEntry? LeafEntry => _leafId is not null && _byId.TryGetValue(_leafId, out var e) ? e : null;

    public SessionEntry? GetEntry(string id) => _byId.GetValueOrDefault(id);

    public List<SessionEntry> GetChildren(string parentId) => _byId.Values.Where(e => e.ParentId == parentId).ToList();

    public string? GetLabel(string id) => _labelsById.GetValueOrDefault(id);

    public string AppendLabelChange(string targetId, string? label)
    {
        if (!_byId.ContainsKey(targetId)) throw new InvalidOperationException($"Entry {targetId} not found");
        var entry = new LabelEntry { Id = NextId(), ParentId = _leafId, Timestamp = NowIso(), TargetId = targetId, Label = label };
        AppendEntry(entry);
        if (!string.IsNullOrEmpty(label))
        {
            _labelsById[targetId] = label;
            _labelTimestampsById[targetId] = entry.Timestamp;
        }
        else
        {
            _labelsById.Remove(targetId);
            _labelTimestampsById.Remove(targetId);
        }
        return entry.Id;
    }

    /// <summary>Walk from an entry (default leaf) to the root, returning path order.</summary>
    public List<SessionEntry> GetBranch(string? fromId = null)
    {
        var path = new List<SessionEntry>();
        var startId = fromId ?? _leafId;
        var current = startId is not null ? _byId.GetValueOrDefault(startId) : null;
        var visited = new HashSet<string>();
        while (current is not null && visited.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is not null ? _byId.GetValueOrDefault(current.ParentId) : null;
        }
        path.Reverse();
        return path;
    }

    public List<SessionEntry> BuildContextEntries() => BuildContextEntries(GetEntries(), _leafId, true, _byId);

    public SessionContext BuildSessionContext() => BuildSessionContext(GetEntries(), _leafId, true, _byId);

    public SessionHeader? Header => _fileEntries.OfType<SessionHeader>().FirstOrDefault();

    public List<SessionEntry> GetEntries() => _fileEntries.OfType<SessionEntry>().ToList();

    public List<SessionTreeNode> GetTree()
    {
        var entries = GetEntries();
        var nodes = new Dictionary<string, SessionTreeNode>();
        var roots = new List<SessionTreeNode>();
        foreach (var entry in entries)
        {
            nodes[entry.Id] = new SessionTreeNode
            {
                Entry = entry,
                Label = _labelsById.GetValueOrDefault(entry.Id),
                LabelTimestamp = _labelTimestampsById.GetValueOrDefault(entry.Id),
            };
        }
        foreach (var entry in entries)
        {
            var node = nodes[entry.Id];
            if (entry.ParentId is null || entry.ParentId == entry.Id) roots.Add(node);
            else if (nodes.TryGetValue(entry.ParentId, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        var stack = new Stack<SessionTreeNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            var sorted = node.Children.OrderBy(c => CodingAgentMessages.ParseTimestamp(c.Entry.Timestamp)).ToList();
            node.Children.Clear();
            node.Children.AddRange(sorted);
            foreach (var child in node.Children) stack.Push(child);
        }
        return roots;
    }

    // ---- branching ----

    public void Branch(string branchFromId)
    {
        if (!_byId.ContainsKey(branchFromId)) throw new InvalidOperationException($"Entry {branchFromId} not found");
        _leafId = branchFromId;
    }

    public void ResetLeaf() => _leafId = null;

    public string BranchWithSummary(string? branchFromId, string summary, JsonNode? details = null, bool? fromHook = null, Usage? usage = null)
    {
        if (branchFromId is not null && !_byId.ContainsKey(branchFromId)) throw new InvalidOperationException($"Entry {branchFromId} not found");
        var fromId = _leafId ?? "root";
        _leafId = branchFromId;
        var entry = new BranchSummaryEntry
        {
            Id = NextId(), ParentId = branchFromId, Timestamp = NowIso(),
            FromId = fromId, Summary = summary, Details = details, Usage = usage, FromHook = fromHook,
        };
        AppendEntry(entry);
        return entry.Id;
    }

    /// <summary>Create a new session file containing only the path from root to the given leaf.</summary>
    public string? CreateBranchedSession(string leafId)
    {
        var previousSessionFile = _sessionFile;
        var path = GetBranch(leafId);
        if (path.Count == 0) throw new InvalidOperationException($"Entry {leafId} not found");

        var pathWithoutLabels = new List<SessionEntry>();
        var replacementByLabelId = new Dictionary<string, string>();
        var pendingLabelIds = new List<string>();
        string? pathParentId = null;
        foreach (var entry in path)
        {
            if (entry is LabelEntry)
            {
                pendingLabelIds.Add(entry.Id);
                continue;
            }
            foreach (var labelId in pendingLabelIds) replacementByLabelId[labelId] = entry.Id;
            pendingLabelIds.Clear();
            var copy = entry is UnknownSessionEntry unknown ? new UnknownSessionEntry((JsonObject)unknown.Raw.DeepClone()) : entry.ShallowCopy();
            copy.ParentId = pathParentId;
            if (copy is UnknownSessionEntry u) u.Raw["parentId"] = pathParentId;
            if (copy is CompactionEntry compaction && replacementByLabelId.TryGetValue(compaction.FirstKeptEntryId, out var replacement))
            {
                compaction.FirstKeptEntryId = replacement;
            }
            pathWithoutLabels.Add(copy);
            pathParentId = entry.Id;
        }

        var newSessionId = CreateSessionId();
        var timestamp = NowIso();
        var fileTimestamp = timestamp.Replace(':', '-').Replace('.', '-');
        var newSessionFile = Path.Combine(_sessionDir, $"{fileTimestamp}_{newSessionId}.jsonl");
        var header = new SessionHeader
        {
            Version = CurrentSessionVersion,
            Id = newSessionId,
            Timestamp = timestamp,
            Cwd = _cwd,
            ParentSession = _persist ? previousSessionFile : null,
        };

        var pathEntryIds = pathWithoutLabels.Select(e => e.Id).ToHashSet();
        var labelsToWrite = _labelsById.Where(kv => pathEntryIds.Contains(kv.Key))
            .Select(kv => (TargetId: kv.Key, Label: kv.Value, Timestamp: _labelTimestampsById[kv.Key])).ToList();

        var labelEntries = new List<LabelEntry>();
        var parentId = pathWithoutLabels.Count > 0 ? pathWithoutLabels[^1].Id : null;
        foreach (var (targetId, label, labelTimestamp) in labelsToWrite)
        {
            var labelEntry = new LabelEntry
            {
                Id = GenerateId(pathEntryIds.Contains),
                ParentId = parentId,
                Timestamp = labelTimestamp,
                TargetId = targetId,
                Label = label,
            };
            pathEntryIds.Add(labelEntry.Id);
            labelEntries.Add(labelEntry);
            parentId = labelEntry.Id;
        }

        _fileEntries = [header, .. pathWithoutLabels, .. labelEntries];
        _sessionId = newSessionId;
        if (_persist)
        {
            _sessionFile = newSessionFile;
            BuildIndex();
            if (_fileEntries.Any(e => e is SessionMessageEntry { Message: AssistantMessage }))
            {
                RewriteFile();
                _flushed = true;
            }
            else
            {
                _flushed = false;
            }
            return newSessionFile;
        }
        BuildIndex();
        return null;
    }

    // ---- factories ----

    public static SessionManager Create(string cwd, string? sessionDir = null, NewSessionOptions? options = null)
    {
        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : GetDefaultSessionDir(cwd);
        return new SessionManager(cwd, dir, null, true, options);
    }

    public static SessionManager Open(string path, string? sessionDir = null, string? cwdOverride = null)
    {
        var resolved = PathUtils.ResolvePath(path);
        SessionHeader? header = null;
        if (cwdOverride is null && File.Exists(resolved)) header = ReadSessionHeader(resolved);
        var cwd = cwdOverride ?? header?.Cwd ?? Directory.GetCurrentDirectory();
        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : Path.GetDirectoryName(resolved)!;
        return new SessionManager(cwd, dir, resolved, true);
    }

    public static SessionManager ContinueRecent(string cwd, string? sessionDir = null)
    {
        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : GetDefaultSessionDir(cwd);
        var filterCwd = sessionDir is not null && dir != GetDefaultSessionDirPath(cwd);
        var mostRecent = FindMostRecentSession(dir, filterCwd ? cwd : null);
        return mostRecent is not null ? new SessionManager(cwd, dir, mostRecent, true) : new SessionManager(cwd, dir, null, true);
    }

    public static SessionManager InMemory(string? cwd = null, NewSessionOptions? options = null, List<FileEntry>? entries = null) =>
        new(cwd ?? Directory.GetCurrentDirectory(), "", null, false, options, entries);

    public static SessionManager ForkFrom(string sourcePath, string targetCwd, string? sessionDir = null, NewSessionOptions? options = null)
    {
        var resolvedSource = PathUtils.ResolvePath(sourcePath);
        var resolvedTargetCwd = PathUtils.ResolvePath(targetCwd);
        var sourceEntries = LoadEntriesFromFile(resolvedSource);
        if (sourceEntries.Count == 0) throw new InvalidOperationException($"Cannot fork: source session file is empty or invalid: {resolvedSource}");
        if (!sourceEntries.OfType<SessionHeader>().Any()) throw new InvalidOperationException($"Cannot fork: source session has no header: {resolvedSource}");

        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : GetDefaultSessionDir(resolvedTargetCwd);
        Directory.CreateDirectory(dir);
        if (options?.Id is not null) AssertValidSessionId(options.Id);
        var newSessionId = options?.Id ?? CreateSessionId();
        var timestamp = NowIso();
        var newSessionFile = Path.Combine(dir, $"{timestamp.Replace(':', '-').Replace('.', '-')}_{newSessionId}.jsonl");
        var newHeader = new SessionHeader { Version = CurrentSessionVersion, Id = newSessionId, Timestamp = timestamp, Cwd = resolvedTargetCwd, ParentSession = resolvedSource };

        var sb = new StringBuilder();
        sb.Append(SerializeEntry(newHeader)).Append('\n');
        foreach (var entry in sourceEntries.Where(e => e is not SessionHeader)) sb.Append(SerializeEntry(entry)).Append('\n');
        using (var stream = new FileStream(newSessionFile, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(sb.ToString());
        }
        return new SessionManager(resolvedTargetCwd, dir, newSessionFile, true);
    }

    /// <summary>Find a session file by exact ID, reading only headers (no transcript bodies). Best-effort, like ListAsync.</summary>
    public static string? FindById(string cwd, string id, string? sessionDir = null)
    {
        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : GetDefaultSessionDir(cwd);
        var filterCwd = sessionDir is not null && dir != GetDefaultSessionDirPath(cwd);
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                var header = ReadSessionHeaderForDiscovery(path);
                if (header?.Id != id) continue;
                if (filterCwd && !SessionCwdMatches(header.Cwd, resolvedCwd)) continue;
                return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public static async Task<List<SessionInfo>> ListAsync(string cwd, string? sessionDir = null, Action<int, int>? onProgress = null)
    {
        var dir = sessionDir is not null ? PathUtils.NormalizePath(sessionDir) : GetDefaultSessionDir(cwd);
        var filterCwd = sessionDir is not null && dir != GetDefaultSessionDirPath(cwd);
        var resolvedCwd = PathUtils.ResolvePath(cwd);
        var sessions = (await ListSessionsFromDirAsync(dir, onProgress))
            .Where(s => !filterCwd || SessionCwdMatches(s.Cwd, resolvedCwd))
            .OrderByDescending(s => s.Modified)
            .ToList();
        return sessions;
    }

    private static async Task<List<SessionInfo>> ListSessionsFromDirAsync(string dir, Action<int, int>? onProgress)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.jsonl").ToList();
            var loaded = 0;
            return await BuildSessionInfosAsync(files, () => onProgress?.Invoke(Interlocked.Increment(ref loaded), files.Count));
        }
        catch
        {
            return [];
        }
    }

    public static async Task<List<SessionInfo>> ListAllAsync(string? sessionDir = null, Action<int, int>? onProgress = null)
    {
        if (sessionDir is not null)
        {
            return (await ListSessionsFromDirAsync(PathUtils.NormalizePath(sessionDir), onProgress)).OrderByDescending(s => s.Modified).ToList();
        }
        var sessionsDir = AppConfig.SessionsDir;
        try
        {
            if (!Directory.Exists(sessionsDir)) return [];
            var allFiles = Directory.EnumerateDirectories(sessionsDir)
                .SelectMany(d =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(d, "*.jsonl").ToList();
                    }
                    catch
                    {
                        return [];
                    }
                })
                .ToList();
            var loaded = 0;
            var sessions = await BuildSessionInfosAsync(allFiles, () => onProgress?.Invoke(Interlocked.Increment(ref loaded), allFiles.Count));
            return sessions.OrderByDescending(s => s.Modified).ToList();
        }
        catch
        {
            return [];
        }
    }
}
