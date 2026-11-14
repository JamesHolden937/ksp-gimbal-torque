# ModuleGimbal.GetPotentialTorque: stock measurement and a fix

Supporting material for a KSPCommunityFixes issue. `ModuleGimbal.GetPotentialTorque()`
in KSP 1.12.5 credits more torque than the gimbals produce. SAS, kRPC, MechJeb and kOS
all size their gains from it.

| path | what |
|---|---|
| [`probe/GimbalProbe.cs`](probe/GimbalProbe.cs) | Read-only measurement plugin, no Harmony. Starts a sandbox game, launches stock Kerbal X, puts it in a 600 km Kerbin orbit, turns off the reaction wheels, SAS and RCS, and lights the first stage. It then steps each axis 0/+1/0/−1/0 and compares `MOI × α` with Σ `GetPotentialTorque()`. Writes `gimbalprobe.txt` in the KSP root and `[GimbalProbe]` lines in KSP.log, then quits. |
| [`fix/GimbalTorqueFix.cs`](fix/GimbalTorqueFix.cs) | Harmony prefix that replaces `GetPotentialTorque()`. For each gimbal transform, axis and sign it takes the deflection stock's own `GimbalRotation()` gives, rotates the engine's thrust transforms by it, and sums `r × ΔF` about the CoM. |
| [`logs/KSP-stock.log`](logs/KSP-stock.log) | KSP.log of the probe run on an unmodded 1.12.5.3190 install (Squad, SquadExpansion, GimbalProbe only). The results are at the end. |
| [`ISSUE.md`](ISSUE.md) | The KSPCommunityFixes issue text. |
| [`build.sh`](build.sh) | Builds both plugins with `mcs`: `KSP=/path/to/KSP ./build.sh` → `bin/`. |

## Stock result (Kerbal X, 7 engines, 2790 kN)

| axis | credited + (kN·m) | measured + | ratio | credited − | measured − | ratio |
|---|---:|---:|---:|---:|---:|---:|
| pitch | 825.8 | 690.3 | 1.20 | 825.8 | 700.3 | 1.18 |
| roll  | 129.9 | 120.3 | 1.08 | 129.9 |  91.6 | 1.42 |
| yaw   | 656.6 | 594.1 | 1.11 | 656.6 | 612.7 | 1.07 |

## Reproducing

1. A clean KSP 1.12.5 install with only `Squad` and `SquadExpansion` in GameData.
2. `KSP=/path/to/KSP ./build.sh`, then copy `bin/GimbalProbe.dll` to `GameData/GimbalProbe/Plugins/`.
3. Start KSP and leave it. The probe runs from the main menu and quits when done (about 45 s in flight).
4. Read `gimbalprobe.txt`, or grep `[GimbalProbe]` in KSP.log.

To measure the fix, also install `000_Harmony` and `bin/GimbalTorqueFix.dll` (in
`GameData/GimbalTorqueFix/Plugins/`) and run the probe again.

The craft can be changed with `Cfg.Craft` in `GimbalProbe.cs`.

## License

MIT, see [LICENSE](LICENSE).
