using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using GposeCast.Models;

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
                || (allowExperimentalNonPlayerHiding && hideMinionsAndPets && entry.IsCompanionLike && entry.CanNativeAlphaHide)
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
    /// Checks whether the object wrapper is valid enough to read safely.
    /// </summary>
    private static bool IsUsableObject([NotNullWhen(true)] IGameObject? actor)
    {
        return actor is not null && actor.IsValid() && actor.Address != nint.Zero;
    }

    /// <summary>
    /// Converts a live game object into a UI/service actor entry.
    /// </summary>
    private static ActorEntry ToEntry(IGameObject actor, Vector3 localPosition, bool isLocalPlayer)
    {
        var position = actor.Position;
        return new ActorEntry
        {
            Key = ActorKey.From(actor),
            Name = actor.Name.ToString(),
            ObjectKind = actor.ObjectKind,
            SubKind = actor.SubKind,
            Distance = Vector3.Distance(localPosition, position),
            Position = position,
            IsTargetable = actor.IsTargetable,
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
    /// Returns true only for live character-like wrappers that match Gpose Cast's supported hide categories.
    /// </summary>
    private static bool CanNativeAlphaHide(IGameObject actor)
    {
        return actor is ICharacter
            && (IsPlayer(actor) || IsNpcLike(actor) || IsCompanionLike(actor));
    }
}
