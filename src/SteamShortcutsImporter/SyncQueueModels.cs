using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SteamShortcutsImporter;

[DataContract]
internal sealed class SyncQueueEntry
{
    [DataMember(Order = 1)] public long Sequence { get; set; }
    [DataMember(Order = 2)] public string PlayniteGameId { get; set; } = string.Empty;
    [DataMember(Order = 3)] public string SourcePluginId { get; set; } = string.Empty;
    [DataMember(Order = 4)] public string GameName { get; set; } = string.Empty;
    [DataMember(Order = 5)] public string Fingerprint { get; set; } = string.Empty;
    [DataMember(Order = 6)] public string SteamUserId { get; set; } = string.Empty;
    [DataMember(Order = 7)] public uint AppId { get; set; }
    [DataMember(Order = 8)] public string Exe { get; set; } = string.Empty;
    [DataMember(Order = 9)] public string StartDir { get; set; } = string.Empty;
    [DataMember(Order = 10)] public string LaunchOptions { get; set; } = string.Empty;
    [DataMember(Order = 11)] public string Icon { get; set; } = string.Empty;
    [DataMember(Order = 12)] public List<string> Tags { get; set; } = new List<string>();
    [DataMember(Order = 13)] public DateTime EnqueuedUtc { get; set; } = DateTime.UtcNow;
    [DataMember(Order = 14)] public string InitialStatus { get; set; } = SyncQueueStore.PendingStatus;
    [DataMember(Order = 15)] public string InitialError { get; set; } = string.Empty;
    [DataMember(Order = 16)] public string InstallDirectory { get; set; } = string.Empty;
    [DataMember(Order = 17)] public string ActionType { get; set; } = string.Empty;
    [DataMember(Order = 18)] public string ActionPath { get; set; } = string.Empty;
    [DataMember(Order = 19)] public string ActionArguments { get; set; } = string.Empty;
    [DataMember(Order = 20)] public string ActionWorkingDirectory { get; set; } = string.Empty;
    [DataMember(Order = 21)] public string EmulatorId { get; set; } = string.Empty;
    [DataMember(Order = 22)] public string EmulatorProfileId { get; set; } = string.Empty;
    [DataMember(Order = 23)] public string EmulatorAdditionalArguments { get; set; } = string.Empty;
    [DataMember(Order = 24)] public bool EmulatorOverrideDefaultArgs { get; set; }
    [DataMember(Order = 25)] public bool IsInstalled { get; set; }
    [DataMember(Order = 26)] public bool IsHidden { get; set; }
    [DataMember(Order = 27)] public string CoverImage { get; set; } = string.Empty;
    [DataMember(Order = 28)] public string GameExternalId { get; set; } = string.Empty;
    [DataMember(Order = 29)] public List<SyncGameRom> Roms { get; set; } = new List<SyncGameRom>();
    [DataMember(Order = 30)] public string PlayniteSourceId { get; set; } = string.Empty;
    [DataMember(Order = 31)] public string BackgroundImage { get; set; } = string.Empty;
    [DataMember(Order = 32)] public string ArtworkCheckToken { get; set; } = string.Empty;

    public SyncQueueEntry Clone()
    {
        return new SyncQueueEntry
        {
            Sequence = Sequence,
            PlayniteGameId = PlayniteGameId,
            SourcePluginId = SourcePluginId,
            GameName = GameName,
            Fingerprint = Fingerprint,
            SteamUserId = SteamUserId,
            AppId = AppId,
            Exe = Exe,
            StartDir = StartDir,
            LaunchOptions = LaunchOptions,
            Icon = Icon,
            Tags = new List<string>(Tags ?? new List<string>()),
            EnqueuedUtc = EnqueuedUtc,
            InitialStatus = InitialStatus,
            InitialError = InitialError,
            InstallDirectory = InstallDirectory,
            ActionType = ActionType,
            ActionPath = ActionPath,
            ActionArguments = ActionArguments,
            ActionWorkingDirectory = ActionWorkingDirectory,
            EmulatorId = EmulatorId,
            EmulatorProfileId = EmulatorProfileId,
            EmulatorAdditionalArguments = EmulatorAdditionalArguments,
            EmulatorOverrideDefaultArgs = EmulatorOverrideDefaultArgs,
            IsInstalled = IsInstalled,
            IsHidden = IsHidden,
            CoverImage = CoverImage,
            GameExternalId = GameExternalId,
            Roms = (Roms ?? new List<SyncGameRom>()).Select(rom => rom.Clone()).ToList(),
            PlayniteSourceId = PlayniteSourceId,
            BackgroundImage = BackgroundImage,
            ArtworkCheckToken = ArtworkCheckToken ?? string.Empty
        };
    }
}

[DataContract]
internal sealed class SyncGameRom
{
    [DataMember(Order = 1)] public string Name { get; set; } = string.Empty;
    [DataMember(Order = 2)] public string Path { get; set; } = string.Empty;

    public SyncGameRom Clone()
    {
        return new SyncGameRom { Name = Name, Path = Path };
    }
}

[DataContract]
internal sealed class SyncQueueManifest
{
    [DataMember(Order = 1)] public int SchemaVersion { get; set; } = 1;
    [DataMember(Order = 2)] public long LastSequence { get; set; }
    [DataMember(Order = 3)] public long Generation { get; set; }
    [DataMember(Order = 4)] public bool InitialBaselineCompleted { get; set; }
    [DataMember(Order = 5)] public bool SessionClean { get; set; } = true;
    [DataMember(Order = 6)] public string SteamUserId { get; set; } = string.Empty;
    [DataMember(Order = 7)] public Dictionary<string, SyncGameRecord> Games { get; set; } = new Dictionary<string, SyncGameRecord>(StringComparer.OrdinalIgnoreCase);
    [DataMember(Order = 8)] public Dictionary<string, SyncOperationState> Operations { get; set; } = new Dictionary<string, SyncOperationState>(StringComparer.OrdinalIgnoreCase);
    [DataMember(Order = 9)] public Dictionary<string, string> ObservedGames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    [DataMember(Order = 10)] public int ArtworkSyncVersion { get; set; }
}

[DataContract]
internal sealed class SyncGameRecord
{
    [DataMember(Order = 1)] public string PlayniteGameId { get; set; } = string.Empty;
    [DataMember(Order = 2)] public string SourcePluginId { get; set; } = string.Empty;
    [DataMember(Order = 3)] public string GameName { get; set; } = string.Empty;
    [DataMember(Order = 4)] public string Fingerprint { get; set; } = string.Empty;
    [DataMember(Order = 5)] public uint AppId { get; set; }
    [DataMember(Order = 6)] public long LastAppliedSequence { get; set; }
    [DataMember(Order = 7)] public string Status { get; set; } = SyncQueueStore.SyncedStatus;
    [DataMember(Order = 8)] public string LastError { get; set; } = string.Empty;
    [DataMember(Order = 9)] public string PlayniteSourceId { get; set; } = string.Empty;
}

[DataContract]
internal sealed class SyncOperationState
{
    [DataMember(Order = 1)] public string Status { get; set; } = SyncQueueStore.PendingStatus;
    [DataMember(Order = 2)] public int AttemptCount { get; set; }
    [DataMember(Order = 3)] public DateTime? RetryAfterUtc { get; set; }
    [DataMember(Order = 4)] public string Error { get; set; } = string.Empty;
    [DataMember(Order = 5)] public SyncPreparedShortcut? PreparedShortcut { get; set; }
}

[DataContract]
internal sealed class SyncPreparedShortcut
{
    [DataMember(Order = 1)] public uint AppId { get; set; }
    [DataMember(Order = 2)] public string AppName { get; set; } = string.Empty;
    [DataMember(Order = 3)] public string Exe { get; set; } = string.Empty;
    [DataMember(Order = 4)] public string StartDir { get; set; } = string.Empty;
    [DataMember(Order = 5)] public string LaunchOptions { get; set; } = string.Empty;
    [DataMember(Order = 6)] public string Icon { get; set; } = string.Empty;
    [DataMember(Order = 7)] public List<string> Tags { get; set; } = new List<string>();
}

internal sealed class SyncQueueStatusItem
{
    public SyncQueueEntry Entry { get; set; } = new SyncQueueEntry();
    public string Status { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
}
