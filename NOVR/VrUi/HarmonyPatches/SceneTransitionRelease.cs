using HarmonyLib;
using NOVR.VrUi.Capture;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Give the game back the HUD subtrees the capture backends borrowed before a
/// scene change starts tearing the mission down.
///
/// <para><b>What broke.</b> <see cref="ViewLayerBackend"/> moves
/// <c>CombatHUD.iconLayer</c> — every unit marker's image — into its own rig,
/// and <see cref="HmdVisorBackend"/> does the same with the helmet display.
/// Those rigs are separate roots in the mission scene, and Unity destroys a
/// scene's roots in no particular order. When a rig went first, the marker
/// images went with it, and <c>CombatHUD.OnDestroy</c> then threw in
/// <c>HUDUnitMarker.RemoveIcon</c> on <c>image.gameObject</c>. That aborted
/// <c>ClearIcons</c> half way, so every marker after it stayed subscribed to
/// the static <c>PlayerSettings.OnApplyOptions</c>, and the next mission's
/// <c>ApplyPrefs</c> threw on those dead markers inside <c>StartHostAsync</c>:
/// <c>RestartMission</c> never finished and the loading screen stayed up. It
/// took a few restarts only because the destroy order changes between
/// unloads.</para>
///
/// <para><b>Why the loading screen.</b> Every caller in the game is a scene
/// change (<c>RestartMission</c>, the network manager's scene loads, the
/// mission editor's play and return), and each shows it before anything
/// unloads, the unload following within the same game time. The release runs
/// inside the prefix rather than on the next Update for exactly that reason. The
/// backends stay down until it is hidden, so they cannot re-borrow from the
/// scene that is on its way out; they rebind on their own afterwards.</para>
/// </summary>
internal static class SceneTransitionRelease
{
    /// <summary>
    /// True from the loading screen going up until it comes down again. The
    /// capture backends read it as "do not borrow anything now".
    /// </summary>
    public static bool InProgress { get; private set; }

    [HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.ShowLoadingScreen))]
    private static class ShowLoadingScreenPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            InProgress = true;
            ViewLayerBackend.ReleaseForSceneChange();
            HmdVisorBackend.ReleaseForSceneChange();
        }
    }

    [HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.HideLoadingScreen))]
    private static class HideLoadingScreenPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            InProgress = false;
        }
    }
}
