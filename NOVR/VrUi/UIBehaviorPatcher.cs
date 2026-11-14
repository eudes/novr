using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace NOVR.VrUi;

public class UIBehaviorPatcher : NOVRBehaviour
{

    private static Dictionary<Type, Type> _patchMap = BuildPatchMap();

    private static Dictionary<Type, Type> BuildPatchMap()
    {
        var map = new Dictionary<Type, Type>
        {
            { typeof(GameplayUI), typeof(NOVRGameplayUIBehaviour) },
            { typeof(MessageUI), typeof(NOVRMessagePanelBehaviour) },
            { typeof(StatusDisplay), typeof(NOVRStatusDisplayBehavior) }
        };

        //: The flight HUD is the single entry the Vanilla Flight HUD diagnostic
        //: withholds. NOVRFlightHudBehavior is the root of all of it: it is what
        //: converts the canvas to world space, and it attaches the HUD/HMD centre
        //: behaviours, the pitch compass and the target designator on the way.
        //: Leaving it off is what makes the HUD the game's own.
        if (VanillaFlightHud.ModdedHudActive)
        {
            map[typeof(FlightHud)] = typeof(NOVRFlightHudBehavior);
        }

        return map;
    }

    private static Dictionary<string, Type> _sceneLoadPatchMap = new() // We patch gameobjects by name the first time a scene is loaded (yes we iterate the tree recursively)
    {
        { "MainCanvas", typeof(NOVRMainMenuBehavior) },
        { "MenuCanvas", typeof(NOVRGameplayUIBehaviour) },
        { "MaximizedMapCanvas", typeof(NOVRGameplayUIBehaviour) },
        { "BlackoutCanvas", typeof(NOVRBlackoutCanvasBehavior)}, //Template.ForCanvas("BlackoutCanvas", UiTranslationSpace.ScreenSpace, GameUiRegion.Absolute),
        { "SceneEssentials", typeof(PositionZeroBehavior)},
        { "Canvas", typeof(PositionZeroBehavior)},
    };


    private static Dictionary<Component, Type> _toPatch_component = new();
    private static Dictionary<string, Type> _toPatch_name = new();
    private static List<GameObject> _toReactivate = new();
    private static readonly List<KeyValuePair<Component, Type>> _notReady = new();
    

    static UIBehaviorPatcher()
    {
        SceneManager.sceneLoaded += SceneLoaded;
    }

    private static void SceneLoaded(Scene arg0, LoadSceneMode arg1)
    {
        Debug.Log("UIBehaviorPatcher: Scene loaded");
        foreach (var kvp in _sceneLoadPatchMap) _toPatch_name[kvp.Key] = kvp.Value;
    }


    public static void DoPatching()
    {
        var harmony = new Harmony("novr.vrui.behavioradder");
        
        var postfixMethod = typeof(UIBehaviorPatcher).GetMethod(nameof(UniversalPostfix), BindingFlags.Static | BindingFlags.NonPublic);

        foreach (var entry in _patchMap)
        {
            Type targetType = entry.Key;
            
            ConstructorInfo ctor = targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

            if (ctor != null)
            {
                harmony.Patch(ctor, postfix: new HarmonyMethod(postfixMethod));
            }
        }
    }
    
    static void UniversalPostfix(object __instance)
    {
        if (__instance is Component comp)
        {
            EnqueueForAddition(comp);
        }
    }

    // Adding components directly from the constructor scope is risky so we defer it till the next update
    static void EnqueueForAddition(Component comp)
    {
        var toAddComp = _patchMap[comp.GetType()];
        _toPatch_component[comp] = toAddComp;
    }


    private void Start()
    {
        foreach (var kvp in _sceneLoadPatchMap) _toPatch_name[kvp.Key] = kvp.Value;
    }
    
    private void FixedUpdate()
    {
        if (_toReactivate.Count > 0)
        {
            foreach (var go in _toReactivate.Where(go => go != null))
            {
                Debug.Log($"UIBehaviorPatcher: Reactivating {go.name}");
                go.SetActive(true);
            }
            
            _toReactivate.Clear();
        }
        
        if (_toPatch_component.Count > 0)
        {
            // A component whose object has no name yet is still being built; it
            // stays queued for the next tick. The old code returned out of the
            // whole method instead, which left the queue full and logged the
            // same two lines again on every physics tick until the name arrived.
            _notReady.Clear();
            foreach (var kvp in _toPatch_component)
            {
                var comp = kvp.Key;
                if (comp == null) continue;
                if (string.IsNullOrEmpty(comp.name))
                {
                    _notReady.Add(kvp);
                    continue;
                }

                var toAdd = kvp.Value;
                Debug.Log($"UIBehaviorPatcher: Adding {toAdd.Name} to {comp.name} (component patch)");
                if (!comp.gameObject.TryGetComponent(toAdd, out Component _))
                {
                    AddAndBounceIfActive(comp.gameObject, toAdd);
                }
            }

            _toPatch_component.Clear();
            foreach (var kvp in _notReady) _toPatch_component[kvp.Key] = kvp.Value;
        }


        if (_toPatch_name.Count > 0)
        {
            foreach (var go in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
            {
                if (go == null) continue;
                var name = go.name;

                // FindObjectsOfTypeAll also returns prefab assets, and a prefab
                // that gets a component here passes it to every copy the game
                // spawns later. That includes the generic "Canvas" name, so
                // every nested canvas in a spawned unit prefab is pinned to the
                // world origin. It looks like a bug, and FinnGrndl/novr#2 limited
                // "Canvas" to scene objects, but the first headset test of that
                // showed a second, cockpit-fixed copy of HUD elements that
                // shifted against the captured HUD with head movement. Most
                // likely one of those prefab canvases is the aircraft's own
                // in-cockpit HUD, which NOVR replaces with the captured HUD and
                // which the pinning has been keeping out of view. Do not filter
                // prefabs out here without first hiding that canvas another way.
                if (_toPatch_name.TryGetValue(name, out var toAdd))
                {
                    Debug.Log($"UIBehaviorPatcher: Adding {toAdd.Name} to {name} (name patch)");
                    if (!go.TryGetComponent(toAdd, out Component _)) AddAndBounceIfActive(go, toAdd);
                }
            }
            _toPatch_name.Clear();
        }
    }
    
    private static void AddAndBounceIfActive(GameObject go, Type toAdd)
    {
        var wasActive = go.activeInHierarchy;
        go.AddComponent(toAdd);

        if (!wasActive) return;
        
        Debug.Log($"UIBehaviorPatcher: Deactivating {go.name} for one frame to force lifecycle callbacks");
        go.SetActive(false);
        _toReactivate.Add(go);
    }
}
