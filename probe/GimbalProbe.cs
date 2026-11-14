// GimbalProbe: measures ModuleGimbal.GetPotentialTorque against the torque the
// gimbals actually produce, in an otherwise unmodded KSP 1.12.5.  Read-only: no
// Harmony, nothing patched.  It drives the game itself:
//   main menu  -> new sandbox save "gimbalprobe"
//   KSC        -> launch Ships/VAB/<craft> (default "Kerbal X") on the pad
//   flight     -> put the vessel in a 600 km circular Kerbin orbit (no aero, ~no
//                 gravity gradient), reaction wheels/SAS/RCS off, every engine lit
//                 at full throttle, then per axis steps the input 0,+1,0,-1,0
//                 (1.5 s each) and fits angular acceleration in the last 0.75 s.
// Measured torque = MOI[axis] * (alpha(step) - mean alpha(neighbouring zeros)).
// Credited torque = sum over ModuleGimbal of GetPotentialTorque, same window.
// Writes <KSP>/gimbalprobe.txt and [GimbalProbe] lines in KSP.log, then quits.
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GimbalProbe
{
    static class Cfg
    {
        public static string Craft = "Kerbal X";
        public static string Root => KSPUtil.ApplicationRootPath;
    }

    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class Menu : MonoBehaviour
    {
        int frames;
        void Update()
        {
            if (++frames != 120) return;
            Game g = GamePersistence.CreateNewGame("gimbalprobe", Game.Modes.SANDBOX,
                GameParameters.GetDefaultParameters(Game.Modes.SANDBOX, GameParameters.Preset.Normal),
                "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.VAB);
            HighLogic.SaveFolder = "gimbalprobe";
            HighLogic.CurrentGame = g;
            GamePersistence.SaveGame(g, "persistent", "gimbalprobe", SaveMode.OVERWRITE);
            Debug.Log("[GimbalProbe] new sandbox, starting");
            g.Start();
        }
    }

    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class Ksc : MonoBehaviour
    {
        int frames;
        void Update()
        {
            if (++frames != 120) return;
            string path = Path.Combine(Cfg.Root, "Ships/VAB/" + Cfg.Craft + ".craft");
            ConfigNode node = ConfigNode.Load(path);
            VesselCrewManifest m = VesselCrewManifest.FromConfigNode(node);
            m = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(node, m, true, true);
            Debug.Log("[GimbalProbe] launching " + path);
            FlightDriver.StartWithNewLaunch(path, "Squad/Flags/default", "LaunchPad", m);
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Probe : MonoBehaviour
    {
        const float Step = 1.5f, Fit = 0.75f;
        static readonly string[] Names = { "pitch", "roll", "yaw" };
        static readonly float[] Seq = { 0, 1, 0, -1, 0 };

        Vessel v;
        float t0 = -1;
        int stage;          // 0 settle on pad, 1 orbit set, 2 lit, 3 stepping, 4 done
        int axis, step;
        float stepStart;
        Vector3 input;
        readonly List<float> ts = new List<float>(), ws = new List<float>();
        readonly float[] alpha = new float[5];
        Vector3 credPos, credNeg; int credN;
        Vector3 moiSum; int moiN;
        readonly StringBuilder report = new StringBuilder();

        void Log(string s) { Debug.Log("[GimbalProbe] " + s); report.AppendLine(s); }

        void FixedUpdate()
        {
            if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null) return;
            v = FlightGlobals.ActiveVessel;
            float t = Time.time;
            if (t0 < 0) t0 = t;
            switch (stage)
            {
                case 0:
                    if (t - t0 < 4) return;
                    FlightGlobals.fetch.SetShipOrbit(1, 0, 1200000, 0, 0, 0, 0, 0);
                    stage = 1; t0 = t;
                    return;
                case 1:
                    if (t - t0 < 6 || v.packed) return;
                    foreach (Part p in v.parts)
                        foreach (PartModule pm in p.Modules)
                            if (pm is ModuleReactionWheel rw)
                            { rw.authorityLimiter = 0; rw.wheelState = ModuleReactionWheel.WheelState.Disabled; }
                    v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
                    v.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
                    v.OnFlyByWire += Drive;
                    foreach (Part p in v.parts)
                        foreach (PartModule pm in p.Modules)
                            if (pm is ModuleEngines e && p.partInfo.name != "liquidEngine2-2.v2") e.Activate();
                    stage = 2; t0 = t;
                    return;
                case 2:
                    if (t - t0 < 3) return;
                    float thrust = 0; int n = 0;
                    foreach (Part p in v.parts)
                        foreach (PartModule pm in p.Modules)
                            if (pm is ModuleEngines e && e.finalThrust > 0) { thrust += e.finalThrust; n++; }
                    Log(string.Format("KSP {0}, craft '{1}', {2} parts, mass {3:F2} t, {4} engines lit, thrust {5:F1} kN",
                        Versioning.VersionString, Cfg.Craft, v.parts.Count, v.GetTotalMass(), n, thrust));
                    foreach (Part p in v.parts)
                        foreach (PartModule pm in p.Modules)
                            if (pm is ModuleGimbal g)
                                Log(string.Format("  gimbal {0}: range {1}, responseSpeed {2} (used {3})",
                                    p.partInfo.name, g.gimbalRange, g.gimbalResponseSpeed, g.useGimbalResponseSpeed));
                    Log("axis   credited+   measured+   ratio+ | credited-   measured-   ratio-   (kN m)");
                    stage = 3; axis = 0; Begin(0, t);
                    return;
                case 3:
                    Sample(t);
                    return;
            }
        }

        void Begin(int s, float t)
        {
            step = s; stepStart = t; ts.Clear(); ws.Clear();
            input = Vector3.zero;
            input[axis] = Seq[s];
        }

        void Sample(float t)
        {
            float el = t - stepStart;
            if (el >= Step - Fit)
            {
                ts.Add(el);
                ws.Add(v.angularVelocity[axis]);
                Vector3 pos = Vector3.zero, neg = Vector3.zero;
                foreach (Part p in v.parts)
                    foreach (PartModule pm in p.Modules)
                        if (pm is ModuleGimbal g)
                        { g.GetPotentialTorque(out Vector3 a, out Vector3 b); pos += a; neg += b; }
                credPos += pos; credNeg += neg; credN++;
                moiSum += v.MOI; moiN++;
            }
            if (el < Step) return;
            alpha[step] = Slope(ts, ws);
            if (step < Seq.Length - 1) { Begin(step + 1, t); return; }
            float moi = moiSum[axis] / moiN;
            float mp = moi * (alpha[1] - 0.5f * (alpha[0] + alpha[2]));
            float mn = moi * (alpha[3] - 0.5f * (alpha[2] + alpha[4]));
            float cp = credPos[axis] / credN, cn = credNeg[axis] / credN;
            Log(string.Format("{0,-5} {1,10:F1} {2,11:F1} {3,8:F2} | {4,9:F1} {5,11:F1} {6,8:F2}",
                Names[axis], cp, Mathf.Abs(mp), cp / Mathf.Abs(mp), cn, Mathf.Abs(mn), cn / Mathf.Abs(mn)));
            credPos = credNeg = moiSum = Vector3.zero; credN = moiN = 0;
            if (++axis < 3) { Begin(0, t); return; }
            stage = 4;
            v.OnFlyByWire -= Drive;
            File.WriteAllText(Path.Combine(Cfg.Root, "gimbalprobe.txt"), report.ToString());
            Log("done");
            Application.Quit();
        }

        static float Slope(List<float> x, List<float> y)
        {
            float mx = 0, my = 0; int n = x.Count;
            for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
            mx /= n; my /= n;
            float sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            return sxy / sxx;
        }

        void Drive(FlightCtrlState s)
        {
            s.mainThrottle = 1;
            s.pitch = input.x; s.roll = input.y; s.yaw = input.z;
            s.pitchTrim = s.rollTrim = s.yawTrim = 0;
        }
    }
}
