using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SteamShortcutsImporter;

/// <summary>
/// Durable journal and manifest for automatic Steam shortcut synchronization.
/// The journal is authoritative for pending operations; the manifest is an atomic checkpoint.
/// </summary>
internal sealed class SyncQueueStore
{
    internal const string PendingStatus = "Pending";
    internal const string FilteredOutStatus = "FilteredOut";
    internal const string WaitingForSteamStatus = "WaitingForSteam";
    internal const string NeedsAttentionStatus = "NeedsAttention";
    internal const string RetryableFailureStatus = "RetryableFailure";
    internal const string CommittedStatus = "Committed";
    internal const string SupersededStatus = "Superseded";
    internal const string SyncedStatus = "Synced";

    private readonly object _sync = new object();
    private readonly ILogger? _logger;
    private readonly string _manifestPath;
    private readonly string _journalPath;
    private readonly List<SyncQueueEntry> _journal = new List<SyncQueueEntry>();
    private SyncQueueManifest _manifest;
    private bool _needsRecovery;

    public SyncQueueStore(string dataDirectory, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A queue data directory is required.", nameof(dataDirectory));
        }

        Directory.CreateDirectory(dataDirectory);
        _manifestPath = Path.Combine(dataDirectory, "sync-state.json");
        _journalPath = Path.Combine(dataDirectory, "sync-journal.jsonl");
        _logger = logger;
        _manifest = LoadManifest();
        LoadJournal();
    }

    public string DataDirectory => Path.GetDirectoryName(_manifestPath)!;

    public string SteamUserId
    {
        get { lock (_sync) return _manifest.SteamUserId ?? string.Empty; }
    }

    public bool InitialBaselineCompleted
    {
        get { lock (_sync) return _manifest.InitialBaselineCompleted; }
    }

    public bool WasLastSessionClean
    {
        get { lock (_sync) return _manifest.SessionClean; }
    }

    public bool NeedsRecovery
    {
        get { lock (_sync) return _needsRecovery; }
    }

    public long LastSequence
    {
        get { lock (_sync) return _manifest.LastSequence; }
    }

    public long Generation
    {
        get { lock (_sync) return _manifest.Generation; }
    }

    public int ArtworkSyncVersion
    {
        get { lock (_sync) return _manifest.ArtworkSyncVersion; }
    }

    public void SetArtworkSyncVersion(int version)
    {
        lock (_sync)
        {
            if (version <= _manifest.ArtworkSyncVersion) return;
            _manifest.ArtworkSyncVersion = version;
            SaveManifestLocked();
        }
    }

    public void SetSteamUserId(string? steamUserId)
    {
        lock (_sync)
        {
            var normalized = steamUserId ?? string.Empty;
            if (string.Equals(_manifest.SteamUserId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            if (_journal.Any(x => !IsTerminal(GetOperationState(x.Sequence).Status)))
            {
                throw new InvalidOperationException("Cannot change the Steam user while sync operations are pending.");
            }

            _manifest.SteamUserId = normalized;
            SaveManifestLocked();
        }
    }

    /// <summary>
    /// Binds a legacy auto-detect queue to one resolved Steam account. Journal entries are
    /// rewritten atomically so pending work remains account-specific after migration.
    /// </summary>
    public void BindSteamUserId(string steamUserId)
    {
        if (string.IsNullOrWhiteSpace(steamUserId))
        {
            throw new ArgumentException("A resolved Steam user ID is required.", nameof(steamUserId));
        }

        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_manifest.SteamUserId))
            {
                if (string.Equals(_manifest.SteamUserId, steamUserId, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("The existing sync queue is already bound to a different Steam user.");
            }

            var reboundEntries = _journal.Select(entry => entry.Clone()).ToList();
            foreach (var entry in reboundEntries)
            {
                if (!string.IsNullOrWhiteSpace(entry.SteamUserId) &&
                    !string.Equals(entry.SteamUserId, steamUserId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The legacy sync journal contains operations for a different Steam user.");
                }

                entry.SteamUserId = steamUserId;
            }

            if (reboundEntries.Count > 0) RewriteJournalLocked(reboundEntries);
            _journal.Clear();
            _journal.AddRange(reboundEntries);
            _manifest.SteamUserId = steamUserId;
            SaveManifestLocked();
        }
    }

    public void MarkSessionStarted()
    {
        lock (_sync)
        {
            _manifest.SessionClean = false;
            SaveManifestLocked();
        }
    }

    public void MarkSessionClean()
    {
        lock (_sync)
        {
            _manifest.SessionClean = true;
            SaveManifestLocked();
        }
    }

    public void MarkInitialBaselineCompleted()
    {
        lock (_sync)
        {
            if (_manifest.InitialBaselineCompleted)
            {
                return;
            }

            _manifest.InitialBaselineCompleted = true;
            _manifest.Generation++;
            SaveManifestLocked();
        }
    }

    /// <summary>
    /// Adds a batch in one durable journal write. Returns the number of new operations.
    /// </summary>
    public int EnqueueBatch(IEnumerable<SyncQueueEntry> entries)
    {
        if (entries == null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        lock (_sync)
        {
            var append = new List<SyncQueueEntry>();
            var nextSequence = _manifest.LastSequence;
            var observedChanged = false;
            var latestCandidateByGame = entries
                .Where(candidate => candidate != null && Guid.TryParse(candidate.PlayniteGameId, out var id) && id != Guid.Empty)
                .GroupBy(candidate => Guid.Parse(candidate.PlayniteGameId).ToString("D"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();

            foreach (var candidate in latestCandidateByGame)
            {
                if (candidate == null || !Guid.TryParse(candidate.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                var key = gameId.ToString("D");
                var artworkCheckRequested = !string.IsNullOrWhiteSpace(candidate.ArtworkCheckToken);
                if (!artworkCheckRequested && _manifest.Games.TryGetValue(key, out var record) &&
                    string.Equals(record.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal) &&
                    string.Equals(record.Status, SyncedStatus, StringComparison.Ordinal))
                {
                    observedChanged |= _manifest.ObservedGames.Remove(key);
                    continue;
                }

                var duplicate = _journal
                    .Where(x => string.Equals(x.PlayniteGameId, key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Sequence)
                    .FirstOrDefault(x => !IsTerminal(GetOperationState(x.Sequence).Status));
                if (duplicate != null &&
                    string.Equals(duplicate.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal) &&
                    string.Equals(duplicate.ArtworkCheckToken ?? string.Empty, candidate.ArtworkCheckToken ?? string.Empty, StringComparison.Ordinal))
                {
                    continue;
                }

                var latest = _journal
                    .Where(x => string.Equals(x.PlayniteGameId, key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Sequence)
                    .FirstOrDefault();
                if (latest != null &&
                    !artworkCheckRequested &&
                    string.Equals(latest.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal) &&
                    string.Equals(GetOperationState(latest.Sequence).Status, FilteredOutStatus, StringComparison.Ordinal))
                {
                    continue;
                }

                var operation = candidate.Clone();
                operation.Sequence = ++nextSequence;
                operation.PlayniteGameId = key;
                operation.SteamUserId = string.IsNullOrWhiteSpace(operation.SteamUserId) ? _manifest.SteamUserId : operation.SteamUserId;
                operation.EnqueuedUtc = operation.EnqueuedUtc == default ? DateTime.UtcNow : operation.EnqueuedUtc.ToUniversalTime();
                operation.InitialStatus = string.IsNullOrWhiteSpace(operation.InitialStatus) ? PendingStatus : operation.InitialStatus;
                append.Add(operation);
                if (!string.Equals(operation.InitialStatus, FilteredOutStatus, StringComparison.Ordinal))
                {
                    observedChanged |= _manifest.ObservedGames.Remove(key);
                }
            }

            if (append.Count == 0)
            {
                if (observedChanged) SaveManifestLocked();
                return 0;
            }

            AppendJournalLocked(append);
            foreach (var filtered in append.Where(x => string.Equals(x.InitialStatus, FilteredOutStatus, StringComparison.Ordinal)))
            {
                foreach (var previous in _journal.Where(x =>
                             string.Equals(x.PlayniteGameId, filtered.PlayniteGameId, StringComparison.OrdinalIgnoreCase) &&
                             x.Sequence < filtered.Sequence &&
                             !IsTerminal(GetOperationState(x.Sequence).Status)))
                {
                    var previousState = GetOperationState(previous.Sequence);
                    previousState.Status = SupersededStatus;
                    previousState.RetryAfterUtc = null;
                }
            }
            _journal.AddRange(append);
            _manifest.LastSequence = nextSequence;
            foreach (var operation in append)
            {
                _manifest.Operations[GetOperationKey(operation.Sequence)] = new SyncOperationState
                {
                    Status = operation.InitialStatus,
                    Error = operation.InitialError ?? string.Empty
                };
            }

            SaveManifestLocked();
            return append.Count;
        }
    }

    /// <summary>
    /// Remembers newly added selected-source games even when a filter currently excludes them,
    /// so a later installed/hidden/action update can be captured without scanning the library.
    /// </summary>
    public void ObserveGames(IEnumerable<(string GameId, string PlayniteSourceId)> games)
    {
        if (games == null) throw new ArgumentNullException(nameof(games));

        lock (_sync)
        {
            var changed = false;
            foreach (var item in games)
            {
                if (!Guid.TryParse(item.GameId, out var gameId) || gameId == Guid.Empty) continue;
                var key = gameId.ToString("D");
                if (_manifest.Games.ContainsKey(key)) continue;
                var latest = _journal
                    .Where(x => string.Equals(x.PlayniteGameId, key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Sequence)
                    .FirstOrDefault();
                if (latest != null && !string.Equals(GetOperationState(latest.Sequence).Status, FilteredOutStatus, StringComparison.Ordinal)) continue;
                var sourceId = item.PlayniteSourceId ?? string.Empty;
                if (!_manifest.ObservedGames.TryGetValue(key, out var existing) || !string.Equals(existing, sourceId, StringComparison.OrdinalIgnoreCase))
                {
                    _manifest.ObservedGames[key] = sourceId;
                    changed = true;
                }
            }

            if (changed) SaveManifestLocked();
        }
    }

    public bool IsGameTracked(string playniteGameId)
    {
        lock (_sync)
        {
            if (_manifest.Games.ContainsKey(playniteGameId) || _manifest.ObservedGames.ContainsKey(playniteGameId)) return true;
            return _journal.Any(x => string.Equals(x.PlayniteGameId, playniteGameId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Returns the newest runnable operation for each game. Older pending snapshots are superseded.
    /// </summary>
    public IReadOnlyList<SyncQueueEntry> GetPending(DateTime? nowUtc = null)
    {
        lock (_sync)
        {
            var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var changed = false;
            var latestByGame = _journal
                .GroupBy(x => x.PlayniteGameId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(x => x.Sequence).First())
                .ToDictionary(x => x.PlayniteGameId, x => x.Sequence, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _journal)
            {
                if (latestByGame.TryGetValue(entry.PlayniteGameId, out var latestSequence) &&
                    entry.Sequence < latestSequence && !IsTerminal(GetOperationState(entry.Sequence).Status))
                {
                    GetOperationState(entry.Sequence).Status = SupersededStatus;
                    GetOperationState(entry.Sequence).RetryAfterUtc = null;
                    changed = true;
                }
            }

            var runnable = _journal
                .Where(entry => latestByGame.TryGetValue(entry.PlayniteGameId, out var sequence) && sequence == entry.Sequence)
                .Where(entry =>
                {
                    var state = GetOperationState(entry.Sequence);
                    if (IsTerminal(state.Status)) return false;
                    if (state.Status == NeedsAttentionStatus) return false;

                    if (_manifest.Games.TryGetValue(entry.PlayniteGameId, out var record) &&
                        record.LastAppliedSequence >= entry.Sequence)
                    {
                        state.Status = CommittedStatus;
                        changed = true;
                        return false;
                    }

                    return !state.RetryAfterUtc.HasValue || state.RetryAfterUtc.Value.ToUniversalTime() <= now;
                })
                .OrderBy(x => GetOperationState(x.Sequence).Status == PendingStatus ? 0 : 1)
                .ThenBy(x => x.Sequence)
                .ToList();

            if (changed)
            {
                SaveManifestLocked();
            }

            return runnable.Select(x => x.Clone()).ToList();
        }
    }

    public DateTime? GetNextRetryUtc()
    {
        lock (_sync)
        {
            return _journal
                .Select(x => GetOperationState(x.Sequence))
                .Where(x => !IsTerminal(x.Status) && x.RetryAfterUtc.HasValue)
                .Select(x => x.RetryAfterUtc!.Value)
                .OrderBy(x => x)
                .Cast<DateTime?>()
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<SyncQueueStatusItem> GetStatusItems()
    {
        lock (_sync)
        {
            return _journal
                .OrderByDescending(x => x.Sequence)
                .Select(entry =>
                {
                    var state = GetOperationState(entry.Sequence);
                    return new SyncQueueStatusItem
                    {
                        Entry = entry.Clone(),
                        Status = state.Status,
                        Error = state.Error,
                        AttemptCount = state.AttemptCount
                    };
                })
                .ToList();
        }
    }

    public int RetryAllRecoverable()
    {
        lock (_sync)
        {
            var changed = 0;
            foreach (var entry in _journal)
            {
                var state = GetOperationState(entry.Sequence);
                if (IsTerminal(state.Status)) continue;
                var wasNeedsAttention = state.Status == NeedsAttentionStatus;
                state.Status = PendingStatus;
                state.RetryAfterUtc = null;
                state.Error = string.Empty;
                if (wasNeedsAttention) state.PreparedShortcut = null;
                changed++;
            }

            if (changed > 0) SaveManifestLocked();
            return changed;
        }
    }

    public void MarkRecoveryCompleted()
    {
        lock (_sync)
        {
            _needsRecovery = false;
        }
    }

    public void MarkWaitingForSteam(long sequence, DateTime? retryAfterUtc = null)
    {
        lock (_sync)
        {
            var state = GetOperationState(sequence);
            state.Status = WaitingForSteamStatus;
            state.RetryAfterUtc = (retryAfterUtc ?? DateTime.UtcNow.AddSeconds(5)).ToUniversalTime();
            state.Error = string.Empty;
            SaveManifestLocked();
        }
    }

    public void MarkRetryableFailure(long sequence, string error)
    {
        lock (_sync)
        {
            var state = GetOperationState(sequence);
            state.AttemptCount++;
            var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(state.AttemptCount, 8)));
            state.Status = RetryableFailureStatus;
            state.RetryAfterUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            state.Error = error ?? string.Empty;
            SaveManifestLocked();
        }
    }

    public void MarkNeedsAttention(long sequence, string error)
    {
        lock (_sync)
        {
            var state = GetOperationState(sequence);
            state.Status = NeedsAttentionStatus;
            state.RetryAfterUtc = null;
            state.Error = error ?? string.Empty;
            state.PreparedShortcut = null;
            SaveManifestLocked();
        }
    }

    public SyncPreparedShortcut? GetPreparedShortcut(long sequence)
    {
        lock (_sync)
        {
            var prepared = GetOperationState(sequence).PreparedShortcut;
            return prepared == null ? null : ClonePrepared(prepared);
        }
    }

    public void MarkPrepared(long sequence, SyncPreparedShortcut shortcut)
    {
        if (shortcut == null) throw new ArgumentNullException(nameof(shortcut));

        lock (_sync)
        {
            var state = GetOperationState(sequence);
            state.Status = "Prepared";
            state.RetryAfterUtc = null;
            state.Error = string.Empty;
            state.PreparedShortcut = ClonePrepared(shortcut);
            SaveManifestLocked();
        }
    }

    public bool Retry(long sequence)
    {
        lock (_sync)
        {
            if (!_journal.Any(x => x.Sequence == sequence))
            {
                return false;
            }

            var state = GetOperationState(sequence);
            if (state.Status == CommittedStatus || state.Status == SupersededStatus || state.Status == FilteredOutStatus)
            {
                return false;
            }

            var wasNeedsAttention = state.Status == NeedsAttentionStatus;
            state.Status = PendingStatus;
            state.RetryAfterUtc = null;
            state.Error = string.Empty;
            if (wasNeedsAttention) state.PreparedShortcut = null;
            SaveManifestLocked();
            return true;
        }
    }

    public void MarkCommitted(SyncQueueEntry operation, uint appId)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));

        lock (_sync)
        {
            if (!Guid.TryParse(operation.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
            {
                throw new InvalidOperationException("Queue operation has an invalid Playnite game ID.");
            }

            var key = gameId.ToString("D");
            _manifest.Games[key] = new SyncGameRecord
            {
                PlayniteGameId = key,
                SourcePluginId = operation.SourcePluginId,
                PlayniteSourceId = operation.PlayniteSourceId,
                GameName = operation.GameName,
                Fingerprint = operation.Fingerprint,
                AppId = appId,
                LastAppliedSequence = operation.Sequence,
                Status = SyncedStatus,
                LastError = string.Empty
            };

            var state = GetOperationState(operation.Sequence);
            state.Status = CommittedStatus;
            state.RetryAfterUtc = null;
            state.Error = string.Empty;
            _manifest.Generation++;
            SaveManifestLocked();
            CompactJournalIfNeededLocked();
        }
    }

    /// <summary>Records a manually exported batch and optionally completes the initial baseline in one checkpoint.</summary>
    public void RecordManualExports(IEnumerable<SyncQueueEntry> entries, bool establishInitialBaseline)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));

        var validEntries = entries
            .Where(entry => entry != null && Guid.TryParse(entry.PlayniteGameId, out var gameId) && gameId != Guid.Empty)
            .ToList();
        if (validEntries.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var entry in validEntries)
            {
                if (!Guid.TryParse(entry.PlayniteGameId, out var gameId) || gameId == Guid.Empty) continue;
                var key = gameId.ToString("D");
                _manifest.Games[key] = new SyncGameRecord
                {
                    PlayniteGameId = key,
                    SourcePluginId = entry.SourcePluginId,
                    PlayniteSourceId = entry.PlayniteSourceId,
                    GameName = entry.GameName,
                    Fingerprint = entry.Fingerprint,
                    AppId = entry.AppId,
                    LastAppliedSequence = _manifest.LastSequence,
                    Status = SyncedStatus,
                    LastError = string.Empty
                };
                _manifest.ObservedGames.Remove(key);
            }

            if (establishInitialBaseline) _manifest.InitialBaselineCompleted = true;
            _manifest.Generation++;
            SaveManifestLocked();
        }
    }

    public SyncGameRecord? GetGameRecord(string playniteGameId)
    {
        lock (_sync)
        {
            if (_manifest.Games.TryGetValue(playniteGameId, out var record))
            {
                return new SyncGameRecord
                {
                    PlayniteGameId = record.PlayniteGameId,
                    SourcePluginId = record.SourcePluginId,
                    PlayniteSourceId = record.PlayniteSourceId,
                    GameName = record.GameName,
                    Fingerprint = record.Fingerprint,
                    AppId = record.AppId,
                    LastAppliedSequence = record.LastAppliedSequence,
                    Status = record.Status,
                    LastError = record.LastError
                };
            }

            return null;
        }
    }

    private SyncQueueManifest LoadManifest()
    {
        if (TryReadManifest(_manifestPath, out var manifest)) return manifest!;

        var backupPath = _manifestPath + ".bak";
        if (TryReadManifest(backupPath, out manifest))
        {
            _needsRecovery = true;
            _logger?.Warn("Automatic sync manifest was recovered from its backup.");
            return manifest!;
        }

        if (File.Exists(_manifestPath) || File.Exists(backupPath))
        {
            _needsRecovery = true;
            _logger?.Warn("Automatic sync manifest and its backup could not be read; journal replay will rebuild state.");
        }

        return new SyncQueueManifest();
    }

    private bool TryReadManifest(string path, out SyncQueueManifest? manifest)
    {
        manifest = null;
        if (!File.Exists(path)) return false;

        try
        {
            using var stream = File.OpenRead(path);
            manifest = Deserialize<SyncQueueManifest>(stream);
            manifest.Games ??= new Dictionary<string, SyncGameRecord>(StringComparer.OrdinalIgnoreCase);
            manifest.Operations ??= new Dictionary<string, SyncOperationState>(StringComparer.OrdinalIgnoreCase);
            manifest.ObservedGames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return manifest.SchemaVersion == 1;
        }
        catch (Exception ex)
        {
            _logger?.Warn(ex, $"Failed to read automatic sync manifest '{path}'.");
            return false;
        }
    }

    private void LoadJournal()
    {
        if (!File.Exists(_journalPath)) return;

        var parsed = new List<SyncQueueEntry>();
        var sequences = new HashSet<long>();
        var malformed = false;
        foreach (var line in File.ReadLines(_journalPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
                var entry = Deserialize<SyncQueueEntry>(stream);
                if (entry.Sequence <= 0 || !Guid.TryParse(entry.PlayniteGameId, out var id) || id == Guid.Empty || !sequences.Add(entry.Sequence))
                {
                    malformed = true;
                    continue;
                }

                entry.PlayniteGameId = id.ToString("D");
                entry.Tags ??= new List<string>();
                parsed.Add(entry);
            }
            catch (Exception ex)
            {
                malformed = true;
                _logger?.Warn(ex, "Ignoring a malformed automatic sync journal record.");
            }
        }

        _journal.AddRange(parsed.OrderBy(x => x.Sequence));
        var highestSequence = _journal.Select(x => x.Sequence).DefaultIfEmpty(0).Max();
        var changed = highestSequence > _manifest.LastSequence;
        _manifest.LastSequence = Math.Max(_manifest.LastSequence, highestSequence);
        foreach (var entry in _journal)
        {
            var key = GetOperationKey(entry.Sequence);
            if (!_manifest.Operations.ContainsKey(key))
            {
                _manifest.Operations[key] = new SyncOperationState
                {
                    Status = string.IsNullOrWhiteSpace(entry.InitialStatus) ? PendingStatus : entry.InitialStatus,
                    Error = entry.InitialError ?? string.Empty
                };
                changed = true;
            }
        }

        if (malformed)
        {
            _needsRecovery = true;
            RewriteJournalLocked(_journal);
        }

        if (changed || malformed)
        {
            SaveManifestLocked();
        }
    }

    private void AppendJournalLocked(IEnumerable<SyncQueueEntry> entries)
    {
        var directory = Path.GetDirectoryName(_journalPath)!;
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true);
        foreach (var entry in entries)
        {
            writer.WriteLine(Serialize(entry));
        }
        writer.Flush();
        stream.Flush(true);
    }

    private void SaveManifestLocked()
    {
        var tempPath = _manifestPath + ".tmp";
        var backupPath = _manifestPath + ".bak";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(SyncQueueManifest)).WriteObject(stream, _manifest);
                stream.Flush(true);
            }

            if (File.Exists(_manifestPath))
            {
                File.Replace(tempPath, _manifestPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, _manifestPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void CompactJournalIfNeededLocked()
    {
        if (_journal.Count < 128) return;

        var active = _journal
            .Where(x => !IsTerminal(GetOperationState(x.Sequence).Status))
            .ToList();
        var latestFilteredOutByGame = _journal
            .GroupBy(x => x.PlayniteGameId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Sequence).First())
            .Where(entry => string.Equals(GetOperationState(entry.Sequence).Status, FilteredOutStatus, StringComparison.Ordinal))
            .ToList();

        // Keep the latest filtered snapshot per game as a durable deduplication tombstone.
        // Without it, replaying the same filtered game after compaction would allocate a new
        // sequence every time that tracked game receives another update.
        var retainedBySequence = new Dictionary<long, SyncQueueEntry>();
        foreach (var entry in active) retainedBySequence[entry.Sequence] = entry;
        foreach (var entry in latestFilteredOutByGame) retainedBySequence[entry.Sequence] = entry;
        var retained = retainedBySequence.Values.OrderBy(x => x.Sequence).ToList();
        if (retained.Count * 2 > _journal.Count) return;

        RewriteJournalLocked(retained);
        _journal.Clear();
        _journal.AddRange(retained);
        var retainedKeys = new HashSet<string>(retained.Select(x => GetOperationKey(x.Sequence)), StringComparer.OrdinalIgnoreCase);
        foreach (var key in _manifest.Operations.Keys.ToList())
        {
            if (!retainedKeys.Contains(key)) _manifest.Operations.Remove(key);
        }
        SaveManifestLocked();
    }

    private void RewriteJournalLocked(IEnumerable<SyncQueueEntry> entries)
    {
        var tempPath = _journalPath + ".tmp";
        var backupPath = _journalPath + ".bak";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
            {
                foreach (var entry in entries.OrderBy(x => x.Sequence)) writer.WriteLine(Serialize(entry));
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(_journalPath))
            {
                File.Replace(tempPath, _journalPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, _journalPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private SyncOperationState GetOperationState(long sequence)
    {
        var key = GetOperationKey(sequence);
        if (!_manifest.Operations.TryGetValue(key, out var state))
        {
            state = new SyncOperationState();
            _manifest.Operations[key] = state;
        }
        return state;
    }

    private static SyncPreparedShortcut ClonePrepared(SyncPreparedShortcut shortcut)
    {
        return new SyncPreparedShortcut
        {
            AppId = shortcut.AppId,
            AppName = shortcut.AppName,
            Exe = shortcut.Exe,
            StartDir = shortcut.StartDir,
            LaunchOptions = shortcut.LaunchOptions,
            Icon = shortcut.Icon,
            Tags = new List<string>(shortcut.Tags ?? new List<string>())
        };
    }

    private static bool IsTerminal(string status)
    {
        return status == CommittedStatus || status == SupersededStatus || status == FilteredOutStatus;
    }

    private static string GetOperationKey(long sequence) => sequence.ToString(CultureInfo.InvariantCulture);

    private static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static T Deserialize<T>(Stream stream)
    {
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return Deserialize<T>(buffered);
        }

        var startPosition = stream.Position;
        var prefix = new byte[3];
        var prefixLength = stream.Read(prefix, 0, prefix.Length);
        if (prefixLength != prefix.Length || prefix[0] != 0xEF || prefix[1] != 0xBB || prefix[2] != 0xBF)
        {
            stream.Position = startPosition;
        }

        var value = new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        if (value is not T result) throw new InvalidDataException($"Journal record is not a {typeof(T).Name}.");
        return result;
    }
}
