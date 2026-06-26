using System;
using System.Collections.Generic;
using System.Linq;
using GposeCast.Models;

namespace GposeCast.Services;

/// <summary>
/// Owns the session-local picked group that should remain visible during isolation.
/// </summary>
/// <remarks>
/// The group is intentionally not persisted. Actor ids, object-table indices, and native
/// addresses are only meaningful for the current GPose/world session.
/// </remarks>
public sealed class CastGroupService
{
    private readonly List<ActorEntry> pickedActors = new();

    /// <summary>Actors currently selected as the photo cast.</summary>
    public IReadOnlyList<ActorEntry> PickedActors => pickedActors;

    /// <summary>Returns true when an exact key exists in the picked group.</summary>
    public bool Contains(ActorKey key) => pickedActors.Any(actor => actor.Key.Equals(key));

    /// <summary>Returns true when the actor is already represented in the picked group.</summary>
    public bool ContainsActor(ActorEntry actor)
    {
        return pickedActors.Any(picked => SameActor(picked, actor));
    }

    /// <summary>Adds an actor or upgrades an existing world actor to its GPose clone.</summary>
    public void Add(ActorEntry actor)
    {
        var existingIndex = pickedActors.FindIndex(picked => SameActor(picked, actor));
        if (existingIndex >= 0)
        {
            // If an actor was first added from the world and later imported to GPose,
            // replace the row with the GPose clone so Brio/Ktisis workflows stay tidy.
            if (!pickedActors[existingIndex].IsGposeActor && actor.IsGposeActor)
                pickedActors[existingIndex] = actor;

            return;
        }

        pickedActors.Add(actor);
    }

    /// <summary>Removes an actor by exact key.</summary>
    public void Remove(ActorKey key)
    {
        pickedActors.RemoveAll(actor => actor.Key.Equals(key));
    }

    /// <summary>Clears the picked group.</summary>
    public void Clear() => pickedActors.Clear();

    /// <summary>
    /// Determines whether two entries represent the same person for cast-group purposes.
    /// </summary>
    private static bool SameActor(ActorEntry left, ActorEntry right)
    {
        if (left.Key.Equals(right.Key))
            return true;

        // Imported GPose clones and their original overworld actors usually share a
        // player name even when their object ids differ, so use exact name matching for PCs.
        var leftName = left.DisplayName;
        var rightName = right.DisplayName;
        if (!string.IsNullOrWhiteSpace(leftName)
            && !string.IsNullOrWhiteSpace(rightName)
            && leftName != "<unnamed>"
            && rightName != "<unnamed>"
            && left.IsPlayerCharacter
            && right.IsPlayerCharacter
            && leftName.Equals(rightName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
