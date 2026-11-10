using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Places the game's chat and message canvas (<c>MessageUI</c>) from
/// <see cref="ChatPanelConfig"/>.
///
/// The canvas is a whole flat screen, almost 6 m wide at 3 m, and the chat is
/// only a box in one corner of it. A flat panel faces you squarely at one point
/// only, so with the canvas centre facing you the chat box was seen at an
/// angle: tipped back, with its far side further away than its near side. The
/// panel is therefore turned about the chat box instead, so the box stays
/// exactly where the settings put it and faces the room origin (your recentred
/// head) squarely, with no roll.
/// </summary>
public class NOVRChatPanelBehaviour : UIRenderedCanvasBehavior
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
    private RectTransform? _chatBox;
    private bool _lookedUpChatBox;

    private void Update()
    {
        _baseRotation ??= transform.rotation;

        var distance = ChatPanelConfig.DistanceMeters;
        var scale = BaseScale * (distance / ChatPanelConfig.DefaultDistanceMeters) * ChatPanelConfig.Size;
        var turn = Quaternion.Euler(-ChatPanelConfig.PitchDegrees, ChatPanelConfig.YawDegrees, 0f);

        transform.localScale = new Vector3(scale, scale, scale);
        transform.SetPositionAndRotation(turn * new Vector3(0f, 0f, distance), turn * _baseRotation.Value);

        FaceChatBox();
    }

    /// <summary>
    /// Turn the panel about the chat box so the box faces the room origin.
    /// The box keeps its position; only the panel's orientation changes.
    /// </summary>
    private void FaceChatBox()
    {
        var box = ChatBox();
        if (box == null) return;

        var boxCentre = box.TransformPoint(box.rect.center);
        var towardsBox = boxCentre;
        if (towardsBox.sqrMagnitude < 1e-6f) return;

        transform.rotation = Quaternion.LookRotation(towardsBox.normalized, Vector3.up) * _baseRotation!.Value;
        transform.position += boxCentre - box.TransformPoint(box.rect.center);
    }

    private RectTransform? ChatBox()
    {
        if (_chatBox != null) return _chatBox;
        if (_lookedUpChatBox) return null;
        _lookedUpChatBox = true;

        var messageUi = GetComponent<MessageUI>();
        var background = MessageBackgroundField?.GetValue(messageUi) as GameObject;
        _chatBox = background != null ? background.transform as RectTransform : null;

        if (_chatBox == null)
        {
            Debug.LogWarning("[NOVR] Chat panel: MessageUI.messageBackground not found; the panel " +
                             "faces you at its centre instead of at the chat.");
        }
        else if (!_chatBox.IsChildOf(transform))
        {
            Debug.LogWarning("[NOVR] Chat panel: the chat box is not under the message canvas; " +
                             "the panel faces you at its centre instead of at the chat.");
            _chatBox = null;
        }

        return _chatBox;
    }
}
