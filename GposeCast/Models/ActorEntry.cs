using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace GposeCast.Models;

/// <summary>
/// Stable-ish identity for an object-table actor during the current client session.
/// </summary>
/// <remarks>
/// FFXIV object identity is not perfect across GPose/world clones. We therefore keep
/// the game object id, entity id, and object table index, then allow higher-level
/// services to fall back to exact player-name matching when a GPose clone replaces
/// its overworld counterpart.
/// </remarks>
public readonly record struct ActorKey(ulong GameObjectId, uint EntityId, ushort ObjectIndex)
{
    /// <summary>
    /// Builds a key from the current Dalamud game-object wrapper.
    /// </summary>
    public static ActorKey From(IGameObject actor) => new(actor.GameObjectId, actor.EntityId, actor.ObjectIndex);

    /// <summary>
    /// Checks whether the provided live actor still represents this key.
    /// </summary>
    public bool Matches(IGameObject actor)
    {
        // GameObjectId is the preferred identity when available.
        if (GameObjectId != 0 && actor.GameObjectId == GameObjectId)
            return true;

        // EntityId is useful for networked actors. 0xE0000000 is the game's
        // sentinel for non-networked/local-only objects, so do not treat it as stable.
        if (EntityId != 0 && EntityId != 0xE0000000 && actor.EntityId == EntityId)
            return true;

        // ObjectIndex is session-local, but it is still the best final fallback
        // when manipulating GPose clones and temporary spawned actors.
        return ObjectIndex == actor.ObjectIndex && actor.Address != nint.Zero;
    }

    /// <summary>
    /// Small diagnostic label for logs and debug tooltips.
    /// </summary>
    public string DebugText => $"OID:{GameObjectId:X} / EID:{EntityId:X8} / IDX:{ObjectIndex}";
}

/// <summary>
/// Immutable view-model for one object-table actor shown or controlled by Gpose Cast.
/// </summary>
public sealed class ActorEntry
{
    /// <summary>Actor identity for current-session lookups.</summary>
    public required ActorKey Key { get; init; }

    /// <summary>Raw actor name as reported by Dalamud.</summary>
    public required string Name { get; init; }

    /// <summary>Dalamud object kind, used for player/NPC/pet filtering.</summary>
    public required ObjectKind ObjectKind { get; init; }

    /// <summary>Dalamud sub-kind, kept for future filtering/debugging.</summary>
    public required byte SubKind { get; init; }

    /// <summary>Distance from the local player. Currently used only for sorting.</summary>
    public required float Distance { get; init; }

    /// <summary>Current world position.</summary>
    public required Vector3 Position { get; init; }

    /// <summary>Whether the game currently considers this actor targetable.</summary>
    public required bool IsTargetable { get; init; }

    /// <summary>Whether this entry represents the local player's current actor.</summary>
    public required bool IsLocalPlayer { get; init; }

    /// <summary>Whether this actor is treated as a player character.</summary>
    public required bool IsPlayerCharacter { get; init; }

    /// <summary>Whether this actor is treated as a pet/minion/companion-like entry.</summary>
    public required bool IsCompanionLike { get; init; }

    /// <summary>Whether this actor is treated as an NPC/event/enemy-like entry.</summary>
    public required bool IsNpcLike { get; init; }

    /// <summary>True when the actor lives in the GPose object-table range.</summary>
    public required bool IsGposeActor { get; init; }

    /// <summary>True when the actor lives in the regular overworld object-table range.</summary>
    public required bool IsOverworldActor { get; init; }

    /// <summary>Native actor address. Never persist this beyond the current frame/session.</summary>
    public required nint Address { get; init; }

    /// <summary>Human-readable source table label for compact badges/debugging.</summary>
    public string ActorTableLabel => IsGposeActor ? "GPose" : IsOverworldActor ? "World" : "Other";

    /// <summary>Human-readable object kind label.</summary>
    public string KindLabel => ObjectKind.ToString();

    /// <summary>Display-safe name shown in the UI.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "<unnamed>" : Name;
}
