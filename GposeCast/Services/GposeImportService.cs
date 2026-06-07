using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Common.Math;
using GposeCast.Models;
using GposeCast.Structs;

namespace GposeCast.Services;

/// <summary>
/// Spawns an overworld player into the GPose actor table so Brio/Ktisis can see it.
/// </summary>
/// <remarks>
/// This follows the same broad strategy observed in KtisisPyon: allocate a
/// GPoseActorEvent, replace the finalize vtable slot to mark the clone as a
/// temporary/local GPose actor, dispatch the event, then wait for the expected object
/// table slot to become valid.
/// </remarks>
public sealed class GposeImportService : IDisposable
{
    private const ushort SpawnStart = 200;
    private const ushort SpawnEnd = 238;
    private const int VfSize = 9;
    private const int ImportTimeoutMilliseconds = 10000;

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly ActorScannerService actorScanner;

    private unsafe nint* hookVfTable;
    private unsafe nint* eventVfTable;
    private bool initialized;

    private static FinalizeDelegate? finalizeHookDelegate;
    private static FinalizeDelegate? finalizeOriginal;

#pragma warning disable CS0649 // Dalamud signature scanner assigns these fields at runtime.
    [Signature("48 8D 05 ?? ?? ?? ?? ?? ?? ?? 33 C0 0F 11 42 ?? 0F 11 42 ?? 0F 11 42 ?? 0F 11 42 ?? 0F 11 82 ?? ?? ?? ?? 4C 89 82", ScanType = ScanType.StaticAddress)]
    private unsafe nint* eventVfTableSig = null;

    [Signature("80 61 0C FC 48 8D 05 ?? ?? ?? ?? 4C 8B C9")]
    private GPoseActorEventCtorDelegate? gPoseActorEventCtor;

    [Signature("48 89 5C 24 ?? 48 89 54 24 ?? 57 48 83 EC 20 48 8B 02")]
    private DispatchEventDelegate? dispatchEvent;
#pragma warning restore CS0649

    /// <summary>Latest user-facing import status for the compact header.</summary>
    public string LastImportStatus { get; private set; } = "Import not tested yet.";

    /// <summary>Raised when an import completes and the spawned GPose actor is found.</summary>
    public event Action<ActorEntry, bool>? ImportCompleted;

    /// <summary>Creates and initializes the import service.</summary>
    public GposeImportService(IClientState clientState, IObjectTable objectTable, IFramework framework, ActorScannerService actorScanner)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.actorScanner = actorScanner;

        TryInitializeSpawner();
    }

    /// <summary>Returns true when the actor can currently be imported into GPose.</summary>
    public bool CanImport(ActorEntry actor)
    {
        return clientState.IsGPosing
            && initialized
            && actor.IsOverworldActor
            && actor.IsPlayerCharacter
            && !actor.IsLocalPlayer
            && actor.Address != nint.Zero;
    }

    /// <summary>
    /// Starts an asynchronous GPose import for an overworld actor.
    /// </summary>
    public bool ImportOverworldActor(ActorEntry actor, bool addToPickedAfterImport = false)
    {
        if (!clientState.IsGPosing)
        {
            LastImportStatus = "Import refused: enter GPose first.";
            return false;
        }

        if (!initialized)
        {
            LastImportStatus = "Import refused: actor spawner is not initialized. Check /xllog for signature errors.";
            return false;
        }

        var live = actorScanner.FindCurrent(actor.Key) ?? actor;
        if (!CanImport(live))
        {
            LastImportStatus = $"Cannot import {actor.DisplayName}: actor must be a loaded world player while in GPose.";
            Plugin.Log.Warning($"Gpose Cast: {LastImportStatus}");
            return false;
        }

        LastImportStatus = $"Requested GPose clone spawn for {live.DisplayName}...";
        _ = ImportOverworldActorAsync(live, addToPickedAfterImport);
        return true;
    }

    /// <summary>Runs the import flow and marshals UI-facing updates back to the framework thread.</summary>
    private async Task ImportOverworldActorAsync(ActorEntry live, bool addToPickedAfterImport)
    {
        try
        {
            var address = await CreateActorAsync(live);

            await framework.RunOnFrameworkThread(() =>
            {
                RefreshKtisisActorList();

                var importedActor = actorScanner.FindByAddress(address);
                if (importedActor is not null)
                    ImportCompleted?.Invoke(importedActor, addToPickedAfterImport);

                LastImportStatus = importedActor is null
                    ? $"Imported {live.DisplayName} into GPose at 0x{address.ToInt64():X}. Refresh Brio/Ktisis if needed."
                    : $"Imported {importedActor.DisplayName} into GPose at index {importedActor.Key.ObjectIndex}.";
                Plugin.Log.Information($"Gpose Cast: {LastImportStatus}");
            });
        }
        catch (Exception ex)
        {
            LastImportStatus = $"Import failed for {live.DisplayName}: {ex.Message}";
            Plugin.Log.Error(ex, $"Gpose Cast: failed to import {live.DisplayName} to GPose.");
        }
    }

    /// <summary>Initializes signatures and creates a copied vtable with a custom finalize hook.</summary>
    private unsafe void TryInitializeSpawner()
    {
        try
        {
            Plugin.GameInteropProvider.InitializeFromAttributes(this);

            eventVfTable = eventVfTableSig;
            if (eventVfTable == null)
                throw new InvalidOperationException("GPoseActorEvent vtable signature was not found.");
            if (gPoseActorEventCtor == null)
                throw new InvalidOperationException("GPoseActorEvent constructor signature was not found.");
            if (dispatchEvent == null)
                throw new InvalidOperationException("Event dispatch signature was not found.");

            var copiedVTable = (nint*)Marshal.AllocHGlobal(sizeof(nint) * VfSize);
            for (var i = 0; i < VfSize; i++)
            {
                var original = eventVfTable[i];
                if (i == 2)
                {
                    finalizeOriginal = Marshal.GetDelegateForFunctionPointer<FinalizeDelegate>(original);
                    finalizeHookDelegate ??= FinalizeHook;
                    copiedVTable[i] = Marshal.GetFunctionPointerForDelegate(finalizeHookDelegate);
                }
                else
                {
                    copiedVTable[i] = original;
                }
            }

            hookVfTable = copiedVTable;
            initialized = true;
            LastImportStatus = "Real GPose import spawner initialized.";
            Plugin.Log.Information("Gpose Cast: real GPose import spawner initialized.");
        }
        catch (Exception ex)
        {
            initialized = false;
            LastImportStatus = $"Real import unavailable: {ex.Message}";
            Plugin.Log.Error(ex, "Gpose Cast: failed to initialize real GPose import spawner.");
        }
    }

    /// <summary>Dispatches the event and waits for the expected GPose slot to become valid.</summary>
    private async Task<nint> CreateActorAsync(ActorEntry original)
    {
        using var source = new CancellationTokenSource();
        source.CancelAfter(ImportTimeoutMilliseconds);

        var index = await framework.RunOnFrameworkThread(() =>
        {
            if (!TryDispatch(original, out var spawnIndex))
                throw new InvalidOperationException("GPose object table is full. KtisisPyon uses slots 200-238.");
            return spawnIndex;
        });

        while (!source.IsCancellationRequested)
        {
            var address = await framework.RunOnFrameworkThread(() =>
            {
                var spawned = objectTable[(int)index];
                if (spawned is null || !spawned.IsValid() || spawned.Address == nint.Zero)
                    return nint.Zero;
                return spawned.Address;
            });

            if (address != nint.Zero)
                return address;

            await Task.Delay(10, CancellationToken.None);
        }

        throw new TaskCanceledException($"Actor spawn at index {index} timed out.");
    }

    /// <summary>Finds the next free GPose actor slot and dispatches the spawn event.</summary>
    private bool TryDispatch(ActorEntry original, out uint index)
    {
        index = CalculateNextIndex();
        if (index == ushort.MaxValue)
            return false;

        Plugin.Log.Information($"Gpose Cast: dispatching GPose actor spawn, expecting slot {index}.");
        DispatchSpawn(original);
        return true;
    }

    /// <summary>Allocates and dispatches the native GPose actor event.</summary>
    private unsafe void DispatchSpawn(ActorEntry original)
    {
        if (hookVfTable == null || gPoseActorEventCtor == null || dispatchEvent == null)
            throw new InvalidOperationException("Actor spawner is not initialized.");

        var player = (Character*)original.Address;
        if (player == null || !player->GameObject.IsCharacter())
            throw new InvalidOperationException($"Original actor '{original.DisplayName}' ({original.Key.ObjectIndex}) is invalid.");

        var task = IMemorySpace.GetDefaultSpace()->Malloc<GPoseActorEvent>();
        if (task == null)
            throw new InvalidOperationException("Failed to allocate GPoseActorEvent.");

        // Parameters mirror the KtisisPyon path that successfully produced GPose clones.
        gPoseActorEventCtor(task, player, &player->GameObject.Position, 64U, 30, 0, 4294934523U, true);
        task->VTable = hookVfTable;

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            throw new InvalidOperationException("EventFramework is null.");

        // KtisisPyon dispatches through EventFramework + 432 + 152.
        var handler = (nint)eventFramework + 432 + 152;
        dispatchEvent(handler, task);
    }

    /// <summary>Finds the first free GPose slot in the KtisisPyon spawn range.</summary>
    private ushort CalculateNextIndex()
    {
        for (ushort i = SpawnStart; i <= SpawnEnd; i++)
        {
            if (objectTable[i] == null)
                return i;
        }

        return ushort.MaxValue;
    }

    /// <summary>Marks the spawned actor as a local temporary GPose entity before finalization.</summary>
    private static unsafe void FinalizeHook(GPoseActorEvent* self, nint a2, nint a3)
    {
        if (self->Character != null)
            self->EntityId = unchecked((ulong)-536870912L);

        finalizeOriginal?.Invoke(self, a2, a3);
    }

    /// <summary>Asks Ktisis/KtisisPyon to refresh, if its IPC endpoint is available.</summary>
    private static void RefreshKtisisActorList()
    {
        try
        {
            var refreshActors = Plugin.PluginInterface.GetIpcSubscriber<bool>("Ktisis.RefreshActors");
            if (refreshActors.HasFunction)
                refreshActors.InvokeFunc();
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Gpose Cast: Ktisis.RefreshActors IPC not available or failed.");
        }
    }

    /// <summary>Frees the copied vtable allocated during initialization.</summary>
    public unsafe void Dispose()
    {
        if (hookVfTable != null)
        {
            Marshal.FreeHGlobal((nint)hookVfTable);
            hookVfTable = null;
        }
    }

    private unsafe delegate nint GPoseActorEventCtorDelegate(GPoseActorEvent* self, Character* target, Vector3* position, uint a4, int a5, int a6, uint a7, bool a8);
    private unsafe delegate nint DispatchEventDelegate(nint handler, GPoseActorEvent* task);
    private unsafe delegate void FinalizeDelegate(GPoseActorEvent* self, nint a2, nint a3);
}
