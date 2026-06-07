using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace GposeCast.Structs;

/// <summary>
/// Native GPose actor-spawn event layout used by the import service.
/// </summary>
/// <remarks>
/// The offsets were derived from KtisisPyon 0.4.0.3 and should be treated as
/// version-sensitive native interop. Keep this struct small, explicit, and isolated.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 304)]
public unsafe struct GPoseActorEvent
{
    /// <summary>Event vtable pointer. Gpose Cast swaps this for a copied vtable.</summary>
    [FieldOffset(0)] public nint* VTable;

    /// <summary>Entity id assigned by the event. The finalize hook marks it local.</summary>
    [FieldOffset(32)] public ulong EntityId;

    /// <summary>Original character pointer used as the spawn source.</summary>
    [FieldOffset(208)] public Character* Character;

    /// <summary>Object id field used by the native event.</summary>
    [FieldOffset(224)] public uint ObjectId;

    /// <summary>Native event parameter copied from KtisisPyon's constructor call.</summary>
    [FieldOffset(264)] public uint Param4;

    /// <summary>Native event parameter copied from KtisisPyon's constructor call.</summary>
    [FieldOffset(268)] public uint Param5;

    /// <summary>Native event parameter copied from KtisisPyon's constructor call.</summary>
    [FieldOffset(272)] public uint Param6;

    /// <summary>Source object-table index used by the native copy logic.</summary>
    [FieldOffset(276)] public uint CopyObjectIndex;
}
