using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using GposeCast.Models;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace GposeCast.Services;

/// <summary>
/// Reads the Dalamud object table and turns live game objects into Gpose Cast actor entries.
/// </summary>
/// <remarks>
/// The scanner intentionally separates the compact UI scan from the isolation scan.
/// The UI should be tidy and deduplicated, while isolation must be broad and aggressive
/// so late-loading players, pets, and NPC-like objects also get hidden.
/// </remarks>
public sealed class ActorScannerService
{
    /// <summary>Regular world actors normally occupy slots 0 through 199.</summary>
    private const ushort OverworldStart = 0;

    /// <summary>Regular world actors normally occupy slots 0 through 199.</summary>
    private const ushort OverworldEnd = 199;

    /// <summary>GPose-created/managed actors normally start at slot 201.</summary>
    private const ushort GPoseStart = 201;

    /// <summary>Upper range used by Brio/Ktisis-style GPose actor scans.</summary>
    private const ushort GPoseEnd = 439;

    /// <summary>Default radius used by the nearby-actor diagnostic dump.</summary>
    public const float DefaultNearbyDumpRadius = 12.0f;

    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;

    /// <summary>
    /// Creates a new scanner over the current object table.
    /// </summary>
    public ActorScannerService(IObjectTable objectTable, IClientState clientState)
    {
        this.objectTable = objectTable;
        this.clientState = clientState;
    }

    /// <summary>
    /// Scans actors for the compact UI list.
    /// </summary>
    public IReadOnlyList<ActorEntry> Scan(string searchText, bool playersOnly, bool includeUnnamed)
    {
        var localPlayer = objectTable.LocalPlayer;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;
        var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);
        var query = searchText.Trim();

        var entries = objectTable
            .Where(IsUsableObject)
            .Where(ShouldShowActorForCurrentMode)
            .Select(actor => ToEntry(actor, localPosition, localPlayer is not null && localKey.Matches(actor)))
            .Where(entry => includeUnnamed || !string.IsNullOrWhiteSpace(entry.Name))
            .Where(entry => !playersOnly || entry.IsPlayerCharacter)
            .Where(entry => string.IsNullOrWhiteSpace(query) || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));

        // In GPose, actors can be present twice: once as an overworld object and once
        // as a GPose clone. Keep the GPose clone in the UI to match Brio/Ktisis.
        if (clientState.IsGPosing)
            entries = DeduplicateGposeAndWorldEntries(entries);

        return entries
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToList();
    }

    /// <summary>
    /// Scans the full object table for entries that isolation should consider hiding.
    /// </summary>
    public IReadOnlyList<ActorEntry> ScanIsolationCandidates(bool hideNpcs, bool hideMinionsAndPets, bool allowExperimentalNonPlayerHiding)
    {
        var localPlayer = objectTable.LocalPlayer;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;
        var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);

        return objectTable
            .Where(IsUsableObject)
            // Isolation must be wider than the compact UI list. Vanilla GPose's
            // non-controlled character filter affects actors outside the usual PC range,
            // especially city NPCs, pets, and minions.
            .Select(actor => ToEntry(actor, localPosition, localPlayer is not null && localKey.Matches(actor)))
            // Never apply the UI's unnamed filter here. Many visible pets/minions/NPCs
            // have blank names while in GPose and still need to be hidden.
            .Where(entry => entry.IsPlayerCharacter
                // Mounted players, minions, pets, and ornaments can stream in after
                // isolation starts. Include character-like linked children in the
                // candidate set by default; VisibilityService will still preserve
                // picked/local linked children before hiding the rest.
                || (entry.IsCompanionLike && entry.CanNativeAlphaHide)
                || (allowExperimentalNonPlayerHiding && hideNpcs && entry.IsNpcLike && entry.CanNativeAlphaHide))
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Converts a live Dalamud object into an <see cref="ActorEntry"/>.
    /// </summary>
    public ActorEntry? FromGameObject(IGameObject? actor)
    {
        if (!IsUsableObject(actor))
            return null;

        // When the user targets an overworld actor while in GPose, prefer the matching
        // GPose clone if one exists. This keeps Add Target consistent with Brio/Ktisis.
        if (clientState.IsGPosing && !IsGposeActor(actor))
        {
            var gposeClone = FindGposeClone(actor);
            if (gposeClone is not null)
                actor = gposeClone;
        }

        var localPlayer = objectTable.LocalPlayer;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;
        var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);
        return ToEntry(actor, localPosition, localPlayer is not null && localKey.Matches(actor));
    }

    /// <summary>
    /// Finds a live actor by native address. Used after spawning a GPose clone.
    /// </summary>
    public ActorEntry? FindByAddress(nint address)
    {
        if (address == nint.Zero)
            return null;

        foreach (var actor in objectTable)
        {
            if (!IsUsableObject(actor) || actor.Address != address)
                continue;

            return FromGameObject(actor);
        }

        return null;
    }

    /// <summary>
    /// Finds the local player's current actor entry.
    /// </summary>
    public ActorEntry? FindLocalPlayer()
    {
        var localPlayer = objectTable.LocalPlayer;
        return localPlayer is null ? null : FromGameObject(localPlayer);
    }

    /// <summary>
    /// Finds a current actor from the compact UI's current-mode range.
    /// </summary>
    public ActorEntry? FindCurrent(ActorKey key)
    {
        foreach (var actor in objectTable)
        {
            if (!IsUsableObject(actor))
                continue;

            if (!ShouldShowActorForCurrentMode(actor))
                continue;

            if (!key.Matches(actor))
                continue;

            return FromGameObject(actor);
        }

        return null;
    }

    /// <summary>
    /// Finds a current actor anywhere in the object table.
    /// </summary>
    public ActorEntry? FindAnyCurrent(ActorKey key)
    {
        foreach (var actor in objectTable)
        {
            if (!IsUsableObject(actor) || !key.Matches(actor))
                continue;

            var localPlayer = objectTable.LocalPlayer;
            var localPosition = localPlayer?.Position ?? Vector3.Zero;
            var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);
            return ToEntry(actor, localPosition, localPlayer is not null && localKey.Matches(actor));
        }

        return null;
    }

    /// <summary>
    /// Finds a current actor anywhere in the object table, requiring loose identity details
    /// when the lookup falls back to the session-local object index.
    /// </summary>
    public ActorEntry? FindAnyCurrent(ActorKey key, string expectedName, ObjectKind expectedKind)
    {
        foreach (var actor in objectTable)
        {
            if (!IsUsableObject(actor) || !key.Matches(actor, expectedName, expectedKind))
                continue;

            var localPlayer = objectTable.LocalPlayer;
            var localPosition = localPlayer?.Position ?? Vector3.Zero;
            var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);
            return ToEntry(actor, localPosition, localPlayer is not null && localKey.Matches(actor));
        }

        return null;
    }

    /// <summary>
    /// Scans all currently loaded objects near the provided anchor. Distances are measured
    /// from the anchor actor instead of from the local player.
    /// </summary>
    public IReadOnlyList<ActorEntry> ScanNearbyActors(ActorEntry anchor, float radius = DefaultNearbyDumpRadius)
    {
        var liveAnchor = FindAnyCurrent(anchor.Key, anchor.Name, anchor.ObjectKind) ?? anchor;
        var localPlayer = objectTable.LocalPlayer;
        var localKey = localPlayer is null ? default : ActorKey.From(localPlayer);
        var radiusSquared = radius * radius;

        return objectTable
            .Where(IsUsableObject)
            .Select(actor => ToEntry(actor, liveAnchor.Position, localPlayer is not null && localKey.Matches(actor)))
            .Where(entry => Vector3.DistanceSquared(liveAnchor.Position, entry.Position) <= radiusSquared)
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.Key.ObjectIndex)
            .ToList();
    }

    /// <summary>
    /// Finds fashion accessories that are linked to the supplied player through the native
    /// parent-character relationship. A proximity fallback can be enabled for diagnostics,
    /// but real import decisions keep this disabled.
    /// </summary>
    public IReadOnlyList<ActorEntry> FindLinkedFashionAccessories(ActorEntry owner, float radius = DefaultNearbyDumpRadius, bool allowProximityFallback = false)
    {
        return FindLinkedOwnedCompanions(owner, radius, allowProximityFallback)
            .Where(actor => actor.IsFashionAccessory)
            .ToList();
    }

    /// <summary>
    /// Finds native child actors owned by a player, including ornaments/fashion accessories,
    /// mounts, minions, and companion-like objects. Mounted actors use the mount as the
    /// movable root in Brio, so Gpose Cast must hide/protect that child just like umbrellas.
    /// </summary>
    public IReadOnlyList<ActorEntry> FindLinkedOwnedCompanions(ActorEntry owner, float radius = DefaultNearbyDumpRadius, bool allowProximityFallback = false)
    {
        var liveOwner = FindAnyCurrent(owner.Key, owner.Name, owner.ObjectKind) ?? owner;
        var ownerName = liveOwner.DisplayName;

        return ScanNearbyActors(liveOwner, radius)
            .Where(actor => IsOwnedCompanionCandidate(actor) && actor.CanNativeAlphaHide)
            .Where(actor => IsLinkedToOwner(actor, liveOwner, ownerName, allowProximityFallback))
            .OrderBy(actor => actor.Distance)
            .ThenBy(actor => actor.Key.ObjectIndex)
            .ToList();
    }

    /// <summary>
    /// Writes a diagnostic nearby object-table dump to /xllog. This is intentionally verbose
    /// so umbrella/parasol and modded flag actors can be identified from real client data.
    /// </summary>
    public void DumpNearbyActors(ActorEntry anchor, float radius = DefaultNearbyDumpRadius, string reason = "manual")
    {
        var liveAnchor = FindAnyCurrent(anchor.Key, anchor.Name, anchor.ObjectKind) ?? anchor;
        var nearby = ScanNearbyActors(liveAnchor, radius);
        var linkedAccessories = FindLinkedFashionAccessories(liveAnchor, radius, allowProximityFallback: false);
        var linkedOwnedCompanions = FindLinkedOwnedCompanions(liveAnchor, radius, allowProximityFallback: false);

        Plugin.Log.Information($"Gpose Cast: nearby actor dump '{reason}' around {FormatActorDebug(liveAnchor)} radius={radius:F1}m count={nearby.Count} linkedFashionAccessories={linkedAccessories.Count} linkedOwnedCompanions={linkedOwnedCompanions.Count}.");

        foreach (var actor in nearby)
        {
            Plugin.Log.Information(
                "Gpose Cast dump: "
                + $"Name=\"{actor.DisplayName}\" | "
                + $"Kind={actor.ObjectKind} | "
                + $"SubKind=0x{actor.SubKind:X2} | "
                + $"ObjectIndex={actor.Key.ObjectIndex} | "
                + $"GameObjectId=0x{actor.Key.GameObjectId:X} | "
                + $"EntityId=0x{actor.Key.EntityId:X8} | "
                + $"Address={FormatAddress(actor.Address)} | "
                + $"Distance={actor.Distance:F2}m | "
                + $"IsICharacter={actor.IsICharacter} | "
                + $"IsGposeActor={actor.IsGposeActor} | "
                + $"IsOverworldActor={actor.IsOverworldActor} | "
                + $"CanNativeAlphaHide={actor.CanNativeAlphaHide} | "
                + $"IsFashionAccessory={actor.IsFashionAccessory} | "
                + $"ParentName=\"{actor.ParentName}\" | "
                + $"ParentKey={FormatParentKey(actor)} | "
                + $"ParentAddress={FormatAddress(actor.ParentAddress)}");
        }
    }


    // The earlier pre-release deep native-memory dump lived here. It was intentionally removed
    // before 0.9.0.0 because release diagnostics should avoid broad raw pointer reads.

    /// <summary>
    /// Checks whether the object wrapper is valid enough to read safely.
    /// </summary>
    private static bool IsUsableObject([NotNullWhen(true)] IGameObject? actor)
    {
        return actor is not null && actor.IsValid() && actor.Address != nint.Zero;
    }

    /// <summary>
    /// Converts a live game object into a UI/service actor entry.
    /// </summary>
    private ActorEntry ToEntry(IGameObject actor, Vector3 localPosition, bool isLocalPlayer)
    {
        var position = actor.Position;
        var parent = FindParentCharacter(actor);
        return new ActorEntry
        {
            Key = ActorKey.From(actor),
            Name = actor.Name.ToString(),
            ObjectKind = actor.ObjectKind,
            SubKind = actor.SubKind,
            Distance = Vector3.Distance(localPosition, position),
            Position = position,
            IsTargetable = actor.IsTargetable,
            IsICharacter = actor is ICharacter,
            ParentKey = parent is null ? null : ActorKey.From(parent),
            ParentName = parent?.Name.ToString() ?? string.Empty,
            ParentAddress = parent?.Address ?? nint.Zero,
            IsLocalPlayer = isLocalPlayer,
            IsPlayerCharacter = IsPlayer(actor),
            IsCompanionLike = IsCompanionLike(actor),
            IsFashionAccessory = IsFashionAccessory(actor),
            IsNpcLike = IsNpcLike(actor),
            CanNativeAlphaHide = CanNativeAlphaHide(actor),
            IsGposeActor = IsGposeActor(actor),
            IsOverworldActor = IsOverworldActor(actor),
            Address = actor.Address,
        };
    }

    /// <summary>
    /// Chooses which object-table ranges the compact UI should show.
    /// </summary>
    private bool ShouldShowActorForCurrentMode(IGameObject actor)
    {
        // In GPose, some busy outdoor areas keep visible players only as overworld
        // entries instead of GPose clones. Show both ranges, then deduplicate later.
        return clientState.IsGPosing
            ? IsGposeActor(actor) || IsOverworldActor(actor)
            : IsOverworldActor(actor);
    }

    /// <summary>
    /// Deduplicates world/GPose entries by player name, preferring the GPose clone.
    /// </summary>
    private static IEnumerable<ActorEntry> DeduplicateGposeAndWorldEntries(IEnumerable<ActorEntry> entries)
    {
        return entries
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Name)
                ? entry.Key.DebugText
                : entry.Name, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entry => entry.IsGposeActor)
                .ThenBy(entry => entry.Distance)
                .First());
    }

    /// <summary>
    /// Finds a GPose clone that appears to represent the provided overworld actor.
    /// </summary>
    private IGameObject? FindGposeClone(IGameObject source)
    {
        var name = source.Name.ToString();
        foreach (var actor in objectTable)
        {
            if (!IsUsableObject(actor))
                continue;

            if (!IsGposeActor(actor))
                continue;

            if (!actor.Name.ToString().Equals(name, StringComparison.Ordinal))
                continue;

            return actor;
        }

        return null;
    }

    /// <summary>Returns true when the actor index is in the GPose actor range.</summary>
    private static bool IsGposeActor(IGameObject actor) => actor.ObjectIndex is >= GPoseStart and <= GPoseEnd;

    /// <summary>Returns true when the actor index is in the overworld actor range.</summary>
    private static bool IsOverworldActor(IGameObject actor) => actor.ObjectIndex is >= OverworldStart and <= OverworldEnd;

    /// <summary>
    /// Detects player characters across Dalamud object-kind naming differences.
    /// </summary>
    private static bool IsPlayer(IGameObject actor)
    {
        var kind = actor.ObjectKind.ToString();
        return kind.Equals("Player", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Pc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("PC", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Character", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects NPC-like character actors. Generic event/world objects are intentionally
    /// excluded because Gpose Cast's local alpha hide is only applied to character-like wrappers.
    /// </summary>
    private static bool IsNpcLike(IGameObject actor)
    {
        var kind = actor.ObjectKind.ToString();
        return kind.Equals("EventNpc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("BattleNpc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Retainer", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Npc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("BNpc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("ENpc", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("EventNpcType", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("BattleNpcType", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("Npc", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("Enemy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects companion-like actors such as minions, pets, mounts, and ornaments.
    /// </summary>
    private static bool IsCompanionLike(IGameObject actor)
    {
        var kind = actor.ObjectKind.ToString();
        return kind.Equals("Companion", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Minion", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Mount", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("MountType", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Ornament", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("OrnamentType", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects fashion accessories. FFXIV exposes umbrellas/parasols and similar accessories
    /// as separate ornament actors rather than as part of the player actor itself.
    /// </summary>
    private static bool IsFashionAccessory(IGameObject actor)
    {
        var kind = actor.ObjectKind.ToString();
        return kind.Equals("Ornament", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("OrnamentType", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when a fashion accessory has a native parent link to the owner. When
    /// explicitly requested, falls back to the nearby scan distance already applied upstream.
    /// </summary>
    private static bool IsLinkedToOwner(ActorEntry accessory, ActorEntry owner, string ownerName, bool allowProximityFallback)
    {
        if (!IsOwnedCompanionCandidate(accessory))
            return false;

        if (accessory.ParentKey is { } parentKey && parentKey.Equals(owner.Key))
            return true;

        if (!string.IsNullOrWhiteSpace(accessory.ParentName)
            && !string.IsNullOrWhiteSpace(ownerName)
            && accessory.ParentName.Equals(ownerName, StringComparison.Ordinal))
        {
            return true;
        }

        return allowProximityFallback;
    }

    /// <summary>True for native child actors that should follow a player import as one cast unit.</summary>
    private static bool IsOwnedCompanionCandidate(ActorEntry actor) => actor.IsFashionAccessory || actor.IsCompanionLike;

    /// <summary>
    /// Resolves the native parent character for mounts, minions, and ornaments when the
    /// client exposes one. Generic objects are ignored.
    /// </summary>
    private unsafe IGameObject? FindParentCharacter(IGameObject actor)
    {
        if (actor is not ICharacter || (!IsCompanionLike(actor) && !IsFashionAccessory(actor)))
            return null;

        try
        {
            var native = (NativeCharacter*)actor.Address;
            if (native == null)
                return null;

            var parent = native->GetParentCharacter();
            if (parent == null)
                return null;

            var parentAddress = (nint)parent;
            foreach (var candidate in objectTable)
            {
                if (!IsUsableObject(candidate) || candidate.Address != parentAddress)
                    continue;

                return candidate;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, $"Gpose Cast: failed to resolve parent character for {actor.Name}.");
        }

        return null;
    }

    /// <summary>Formats one actor for compact diagnostic logs.</summary>
    private static string FormatActorDebug(ActorEntry actor)
    {
        return $"{actor.DisplayName} [Kind={actor.ObjectKind}, Index={actor.Key.ObjectIndex}, GameObjectId=0x{actor.Key.GameObjectId:X}, EntityId=0x{actor.Key.EntityId:X8}, Address={FormatAddress(actor.Address)}]";
    }

    /// <summary>Formats a nullable parent key for diagnostic logs.</summary>
    private static string FormatParentKey(ActorEntry actor)
    {
        return actor.ParentKey is { } parentKey ? parentKey.DebugText : "<none>";
    }

    /// <summary>Formats native pointers for diagnostic logs.</summary>
    private static string FormatAddress(nint address)
    {
        return address == nint.Zero ? "0x0" : $"0x{address.ToInt64():X}";
    }

    /// <summary>
    /// Returns true only for live character-like wrappers that match Gpose Cast's supported hide categories.
    /// </summary>
    private static bool CanNativeAlphaHide(IGameObject actor)
    {
        return actor is ICharacter
            && (IsPlayer(actor) || IsNpcLike(actor) || IsCompanionLike(actor));
    }
}
