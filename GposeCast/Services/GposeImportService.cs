using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
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
    private readonly object importLock = new();

    private CancellationTokenSource? activeImportCancellationSource;
    private string? activeImportCancellationReason;
    private volatile bool importInProgress;

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

    /// <summary>True while one GPose import request is running.</summary>
    public bool IsImportInProgress => importInProgress;

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

        CancellationTokenSource cancellationSource;
        lock (importLock)
        {
            if (importInProgress)
            {
                LastImportStatus = "Import refused: another import is already running.";
                return false;
            }

            cancellationSource = new CancellationTokenSource(ImportTimeoutMilliseconds);
            activeImportCancellationSource = cancellationSource;
            activeImportCancellationReason = null;
            importInProgress = true;
        }

        var live = actorScanner.FindAnyCurrent(actor.Key, actor.Name, actor.ObjectKind) ?? actor;
        if (!CanImport(live))
        {
            FinishImport(cancellationSource);
            LastImportStatus = $"Cannot import {actor.DisplayName}: actor must be a loaded world player while in GPose.";
            Plugin.Log.Warning($"Gpose Cast: {LastImportStatus}");
            return false;
        }

        LastImportStatus = $"Requested GPose clone spawn for {live.DisplayName}...";
        _ = ImportOverworldActorAsync(live, addToPickedAfterImport, cancellationSource);
        return true;
    }

    /// <summary>Cancels a pending import if one is running.</summary>
    public void CancelPendingImport(string reason)
    {
        CancellationTokenSource? source;
        lock (importLock)
        {
            if (!importInProgress || activeImportCancellationSource is null)
                return;

            activeImportCancellationReason = reason;
            source = activeImportCancellationSource;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The import completed at the same time as the cancel request. Nothing to do.
        }
    }

    /// <summary>Runs the import flow and marshals UI-facing updates back to the framework thread.</summary>
    private async Task ImportOverworldActorAsync(ActorEntry live, bool addToPickedAfterImport, CancellationTokenSource cancellationSource)
    {
        try
        {
            var importedActor = await CreateActorAsync(live, cancellationSource.Token);

            await framework.RunOnFrameworkThread(() =>
            {
                cancellationSource.Token.ThrowIfCancellationRequested();
                RefreshKtisisActorList();

                var finalActor = actorScanner.FindByAddress(importedActor.Address) ?? importedActor;
                ImportCompleted?.Invoke(finalActor, addToPickedAfterImport);

                LastImportStatus = $"Imported {finalActor.DisplayName} into GPose at index {finalActor.Key.ObjectIndex}.";
                Plugin.Log.Information($"Gpose Cast: {LastImportStatus}");
            });
        }
        catch (OperationCanceledException)
        {
            var reason = GetImportCancellationReason() ?? (clientState.IsGPosing ? "request cancelled." : "GPose ended.");
            LastImportStatus = $"Import cancelled: {reason}";
            Plugin.Log.Information($"Gpose Cast: {LastImportStatus}");
        }
        catch (Exception ex)
        {
            LastImportStatus = $"Import failed for {live.DisplayName}: {ex.Message}";
            Plugin.Log.Error(ex, $"Gpose Cast: failed to import {live.DisplayName} to GPose.");
        }
        finally
        {
            FinishImport(cancellationSource);
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
    private async Task<ActorEntry> CreateActorAsync(ActorEntry original, CancellationToken cancellationToken)
    {
        var pending = await framework.RunOnFrameworkThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!clientState.IsGPosing)
                throw new OperationCanceledException("GPose ended before import dispatch.", cancellationToken);

            // Re-resolve immediately before touching native state. The row that the user
            // clicked may have been rebuilt or unloaded between UI draw and dispatch.
            var live = actorScanner.FindAnyCurrent(original.Key, original.Name, original.ObjectKind);
            if (live is null || !CanImport(live))
                throw new InvalidOperationException($"Source actor '{original.DisplayName}' is no longer a loaded world player.");

            if (!TryDispatch(live, out var spawnIndex))
                throw new InvalidOperationException("GPose object table is full. KtisisPyon uses slots 200-238.");

            return new PendingImport(spawnIndex, live.Name, live.ObjectKind);
        });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imported = await framework.RunOnFrameworkThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!clientState.IsGPosing)
                    throw new OperationCanceledException("GPose ended while waiting for import.", cancellationToken);

                return TryGetSpawnedActor(pending);
            });

            if (imported is not null)
                return imported;

            await Task.Delay(25, cancellationToken);
        }
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

    /// <summary>Returns the spawned GPose actor only after the expected slot contains a matching player actor.</summary>
    private ActorEntry? TryGetSpawnedActor(PendingImport pending)
    {
        var spawned = objectTable[(int)pending.ObjectIndex];
        var entry = actorScanner.FromGameObject(spawned);
        if (entry is null || entry.Address == nint.Zero)
            return null;

        if (!entry.IsGposeActor || !entry.IsPlayerCharacter)
            return null;

        if (!NamesMatch(pending.SourceName, entry.Name))
        {
            Plugin.Log.Warning($"Gpose Cast: GPose slot {pending.ObjectIndex} filled by unexpected actor '{entry.DisplayName}', waiting for '{pending.SourceName}'.");
            return null;
        }

        return entry;
    }

    /// <summary>Returns true when the spawned actor still appears to be the requested source actor.</summary>
    private static bool NamesMatch(string expectedName, string liveName)
    {
        return string.IsNullOrWhiteSpace(expectedName)
            || string.IsNullOrWhiteSpace(liveName)
            || string.Equals(expectedName, liveName, StringComparison.Ordinal);
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

    /// <summary>Marks an import as finished and clears the single-import guard.</summary>
    private void FinishImport(CancellationTokenSource cancellationSource)
    {
        lock (importLock)
        {
            if (ReferenceEquals(activeImportCancellationSource, cancellationSource))
            {
                activeImportCancellationSource = null;
                activeImportCancellationReason = null;
                importInProgress = false;
            }
        }

        cancellationSource.Dispose();
    }

    /// <summary>Returns the current cancellation reason, if the active import was cancelled externally.</summary>
    private string? GetImportCancellationReason()
    {
        lock (importLock)
            return activeImportCancellationReason;
    }

    /// <summary>Small identity snapshot for an import request that has been dispatched.</summary>
    private readonly record struct PendingImport(uint ObjectIndex, string SourceName, ObjectKind SourceKind);

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
        CancelPendingImport("plugin unloading.");

        // If the native event may still finalize asynchronously, do not free the copied
        // vtable under it. The allocation is tiny and process-local; avoiding a possible
        // use-after-free during plugin unload is more important than reclaiming it here.
        if (importInProgress)
        {
            Plugin.Log.Warning("Gpose Cast: leaving import vtable allocated because plugin unloaded during an active import.");
            hookVfTable = null;
            return;
        }

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
