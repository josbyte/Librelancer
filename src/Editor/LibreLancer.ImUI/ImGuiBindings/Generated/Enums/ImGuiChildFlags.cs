// <auto-generated/>
// ReSharper disable InconsistentNaming
using System;

namespace ImGuiNET;

/// <summary>
/// <para>Flags for ImGui::BeginChild()</para>
/// <para>(Legacy: bit 0 must always correspond to ImGuiChildFlags_Borders to be backward compatible with old API using 'bool border = false'.</para>
/// <para>About using AutoResizeX/AutoResizeY flags:</para>
/// <para>- May be combined with SetNextWindowSizeConstraints() to set a min/max size for each axis (see "Demo-&gt;Child-&gt;Auto-resize with Constraints").</para>
/// <para>- Size measurement for a given axis is only performed when the child window is within visible boundaries, or is just appearing.</para>
/// <para>- This allows BeginChild() to return false when not within boundaries (e.g. when scrolling), which is more optimal. BUT it won't update its auto-size while clipped.</para>
/// <para>While not perfect, it is a better default behavior as the always-on performance gain is more valuable than the occasional "resizing after becoming visible again" glitch.</para>
/// <para>- You may also use ImGuiChildFlags_AlwaysAutoResize to force an update even when child window is not in view.</para>
/// HOWEVER PLEASE UNDERSTAND THAT DOING SO WILL PREVENT BeginChild() FROM EVER RETURNING FALSE, disabling benefits of coarse clipping.
/// </summary>
[Flags]
public enum ImGuiChildFlags
{
    None = 0,
    /// <summary>
    /// Show an outer border and enable WindowPadding. (IMPORTANT: this is always == 1 == true for legacy reason)
    /// </summary>
    Borders = 1<<0,
    /// <summary>
    /// Pad with style.WindowPadding even if no border are drawn (no padding by default for non-bordered child windows because it makes more sense)
    /// </summary>
    AlwaysUseWindowPadding = 1<<1,
    /// <summary>
    /// Allow resize from right border (layout direction). Enable .ini saving (unless ImGuiWindowFlags_NoSavedSettings passed to window flags)
    /// </summary>
    ResizeX = 1<<2,
    /// <summary>
    /// Allow resize from bottom border (layout direction). "
    /// </summary>
    ResizeY = 1<<3,
    /// <summary>
    /// Enable auto-resizing width. Read "IMPORTANT: Size measurement" details above.
    /// </summary>
    AutoResizeX = 1<<4,
    /// <summary>
    /// Enable auto-resizing height. Read "IMPORTANT: Size measurement" details above.
    /// </summary>
    AutoResizeY = 1<<5,
    /// <summary>
    /// Combined with AutoResizeX/AutoResizeY. Always measure size even when child is hidden, always return true, always disable clipping optimization! NOT RECOMMENDED.
    /// </summary>
    AlwaysAutoResize = 1<<6,
    /// <summary>
    /// Style the child window like a framed item: use FrameBg, FrameRounding, FrameBorderSize, FramePadding instead of ChildBg, ChildRounding, ChildBorderSize, WindowPadding.
    /// </summary>
    FrameStyle = 1<<7,
    /// <summary>
    /// [BETA] Share focus scope, allow keyboard/gamepad navigation to cross over parent border to this child or between sibling child windows.
    /// </summary>
    NavFlattened = 1<<8
}
