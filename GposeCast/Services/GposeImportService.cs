using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using CharacterCopyFlags = FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags;
using ClientObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager;
using StructsObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
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
/// GPoseActorEvent, replace the finalize vtable slot to mark the import as a
/// temporary local GPose actor, dispatch the event, then wait for the expected object
/// table slot to become valid.
/// </remarks>
public sealed class GposeImportService : IDisposable
{
    private const ushort SpawnStart = 200;
    private const ushort SpawnEnd = 238;
    private const int VfSize = 9;
    private const int ImportTimeoutMilliseconds = 10000;
    private const int MaxLinkedFashionAccessoryImports = 3;
    private const float LinkedFashionAccessorySearchRadius = 12.0f;

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly ActorScannerService actorScanner;
    private readonly Configuration configuration;
    private readonly object importLock = new();

    private CancellationTokenSource? activeImportCancellationSource;
    private string? activeImportCancellationReason;
    private volatile bool importInProgress;
    private readonly Dictionary<ActorKey, ImportedCloneRecord> importedCloneRecords = new();
    private readonly HashSet<nint> destroyedImportedCloneAddresses = new();

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

    /// <summary>Number of temporary GPose actors created by the linked-child import path.</summary>
    public int ImportedCloneCount => importedCloneRecords.Count;

    /// <summary>Raised when an import completes and the spawned GPose actor is found.</summary>
    public event Action<ActorEntry, ActorEntry, bool, IReadOnlyList<ActorEntry>, bool>? ImportCompleted;

    /// <summary>Creates and initializes the import service.</summary>
    public GposeImportService(IClientState clientState, IObjectTable objectTable, IFramework framework, ActorScannerService actorScanner, Configuration configuration)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.actorScanner = actorScanner;
        this.configuration = configuration;

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

        LastImportStatus = $"Requested GPose import for {live.DisplayName}...";
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
            var linkedAccessories = await FindLinkedCompanionActorsBeforeImportAsync(live, cancellationSource.Token);
            var useLinkedChildImport = ShouldUseLinkedChildImport(live, linkedAccessories);

            var importedActor = useLinkedChildImport
                ? await CreateLinkedChildImportAsync(live, cancellationSource.Token)
                : await CreateActorAsync(live, cancellationSource.Token);

            var accessoryResult = BuildLinkedAccessoryResult(linkedAccessories, useLinkedChildImport);
            var accessoriesToProtect = configuration.HandleLinkedFashionAccessories
                ? linkedAccessories
                    .GroupBy(accessory => accessory.Key)
                    .Select(group => group.First())
                    .ToList()
                : new List<ActorEntry>();

            await framework.RunOnFrameworkThread(() =>
            {
                cancellationSource.Token.ThrowIfCancellationRequested();
                RefreshKtisisActorList();

                var finalActor = actorScanner.FindByAddress(importedActor.Address) ?? importedActor;
                ImportCompleted?.Invoke(finalActor, live, addToPickedAfterImport, accessoriesToProtect, useLinkedChildImport);

                LastImportStatus = BuildImportStatus(finalActor, accessoryResult);
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
            LastImportStatus = "GPose import spawner initialized.";
            Plugin.Log.Information("Gpose Cast: GPose import spawner initialized.");
        }
        catch (Exception ex)
        {
            initialized = false;
            LastImportStatus = $"Import unavailable: {ex.Message}";
            Plugin.Log.Error(ex, "Gpose Cast: failed to initialize GPose import spawner.");
        }
    }

    /// <summary>Finds linked native child actors before the player clone is spawned.</summary>
    private async Task<IReadOnlyList<ActorEntry>> FindLinkedCompanionActorsBeforeImportAsync(ActorEntry original, CancellationToken cancellationToken)
    {
        return await framework.RunOnFrameworkThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!clientState.IsGPosing)
                throw new OperationCanceledException("GPose ended before linked companion scan.", cancellationToken);

            var live = actorScanner.FindAnyCurrent(original.Key, original.Name, original.ObjectKind) ?? original;
            if (configuration.EnableAccessoryDiagnostics)
                actorScanner.DumpNearbyActors(live, LinkedFashionAccessorySearchRadius, "before remote player import");

            var linkedAccessories = actorScanner
                .FindLinkedOwnedCompanions(live, LinkedFashionAccessorySearchRadius, allowProximityFallback: false)
                .Where(CanImportLinkedCompanionActor)
                .Take(MaxLinkedFashionAccessoryImports)
                .ToList();

            if (linkedAccessories.Count == 0)
            {
                Plugin.Log.Debug($"Gpose Cast: no confirmed linked companion/mount/accessory found for {live.DisplayName} before import.");
            }
            else
            {
                Plugin.Log.Information($"Gpose Cast: found {linkedAccessories.Count} linked companion/mount/accessory actor(s) for {live.DisplayName} before import.");
                if (configuration.EnableAccessoryDiagnostics)
                {
                    foreach (var accessory in linkedAccessories)
                        Plugin.Log.Information($"Gpose Cast: linked companion/mount/accessory candidate: {FormatAccessoryDebug(accessory)}.");
                }
            }

            return (IReadOnlyList<ActorEntry>)linkedAccessories;
        });
    }

    /// <summary>Builds the linked accessory result for the selected import path.</summary>
    private LinkedAccessoryImportResult BuildLinkedAccessoryResult(IReadOnlyList<ActorEntry> accessories, bool linkedChildImportUsed)
    {
        if (!configuration.HandleLinkedFashionAccessories)
        {
            if (accessories.Count > 0)
                Plugin.Log.Information($"Gpose Cast: linked companion/mount/accessory preservation is shelved; importing base actor only and leaving {accessories.Count} linked child actor(s) to the game/client.");

            return new LinkedAccessoryImportResult(0, 0, accessories.Count, LinkedChildImportUsed: false, HandlingEnabled: false);
        }

        if (linkedChildImportUsed)
        {
            Plugin.Log.Information($"Gpose Cast: imported actor with linked-child import path; source had {accessories.Count} linked companion/mount/accessory actor(s). No separate child import is needed.");
            return new LinkedAccessoryImportResult(0, 0, accessories.Count, LinkedChildImportUsed: true, HandlingEnabled: true);
        }

        if (accessories.Count > 0)
            Plugin.Log.Information($"Gpose Cast: linked companion/mount/accessory preservation is shelved; importing base actor only and leaving {accessories.Count} linked child actor(s) to the game/client.");

        return new LinkedAccessoryImportResult(0, 0, accessories.Count, LinkedChildImportUsed: false, HandlingEnabled: false);
    }

    /// <summary>Formats the source actor used for a GPose spawn event.</summary>
    private static string FormatSpawnSourceDebug(ActorEntry source)
    {
        var parent = string.IsNullOrWhiteSpace(source.ParentName)
            ? "<none>"
            : source.ParentName;
        var parentIndex = source.ParentKey is { } parentKey
            ? parentKey.ObjectIndex.ToString()
            : "<none>";

        return $"{source.DisplayName} [Kind={source.ObjectKind}, SubKind=0x{source.SubKind:X2}, Index={source.Key.ObjectIndex}, GameObjectId=0x{source.Key.GameObjectId:X}, EntityId=0x{source.Key.EntityId:X8}, Parent=\"{parent}\", ParentIndex={parentIndex}, Address=0x{source.Address:X}]";
    }

    /// <summary>Formats an ornament/fashion accessory for import diagnostics.</summary>
    private static string FormatAccessoryDebug(ActorEntry accessory)
    {
        var parent = string.IsNullOrWhiteSpace(accessory.ParentName)
            ? "<none>"
            : accessory.ParentName;
        var parentIndex = accessory.ParentKey is { } parentKey
            ? parentKey.ObjectIndex.ToString()
            : "<none>";

        return $"{accessory.DisplayName} [Kind={accessory.ObjectKind}, SubKind=0x{accessory.SubKind:X2}, Index={accessory.Key.ObjectIndex}, GameObjectId=0x{accessory.Key.GameObjectId:X}, EntityId=0x{accessory.Key.EntityId:X8}, Parent=\"{parent}\", ParentIndex={parentIndex}]";
    }

    /// <summary>Builds a compact import status for the UI/log.</summary>
    private static string BuildImportStatus(ActorEntry finalActor, LinkedAccessoryImportResult accessoryResult)
    {
        var baseStatus = $"Imported {finalActor.DisplayName} into GPose at index {finalActor.Key.ObjectIndex}.";
        if (accessoryResult.Total == 0)
            return baseStatus;

        var accessoryLabel = accessoryResult.Total == 1 ? "linked fashion accessory" : "linked fashion accessories";
        if (!accessoryResult.HandlingEnabled)
            return $"{baseStatus} Linked {accessoryLabel} are temporarily unsupported.";

        return $"{baseStatus} Preserved {accessoryResult.Total} linked {accessoryLabel}.";
    }

    /// <summary>Returns true when the linked-child import path should be used for this import.</summary>
    private bool ShouldUseLinkedChildImport(ActorEntry source, IReadOnlyList<ActorEntry> linkedAccessories)
    {
        // Public 0.9.0.3 intentionally shelves the linked-child import path.
        // The native code remains in place for future investigation, but normal
        // imports use the legacy GPose event path because it preserves external
        // PlayerSync/Lightless/Penumbra state more reliably.
        return false;
    }

    /// <summary>Checks whether the source character currently owns an ornament, mount, or companion through the linked-child slot.</summary>
    private unsafe static bool HasNativeSpawnedCompanion(ActorEntry source)
    {
        if (source.Address == nint.Zero)
            return false;

        var native = (Character*)source.Address;
        return native != null
            && native->CompanionObject != null
            && (native->OrnamentData.OrnamentObject != null
                || native->Mount.MountObject != null
                || native->CompanionData.CompanionObject != null);
    }

    /// <summary>Creates a GPose actor with a linked-child slot, then copies the source with Companion/Ornament/Mount flags.</summary>
    private async Task<ActorEntry> CreateLinkedChildImportAsync(ActorEntry original, CancellationToken cancellationToken)
    {
        var pending = await framework.RunOnFrameworkThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!clientState.IsGPosing)
                throw new OperationCanceledException("GPose ended before linked-child import dispatch.", cancellationToken);

            var live = actorScanner.FindAnyCurrent(original.Key, original.Name, original.ObjectKind);
            if (live is null || !CanImport(live))
                throw new InvalidOperationException($"Source actor '{original.DisplayName}' is no longer a loaded supported player actor.");

            if (TryGetExistingImportedClone(live, out var existing))
            {
                Plugin.Log.Information($"Gpose Cast: reusing existing linked-child import for {live.DisplayName}: {FormatSpawnSourceDebug(existing)}.");
                return new PendingImport(existing.Key.ObjectIndex, existing.Address, live.Name, live.DisplayName, live.ObjectKind, RequireNameMatch: false, RequireDrawObject: true, SourceKey: live.Key);
            }

            return CreateLinkedChildImportOnFrameworkThread(live);
        });

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imported = await framework.RunOnFrameworkThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!clientState.IsGPosing)
                    throw new OperationCanceledException("GPose ended while waiting for linked-child import.", cancellationToken);

                return TryGetSpawnedActor(pending);
            });

            if (imported is not null)
                return imported.WithDisplayAlias(pending.DisplayAlias);

            await Task.Delay(25, cancellationToken);
        }
    }

    /// <summary>Runs the actual linked-child import on the framework thread.</summary>
    private unsafe PendingImport CreateLinkedChildImportOnFrameworkThread(ActorEntry source)
    {
        var sourceNative = (Character*)source.Address;
        if (sourceNative == null || !sourceNative->GameObject.IsCharacter())
            throw new InvalidOperationException($"Source actor '{source.DisplayName}' ({source.Key.ObjectIndex}) is invalid.");

        var objectManager = ClientObjectManager.Instance();
        if (objectManager == null)
            throw new InvalidOperationException("ClientObjectManager is null.");

        // CreateBattleCharacter(param: 1) reserves the client slot needed for linked children.
        var idCheck = objectManager->CreateBattleCharacter(param: 1);
        if (idCheck == 0xffffffff)
            throw new InvalidOperationException("CreateBattleCharacter returned an invalid object id.");

        var createdIndex = (ushort)idCheck;
        var newObject = objectManager->GetObjectByIndex(createdIndex);
        if (newObject == null)
            throw new InvalidOperationException($"Created GPose object {createdIndex} could not be resolved.");

        var targetNative = (Character*)newObject;

        // Give the temporary object a name before registering it with GPose, then restore the source name after CopyFromCharacter.
        SetNativeGameObjectName((nint)newObject, $"GposeCast {createdIndex}");

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            throw new InvalidOperationException("EventFramework is null.");

        // Set the source name again after CopyFromCharacter because the copy can leave temporary actors with a blank or partial native name.
        SetNativeGameObjectName((nint)newObject, source.DisplayName);
        eventFramework->EventSceneModule.EventGPoseController.AddCharacterToGPose(targetNative);
        CreateObjectReferenceForSideEffects((nint)newObject);

        var weaponVisibility = CaptureWeaponVisibility(sourceNative);

        var copyFlags = CharacterCopyFlags.Position
            | CharacterCopyFlags.WeaponHiding
            | CharacterCopyFlags.Companion
            | CharacterCopyFlags.Ornament
            | CharacterCopyFlags.Mount;

        targetNative->CharacterSetup.CopyFromCharacter(sourceNative, copyFlags);
        RestoreWeaponVisibility(targetNative, weaponVisibility);
        targetNative->CharacterSetup.CopyFromCharacter(targetNative, CharacterCopyFlags.None);
        RestoreWeaponVisibility(targetNative, weaponVisibility);
        SetNativeGameObjectName((nint)newObject, source.DisplayName);
        CreateObjectReferenceForSideEffects((nint)newObject);
        TryEnableDrawIfReady((nint)newObject);
        TryEnableCompanionDrawIfReady((nint)newObject);
        QueueDrawWhenReady((nint)newObject, $"linked-child owner {source.DisplayName}");
        QueueCompanionDrawWhenReady((nint)newObject, $"linked-child companion for {source.DisplayName}");

        var position = sourceNative->GameObject.Position;
        var rotation = sourceNative->GameObject.Rotation;
        targetNative->GameObject.DefaultPosition = position;
        targetNative->GameObject.Position = position;
        targetNative->GameObject.DefaultRotation = rotation;
        targetNative->GameObject.Rotation = rotation;

        var createdAddress = (nint)newObject;
        destroyedImportedCloneAddresses.Remove(createdAddress);
        var companionAddress = GetCompanionAddress(createdAddress);
        if (companionAddress != nint.Zero)
            destroyedImportedCloneAddresses.Remove(companionAddress);

        importedCloneRecords[source.Key] = new ImportedCloneRecord(source.Key, createdAddress, source.DisplayName);

        Plugin.Log.Information($"Gpose Cast: created linked-child import. Source={FormatSpawnSourceDebug(source)}. ObjectIndex={createdIndex}; CreatedAddress=0x{createdAddress:X}. WeaponVisibility={weaponVisibility}. Waiting for GPose object-table entry with draw object. Method=CreateBattleCharacter(param:1)+AddCharacterToGPose+CopyFromCharacter(Companion|Ornament|Mount|Position|WeaponHiding).");
        return new PendingImport(createdIndex, createdAddress, source.Name, source.DisplayName, source.ObjectKind, RequireNameMatch: false, RequireDrawObject: true, SourceKey: source.Key);
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
                throw new InvalidOperationException($"Source actor '{original.DisplayName}' is no longer a loaded supported player actor.");

            if (!TryDispatch(live, out var spawnIndex))
                throw new InvalidOperationException("GPose object table is full. the GPose import range uses slots 200-238.");

            return new PendingImport(spawnIndex, nint.Zero, live.Name, live.DisplayName, live.ObjectKind, RequireNameMatch: true, RequireDrawObject: false, SourceKey: live.Key);
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

        Plugin.Log.Information($"Gpose Cast: dispatching GPose Player spawn, expecting slot {index}. Source={FormatSpawnSourceDebug(original)}. Method=GPoseActorEvent.");
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

        // Parameters mirror the KtisisPyon path that successfully produced GPose actors.
        gPoseActorEventCtor(task, player, &player->GameObject.Position, 64U, 30, 0, 4294934523U, true);
        task->VTable = hookVfTable;

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            throw new InvalidOperationException("EventFramework is null.");

        // KtisisPyon dispatches through EventFramework + 432 + 152.
        var handler = (nint)eventFramework + 432 + 152;
        dispatchEvent(handler, task);
    }

    /// <summary>Returns the spawned GPose actor only after the expected slot/address contains a matching player actor.</summary>
    private ActorEntry? TryGetSpawnedActor(PendingImport pending)
    {
        ActorEntry? entry = null;

        if (pending.Address != nint.Zero)
            entry = actorScanner.FindByAddress(pending.Address);

        if (entry is null)
        {
            var spawned = objectTable[(int)pending.ObjectIndex];
            entry = actorScanner.FromGameObject(spawned);
        }

        if (entry is null || entry.Address == nint.Zero)
            return null;

        if (!entry.IsGposeActor || !entry.IsPlayerCharacter)
            return null;

        if (pending.RequireNameMatch && !NamesMatch(pending.SourceName, entry.Name))
        {
            Plugin.Log.Warning($"Gpose Cast: GPose slot {pending.ObjectIndex} filled by unexpected actor '{entry.DisplayName}', waiting for '{pending.SourceName}'.");
            return null;
        }

        if (pending.RequireDrawObject)
        {
            TryEnableDrawIfReady(entry.Address);
            TryEnableCompanionDrawIfReady(entry.Address);

            if (!HasNativeDrawObject(entry))
                return null;
        }

        return entry.WithDisplayAlias(pending.DisplayAlias);
    }

    /// <summary>Reuses an existing linked-child import for a source instead of creating duplicate 202/204 pairs.</summary>
    private bool TryGetExistingImportedClone(ActorEntry source, out ActorEntry existing)
    {
        existing = null!;
        if (!importedCloneRecords.TryGetValue(source.Key, out var record))
            return false;

        if (destroyedImportedCloneAddresses.Contains(record.Address))
        {
            importedCloneRecords.Remove(source.Key);
            return false;
        }

        var live = actorScanner.FindByAddress(record.Address);
        if (live is null || !live.IsGposeActor || !live.IsPlayerCharacter)
        {
            importedCloneRecords.Remove(source.Key);
            return false;
        }

        existing = live.WithDisplayAlias(record.DisplayAlias);
        return true;
    }

    /// <summary>Destroys every linked-child import created by Gpose Cast in this GPose session.</summary>
    public int DestroyAllImportedClones(string reason)
    {
        if (importedCloneRecords.Count == 0)
            return 0;

        var records = importedCloneRecords.Values.ToList();
        var destroyed = 0;

        foreach (var record in records)
            destroyed += DestroyImportedCloneRecord(record, reason);

        importedCloneRecords.Clear();

        if (destroyed > 0)
            Plugin.Log.Information($"Gpose Cast: destroyed {destroyed} temporary imported object(s) ({reason}).");

        return destroyed;
    }

    /// <summary>Destroys one linked-child owner and its generated companion/accessory child.</summary>
    private int DestroyImportedCloneRecord(ImportedCloneRecord record, string reason)
    {
        var destroyed = 0;
        var ownerAddress = record.Address;
        var companionAddress = GetCompanionAddress(ownerAddress);

        // Delete the generated child first. Deleting the owner first can leave the
        // companion/ornament object hanging around until the next object sweep.
        if (companionAddress != nint.Zero && companionAddress != ownerAddress)
        {
            if (TryDeleteNativeObject(companionAddress, $"{record.DisplayAlias} companion", reason))
                destroyed++;
        }

        if (TryDeleteNativeObject(ownerAddress, record.DisplayAlias, reason))
            destroyed++;

        return destroyed;
    }

    /// <summary>Deletes one native object by resolving its live object-table index.</summary>
    private unsafe bool TryDeleteNativeObject(nint address, string label, string reason)
    {
        if (address == nint.Zero)
            return false;

        destroyedImportedCloneAddresses.Add(address);

        try
        {
            var objectManager = ClientObjectManager.Instance();
            if (objectManager == null)
                return false;

            var native = (StructsObject*)address;
            if (native == null)
                return false;

            var index = objectManager->GetIndexByObject(native);
            if (index == 0xFFFFFFFF)
            {
                Plugin.Log.Debug($"Gpose Cast: temporary imported object {SafeDisplayName(label)} was already gone during cleanup ({reason}). Address=0x{address:X}.");
                return false;
            }

            objectManager->DeleteObjectByIndex((ushort)index, 0);
            Plugin.Log.Information($"Gpose Cast: destroyed temporary imported object {SafeDisplayName(label)} at slot {index} ({reason}).");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Gpose Cast: failed to destroy temporary imported object {SafeDisplayName(label)} ({reason}). Address=0x{address:X}.");
            return false;
        }
    }

    /// <summary>Captures the source character's current weapon visibility so imports preserve hidden/shown state instead of forcing weapons visible.</summary>
    private unsafe static WeaponVisibilitySnapshot CaptureWeaponVisibility(Character* character)
    {
        if (character == null)
            return default;

        var hasMain = TryReadWeaponHidden(character, DrawDataContainer.WeaponSlot.MainHand, out var mainHidden);
        var hasOff = TryReadWeaponHidden(character, DrawDataContainer.WeaponSlot.OffHand, out var offHidden);
        var hasProp = TryReadWeaponHidden(character, (DrawDataContainer.WeaponSlot)2, out var propHidden);

        return new WeaponVisibilitySnapshot(
            hasMain, mainHidden,
            hasOff, offHidden,
            hasProp, propHidden,
            character->WeaponFlags);
    }

    /// <summary>Restores weapon hidden/shown state captured before CopyFromCharacter.</summary>
    private unsafe static void RestoreWeaponVisibility(Character* character, WeaponVisibilitySnapshot snapshot)
    {
        if (character == null)
            return;

        character->WeaponFlags = snapshot.WeaponFlags;

        if (snapshot.HasMainHand)
            TryWriteWeaponHidden(character, DrawDataContainer.WeaponSlot.MainHand, snapshot.MainHandHidden);
        if (snapshot.HasOffHand)
            TryWriteWeaponHidden(character, DrawDataContainer.WeaponSlot.OffHand, snapshot.OffHandHidden);
        if (snapshot.HasProp)
            TryWriteWeaponHidden(character, (DrawDataContainer.WeaponSlot)2, snapshot.PropHidden);
    }

    /// <summary>Reads one weapon draw-object hidden flag without taking a dependency on external posing helpers.</summary>
    private unsafe static bool TryReadWeaponHidden(Character* character, DrawDataContainer.WeaponSlot slot, out bool isHidden)
    {
        isHidden = false;
        if (character == null)
            return false;

        try
        {
            var drawData = &character->DrawData;
            fixed (DrawObjectData* drawObjectData = &drawData->Weapon(slot))
            {
                if (drawObjectData == null)
                    return false;

                isHidden = drawObjectData->IsHidden;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes one weapon draw-object hidden flag, preserving whether the source had that weapon slot shown or hidden.</summary>
    private unsafe static bool TryWriteWeaponHidden(Character* character, DrawDataContainer.WeaponSlot slot, bool isHidden)
    {
        if (character == null)
            return false;

        try
        {
            var drawData = &character->DrawData;
            fixed (DrawObjectData* drawObjectData = &drawData->Weapon(slot))
            {
                if (drawObjectData == null)
                    return false;

                drawObjectData->IsHidden = isHidden;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Best-effort check that a GPose clone has a draw object, so hiding the world actor will not make the shot vanish.</summary>
    private unsafe static bool HasNativeDrawObject(ActorEntry entry)
    {
        if (entry.Address == nint.Zero)
            return false;

        try
        {
            return *(nint*)((byte*)entry.Address + 0x100) != nint.Zero;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Best-effort mirror of Brio's DrawWhenReady: when the native object reports ready, enable drawing.</summary>
    private unsafe static bool TryEnableDrawIfReady(nint address)
    {
        if (address == nint.Zero)
            return false;

        try
        {
            var native = (StructsObject*)address;
            if (native == null)
                return false;

            if (!native->IsReadyToDraw())
                return false;

            native->EnableDraw();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Best-effort draw wake-up for the companion created by a linked-child import.</summary>
    private unsafe void TryEnableCompanionDrawIfReady(nint ownerAddress)
    {
        if (ownerAddress == nint.Zero)
            return;

        try
        {
            var owner = (Character*)ownerAddress;
            if (owner == null || owner->CompanionObject == null)
                return;

            var companionAddress = (nint)owner->CompanionObject;
            CreateObjectReferenceForSideEffects(companionAddress);
            TryEnableDrawIfReady(companionAddress);
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Gpose Cast: companion EnableDraw wake-up was unavailable or failed.");
        }
    }

    /// <summary>Fire-and-forget DrawWhenReady for objects that become ready a few framework ticks after spawning.</summary>
    private void QueueDrawWhenReady(nint objectAddress, string label)
    {
        if (objectAddress == nint.Zero)
            return;

        _ = DrawWhenReadyAsync(
            () => objectAddress,
            label,
            requireDrawObjectAfterEnable: false);
    }

    /// <summary>Fire-and-forget DrawWhenReady for a companion object that can appear after CopyFromCharacter.</summary>
    private void QueueCompanionDrawWhenReady(nint ownerAddress, string label)
    {
        if (ownerAddress == nint.Zero)
            return;

        _ = DrawWhenReadyAsync(
            () => GetCompanionAddress(ownerAddress),
            label,
            requireDrawObjectAfterEnable: false);
    }

    /// <summary>Polls on the framework thread until a native GameObject reports ready, then enables drawing.</summary>
    private async Task DrawWhenReadyAsync(Func<nint> getAddress, string label, bool requireDrawObjectAfterEnable)
    {
        // Wait a couple ticks before checking readiness. Without
        // that delay, companion-slot children can be observed before their draw state wakes up.
        await Task.Delay(50).ConfigureAwait(false);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                var enabled = await framework.RunOnFrameworkThread(() =>
                {
                    if (!clientState.IsGPosing)
                        return DrawWakeResult.Cancelled;

                    var address = getAddress();
                    if (address == nint.Zero)
                        return DrawWakeResult.Waiting;

                    if (destroyedImportedCloneAddresses.Contains(address))
                        return DrawWakeResult.Cancelled;

                    CreateObjectReferenceForSideEffects(address);
                    if (!TryEnableDrawIfReady(address))
                        return DrawWakeResult.Waiting;

                    var drawObject = TryReadDrawObject(address);
                    if (requireDrawObjectAfterEnable && drawObject == nint.Zero)
                        return DrawWakeResult.Waiting;

                    Plugin.Log.Debug($"Gpose Cast: DrawWhenReady enabled {label}. Address=0x{address:X}; DrawObject=0x{drawObject:X}; Attempt={attempt + 1}.");
                    return DrawWakeResult.Enabled;
                }).ConfigureAwait(false);

                if (enabled == DrawWakeResult.Enabled || enabled == DrawWakeResult.Cancelled)
                    return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Verbose(ex, $"Gpose Cast: DrawWhenReady poll failed for {label}.");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Plugin.Log.Debug($"Gpose Cast: DrawWhenReady timed out for {label}.");
    }

    /// <summary>Reads the current native companion pointer from a GPose owner.</summary>
    private unsafe static nint GetCompanionAddress(nint ownerAddress)
    {
        if (ownerAddress == nint.Zero)
            return nint.Zero;

        try
        {
            var owner = (Character*)ownerAddress;
            if (owner == null || owner->CompanionObject == null)
                return nint.Zero;

            return (nint)owner->CompanionObject;
        }
        catch (Exception)
        {
            return nint.Zero;
        }
    }

    /// <summary>Reads GameObject.DrawObject through the stable raw offset used elsewhere in diagnostics.</summary>
    private unsafe static nint TryReadDrawObject(nint objectAddress)
    {
        if (objectAddress == nint.Zero)
            return nint.Zero;

        try
        {
            return *(nint*)((byte*)objectAddress + 0x100);
        }
        catch (Exception)
        {
            return nint.Zero;
        }
    }

    /// <summary>Writes a short UTF-8 native object name into the GameObject name buffer, mirroring Brio's empty actor setup.</summary>
    private unsafe static void SetNativeGameObjectName(nint objectAddress, string name)
    {
        if (objectAddress == nint.Zero || string.IsNullOrWhiteSpace(name) || name == "<unnamed>")
            return;

        var bytes = Encoding.UTF8.GetBytes(name);
        var destination = (byte*)objectAddress + 0x30;
        var max = Math.Min(bytes.Length, 63);
        for (var i = 0; i < max; i++)
            destination[i] = bytes[i];
        destination[max] = 0;
    }

    /// <summary>Creates a Dalamud object wrapper if the current object-table implementation exposes that helper.</summary>
    private void CreateObjectReferenceForSideEffects(nint address)
    {
        if (address == nint.Zero)
            return;

        try
        {
            var method = objectTable.GetType().GetMethod("CreateObjectReference", new[] { typeof(nint) });
            method?.Invoke(objectTable, new object[] { address });
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Gpose Cast: CreateObjectReference side-effect call was unavailable or failed.");
        }
    }

    /// <summary>Returns true when a linked native child is safe enough to hide/protect with its owner import.</summary>
    private bool CanImportLinkedCompanionActor(ActorEntry actor)
    {
        return clientState.IsGPosing
            && initialized
            && actor.IsOverworldActor
            && (actor.IsFashionAccessory || actor.IsCompanionLike)
            && actor.IsICharacter
            && actor.CanNativeAlphaHide
            && actor.Address != nint.Zero;
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

    /// <summary>Formats an optional label without leaking blank accessory names into logs.</summary>
    private static string SafeDisplayName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name;
    }

    /// <summary>Result state for background native draw wake-up polling.</summary>
    private enum DrawWakeResult
    {
        Waiting,
        Enabled,
        Cancelled,
    }

    /// <summary>Small identity snapshot for an import request that has been dispatched.</summary>
    private readonly record struct PendingImport(uint ObjectIndex, nint Address, string SourceName, string DisplayAlias, ObjectKind SourceObjectKind, bool RequireNameMatch, bool RequireDrawObject, ActorKey SourceKey);

    /// <summary>Snapshot of weapon visibility copied from the source before import setup mutates the target.</summary>
    private readonly record struct WeaponVisibilitySnapshot(
        bool HasMainHand,
        bool MainHandHidden,
        bool HasOffHand,
        bool OffHandHidden,
        bool HasProp,
        bool PropHidden,
        byte WeaponFlags)
    {
        public override string ToString()
            => $"Main={(HasMainHand ? (MainHandHidden ? "hidden" : "shown") : "n/a")}, Off={(HasOffHand ? (OffHandHidden ? "hidden" : "shown") : "n/a")}, Prop={(HasProp ? (PropHidden ? "hidden" : "shown") : "n/a")}, Flags=0x{WeaponFlags:X2}";
    }

    /// <summary>Session-local record for a linked-child import, used to prevent duplicate imports when a retry is pressed.</summary>
    private readonly record struct ImportedCloneRecord(ActorKey SourceKey, nint Address, string DisplayAlias);

    /// <summary>Result summary for linked fashion accessory imports.</summary>
    private readonly record struct LinkedAccessoryImportResult(int Imported, int Failed, int Total, bool LinkedChildImportUsed, bool HandlingEnabled);

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
        DestroyAllImportedClones("plugin unloading");

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
