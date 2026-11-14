**Title:** `ModuleGimbal.GetPotentialTorque()` over-credits gimbal torque (roll lever and pitch/yaw lever/axes are wrong)

**Provide a description of the KSP bug or improvement**

`ModuleGimbal.GetPotentialTorque()` reports more torque than the gimbals actually produce. Stock SAS sizes its gains from it via `VesselAutopilot.VesselSAS.GetTotalVesselTorque()`, and so do kRPC, MechJeb, kOS and other mods. When the authority is over-credited, the loop gains come out too low, and the overshoot or oscillation grows with the number of gimballed engines.

### What the stock code does (1.12.5, build 3190, decompiled)

For each gimbal transform and each engine it steers, with `thrust = finalThrust * mult`:

```
r      = gimbalTransform.position - vessel.CurrentCoM
lever  = r.magnitude                                      // whole distance to the CoM
radial = Vector3.ProjectOnPlane(r, ReferenceTransform.up).magnitude

d = GimbalRotation(t, ±right,   localCoM); pitch ±= lever  * thrust * (sin|d.x| + sin|d.y|)
d = GimbalRotation(t, ±forward, localCoM); yaw   ±= lever  * thrust * (sin|d.x| + sin|d.y|)
if (radial > minRollOffset)
  d = GimbalRotation(t, ±up,    localCoM); roll  ±= radial * thrust * (sin|d.x| + sin|d.y|)
```

What is wrong with it:
1. **Pitch/yaw lever.** The code uses `|r|`, the full distance from the CoM, where the torque arm is the component of `r` perpendicular to the deflected thrust. That over-credits any engine off the CoM line. It also ignores that tilting the thrust of an off-axis engine changes the torque by `r × ΔF`, not by `|r| · |ΔF|`.
2. **Both gimbal axes summed.** `sin|d.x| + sin|d.y|` adds the deflection on both gimbal axes for a single control axis. Only the component of the thrust tilt that acts about that axis produces torque on it.
3. **Roll.** Each engine beyond `minRollOffset` is credited `radial × thrust × (sin|d.x| + sin|d.y|)`. `FixedUpdate` adds the roll input to the engine's pitch/yaw deflection according to which side of the axis the engine sits on, so the real roll torque is what that tilt produces about the CoM. It is not the full radial lever times both axes' deflection.

### Reproduction (stock only)

- **Install:** KSP 1.12.5.3190 (Windows x64). GameData contains only `Squad`, `SquadExpansion` and a read-only measurement plugin (below): no Harmony, no ModuleManager, nothing patched. `KSP.log` "Mod DLLs found" lists only `Assembly-CSharp`, `GimbalProbe` and `KSPSteamCtrlr`. `Assembly-CSharp.dll` md5 `96b01ccc61875ef725fc315c2333d7cd`.
- **Craft:** stock **Kerbal X** (Mainsail + 6 radial LV-T45s).
- **Steps** (the plugin does them all): new sandbox, launch Kerbal X, `SetShipOrbit` to a 600 km circular Kerbin orbit (no aero), reaction wheels disabled (`authorityLimiter = 0`), SAS/RCS off. Light the 7 first-stage engines at full throttle (2790 kN). Then, for each axis, step the input 0 / +1 / 0 / −1 / 0, 1.5 s each, and fit the angular acceleration over the last 0.75 s of each step.
  - measured torque = `vessel.MOI[axis] × (α_step − mean α of the neighbouring zero steps)`
  - credited torque = Σ `ModuleGimbal.GetPotentialTorque()` over the same window

| axis | credited + (kN·m) | measured + | ratio | credited − | measured − | ratio |
|---|---:|---:|---:|---:|---:|---:|
| pitch | 825.8 | 690.3 | **1.20** | 825.8 | 700.3 | **1.18** |
| roll  | 129.9 | 120.3 | **1.08** | 129.9 |  91.6 | **1.42** |
| yaw   | 656.6 | 594.1 | **1.11** | 656.6 | 612.7 | **1.07** |

Every axis is over-credited, by 7–42 %. For scale, outside this stock repro: in my modded install, on a 13-engine booster (a ring of gimballed outer engines), the credited/measured ratio was 1.25–1.5 on pitch/yaw. Roll was 3–6× over-credited.

Everything is in [JamesHolden937/ksp-gimbal-torque](https://github.com/JamesHolden937/ksp-gimbal-torque): the probe source, the fix, a build script, and the full [KSP.log of this run](https://github.com/JamesHolden937/ksp-gimbal-torque/blob/a1e4b4984182540317afddd02aa36a7613cd35d5/logs/KSP-stock.log) (419 kB, results at the end). Both sources are also below.

<details>
<summary><code>GimbalProbe.cs</code>: the measurement plugin (read-only, ~190 lines)</summary>

```csharp
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
```

</details>

### Suggested fix

For each gimbal transform, axis and sign, take the deflection stock's own `GimbalRotation()` returns for a unit input. That already includes the ranges, the limiter, `minRollOffset`, the CoM-side flip and the enable flags. Rotate each of the engine's thrust transforms by that deflection, and report the change in `Σ (p − CoM) × F` about the reference-transform axis. With that patch the same probe reads credited/measured 0.85–1.00 on every axis. I have a working Harmony implementation ([`fix/GimbalTorqueFix.cs`](https://github.com/JamesHolden937/ksp-gimbal-torque/blob/a1e4b4984182540317afddd02aa36a7613cd35d5/fix/GimbalTorqueFix.cs), below) and can open a PR.

<details>
<summary><code>GimbalTorqueFix.cs</code>: the Harmony implementation (~150 lines)</summary>

```csharp
// GimbalTorqueFix: ModuleGimbal.GetPotentialTorque from the gimbal's own actuation.
//
// Stock ModuleGimbal.GetPotentialTorque (what SAS, kRPC's auto-tune and every
// other controller size their gains from) does not model what FixedUpdate does:
//
// - roll: every engine more than minRollOffset off the roll axis is credited
//   (distance from the axis) x thrust x (sin|deflection x| + sin|deflection y|):
//   both gimbal axes' full range at once, at the whole radial lever.  FixedUpdate
//   actually adds the roll input to an engine's pitch and yaw deflection by the
//   side of the axis it sits on, and the torque is what that tilt of the thrust
//   makes about the centre of mass;
// - pitch/yaw: the lever is the whole distance from the CoM rather than the arm
//   perpendicular to the force, and again the sum of both axes' deflection.
//
// On a 13-engine booster kRPC's roll figure was 3-6x what flight measured
// (krpcLab LOG16403/16415), so its roll loop barely pushed and the booster spun
// at 150-250 deg/s through the boostback.
//
// Fix: for each gimbal transform and each axis and sign, take the deflection
// stock's own GimbalRotation gives for that unit input (ranges, limiter,
// minRollOffset, the CoM-side flip, enable flags), rotate each of the engine's
// thrust transforms under it by that deflection, and sum r x dF about the CoM
// (force -thrustTransform.forward x finalThrust x multiplier at its position, as
// ModuleEngines.ThrustUpdate applies it).  Pure geometry: no transform is moved.
// Like stock it is a potential per axis, both signs reported as magnitudes, at the
// thrust the engines are making now; it says nothing of the gimbal's response lag.

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GimbalTorqueFix
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Loader : MonoBehaviour
    {
        void Awake()
        {
            new Harmony("krpclab.gimbaltorquefix").PatchAll(typeof(Loader).Assembly);
            Debug.Log("[GimbalTorqueFix] patched ModuleGimbal.GetPotentialTorque");
        }
    }

    [HarmonyPatch(typeof(ModuleGimbal), nameof(ModuleGimbal.GetPotentialTorque))]
    static class PotentialTorque
    {
        static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };

        static bool Prefix(ModuleGimbal __instance, out Vector3 pos, out Vector3 neg)
        {
            pos = Vector3.zero;
            neg = Vector3.zero;
            try
            {
                Compute(__instance, ref pos, ref neg);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GimbalTorqueFix] " + e.Message);
                pos = neg = Vector3.zero;
            }
            return false;
        }

        static void Compute(ModuleGimbal g, ref Vector3 pos, ref Vector3 neg)
        {
            if (g.gimbalLock || !g.moduleIsEnabled || g.vessel == null)
                return;
            Transform reference = g.vessel.ReferenceTransform;
            if (reference == null || g.gimbalTransforms == null || g.initRots == null)
                return;
            if (g.engineMultsList == null)
                g.CreateEngineList();
            Vector3 com = g.vessel.CurrentCoM;
            Vector3 localCoM = reference.InverseTransformPoint(com);
            bool[] enabled = { g.enablePitch, g.enableRoll, g.enableYaw };
            int count = Mathf.Min(g.gimbalTransforms.Count, g.initRots.Count);
            for (int i = 0; i < count; i++)
            {
                Transform t = g.gimbalTransforms[i];
                if (t == null || t.parent == null || i >= g.engineMultsList.Count)
                    continue;
                // The thrusts this transform steers: position, direction, force.
                var points = new List<Vector3>();
                var dirs = new List<Vector3>();
                var forces = new List<float>();
                foreach (KeyValuePair<ModuleEngines, float> pair in g.engineMultsList[i])
                {
                    ModuleEngines engine = pair.Key;
                    if (engine == null || engine.finalThrust <= 0f || engine.thrustTransforms == null)
                        continue;
                    for (int j = 0; j < engine.thrustTransforms.Count; j++)
                    {
                        Transform tt = engine.thrustTransforms[j];
                        if (tt == null || !(tt == t || tt.IsChildOf(t)))
                            continue;
                        float mult = j < engine.thrustTransformMultipliers.Count
                            ? engine.thrustTransformMultipliers[j] : 1f;
                        points.Add(tt.position);
                        dirs.Add(-tt.forward);
                        forces.Add(engine.finalThrust * mult);
                    }
                }
                if (points.Count == 0)
                    continue;
                Quaternion parent = t.parent.rotation;
                Quaternion init = g.initRots[i];
                // Rotation from where the transform is now to its neutral pose.
                Quaternion toNeutral = (parent * init) * Quaternion.Inverse(t.rotation);
                Vector3 pivot = t.position;
                Vector3 baseTorque = Torque(points, dirs, forces, toNeutral, pivot, com);
                for (int axis = 0; axis < 3; axis++)
                {
                    if (!enabled[axis])
                        continue;
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Vector3 d = g.GimbalRotation(t, s * Axes[axis], localCoM);
                        if (d.x == 0f && d.y == 0f)
                            continue;
                        Quaternion local = init
                            * Quaternion.AngleAxis(d.x * g.xMult, Vector3.right)
                            * Quaternion.AngleAxis(d.y * g.yMult, g.flipYZ ? Vector3.forward : Vector3.up);
                        Quaternion toDeflected = (parent * local) * Quaternion.Inverse(t.rotation);
                        Vector3 delta = Torque(points, dirs, forces, toDeflected, pivot, com) - baseTorque;
                        float about = Mathf.Abs(reference.InverseTransformDirection(delta)[axis]);
                        if (s > 0)
                            pos[axis] += about;
                        else
                            neg[axis] += about;
                    }
                }
            }
        }

        // Sum of r x F about the CoM with every thrust rotated by `rot` about `pivot`.
        static Vector3 Torque(List<Vector3> points, List<Vector3> dirs, List<float> forces,
                              Quaternion rot, Vector3 pivot, Vector3 com)
        {
            Vector3 sum = Vector3.zero;
            for (int k = 0; k < points.Count; k++)
            {
                Vector3 p = pivot + rot * (points[k] - pivot);
                Vector3 f = rot * dirs[k] * forces[k];
                sum += Vector3.Cross(p - com, f);
            }
            return sum;
        }
    }
}
```

</details>

### Related

- KSPModdingLibs/KSPCommunityFixes#118 (draft, "Attitude control overhaul") includes a `ModuleGimbal` rewrite in *GetPotentialTorqueFixes*, and its description notes the pitch/yaw magnitude is wrong. This issue is a measured, stock-only confirmation of that part and could be fixed on its own if #118 stays a draft.
- KSPModdingLibs/KSPCommunityFixes#32: the same class of bug for `ModuleRCS`.
