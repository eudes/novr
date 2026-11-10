using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Put the tactical map on the VR UI layer when it is maximized, so the VR UI
/// camera draws it.
///
/// <para><b>What broke.</b> A world-space canvas is drawn by the cameras whose
/// culling mask includes its layer, and the map carries nested canvases of its
/// own (<c>MapCanvas</c> with the terrain, <c>iconLayer</c> with the units).
/// <see cref="SpecialBehavior.UIRenderedCanvasBehavior"/> moves a canvas's
/// children onto the VR UI layer once, when it first initializes it, and for
/// <c>MaximizedMapCanvas</c> that happens in <c>OnEnable</c>. The map is not a
/// permanent child of that canvas: <c>DynamicMap.Maximize</c> activates the
/// canvas first and only then reparents the map into it. When the canvas
/// first came up from an in-flight map press (after a restart, where the spawn
/// screen with the map open was skipped), the map arrived just after the
/// layer pass and kept the game's layer: the map's own frame and controls were
/// drawn, the map itself was not. When the canvas was first initialized with
/// the map already inside it, everything worked, which is why it only
/// happened in some flights.</para>
///
/// <para>The postfix does the same pass after the reparent, every time the map
/// is maximized, and only while the canvas really is NOVR's world-space one.
/// The layer is harmless when the map docks back into the helmet display: the
/// capture cameras there draw every layer, and a screen-space overlay canvas
/// ignores layers.</para>
/// </summary>
[HarmonyPatch(typeof(global::DynamicMap), nameof(global::DynamicMap.Maximize))]
internal static class MaximizedMapLayerPatch
{
    private static readonly FieldInfo? MaximizedMapCanvasField =
        AccessTools.Field(typeof(global::DynamicMap), "maximizedMapCanvas");

    [HarmonyPostfix]
    private static void Postfix(global::DynamicMap __instance)
    {
        // Maximize returns early when the map is not allowed to open.
        if (!global::DynamicMap.mapMaximized) return;

        var canvas = MaximizedMapCanvasField?.GetValue(__instance) as Canvas;
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return;

        LayerHelper.SetLayerRecursive(__instance.transform, LayerHelper.GetVrUiLayer());
    }
}
