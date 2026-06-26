using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
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
    private readonly HashSet<ActorKey> protectedFashionAccessories = new();

    /// <summary>Creates a visibility service backed by the scanner for live actor lookup.</summary>
    public VisibilityService(ActorScannerService actorScanner, Configuration configuration)
    {
        this.actorScanner = actorScanner;
        this.configuration = configuration;
    }

    /// <summary>Number of actors currently tracked as hidden by this plugin.</summary>
    public int HiddenCount => hiddenActors.Count;

    /// <summary>Number of explicitly protected linked fashion accessories.</summary>
    public int ProtectedFashionAccessoryCount => protectedFashionAccessories.Count;

    /// <summary>Whether the picked-group isolation rule is currently active.</summary>
    public bool IsIsolationActive { get; private set; }

    /// <summary>Returns true when the actor key is currently hidden by this plugin.</summary>
    public bool IsHidden(ActorKey key) => hiddenActors.ContainsKey(key);

    /// <summary>Hides one actor manually from the compact actor table.</summary>
    public bool HideTest(ActorEntry actor) => Hide(actor, allowLocalPlayer: false, reason: "alpha-hide test");

    /// <summary>
    /// Hides the original overworld player and its original linked children after a
    /// linked-child import. The imported GPose actor/mount/accessory remain visible.
    /// </summary>
    public int HideImportedSourceDuplicate(ActorEntry sourceActor, IEnumerable<ActorEntry> linkedAccessories, string reason)
    {
        var hidden = 0;

        if (sourceActor.IsOverworldActor && Hide(sourceActor, allowLocalPlayer: false, reason: reason))
            hidden++;

        foreach (var accessory in linkedAccessories)
        {
            if (!IsLinkedCompanionActor(accessory))
                continue;

            if (Hide(accessory, allowLocalPlayer: false, reason: $"{reason} linked child", allowLinkedCompanion: true))
                hidden++;
        }

        if (hidden > 0)
            Plugin.Log.Information($"Gpose Cast: hid {hidden} original overworld actor/linked-child duplicate(s) after linked-child import.");

        return hidden;
    }

    /// <summary>Explicitly protects linked fashion accessories discovered before import.</summary>
    public void ProtectFashionAccessories(IEnumerable<ActorEntry> accessories, string reason)
    {
        var added = 0;
        foreach (var accessory in accessories)
        {
            if (!accessory.IsFashionAccessory)
                continue;

            if (protectedFashionAccessories.Add(accessory.Key))
            {
                added++;
                Plugin.Log.Information($"Gpose Cast: protected linked fashion accessory {FormatAccessoryDebug(accessory)} ({reason}).");
            }

            if (IsIsolationActive && IsHidden(accessory.Key))
                Restore(accessory.Key);
        }

        if (added > 0)
            Plugin.Log.Information($"Gpose Cast: protected {added} linked fashion accessory actor(s) from isolation hide.");
    }

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
        protectedFashionAccessories.Clear();
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
        var pickedGposePlayerNames = picked
            .Where(actor => actor.IsPlayerCharacter && actor.IsGposeActor)
            .Select(actor => actor.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "<unnamed>")
            .ToHashSet(StringComparer.Ordinal);
        var pickedFashionAccessories = configuration.HandleLinkedFashionAccessories
            ? FindPickedLinkedCompanionActors(visibleActors, picked)
            : new HashSet<ActorKey>();
        var localPlayerKeys = visibleActors
            .Where(actor => actor.IsLocalPlayer)
            .Select(actor => actor.Key)
            .ToHashSet();

        foreach (var actor in visibleActors)
        {
            // Name matching keeps imported GPose clones and their original world actor
            // aligned. Exact key matching still handles pets/NPCs/unnamed objects.
            var samePickedPlayerName = actor.IsPlayerCharacter
                && pickedNames.Contains(actor.DisplayName)
                && (!pickedGposePlayerNames.Contains(actor.DisplayName) || actor.IsGposeActor || actor.IsLocalPlayer);
            var linkedPickedFashionAccessory = IsLinkedCompanionActor(actor) && pickedFashionAccessories.Contains(actor.Key);
            var explicitlyProtectedFashionAccessory = IsLinkedCompanionActor(actor) && protectedFashionAccessories.Contains(actor.Key);
            var linkedLocalPlayerChild = IsLinkedCompanionActor(actor)
                && actor.ParentKey is { } localParentKey
                && localPlayerKeys.Contains(localParentKey);
            var shouldStayVisible = actor.IsLocalPlayer
                || linkedLocalPlayerChild
                || samePickedPlayerName
                || linkedPickedFashionAccessory
                || explicitlyProtectedFashionAccessory
                || pickedKeys.Any(key => key.Equals(actor.Key));

            if (shouldStayVisible)
            {
                if (IsHidden(actor.Key))
                    Restore(actor.Key);

                continue;
            }

            Hide(actor, allowLocalPlayer: false, reason: "group isolation", allowLinkedCompanion: IsLinkedCompanionActor(actor));
        }
    }

    /// <summary>
    /// Writes a focused post-isolation diagnostic dump around each picked actor. This is
    /// intentionally separate from the scanner dump because it includes Gpose Cast's own
    /// visibility decision, protection state, hidden tracking, and current native alpha.
    /// </summary>
    public void DumpIsolationDebug(IReadOnlyCollection<ActorEntry> anchors, IReadOnlyCollection<ActorEntry> pickedActors, string reason, float radius = ActorScannerService.DefaultNearbyDumpRadius)
    {
        if (anchors.Count == 0)
        {
            Plugin.Log.Information($"Gpose Cast: isolation dump '{reason}' skipped because there are no picked anchors.");
            return;
        }

        var picked = pickedActors.ToList();
        var pickedKeys = picked.Select(actor => actor.Key).ToList();
        var pickedNames = picked
            .Where(actor => actor.IsPlayerCharacter)
            .Select(actor => actor.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "<unnamed>")
            .ToHashSet(StringComparer.Ordinal);
        var pickedGposePlayerNames = picked
            .Where(actor => actor.IsPlayerCharacter && actor.IsGposeActor)
            .Select(actor => actor.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "<unnamed>")
            .ToHashSet(StringComparer.Ordinal);

        var dumpedAnchors = new HashSet<ActorKey>();
        foreach (var anchor in anchors)
        {
            var liveAnchor = actorScanner.FindAnyCurrent(anchor.Key, anchor.Name, anchor.ObjectKind) ?? anchor;
            if (!dumpedAnchors.Add(liveAnchor.Key))
                continue;

            var nearby = actorScanner.ScanNearbyActors(liveAnchor, radius);
            var pickedFashionAccessories = configuration.HandleLinkedFashionAccessories
                ? FindPickedLinkedCompanionActors(nearby, picked)
                : new HashSet<ActorKey>();
            var fashionAccessories = nearby.Count(IsLinkedCompanionActor);
            var protectedAccessories = nearby.Count(actor => IsLinkedCompanionActor(actor) && protectedFashionAccessories.Contains(actor.Key));
            var hiddenNearby = nearby.Count(actor => IsHidden(actor.Key));

            Plugin.Log.Information($"Gpose Cast: isolation dump '{reason}' around {FormatActorDebug(liveAnchor)} radius={radius:F1}m count={nearby.Count} fashionAccessories={fashionAccessories} protectedAccessories={protectedAccessories} hiddenTracked={hiddenNearby}.");

            foreach (var actor in nearby)
            {
                var exactPickedKey = pickedKeys.Any(key => key.Equals(actor.Key));
                var samePickedPlayerName = actor.IsPlayerCharacter
                    && pickedNames.Contains(actor.DisplayName)
                    && (!pickedGposePlayerNames.Contains(actor.DisplayName) || actor.IsGposeActor || actor.IsLocalPlayer);
                var linkedPickedFashionAccessory = IsLinkedCompanionActor(actor) && pickedFashionAccessories.Contains(actor.Key);
                var explicitlyProtectedFashionAccessory = IsLinkedCompanionActor(actor) && protectedFashionAccessories.Contains(actor.Key);
                var shouldStayVisible = actor.IsLocalPlayer
                    || samePickedPlayerName
                    || linkedPickedFashionAccessory
                    || explicitlyProtectedFashionAccessory
                    || exactPickedKey;

                Plugin.Log.Information(
                    "Gpose Cast isolation dump: "
                    + $"Name=\"{actor.DisplayName}\" | "
                    + $"Kind={actor.ObjectKind} | "
                    + $"SubKind=0x{actor.SubKind:X2} | "
                    + $"ObjectIndex={actor.Key.ObjectIndex} | "
                    + $"GameObjectId=0x{actor.Key.GameObjectId:X} | "
                    + $"EntityId=0x{actor.Key.EntityId:X8} | "
                    + $"Address={FormatAddress(actor.Address)} | "
                    + $"Distance={actor.Distance:F2}m | "
                    + $"IsGposeActor={actor.IsGposeActor} | "
                    + $"IsOverworldActor={actor.IsOverworldActor} | "
                    + $"IsFashionAccessory={actor.IsFashionAccessory} | "
                    + $"CanNativeAlphaHide={actor.CanNativeAlphaHide} | "
                    + $"ParentName=\"{actor.ParentName}\" | "
                    + $"ParentKey={FormatParentKey(actor)} | "
                    + $"IsHidden={IsHidden(actor.Key)} | "
                    + $"IsProtected={explicitlyProtectedFashionAccessory} | "
                    + $"ShouldStayVisible={shouldStayVisible} | "
                    + $"KeepReason={BuildKeepReason(actor, exactPickedKey, samePickedPlayerName, linkedPickedFashionAccessory, explicitlyProtectedFashionAccessory)} | "
                    + $"Alpha={ReadAlphaDebug(actor)}");
            }
        }
    }

    /// <summary>
    /// Finds native child actors that should stay visible because they are attached to a
    /// picked player. FFXIV exposes umbrellas, mounts, and some companions as separate
    /// actors, so exact actor-key matching alone would hide them during isolation.
    /// </summary>
    private static HashSet<ActorKey> FindPickedLinkedCompanionActors(IReadOnlyCollection<ActorEntry> visibleActors, IReadOnlyCollection<ActorEntry> pickedActors)
    {
        var pickedPlayers = pickedActors
            .Where(actor => actor.IsPlayerCharacter)
            .ToList();

        if (pickedPlayers.Count == 0)
            return new HashSet<ActorKey>();

        // When the picked player is an overworld actor, preserve its overworld accessory by name.
        // When the picked player is a GPose clone, only preserve accessories whose parent key is
        // the GPose clone. This prevents the original world umbrella/wing from surviving isolation.
        var pickedWorldNames = pickedPlayers
            .Where(actor => !actor.IsGposeActor)
            .Select(actor => actor.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "<unnamed>")
            .ToHashSet(StringComparer.Ordinal);
        var pickedKeys = pickedPlayers
            .Select(actor => actor.Key)
            .ToHashSet();

        var protectedAccessories = new HashSet<ActorKey>();

        foreach (var accessory in visibleActors.Where(actor => IsLinkedCompanionActor(actor) && actor.CanNativeAlphaHide))
        {
            // FFXIVClientStructs exposes a parent-character link for ornaments. Use only
            // confirmed parent/name links here. The old proximity fallback preserved unrelated
            // umbrellas/wings when other players stood near the picked actor.
            if (accessory.ParentKey is { } parentKey && pickedKeys.Contains(parentKey))
            {
                protectedAccessories.Add(accessory.Key);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(accessory.ParentName) && pickedWorldNames.Contains(accessory.ParentName))
                protectedAccessories.Add(accessory.Key);
        }

        return protectedAccessories;
    }

    /// <summary>Applies alpha hide to a live actor and stores the original alpha.</summary>
    private bool Hide(ActorEntry actor, bool allowLocalPlayer, string reason, bool allowFashionAccessory = false, bool allowLinkedCompanion = false)
    {
        if (actor.IsLocalPlayer && !allowLocalPlayer)
        {
            Plugin.Log.Warning("Gpose Cast: refusing to hide local player.");
            return false;
        }

        var live = actorScanner.FindAnyCurrent(actor.Key, actor.Name, actor.ObjectKind);
        if (live is null || live.Address == nint.Zero)
        {
            Plugin.Log.Warning("Gpose Cast: actor is not currently loaded, cannot hide.");
            return false;
        }

        if (!CanAlphaHide(live, allowFashionAccessory, allowLinkedCompanion))
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

                hiddenActors[live.Key] = new HiddenActorState(live.Key, live.Name, live.DisplayName, live.ObjectKind, originalAlpha);
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

        var live = actorScanner.FindAnyCurrent(key, state.Name, state.ObjectKind);
        if (live is null || live.Address == nint.Zero)
        {
            Plugin.Log.Debug($"Gpose Cast: cannot restore {state.DisplayName}, actor is not currently loaded.");
            hiddenActors.Remove(key);
            return false;
        }

        try
        {
            if (!CanRestoreAlpha(live))
            {
                hiddenActors.Remove(key);
                Plugin.Log.Warning($"Gpose Cast: skipped restore for {state.DisplayName} because the current actor is not a supported restore target.");
                return false;
            }

            unsafe
            {
                var native = (NativeCharacter*)live.Address;
                native->Alpha = state.OriginalAlpha;
            }

            hiddenActors.Remove(key);
            Plugin.Log.Information($"Gpose Cast: restored {state.DisplayName}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Gpose Cast: failed to restore {state.DisplayName}.");
            return false;
        }
    }

    /// <summary>True for owned child actors that should travel with a picked player/mount import.</summary>
    private static bool IsLinkedCompanionActor(ActorEntry actor) => actor.IsFashionAccessory || actor.IsCompanionLike;

    /// <summary>Returns true when native alpha writes are allowed for this actor.</summary>
    private bool CanAlphaHide(ActorEntry actor, bool allowFashionAccessory = false, bool allowLinkedCompanion = false)
    {
        if (actor.IsLocalPlayer)
            return false;

        if (!actor.CanNativeAlphaHide)
            return false;

        if (allowFashionAccessory && actor.IsFashionAccessory)
            return true;

        if (allowLinkedCompanion && IsLinkedCompanionActor(actor))
            return true;

        // Player characters are the stable/core path. Optional non-player hiding only
        // applies to character-like NPCs, companions, minions, mounts, and ornaments.
        return actor.IsPlayerCharacter
            || (configuration.AllowExperimentalNonPlayerHiding && (actor.IsNpcLike || actor.IsCompanionLike));
    }

    /// <summary>Returns true when restoring alpha is allowed for an actor already hidden by this plugin.</summary>
    private static bool CanRestoreAlpha(ActorEntry actor)
    {
        // Restore must not depend on the current optional-hiding toggle. If the user hid
        // a non-player actor while the toggle was enabled and later disables it, the plugin
        // must still be allowed to undo its own alpha write.
        return !actor.IsLocalPlayer && actor.CanNativeAlphaHide;
    }

    /// <summary>Restores all actors hidden by the plugin.</summary>
    public void RestoreAll()
    {
        foreach (var key in new List<ActorKey>(hiddenActors.Keys))
            Restore(key);

        hiddenActors.Clear();
    }

    /// <summary>Builds a compact reason string for post-isolation diagnostics.</summary>
    private static string BuildKeepReason(ActorEntry actor, bool exactPickedKey, bool samePickedPlayerName, bool linkedPickedFashionAccessory, bool explicitlyProtectedFashionAccessory)
    {
        var reasons = new List<string>();
        if (actor.IsLocalPlayer)
            reasons.Add("local-player");
        if (exactPickedKey)
            reasons.Add("picked-key");
        if (samePickedPlayerName)
            reasons.Add("picked-player-name");
        if (linkedPickedFashionAccessory)
            reasons.Add("linked-fashion-accessory");
        if (explicitlyProtectedFashionAccessory)
            reasons.Add("explicitly-protected-fashion-accessory");

        return reasons.Count == 0 ? "hide-candidate" : string.Join("+", reasons);
    }

    /// <summary>Formats one actor for compact dev logs.</summary>
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

    /// <summary>Reads current Character.Alpha for diagnostics without changing it.</summary>
    private static string ReadAlphaDebug(ActorEntry actor)
    {
        if (!actor.CanNativeAlphaHide || actor.Address == nint.Zero)
            return "<n/a>";

        try
        {
            unsafe
            {
                var native = (NativeCharacter*)actor.Address;
                return native == null ? "<null>" : native->Alpha.ToString("F3");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, $"Gpose Cast: failed to read alpha for {actor.DisplayName} during isolation dump.");
            return "<error>";
        }
    }

    /// <summary>Formats an explicitly protected accessory for diagnostics.</summary>
    private static string FormatAccessoryDebug(ActorEntry accessory)
    {
        var parent = string.IsNullOrWhiteSpace(accessory.ParentName)
            ? "<none>"
            : accessory.ParentName;

        return $"{accessory.DisplayName} [Kind={accessory.ObjectKind}, SubKind=0x{accessory.SubKind:X2}, Index={accessory.Key.ObjectIndex}, GameObjectId=0x{accessory.Key.GameObjectId:X}, EntityId=0x{accessory.Key.EntityId:X8}, Parent=\"{parent}\"]";
    }

    /// <summary>Original alpha state for one hidden actor.</summary>
    private readonly record struct HiddenActorState(ActorKey Key, string Name, string DisplayName, ObjectKind ObjectKind, float OriginalAlpha);
}
