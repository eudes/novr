using BepInEx.Configuration;

namespace NOVR;

/// <summary>
/// The OpenXR stereo render mode. The plugin only declares the setting so it
/// shows in the config file and the F1 window; NOVR.Patcher reads it from the
/// file before the game starts, since the mode has to be patched into
/// OpenXRSettings in the preloader.
/// </summary>
[ConfigSection(Order = 99)]
public static class StereoRenderModeConfig
{
    public enum StereoRenderMode
    {
        MultiPass,
        SinglePassInstanced,
    }

    public static ConfigEntry<StereoRenderMode>? ModeEntry;

    public static void Bind(ConfigFile config)
    {
        ModeEntry = config.Bind(
            "Experimental",
            "Stereo Render Mode",
            StereoRenderMode.MultiPass,
            "How the two eyes are rendered. MultiPass renders the whole scene once per eye. "
            + "SinglePassInstanced renders both eyes in one pass, which would save several ms of "
            + "main thread a frame, but NOVR's original author found it broken with this game, most "
            + "likely because the game's shaders were built without the stereo variants it needs: "
            + "expect parts of the world missing or wrong in one eye. For trying it out only. "
            + "Takes effect on the next launch.");
    }
}
