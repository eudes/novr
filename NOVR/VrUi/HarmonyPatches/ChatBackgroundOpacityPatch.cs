using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Scales the chat box's background by <see cref="ChatPanelConfig.BackgroundOpacity"/>.
/// The game writes the alpha absolutely in <c>ApplyBackgroundAlpha</c>, as
/// <c>Lerp(passiveAlpha, activeAlpha, backgroundBlend)</c>, and never reads it
/// back, so scaling it after each write cannot compound.
/// </summary>
[HarmonyPatch(typeof(MessageUI), "ApplyBackgroundAlpha")]
internal static class ChatBackgroundOpacityPatch
{
    private static readonly FieldInfo? BackgroundImageField =
        AccessTools.Field(typeof(MessageUI), "backgroundImage");

    [HarmonyPostfix]
    private static void Postfix(MessageUI __instance)
    {
        var opacity = ChatPanelConfig.BackgroundOpacity;
        if (opacity >= 1f) return;

        if (BackgroundImageField?.GetValue(__instance) is not Image image || image == null) return;

        var colour = image.color;
        colour.a *= opacity;
        image.color = colour;
    }
}
