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
    public int Version { get; set; } = 6;

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

    /// <summary>Whether isolation should hide supported NPC/enemy-like character entries.</summary>
    public bool HideNpcs { get; set; } = false;

    /// <summary>Whether isolation should hide supported pets, minions, mounts, and ornaments.</summary>
    public bool HideMinionsAndPets { get; set; } = false;

    /// <summary>Allows optional non-player alpha writes for supported character-like objects.</summary>
    public bool AllowExperimentalNonPlayerHiding { get; set; } = false;

    /// <summary>Whether isolation should keep enforcing itself as actors load in.</summary>
    public bool AutoHideNewArrivals { get; set; } = true;

    /// <summary>Attempts to clear and keep suppressing lingering emote VFX, such as glowsticks, on non-picked players hidden during isolation.</summary>
    public bool ClearEmoteVfxOnIsolation { get; set; } = false;

    /// <summary>Whether the main window should open automatically when GPose starts.</summary>
    public bool AutoOpenInGpose { get; set; } = true;

    /// <summary>Legacy/disabled switch. Fashion accessory and mount preservation is temporarily shelved; imports use the stable legacy GPose event path.</summary>
    public bool HandleLinkedFashionAccessories { get; set; } = false;

    /// <summary>Whether the compact main window should show the decorative camera peepo mascot.</summary>
    public bool ShowMascot { get; set; } = true;

    /// <summary>Whether import/isolation diagnostic buttons and verbose debug dumps should be shown.</summary>
    public bool EnableAccessoryDiagnostics { get; set; } = false;

    /// <summary>Internal compatibility switch for the shelved 0.9 linked-child import path. Kept for config compatibility, but disabled in public builds.</summary>
    public bool UseNativeCompanionCloneForLinkedAccessories { get; set; } = false;

    /// <summary>Legacy field kept so old config files deserialize safely. The separate ornament clone path has been retired.</summary>
    public bool CloneLinkedFashionAccessories { get; set; } = false;

    /// <summary>Legacy field kept so old config files deserialize safely. The manual bind lab UI has been removed.</summary>
    public bool AllowExperimentalAccessoryBindPatch { get; set; } = false;

    /// <summary>Legacy field kept so old config files deserialize safely. Gpose Cast now always closes the main window when GPose ends.</summary>
    public bool AutoCloseWhenLeavingGpose { get; set; } = true;

    /// <summary>
    /// Applies one-time safety migrations when loading older configuration files.
    /// </summary>
    public void MigrateIfNeeded()
    {
        var changed = false;

        if (Version < 2)
        {
            // Keep existing users on the stable players-only isolation path until they
            // explicitly opt into optional non-player hiding.
            HideNpcs = false;
            HideMinionsAndPets = false;
            AllowExperimentalNonPlayerHiding = false;
            Version = 2;
            changed = true;
        }

        if (Version < 3)
        {
            CloneLinkedFashionAccessories = false;
            AllowExperimentalAccessoryBindPatch = false;
            UseNativeCompanionCloneForLinkedAccessories = true;
            Version = 3;
            changed = true;
        }

        if (Version < 4)
        {
            // In 0.9.0.0, linked accessories and mounts become part of the normal import
            // behavior instead of an experiment. Verbose diagnostics remain disabled.
            HandleLinkedFashionAccessories = true;
            UseNativeCompanionCloneForLinkedAccessories = true;
            CloneLinkedFashionAccessories = false;
            AllowExperimentalAccessoryBindPatch = false;
            EnableAccessoryDiagnostics = false;
            Version = 4;
            changed = true;
        }

        if (Version < 5)
        {
            // New in 0.9.0.1. This touches animation state before hiding, so keep it
            // opt-in for existing users until it has had wider GPose testing.
            ClearEmoteVfxOnIsolation = false;
            Version = 5;
            changed = true;
        }

        if (Version < 6)
        {
            // 0.9.0.3 shelves linked accessory/mount preservation and returns public imports
            // to the stable legacy GPose event path. Keep the old fields for config safety.
            Version = 6;
            changed = true;
        }

        // Public safety clamp: experimental/dev configs may have a higher schema version
        // with linked-child diagnostics enabled. This release intentionally disables them.
        if (HandleLinkedFashionAccessories
            || UseNativeCompanionCloneForLinkedAccessories
            || CloneLinkedFashionAccessories
            || AllowExperimentalAccessoryBindPatch
            || EnableAccessoryDiagnostics
            || Version > 6)
        {
            HandleLinkedFashionAccessories = false;
            UseNativeCompanionCloneForLinkedAccessories = false;
            CloneLinkedFashionAccessories = false;
            AllowExperimentalAccessoryBindPatch = false;
            EnableAccessoryDiagnostics = false;
            Version = 6;
            changed = true;
        }

        if (changed)
            Save();
    }

    /// <summary>Saves the current configuration using Dalamud's plugin config store.</summary>
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
