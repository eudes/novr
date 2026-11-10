using System.Collections.Generic;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace NOVR;

// Nuclear Option 0.34 removed MissionSaveLoad.QuickLoadMany; rebuild it on top of QuickLoadOne.
public static class MissionQuickLoadHelper
{
    public static List<(MissionKey key, MissionQuickLoad mission)> QuickLoadMany(IEnumerable<MissionKey> keys)
    {
        // QuickLoadOne returns only the tags saved in the mission file. SinglePlayer,
        // Multiplayer, PvE/PvP and the time-of-day tags are derived at runtime, and the
        // game's own MissionSelectPanel adds them with AddAutoTags right after loading;
        // without them every SinglePlayer/Multiplayer filter here matches nothing. A
        // missing MapLoader only skips the map-name tag.
        var mapLoader = Object.FindObjectOfType<MapLoader>();

        var result = new List<(MissionKey key, MissionQuickLoad mission)>();
        foreach (var key in keys)
        {
            if (MissionSaveLoad.QuickLoadOne(key, out var mission, out var error))
            {
                MissionTag.AddAutoTags(mission, mapLoader);
                result.Add((key, mission));
            }
            else
            {
                Debug.LogWarning($"[NOVR] Failed to quick-load mission '{key}': {error}");
            }
        }

        return result;
    }
}
