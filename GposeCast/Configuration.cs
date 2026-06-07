using System;
using Dalamud.Configuration;

namespace GposeCast;

/// <summary>
/// Persistent plugin configuration saved by Dalamud.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>Dalamud configuration schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Default state for the compact actor list's player filter.</summary>
    public bool PlayersOnly { get; set; } = true;

    /// <summary>Default state for showing unnamed objects in the compact actor list.</summary>
    public bool IncludeUnnamed { get; set; } = false;

    /// <summary>Safety option: never hide the local player during isolation.</summary>
    public bool KeepSelfVisible { get; set; } = true;

    /// <summary>Safety option reserved for future party-aware isolation rules.</summary>
    public bool KeepPartyVisible { get; set; } = true;

    /// <summary>Legacy field kept so old config files deserialize safely.</summary>
    public bool KeepNpcsVisible { get; set; } = true;

    /// <summary>Whether isolation should hide NPC/event/enemy-like entries.</summary>
    public bool HideNpcs { get; set; } = true;

    /// <summary>Whether isolation should hide pets, minions, mounts, and ornaments.</summary>
    public bool HideMinionsAndPets { get; set; } = true;

    /// <summary>Whether isolation should keep enforcing itself as actors load in.</summary>
    public bool AutoHideNewArrivals { get; set; } = true;

    /// <summary>Whether the main window should open automatically when GPose starts.</summary>
    public bool AutoOpenInGpose { get; set; } = true;

    /// <summary>Whether the compact main window should show the decorative camera peepo mascot.</summary>
    public bool ShowMascot { get; set; } = true;

    /// <summary>Legacy field kept so old config files deserialize safely. Gpose Cast now always closes the main window when GPose ends.</summary>
    public bool AutoCloseWhenLeavingGpose { get; set; } = true;

    /// <summary>Saves the current configuration using Dalamud's plugin config store.</summary>
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
