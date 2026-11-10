using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Places the game's message canvas (<c>MessageUI</c>: story messages, kill
/// feed and the chat box) from <see cref="MessagePanelConfig"/>.
///
/// The canvas is a whole flat screen, almost 6 m wide at 3 m, and the message
/// box is only one corner of it. A flat panel faces you squarely at one point
/// only, so with the canvas centre facing you the message box was seen at an
/// angle: tipped back, with its far side further away than its near side. The
/// panel is therefore turned about the message box instead, so the box stays
/// exactly where the settings put it and faces the room origin (your recentred
/// head) squarely, with no roll.
/// </summary>
public class NOVRMessagePanelBehaviour : UIRenderedCanvasBehavior
{
    // NOVRGameplayUIBehaviour's fixed pose: 0.003 world units per canvas pixel
    // at 3 m. The scale follows the distance so moving the panel only changes
    // its depth, not how much of the view it covers.
    private const float BaseScale = 0.003f;

    private static readonly FieldInfo? MessageBackgroundField =
        AccessTools.Field(typeof(MessageUI), "messageBackground");

    // The rotation the canvas had before this behaviour first turned it, so
    // zero angles start from what the shared gameplay pose had (which never
    // touched rotation).
    private Quaternion? _baseRotation;
    private RectTransform? _messageBox;
    private bool _lookedUpMessageBox;

    private void Update()
    {
        _baseRotation ??= transform.rotation;

        var distance = MessagePanelConfig.DistanceMeters;
        var scale = BaseScale * (distance / MessagePanelConfig.DefaultDistanceMeters) * MessagePanelConfig.Size;
        var turn = Quaternion.Euler(-MessagePanelConfig.PitchDegrees, MessagePanelConfig.YawDegrees, 0f);

        transform.localScale = new Vector3(scale, scale, scale);
        transform.SetPositionAndRotation(turn * new Vector3(0f, 0f, distance), turn * _baseRotation.Value);

        FaceMessageBox();
    }

    /// <summary>
    /// Turn the panel about the message box so the box faces the room origin.
    /// The box keeps its position; only the panel's orientation changes.
    /// </summary>
    private void FaceMessageBox()
    {
        var box = MessageBox();
        if (box == null) return;

        var boxCentre = box.TransformPoint(box.rect.center);
        var towardsBox = boxCentre;
        if (towardsBox.sqrMagnitude < 1e-6f) return;

        transform.rotation = Quaternion.LookRotation(towardsBox.normalized, Vector3.up) * _baseRotation!.Value;
        transform.position += boxCentre - box.TransformPoint(box.rect.center);
    }

    private RectTransform? MessageBox()
    {
        if (_messageBox != null) return _messageBox;
        if (_lookedUpMessageBox) return null;
        _lookedUpMessageBox = true;

        var messageUi = GetComponent<MessageUI>();
        var background = MessageBackgroundField?.GetValue(messageUi) as GameObject;
        _messageBox = background != null ? background.transform as RectTransform : null;

        if (_messageBox == null)
        {
            Debug.LogWarning("[NOVR] Message panel: MessageUI.messageBackground not found; the " +
                             "panel faces you at its centre instead of at the message box.");
        }
        else if (!_messageBox.IsChildOf(transform))
        {
            Debug.LogWarning("[NOVR] Message panel: the message box is not under the message " +
                             "canvas; the panel faces you at its centre instead of at the box.");
            _messageBox = null;
        }

        return _messageBox;
    }
}
