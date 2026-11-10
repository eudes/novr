using BepInEx.Configuration;
using UnityEngine;

namespace NOVR.VrUi;

/// <summary>
/// Placement of the game's chat and message canvas (<c>MessageUI</c>). It used
/// to share <see cref="SpecialBehavior.NOVRGameplayUIBehaviour"/>'s fixed pose
/// (3 m ahead, scale 0.003) with the gameplay, menu and maximised map canvases,
/// which puts a canvas almost 6 m wide at arm's reach: the chat, in a
/// corner of it, lands close and well off to one side. These settings move
/// the chat canvas on its own, leaving the other canvases where they were.
///
/// The settings live here rather than in ModConfiguration for the reason given
/// on <see cref="ConfigSectionAttribute"/>.
/// </summary>
[ConfigSection(Order = 58)]
public static class ChatPanelConfig
{
    private const string Section = "Chat Panel";

    public const float DefaultDistanceMeters = 3f;

    public static ConfigEntry<float> DistanceEntry;
    public static ConfigEntry<float> SizeEntry;
    public static ConfigEntry<float> YawEntry;
    public static ConfigEntry<float> PitchEntry;
    public static ConfigEntry<float> BackgroundOpacityEntry;

    // Every read goes through these: ConfigSections catches a section that
    // fails to bind and carries on, so an entry can legitimately be null and
    // the panel must fall back to the old fixed pose rather than throw every
    // frame.
    public static float DistanceMeters =>
        DistanceEntry != null ? Mathf.Clamp(DistanceEntry.Value, 1f, 10f) : DefaultDistanceMeters;

    public static float Size => SizeEntry != null ? Mathf.Clamp(SizeEntry.Value, 0.25f, 2f) : 1f;

    public static float YawDegrees => YawEntry != null ? Mathf.Clamp(YawEntry.Value, -60f, 60f) : 0f;

    public static float PitchDegrees => PitchEntry != null ? Mathf.Clamp(PitchEntry.Value, -45f, 45f) : 0f;

    public static float BackgroundOpacity =>
        BackgroundOpacityEntry != null ? Mathf.Clamp01(BackgroundOpacityEntry.Value) : 1f;

    public static void Bind(ConfigFile config)
    {
        DistanceEntry = config.Bind(
            Section,
            "Chat Panel Distance",
            DefaultDistanceMeters,
            new ConfigDescription(
                "How far in front of you the chat and message panel sits, in metres. Only the "
                + "depth changes: the panel is scaled with the distance so it covers the same part "
                + "of your view, and text stays the same apparent size. Raise it if the chat feels "
                + "too close to focus on comfortably. Takes effect immediately.",
                new AcceptableValueRange<float>(1f, 10f)));

        SizeEntry = config.Bind(
            Section,
            "Chat Panel Size",
            1f,
            new ConfigDescription(
                "How large the chat and message panel is, as a multiple of its default size. The "
                + "chat sits in a corner of the panel, so a smaller panel also brings the "
                + "chat in towards the centre of your view. Takes effect immediately.",
                new AcceptableValueRange<float>(0.25f, 2f)));

        YawEntry = config.Bind(
            Section,
            "Chat Panel Horizontal Angle",
            0f,
            new ConfigDescription(
                "Turns the chat and message panel around you, in degrees. Positive moves it to the "
                + "right, negative to the left. The panel turns to keep facing you. Takes effect "
                + "immediately.",
                new AcceptableValueRange<float>(-60f, 60f)));

        PitchEntry = config.Bind(
            Section,
            "Chat Panel Vertical Angle",
            0f,
            new ConfigDescription(
                "Tilts the chat and message panel up or down around you, in degrees. Positive moves "
                + "it up, negative down. The panel tilts to keep facing you. Takes effect "
                + "immediately.",
                new AcceptableValueRange<float>(-45f, 45f)));

        BackgroundOpacityEntry = config.Bind(
            Section,
            "Chat Panel Background Opacity",
            1f,
            new ConfigDescription(
                "How dark the box behind the chat and message text is, as a multiple of what the "
                + "game draws. The game fades the box between a light shade while you only read "
                + "and a darker one while you type; this scales both. 0 removes the box entirely "
                + "(text only), 1 is the game's own. Takes effect the next time the box fades, for "
                + "example when you open or close the chat.",
                new AcceptableValueRange<float>(0f, 1f)));
    }
}
