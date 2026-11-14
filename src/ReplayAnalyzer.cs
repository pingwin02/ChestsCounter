using System.Globalization;
using System.Reflection;
using FortniteReplayReader;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestsCounter;

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C", ParseMode.Minimal)]
public sealed class ChestExport : INetFieldExportGroup
{
    [NetFieldExport("bAlreadySearched", RepLayoutCmdType.PropertyBool)]
    public bool? bAlreadySearched { get; set; }

    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }

    [NetFieldExport("SearchAnimationCount", RepLayoutCmdType.PropertyUInt32)]
    public uint? SearchAnimationCount { get; set; }
}

public sealed record ChestState(bool Searched, uint? PawnGuid, uint AnimCount);

public sealed record PlayerStateInfo(int? PlayerId, int? Team);

public sealed record PlayerChestStat(string Key, string Name, bool IsOwner, int Chests);

public sealed record MatchResult(
    string FileName,
    DateTime Day,
    long DurationMs,
    int OwnerOpened,
    int TeammatesOpened,
    string Status,
    List<PlayerChestStat> Players,
    long FileLength = 0
);

internal sealed class SkipBunchException : Exception { }
internal sealed class SkipPacketException : Exception { }
internal sealed class StopChunkException : Exception { }

public class SafeChestReplayReader : ReplayReader
{
    private int _lastBits = -1;
    private int _sameBitsCount;
    private int _iloCount;
    private uint _lastChIndex = uint.MaxValue;
    private int _lastChSeq = -1;
    private int _lastBunchBits = -1;
    private int _sameBunchCount;
    private int _totalBunchCalls;

    public Dictionary<uint, uint> ChannelToActor { get; } = new();
    public Dictionary<uint, uint> PawnToPlayerState { get; } = new();
    public Dictionary<uint, PlayerStateInfo> PlayerStates { get; } = new();
    public Dictionary<uint, ChestState> Chests { get; } = new();

    public SafeChestReplayReader() : base(parseMode: ParseMode.Minimal)
    {
        RegisterChestGroups();
    }

    private void RegisterChestGroups()
    {
        var baseType = typeof(ReplayReader<FortniteReplayReader.Models.FortniteReplay>);
        var parser = baseType.GetField("_netFieldParser", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(this)!;
        var guidCache = baseType.GetField("_netGuidCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(this)!;

        var ctor = typeof(NetFieldParser).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)[0];
        var ctorArgs = ctor.GetParameters().Length == 4
            ? new object?[] { guidCache, ParseMode.Minimal, Assembly.GetExecutingAssembly().GetName().Name, null }
            : new object?[] { guidCache, ParseMode.Minimal, Assembly.GetExecutingAssembly().GetName().Name };
        var customParser = ctor.Invoke(ctorArgs);

        var nfgField = typeof(NetFieldParser).GetField("NetFieldGroups", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var targetNfg = nfgField.GetValue(parser)!;
        var sourceNfg = nfgField.GetValue(customParser)!;

        var objectsField = typeof(NetFieldParser).GetField("_objects", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var targetObjects = (Array)objectsField.GetValue(parser)!;
        var sourceObjects = (Array)objectsField.GetValue(customParser)!;

        var infoType = sourceNfg.GetType().GetGenericArguments()[1];
        var tryGet = sourceNfg.GetType().GetMethod("TryGetValue", new[] { typeof(string), infoType.MakeByRefType() })!;
        var addMethod = targetNfg.GetType().GetMethod("Add")!;

        const string primaryPath = "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";
        var args = new object?[] { primaryPath, null };
        if (!(bool)tryGet.Invoke(sourceNfg, args)!)
        {
            return;
        }

        var info = args[1]!;
        int newTypeId = targetObjects.Length;
        infoType.GetProperty("TypeId")!.SetValue(info, newTypeId);

        var elemType = targetObjects.GetType().GetElementType()!;
        var newObjects = Array.CreateInstance(elemType, newTypeId + 1);
        Array.Copy(targetObjects, newObjects, targetObjects.Length);
        newObjects.SetValue(sourceObjects.GetValue(0), newTypeId);
        objectsField.SetValue(parser, newObjects);

        var groupPaths = new[]
        {
            primaryPath,
            "/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_NormalChest.AlwaysSpawn_NormalChest_C",
            "/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C",
            "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_5_Child.Tiered_Chest_5_Child_C",
            "/Game/Athena/Items/ForagedItems/BuriedChest/B_Athena_BuriedChest_Chest.B_Athena_BuriedChest_Chest_C",
            "/RelicChest/Gameplay/B_TieredChest_Relics.B_TieredChest_Relics_C"
        };

        foreach (var path in groupPaths)
        {
            addMethod.Invoke(targetNfg, new object[] { path, info });
        }
    }

    public override void ReadNetExportGuids(FArchive archive)
    {
        _lastBits = -1;
        _sameBitsCount = 0;
        _iloCount = 0;
        try
        {
            base.ReadNetExportGuids(archive);
        }
        catch (SkipBunchException)
        {
        }
    }

    public override void ReadReplayData(FArchive archive, int fallbackChunkSize)
    {
        try
        {
            base.ReadReplayData(archive, fallbackChunkSize);
        }
        catch (StopChunkException)
        {
        }
    }

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        try
        {
            base.ReadDemoFrameIntoPlaybackPackets(archive);
        }
        catch (SkipPacketException)
        {
        }
    }

    public override void ProcessBunch(DataBunch bunch)
    {
        if (++_totalBunchCalls > 500000)
        {
            throw new StopChunkException();
        }

        int bunchBits = bunch.Archive.GetBitsLeft();
        if (bunch.ChIndex == _lastChIndex && bunch.ChSequence == _lastChSeq && bunchBits == _lastBunchBits)
        {
            if (++_sameBunchCount > 10)
            {
                throw new SkipPacketException();
            }
        }
        else
        {
            _lastChIndex = bunch.ChIndex;
            _lastChSeq = bunch.ChSequence;
            _lastBunchBits = bunchBits;
            _sameBunchCount = 0;
        }

        _lastBits = -1;
        _sameBitsCount = 0;
        _iloCount = 0;
        try
        {
            base.ProcessBunch(bunch);
        }
        catch (SkipBunchException)
        {
        }
        catch (IndexOutOfRangeException)
        {
        }
    }

    private bool IsStuckBits(int bits, int limit)
    {
        if (bits == _lastBits)
        {
            _sameBitsCount++;
            return _sameBitsCount > limit;
        }
        _lastBits = bits;
        _sameBitsCount = 0;
        return false;
    }

    private void GuardStuckBits(int bits, int limit)
    {
        if (IsStuckBits(bits, limit))
        {
            throw new SkipBunchException();
        }
    }

    public override bool NetDeltaSerialize(FBitArchive reader, NetFieldExportGroup group, uint channelIndex, bool enablePropertyChecksum)
    {
        GuardStuckBits(reader.GetBitsLeft(), 8);
        return base.NetDeltaSerialize(reader, group, channelIndex, enablePropertyChecksum);
    }

    public override void ReceiveNetFieldExportsCompat(FBitArchive bitArchive)
    {
        int pos = bitArchive.Position;
        uint count = bitArchive.ReadIntPacked();
        if (bitArchive.IsError || count > 10000 || count > (uint)bitArchive.GetBitsLeft())
        {
            throw new SkipBunchException();
        }
        bitArchive.Seek(pos);
        base.ReceiveNetFieldExportsCompat(bitArchive);
    }

    public override NetworkGUID InternalLoadObject(FArchive archive, bool isExportingNetGUIDBunch, int internalLoadObjectRecursionCount)
    {
        if (++_iloCount > 2000 || internalLoadObjectRecursionCount > 16 || (archive is FBitArchive ba && IsStuckBits(ba.GetBitsLeft(), 16)))
        {
            throw new SkipBunchException();
        }
        return base.InternalLoadObject(archive, isExportingNetGUIDBunch, internalLoadObjectRecursionCount);
    }

    public override uint? ReadContentBlockHeader(DataBunch bunch, out bool bOutHasRepLayout, out bool bObjectDeleted)
    {
        GuardStuckBits(bunch.Archive.GetBitsLeft(), 4);
        return base.ReadContentBlockHeader(bunch, out bOutHasRepLayout, out bObjectDeleted);
    }

    public override uint? ReadContentBlockPayload(DataBunch bunch, out bool bObjectDeleted, out bool bOutHasRepLayout, out uint? payload)
    {
        GuardStuckBits(bunch.Archive.GetBitsLeft(), 4);
        return base.ReadContentBlockPayload(bunch, out bObjectDeleted, out bOutHasRepLayout, out payload);
    }

    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        if (actor != null)
        {
            ChannelToActor[channelIndex] = actor.Value;
        }
        base.OnChannelOpened(channelIndex, actor);
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        if (exportGroup is PlayerPawn pawn && pawn.PlayerState is uint psVal && psVal != 0 && ChannelToActor.TryGetValue(channelIndex, out var pawnGuid))
        {
            PawnToPlayerState[pawnGuid] = psVal;
        }
        else if (exportGroup is FortPlayerState ps && ChannelToActor.TryGetValue(channelIndex, out var psGuid))
        {
            var existing = PlayerStates.GetValueOrDefault(psGuid) ?? new PlayerStateInfo(null, null);
            PlayerStates[psGuid] = new PlayerStateInfo(ps.PlayerID ?? existing.PlayerId, ps.TeamIndex ?? existing.Team);
        }
        else if (exportGroup is ChestExport chest && ChannelToActor.TryGetValue(channelIndex, out var chestGuid))
        {
            var cur = Chests.GetValueOrDefault(chestGuid) ?? new ChestState(false, null, 0);
            bool searched = chest.bAlreadySearched ?? cur.Searched;
            uint? pGuid = (chest.SearchingPawn is uint sp && sp != 0) ? sp : cur.PawnGuid;
            uint anim = chest.SearchAnimationCount ?? cur.AnimCount;
            if (searched || pGuid != null || anim > 0)
            {
                Chests[chestGuid] = new ChestState(searched, pGuid, anim);
            }
        }
        base.OnExportRead(channelIndex, exportGroup);
    }
}

public static class ReplayAnalyzer
{
    public static DateTime ParseFileDate(string file)
    {
        var name = Path.GetFileName(file);
        if (name.StartsWith("UnsavedReplay-", StringComparison.Ordinal) && name.Length >= 33)
        {
            if (DateTime.TryParseExact(name.Substring(14, 19), "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return dt;
            }
        }
        return File.GetLastWriteTime(file);
    }

    public static (DateTime Day, long DurationMs) ReadHeaderFallback(string file)
    {
        var fallbackDate = ParseFileDate(file).Date;
        try
        {
            var reader = new ReplayReader(parseMode: ParseMode.EventsOnly);
            var replay = reader.ReadReplay(file);
            var day = replay.Info?.Timestamp.Date ?? fallbackDate;
            long ms = replay.Info?.LengthInMs ?? 0;
            return (day, ms);
        }
        catch
        {
            return (fallbackDate, 0);
        }
    }

    public static MatchResult AnalyzeFile(string file)
    {
        var name = Path.GetFileName(file);
        var fallback = ReadHeaderFallback(file);
        try
        {
            var reader = new SafeChestReplayReader();
            var replay = reader.ReadReplay(file);
            long ms = replay.Info?.LengthInMs ?? fallback.DurationMs;
            var day = replay.Info?.Timestamp.Date ?? fallback.Day;

            var owner = replay.PlayerData?.FirstOrDefault(p => p.IsReplayOwner);
            uint? ownerPsGuid = replay.GameData?.RecorderId;
            if (ownerPsGuid == null && owner != null)
            {
                var match = reader.PlayerStates.FirstOrDefault(kv => kv.Value.PlayerId == owner.Id);
                if (match.Key != 0)
                {
                    ownerPsGuid = match.Key;
                }
            }

            int? ownerTeam = owner?.TeamIndex;
            if (ownerTeam == null && ownerPsGuid != null && reader.PlayerStates.TryGetValue(ownerPsGuid.Value, out var ops))
            {
                ownerTeam = ops.Team;
            }

            var playerById = new Dictionary<int, FortniteReplayReader.Models.PlayerData>();
            if (replay.PlayerData != null)
            {
                foreach (var p in replay.PlayerData)
                {
                    if (p.Id is int pid)
                    {
                        playerById[pid] = p;
                    }
                }
            }

            int ownerOpened = 0;
            int teammatesOpened = 0;
            var perPlayer = new Dictionary<string, (string Name, bool IsOwner, int Chests)>();

            foreach (var chest in reader.Chests.Values)
            {
                if (!chest.Searched && chest.AnimCount == 0)
                {
                    continue;
                }

                if (chest.PawnGuid is uint pawnGuid && reader.PawnToPlayerState.TryGetValue(pawnGuid, out var psGuid))
                {
                    if (reader.PlayerStates.TryGetValue(psGuid, out var psInfo) && ownerTeam != null && psInfo.Team == ownerTeam)
                    {
                        bool isOwner = ownerPsGuid != null && psGuid == ownerPsGuid.Value;
                        if (isOwner)
                        {
                            ownerOpened++;
                        }
                        else
                        {
                            teammatesOpened++;
                        }

                        FortniteReplayReader.Models.PlayerData? pd = null;
                        if (psInfo.PlayerId is int pid)
                        {
                            playerById.TryGetValue(pid, out pd);
                        }
                        if (pd == null && isOwner)
                        {
                            pd = owner;
                        }

                        string key = !string.IsNullOrWhiteSpace(pd?.EpicId)
                            ? pd.EpicId
                            : (!string.IsNullOrWhiteSpace(pd?.PlayerName)
                                ? pd.PlayerName
                                : (isOwner ? "OWNER" : $"PS_{psGuid}"));
                        string playerName = !string.IsNullOrWhiteSpace(pd?.PlayerName)
                            ? pd.PlayerName
                            : (isOwner ? "You" : $"Teammate ({psGuid})");

                        if (perPlayer.TryGetValue(key, out var existing))
                        {
                            perPlayer[key] = (existing.Name, existing.IsOwner || isOwner, existing.Chests + 1);
                        }
                        else
                        {
                            perPlayer[key] = (playerName, isOwner, 1);
                        }
                    }
                }
            }

            var players = perPlayer
                .Select(kv => new PlayerChestStat(kv.Key, kv.Value.Name, kv.Value.IsOwner, kv.Value.Chests))
                .ToList();

            long fileLen = new FileInfo(file).Length;
            return new MatchResult(name, day, ms, ownerOpened, teammatesOpened, "OK", players, fileLen);
        }
        catch (Exception ex)
        {
            return new MatchResult(name, fallback.Day, fallback.DurationMs, 0, 0, $"ERR: {ex.Message}", new List<PlayerChestStat>());
        }
    }
}
