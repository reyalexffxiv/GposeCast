using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace GposeCast.Windows;

/// <summary>
/// Small settings window for defaults and isolation behavior.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    /// <summary>Creates the configuration window.</summary>
    public ConfigWindow(Plugin plugin) : base("Gpose Cast Settings###GposeCastConfig")
    {
        Size = new Vector2(390, 280);
        SizeCondition = ImGuiCond.FirstUseEver;
        configuration = plugin.Configuration;
    }

    /// <summary>No unmanaged UI resources are owned by this window.</summary>
    public void Dispose() { }

    /// <inheritdoc />
    public override void Draw()
    {
        ImGui.TextWrapped("Gpose Cast is designed to work inside GPose. Hidden actors are restored when leaving GPose or unloading the plugin.");
        ImGui.Separator();

        DrawCheckbox("Players only by default", nameof(Configuration.PlayersOnly), configuration.PlayersOnly, value => configuration.PlayersOnly = value);
        DrawCheckbox("Include unnamed actors", nameof(Configuration.IncludeUnnamed), configuration.IncludeUnnamed, value => configuration.IncludeUnnamed = value);
        ImGui.Spacing();
        DrawCheckbox("Keep self visible", nameof(Configuration.KeepSelfVisible), configuration.KeepSelfVisible, value => configuration.KeepSelfVisible = value);
        DrawCheckbox("Auto-hide new arrivals", nameof(Configuration.AutoHideNewArrivals), configuration.AutoHideNewArrivals, value => configuration.AutoHideNewArrivals = value);
        DrawCheckbox("Auto-open in GPose", nameof(Configuration.AutoOpenInGpose), configuration.AutoOpenInGpose, value => configuration.AutoOpenInGpose = value);
        DrawCheckbox("Show peepo mascot", nameof(Configuration.ShowMascot), configuration.ShowMascot, value => configuration.ShowMascot = value);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.35f, 1f), "Optional non-player hiding");

        DrawCheckbox("Enable optional NPC/pet hiding", nameof(Configuration.AllowExperimentalNonPlayerHiding), configuration.AllowExperimentalNonPlayerHiding, value => configuration.AllowExperimentalNonPlayerHiding = value);

        using (ImRaii.Disabled(!configuration.AllowExperimentalNonPlayerHiding))
        {
            DrawCheckbox("Hide NPCs", nameof(Configuration.HideNpcs), configuration.HideNpcs, value => configuration.HideNpcs = value);
            DrawCheckbox("Hide minions and pets", nameof(Configuration.HideMinionsAndPets), configuration.HideMinionsAndPets, value => configuration.HideMinionsAndPets = value);
        }
    }

    /// <summary>Draws a persisted checkbox and saves immediately when it changes.</summary>
    private void DrawCheckbox(string label, string id, bool value, Action<bool> setter)
    {
        var temp = value;
        if (ImGui.Checkbox($"{label}##{id}", ref temp))
        {
            setter(temp);
            configuration.Save();
        }
    }
}
