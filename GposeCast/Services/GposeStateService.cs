using Dalamud.Plugin.Services;

namespace GposeCast.Services;

/// <summary>
/// Tracks GPose state transitions using Dalamud's client-state API.
/// </summary>
public sealed class GposeStateService
{
    private readonly IClientState clientState;
    private bool lastKnownState;

    /// <summary>Creates a GPose state tracker.</summary>
    public GposeStateService(IClientState clientState)
    {
        this.clientState = clientState;
        lastKnownState = clientState.IsGPosing;
    }

    /// <summary>Returns true when the local client is currently in GPose.</summary>
    public bool IsInGpose => clientState.IsGPosing;

    /// <summary>
    /// Updates the cached GPose state and returns transition flags.
    /// </summary>
    public (bool Entered, bool Left) UpdateAndCheckTransition()
    {
        var current = clientState.IsGPosing;
        var entered = current && !lastKnownState;
        var left = !current && lastKnownState;
        lastKnownState = current;
        return (entered, left);
    }
}
