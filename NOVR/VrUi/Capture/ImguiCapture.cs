using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Brings IMGUI windows — above all the BepInEx Configuration Manager (F1) —
/// into the headset.
///
/// <para><b>Why they are missing.</b> IMGUI (<c>OnGUI</c>) draws straight to the
/// game window after every camera has rendered. None of the mod's capture
/// paths see it: they capture canvases, and IMGUI has none. So F1 opens the
/// settings window on the monitor and nowhere else.</para>
///
/// <para><b>How it is captured.</b> Unity calls <c>OnGUI</c> per behaviour in
/// <see cref="GUI.depth"/> order — highest depth first for Repaint, lowest
/// first for input events. <see cref="ImguiCaptureBegin"/> sits at the top
/// depth and, on Repaint, points <see cref="RenderTexture.active"/> at a
/// screen-sized texture and clears it; <see cref="ImguiCaptureEnd"/> sits at
/// the bottom depth and restores the previous target. Everything that draws
/// in between, the settings window included, lands in the texture, which the
/// captured-menu panel shows over the menu capture. That panel already maps
/// the VR cursor back to screen pixels, so the window is where the cursor
/// thinks it is.</para>
///
/// <para><b>How it is operated.</b> IMGUI reads the OS pointer, not the mod's
/// virtual mouse. <see cref="ImguiCaptureEnd"/> runs first for input events and
/// rewrites <see cref="Event.mousePosition"/> to the VR cursor, so hover,
/// clicks, drags and the scroll wheel all happen where the VR cursor is. A VR
/// click (controller trigger, gaze key) produces no OS click at all, so while
/// the window is open it is turned into one with <c>SendInput</c>.</para>
/// </summary>
internal static class ImguiCapture
{
    private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";

    private static RenderTexture? _texture;
    private static RenderTexture? _previousActive;
    private static bool _redirected;
    private static bool _windowOpen;
    private static float _nextLookup;
    private static object? _configManager;
    private static PropertyInfo? _displayingWindow;
    private static PropertyInfo? _settingWindowRect;
    private static FieldInfo? _keybindField;
    private static FieldInfo? _overrideHotkeyField;
    private static bool _displayingAtLastLateTick;
    private static bool _loggedMissing;
    private static bool _loggedActive;

    /// <summary>The texture the IMGUI layer is drawn into, while it is.</summary>
    public static RenderTexture? Texture => _windowOpen ? _texture : null;

    /// <summary>True while an IMGUI window that belongs in the headset is open.</summary>
    public static bool WindowOpen => _windowOpen;

    public static bool FlipVertical => ImguiCaptureConfig.FlipVertical;

    internal static void Tick()
    {
        var open = ImguiCaptureConfig.Enabled && ConfigManagerOpen();
        if (open != _windowOpen)
        {
            _windowOpen = open;
            Debug.Log(open
                ? "[NOVR] IMGUI capture: the Configuration Manager window is open, drawing it onto the menu panel."
                : "[NOVR] IMGUI capture: the Configuration Manager window closed.");
        }

        if (!_windowOpen) return;

        var width = Mathf.Max(640, Screen.width);
        var height = Mathf.Max(480, Screen.height);
        if (_texture != null && _texture.width == width && _texture.height == height) return;

        if (_texture != null)
        {
            _texture.Release();
            UnityEngine.Object.Destroy(_texture);
        }

        _texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "NOVR IMGUI Capture",
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _texture.Create();
    }

    private static bool ConfigManagerOpen()
    {
        if (_configManager == null || _displayingWindow == null)
        {
            if (Time.unscaledTime < _nextLookup) return false;
            _nextLookup = Time.unscaledTime + 2f;

            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(ConfigurationManagerGuid, out var info) || info.Instance == null)
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        Debug.Log("[NOVR] IMGUI capture: BepInEx Configuration Manager not loaded, nothing to bring into the headset.");
                    }
                    return false;
                }

                _configManager = info.Instance;
                var type = _configManager.GetType();
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _displayingWindow = type.GetProperty("DisplayingWindow", any);
                _settingWindowRect = type.GetProperty("SettingWindowRect", any);
                _keybindField = type.GetField("_keybind", any);
                _overrideHotkeyField = type.GetField("OverrideHotkey", any);
                PatchWindowLayout(type);
                if (_displayingWindow == null)
                {
                    Debug.LogWarning("[NOVR] IMGUI capture: Configuration Manager has no DisplayingWindow property; cannot tell when F1 is open.");
                    _configManager = null;
                    _nextLookup = float.MaxValue;
                    return false;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NOVR] IMGUI capture: looking up the Configuration Manager failed: {e.Message}");
                return false;
            }
        }

        try
        {
            return _displayingWindow.GetValue(_configManager, null) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The settings window's rectangle in IMGUI coordinates (pixels, origin
    /// top left), while the window is open.
    /// </summary>
    public static bool TryGetWindowRect(out Rect rect)
    {
        rect = default;
        if (!_windowOpen || _configManager == null || _settingWindowRect == null) return false;
        try
        {
            rect = (Rect)_settingWindowRect.GetValue(_configManager, null);
            return rect.width > 0f && rect.height > 0f;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Tallest the settings window may be, in pixels. The Configuration
    /// Manager makes it the screen height less 100: at 4K a 650 x 2060 pixel
    /// column, which in the headset is a strip of tiny text much taller than
    /// the view. Capped, it is a window that fits in front of the pilot.
    /// </summary>
    public const float MaxWindowHeight = 760f;

    /// <summary>
    /// Called after the Configuration Manager lays its window out: keep its
    /// width and position, cap its height and centre it again vertically.
    /// </summary>
    internal static void LimitWindowHeight(object configManager)
    {
        try
        {
            var property = configManager.GetType().GetProperty(
                "SettingWindowRect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property == null) return;

            var rect = (Rect)property.GetValue(configManager, null);
            if (rect.height <= MaxWindowHeight) return;

            rect.y = Mathf.Round((Screen.height - MaxWindowHeight) / 2f);
            rect.height = MaxWindowHeight;
            property.SetValue(configManager, rect, null);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR] IMGUI capture: could not resize the settings window: {e.Message}");
        }
    }

    /// <summary>
    /// The Configuration Manager's own F1 test is BepInEx's
    /// <c>KeyboardShortcut.IsDown</c>, which refuses whenever any other key is
    /// held, joystick buttons included. A HOTAS switch left in its "on"
    /// position is a held joystick button, so F1 did nothing on most presses.
    /// Runs after every Update: if the key went down this frame and the
    /// manager did not toggle its window, toggle it here.
    /// </summary>
    internal static void LateTick()
    {
        if (_configManager == null || _displayingWindow == null) return;

        bool displaying;
        try
        {
            displaying = _displayingWindow.GetValue(_configManager, null) is true;
        }
        catch (Exception)
        {
            return;
        }

        var key = HotkeyMainKey();
        if (key != KeyCode.None &&
            UnityEngine.Input.GetKeyDown(key) &&
            displaying == _displayingAtLastLateTick &&
            !(_overrideHotkeyField?.GetValue(_configManager) is true))
        {
            try
            {
                displaying = !displaying;
                _displayingWindow.SetValue(_configManager, displaying, null);
                Debug.Log($"[NOVR] IMGUI capture: the Configuration Manager ignored {key} ({HeldKeys(key)}); " +
                          $"{(displaying ? "opened" : "closed")} its window directly.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NOVR] IMGUI capture: could not toggle the settings window: {e.Message}");
            }
        }

        _displayingAtLastLateTick = displaying;
    }

    private static KeyCode HotkeyMainKey()
    {
        try
        {
            if (_keybindField?.GetValue(_configManager) is ConfigEntry<KeyboardShortcut> entry)
            {
                return entry.Value.MainKey;
            }
        }
        catch (Exception)
        {
            // Fall through to the default.
        }

        return KeyCode.F1;
    }

    private static string HeldKeys(KeyCode except)
    {
        var held = new System.Collections.Generic.List<string>();
        foreach (KeyCode code in Enum.GetValues(typeof(KeyCode)))
        {
            if (code == except || code == KeyCode.None) continue;
            if (code >= KeyCode.Mouse0 && code <= KeyCode.Mouse6) continue;
            if (UnityEngine.Input.GetKey(code)) held.Add(code.ToString());
            if (held.Count >= 6) break;
        }

        return held.Count == 0
            ? "no other held key found"
            : "also held: " + string.Join(", ", held);
    }

    private static bool _patchedWindowLayout;

    /// <summary>
    /// Hooks <see cref="LimitWindowHeight"/> after the manager's own layout.
    /// Patched here, on first contact, rather than with a [HarmonyPatch]: the
    /// manager's assembly need not be loaded yet when NOVR's patches are
    /// applied at start-up.
    /// </summary>
    private static void PatchWindowLayout(Type type)
    {
        if (_patchedWindowLayout) return;
        _patchedWindowLayout = true;

        var method = type.GetMethod("CalculateWindowRect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method == null)
        {
            Debug.LogWarning("[NOVR] IMGUI capture: Configuration Manager has no CalculateWindowRect; its window keeps the full screen height.");
            return;
        }

        try
        {
            new HarmonyLib.Harmony(NOVRPlugin.HarmonyId).Patch(
                method,
                postfix: new HarmonyLib.HarmonyMethod(typeof(ImguiCapture), nameof(CalculateWindowRectPostfix)));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR] IMGUI capture: could not hook the settings window layout: {e.Message}");
        }
    }

    private static void CalculateWindowRectPostfix(object __instance) => LimitWindowHeight(__instance);

    internal static void BeginRepaint()
    {
        if (!_windowOpen || _texture == null || !MenuCaptureBackend.IsActive) return;

        _previousActive = RenderTexture.active;
        RenderTexture.active = _texture;
        GL.Clear(true, true, Color.clear);
        _redirected = true;

        if (_loggedActive) return;
        _loggedActive = true;
        Debug.Log($"[NOVR] IMGUI capture: redirecting OnGUI into a {_texture.width}x{_texture.height} texture.");
    }

    internal static void EndRepaint()
    {
        if (!_redirected) return;
        RenderTexture.active = _previousActive;
        _previousActive = null;
        _redirected = false;
    }

    /// <summary>
    /// Move an input event to where the VR cursor is. IMGUI's origin is the top
    /// left; the cursor's screen point is bottom left.
    /// </summary>
    internal static void RemapInput(Event e)
    {
        if (!_windowOpen || !MenuCaptureBackend.IsActive) return;

        // Every event type, not just clicks: Layout and Repaint use the position too,
        // for hover highlights and for which control a click is about to land on.
        var cursor = VrUiCursor.I;
        if (cursor == null) return;

        var point = cursor.GetScreenPoint();
        e.mousePosition = new Vector2(point.x, Screen.height - point.y);
    }

    // ---- VR click -> OS click ---------------------------------------------

    private static bool _vrPressed;

    /// <summary>
    /// Called every frame with whether a non-mouse VR click (trigger, gaze key)
    /// is held. Edges become OS mouse button events while the window is open,
    /// because IMGUI only ever sees OS input.
    /// </summary>
    internal static void UpdateVrClick(bool pressed)
    {
        if (!_windowOpen || !ImguiCaptureConfig.VrClick || !Application.isFocused)
        {
            if (_vrPressed) SendMouse(MouseEventLeftUp);
            _vrPressed = false;
            return;
        }

        if (pressed == _vrPressed) return;
        _vrPressed = pressed;
        SendMouse(pressed ? MouseEventLeftDown : MouseEventLeftUp);
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint type;
        public MouseInput mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, NativeInput[] pInputs, int cbSize);

    private static bool _loggedSendInputFailure;

    private static void SendMouse(uint flags)
    {
        try
        {
            var inputs = new[] { new NativeInput { type = 0, mi = new MouseInput { dwFlags = flags } } };
            if (SendInput(1, inputs, Marshal.SizeOf(typeof(NativeInput))) == 0 && !_loggedSendInputFailure)
            {
                _loggedSendInputFailure = true;
                Debug.LogWarning($"[NOVR] IMGUI capture: SendInput failed ({Marshal.GetLastWin32Error()}); use the mouse button to click in the settings window.");
            }
        }
        catch (Exception e)
        {
            if (_loggedSendInputFailure) return;
            _loggedSendInputFailure = true;
            Debug.LogWarning($"[NOVR] IMGUI capture: SendInput unavailable ({e.Message}); use the mouse button to click in the settings window.");
        }
    }
}

/// <summary>Runs first on Repaint (highest depth): starts the redirect.</summary>
internal class ImguiCaptureBegin : NOVRBehaviour
{
    private void Update() => ImguiCapture.Tick();

    private void LateUpdate() => ImguiCapture.LateTick();

    private void OnGUI()
    {
        GUI.depth = int.MaxValue;
        var e = Event.current;
        // Input order is the reverse of Repaint order; remapping is idempotent,
        // so both ends do it and whichever runs first wins.
        ImguiCapture.RemapInput(e);
        if (e.type == EventType.Repaint) ImguiCapture.BeginRepaint();
    }
}

/// <summary>
/// Runs last on Repaint and first on input (lowest depth): ends the redirect,
/// and moves input events to the VR cursor before any window sees them.
/// </summary>
internal class ImguiCaptureEnd : NOVRBehaviour
{
    private void OnGUI()
    {
        GUI.depth = int.MinValue;
        var e = Event.current;
        if (e.type == EventType.Repaint) ImguiCapture.EndRepaint();
        else ImguiCapture.RemapInput(e);
    }
}

[ConfigSection(Order = 57)]
public static class ImguiCaptureConfig
{
    private const string Section = "Experimental";

    public static ConfigEntry<bool> CaptureSettingsWindow;
    public static ConfigEntry<bool> SettingsWindowVrClick;
    public static ConfigEntry<bool> SettingsWindowFlipVertical;

    public static bool Enabled => CaptureSettingsWindow == null || CaptureSettingsWindow.Value;
    public static bool VrClick => SettingsWindowVrClick == null || SettingsWindowVrClick.Value;
    public static bool FlipVertical => SettingsWindowFlipVertical != null && SettingsWindowFlipVertical.Value;

    public static void Bind(ConfigFile config)
    {
        CaptureSettingsWindow = config.Bind(
            Section,
            "Settings Window In Headset",
            true,
            "Show the BepInEx Configuration Manager window (F1) on the menu panel in the headset instead "
            + "of only on the monitor, operated with the VR cursor.");

        SettingsWindowVrClick = config.Bind(
            Section,
            "Settings Window VR Click",
            true,
            "While the settings window is open, turn a VR click (controller trigger, head-gaze click key) "
            + "into a mouse click, because the window only listens to the mouse. Turn off if clicks land twice.");

        SettingsWindowFlipVertical = config.Bind(
            Section,
            "Settings Window Flip Vertical",
            false,
            "Flip the settings window upside down on the panel. Only needed if it appears mirrored top to bottom.");
    }
}
