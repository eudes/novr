using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Restores the one thing a screen-space canvas provided for free and a
/// world-space canvas does not: an edge.
///
/// The game parks UI it does not currently want by moving it outside the canvas
/// rect — the gameplay MFD screens sit at local x = ±2560 on a 1920-wide canvas
/// while closed. On a ScreenSpaceOverlay canvas that is enough, because there is
/// nothing to rasterise them into; the screen *is* the clip. NOVR converts these
/// canvases to WorldSpace, where the rect is just a number and the geometry is
/// ordinary geometry, so the parked panels become real objects hanging beside
/// the pilot. Seen from the cockpit they read as two faint white rectangles far
/// left and right, apparently in the HUD plane, because that is exactly where
/// they are — the MFD panel backgrounds, white, 450×650, viewed nearly edge-on.
///
/// So: anything whose rect lies entirely outside the canvas rect gets culled.
/// Entirely outside, not partially — a partially visible element is something
/// the pilot is meant to see part of, and hiding it would be a worse bug than
/// the one being fixed. Culling through CanvasRenderer.cull is what RectMask2D
/// itself uses; a RectMask2D would also clip the partial cases, but it works by
/// driving a shader clip rect, and this fork has already been bitten once by UI
/// masking that did not survive the world-space conversion (see the render-queue
/// notes in the research repo). Culling depends on no shader support at all, and
/// its worst failure is that something stays visible.
/// </summary>
public class OffscreenGraphicCuller : MonoBehaviour
{
    /// <summary>
    /// How often the graphic list is rebuilt. The cull test itself runs every
    /// frame against the cached list, so a panel sliding back into view appears
    /// immediately; only newly *created* graphics wait for a rescan.
    /// </summary>
    private const float RescanInterval = 1f;

    /// <summary>
    /// Slack in canvas units, so an element deliberately flush with the edge is
    /// never culled by float error.
    /// </summary>
    private const float Margin = 2f;

    // Per graphic, filled at each rescan. This runs on every converted canvas
    // every frame over hundreds of graphics, so it keeps engine calls per
    // graphic to a minimum: the frame timing log put the old version, at
    // seven engine calls per graphic, at about 1 ms of main thread a frame.
    private readonly List<Graphic> _scan = new();
    private Graphic[] _graphics = Array.Empty<Graphic>();
    private RectTransform[] _rects = Array.Empty<RectTransform>();
    private CanvasRenderer?[] _renderers = Array.Empty<CanvasRenderer?>();

    // The cull value last written per graphic, so CanvasRenderer.cull is
    // neither read back nor rewritten every frame.
    private bool[] _culled = Array.Empty<bool>();

    private RectTransform _canvasRect;
    private float _nextRescan;

    private void Awake()
    {
        _canvasRect = GetComponent<RectTransform>();
    }

    private void LateUpdate()
    {
        if (_canvasRect == null) return;

        if (Time.unscaledTime >= _nextRescan)
        {
            _nextRescan = Time.unscaledTime + RescanInterval;
            Rescan();
        }

        var bounds = _canvasRect.rect;
        bounds.xMin -= Margin;
        bounds.xMax += Margin;
        bounds.yMin -= Margin;
        bounds.yMax += Margin;

        var worldToCanvas = _canvasRect.worldToLocalMatrix;

        for (var i = 0; i < _graphics.Length; i++)
        {
            var graphic = _graphics[i];

            // An inactive graphic draws nothing, so its cull flag can wait until
            // it is active again; this LateUpdate runs before the frame renders.
            if (graphic == null || !graphic.isActiveAndEnabled) continue;

            var renderer = _renderers[i];
            if (renderer == null) continue;

            var cull = IsEntirelyOutside(bounds, worldToCanvas, _rects[i]);

            // Only write on a change: CanvasRenderer.cull dirties the batch when
            // it is set, even to the value it already had.
            if (_culled[i] != cull)
            {
                renderer.cull = cull;
                _culled[i] = cull;
            }
        }
    }

    private void Rescan()
    {
        _scan.Clear();
        GetComponentsInChildren(true, _scan);

        var count = _scan.Count;
        _graphics = _scan.ToArray();
        _rects = new RectTransform[count];
        _renderers = new CanvasRenderer?[count];
        _culled = new bool[count];

        for (var i = 0; i < count; i++)
        {
            var graphic = _graphics[i];
            _rects[i] = graphic.rectTransform;
            _renderers[i] = graphic.canvasRenderer;
            _culled[i] = _renderers[i] != null && _renderers[i]!.cull;
        }
    }

    private static bool IsEntirelyOutside(Rect bounds, Matrix4x4 worldToCanvas, RectTransform target)
    {
        if (target == null) return false;

        // The element's corners in canvas space, through one combined matrix
        // rather than world corners plus four InverseTransformPoint calls; the
        // element may be scaled, rotated or nested several layouts deep, and
        // the only position that matters is the one it renders at.
        var toCanvas = worldToCanvas * target.localToWorldMatrix;
        var rect = target.rect;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < 4; i++)
        {
            var corner = new Vector3(i < 2 ? rect.xMin : rect.xMax, i == 0 || i == 3 ? rect.yMin : rect.yMax, 0f);
            var local = toCanvas.MultiplyPoint3x4(corner);
            if (local.x < minX) minX = local.x;
            if (local.x > maxX) maxX = local.x;
            if (local.y < minY) minY = local.y;
            if (local.y > maxY) maxY = local.y;
        }

        return maxX < bounds.xMin || minX > bounds.xMax ||
               maxY < bounds.yMin || minY > bounds.yMax;
    }
}
