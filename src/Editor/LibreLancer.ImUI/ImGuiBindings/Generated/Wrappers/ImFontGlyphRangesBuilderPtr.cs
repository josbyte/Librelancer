// <auto-generated/>
// ReSharper disable InconsistentNaming
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ImGuiNET;

public unsafe partial class ImFontGlyphRangesBuilderPtr
{
    public ImFontGlyphRangesBuilder* Handle { get; private set; }

    public ImFontGlyphRangesBuilderPtr (ImFontGlyphRangesBuilder* handle)
    {
        Handle = handle;
    }

    internal static ImFontGlyphRangesBuilderPtr Create(ImFontGlyphRangesBuilder* handle)
    {
        return handle == null ? null : new(handle);
    }

    internal static ImFontGlyphRangesBuilder* GetHandle(ImFontGlyphRangesBuilderPtr self)
    {
        return self == null ? null : self.Handle;
    }

    /// <summary>
    /// Store 1-bit per Unicode code point (0=unused, 1=used)
    /// </summary>
    public ref ImVector<uint> UsedChars => ref Unsafe.AsRef<ImVector<uint>>(&Handle->UsedChars);

    public void Clear()
    {
        ImGuiNative.ImFontGlyphRangesBuilder_Clear(this.Handle);
    }
    /// <summary>
    /// Get bit n in the array
    /// </summary>
    public bool GetBit(nint n)
    {
        return ImGuiNative.ImFontGlyphRangesBuilder_GetBit(this.Handle, n) != 0;
    }
    /// <summary>
    /// Set bit n in the array
    /// </summary>
    public void SetBit(nint n)
    {
        ImGuiNative.ImFontGlyphRangesBuilder_SetBit(this.Handle, n);
    }
    /// <summary>
    /// Add character
    /// </summary>
    public void AddChar(ushort c)
    {
        ImGuiNative.ImFontGlyphRangesBuilder_AddChar(this.Handle, c);
    }
    /// <summary>
    /// Add string (each character of the UTF-8 string are added)
    /// </summary>
    public void AddText(string text, int? text_end = null)
    {
        byte* __bytes_text = stackalloc byte[128];
        using var __utf8z_text = new UTF8ZHelper(__bytes_text, 128, text);
        ImGuiNative.ImFontGlyphRangesBuilder_AddText(this.Handle, __utf8z_text.Pointer, __utf8z_text.GetTextEnd(text_end));
    }
    /// <summary>
    /// Add ranges, e.g. builder.AddRanges(ImFontAtlas::GetGlyphRangesDefault()) to force add all of ASCII/Latin+Ext
    /// </summary>
    public void AddRanges(ref ushort ranges)
    {
        fixed(ushort* __ranges_p = &ranges)
        {
            ImGuiNative.ImFontGlyphRangesBuilder_AddRanges(this.Handle, __ranges_p);
        }
    }
    /// <summary>
    /// Output new ranges (ImVector_Construct()/ImVector_Destruct() can be used to safely construct out_ranges)
    /// </summary>
    public void BuildRanges(ref ImVector<ushort> out_ranges)
    {
        fixed(ImVector<ushort>* __out_ranges_p = &out_ranges)
        {
            ImGuiNative.ImFontGlyphRangesBuilder_BuildRanges(this.Handle, __out_ranges_p);
        }
    }
}
