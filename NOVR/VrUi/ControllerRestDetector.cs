using UnityEngine;

namespace NOVR.VrUi;

/// <summary>
/// Whether a motion controller has been put down: held within a small
/// tolerance of one pose for <c>Controller Idle Timeout</c> seconds.
///
/// <para><b>Measured from where it came to rest, not from the last
/// frame.</b> The first version compared each frame with the one before, so
/// "moved" meant 2 cm or 3° in a single frame, which at 90 to 120 Hz is a
/// hand swung at roughly 2 m/s or 270°/s. A controller aimed calmly at a menu
/// never moves that fast, so it read as lying on the desk and vanished after
/// the timeout while it was still in the hand. Here the pose is compared with
/// the one it came to rest at, so slow drift adds up until it counts.</para>
///
/// <para><b>Why the tolerance is this tight.</b> A controller on a desk only
/// moves by tracking noise, well under a millimetre and a fraction of a
/// degree. One in a hand, however steadily it is aimed, wanders further than
/// this within a second or two. Erring tight means a controller resting
/// somewhere wobbly may never count as put down, which is the old
/// "timeout 0" behaviour; erring loose hides a controller in use.</para>
///
/// <para>Shared by <see cref="MotionControllerVisual"/> and
/// <see cref="VrUiCursor"/>, so the controller the pilot can see and the
/// controller that owns the cursor disappear together.</para>
/// </summary>
public struct ControllerRestDetector
{
    public const float RestMeters = 0.005f;
    public const float RestDegrees = 1f;

    private Vector3 _restPosition;
    private Quaternion _restRotation;
    private float _stillTime;
    private bool _hasRest;

    /// <summary>
    /// Feed this frame's pose. True once the controller has stayed within the
    /// tolerance of its rest pose for longer than <paramref name="timeout"/>.
    /// A timeout of 0 or less disables the test (never idle).
    /// </summary>
    public bool Update(Vector3 position, Quaternion rotation, float deltaTime, float timeout)
    {
        if (timeout <= 0f)
        {
            Reset();
            return false;
        }

        var moved = !_hasRest ||
                    Vector3.Distance(position, _restPosition) > RestMeters ||
                    Quaternion.Angle(_restRotation, rotation) > RestDegrees;
        if (moved)
        {
            // The first tracked frame counts as movement too: picking a
            // controller up is exactly the case this must not sit out.
            _restPosition = position;
            _restRotation = rotation;
            _stillTime = 0f;
            _hasRest = true;
            return false;
        }

        _stillTime += deltaTime;
        return _stillTime > timeout;
    }

    /// <summary>Forget the rest pose, for a controller that lost tracking.</summary>
    public void Reset()
    {
        _hasRest = false;
        _stillTime = 0f;
    }
}
