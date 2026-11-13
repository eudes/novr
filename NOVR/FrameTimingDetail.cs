using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace NOVR;

/// <summary>
/// Times the main thread's frame by putting timestamp markers into Unity's
/// player loop, so the log can say where the main thread's time goes and,
/// in particular, how much of it is spent waiting for the XR runtime's frame
/// slot rather than working. Unity's own main thread figure
/// (<see cref="FrameTiming.cpuMainThreadFrameTime"/>) includes that wait,
/// so on its own it cannot tell a CPU-bound frame from a paced one.
///
/// <para>Each marker adds the time since the previous marker to the segment
/// the previous marker opened. A segment is labelled with what runs after
/// its marker, so attribution stays right when a phase repeats within a
/// frame (FixedUpdate runs once per physics step). A frame starts at the
/// first marker of the first phase; the time between the end of the last
/// phase and that marker is "outside player loop".</para>
///
/// <para>Simple mode puts one marker at the start of each top-level phase
/// (about ten callbacks a frame). Detailed mode puts one before every step of
/// every phase (a little over a hundred), which names the steps that cost
/// the most.</para>
/// </summary>
internal sealed class PlayerLoopTimer
{
    private sealed class FrameTimingMarker
    {
    }

    private const string OutsideLabel = "outside player loop";

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private string[] _labels = Array.Empty<string>();
    private int[] _phaseOf = Array.Empty<int>();
    private string[] _phaseNames = Array.Empty<string>();

    private long[] _frameTicks = Array.Empty<long>();
    private double[] _segmentSum = Array.Empty<double>();
    private double[] _segmentMax = Array.Empty<double>();
    private double[] _phaseFrame = Array.Empty<double>();
    private double[] _phaseSum = Array.Empty<double>();
    private double[] _phaseMax = Array.Empty<double>();

    private int _frames;
    private int _lastIndex = -1;
    private long _lastStamp;

    public bool Installed { get; private set; }
    public bool Detailed { get; private set; }
    public int Frames => _frames;

    public void Install(bool detailed)
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        Strip(ref loop);

        var top = loop.subSystemList;
        if (top == null || top.Length == 0) return;

        var labels = new List<string>();
        var phaseOf = new List<int>();
        var phaseNames = new List<string>();

        for (var p = 0; p < top.Length; p++)
        {
            var phaseName = top[p].type?.Name ?? "Phase" + p;
            phaseNames.Add(phaseName);

            var steps = top[p].subSystemList ?? Array.Empty<PlayerLoopSystem>();
            var list = new List<PlayerLoopSystem>(steps.Length * 2 + 2);
            var last = p == top.Length - 1;

            if (detailed)
            {
                foreach (var step in steps)
                {
                    list.Add(Marker(labels.Count));
                    labels.Add(phaseName + "." + (step.type?.Name ?? "?"));
                    phaseOf.Add(p);
                    list.Add(step);
                }

                list.Add(Marker(labels.Count));
                labels.Add(last ? OutsideLabel : phaseName + " gap");
                phaseOf.Add(last ? top.Length : p);
            }
            else
            {
                list.Add(Marker(labels.Count));
                labels.Add(phaseName);
                phaseOf.Add(p);
                list.AddRange(steps);

                if (last)
                {
                    list.Add(Marker(labels.Count));
                    labels.Add(OutsideLabel);
                    phaseOf.Add(top.Length);
                }
            }

            top[p].subSystemList = list.ToArray();
        }

        phaseNames.Add(OutsideLabel);

        _labels = labels.ToArray();
        _phaseOf = phaseOf.ToArray();
        _phaseNames = phaseNames.ToArray();
        _frameTicks = new long[_labels.Length];
        _segmentSum = new double[_labels.Length];
        _segmentMax = new double[_labels.Length];
        _phaseFrame = new double[_phaseNames.Length];
        _phaseSum = new double[_phaseNames.Length];
        _phaseMax = new double[_phaseNames.Length];
        _lastIndex = -1;
        ResetInterval();

        PlayerLoop.SetPlayerLoop(loop);
        Installed = true;
        Detailed = detailed;
    }

    public void Uninstall()
    {
        if (!Installed) return;
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        if (Strip(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        Installed = false;
        _lastIndex = -1;
    }

    public void ResetInterval()
    {
        _frames = 0;
        Array.Clear(_segmentSum, 0, _segmentSum.Length);
        Array.Clear(_segmentMax, 0, _segmentMax.Length);
        Array.Clear(_phaseSum, 0, _phaseSum.Length);
        Array.Clear(_phaseMax, 0, _phaseMax.Length);
    }

    /// <summary>"Phase avg/max" for each phase that takes measurable time.</summary>
    public void AppendPhases(StringBuilder sb)
    {
        var first = true;
        for (var p = 0; p < _phaseNames.Length; p++)
        {
            var avg = _phaseSum[p] / _frames;
            if (avg < 0.05 && _phaseMax[p] < 0.5) continue;
            sb.Append(first ? "" : ", ").Append(_phaseNames[p]).Append(' ')
                .Append(F(avg)).Append('/').Append(F(_phaseMax[p]));
            first = false;
        }
    }

    /// <summary>The <paramref name="count"/> steps with the highest average.</summary>
    public void AppendTopSteps(StringBuilder sb, int count)
    {
        var order = Enumerable.Range(0, _labels.Length)
            .OrderByDescending(i => _segmentSum[i])
            .Take(count);

        var first = true;
        foreach (var i in order)
        {
            var avg = _segmentSum[i] / _frames;
            if (avg < 0.05) break;
            sb.Append(first ? "" : ", ").Append(_labels[i]).Append(' ')
                .Append(F(avg)).Append('/').Append(F(_segmentMax[i]));
            first = false;
        }
    }

    private PlayerLoopSystem Marker(int index) => new()
    {
        type = typeof(FrameTimingMarker),
        updateDelegate = () => OnMarker(index),
    };

    private void OnMarker(int index)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastIndex >= 0)
        {
            _frameTicks[_lastIndex] += now - _lastStamp;
            if (index == 0) FoldFrame();
        }

        _lastIndex = index;
        _lastStamp = now;
    }

    private void FoldFrame()
    {
        Array.Clear(_phaseFrame, 0, _phaseFrame.Length);
        for (var i = 0; i < _frameTicks.Length; i++)
        {
            var ms = _frameTicks[i] * MsPerTick;
            _frameTicks[i] = 0;
            _segmentSum[i] += ms;
            if (ms > _segmentMax[i]) _segmentMax[i] = ms;
            _phaseFrame[_phaseOf[i]] += ms;
        }

        for (var p = 0; p < _phaseFrame.Length; p++)
        {
            _phaseSum[p] += _phaseFrame[p];
            if (_phaseFrame[p] > _phaseMax[p]) _phaseMax[p] = _phaseFrame[p];
        }

        _frames++;
    }

    /// <summary>Removes this class's markers. True if there were any.</summary>
    private static bool Strip(ref PlayerLoopSystem loop)
    {
        var top = loop.subSystemList;
        if (top == null) return false;

        var removed = false;
        for (var p = 0; p < top.Length; p++)
        {
            var steps = top[p].subSystemList;
            if (steps == null || !steps.Any(IsMarker)) continue;
            top[p].subSystemList = steps.Where(s => !IsMarker(s)).ToArray();
            removed = true;
        }

        return removed;
    }

    private static bool IsMarker(PlayerLoopSystem system) => system.type == typeof(FrameTimingMarker);

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Measures how much main thread time NOVR's own code takes, by patching
/// with Harmony every per-frame message method of NOVR's MonoBehaviours
/// (Update, LateUpdate, the render callbacks of <see cref="NOVRBehaviour"/>
/// and so on) and every prefix, postfix and finalizer NOVR adds to game
/// code. Only the outermost NOVR call on the main thread is timed, so a NOVR
/// method calling into patched game code is not counted twice.
///
/// <para>Not covered: transpilers (they change game code rather than run
/// on their own), NOVR handlers subscribed to events under other names, and
/// render work NOVR causes (its cameras are timed by
/// <see cref="CameraRenderTimer"/>). The timing patches cost a little per
/// call themselves, which the calls-per-frame figure lets the reader judge.</para>
/// </summary>
internal static class NovrCodeProfiler
{
    private const string HarmonyId = "deltawing.novr.frametiming";

    private static readonly HashSet<string> MessageNames = new()
    {
        "Update", "LateUpdate", "FixedUpdate", "OnGUI",
        "OnBeforeRender", "OnBeginFrameRendering", "OnEndFrameRendering",
        "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnWillRenderObject",
    };

    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private static Harmony? _harmony;
    private static readonly Dictionary<IntPtr, int> Index = new();
    private static readonly List<string> Names = new();
    private static long[] _ticks = Array.Empty<long>();
    private static int[] _calls = Array.Empty<int>();
    private static int _mainThreadId;
    private static int _depth;
    private static long _outerStart;

    public static bool Active => _harmony != null;
    public static int PatchedCount => Names.Count;
    public static int FailedCount { get; private set; }

    public static void Start()
    {
        if (_harmony != null) return;

        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _depth = 0;
        Index.Clear();
        Names.Clear();
        FailedCount = 0;

        var targets = new List<(MethodBase Method, string Name)>();
        CollectMessageMethods(targets);
        CollectPatchMethods(targets);

        _harmony = new Harmony(HarmonyId);
        var prefix = new HarmonyMethod(typeof(NovrCodeProfiler), nameof(Prefix));
        var finalizer = new HarmonyMethod(typeof(NovrCodeProfiler), nameof(Finalizer));

        var slots = new Dictionary<string, int>();
        foreach (var (method, name) in targets)
        {
            var handle = method.MethodHandle.Value;
            if (Index.ContainsKey(handle)) continue;

            if (!slots.TryGetValue(name, out var slot))
            {
                slot = Names.Count;
                slots[name] = slot;
                Names.Add(name);
            }

            // Registered before patching: the patch can run as soon as it is in.
            Index[handle] = slot;
            _ticks = new long[Names.Count];
            _calls = new int[Names.Count];

            try
            {
                _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
            }
            catch (Exception e)
            {
                Index.Remove(handle);
                FailedCount++;
                Debug.LogWarning($"[NOVR] Frame timing: could not time {name}: {e.Message}");
            }
        }

        ResetInterval();
    }

    public static void Stop()
    {
        if (_harmony == null) return;
        _harmony.UnpatchSelf();
        _harmony = null;
    }

    public static void ResetInterval()
    {
        Array.Clear(_ticks, 0, _ticks.Length);
        Array.Clear(_calls, 0, _calls.Length);
    }

    /// <summary>Total ms per frame, calls per frame, and the top entries.</summary>
    public static void Append(StringBuilder sb, int frames, int count)
    {
        if (frames <= 0) return;

        double total = 0;
        long calls = 0;
        for (var i = 0; i < _ticks.Length; i++)
        {
            total += _ticks[i] * MsPerTick;
            calls += _calls[i];
        }

        sb.Append(F(total / frames)).Append(" ms/frame over ")
            .Append((calls / (double)frames).ToString("0", CultureInfo.InvariantCulture)).Append(" calls/frame");

        var order = Enumerable.Range(0, _ticks.Length).OrderByDescending(i => _ticks[i]).Take(count);
        var first = true;
        foreach (var i in order)
        {
            var avg = _ticks[i] * MsPerTick / frames;
            if (avg < 0.05) break;
            sb.Append(first ? "; top: " : ", ").Append(Names[i]).Append(' ').Append(F(avg))
                .Append(" (").Append((_calls[i] / (double)frames).ToString("0.#", CultureInfo.InvariantCulture))
                .Append("x)");
            first = false;
        }
    }

    private static void Prefix()
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;
        if (_depth++ == 0) _outerStart = Stopwatch.GetTimestamp();
    }

    // A finalizer, not a postfix, so a method that throws still closes its
    // timing; returning void lets the exception carry on as before.
    private static void Finalizer(MethodBase __originalMethod)
    {
        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) return;
        if (--_depth != 0)
        {
            if (_depth < 0) _depth = 0;
            return;
        }

        if (__originalMethod != null && Index.TryGetValue(__originalMethod.MethodHandle.Value, out var slot))
        {
            _ticks[slot] += Stopwatch.GetTimestamp() - _outerStart;
            _calls[slot]++;
        }
    }

    private static void CollectMessageMethods(List<(MethodBase, string)> targets)
    {
        Type[] types;
        try
        {
            types = typeof(NovrCodeProfiler).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).ToArray()!;
        }

        foreach (var type in types)
        {
            if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.ContainsGenericParameters) continue;
            if (type == typeof(FrameTimingLog)) continue;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(flags))
            {
                if (!MessageNames.Contains(method.Name) || method.IsAbstract) continue;
                if (method.GetParameters().Length != 0 || method.GetMethodBody() == null) continue;
                targets.Add((method, type.Name + "." + method.Name));
            }
        }
    }

    private static void CollectPatchMethods(List<(MethodBase, string)> targets)
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToList())
        {
            var info = Harmony.GetPatchInfo(original);
            if (info == null) continue;

            foreach (var patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
            {
                if (patch.owner != NOVRPlugin.HarmonyId) continue;
                var method = patch.PatchMethod;
                if (method.IsGenericMethodDefinition || method.DeclaringType?.ContainsGenericParameters == true)
                    continue;
                targets.Add((method, "patch " + method.DeclaringType?.Name + "." + method.Name));
            }
        }
    }

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Times, on the main thread, each render pipeline context (one per
/// <c>RenderPipeline.Render</c> call: the frame's cameras, plus any camera a
/// script renders by hand) and each camera within it. Times are exclusive:
/// an overlay camera rendered inside a base camera is not also counted in
/// the base camera. This is the CPU cost of culling and recording each
/// camera, not its GPU cost.
/// </summary>
internal sealed class CameraRenderTimer
{
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    private const int OverheadSlot = 0;

    private readonly Dictionary<Camera, int> _slots = new();
    private readonly List<string> _names = new() { "(between cameras)" };
    private readonly List<long> _ticks = new() { 0 };
    // A List, not a Stack: Stack<T> is ambiguous between this game's mscorlib and System.
    private readonly List<int> _stack = new();
    private long _last;
    private long _contextStart;
    private long _contextTicks;
    private int _cameraRenders;
    private bool _active;

    public void Start()
    {
        if (_active) return;
        RenderPipelineManager.beginContextRendering += OnBeginContext;
        RenderPipelineManager.endContextRendering += OnEndContext;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
        _active = true;
        ResetInterval();
    }

    public void Stop()
    {
        if (!_active) return;
        RenderPipelineManager.beginContextRendering -= OnBeginContext;
        RenderPipelineManager.endContextRendering -= OnEndContext;
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;
        _active = false;
    }

    public void ResetInterval()
    {
        // Forget cameras each interval so destroyed ones do not pile up.
        _slots.Clear();
        _names.RemoveRange(1, _names.Count - 1);
        _ticks.Clear();
        _ticks.Add(0);
        _contextTicks = 0;
        _cameraRenders = 0;
    }

    public void Append(StringBuilder sb, int frames, int count)
    {
        if (frames <= 0) return;
        sb.Append(F(_contextTicks * MsPerTick / frames)).Append(" ms/frame, ")
            .Append((_cameraRenders / (double)frames).ToString("0.#", CultureInfo.InvariantCulture))
            .Append(" camera renders/frame");

        var order = Enumerable.Range(0, _ticks.Count).OrderByDescending(i => _ticks[i]).Take(count);
        var first = true;
        foreach (var i in order)
        {
            var avg = _ticks[i] * MsPerTick / frames;
            if (avg < 0.05) break;
            sb.Append(first ? "; top: " : ", ").Append(_names[i]).Append(' ').Append(F(avg));
            first = false;
        }
    }

    private void OnBeginContext(ScriptableRenderContext context, List<Camera> cameras)
    {
        _contextStart = _last = Stopwatch.GetTimestamp();
        _stack.Clear();
        _stack.Add(OverheadSlot);
    }

    private void OnEndContext(ScriptableRenderContext context, List<Camera> cameras)
    {
        var now = Charge();
        _contextTicks += now - _contextStart;
        _stack.Clear();
    }

    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        Charge();
        if (!_slots.TryGetValue(camera, out var slot))
        {
            slot = _names.Count;
            _slots[camera] = slot;
            _names.Add(camera.name);
            _ticks.Add(0);
        }

        _stack.Add(slot);
        _cameraRenders++;
    }

    private void OnEndCamera(ScriptableRenderContext context, Camera camera)
    {
        Charge();
        if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1);
    }

    /// <summary>Charges the time since the last event to the innermost open slot.</summary>
    private long Charge()
    {
        var now = Stopwatch.GetTimestamp();
        if (_stack.Count > 0) _ticks[_stack[_stack.Count - 1]] += now - _last;
        _last = now;
        return now;
    }

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
