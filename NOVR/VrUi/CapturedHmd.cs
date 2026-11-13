using BepInEx.Configuration;

namespace NOVR.VrUi;

/// <summary>
/// Settings for the captured helmet-mounted display and the gaze target
/// designator — the base game's own head-tracking systems, carried into VR
/// with their code unmodified. See <c>VrUi/Capture/HmdVisorBackend.cs</c> and
/// <c>VrUi/Components/GazeDesignatorDriver.cs</c> for what they do.
///
/// Both only act while the captured flight HUD is active
/// (<see cref="CapturedFlightHud"/>): they are the head-referenced half of the
/// same faithful-to-the-original arrangement, and without the captured HUD the
/// modded world-space path has its own equivalents.
/// </summary>
[ConfigSection(Order = 95)]
public static class CapturedHmd
{
    public static ConfigEntry<bool> Visor;
    public static ConfigEntry<float> VisorFieldOfView;
    public static ConfigEntry<float> ViewIconsFieldOfView;
    public static ConfigEntry<float> PanelShading;
    public static ConfigEntry<float> PanelSpread;
    public static ConfigEntry<bool> PanelLock;
    public static ConfigEntry<bool> GazeDesignator;
    public static ConfigEntry<bool> ViewIcons;

    public static ConfigEntry<float> ViewIconsMarkerScale;

    /// <summary>
    /// Multiplier on the size of the game's unit markers on the View Icons
    /// screen. Null-safe like every other read here.
    /// </summary>
    public static float ViewIconsMarkerScaleFactor =>
        UnityEngine.Mathf.Clamp(ViewIconsMarkerScale?.Value ?? 1f, 0.5f, 5f);

    /// <summary>
    /// The View Icons screen's horizontal field of view: its own setting, or
    /// the visor's when that is 0. Null-safe like every other read here.
    /// </summary>
    public static float ViewIconsFieldOfViewDegrees
    {
        get
        {
            var own = ViewIconsFieldOfView?.Value ?? 0f;
            var fov = own > 0f ? own : VisorFieldOfView?.Value ?? 70f;
            return UnityEngine.Mathf.Clamp(fov, 30f, 110f);
        }
    }

    public static void Bind(ConfigFile config)
    {
        Visor = config.Bind(
            "Experimental",
            "Captured HMD Visor",
            true,
            "Show the game's own helmet-mounted display — the minimal speed/altitude/bearing/"
            + "horizon readouts the flat game glues to the view — on a head-locked panel, the way "
            + "the original game glues them to the screen. The game's own declutter runs "
            + "unmodified: readouts near the nose hide when you look at the HUD and return when "
            + "you look away (the HMD Hide Distance game setting works again). Only active while "
            + "Captured Flight HUD is on. Takes effect immediately.");

        VisorFieldOfView = config.Bind(
            "Experimental",
            "Captured HMD Field Of View",
            70f,
            new ConfigDescription(
                "Horizontal angle the helmet visor panel covers, in degrees. The flat game "
                + "spreads the HMD readouts across the whole screen; this is how much of your "
                + "view that screen maps to.",
                new AcceptableValueRange<float>(30f, 110f)));

        ViewIconsFieldOfView = config.Bind(
            "Experimental",
            "View Icons Field Of View",
            0f,
            new ConfigDescription(
                "Horizontal angle the View Icons virtual screen covers, in degrees, or 0 to use "
                + "Captured HMD Field Of View. An off-view selected target or waypoint becomes an "
                + "arrow pinned to this screen's edge, so this sets how far out from the centre "
                + "of your view those arrows sit: lower brings them in. Icons on units stay on "
                + "their units whatever the value. Set it separately to move the arrows without "
                + "also resizing the helmet readouts and the weapon/map panels. Takes effect "
                + "immediately.",
                new AcceptableValueRange<float>(0f, 110f)));

        ViewIconsMarkerScale = config.Bind(
            "Experimental",
            "View Icons Marker Scale",
            1f,
            new ConfigDescription(
                "Size of the game's markers on units (the full icons, the small square or "
                + "triangle a distant hostile shrinks to, and the selected target's box), without "
                + "moving anything. The View Icons screen packs the flat game's 1920 pixels into "
                + "View Icons Field Of View, so a narrow field of view also shrinks every marker "
                + "against the world: at 40 degrees they are less than half the size they are in "
                + "the flat game, and about 3 brings them back. Applies the next time the game "
                + "resizes a marker, within a second or so.",
                new AcceptableValueRange<float>(0.5f, 5f)));

        PanelShading = config.Bind(
            "Experimental",
            "Captured HMD Panel Shading",
            0.65f,
            new ConfigDescription(
                "How dark the backing behind the weapon readout, the tactical map and the HMD "
                + "number boxes is — the flat game draws them over a translucent black panel with "
                + "a soft edge, which the pilot needs to read green symbols against bright cloud. "
                + "0 turns it off (symbols only, like a HUD combiner, which cannot darken "
                + "anything); 0.65 is the flat game measured, which passes about a third of what "
                + "is behind it. The number is in the flat game's units and is converted for the "
                + "colour space this panel is composited in, so it means the same thing here as "
                + "it does there. Only the helmet visor is shaded — the airframe HUD panel is "
                + "combiner glass and stays purely additive. Takes effect immediately.",
                new AcceptableValueRange<float>(0f, 1f)));

        PanelSpread = config.Bind(
            "Experimental",
            "Captured HMD Panel Spread",
            10f,
            new ConfigDescription(
                "How far out from the centre of your view the tactical map and the weapon/"
                + "countermeasure readout sit, in degrees. The flat game puts them in screen "
                + "corners, and a screen is much wider than the visor, so mapped onto the visor "
                + "they crowd the middle of your view where the flight HUD already is. Each moves "
                + "outwards along its own bearing, and is clamped so it stays on the visor — a "
                + "large element may not be able to take the whole amount. 0 leaves them exactly "
                + "where the flat game puts them. The four HMD readouts (speed, altitude, "
                + "bearing, horizon) are not affected: the game's own HMD settings place those. "
                + "Takes effect immediately.",
                new AcceptableValueRange<float>(0f, 30f)));

        PanelLock = config.Bind(
            "Experimental",
            "Captured HMD Panel Lock",
            true,
            "Fix the tactical map and the weapon/countermeasure readout in the cockpit, so they "
            + "hold still while you look around, instead of carrying them on the head-locked "
            + "visor with the rest of the helmet display. The flat game puts them on the screen "
            + "because a screen is where the pilot is already looking; in VR that makes two large, "
            + "dense panels ride your view wherever it goes. The four HMD readouts (speed, "
            + "altitude, bearing, horizon) stay on the visor — they are small, they are what a "
            + "helmet display is for, and they are the only widgets the game itself places and "
            + "declutters. Off puts the map and the readout back on the visor. Takes effect "
            + "immediately.");

        GazeDesignator = config.Bind(
            "Experimental",
            "Gaze Target Designator",
            true,
            "Restore the original game's look-to-select: the target designator follows your "
            + "gaze across the HUD, and Select picks the marker under it — the game's own "
            + "selection code (radius, priority, painting, the edge arrow) runs unmodified, it "
            + "is only handed where you are actually looking instead of a fixed screen centre. "
            + "Only active while Captured Flight HUD is on with Conformal. Takes effect "
            + "immediately.");

        ViewIcons = config.Bind(
            "Experimental",
            "View Icons",
            true,
            "Put the game's screen-space icons — unit markers, the target designator, the "
            + "selected-target edge arrow, radar warnings, missile notch cues and the objective "
            + "pointer — on a head-locked virtual screen, the way the flat game spreads them "
            + "across the whole screen and the screen follows the view. Icons appear wherever "
            + "you look, the designator sits fixed at the centre of your view (Select picks the "
            + "marker under it), and an off-view selected target becomes the game's own edge "
            + "arrow pinned to the virtual screen's edge. The screen spans View Icons Field Of "
            + "View. Only active while Captured Flight HUD is on. Takes effect immediately.");
    }
}
