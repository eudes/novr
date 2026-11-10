using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Draws each waypoint's fly-through ring (ObjectiveOverlay.sizeIndicator) as
/// a ring in the VR overlay room, along the waypoint's true direction, instead
/// of on the flat head-locked virtual screen.
///
/// <para><b>Why.</b> On the virtual screen the ring was a circle drawn on a flat
/// panel spanning the visor field of view, and however it was sized or
/// stretched it read in the headset as a hoop that tilted and turned whenever
/// the head moved. A ring placed in the room along the waypoint's direction
/// has nothing to correct: it is ordinary geometry seen from the eye, so its
/// shape depends only on where the waypoint is, never on where the head
/// points.</para>
///
/// <para><b>Where.</b> The room is the cockpit mount's frame: the head-locked
/// panels are posed in it from the main camera's local pose, which is the head
/// relative to that mount. The waypoint's direction from the head is taken
/// into the mount's frame, and the ring is placed along it at the panels'
/// distance, a little nearer than the virtual screen.</para>
///
/// <para><b>Orientation.</b> The ring is a hoop fixed in the world: upright,
/// facing the horizontal direction the waypoint was approached from when its
/// ring first appeared, and re-taken when the overlay moves on to another
/// waypoint. An earlier version turned it to face the head every frame, and as
/// the aircraft closed on a waypoint above, below or beside it the bearing
/// swung faster and faster and the ring visibly tilted round to follow.</para>
///
/// <para><b>Size.</b> The game scales the ring so its rect spans
/// 2 · range / distance in tangent units: range is the radius at the rect's
/// edge. At render distance D that is 2 · D · range / distance metres, which
/// is what the ring's rect is scaled to. A copy of the real hoop scaled about
/// the eye by D / distance, with the same orientation, looks exactly like the
/// real one, which is why drawing it at D loses nothing.</para>
///
/// <para><b>Sea level.</b> The ring is drawn over the world, so nothing in the
/// scene can hide it. The one occluder it does honour is the sea plane
/// (<c>Datum.LocalSeaY</c>): the hoop is upright, so the plane cuts it along a
/// horizontal line in its own plane, and the image is filled from the top down
/// to that line. Terrain is deliberately not considered.</para>
///
/// <para><b>Look.</b> Sprite, image type and colour (the game's fade lives in
/// its alpha) are copied from the game's ring every update, and the game's ring
/// is switched off for the frame. It is drawn with the additive material
/// NOVR's HUD panels use, which is how it reached the eye on the virtual screen
/// too: the sprite has an opaque black surround, which on Unity's default UI
/// material came out as a black disc covering the HUD, and which adding light
/// turns into nothing. Additive drawing also makes the order against the HUD
/// panels irrelevant: neither can cover the other. It is shown whenever the
/// waypoint is within 90° of the view direction — the game's own front test —
/// rather than only while its centre is on the virtual screen, which is only
/// about 43° tall.
/// While the virtual screen is not active none of this runs and the game's
/// ring is drawn as the flat game draws it.</para>
/// </summary>
internal sealed class WaypointRings : MonoBehaviour
{
    // Just in front of the head-locked panels, which sit at the same distance.
    private const float DistanceFraction = 0.98f;

    private static readonly FieldInfo? SizeIndicatorField =
        AccessTools.Field(typeof(ObjectiveOverlay), "sizeIndicator");

    private static WaypointRings? _instance;
    private static Material? _additive;

    private sealed class Ring
    {
        public Canvas Canvas = null!;
        public Image Image = null!;
        public int LastFrame;
        public bool Oriented;
        public Vector3 OrientedFor;
        public Quaternion WorldRotation;
    }

    private readonly Dictionary<ObjectiveOverlay, Ring> _rings = new();

    [HarmonyPatch(typeof(ObjectiveOverlay), nameof(ObjectiveOverlay.UpdateOverlay))]
    private static class UpdateOverlayPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ObjectiveOverlay __instance, MissionPosition.PositionResult result)
        {
            if (!ViewLayerBackend.IsActive) return;

            var indicator = SizeIndicatorField?.GetValue(__instance) as Image;
            if (indicator == null) return;

            var self = EnsureInstance();
            if (self == null) return;

            self.UpdateRing(__instance, indicator, result);
            indicator.enabled = false;
        }
    }

    private static WaypointRings? EnsureInstance()
    {
        if (_instance != null) return _instance;

        var hudCamera = APIBus.CockpitHudCamera;
        var room = hudCamera != null ? hudCamera.transform.parent : null;
        if (room == null) return null;

        var go = new GameObject("NOVR Waypoint Rings");
        go.transform.SetParent(room, false);
        _instance = go.AddComponent<WaypointRings>();
        return _instance;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    private void UpdateRing(ObjectiveOverlay overlay, Image indicator, MissionPosition.PositionResult result)
    {
        var mainCamera = APIBus.MainCamera;
        var mount = mainCamera != null ? mainCamera.transform.parent : null;
        var hudCamera = APIBus.CockpitHudCamera;
        if (mainCamera == null || mount == null || hudCamera == null) return;

        if (!_rings.TryGetValue(overlay, out var ring) || ring.Canvas == null)
        {
            ring = CreateRing(hudCamera);
            _rings[overlay] = ring;
        }

        ring.LastFrame = Time.frameCount;

        var head = mainCamera.transform;
        var waypoint = result.Position.ToLocalPosition();
        var delta = waypoint - head.position;
        var distance = result.Distance > 0f ? result.Distance : delta.magnitude;
        var range = result.Range ?? 0f;
        var rectHeight = indicator.rectTransform.rect.height;

        // The part of the hoop above the sea plane, as a fraction of its rect
        // from the top: the rect spans range either side of the centre.
        var aboveSea = range > 0f
            ? Mathf.Clamp01((range + waypoint.y - Datum.LocalSeaY) / (2f * range))
            : 0f;

        var visible = range > 0f && distance > 0f && rectHeight > 0f &&
                      aboveSea > 0f &&
                      indicator.color.a > 0.001f &&
                      Vector3.Angle(head.forward, delta) < 90f;
        ring.Canvas.enabled = visible;
        if (!visible) return;

        // Absolute (floating-origin-proof) position, so a reused overlay is
        // recognised by the waypoint it now points at.
        var target = result.Position.AsVector3();
        if (!ring.Oriented || (ring.OrientedFor - target).sqrMagnitude > 1f)
        {
            var approach = Vector3.ProjectOnPlane(delta, Vector3.up);
            if (approach.sqrMagnitude < 0.0001f) approach = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (approach.sqrMagnitude < 0.0001f) approach = Vector3.forward;

            ring.WorldRotation = Quaternion.LookRotation(approach.normalized, Vector3.up);
            ring.OrientedFor = target;
            ring.Oriented = true;
        }

        var direction = mount.InverseTransformDirection(delta).normalized;
        var renderDistance = CapturedFlightHud.DistanceMeters * DistanceFraction;

        var canvasTransform = ring.Canvas.transform;
        canvasTransform.localPosition = head.localPosition + direction * renderDistance;
        canvasTransform.localRotation = Quaternion.Inverse(mount.rotation) * ring.WorldRotation;
        canvasTransform.localScale = Vector3.one * (2f * renderDistance * range / distance / rectHeight);

        ring.Image.sprite = indicator.sprite;
        ring.Image.type = Image.Type.Filled;
        ring.Image.fillMethod = Image.FillMethod.Vertical;
        ring.Image.fillOrigin = (int)Image.OriginVertical.Top;
        ring.Image.fillAmount = aboveSea;
        ring.Image.preserveAspect = indicator.preserveAspect;
        ring.Image.color = indicator.color;
        ring.Image.rectTransform.sizeDelta = indicator.rectTransform.rect.size;
    }

    private Ring CreateRing(Camera hudCamera)
    {
        var go = new GameObject("NOVR Waypoint Ring");
        go.transform.SetParent(transform, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = hudCamera;

        var imageGo = new GameObject("Ring");
        imageGo.transform.SetParent(go.transform, false);
        var image = imageGo.AddComponent<Image>();
        image.raycastTarget = false;
        // Shared by every ring, and kept for the session: one material, created
        // once, so nothing to destroy per ring.
        image.material = _additive ??= FlightHudCaptureBackend.CreatePanelMaterial();

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());
        return new Ring { Canvas = canvas, Image = image };
    }

    // Rings whose overlay was not updated this frame are hidden (the overlay is
    // hidden, or the virtual screen went down), and rings whose overlay is gone
    // are destroyed with it. Either order against UpdateOverlay works: an
    // update later in the same frame turns its ring back on before rendering.
    private void LateUpdate()
    {
        var active = ViewLayerBackend.IsActive;
        List<ObjectiveOverlay>? gone = null;

        foreach (var pair in _rings)
        {
            var ring = pair.Value;
            if (pair.Key == null || ring.Canvas == null)
            {
                if (ring.Canvas != null) Destroy(ring.Canvas.gameObject);
                (gone ??= new List<ObjectiveOverlay>()).Add(pair.Key!);
                continue;
            }

            if (!active || ring.LastFrame != Time.frameCount) ring.Canvas.enabled = false;
        }

        if (gone == null) return;
        foreach (var key in gone) _rings.Remove(key);
    }
}
