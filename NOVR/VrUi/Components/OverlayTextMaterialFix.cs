using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Makes TextMeshPro text drawn with the "Distance Field Overlay" shader show
/// up on a canvas NOVR has converted to world space.
///
/// <para>The game's menus use two TMP materials: the dropdown captions use
/// "TextMeshPro/Mobile/Distance Field", and nearly everything else (row
/// labels, slider values, headers, button text) uses "TextMeshPro/Distance
/// Field Overlay". The overlay variant is meant for screen-space overlay
/// canvases, and on a world-space canvas in the headset it draws nothing: the
/// graphics settings screen showed its dropdown values but no labels at all,
/// with every one of those labels active, opaque, unculled and inside the
/// canvas. Each such material is swapped for a copy on the ordinary distance
/// field shader, which draws the same font atlas the same way.</para>
///
/// <para>Menus are instantiated long after the canvas is converted (the
/// graphics screen is a clone made when it is opened), so the canvas is
/// rescanned every <see cref="RescanInterval"/> seconds. Copies are cached per
/// source material, so each overlay material is copied once per session.</para>
/// </summary>
public class OverlayTextMaterialFix : MonoBehaviour
{
    private const float RescanInterval = 1f;
    private const string OverlayShaderName = "TextMeshPro/Distance Field Overlay";

    private static readonly Dictionary<Material, Material> Replacements = new();
    private static Shader? _replacementShader;
    private static bool _lookedUpShader;

    private readonly List<TMP_Text> _texts = new();
    private readonly List<TMP_SubMeshUI> _subMeshes = new();
    private float _nextScan;

    private void OnEnable() => _nextScan = 0f;

    private void LateUpdate()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + RescanInterval;

        var shader = ReplacementShader();
        if (shader == null) return;

        GetComponentsInChildren(true, _texts);
        foreach (var text in _texts)
        {
            if (text == null) continue;
            var replacement = Replacement(text.fontSharedMaterial, shader);
            if (replacement != null) text.fontSharedMaterial = replacement;
        }

        // Glyphs from a fallback font are drawn by a sub-mesh with its own
        // material, which would otherwise stay invisible in the same way.
        GetComponentsInChildren(true, _subMeshes);
        foreach (var subMesh in _subMeshes)
        {
            if (subMesh == null) continue;
            var replacement = Replacement(subMesh.sharedMaterial, shader);
            if (replacement != null) subMesh.sharedMaterial = replacement;
        }
    }

    private static Material? Replacement(Material? source, Shader shader)
    {
        if (source == null || source.shader == null || source.shader.name != OverlayShaderName) return null;

        if (!Replacements.TryGetValue(source, out var replacement) || replacement == null)
        {
            replacement = new Material(source)
            {
                name = source.name + " (NOVR world space)",
                shader = shader,
            };
            Replacements[source] = replacement;
        }

        return replacement;
    }

    private static Shader? ReplacementShader()
    {
        if (_lookedUpShader) return _replacementShader;
        _lookedUpShader = true;

        // Shader.Find only finds shaders the game's build included. The full
        // distance field shader is preferred; the mobile one is certainly
        // included, because the game's own dropdown captions draw with it.
        _replacementShader = Shader.Find("TextMeshPro/Distance Field") ??
                             Shader.Find("TextMeshPro/Mobile/Distance Field");

        Debug.Log(_replacementShader != null
            ? $"[NOVR] World-space menus: '{OverlayShaderName}' text is redrawn with '{_replacementShader.name}'."
            : $"[NOVR] World-space menus: no distance field shader found; '{OverlayShaderName}' text stays invisible.");
        return _replacementShader;
    }
}
