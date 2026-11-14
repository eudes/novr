using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace NOVR;

/// <summary>
/// Settings for <see cref="FrameTimingLog"/>. They live here rather than in
/// ModConfiguration for the reason given on <see cref="ConfigSectionAttribute"/>.
/// </summary>
[ConfigSection(Order = 90)]
public static class FrameTimingLogConfig
{
    private const string Section = "Diagnostics";

    public static ConfigEntry<bool>? EnabledEntry;
    public static ConfigEntry<float>? IntervalEntry;
    public static ConfigEntry<bool>? DetailEntry;

    public static bool Enabled => EnabledEntry?.Value ?? true;

    public static bool Detail => DetailEntry?.Value ?? false;

    public static float IntervalSeconds =>
        IntervalEntry != null ? Mathf.Clamp(IntervalEntry.Value, 2f, 120f) : 10f;

    public static void Bind(ConfigFile config)
    {
        EnabledEntry = config.Bind(
            Section,
            "Frame Timing Log",
            true,
            "Write frame timing to the game's Player.log: a summary line every few seconds "
            + "(frame rate, frame times against the headset's refresh budget, garbage "
            + "collections), a line for each big hitch, and a session summary on quit. Lines "
            + "start with [NOVR] Frame timing / [NOVR] Hitch. Costs next to nothing. Takes "
            + "effect immediately.");

        IntervalEntry = config.Bind(
            Section,
            "Frame Timing Interval",
            10f,
            new ConfigDescription(
                "Seconds between frame timing summary lines.",
                new AcceptableValueRange<float>(2f, 120f)));

        DetailEntry = config.Bind(
            Section,
            "Frame Timing Detail",
            false,
            "Add a detail line to each frame timing summary: the main thread's most expensive "
            + "frame loop steps, NOVR's own code (its per-frame methods and its patches on game "
            + "code), and the CPU time of each camera render. Costs a little frame time itself, "
            + "so leave it off unless investigating performance. Takes effect immediately.");
    }
}

/// <summary>
/// Logs how well the game keeps up with the headset, so a "the game felt
/// jerky" report can be answered from Player.log instead of guessed at.
///
/// <para>Frame time is <see cref="Time.unscaledDeltaTime"/>: in XR the frame
/// loop is paced by the runtime, so this is the real interval between the
/// frames the app submitted. It is compared with the headset's refresh
/// interval (the "budget"). A frame over 1.5x budget missed at least one
/// refresh, which the runtime covers with reprojection; a steady run of those
/// reads as judder, a single long frame as a hitch.</para>
///
/// <para>Where Unity or the XR runtime provide them, the summary also splits
/// the time into CPU main thread, CPU render thread and GPU, which says
/// whether a slow frame was CPU or GPU bound. Each source is probed once at
/// start-up and the log says which ones are available, so a missing column is
/// explained rather than silently absent.</para>
///
/// <para>Per frame this allocates nothing: samples go into a preallocated
/// list and strings are only built when a line is written.</para>
/// </summary>
public class FrameTimingLog : MonoBehaviour
{
    // A frame this many budgets long is logged on its own line.
    private const float HitchBudgets = 4f;

    // ...but never below this, so a 120 Hz headset does not report every
    // 35 ms frame as a hitch.
    private const float HitchMinimumMs = 50f;

    // At most one hitch line per this many seconds; the rest are counted.
    private const float HitchLineInterval = 1f;

    private static readonly float[] HistogramBudgets = { 1.1f, 1.5f, 2.5f, 4f };

    /// <summary>
    /// True when the VR mod is disabled and this log runs on its own, so a
    /// flat-screen run of the same mission can be compared with a VR one.
    /// Headset budget figures are then left out.
    /// </summary>
    public static bool Flat { get; private set; }

    /// <summary>
    /// Starts the log without the rest of NOVR, for flat-screen runs, on
    /// <paramref name="host"/> (the plugin's own BepInEx manager object). It
    /// patches nothing and touches no game objects, so the game stays
    /// unmodified apart from the timing itself.
    ///
    /// <para>In a flat run of this game, objects made while plugins load stop
    /// getting Updates (no error is logged): a DontDestroyOnLoad object made
    /// the way <see cref="Core"/> starts never ran, and neither did the
    /// BepInEx manager object, so the F1 Configuration Manager was dead too.
    /// <c>HideManagerGameObject = true</c> in BepInEx.cfg keeps the manager
    /// object running, so this rides on it. Without that setting a flat run
    /// logs nothing. Why VR runs are unaffected is not known.</para>
    /// </summary>
    internal static void AttachFlat(GameObject host)
    {
        Flat = true;
        host.AddComponent<FrameTimingLog>();
        Debug.Log("[NOVR] Frame timing log attached for a flat-screen run (VR mod disabled).");
    }

    private readonly List<float> _frameMs = new(2048);
    private readonly FrameTiming[] _timing = new FrameTiming[1];
    private readonly List<XRDisplaySubsystem> _displays = new();

    private float _intervalStart;
    private int _intervalGcCount;
    private int _intervalHitches;
    private int _suppressedHitchLines;
    private float _lastHitchLine = float.NegativeInfinity;
    private int _lastGcCount;
    private bool _probed;

    private bool _frameTimingAvailable;
    private double _cpuMainSum, _cpuRenderSum, _gpuSum, _presentWaitSum;
    private double _cpuMainMax, _cpuRenderMax, _gpuMax, _presentWaitMax;

    private readonly PlayerLoopTimer _loopTimer = new();
    private readonly CameraRenderTimer _cameraTimer = new();
    private bool _detailActive;
    private int _frameTimingSamples;

    private XRDisplaySubsystem? _display;
    private bool _xrGpuAvailable;
    private double _xrGpuSum, _xrGpuMax;
    private int _xrGpuSamples;
    private int? _droppedAtIntervalStart;

    private ProfilerRecorder _gcAllocRecorder;
    private long _gcAllocBytes;

    // Session totals, for the summary written on quit.
    private int _sessionFrames;
    private double _sessionSeconds;
    private int _sessionHitches;
    private int _sessionGc;
    private readonly int[] _sessionHistogram = new int[HistogramBudgets.Length + 1];
    private float _sessionBudgetMs;

    private void OnEnable()
    {
        _gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        ResetInterval();
    }

    private void OnDisable()
    {
        _gcAllocRecorder.Dispose();
        StopTimers();
    }

    /// <summary>
    /// Puts the player loop markers in, and the detail timers in or out, to
    /// match the settings. Changing the player loop mid-interval would mix
    /// two layouts in one summary, so a change also restarts the interval.
    /// </summary>
    private void SyncTimers()
    {
        var detail = FrameTimingLogConfig.Detail;
        if (_loopTimer.Installed && _loopTimer.Detailed == detail && _detailActive == detail) return;

        try
        {
            _loopTimer.Install(detail);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR] Frame timing: could not time the player loop: {e.Message}");
        }

        if (detail && !_detailActive)
        {
            _cameraTimer.Start();
            try
            {
                NovrCodeProfiler.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NOVR] Frame timing: could not time NOVR code: {e.Message}");
            }

            Debug.Log($"[NOVR] Frame timing detail on: timing {NovrCodeProfiler.PatchedCount} NOVR methods"
                      + (NovrCodeProfiler.FailedCount > 0 ? $" ({NovrCodeProfiler.FailedCount} could not be timed)" : "")
                      + ", each camera render and each frame loop step.");
        }
        else if (!detail && _detailActive)
        {
            _cameraTimer.Stop();
            NovrCodeProfiler.Stop();
            Debug.Log("[NOVR] Frame timing detail off.");
        }

        _detailActive = detail;
        ResetInterval();
    }

    private void StopTimers()
    {
        _loopTimer.Uninstall();
        if (!_detailActive) return;
        _cameraTimer.Stop();
        NovrCodeProfiler.Stop();
        _detailActive = false;
    }

    private void Update()
    {
        if (!FrameTimingLogConfig.Enabled)
        {
            if (_loopTimer.Installed || _detailActive) StopTimers();
            if (_frameMs.Count > 0) ResetInterval();
            return;
        }

        if (!_probed) Probe();
        SyncTimers();

        var frameMs = Time.unscaledDeltaTime * 1000f;
        _frameMs.Add(frameMs);

        var gcCount = GC.CollectionCount(0);
        var gcThisFrame = gcCount != _lastGcCount;
        if (gcThisFrame) _intervalGcCount += gcCount - _lastGcCount;
        _lastGcCount = gcCount;

        if (_gcAllocRecorder.Valid) _gcAllocBytes += _gcAllocRecorder.LastValue;

        float cpuMain = -1f, cpuRender = -1f, gpu = -1f;
        if (_frameTimingAvailable)
        {
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
            {
                cpuMain = (float)_timing[0].cpuMainThreadFrameTime;
                cpuRender = (float)_timing[0].cpuRenderThreadFrameTime;
                gpu = (float)_timing[0].gpuFrameTime;
                Accumulate(_timing[0].cpuMainThreadPresentWaitTime, ref _presentWaitSum, ref _presentWaitMax);
                Accumulate(cpuMain, ref _cpuMainSum, ref _cpuMainMax);
                Accumulate(cpuRender, ref _cpuRenderSum, ref _cpuRenderMax);
                Accumulate(gpu, ref _gpuSum, ref _gpuMax);
                _frameTimingSamples++;
            }
        }

        if (_xrGpuAvailable && _display != null && _display.TryGetAppGPUTimeLastFrame(out var xrGpu))
        {
            var xrGpuMs = XrSecondsOrMs(xrGpu);
            Accumulate(xrGpuMs, ref _xrGpuSum, ref _xrGpuMax);
            _xrGpuSamples++;
        }

        var budgetMs = BudgetMs();
        if (frameMs > Mathf.Max(HitchMinimumMs, HitchBudgets * budgetMs))
        {
            _intervalHitches++;
            LogHitch(frameMs, budgetMs, gcThisFrame, cpuMain, cpuRender, gpu);
        }

        if (Time.unscaledTime - _intervalStart >= FrameTimingLogConfig.IntervalSeconds)
        {
            WriteSummary(budgetMs);
            ResetInterval();
        }
    }

    private void OnApplicationQuit()
    {
        if (!FrameTimingLogConfig.Enabled) return;
        if (_frameMs.Count > 0) WriteSummary(BudgetMs());
        if (_sessionFrames == 0) return;

        var sb = new StringBuilder();
        sb.Append("[NOVR] Frame timing session: ")
            .Append(_sessionFrames).Append(" frames in ").Append(F(_sessionSeconds, "0")).Append(" s = ")
            .Append(F(_sessionFrames / Math.Max(_sessionSeconds, 0.001), "0.0")).Append(" fps avg");

        if (!Flat)
        {
            sb.Append(", budget ").Append(F(_sessionBudgetMs, "0.0")).Append(" ms; frames by budget multiple:");
        }

        for (var i = 0; i < _sessionHistogram.Length && !Flat; i++)
        {
            var label = i < HistogramBudgets.Length
                ? "<=" + F(HistogramBudgets[i], "0.#") + "x"
                : ">" + F(HistogramBudgets[HistogramBudgets.Length - 1], "0.#") + "x";
            sb.Append(' ').Append(label).Append(' ')
                .Append(F(100.0 * _sessionHistogram[i] / _sessionFrames, "0.0")).Append('%');
        }

        sb.Append("; hitches ").Append(_sessionHitches).Append(", GC ").Append(_sessionGc);
        Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Finds out once which timing sources this game build and runtime
    /// actually provide, and says so in the log.
    /// </summary>
    private void Probe()
    {
        _probed = true;
        _lastGcCount = GC.CollectionCount(0);

        try
        {
            _frameTimingAvailable = FrameTimingManager.IsFeatureEnabled();
        }
        catch (Exception)
        {
            _frameTimingAvailable = false;
        }

        FindDisplay();
        _xrGpuAvailable = _display != null && _display.TryGetAppGPUTimeLastFrame(out _);

        Debug.Log("[NOVR] Frame timing log on (Diagnostics > Frame Timing Log). Sources: "
                  + "frame interval yes; "
                  + "Unity CPU/GPU split " + (_frameTimingAvailable ? "yes" : "no (Frame Timing Stats is off in this game build)") + "; "
                  + "XR runtime GPU time " + (_xrGpuAvailable ? "yes" : "no") + "; "
                  + "XR dropped frames " + (_display != null && _display.TryGetDroppedFrameCount(out _) ? "yes" : "no") + "; "
                  + "GC allocation per frame " + (_gcAllocRecorder.Valid ? "yes" : "no") + "; "
                  + (Flat
                      ? "flat screen (VR mod disabled), vSyncCount " + QualitySettings.vSyncCount
                        + ", targetFrameRate " + Application.targetFrameRate
                        + ", monitor " + F(Screen.currentResolution.refreshRateRatio.value, "0.#") + " Hz."
                      : "headset refresh " + F(RefreshRate(), "0.#") + " Hz."));
    }

    private void FindDisplay()
    {
        _displays.Clear();
        SubsystemManager.GetSubsystems(_displays);
        _display = null;
        foreach (var display in _displays)
        {
            if (display.running)
            {
                _display = display;
                break;
            }
        }
    }

    private float RefreshRate()
    {
        if (_display == null || !_display.running) FindDisplay();
        if (_display != null && _display.TryGetDisplayRefreshRate(out var hz) && hz > 1f) return hz;
        return 0f;
    }

    /// <summary>
    /// The headset's refresh interval. SteamVR's OpenXR runtime does not
    /// report one, so it is then inferred from the frames: the fastest
    /// frames the app ever manages (the 5th percentile of the quickest
    /// interval so far, typically in a menu) sit on the refresh interval, and
    /// are snapped to the nearest common headset rate. Until an interval has
    /// been measured it is a 90 Hz guess. Either way the log marks it with "~".
    /// </summary>
    private float BudgetMs()
    {
        // Flat there is no headset slot to miss; this only sets the hitch threshold.
        if (Flat) return 1000f / 60f;

        var hz = RefreshRate();
        if (hz > 0f) return 1000f / hz;
        return _inferredBudgetMs > 0f ? _inferredBudgetMs : 1000f / 90f;
    }

    private static readonly float[] CommonRefreshRates = { 60f, 72f, 80f, 90f, 96f, 120f, 144f };
    private float _fastestP5Ms = float.MaxValue;
    private float _inferredBudgetMs;

    /// <summary><see cref="_frameMs"/> must be sorted.</summary>
    private void InferBudget()
    {
        if (_frameMs.Count < 100) return;

        var p5 = Percentile(0.05f);
        if (p5 >= _fastestP5Ms) return;
        _fastestP5Ms = p5;

        var best = 0f;
        var bestError = float.MaxValue;
        foreach (var rate in CommonRefreshRates)
        {
            var error = Mathf.Abs(1000f / rate - p5);
            if (error < bestError)
            {
                bestError = error;
                best = rate;
            }
        }

        _inferredBudgetMs = 1000f / best;
    }

    private void LogHitch(float frameMs, float budgetMs, bool gc, float cpuMain, float cpuRender, float gpu)
    {
        var now = Time.unscaledTime;
        if (now - _lastHitchLine < HitchLineInterval)
        {
            _suppressedHitchLines++;
            return;
        }

        _lastHitchLine = now;

        var sb = new StringBuilder();
        sb.Append("[NOVR] Hitch: ").Append(F(frameMs, "0.0")).Append(" ms frame (")
            .Append(F(frameMs / budgetMs, "0.0")).Append("x budget) at ").Append(F(Time.realtimeSinceStartup, "0.0"))
            .Append(" s, scene ").Append(SceneManager.GetActiveScene().name)
            .Append(", GC this frame ").Append(gc ? "yes" : "no");

        if (cpuMain >= 0f)
        {
            sb.Append(", cpu main ").Append(F(cpuMain, "0.0"))
                .Append(" / render ").Append(F(cpuRender, "0.0"))
                .Append(" / gpu ").Append(F(gpu, "0.0")).Append(" ms");
        }

        if (_suppressedHitchLines > 0)
        {
            sb.Append(" (+").Append(_suppressedHitchLines).Append(" more since the last hitch line)");
            _suppressedHitchLines = 0;
        }

        Debug.Log(sb.ToString());
    }

    private void WriteSummary(float budgetMs)
    {
        var count = _frameMs.Count;
        if (count == 0) return;

        var seconds = Time.unscaledTime - _intervalStart;

        _frameMs.Sort();
        if (!Flat && RefreshRate() <= 0f)
        {
            InferBudget();
            budgetMs = BudgetMs();
        }

        double sum = 0;
        var overBudget = 0;
        foreach (var ms in _frameMs)
        {
            sum += ms;
            if (ms > 1.5f * budgetMs) overBudget++;

            var bucket = 0;
            while (bucket < HistogramBudgets.Length && ms > HistogramBudgets[bucket] * budgetMs) bucket++;
            _sessionHistogram[bucket]++;
        }

        var hz = RefreshRate();
        var shownHz = hz > 0f ? hz : 1000f / budgetMs;

        var sb = new StringBuilder();
        sb.Append("[NOVR] Frame timing ").Append(F(seconds, "0.0")).Append(" s, scene ")
            .Append(SceneManager.GetActiveScene().name);

        if (Flat)
        {
            sb.Append(", flat screen: ");
        }
        else
        {
            sb.Append(", budget ")
                .Append(hz > 0f ? "" : "~").Append(F(budgetMs, "0.0")).Append(" ms")
                .Append(" (").Append(hz > 0f ? "" : "~").Append(F(shownHz, "0.#")).Append(" Hz)").Append(": ");
        }

        sb.Append(count).Append(" frames = ").Append(F(count / Math.Max(seconds, 0.001f), "0.0")).Append(" fps")
            .Append(" | frame ms avg ").Append(F(sum / count, "0.0"))
            .Append(" p50 ").Append(F(Percentile(0.50f), "0.0"))
            .Append(" p95 ").Append(F(Percentile(0.95f), "0.0"))
            .Append(" p99 ").Append(F(Percentile(0.99f), "0.0"))
            .Append(" max ").Append(F(_frameMs[count - 1], "0.0"));

        if (!Flat)
        {
            sb.Append(" | missed refresh ").Append(overBudget).Append(" (")
                .Append(F(100.0 * overBudget / count, "0")).Append("%)");
        }

        sb.Append(" | hitches ").Append(_intervalHitches)
            .Append(" | GC ").Append(_intervalGcCount);

        if (_gcAllocRecorder.Valid)
        {
            sb.Append(" (").Append(F(_gcAllocBytes / 1024.0 / Math.Max(seconds, 0.001f), "0")).Append(" KB/s alloc)");
        }

        if (_frameTimingSamples > 0)
        {
            sb.Append(" | cpu main avg ").Append(F(_cpuMainSum / _frameTimingSamples, "0.0"))
                .Append(" max ").Append(F(_cpuMainMax, "0.0"))
                .Append(", render avg ").Append(F(_cpuRenderSum / _frameTimingSamples, "0.0"))
                .Append(" max ").Append(F(_cpuRenderMax, "0.0"))
                .Append(", gpu avg ").Append(F(_gpuSum / _frameTimingSamples, "0.0"))
                .Append(" max ").Append(F(_gpuMax, "0.0")).Append(" ms")
                .Append(", main present wait avg ").Append(F(_presentWaitSum / _frameTimingSamples, "0.0"))
                .Append(" max ").Append(F(_presentWaitMax, "0.0")).Append(" ms");
        }

        if (_xrGpuSamples > 0)
        {
            sb.Append(" | xr gpu avg ").Append(F(_xrGpuSum / _xrGpuSamples, "0.0"))
                .Append(" max ").Append(F(_xrGpuMax, "0.0")).Append(" ms");
        }

        if (_display != null && _display.TryGetDroppedFrameCount(out var dropped))
        {
            sb.Append(" | xr dropped ").Append(dropped);
            if (_droppedAtIntervalStart is { } start && dropped >= start)
            {
                sb.Append(" (+").Append(dropped - start).Append(')');
            }
        }

        Debug.Log(sb.ToString());
        WriteMainThreadLines();

        _sessionFrames += count;
        _sessionSeconds += seconds;
        _sessionHitches += _intervalHitches;
        _sessionGc += _intervalGcCount;
        _sessionBudgetMs = budgetMs;
    }

    /// <summary>
    /// Where the main thread's frame went, from the player loop markers:
    /// per phase, and with detail on, the top steps, the camera renders and
    /// NOVR's own code. Figures are avg/max ms per frame. The wait for the
    /// XR runtime's frame slot shows up as one phase or step taking up most
    /// of whatever the frame does not spend working.
    /// </summary>
    private void WriteMainThreadLines()
    {
        var frames = _loopTimer.Frames;
        if (!_loopTimer.Installed || frames == 0) return;

        var sb = new StringBuilder();
        sb.Append("[NOVR] Frame timing main thread, ms avg/max per frame over ").Append(frames)
            .Append(" frames: ");
        _loopTimer.AppendPhases(sb);
        Debug.Log(sb.ToString());

        if (!_detailActive || !_loopTimer.Detailed) return;

        sb.Clear();
        sb.Append("[NOVR] Frame timing detail: top steps ");
        _loopTimer.AppendTopSteps(sb, 10);
        sb.Append(" | camera renders (main thread) ");
        _cameraTimer.Append(sb, frames, 8);
        if (NovrCodeProfiler.Active)
        {
            sb.Append(" | NOVR code ");
            NovrCodeProfiler.Append(sb, frames, 10);
        }

        Debug.Log(sb.ToString());
    }

    private void ResetInterval()
    {
        _loopTimer.ResetInterval();
        _cameraTimer.ResetInterval();
        NovrCodeProfiler.ResetInterval();
        _presentWaitSum = _presentWaitMax = 0;
        _frameMs.Clear();
        _intervalStart = Time.unscaledTime;
        _intervalGcCount = 0;
        _intervalHitches = 0;
        _gcAllocBytes = 0;
        _cpuMainSum = _cpuRenderSum = _gpuSum = 0;
        _cpuMainMax = _cpuRenderMax = _gpuMax = 0;
        _frameTimingSamples = 0;
        _xrGpuSum = _xrGpuMax = 0;
        _xrGpuSamples = 0;
        _droppedAtIntervalStart = _display != null && _display.TryGetDroppedFrameCount(out var dropped)
            ? dropped
            : null;
    }

    /// <summary><see cref="_frameMs"/> must be sorted.</summary>
    private float Percentile(float p)
    {
        var index = Mathf.Clamp(Mathf.CeilToInt(p * _frameMs.Count) - 1, 0, _frameMs.Count - 1);
        return _frameMs[index];
    }

    private static void Accumulate(double value, ref double sum, ref double max)
    {
        sum += value;
        if (value > max) max = value;
    }

    /// <summary>
    /// Unity does not pin down the unit XR providers report GPU time in, and
    /// they differ. A frame's GPU time is far below a second and far above a
    /// millisecond, so a value under 1 can only be seconds.
    /// </summary>
    private static float XrSecondsOrMs(float value) => value < 1f ? value * 1000f : value;

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
