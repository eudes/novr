using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace NOVR.GameplayPatches;

[ConfigSection(Order = 91)]
public static class TargetCamThrottleConfig
{
    private const string Section = "Performance";

    public static ConfigEntry<bool>? HalfRateEntry;

    public static bool HalfRate => HalfRateEntry?.Value ?? false;

    public static void Bind(ConfigFile config)
    {
        HalfRateEntry = config.Bind(
            Section,
            "Targeting Camera Half Rate",
            false,
            "Render the aircraft's targeting camera (the TV image of a locked target on the cockpit "
            + "display) every other frame. Saves about 0.7 ms of main thread a frame while a target is "
            + "locked, but the TV image then updates at half the frame rate and looks visibly jerky, "
            + "so it is off by default. Takes effect immediately.");
    }
}

/// <summary>
/// Registers every TargetCam that runs its Update, so
/// <see cref="TargetCamThrottle"/> finds them without searching the scene.
/// </summary>
[HarmonyPatch(typeof(TargetCam), "Update")]
internal static class TargetCamThrottleRegistration
{
    [HarmonyPostfix]
    private static void Postfix(TargetCam __instance) => TargetCamThrottle.Register(__instance);
}

/// <summary>
/// Renders the game's targeting camera every other frame. The camera draws
/// into a render texture, which keeps its last image while the camera is
/// skipped, so the cockpit display shows the previous frame's picture.
///
/// <para>A skipped camera is switched off after all game code has run for
/// the frame (late LateUpdate) and back on at the end of the frame, after
/// rendering, so the game never sees it off: its own logic turns the camera
/// on when a target is locked and off when the lock times out. A camera that
/// did not render last frame always renders, so a new lock shows at once
/// rather than a stale picture of the previous target.</para>
///
/// <para>The game's UI camera for that display is paused with it when it is
/// a separate Base camera; as an overlay in the targeting camera's stack it
/// is skipped along with it anyway.</para>
/// </summary>
[DefaultExecutionOrder(10000)]
public class TargetCamThrottle : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<TargetCam, Camera> CamField =
        AccessTools.FieldRefAccess<TargetCam, Camera>("cam");

    private static readonly AccessTools.FieldRef<TargetCam, Camera> UiCamField =
        AccessTools.FieldRefAccess<TargetCam, Camera>("UICam");

    private static readonly HashSet<TargetCam> TargetCams = new();
    private static readonly List<TargetCam> Dead = new();

    private readonly Dictionary<Camera, int> _lastRendered = new();
    private readonly List<Camera> _paused = new();
    private readonly WaitForEndOfFrame _endOfFrame = new();

    internal static void Register(TargetCam targetCam) => TargetCams.Add(targetCam);

    private void OnEnable()
    {
        StartCoroutine(RestoreAtEndOfFrame());
    }

    private void OnDisable()
    {
        Restore();
    }

    private void LateUpdate()
    {
        Restore();
        if (!TargetCamThrottleConfig.HalfRate) return;

        var frame = Time.frameCount;
        foreach (var targetCam in TargetCams)
        {
            if (targetCam == null)
            {
                Dead.Add(targetCam!);
                continue;
            }

            var cam = CamField(targetCam);
            if (cam == null || !cam.enabled) continue;

            if (_lastRendered.TryGetValue(cam, out var last) && frame - last < 2)
            {
                Pause(cam);
                var uiCam = UiCamField(targetCam);
                if (uiCam != null && uiCam.enabled && IsBaseCamera(uiCam)) Pause(uiCam);
            }
            else
            {
                _lastRendered[cam] = frame;
            }
        }

        if (Dead.Count > 0)
        {
            foreach (var dead in Dead) TargetCams.Remove(dead);
            Dead.Clear();
            Prune();
        }
    }

    private void Pause(Camera camera)
    {
        camera.enabled = false;
        _paused.Add(camera);
    }

    private IEnumerator RestoreAtEndOfFrame()
    {
        while (true)
        {
            yield return _endOfFrame;
            Restore();
        }
    }

    private void Restore()
    {
        if (_paused.Count == 0) return;
        foreach (var camera in _paused)
        {
            if (camera != null) camera.enabled = true;
        }

        _paused.Clear();
    }

    private void Prune()
    {
        var gone = new List<Camera>();
        foreach (var camera in _lastRendered.Keys)
        {
            if (camera == null) gone.Add(camera!);
        }

        foreach (var camera in gone) _lastRendered.Remove(camera);
    }

    private static bool IsBaseCamera(Camera camera)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        return data == null || data.renderType == CameraRenderType.Base;
    }
}
