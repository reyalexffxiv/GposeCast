using System;
using System.Collections.Generic;
using System.Linq;
using GposeCast.Models;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace GposeCast.Services;

/// <summary>
/// Applies local actor visibility changes for hide tests and picked-group isolation.
/// </summary>
/// <remarks>
/// Visibility is implemented by changing native Character.Alpha. This is local to the
/// client and is restored on isolation stop, GPose exit, or plugin disposal.
/// </remarks>
public sealed class VisibilityService
{
    private const float HiddenAlpha = 0.0f;

    private readonly ActorScannerService actorScanner;
    private readonly Configuration configuration;
    private readonly Dictionary<ActorKey, HiddenActorState> hiddenActors = new();

    /// <summary>Creates a visibility service backed by the scanner for live actor lookup.</summary>
    public VisibilityService(ActorScannerService actorScanner, Configuration configuration)
    {
        this.actorScanner = actorScanner;
        this.configuration = configuration;
    }

    /// <summary>Number of actors currently tracked as hidden by this plugin.</summary>
    public int HiddenCount => hiddenActors.Count;

    /// <summary>Whether the picked-group isolation rule is currently active.</summary>
    public bool IsIsolationActive { get; private set; }

    /// <summary>Returns true when the actor key is currently hidden by this plugin.</summary>
    public bool IsHidden(ActorKey key) => hiddenActors.ContainsKey(key);

    /// <summary>Hides one actor manually from the compact actor table.</summary>
    public bool HideTest(ActorEntry actor) => Hide(actor, allowLocalPlayer: false, reason: "alpha-hide test");

    /// <summary>Starts isolation and immediately enforces it once.</summary>
    public void StartIsolation(IReadOnlyCollection<ActorEntry> visibleActors, IReadOnlyCollection<ActorEntry> pickedActors)
    {
        if (pickedActors.Count == 0)
        {
            Plugin.Log.Warning("Gpose Cast: refusing to isolate because the picked group is empty.");
            return;
        }

        IsIsolationActive = true;
        EnforceIsolation(visibleActors, pickedActors);
    }

    /// <summary>Stops isolation and restores every actor hidden by the plugin.</summary>
    public void StopIsolation()
    {
        IsIsolationActive = false;
        RestoreAll();
    }

    /// <summary>
    /// Re-applies the isolation rule to all currently loaded candidates.
    /// </summary>
    public void EnforceIsolation(IReadOnlyCollection<ActorEntry> visibleActors, IReadOnlyCollection<ActorEntry> pickedActors)
    {
        if (!IsIsolationActive)
            return;

        var picked = pickedActors.ToList();
        var pickedKeys = picked.Select(actor => actor.Key).ToList();
        var pickedNames = picked
            .Where(actor => actor.IsPlayerCharacter)
            .Select(actor => actor.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "<unnamed>")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var actor in visibleActors)
        {
            // Name matching keeps imported GPose clones and their original world actor
            // aligned. Exact key matching still handles pets/NPCs/unnamed objects.
            var samePickedPlayerName = actor.IsPlayerCharacter && pickedNames.Contains(actor.DisplayName);
            var shouldStayVisible = actor.IsLocalPlayer || samePickedPlayerName || pickedKeys.Any(key => key.Equals(actor.Key));

            if (shouldStayVisible)
            {
                if (IsHidden(actor.Key))
                    Restore(actor.Key);

                continue;
            }

            Hide(actor, allowLocalPlayer: false, reason: "group isolation");
        }
    }

    /// <summary>Applies alpha hide to a live actor and stores the original alpha.</summary>
    private bool Hide(ActorEntry actor, bool allowLocalPlayer, string reason)
    {
        if (actor.IsLocalPlayer && !allowLocalPlayer)
        {
            Plugin.Log.Warning("Gpose Cast: refusing to hide local player.");
            return false;
        }

        var live = actorScanner.FindAnyCurrent(actor.Key);
        if (live is null || live.Address == nint.Zero)
        {
            Plugin.Log.Warning("Gpose Cast: actor is not currently loaded, cannot hide.");
            return false;
        }

        if (!CanAlphaHide(live))
        {
            Plugin.Log.Debug($"Gpose Cast: skipped non-player alpha hide for {live.DisplayName} ({live.ObjectKind}).");
            return false;
        }

        if (hiddenActors.ContainsKey(live.Key))
            return true;

        try
        {
            unsafe
            {
                var native = (NativeCharacter*)live.Address;
                var originalAlpha = native->Alpha;

                hiddenActors[live.Key] = new HiddenActorState(live.Key, live.DisplayName, originalAlpha);
                native->Alpha = HiddenAlpha;
            }

            // Avoid per-frame log spam while isolation repeatedly enforces itself in busy areas.
            if (reason != "group isolation")
                Plugin.Log.Information($"Gpose Cast: {reason} applied to {live.DisplayName}.");

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Gpose Cast: failed to hide {live.DisplayName}.");
            hiddenActors.Remove(live.Key);
            return false;
        }
    }

    /// <summary>Restores a single hidden actor if it is still loaded.</summary>
    public bool Restore(ActorKey key)
    {
        if (!hiddenActors.TryGetValue(key, out var state))
            return false;

        var live = actorScanner.FindAnyCurrent(key);
        if (live is null || live.Address == nint.Zero)
        {
            Plugin.Log.Warning($"Gpose Cast: cannot restore {state.Name}, actor is not currently loaded.");
            hiddenActors.Remove(key);
            return false;
        }

        try
        {
            if (!CanRestoreAlpha(live))
            {
                hiddenActors.Remove(key);
                Plugin.Log.Warning($"Gpose Cast: skipped restore for {state.Name} because the current actor resolves to the local player.");
                return false;
            }

            unsafe
            {
                var native = (NativeCharacter*)live.Address;
                native->Alpha = state.OriginalAlpha;
            }

            hiddenActors.Remove(key);
            Plugin.Log.Information($"Gpose Cast: restored {state.Name}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Gpose Cast: failed to restore {state.Name}.");
            return false;
        }
    }

    /// <summary>Returns true when native alpha writes are allowed for this actor.</summary>
    private bool CanAlphaHide(ActorEntry actor)
    {
        if (actor.IsLocalPlayer)
            return false;

        // Player characters are the stable/core path. Non-player entries are kept behind
        // an explicit experimental switch because FFXIV updates can change native layouts
        // for pets, NPCs, event objects, and other object-table entries.
        return actor.IsPlayerCharacter || configuration.AllowExperimentalNonPlayerHiding;
    }

    /// <summary>Returns true when restoring alpha is allowed for an actor already hidden by this plugin.</summary>
    private static bool CanRestoreAlpha(ActorEntry actor)
    {
        // Restore must not depend on the current experimental-hiding toggle. If the user
        // hid a non-player actor while the toggle was enabled and later disables it, the
        // plugin must still be allowed to undo its own alpha write.
        return !actor.IsLocalPlayer;
    }

    /// <summary>Restores all actors hidden by the plugin.</summary>
    public void RestoreAll()
    {
        foreach (var key in new List<ActorKey>(hiddenActors.Keys))
            Restore(key);

        hiddenActors.Clear();
    }

    /// <summary>Original alpha state for one hidden actor.</summary>
    private readonly record struct HiddenActorState(ActorKey Key, string Name, float OriginalAlpha);
}
