using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using GposeCast.Models;

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
    private readonly Plugin plugin;
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
            MinimumSize = new Vector2(250, 390),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        playersOnly = plugin.Configuration.PlayersOnly;
        includeUnnamed = plugin.Configuration.IncludeUnnamed;
        plugin.GposeImport.ImportCompleted += OnImportCompleted;
    }

    /// <summary>Unsubscribes window event handlers.</summary>
    public void Dispose()
    {
        plugin.GposeImport.ImportCompleted -= OnImportCompleted;
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

        // Keep sweeping newly loaded candidates while isolation is active. This is what
        // makes late arrivals disappear after the picked group has already been isolated.
        if (plugin.Visibility.IsIsolationActive && plugin.Configuration.AutoHideNewArrivals)
        {
            var isolationCandidates = plugin.ActorScanner.ScanIsolationCandidates(plugin.Configuration.HideNpcs, plugin.Configuration.HideMinionsAndPets);
            plugin.Visibility.EnforceIsolation(isolationCandidates, plugin.CastGroup.PickedActors);
        }

        var pickedHeight = MathF.Max(82f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * 0.22f);
        DrawPickedGroup(pickedHeight);
        ImGui.Spacing();
        DrawActorTable(actors);
    }

    /// <summary>Draws the title/status area.</summary>
    private void DrawCompactHeader()
    {
        var gposeText = plugin.GposeState.IsInGpose ? "GPose" : "World";
        var statusText = plugin.Visibility.IsIsolationActive
            ? $"Isolation: {plugin.Visibility.HiddenCount} hidden, {plugin.CastGroup.PickedActors.Count} picked"
            : plugin.GposeImport.LastImportStatus;

        ImGui.TextUnformatted($"Gpose Cast · {gposeText}");
        if (string.IsNullOrWhiteSpace(statusText))
            return;

        if (plugin.Visibility.IsIsolationActive)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), statusText);
        else
            ImGui.TextDisabled(statusText);
    }

    /// <summary>Draws the compact top-row action buttons.</summary>
    private void DrawToolbar()
    {
        if (ImGui.SmallButton("Cfg"))
            plugin.ToggleConfigUi();
        DrawTooltip("Settings");

        ImGui.SameLine();
        if (ImGui.SmallButton("+Self"))
            AddSelf();
        DrawTooltip("Add yourself to the picked group");

        ImGui.SameLine();
        if (ImGui.SmallButton("+Target"))
            AddCurrentTarget();
        DrawTooltip("Add your current GPose/world target");

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.CastGroup.PickedActors.Count == 0))
        {
            if (ImGui.SmallButton("Clear"))
            {
                plugin.Visibility.StopIsolation();
                plugin.CastGroup.Clear();
            }
        }
        DrawTooltip("Clear picked group and stop isolation");

        ImGui.SameLine();
        DrawIsolationButton();

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.Visibility.HiddenCount == 0))
        {
            if (ImGui.SmallButton($"Restore ({plugin.Visibility.HiddenCount})"))
                plugin.Visibility.StopIsolation();
        }
        DrawTooltip("Restore every actor hidden by Gpose Cast");
    }

    /// <summary>Draws the isolate/stop button with its safety disabled state.</summary>
    private void DrawIsolationButton()
    {
        var buttonText = plugin.Visibility.IsIsolationActive ? "Stop" : "Isolate";
        var disableButton = !plugin.GposeState.IsInGpose
            || (!plugin.Visibility.IsIsolationActive && plugin.CastGroup.PickedActors.Count == 0);

        using (ImRaii.Disabled(disableButton))
        {
            if (!ImGui.SmallButton(buttonText))
                return;

            if (plugin.Visibility.IsIsolationActive)
            {
                plugin.Visibility.StopIsolation();
                return;
            }

            var isolationCandidates = plugin.ActorScanner.ScanIsolationCandidates(plugin.Configuration.HideNpcs, plugin.Configuration.HideMinionsAndPets);
            plugin.Visibility.StartIsolation(isolationCandidates, plugin.CastGroup.PickedActors);
        }

        DrawTooltip(plugin.Visibility.IsIsolationActive
            ? "Stop isolation and restore hidden actors"
            : "Hide everyone except picked actors");
    }

    /// <summary>Draws search and list filter controls.</summary>
    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##GposeCastSearch", "Search actor name...", ref searchText, 128);

        if (ImGui.Checkbox("Players", ref playersOnly))
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

        using var table = ImRaii.Table("PickedGroupTableCompact", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var picked in OrderedPickedActors())
        {
            var live = plugin.ActorScanner.FindCurrent(picked.Key);
            var display = live ?? picked;

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

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 68f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 34f * ImGuiHelpers.GlobalScale);
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
        DrawActorBadges(actor);

        ImGui.TableNextColumn();
        DrawState(actor, alreadyPicked, alreadyHidden);
    }

    /// <summary>Draws the +, H, and R action buttons for one actor row.</summary>
    private void DrawActorActions(ActorEntry actor, bool alreadyPicked, bool alreadyHidden, bool canImport)
    {
        if (actor.IsGposeActor || !canImport)
            DrawAddButton(actor, alreadyPicked);
        else
            DrawImportAndAddButton(actor, alreadyPicked);

        ImGui.SameLine();
        DrawHideButton(actor, alreadyHidden);

        ImGui.SameLine();
        DrawRestoreButton(actor, alreadyHidden);
    }

    /// <summary>Draws a plain add button for actors already available to GPose.</summary>
    private void DrawAddButton(ActorEntry actor, bool alreadyPicked)
    {
        using (ImRaii.Disabled(alreadyPicked))
        {
            if (ImGui.SmallButton($"+##add-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}"))
                AddActorToPicked(actor);
        }

        DrawTooltip(alreadyPicked ? "Already picked" : "Add to picked group");
    }

    /// <summary>Draws an add button that imports a world actor into GPose before picking it.</summary>
    private void DrawImportAndAddButton(ActorEntry actor, bool alreadyPicked)
    {
        using (ImRaii.Disabled(alreadyPicked))
        {
            if (ImGui.SmallButton($"+##importadd-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}"))
            {
                plugin.GposeImport.ImportOverworldActor(actor, addToPickedAfterImport: true);
                selectedActorKey = actor.Key;
            }
        }

        DrawTooltip("Import to GPose and add to picked group");
    }

    /// <summary>Draws the manual hide-test button.</summary>
    private void DrawHideButton(ActorEntry actor, bool alreadyHidden)
    {
        var hideDisabled = actor.IsLocalPlayer || alreadyHidden || !plugin.GposeState.IsInGpose;
        using (ImRaii.Disabled(hideDisabled))
        {
            if (ImGui.SmallButton($"H##hide-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}"))
            {
                plugin.Visibility.HideTest(actor);
                selectedActorKey = actor.Key;
            }
        }

        DrawTooltip("Hide actor");
    }

    /// <summary>Draws the manual restore button.</summary>
    private void DrawRestoreButton(ActorEntry actor, bool alreadyHidden)
    {
        using (ImRaii.Disabled(!alreadyHidden))
        {
            if (ImGui.SmallButton($"R##restore-{actor.Key.GameObjectId}-{actor.Key.ObjectIndex}"))
                plugin.Visibility.Restore(actor.Key);
        }

        DrawTooltip("Restore actor");
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

    /// <summary>Draws compact source/type badges beside an actor name.</summary>
    private static void DrawActorBadges(ActorEntry actor)
    {
        ImGui.SameLine();
        ImGui.TextDisabled(actor.IsGposeActor ? "G" : "W");

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
        if (alreadyHidden)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "hide");
        else if (alreadyPicked)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), "pick");
        else if (!actor.IsTargetable)
            ImGui.TextDisabled("no");
        else
            ImGui.TextDisabled("vis");
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
    private void OnImportCompleted(ActorEntry importedActor, bool addToPickedAfterImport)
    {
        selectedActorKey = importedActor.Key;
        if (!addToPickedAfterImport)
            return;

        plugin.CastGroup.Add(importedActor);
        if (plugin.Visibility.IsIsolationActive)
            plugin.Visibility.Restore(importedActor.Key);
    }

    /// <summary>Draws a tooltip for the previous ImGui item when hovered.</summary>
    private static void DrawTooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }
}
