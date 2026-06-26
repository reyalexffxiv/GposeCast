using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using GposeCast.Models;
using GposeCast.Services;

namespace GposeCast.Windows;

/// <summary>
/// Compact GPose workflow window.
/// </summary>
/// <remarks>
/// The UI is intentionally narrow because GPose screen space is valuable. The actor
/// table uses tiny action buttons and short badges instead of wide diagnostic columns.
/// </remarks>
public sealed class MainWindow : Window, IDisposable
{
    private const string MascotResourceName = "GposeCast.Assets.peepo_camera.png";

    private readonly Plugin plugin;
    private readonly ISharedImmediateTexture? mascotTexture;
    private string searchText = string.Empty;
    private bool playersOnly;
    private bool includeUnnamed;
    private ActorKey? selectedActorKey;

    /// <summary>Creates the main compact workflow window.</summary>
    public MainWindow(Plugin plugin)
        : base("Gpose Cast###GposeCastMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(235, 80),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        mascotTexture = LoadMascotTexture();
        playersOnly = plugin.Configuration.PlayersOnly;
        includeUnnamed = plugin.Configuration.IncludeUnnamed;

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1.5f, 1.5f),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
            Click = _ => plugin.ToggleConfigUi(),
        });

        plugin.GposeImport.ImportCompleted += OnImportCompleted;
    }

    /// <summary>Unsubscribes window event handlers.</summary>
    public void Dispose()
    {
        plugin.GposeImport.ImportCompleted -= OnImportCompleted;
        if (mascotTexture is IDisposable disposable)
            disposable.Dispose();
    }

    /// <inheritdoc />
    public override void Draw()
    {
        DrawCompactHeader();

        DrawToolbar();
        DrawFilters();

        // Refresh the compact list every frame. Object-table reads are cheap and keep the
        // window accurate as actors load/unload during busy outdoor GPose sessions.
        var actors = plugin.ActorScanner.Scan(searchText, playersOnly, includeUnnamed);

        var pickedRows = Math.Clamp(plugin.CastGroup.PickedActors.Count, 1, 6);
        var pickedHeight = (24f + pickedRows * 22f) * ImGuiHelpers.GlobalScale;
        DrawPickedGroup(pickedHeight);
        ImGui.Spacing();
        DrawActorTable(actors);
    }


    /// <summary>Loads the small decorative camera peepo from embedded resources.</summary>
    private static ISharedImmediateTexture? LoadMascotTexture()
    {
        try
        {
            return Plugin.TextureProvider.GetFromManifestResource(
                Assembly.GetExecutingAssembly(),
                MascotResourceName);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load Gpose Cast mascot texture.");
            return null;
        }
    }

    /// <summary>Draws the decorative camera peepo on the right side of the filter row.</summary>
    private void DrawMascotOnFilterRow(float filterRowY)
    {
        if (!plugin.Configuration.ShowMascot || mascotTexture is null)
            return;

        var wrap = mascotTexture.GetWrapOrDefault();
        if (wrap is null)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var size = 44f * scale;
        var rightPadding = 26f * scale;
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();

        // Keep the mascot anchored to the filter row below the search bar.
        // Nudge it slightly upward so it decorates the filter row without sitting on the picked panel.
        // If the window is extremely narrow, skip it instead of crowding the checkboxes.
        if (windowSize.X < 320f * scale)
            return;

        var imageMin = new Vector2(
            windowPos.X + windowSize.X - rightPadding - size,
            filterRowY - 8f * scale);
        var imageMax = imageMin + new Vector2(size, size);

        ImGui.GetWindowDrawList().AddImage(wrap.Handle, imageMin, imageMax);
    }

    /// <summary>Draws the compact isolation status only when it is useful.</summary>
    private void DrawCompactHeader()
    {
        // The title bar already says "Gpose Cast" and the window is GPose-only, so
        // avoid repeating that information inside the window. The status line only appears
        // while isolation is active because that is the state users need to watch while shooting.
        if (!plugin.Visibility.IsIsolationActive)
            return;

        var protectedSuffix = plugin.Visibility.ProtectedFashionAccessoryCount > 0
            ? $", {plugin.Visibility.ProtectedFashionAccessoryCount} linked accessory protected"
            : string.Empty;

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
            $"Isolation ON: {plugin.Visibility.HiddenCount} hidden, {plugin.CastGroup.PickedActors.Count} visible{protectedSuffix}");
    }

    /// <summary>Draws the compact top-row action buttons.</summary>
    private void DrawToolbar()
    {
        if (DrawToolbarButton("Self", ButtonTone.Positive))
            AddSelf();
        DrawTooltip("Add yourself to the picked group");

        ImGui.SameLine();
        if (DrawToolbarButton("Target", ButtonTone.Positive))
            AddCurrentTarget();
        DrawTooltip("Add your current GPose/world target");

        if (plugin.Configuration.EnableAccessoryDiagnostics)
        {
            ImGui.SameLine();
            if (DrawToolbarButton("Dump", ButtonTone.Neutral))
                DumpSelectedOrTargetNearbyActors();
            DrawTooltip("Dump nearby object-table actors for the selected row, target, or self to /xllog");

            ImGui.SameLine();
            using (ImRaii.Disabled(plugin.CastGroup.PickedActors.Count == 0))
            {
                if (DrawToolbarButton("State", ButtonTone.Neutral))
                    DumpIsolationState("manual UI state dump");
            }
            DrawTooltip("Dump Gpose Cast visibility state for picked actors, including protected ornaments, hidden state, keep reason, and alpha");

        }

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.CastGroup.PickedActors.Count == 0))
        {
            if (DrawToolbarButton("Clear", ButtonTone.Warning))
                RestoreSourcesDestroyImportedClonesAndClear("manual clear");
        }
        DrawTooltip("Restore hidden actors, remove imported temporary actors, and clear the picked group");

        ImGui.SameLine();
        DrawIsolationButton();

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.Visibility.HiddenCount == 0 && plugin.GposeImport.ImportedCloneCount == 0))
        {
            if (DrawToolbarButton("Restore", ButtonTone.Restore))
                RestoreSourcesDestroyImportedClonesAndClear("manual restore");
        }
        DrawTooltip("Restore hidden actors and remove imported temporary actors");
    }

    /// <summary>Small color set used by the compact toolbar buttons.</summary>
    private enum ButtonTone
    {
        Neutral,
        Positive,
        Isolate,
        Warning,
        Danger,
        Restore,
    }

    /// <summary>Draws a compact toolbar button with a subtle VenueHost-style color accent.</summary>
    private static bool DrawToolbarButton(string label, ButtonTone tone)
    {
        var (button, hovered, active) = tone switch
        {
            ButtonTone.Positive => (new Vector4(0.18f, 0.38f, 0.24f, 1f), new Vector4(0.22f, 0.48f, 0.30f, 1f), new Vector4(0.14f, 0.30f, 0.20f, 1f)),
            ButtonTone.Isolate => (new Vector4(0.38f, 0.30f, 0.14f, 1f), new Vector4(0.48f, 0.38f, 0.18f, 1f), new Vector4(0.30f, 0.24f, 0.12f, 1f)),
            ButtonTone.Warning => (new Vector4(0.36f, 0.25f, 0.14f, 1f), new Vector4(0.46f, 0.32f, 0.18f, 1f), new Vector4(0.28f, 0.20f, 0.12f, 1f)),
            ButtonTone.Danger => (new Vector4(0.42f, 0.18f, 0.18f, 1f), new Vector4(0.54f, 0.22f, 0.22f, 1f), new Vector4(0.34f, 0.14f, 0.14f, 1f)),
            ButtonTone.Restore => (new Vector4(0.18f, 0.32f, 0.42f, 1f), new Vector4(0.22f, 0.40f, 0.52f, 1f), new Vector4(0.14f, 0.26f, 0.34f, 1f)),
            _ => (new Vector4(0.25f, 0.25f, 0.25f, 1f), new Vector4(0.34f, 0.34f, 0.34f, 1f), new Vector4(0.20f, 0.20f, 0.20f, 1f)),
        };

        ImGui.PushStyleColor(ImGuiCol.Button, button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        var clicked = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    /// <summary>Draws the isolate/stop button with its safety disabled state.</summary>
    private void DrawIsolationButton()
    {
        var buttonText = plugin.Visibility.IsIsolationActive ? "Stop" : "Isolate";
        var disableButton = !plugin.GposeState.IsInGpose
            || (!plugin.Visibility.IsIsolationActive && plugin.CastGroup.PickedActors.Count == 0);

        using (ImRaii.Disabled(disableButton))
        {
            var tone = plugin.Visibility.IsIsolationActive ? ButtonTone.Danger : ButtonTone.Isolate;
            if (!DrawToolbarButton(buttonText, tone))
                return;

            if (plugin.Visibility.IsIsolationActive)
            {
                plugin.Visibility.StopIsolation();
                return;
            }

            var isolationCandidates = plugin.ActorScanner.ScanIsolationCandidates(plugin.Configuration.HideNpcs, plugin.Configuration.HideMinionsAndPets, plugin.Configuration.AllowExperimentalNonPlayerHiding);
            if (plugin.Configuration.EnableAccessoryDiagnostics)
                Plugin.Log.Information($"Gpose Cast: isolation requested with {plugin.CastGroup.PickedActors.Count} picked actor(s) and {isolationCandidates.Count} candidate actor(s).");
            plugin.Visibility.StartIsolation(isolationCandidates, plugin.CastGroup.PickedActors);
            if (plugin.Visibility.IsIsolationActive && plugin.Configuration.EnableAccessoryDiagnostics)
                DumpIsolationState("after isolation start");
        }

        DrawTooltip(plugin.Visibility.IsIsolationActive
            ? "Stop isolation and restore hidden actors"
            : plugin.Configuration.AllowExperimentalNonPlayerHiding
                ? "Hide everyone except picked actors, including enabled optional NPC/pet categories"
                : "Hide non-picked players. NPC/pet hiding is optional and disabled by default.");
    }

    /// <summary>Draws search and list filter controls.</summary>
    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##GposeCastSearch", "Search player...", ref searchText, 128);

        var filterRowY = ImGui.GetCursorScreenPos().Y;

        if (ImGui.Checkbox("Players only", ref playersOnly))
        {
            plugin.Configuration.PlayersOnly = playersOnly;
            plugin.Configuration.Save();
        }
        DrawTooltip("Only show player characters in the compact actor list");

        ImGui.SameLine();
        if (ImGui.Checkbox("Unnamed", ref includeUnnamed))
        {
            plugin.Configuration.IncludeUnnamed = includeUnnamed;
            plugin.Configuration.Save();
        }
        DrawTooltip("Show unnamed object-table entries in the compact actor list");

        DrawMascotOnFilterRow(filterRowY);
    }

    /// <summary>Draws the picked group table.</summary>
    private void DrawPickedGroup(float height)
    {
        ImGui.Text($"Picked: {plugin.CastGroup.PickedActors.Count}");

        using var child = ImRaii.Child("PickedGroupChild", new Vector2(0, height), true);
        if (!child.Success)
            return;

        if (plugin.CastGroup.PickedActors.Count == 0)
        {
            ImGui.TextDisabled("Use +Self, +Target, or + from the list.");
            return;
        }

        using var table = ImRaii.Table("PickedGroupTableCompact", 2, ImGuiTableFlags.RowBg);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 22f * ImGuiHelpers.GlobalScale);

        foreach (var picked in OrderedPickedActors())
        {
            var live = plugin.ActorScanner.FindCurrent(picked.Key);
            var display = live is null
                ? picked
                : !string.IsNullOrWhiteSpace(picked.DisplayAlias) && string.IsNullOrWhiteSpace(live.Name)
                    ? live.WithDisplayAlias(picked.DisplayAlias)
                    : live;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawActorName(display, live is null);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##picked-{picked.Key.GameObjectId}-{picked.Key.ObjectIndex}"))
                plugin.CastGroup.Remove(picked.Key);
            DrawTooltip("Remove from picked group");
        }
    }

    /// <summary>Returns picked actors in stable UI order, with self first.</summary>
    private IEnumerable<ActorEntry> OrderedPickedActors()
    {
        return plugin.CastGroup.PickedActors
            .OrderByDescending(actor => actor.IsLocalPlayer)
            .ThenBy(actor => actor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Draws the compact actor table.</summary>
    private void DrawActorTable(IReadOnlyList<ActorEntry> actors)
    {
        ImGui.Text($"Actors: {actors.Count}");

        using var child = ImRaii.Child("ActorListChild", Vector2.Zero, true);
        if (!child.Success)
            return;

        using var table = ImRaii.Table("ActorTableCompact", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 50f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 56f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var actor in actors)
            DrawActorRow(actor);
    }

    /// <summary>Draws one actor row in the compact table.</summary>
    private void DrawActorRow(ActorEntry actor)
    {
        var alreadyPicked = plugin.CastGroup.ContainsActor(actor);
        var alreadyHidden = plugin.Visibility.IsHidden(actor.Key);
        var canImport = plugin.GposeImport.CanImport(actor);
        var isSelected = selectedActorKey is { } selected && selected.Equals(actor.Key);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawActorActions(actor, alreadyPicked, alreadyHidden, canImport);

        ImGui.TableNextColumn();
        if (ImGui.Selectable($"{actor.DisplayName}##select-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
            selectedActorKey = actor.Key;
        DrawSourceTooltip(actor);
        DrawActorBadges(actor);

        ImGui.TableNextColumn();
        DrawState(actor, alreadyPicked, alreadyHidden);
    }

    /// <summary>Draws the compact add/import button and one visibility toggle for one actor row.</summary>
    private void DrawActorActions(ActorEntry actor, bool alreadyPicked, bool alreadyHidden, bool canImport)
    {
        if (actor.IsGposeActor || !canImport)
            DrawAddButton(actor, alreadyPicked);
        else
            DrawImportAndAddButton(actor, alreadyPicked);

        ImGui.SameLine();
        DrawVisibilityToggle(actor, alreadyHidden);
    }

    /// <summary>Draws a plain add button for actors already available to GPose.</summary>
    private void DrawAddButton(ActorEntry actor, bool alreadyPicked)
    {
        using (ImRaii.Disabled(alreadyPicked))
        {
            if (DrawSmallIconButton($"add-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}", FontAwesomeIcon.UserPlus))
                AddActorToPicked(actor);
        }

        DrawTooltip(alreadyPicked ? "Already picked" : "Add to picked group");
    }

    /// <summary>Draws an add button that imports a world actor into GPose before picking it.</summary>
    private void DrawImportAndAddButton(ActorEntry actor, bool alreadyPicked)
    {
        var importBusy = plugin.GposeImport.IsImportInProgress;
        using (ImRaii.Disabled(alreadyPicked || importBusy))
        {
            if (DrawSmallIconButton($"importadd-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}", FontAwesomeIcon.UserPlus))
            {
                plugin.GposeImport.ImportOverworldActor(actor, addToPickedAfterImport: true);
                selectedActorKey = actor.Key;
            }
        }

        DrawTooltip(importBusy ? "Another import is already running" : "Import to GPose and add to picked group");
    }

    /// <summary>Draws one compact visibility toggle for manual hide/restore.</summary>
    private void DrawVisibilityToggle(ActorEntry actor, bool alreadyHidden)
    {
        var disabled = actor.IsLocalPlayer
            || !plugin.GposeState.IsInGpose
            || (!actor.IsPlayerCharacter && !plugin.Configuration.AllowExperimentalNonPlayerHiding)
            || !actor.CanNativeAlphaHide;
        var icon = alreadyHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye;
        var action = alreadyHidden ? "Restore actor" : "Hide actor";

        using (ImRaii.Disabled(disabled))
        {
            if (DrawSmallIconButton($"vis-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}", icon))
            {
                if (alreadyHidden)
                    plugin.Visibility.Restore(actor.Key);
                else
                    plugin.Visibility.HideTest(actor);

                selectedActorKey = actor.Key;
            }
        }

        DrawTooltip(actor.IsLocalPlayer
            ? "Self is always kept visible"
            : !actor.CanNativeAlphaHide
                ? "This object is not a supported hide actor"
            : !actor.IsPlayerCharacter && !plugin.Configuration.AllowExperimentalNonPlayerHiding
                ? "Optional non-player hiding is currently disabled"
                : action);
    }

    /// <summary>Draws a compact FontAwesome icon button using Dalamud's icon font.</summary>
    private static bool DrawSmallIconButton(string id, FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            return ImGui.SmallButton($"{icon.ToIconString()}##{id}");
        }
    }

    /// <summary>Draws the actor name, with special styling for unloaded picked actors.</summary>
    private static void DrawActorName(ActorEntry actor, bool unloaded)
    {
        if (unloaded)
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), actor.DisplayName);
        else
            ImGui.TextUnformatted(actor.DisplayName);

        if (actor.IsLocalPlayer)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), "self");
        }
    }

    /// <summary>Shows actor source/type diagnostics as a tooltip instead of cluttering the row.</summary>
    private static void DrawSourceTooltip(ActorEntry actor)
    {
        if (!ImGui.IsItemHovered())
            return;

        var source = actor.IsGposeActor ? "GPose actor" : "World actor";
        var kind = actor.IsCompanionLike ? "pet/minion" : actor.IsNpcLike ? "NPC" : actor.ObjectKind.ToString();
        ImGui.SetTooltip($"{source} · {kind}");
    }

    /// <summary>Draws compact type badges beside an actor name.</summary>
    private static void DrawActorBadges(ActorEntry actor)
    {
        if (actor.IsCompanionLike)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("pet");
        }
        else if (actor.IsNpcLike)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("npc");
        }

        if (actor.IsLocalPlayer)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), "self");
        }
    }

    /// <summary>Draws the right-side row state label.</summary>
    private static void DrawState(ActorEntry actor, bool alreadyPicked, bool alreadyHidden)
    {
        // The state column always describes the actor's current visibility, even
        // when the actor is not part of the picked group. This keeps the compact
        // table readable while isolation is active.
        if (alreadyHidden)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "hidden");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), "visible");

        if (!actor.IsTargetable)
            DrawTooltip("Actor is visible but not targetable");
    }

    /// <summary>Dumps nearby actors around the selected row, current target, or self.</summary>
    private void DumpSelectedOrTargetNearbyActors()
    {
        var anchor = ResolveSelectedActorForDump();
        if (anchor is null)
        {
            Plugin.Log.Warning("Gpose Cast: no actor found for nearby dump.");
            return;
        }

        plugin.ActorScanner.DumpNearbyActors(anchor, ActorScannerService.DefaultNearbyDumpRadius, "manual UI dump");

        if (plugin.CastGroup.PickedActors.Count > 0)
            DumpIsolationState("manual UI dump visibility state");
    }

    /// <summary>Dumps Gpose Cast's current visibility decision state around picked actors.</summary>
    private void DumpIsolationState(string reason)
    {
        if (plugin.CastGroup.PickedActors.Count > 0)
        {
            plugin.Visibility.DumpIsolationDebug(plugin.CastGroup.PickedActors, plugin.CastGroup.PickedActors, reason);
            return;
        }

        var anchor = ResolveSelectedActorForDump();
        if (anchor is null)
        {
            Plugin.Log.Warning($"Gpose Cast: isolation state dump '{reason}' skipped because no actor could be resolved.");
            return;
        }

        plugin.Visibility.DumpIsolationDebug(new[] { anchor }, new[] { anchor }, reason);
    }

    /// <summary>Resolves the best current anchor for the nearby actor dump.</summary>
    private ActorEntry? ResolveSelectedActorForDump()
    {
        if (selectedActorKey is { } selected)
        {
            var selectedActor = plugin.ActorScanner.FindAnyCurrent(selected);
            if (selectedActor is not null)
                return selectedActor;
        }

        var target = plugin.GposeState.IsInGpose
            ? Plugin.TargetManager.GPoseTarget ?? Plugin.TargetManager.Target
            : Plugin.TargetManager.Target ?? Plugin.TargetManager.GPoseTarget;
        var targetActor = plugin.ActorScanner.FromGameObject(target);
        if (targetActor is not null)
            return targetActor;

        return plugin.ActorScanner.FindLocalPlayer();
    }

    /// <summary>Adds the local player to the picked group.</summary>
    private void AddSelf()
    {
        var actor = plugin.ActorScanner.FindLocalPlayer();
        if (actor is null)
        {
            Plugin.Log.Warning("Gpose Cast: no local player found to add.");
            return;
        }

        AddActorToPicked(actor);
    }

    /// <summary>Restores original hidden source actors, removes imported temporary actors, and clears stale picked entries.</summary>
    private void RestoreSourcesDestroyImportedClonesAndClear(string reason)
    {
        plugin.Visibility.StopIsolation();
        plugin.GposeImport.DestroyAllImportedClones(reason);
        plugin.CastGroup.Clear();
        selectedActorKey = null;
    }

    /// <summary>Adds the current target to the picked group.</summary>
    private void AddCurrentTarget()
    {
        var target = plugin.GposeState.IsInGpose
            ? Plugin.TargetManager.GPoseTarget ?? Plugin.TargetManager.Target
            : Plugin.TargetManager.Target ?? Plugin.TargetManager.GPoseTarget;
        var actor = plugin.ActorScanner.FromGameObject(target);

        if (actor is null)
        {
            Plugin.Log.Warning("Gpose Cast: no valid current target to add.");
            return;
        }

        AddActorToPicked(actor);
    }

    /// <summary>Adds an actor and restores it if it was hidden by active isolation.</summary>
    private void AddActorToPicked(ActorEntry actor)
    {
        plugin.CastGroup.Add(actor);
        selectedActorKey = actor.Key;

        if (plugin.Visibility.IsIsolationActive)
            plugin.Visibility.Restore(actor.Key);
    }

    /// <summary>Handles import completion events from the import service.</summary>
    private void OnImportCompleted(ActorEntry importedActor, ActorEntry sourceActor, bool addToPickedAfterImport, IReadOnlyList<ActorEntry> linkedFashionAccessories, bool linkedChildImportUsed)
    {
        if (linkedChildImportUsed)
        {
            plugin.Visibility.HideImportedSourceDuplicate(sourceActor, linkedFashionAccessories, $"linked-child import of {importedActor.DisplayName}");
        }
        else if (linkedFashionAccessories.Count > 0)
        {
            plugin.Visibility.ProtectFashionAccessories(linkedFashionAccessories, $"import of {importedActor.DisplayName}");
        }

        if (plugin.Configuration.EnableAccessoryDiagnostics)
        {
            plugin.ActorScanner.DumpNearbyActors(importedActor, ActorScannerService.DefaultNearbyDumpRadius, "after remote player import");
            var postImportPickedActors = plugin.CastGroup.PickedActors.ToList();
            if (addToPickedAfterImport)
                postImportPickedActors.Add(importedActor);

            if (postImportPickedActors.Count > 0)
                plugin.Visibility.DumpIsolationDebug(new[] { importedActor }, postImportPickedActors, "after remote player import visibility state");
        }

        selectedActorKey = importedActor.Key;
        if (!addToPickedAfterImport)
            return;

        plugin.CastGroup.Add(importedActor);
        if (plugin.Visibility.IsIsolationActive)
        {
            plugin.Visibility.Restore(importedActor.Key);
            if (plugin.Configuration.EnableAccessoryDiagnostics)
                DumpIsolationState("after import during active isolation");
        }
    }

    /// <summary>Draws a tooltip for the previous ImGui item when hovered.</summary>
    private static void DrawTooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
