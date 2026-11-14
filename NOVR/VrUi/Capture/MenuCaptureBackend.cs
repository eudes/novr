using System;
using HarmonyLib;
using Rewired.UI.ControlMapper;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using NOVR.VrCamera;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Alternative backend for the game's own menus: instead of converting their
/// canvases to world space, leave them in ScreenSpaceOverlay, render them into
/// a texture through the engine's overlay path, and show that texture on a
/// panel in front of the player.
///
/// The world-space conversion is what breaks the game's UI in VR — it silently
/// drops three guarantees the UI was authored against (draw order follows the
/// hierarchy, local z is ignored, layers and cameras do not matter), and each
/// lost guarantee has needed its own patch: masked text disappearing behind a
/// stencil pop, list items ordered by z, dropdown popups on the wrong layer.
/// Capturing the overlay pass keeps all three, so those failure modes cannot
/// occur here.
///
/// The flight HUD deliberately stays on the world-space path: it is projected
/// into the cockpit and scaled per element, which a flat panel cannot do.
/// </summary>
public class MenuCaptureBackend : NOVRBehaviour
{
    private const string MenuCanvasName = "MainCanvas";
    // The mission editor's UI is parented under the map scene's MenuCanvas,
    // not a MainCanvas — see FindMenuCanvas.
    private const string EditorCanvasName = "MenuCanvas";
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    // The menu canvas is deactivated for a frame at a time by the behaviour
    // patcher and during scene transitions. Tearing the capture down on the
    // first inactive frame meant rebuilding the target, camera and panel three
    // times in one menu visit, so an absence has to persist to count.
    private const float TeardownGrace = 1.5f;
    // Above the native root and the patched game canvas: when the panel is up
    // it is what the player is looking at.
    private const int GazeAnchorPriority = 3;

    private static MenuCaptureBackend? _instance;

    private Canvas? _menuCanvas;
    private Camera? _captureCamera;
    private RenderTexture? _target;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RawImage? _imguiImage;
    private RectTransform? _panelRect;

    /// <summary>
    /// The part of the screen the panel shows, in screen pixels (origin bottom
    /// left), or null for the whole screen. Set to the F1 settings window when
    /// that window is the only thing being captured, so the panel is the
    /// window rather than a 4K screen with a small window somewhere on it.
    /// </summary>
    private Rect? _view;

    /// <summary>
    /// True while the F1 settings window is the only thing on the panel. The
    /// panel then shows just that window, small, see-through and off to one
    /// side, fixed in the cockpit; the capture camera stays off and the game's
    /// overlay canvases, the flight HUD capture included, carry on as if no
    /// menu were open, so the pilot can fly with the window up.
    /// </summary>
    private bool _imguiOnly;

    /// <summary>
    /// The frame the panel is fixed in while <see cref="_imguiOnly"/>: the
    /// game camera's mount, which is the cockpit eye point in flight, and the
    /// panel's facing relative to it when it was opened.
    /// </summary>
    private Transform? _anchorFrame;
    private Quaternion _anchorLocalRotation = Quaternion.identity;

    /// <summary>
    /// Angle one IMGUI pixel covers on a cropped panel. The settings window
    /// draws its text at about 13 pixels, so this makes it about 0.85 degrees
    /// tall, comfortably readable; at full 4K it was about a third of that.
    /// </summary>
    private const float ImguiDegreesPerPixel = 0.065f;

    /// <summary>Margin kept around the settings window on a cropped panel, in pixels.</summary>
    private const float ImguiViewMargin = 12f;

    /// <summary>
    /// Sorting order of the panel while the F1 settings window is on it. All of
    /// NOVR's world-space UI is drawn by the cockpit HUD camera, where sorting
    /// order decides what is on top regardless of distance: the panel's
    /// default of 0 put the settings window behind the main menu, the native
    /// menu (5000) and the game's own menus. This is above all of them and
    /// below the cursor (short.MaxValue), which has to stay visible on it.
    /// </summary>
    private const int ImguiSortingOrder = 32000;
    private Vector3 _anchorPosition;
    private Quaternion _anchorRotation = Quaternion.identity;
    private bool _anchorInitialized;
    private bool _loggedPlacement;
    private bool _loggedMapper;
    // What is being captured this frame, for the log. "The menu vanished from the
    // headset" and "the menu was never captured" look identical from inside a
    // headset and used to look identical in the log too, because setup said
    // something and teardown said nothing at all.
    private string? _reason;
    private float _nextRebind;
    private float _lastWantedCapture;
    private bool _capturing;
    private bool _loggedPassEnqueued;
    private bool _loggedHookFailure;
    private int _targetWidth;
    private int _targetHeight;

    /// <summary>True while the captured-menu backend owns the overlay UI.</summary>
    public static bool IsActive => _instance != null && _instance._capturing;

    /// <summary>
    /// Whether URP should skip drawing overlay canvases into the eye buffers.
    /// Only while capturing, so a build with the backend off — or a session
    /// that never reaches a menu — renders exactly as before.
    /// </summary>
    public static bool SuppressesScreenOverlayUi => CapturesOverlay;

    /// <summary>
    /// True while the panel is capturing the game's overlay canvases (a menu,
    /// the keybinding screen), as opposed to showing only the F1 settings
    /// window. Only then must the flight HUD capture stand down: both draw
    /// every overlay canvas, and one camera at a time can.
    /// </summary>
    public static bool CapturesOverlay => IsActive && !_instance!._imguiOnly;

    /// <summary>
    /// This backend is how the game's own menus are shown in VR — there is no
    /// second implementation to fall back to, and no switch to turn it off. It
    /// stands down for one reason only: NOVR's native menu UI replaces those
    /// menus outright, so there is nothing left to capture.
    /// </summary>
    public static bool Enabled => !ModConfiguration.Instance.EnableNativeMenuUi.Value;

    public static bool IsCaptureCamera(Camera? camera) =>
        camera != null && _instance != null && ReferenceEquals(camera, _instance._captureCamera);

    protected override void Awake()
    {
        base.Awake();
        _instance = this;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
        TeardownCapture();
    }

    private void Update()
    {
        // The F1 settings window has no other way into the headset: IMGUI is
        // drawn onto this panel whichever menu implementation runs. So with the
        // native menu UI on, the backend still runs, but for that window only;
        // the game's menus are the native UI's to show.
        if (!Enabled && !ImguiCapture.WindowOpen)
        {
            if (_capturing && Time.unscaledTime - _lastWantedCapture > TeardownGrace) TeardownCapture();
            return;
        }

        var wantCapture = false;
        _reason = null;

        if (Enabled)
        {
            if (Time.unscaledTime >= _nextRebind)
            {
                _nextRebind = Time.unscaledTime + RebindInterval;
                _menuCanvas = FindMenuCanvas();
            }

            wantCapture = _menuCanvas != null &&
                          _menuCanvas.isActiveAndEnabled &&
                          _menuCanvas.renderMode == RenderMode.ScreenSpaceOverlay;

            _reason = wantCapture ? "the menu canvas" : null;
            if (ControlMapperUp())
            {
                _reason = wantCapture ? "the menu canvas and the keybinding screen" : "the keybinding screen";
                wantCapture = true;
            }
        }

        var imguiOnly = false;
        if (ImguiCapture.WindowOpen)
        {
            imguiOnly = !wantCapture;
            _reason = wantCapture ? $"{_reason} and the F1 settings window" : "the F1 settings window";
            wantCapture = true;
        }

        _view = imguiOnly ? ImguiView() : null;
        _imguiOnly = imguiOnly;

        if (wantCapture) _lastWantedCapture = Time.unscaledTime;

        if (wantCapture && !_capturing) SetupCapture();
        else if (!wantCapture && _capturing && Time.unscaledTime - _lastWantedCapture > TeardownGrace) TeardownCapture();

        if (_capturing) UpdatePanel();
    }

    /// <summary>
    /// Whether the keybinding screen is up — which is a menu, and is not on any
    /// menu canvas.
    ///
    /// <para><b>Why it needs asking about separately.</b> The bindings UI is
    /// Rewired's <c>ControlMapper</c>, and the game does not build it into a
    /// scene: <c>MainMenu</c> loads a "Rewired" prefab from Resources at startup
    /// and keeps the clone alive for the whole session, so its canvas belongs to
    /// no scene's menu and is switched on and off in place by
    /// <c>ControlMapper.Open</c>. A backend that triggers on a canvas called
    /// <c>MainCanvas</c> therefore covers the bindings screen only by accident —
    /// by the main menu happening to be up behind it — and in flight, where
    /// <c>ControlsMenu.EditBindings</c> disables <c>GameplayUI.menuCanvas</c> on
    /// the way in and there is no <c>MainCanvas</c> at all, by nothing. Reported
    /// from a flight as the keybindings screen being missing in the headset and
    /// drawn on the monitor instead.</para>
    ///
    /// <para>Claiming it here also takes it off the flight HUD backend, which
    /// stands down whenever this one is capturing — so the bindings screen lands
    /// on a panel in front of the player rather than squeezed into the HUD's
    /// footprint on the cockpit.</para>
    /// </summary>
    private bool ControlMapperUp()
    {
        ControlMapper? mapper;
        try
        {
            mapper = GameManager.controlMapper;
            if (mapper == null || !mapper.isOpen) return false;
        }
        catch (Exception)
        {
            // Asked every frame, including before the prefab has finished
            // loading and after the session has started tearing down.
            return false;
        }

        EnsureMapperOverlay(mapper);
        return true;
    }

    /// <summary>
    /// The capture draws the engine's screen-space overlay pass, so a canvas that
    /// is not in that pass is not in the capture — it goes to the game's own
    /// output and nowhere else, which is exactly "missing in VR, fine on the
    /// monitor". Nothing guarantees the Rewired prefab was authored as an overlay,
    /// so it is checked once and said out loud either way.
    /// </summary>
    private void EnsureMapperOverlay(ControlMapper mapper)
    {
        if (_loggedMapper) return;

        var canvas = FindMapperCanvas(mapper);
        if (canvas == null)
        {
            _loggedMapper = true;
            Debug.LogWarning("[NOVR] Captured-menu backend: the keybinding screen is open but has " +
                             "no Canvas this could find. It will be captured only if it draws in " +
                             "the overlay pass.");
            return;
        }

        _loggedMapper = true;
        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
        {
            Debug.Log($"[NOVR] Captured-menu backend: the keybinding screen ('{canvas.name}') is a " +
                      "ScreenSpaceOverlay canvas and comes through the overlay pass as it is.");
            return;
        }

        var was = canvas.renderMode;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        Debug.Log($"[NOVR] Captured-menu backend: the keybinding screen ('{canvas.name}') was " +
                  $"{was}, which the overlay pass does not draw — moved to ScreenSpaceOverlay so " +
                  "it lands on the panel instead of on the monitor.");
    }

    /// <summary>
    /// The GameObject <c>ControlMapper.Open</c> activates, which is the one whose
    /// <c>activeInHierarchy</c> its own <c>isOpen</c> reads. Taken by reflection
    /// because it is private, with a search from the component as the fallback —
    /// a rename there costs a log line, not the feature.
    /// </summary>
    private static Canvas? FindMapperCanvas(ControlMapper mapper)
    {
        try
        {
            var field = AccessTools.Field(typeof(ControlMapper), "canvas");
            if (field?.GetValue(mapper) is GameObject go)
            {
                var own = go.GetComponent<Canvas>();
                if (own != null) return own;
            }
        }
        catch (Exception)
        {
            // Fall through to the search.
        }

        return mapper.GetComponentInChildren<Canvas>(true) ?? mapper.GetComponentInParent<Canvas>();
    }

    /// <summary>
    /// The canvas whose presence means "a menu is up". Normally the menu
    /// scenes' <c>MainCanvas</c>; in the mission editor the game has no
    /// MainCanvas at all, because the editor runs inside the map scene and
    /// <c>GameplayUI.OpenMissionEditor</c> instantiates the whole editor
    /// prefab as a child of <c>GameplayUI.menuCanvas</c> — measured: 964
    /// graphics under a <c>MenuCanvas</c> that no backend was claiming, which
    /// is a ScreenSpaceOverlay canvas nothing draws into the eye buffers.
    ///
    /// Only in the editor. <c>MenuCanvas</c> also carries the in-flight pause
    /// and leaderboard menus, and during flight the captured flight HUD
    /// already owns the overlay pass; taking it there would change what the
    /// pause menu does, which is a separate question from this one.
    /// </summary>
    private static Canvas? FindMenuCanvas()
    {
        var wanted = IsMissionEditorOpen() ? EditorCanvasName : MenuCanvasName;

        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (canvas == null) continue;
            if (canvas.name != wanted) continue;
            if (!canvas.gameObject.scene.IsValid()) continue;
            return canvas;
        }

        return null;
    }

    private static bool IsMissionEditorOpen()
    {
        try
        {
            return GameManager.gameState == GameState.Editor;
        }
        catch
        {
            // Asked every rebind, including while the game state machine is
            // still coming up; "not the editor" is the safe answer.
            return false;
        }
    }

    private void SetupCapture()
    {
        EnsureTarget();
        EnsureCaptureCamera();
        EnsurePanel();
        _anchorInitialized = false;
        RecenterPanel();

        _capturing = true;
        var canvas = _menuCanvas != null ? $"'{_menuCanvas.name}'" : "no menu canvas";
        Debug.Log($"[NOVR] Captured-menu backend active for {_reason ?? "a menu"} ({canvas}): " +
                  $"rendering through the overlay path into a {_targetWidth}x{_targetHeight} texture.");
    }

    private void TeardownCapture()
    {
        if (_capturing)
        {
            Debug.Log("[NOVR] Captured-menu backend standing down: nothing left to capture " +
                      $"(menu canvas {(_menuCanvas == null ? "gone" : _menuCanvas.isActiveAndEnabled ? "up" : "inactive")}, " +
                      "keybinding screen closed). The game's menus go back to the eye buffers.");
        }

        _capturing = false;
        _reason = null;

        if (_panelCanvas != null)
        {
            Destroy(_panelCanvas.gameObject);
            _panelCanvas = null;
            _panelImage = null;
            _imguiImage = null;
            _panelRect = null;
        }

        if (_captureCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_captureCamera);
            Destroy(_captureCamera.gameObject);
            _captureCamera = null;
        }

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
            _target = null;
        }
    }

    private void EnsureTarget()
    {
        var width = Mathf.Max(640, Screen.width);
        var height = Mathf.Max(480, Screen.height);

        if (_target != null && _targetWidth == width && _targetHeight == height) return;

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
        }

        // Matching the game's own resolution keeps the canvas layout identical
        // to the flat one and the text as crisp as the source.
        _target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "NOVR Menu Capture",
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _target.Create();
        _targetWidth = width;
        _targetHeight = height;

        if (_captureCamera != null) _captureCamera.targetTexture = _target;
        if (_panelImage != null) _panelImage.texture = _target;
    }

    private void EnsureCaptureCamera()
    {
        if (_captureCamera != null) return;

        var go = new GameObject("NOVR Menu Capture Camera");
        go.transform.SetParent(transform, false);

        var camera = go.AddComponent<Camera>();
        // Nothing in the scene is drawn: this camera exists purely to give the
        // capture pass a render target and a frame to run in.
        camera.cullingMask = 0;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = _target;
        camera.depth = -100f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        var data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderType = CameraRenderType.Base;
        data.renderPostProcessing = false;
        data.requiresColorOption = CameraOverrideOption.Off;
        data.requiresDepthOption = CameraOverrideOption.Off;

        VrCameraManager.IgnoredCameras.Add(camera);
        _captureCamera = camera;
    }

    private void EnsurePanel()
    {
        if (_panelCanvas != null) return;

        var go = new GameObject("NOVR Menu Panel");
        // The menus live in their own scenes (the mission picker is a scene
        // load away from the main menu), so a plain scene object is destroyed
        // out from under the backend on the way in.
        DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = APIBus.CockpitHudCamera;

        var rect = (RectTransform)canvas.transform;
        var imageGo = new GameObject("Menu Texture");
        imageGo.transform.SetParent(rect, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = _target;
        image.raycastTarget = false;

        var imageRect = (RectTransform)imageGo.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        // IMGUI (the F1 settings window) is drawn after every camera has
        // rendered, into its own texture, so it sits on top of the menu
        // capture as a second layer rather than inside it.
        var imguiGo = new GameObject("IMGUI Texture");
        imguiGo.transform.SetParent(rect, false);
        var imgui = imguiGo.AddComponent<RawImage>();
        imgui.raycastTarget = false;
        imgui.enabled = false;
        var imguiRect = (RectTransform)imguiGo.transform;
        imguiRect.anchorMin = Vector2.zero;
        imguiRect.anchorMax = Vector2.one;
        imguiRect.offsetMin = Vector2.zero;
        imguiRect.offsetMax = Vector2.zero;
        _imguiImage = imgui;

        _panelCanvas = canvas;
        _panelImage = image;
        _panelRect = rect;

        ApplyPanelSize();
    }

    /// <summary>
    /// The settings window's rectangle in screen pixels, with a margin, or
    /// null if it cannot be read.
    /// </summary>
    private static Rect? ImguiView()
    {
        if (!ImguiCapture.TryGetWindowRect(out var window)) return null;

        // IMGUI measures from the top of the screen, the rest of Unity from
        // the bottom.
        var xMin = Mathf.Max(0f, window.xMin - ImguiViewMargin);
        var xMax = Mathf.Min(Screen.width, window.xMax + ImguiViewMargin);
        var yMin = Mathf.Max(0f, Screen.height - window.yMax - ImguiViewMargin);
        var yMax = Mathf.Min(Screen.height, Screen.height - window.yMin + ImguiViewMargin);
        if (xMax - xMin < 32f || yMax - yMin < 32f) return null;

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    /// <summary>The panel's view as a fraction of the screen (0..1, origin bottom left).</summary>
    private Rect NormalizedView()
    {
        if (_view is not { } view || Screen.width <= 0 || Screen.height <= 0) return new Rect(0f, 0f, 1f, 1f);
        return new Rect(view.x / Screen.width, view.y / Screen.height,
            view.width / Screen.width, view.height / Screen.height);
    }

    private void ApplyPanelSize()
    {
        if (_panelRect == null) return;

        if (_view is { } view)
        {
            // Cropped to the settings window: shaped like it, and sized by
            // angle so its text reads at a fixed size whatever the screen.
            _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth * view.height / view.width);
            var distance = Mathf.Clamp(ModConfiguration.Instance.CapturedMenuDistance.Value, 1f, 6f);
            var degreesPerPixel = ImguiDegreesPerPixel * ImguiCaptureConfig.Size;
            var viewWidthMeters = 2f * distance * Mathf.Tan(view.width * degreesPerPixel * 0.5f * Mathf.Deg2Rad);
            var viewScale = viewWidthMeters / PanelCanvasReferenceWidth;
            _panelRect.localScale = new Vector3(viewScale, viewScale, viewScale);
            return;
        }

        var aspect = _targetHeight > 0 ? (float)_targetWidth / _targetHeight : 16f / 9f;
        _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);

        var widthMeters = Mathf.Clamp(ModConfiguration.Instance.CapturedMenuWidth.Value, 0.5f, 8f);
        var scale = widthMeters / PanelCanvasReferenceWidth;
        _panelRect.localScale = new Vector3(scale, scale, scale);
    }

    private void UpdatePanel()
    {
        // Idempotent rebuilds: a scene load can still take the rig with it, and
        // the capture is only torn down after a grace period, so recovery
        // cannot wait for the next Setup.
        EnsureTarget();
        EnsureCaptureCamera();
        EnsurePanel();

        if (_panelCanvas != null)
        {
            var sortingOrder = ImguiCapture.WindowOpen ? ImguiSortingOrder : 0;
            if (_panelCanvas.sortingOrder != sortingOrder) _panelCanvas.sortingOrder = sortingOrder;
        }

        // With only the settings window up there is nothing to capture: the
        // camera would otherwise take every overlay canvas, the HUD included,
        // off the eye buffers and onto this panel.
        if (_captureCamera != null && _captureCamera.enabled == _imguiOnly) _captureCamera.enabled = !_imguiOnly;

        var uv = NormalizedView();
        if (_panelImage != null)
        {
            if (_panelImage.texture != _target) _panelImage.texture = _target;
            _panelImage.uvRect = uv;
            if (_panelImage.enabled == _imguiOnly) _panelImage.enabled = !_imguiOnly;
        }

        if (_imguiImage != null)
        {
            var imguiTexture = ImguiCapture.Texture;
            _imguiImage.texture = imguiTexture;
            _imguiImage.enabled = imguiTexture != null;
            _imguiImage.color = new Color(1f, 1f, 1f, _imguiOnly ? ImguiCaptureConfig.Opacity : 1f);
            _imguiImage.uvRect = ImguiCapture.FlipVertical ? new Rect(uv.x, 1f - uv.y, uv.width, -uv.height) : uv;
        }
        ApplyPanelSize();
        ApplyAnchor();
    }

    /// <summary>
    /// Park the panel in front of the headset, upright and facing the player.
    ///
    /// Placement is anchored rather than continuous — a panel that chases the
    /// head cannot be pointed at — but the anchor is re-applied every frame,
    /// because the reference pose is still settling when a menu first appears
    /// and the world origin shifts underneath long sessions. Placing once and
    /// walking away leaves the panel stranded outside the view.
    /// </summary>
    public void RecenterPanel()
    {
        CaptureAnchor();
        ApplyAnchor();
    }

    private void CaptureAnchor()
    {
        var reference = APIBus.CockpitHudReference;
        if (reference == null) return;

        var forward = Vector3.ProjectOnPlane(reference.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        _anchorPosition = reference.transform.position;
        _anchorRotation = Quaternion.LookRotation(forward, Vector3.up);
        _anchorInitialized = true;

        _anchorFrame = APIBus.MainCamera != null ? APIBus.MainCamera.transform.parent : null;
        _anchorLocalRotation = _anchorFrame != null
            ? Quaternion.Inverse(_anchorFrame.rotation) * _anchorRotation
            : _anchorRotation;
    }

    private void ApplyAnchor()
    {
        if (_panelRect == null) return;
        if (!_anchorInitialized) CaptureAnchor();
        if (!_anchorInitialized) return;

        var distance = Mathf.Clamp(ModConfiguration.Instance.CapturedMenuDistance.Value, 1f, 6f);

        // Only the facing is anchored. The origin has to be read live: the
        // reference pose is at the world origin for the first seconds of a
        // session and only then moves to wherever the menu camera actually is,
        // so a position captured once leaves the panel stranded in empty space
        // — which reads as "the backend renders nothing".
        var reference = APIBus.CockpitHudReference;
        var origin = reference != null ? reference.transform.position : _anchorPosition;

        var rotation = _anchorRotation;
        if (_imguiOnly)
        {
            // Fixed in the cockpit rather than in the world, so it turns with
            // the aircraft, and offset to one side so the view ahead is clear.
            var frame = _anchorFrame != null ? _anchorFrame.rotation * _anchorLocalRotation : _anchorRotation;
            rotation = frame * Quaternion.Euler(-ImguiCaptureConfig.PitchDegrees, ImguiCaptureConfig.YawDegrees, 0f);
        }

        var position = origin + rotation * Vector3.forward * distance;

        _panelRect.position = position;
        _panelRect.rotation = rotation;

        // The panel is the surface the cursor is driven against, so head-gaze
        // amplification is measured from its centre.
        VrUiCursor.I?.SetGazeAnchorCenter(position, GazeAnchorPriority);

        if (_loggedPlacement || origin == Vector3.zero) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] Captured menu panel placed at {position} facing {_anchorRotation.eulerAngles} " +
                  $"(reference at {origin}).");
    }

    public static void Recenter() => _instance?.RecenterPanel();

    /// <summary>
    /// Map a world point on (or near) the panel back to the screen coordinate
    /// the overlay canvases were rendered at, which is what the virtual mouse
    /// and every GraphicRaycaster in the game expect.
    /// </summary>
    public static bool TryGetScreenPoint(Vector3 worldPoint, out Vector2 screenPoint)
    {
        screenPoint = Vector2.zero;
        if (!IsActive || _instance?._panelRect == null) return false;

        var rect = _instance._panelRect;
        var local = rect.InverseTransformPoint(worldPoint);
        var size = rect.rect.size;
        if (size.x <= 0f || size.y <= 0f) return false;

        // Rect pivot is centred, so shift into 0..1 before scaling to pixels.
        // The panel may show only part of the screen (the F1 window); the
        // point is mapped into that part.
        var normalized = new Vector2(local.x / size.x + 0.5f, local.y / size.y + 0.5f);
        var view = _instance.NormalizedView();
        screenPoint = new Vector2(
            Mathf.Clamp((view.x + normalized.x * view.width) * Screen.width, 0f, Screen.width),
            Mathf.Clamp((view.y + normalized.y * view.height) * Screen.height, 0f, Screen.height));
        return true;
    }

    /// <summary>The inverse of <see cref="TryGetScreenPoint"/>: the panel surface
    /// point showing a given screen pixel.</summary>
    public static bool TryGetWorldPoint(Vector2 screenPoint, out Vector3 worldPoint)
    {
        worldPoint = Vector3.zero;
        if (!IsActive || _instance?._panelRect == null) return false;

        var rect = _instance._panelRect;
        var size = rect.rect.size;
        if (size.x <= 0f || size.y <= 0f) return false;

        var view = _instance.NormalizedView();
        var u = (Mathf.Clamp01(screenPoint.x / Screen.width) - view.x) / view.width;
        var v = (Mathf.Clamp01(screenPoint.y / Screen.height) - view.y) / view.height;
        worldPoint = rect.TransformPoint(new Vector3(
            (Mathf.Clamp01(u) - 0.5f) * size.x,
            (Mathf.Clamp01(v) - 0.5f) * size.y,
            0f));
        return true;
    }

    /// <summary>Distance from a ray origin to the panel plane, so the cursor
    /// can sit on the panel rather than at its default projection distance.</summary>
    public static bool TryGetPanelDistance(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0f;
        if (!IsActive || _instance?._panelRect == null) return false;

        var rect = _instance._panelRect;
        var plane = new Plane(-rect.forward, rect.position);
        if (!plane.Raycast(new Ray(origin, direction), out var hit) || hit <= 0f) return false;

        distance = hit;
        return true;
    }

    internal static void NotifyCapturePassEnqueued()
    {
        if (_instance == null || _instance._loggedPassEnqueued) return;
        _instance._loggedPassEnqueued = true;
        Debug.Log("[NOVR] Overlay UI capture pass enqueued on the capture camera.");
    }

    internal static void ReportRenderHookFailure(string reason)
    {
        if (_instance == null || _instance._loggedHookFailure) return;
        _instance._loggedHookFailure = true;
        // Silence here would look like "the menu is just black", which is the
        // one failure this backend must never present without a reason.
        Debug.LogWarning($"[NOVR] Captured-menu backend cannot inject its render pass: {reason}. " +
                         "The game's menus will not be drawn. Set Enable Native Menu UI to true " +
                         "to use NOVR's own VR menus instead, which do not need this hook.");
    }
}
