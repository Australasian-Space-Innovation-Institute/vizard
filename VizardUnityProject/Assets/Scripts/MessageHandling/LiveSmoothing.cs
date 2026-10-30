/*
 Live playback (Scarlet Alpha demo app): a live run watched like a live video
 stream. Class name kept from its first version (live smoothing only).

 The simulation streams one frame per sim step (1 s of sim time in the demo).
 This class decides which moment of the run is drawn, for the whole of a live
 run (Vizard's own playback code is bypassed while it is active):

 - LIVE (MessageList.FollowLive): drawn from a buffer, BufferWallSeconds of
   wall time behind the newest frame (at least 1.5 frames), at a smoothly
   advancing time. The buffer absorbs late or bunched frames: running low
   plays slower rather than stopping dead, building up plays faster, and an
   overdue frame (the simulation slowed or paused) slows it to match.
 - REPLAY (scrubbed back, or paused and resumed): the past plays at the
   simulation's set speed -- told by the app with PLAYBACK_RATE, otherwise
   the measured stream rate -- times Vizard's own speed buttons. The stream
   keeps arriving meanwhile. Replay that reaches the live buffer goes live.
 - Go live: the app's PLAYBACK_LIVE request, or the time slider dragged to
   its end. PLAYBACK_SEEK <sim s> replays from a sim time, as a scrub would.
 - Vizard's pause: freezes the view only (the stream keeps arriving); play
   continues from there, behind live.
 - Vizard's speed buttons: replay speed. Slowing down while live drops behind.

 In every case the drawn moment is blended from the two frames either side
 of it: positions and velocities lerped, attitudes slerped. Only what is
 drawn changes: the frames are kept as received. Files (not live) play as
 stock Vizard.

 Logs `view` (state=live|replay) on each change and a `live_smoothing`
 summary every LogEverySeconds.

 Off with the command-line argument -noLiveSmoothing.

 Frame rate: smoothing needs Vizard to draw often. With vsync on, an X11
 window the compositor does not see as on screen -- an embedded one, under
 XWayland -- is held to about one frame per second. So vsync is off and the
 frame rate capped at TargetFps (50, the fixed update rate) instead.
*/

using System;
using System.Globalization;
using UnityEngine;
using VizProtobufferMessage;

public static class LiveSmoothing
{
    public static readonly bool Enabled =
        Array.IndexOf(Environment.GetCommandLineArgs(), "-noLiveSmoothing") < 0;

    /// <summary>Driving playback: a live run, smoothing not switched off.</summary>
    public static bool Active => Enabled && DataManager.IsLiveSim;

    // The scene moves in FixedUpdate (Fixed Timestep 0.02 s, 50 Hz): drawing at
    // the same rate shows every movement once.
    private const int TargetFps = 50;
    // The live buffer: how far behind the newest frame the drawn time is kept.
    private const double BufferWallSeconds = 1.0;
    private const double MinBufferFrames = 1.5;
    // How strongly the drawn time's speed follows the buffer level: at 0.5,
    // a buffer half empty plays at 75% speed, a buffer 50% over at 125%.
    private const double BufferGain = 0.5;
    private const double MaxLagTargets = 4.0;      // live and further behind than this many buffers: jump
    private const double RateWindowSeconds = 2.0;
    private const double LogEverySeconds = 5.0;

    // From the app, over the link (socket thread).
    private static double appRate = -1;            // the simulation's set speed; <0: not told
    private static volatile bool goLiveRequested;

    private static int lastLatest = -1;            // index of the newest frame last seen
    private static double lastLatestSim;           // its sim time [s]
    private static double lastArrivalWall;         // when it was first seen [s]
    private static double rate = -1;               // measured stream rate, sim s per wall s; <0: unknown
    private static readonly System.Collections.Generic.Queue<double[]> arrivals =
        new System.Collections.Generic.Queue<double[]>();
    private static double frameSim = -1;           // sim time between frames [s]; <0: unknown
    private static double displaySim = -1;         // the sim time being drawn [s]; <0: not started
    private static double lastApplyWall;
    private static bool scrubbing;
    private static string loggedView = "";
    private static double replayRate;              // for the readout
    // The blended frame is a copy of the earlier frame of the pair, made once
    // per pair and then updated in place.
    private static VizMessage blended, blendedFrom;

    // Diagnostics over each log window.
    private static double lastLogWall;
    private static int windowSnaps, windowSteps, windowStarved, windowFrames0, windowFixed;
    private static double windowMaxStep, windowMinLag = double.MaxValue, windowMaxLag, windowMaxGap, windowMaxUpdateMs;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void SetFrameRate()
    {
        if (Enabled)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = TargetFps;
        }
    }

    // ---- from the app (socket thread) ----------------------------------------

    /// <summary>PLAYBACK_RATE &lt;x&gt;: the simulation's set speed, for replay.</summary>
    public static bool SetRateFromRequest(string requestText)
    {
        int start = requestText.IndexOf("PLAYBACK_RATE", StringComparison.Ordinal);
        string value = start < 0 ? "" : requestText.Substring(start + "PLAYBACK_RATE".Length).Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed > 0)
        {
            appRate = parsed;
            return true;
        }
        return false;
    }

    private static double seekRequested = -1;      // PLAYBACK_SEEK: sim time to replay from; <0: none

    /// <summary>PLAYBACK_SEEK &lt;sim seconds&gt;: replay from there, as a scrub would.</summary>
    public static bool SeekFromRequest(string requestText)
    {
        int start = requestText.IndexOf("PLAYBACK_SEEK", StringComparison.Ordinal);
        string value = start < 0 ? "" : requestText.Substring(start + "PLAYBACK_SEEK".Length).Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && parsed >= 0)
        {
            seekRequested = parsed;
            return true;
        }
        return false;
    }

    /// <summary>PLAYBACK_LIVE: jump to the live edge.</summary>
    public static void RequestGoLive()
    {
        goLiveRequested = true;
    }

    // ---- from Vizard's controls (main thread) ---------------------------------

    /// <summary>The time slider is held at this frame.</summary>
    public static void ScrubTo(int index)
    {
        if (MessageList.TryGetMessage(Math.Max(0, index), out VizMessage message))
        {
            scrubbing = true;
            MessageList.FollowLive = false;
            displaySim = SimSeconds(message);
        }
    }

    /// <summary>The time slider was let go; at its end: go live.</summary>
    public static void EndScrub(bool atEnd)
    {
        scrubbing = false;
        if (atEnd)
        {
            GoLive("slider_end");
        }
    }

    /// <summary>Vizard's pause pressed while live: resuming continues from here.</summary>
    public static void PausedByViewer()
    {
        if (Active && MessageList.FollowLive)
        {
            MessageList.FollowLive = false;
        }
    }

    /// <summary>Vizard's speed readout while this drives playback.</summary>
    public static string Readout()
    {
        if (MessageList.PlaybackPaused)
        {
            return "PAUSED";
        }
        if (MessageList.FollowLive)
        {
            bool streamIdle = lastLatest >= 0 && Time.realtimeSinceStartupAsDouble - lastArrivalWall
                              > Math.Max(1.5, 2 * (rate > 0 && frameSim > 0 ? frameSim / rate : 1));
            return streamIdle ? "LIVE (paused)" : "LIVE";
        }
        double behind = Math.Max(0, lastLatestSim - displaySim);
        TimeSpan span = TimeSpan.FromSeconds(Math.Round(behind));
        string ago = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        return $"-{ago}  {FormatRate(replayRate)}";
    }

    /// <summary>Forget the run.</summary>
    public static void Reset()
    {
        lastLatest = -1;
        rate = frameSim = displaySim = -1;
        arrivals.Clear();
        blended = blendedFrom = null;
        scrubbing = false;
        goLiveRequested = false;
        MessageList.BlendedMessage = null;
    }

    // ---- each fixed update (main thread) ---------------------------------------

    /// <summary>
    /// Choose the moment to draw and blend the frames either side of it.
    /// viewerMultiplier: Vizard's own speed buttons (1 = the simulation's speed).
    /// </summary>
    public static void Apply(double viewerMultiplier)
    {
        int total = MessageList.TimestepsTotal;
        if (total - 1 < lastLatest)
        {
            Reset();                                // a new run
        }
        if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != TargetFps)
        {
            SetFrameRate();                         // a quality change in Vizard's menus resets these
        }
        if (total < 1)
        {
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        int latest = total - 1;
        if (!MessageList.TryGetMessage(latest, out VizMessage newest))
        {
            return;
        }
        double newestSim = SimSeconds(newest);
        TrackArrivals(latest, newestSim, now);

        windowFixed++;
        double dt = Math.Max(0, now - lastApplyWall);
        lastApplyWall = now;
        windowMaxUpdateMs = Math.Max(windowMaxUpdateMs, dt * 1000.0);

        if (goLiveRequested)
        {
            goLiveRequested = false;
            GoLive("requested");
        }
        if (seekRequested >= 0 && displaySim >= 0)
        {
            displaySim = Math.Min(seekRequested, newestSim);
            seekRequested = -1;
            MessageList.FollowLive = false;
            VizardCommandLog.Log("seek", $"sim_t={displaySim:F0}");
        }

        if (rate <= 0 || frameSim <= 0 || total < 3)
        {
            // Too early to know the pace: draw the newest frame as it is.
            MessageList.BlendedMessage = null;
            MessageList.CurrentIndex = latest;
            LogView(now, newestSim);
            return;
        }

        double lagSim = Math.Max(MinBufferFrames * frameSim, rate * BufferWallSeconds);
        // Where the stream should be by now, between arrivals: the newest frame
        // plus the time since it came, at most one frame on.
        double streamSim = lastLatestSim + Math.Min(frameSim, rate * (now - lastArrivalWall));
        double liveTarget = streamSim - lagSim;
        double previous = displaySim;

        if (displaySim < 0)
        {
            displaySim = liveTarget;                // starting: at the live edge
            MessageList.FollowLive = true;
        }
        else if (scrubbing || MessageList.PlaybackPaused)
        {
            // Held by the slider, or paused by the viewer: the stream keeps arriving.
        }
        else if (MessageList.FollowLive)
        {
            if (viewerMultiplier < 1)
            {
                MessageList.FollowLive = false;     // slowed down: drops behind live
            }
            else if (displaySim < liveTarget - MaxLagTargets * lagSim)
            {
                displaySim = liveTarget;            // fell far behind (a burst): jump
                windowSnaps++;
            }
            else
            {
                double fill = (streamSim - displaySim) / lagSim;      // 1: buffer at its level
                double speed = Math.Max(0, Math.Min(2, 1 + BufferGain * (fill - 1)));
                double since = now - lastArrivalWall;
                double playRate = rate;
                if (since > 1.5 * frameSim / rate)
                {
                    playRate = Math.Min(rate, frameSim / since);      // the next frame is overdue
                }
                displaySim += playRate * speed * dt;
                replayRate = rate;
            }
        }
        if (!MessageList.FollowLive && !scrubbing && !MessageList.PlaybackPaused)
        {
            replayRate = (appRate > 0 ? appRate : rate) * viewerMultiplier;
            displaySim += replayRate * dt;
            if (displaySim >= liveTarget && viewerMultiplier >= 1)
            {
                GoLive("caught_up");                // replay reached the live buffer
            }
        }

        if (displaySim >= newestSim)
        {
            displaySim = newestSim;
            if (!scrubbing && !MessageList.PlaybackPaused)
            {
                windowStarved++;
            }
        }
        if (previous >= 0 && dt > 0)
        {
            windowSteps++;
            windowMaxStep = Math.Max(windowMaxStep, Math.Abs(displaySim - previous));
        }
        if (MessageList.FollowLive)
        {
            windowMinLag = Math.Min(windowMinLag, newestSim - displaySim);
            windowMaxLag = Math.Max(windowMaxLag, newestSim - displaySim);
        }

        Show(latest, newest, newestSim);
        LogView(now, newestSim);
    }

    private static void GoLive(string reason)
    {
        bool wasLive = MessageList.FollowLive;
        MessageList.FollowLive = true;
        DataManager.DisplayMostRecentMessage = true;
        if (rate > 0 && frameSim > 0)
        {
            double lagSim = Math.Max(MinBufferFrames * frameSim, rate * BufferWallSeconds);
            displaySim = Math.Max(displaySim, lastLatestSim - lagSim);
        }
        if (!wasLive)
        {
            VizardCommandLog.Log("live_resumed", "reason=" + reason);
        }
    }

    private static void TrackArrivals(int latest, double newestSim, double now)
    {
        if (latest == lastLatest)
        {
            return;
        }
        if (lastLatest >= 0)
        {
            double dSim = newestSim - lastLatestSim;
            if (dSim > 0)
            {
                frameSim = dSim / (latest - lastLatest);
            }
            windowMaxGap = Math.Max(windowMaxGap, now - lastArrivalWall);
        }
        if (arrivals.Count > 0 && newestSim < arrivals.Peek()[1])
        {
            arrivals.Clear();                   // time went backwards: a new run
        }
        arrivals.Enqueue(new[] { now, newestSim });
        while (arrivals.Count > 3 && now - arrivals.Peek()[0] > RateWindowSeconds)
        {
            arrivals.Dequeue();
        }
        double[] oldest = arrivals.Peek();
        if (arrivals.Count >= 3 && now - oldest[0] > 0.05 && newestSim > oldest[1])
        {
            rate = (newestSim - oldest[1]) / (now - oldest[0]);
        }
        lastLatest = latest;
        lastLatestSim = newestSim;
        lastArrivalWall = now;
    }

    /// <summary>Draw displaySim: the frames either side of it, blended.</summary>
    private static void Show(int latest, VizMessage newest, double newestSim)
    {
        // Frames are evenly spaced in sim time: start from the estimate, then step.
        int index = latest;
        if (frameSim > 0)
        {
            index = latest - (int)Math.Floor((newestSim - displaySim) / frameSim);
            index = Math.Max(0, Math.Min(latest, index));
        }
        VizMessage at;
        while (index > 0 && MessageList.TryGetMessage(index, out at) && SimSeconds(at) > displaySim)
        {
            index--;
        }
        while (index < latest && MessageList.TryGetMessage(index + 1, out at) && SimSeconds(at) <= displaySim)
        {
            index++;
        }
        // index: the last frame at or before displaySim
        if (index < 1 || index >= latest
            || !MessageList.TryGetMessage(index, out VizMessage before)
            || !MessageList.TryGetMessage(index + 1, out VizMessage after))
        {
            MessageList.BlendedMessage = null;      // the setup frame, or the newest: as it is
            MessageList.CurrentIndex = Math.Max(0, Math.Min(latest, index));
            return;
        }
        double t0 = SimSeconds(before);
        double t1 = SimSeconds(after);
        double alpha = t1 > t0 ? Math.Max(0, Math.Min(1, (displaySim - t0) / (t1 - t0))) : 1;
        MessageList.CurrentIndex = index;
        MessageList.BlendedMessage = Blend(before, after, alpha, displaySim);
    }

    private static void LogView(double now, double newestSim)
    {
        string view = MessageList.FollowLive ? "live" : "replay";
        if (view != loggedView)
        {
            loggedView = view;
            double behind = displaySim < 0 ? 0 : Math.Max(0, newestSim - displaySim);
            VizardCommandLog.Log("view", $"state={view} behind_s={behind:F0}");
        }
        if (now - lastLogWall > LogEverySeconds)
        {
            lastLogWall = now;
            VizardCommandLog.Log("live_smoothing",
                $"view={view} lag_s={(windowMinLag == double.MaxValue ? 0 : windowMinLag):F2}..{windowMaxLag:F2} " +
                $"rate={rate:F1} app_rate={appRate:G4} frame_s={frameSim:F2} " +
                $"updates={windowSteps} biggest_step_s={windowMaxStep:F3} snaps={windowSnaps} " +
                $"starved={windowStarved} max_frame_gap_s={windowMaxGap:F2} max_update_gap_ms={windowMaxUpdateMs:F0} " +
                $"fixed_updates={windowFixed} rendered_frames={Time.frameCount - windowFrames0}");
            windowFrames0 = Time.frameCount;
            windowSnaps = windowSteps = windowFixed = windowStarved = 0;
            windowMaxStep = windowMaxLag = windowMaxGap = windowMaxUpdateMs = 0;
            windowMinLag = double.MaxValue;
        }
    }

    private static string FormatRate(double r)
    {
        if (r <= 0)
        {
            return "";
        }
        return r >= 100 ? r.ToString("F0", CultureInfo.InvariantCulture) + "x"
             : r >= 1 ? r.ToString("0.#", CultureInfo.InvariantCulture) + "x"
             : "1/" + Math.Round(1 / r).ToString(CultureInfo.InvariantCulture) + "x";
    }

    private static double SimSeconds(VizMessage message)
    {
        return message.CurrentTime.SimTimeElapsed / 1e9;
    }

    private static VizMessage Blend(VizMessage a, VizMessage b, double alpha, double simSeconds)
    {
        if (!ReferenceEquals(blendedFrom, a))
        {
            blended = a.Clone();
            blendedFrom = a;
        }
        blended.CurrentTime.SimTimeElapsed = simSeconds * 1e9;

        for (int k = 0; k < blended.Spacecraft.Count && k < a.Spacecraft.Count; k++)
        {
            VizMessage.Types.Spacecraft from = a.Spacecraft[k];
            VizMessage.Types.Spacecraft next = FindSpacecraft(b, from.SpacecraftName);
            if (next == null)
            {
                continue;
            }
            VizMessage.Types.Spacecraft sc = blended.Spacecraft[k];
            Lerp(sc.Position, from.Position, next.Position, alpha);
            Lerp(sc.Velocity, from.Velocity, next.Velocity, alpha);
            SlerpMrp(sc.Rotation, from.Rotation, next.Rotation, alpha);
        }

        for (int k = 0; k < blended.CelestialBodies.Count && k < a.CelestialBodies.Count; k++)
        {
            VizMessage.Types.CelestialBody from = a.CelestialBodies[k];
            VizMessage.Types.CelestialBody next = FindBody(b, from.BodyName);
            if (next == null)
            {
                continue;
            }
            VizMessage.Types.CelestialBody body = blended.CelestialBodies[k];
            Lerp(body.Position, from.Position, next.Position, alpha);
            Lerp(body.Velocity, from.Velocity, next.Velocity, alpha);
            Lerp(body.Rotation, from.Rotation, next.Rotation, alpha);   // a DCM over one frame: a tiny angle
        }
        return blended;
    }

    private static VizMessage.Types.Spacecraft FindSpacecraft(VizMessage message, string name)
    {
        foreach (VizMessage.Types.Spacecraft sc in message.Spacecraft)
        {
            if (sc.SpacecraftName == name)
            {
                return sc;
            }
        }
        return null;
    }

    private static VizMessage.Types.CelestialBody FindBody(VizMessage message, string name)
    {
        foreach (VizMessage.Types.CelestialBody body in message.CelestialBodies)
        {
            if (body.BodyName == name)
            {
                return body;
            }
        }
        return null;
    }

    private static void Lerp(Google.Protobuf.Collections.RepeatedField<double> into,
                             Google.Protobuf.Collections.RepeatedField<double> a,
                             Google.Protobuf.Collections.RepeatedField<double> b, double alpha)
    {
        if (a.Count != b.Count || into.Count != a.Count)
        {
            return;
        }
        for (int i = 0; i < a.Count; i++)
        {
            into[i] = a[i] + (b[i] - a[i]) * alpha;
        }
    }

    /// <summary>Attitude as MRPs (sigma_BN): slerp between the two as quaternions.</summary>
    private static void SlerpMrp(Google.Protobuf.Collections.RepeatedField<double> into,
                                 Google.Protobuf.Collections.RepeatedField<double> a,
                                 Google.Protobuf.Collections.RepeatedField<double> b, double alpha)
    {
        if (a.Count != 3 || b.Count != 3 || into.Count != 3)
        {
            return;
        }
        double[] qa = MrpToQuaternion(a[0], a[1], a[2]);
        double[] qb = MrpToQuaternion(b[0], b[1], b[2]);
        double dot = qa[0] * qb[0] + qa[1] * qb[1] + qa[2] * qb[2] + qa[3] * qb[3];
        if (dot < 0)
        {
            for (int i = 0; i < 4; i++) qb[i] = -qb[i];     // the short way round
            dot = -dot;
        }
        double[] q = new double[4];
        if (dot > 0.9995)
        {
            for (int i = 0; i < 4; i++) q[i] = qa[i] + (qb[i] - qa[i]) * alpha;
        }
        else
        {
            double theta = Math.Acos(dot);
            double sin = Math.Sin(theta);
            double wa = Math.Sin((1 - alpha) * theta) / sin;
            double wb = Math.Sin(alpha * theta) / sin;
            for (int i = 0; i < 4; i++) q[i] = wa * qa[i] + wb * qb[i];
        }
        double norm = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
        for (int i = 0; i < 4; i++) q[i] /= norm;
        if (q[0] < 0)
        {
            for (int i = 0; i < 4; i++) q[i] = -q[i];       // the MRP set with |sigma| <= 1
        }
        into[0] = q[1] / (1 + q[0]);
        into[1] = q[2] / (1 + q[0]);
        into[2] = q[3] / (1 + q[0]);
    }

    private static double[] MrpToQuaternion(double s1, double s2, double s3)
    {
        double s2sum = s1 * s1 + s2 * s2 + s3 * s3;
        double d = 1 + s2sum;
        return new[] { (1 - s2sum) / d, 2 * s1 / d, 2 * s2 / d, 2 * s3 / d };
    }
}
