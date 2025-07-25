// <auto-generated/>
// ReSharper disable InconsistentNaming
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ImGuiNET;

public unsafe partial class ImGuiPayloadPtr
{
    public ImGuiPayload* Handle { get; private set; }

    public ImGuiPayloadPtr (ImGuiPayload* handle)
    {
        Handle = handle;
    }

    internal static ImGuiPayloadPtr Create(ImGuiPayload* handle)
    {
        return handle == null ? null : new(handle);
    }

    internal static ImGuiPayload* GetHandle(ImGuiPayloadPtr self)
    {
        return self == null ? null : self.Handle;
    }

    /// <summary>
    /// <para>Members</para>
    /// Data (copied and owned by dear imgui)
    /// </summary>
    public IntPtr Data
    {
        get => Handle->Data;
        set => Handle->Data = value;
    }

    /// <summary>
    /// Data size
    /// </summary>
    public ref int DataSize => ref Unsafe.AsRef<int>(&Handle->DataSize);

    /// <summary>
    /// <para>[Internal]</para>
    /// Source item id
    /// </summary>
    public ref uint SourceId => ref Unsafe.AsRef<uint>(&Handle->SourceId);

    /// <summary>
    /// Source parent id (if available)
    /// </summary>
    public ref uint SourceParentId => ref Unsafe.AsRef<uint>(&Handle->SourceParentId);

    /// <summary>
    /// Data timestamp
    /// </summary>
    public ref int DataFrameCount => ref Unsafe.AsRef<int>(&Handle->DataFrameCount);

    /// <summary>
    /// Data type tag (short user-supplied string, 32 characters max)
    /// </summary>
    public Span<sbyte> DataType => Handle->DataType;

    /// <summary>
    /// Set when AcceptDragDropPayload() was called and mouse has been hovering the target item (nb: handle overlapping drag targets)
    /// </summary>
    public bool Preview
    {
        get => Handle->Preview;
        set => Handle->Preview = value;
    }

    /// <summary>
    /// Set when AcceptDragDropPayload() was called and mouse button is released over the target item.
    /// </summary>
    public bool Delivery
    {
        get => Handle->Delivery;
        set => Handle->Delivery = value;
    }

    public void Clear()
    {
        ImGuiNative.ImGuiPayload_Clear(this.Handle);
    }
    public bool IsDataType(string type)
    {
        byte* __bytes_type = stackalloc byte[128];
        using var __utf8z_type = new UTF8ZHelper(__bytes_type, 128, type);
        return ImGuiNative.ImGuiPayload_IsDataType(this.Handle, __utf8z_type.Pointer) != 0;
    }
    public bool IsPreview()
    {
        return ImGuiNative.ImGuiPayload_IsPreview(this.Handle) != 0;
    }
    public bool IsDelivery()
    {
        return ImGuiNative.ImGuiPayload_IsDelivery(this.Handle) != 0;
    }
}
