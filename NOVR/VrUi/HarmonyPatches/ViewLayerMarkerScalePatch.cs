using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Applies <see cref="CapturedHmd.ViewIconsMarkerScale"/> to the game's unit
/// markers while they sit on the View Icons screen.
///
/// <para>The game sizes a marker in exactly two places, and each writes the
/// whole <c>localScale</c> from scratch on every call:
/// <c>HUDUnitMarker.UpdateMaximized</c> (the full icon at its distance scale,
/// or the fixed-size minimised sprite: 6 for a hostile, 3 otherwise) and
/// <c>HUDUnitMarker.SelectMarker</c> (20 for the selected target's box). A
/// postfix that multiplies what was just written is therefore idempotent: it
/// never compounds, and a changed setting takes over at the next call.</para>
///
/// <para>Only while <see cref="ViewLayerBackend"/> is active: elsewhere the
/// markers are on a surface whose size already matches the flat game.</para>
/// </summary>
internal static class ViewLayerMarkerScalePatch
{
    private static readonly AccessTools.FieldRef<HUDUnitMarker, Transform> TransformRef =
        AccessTools.FieldRefAccess<HUDUnitMarker, Transform>("_transform");

    private static void Apply(HUDUnitMarker marker)
    {
        if (!ViewLayerBackend.IsActive) return;

        var factor = CapturedHmd.ViewIconsMarkerScaleFactor;
        if (Mathf.Approximately(factor, 1f)) return;

        var transform = TransformRef(marker);
        if (transform == null) return;

        transform.localScale *= factor;
    }

    [HarmonyPatch(typeof(HUDUnitMarker), "UpdateMaximized")]
    private static class UpdateMaximizedPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDUnitMarker __instance) => Apply(__instance);
    }

    [HarmonyPatch(typeof(HUDUnitMarker), nameof(HUDUnitMarker.SelectMarker))]
    private static class SelectMarkerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HUDUnitMarker __instance) => Apply(__instance);
    }
}
