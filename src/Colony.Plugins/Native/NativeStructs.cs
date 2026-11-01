using System.Runtime.InteropServices;

namespace Colony.Plugins.Native;

// The C ABI's data structures (spec section 8), mirrored field for field by the generated colony.h (see
// AbiDefinition.Structs); the header asserts every size, so a layout drift fails the C compile. Natural C
// alignment on 64-bit targets: a pointer after an int32 is padded to 8 in both languages.
#pragma warning disable CA1051, CA1815 // Plain interop data, compared by nobody.

/// <summary><c>colony_random</c>: a <see cref="Numerics.RandomSource"/> (World seed, stream key).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ColonyRandom
{
    public ulong Seed;
    public ulong Key;
}

/// <summary><c>colony_type_info</c>: one Component, Resource or Input Command of a schema.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyTypeInfo
{
    public byte* Name;
    public int Id;
    public int Size;
    public int Alignment;
    public uint Flags;
    public ulong LayoutHash;
}

/// <summary><c>colony_system_info</c>: where the scheduler placed one System.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonySystemInfo
{
    public byte* Name;
    public byte* Phase;
    public int Id;
    public int Order;
    public int Every;
    public int Offset;
}

/// <summary><c>colony_plugin_info</c>: one Plugin of a schema, in load order.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyPluginInfo
{
    public byte* Id;
    public byte* Version;
    public int Deterministic;
    public uint AbiVersion;
}

/// <summary><c>colony_column</c>: one read-only Component column of a chunk.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyColumn
{
    public int Component;
    public int Stride;
    public void* Data;
}

/// <summary><c>colony_chunk</c>: a read-only chunk view, valid for the callback it is passed to.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyChunk
{
    public int Count;
    public int Index;
    public ulong* Entities;
    public int ColumnCount;
    public int Reserved;
    public ColonyColumn* Columns;
}

/// <summary><c>colony_context</c>: what a Native System sees while it runs (the C form of <see cref="SystemContext"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ColonyContext
{
    public long Tick;
    public int SystemId;
    public int JobIndex;
    public int JobCount;
    public int Reserved;
    public ColonyRandom Random;
    public nint Commands;
    public nint World;
    public nint System;
}

/// <summary><c>colony_input</c>: one Input Command of this Tick's canonical list.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyInput
{
    public uint Source;
    public uint Sequence;
    public int Type;
    public int Size;
    public void* Payload;
    public ColonyRandom Random;
}

/// <summary><c>colony_definition_field</c>: one field of a Definition type registered by layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyDefinitionField
{
    public byte* Name;
    public int Kind;
    public int Offset;
}

/// <summary><c>colony_system_desc</c>: a Native System with its access declared in data (spec section 8).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonySystemDesc
{
    public byte* Name;
    public byte* Phase;
    public int* Reads;
    public int ReadsCount;
    public int* With;
    public int WithCount;
    public int* Without;
    public int WithoutCount;
    public int* ReadsResources;
    public int ReadsResourcesCount;
    public int* Emits;
    public int EmitsCount;
    public byte** After;
    public int AfterCount;
    public byte** Before;
    public int BeforeCount;
    public int Every;
    public int Offset;
    public delegate* unmanaged<void*, ColonyContext*, int> Begin;
    public delegate* unmanaged<void*, ColonyContext*, ColonyChunk*, int> ForEachChunk;
    public delegate* unmanaged<void*, ColonyContext*, int> End;
    public void* UserData;
}

/// <summary><c>colony_plugin_desc</c>: what a Native Plugin library returns from <c>colony_plugin_get</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColonyPluginDesc
{
    public uint AbiVersion;
    public int Deterministic;
    public byte* Id;
    public byte* Version;
    public byte** Dependencies;
    public int DependencyCount;
    public int Reserved;
    public delegate* unmanaged<void*, nint, int> Register;
    public void* UserData;
}

#pragma warning restore CA1051, CA1815
