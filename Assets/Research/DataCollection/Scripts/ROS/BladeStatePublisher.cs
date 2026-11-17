using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using AGXUnity;
using AGXUnity.Model;
using AGXUnity.Utils;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// 排土板まわりの状態を std_msgs/Float64MultiArray で配信する。
    /// 各値の名前を layout.dim[0].label にカンマ区切りで入れるので、受信側は名前で値を取り出せる。
    ///
    /// 力は base_link 基準の ROS FLU 座標（x:前, y:左, z:上）。
    /// 位置は Unity のワールド座標を ROS の並び（x = Unity z, y = -Unity x, z = Unity y）にしたもの。
    ///
    /// Player Settings の Scripting Define Symbols で次を定義すると、追加の値も取得する（無効なら NaN）。
    ///   AGX_GRID_CONTROL      : くさびの範囲の締固めの程度（state_compaction_*）と、削っている層の締固めの程度
    ///                           （state_cut_compaction_*）。このプロジェクトでは定義済み（2.37 で動作を確認）
    ///   AGX_EXTRA_TERRAIN_API : 前方の土塊の質量、そのステップの掘削体積。2.37 で API があるか未確認のため定義していない
    /// </summary>
    public class BladeStatePublisher : MonoBehaviour
    {
        [Header("AGX")]
        [SerializeField] DeformableTerrain terrain;
        [SerializeField] DeformableTerrainShovel shovel;
        [SerializeField] Transform referenceFrame;   // base_link

        [Header("地面の高さ")]
        [SerializeField] float aheadDistance = 0.5f;      // ground_ahead を測る距離 [m]
        [SerializeField] float behindDistance = 0.3f;     // ground_behind を測る距離 [m]（刃先の後ろ。削った後の地面）
        [SerializeField] float profileStep = 0.25f;       // 前方の断面を測る間隔 [m]
        [SerializeField] int profileCount = 8;            // 前方の断面の点数

        [Header("状態ラベル（AGX_GRID_CONTROL 有効時）")]
        [SerializeField] float wedgeLength = 1.0f;        // 刃先から前方の、平均を取る範囲 [m]

        [Header("削っている層の締固め（AGX_GRID_CONTROL 有効時）")]
        [SerializeField] float cutAheadDistance = 0.5f;   // 刃先から前方の、読む範囲 [m]（横は排土板の幅）
        [SerializeField] int cutTimingLogInterval = 500;  // この回数の配信ごとに、読み取りの処理時間を Console に出す（0 で出さない）

        [Header("ROS")]
        [SerializeField] string topicName = "/d37pxi_24/blade_state";
        [SerializeField] float publishRate = 50f;

        ROSConnection ros;
        double lastPublishTime = double.NegativeInfinity;
        readonly List<string> names = new List<string>();
        readonly List<double> values = new List<double>();
        string namesLabel = null;

        // 削っている層の読み取り（CompactionProbe と同じく、リフレクションで呼ぶ）
        bool cutApiResolved = false;
        bool cutDiagLogged = false;                  // 最初に読んだマスの値を1回だけ Console に出す（対応の確認用）
        MethodInfo miFindSurface, miGetCompaction, miElementPosWorld;
        readonly System.Diagnostics.Stopwatch cutWatch = new System.Diagnostics.Stopwatch();
        double cutTimeSum = 0, cutTimeMax = 0;
        int cutTimeCount = 0;

        void Start()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<Float64MultiArrayMsg>(topicName);
            Simulation.Instance.StepCallbacks.PostStepForward += OnPostStepForward;
        }

        void OnDestroy()
        {
            if (Simulation.HasInstance)
                Simulation.Instance.StepCallbacks.PostStepForward -= OnPostStepForward;
        }

        void Add(string name, double v) { if (namesLabel == null) names.Add(name); values.Add(v); }
        void Add(string prefix, Vector3 v) { Add(prefix + "_x", v.x); Add(prefix + "_y", v.y); Add(prefix + "_z", v.z); }

        void OnPostStepForward()
        {
            var t = terrain != null ? terrain.Native : null;
            var s = shovel != null ? shovel.Native : null;
            if (t == null || s == null) return;

            double time = Time.fixedTimeAsDouble;
            // 1ステップ（Time.fixedDeltaTime）は浮動小数点の誤差で 0.02 よりわずかに小さいことがあり、
            // 余裕なしで比べると1ステップおきに配信が飛ばされて 25 Hz になる
            // 半ステップ分の余裕を持たせて、50 Hz の設定なら毎ステップ配信されるようにする
            if (time - lastPublishTime < 1.0 / publishRate - 0.5 * Time.fixedDeltaTime) return;
            lastPublishTime = time;
            values.Clear();

            Add("sim_time", time);

            // ---- 排土板にかかる力（DeformableTerrain.ShowForces と同じ API・符号）----
            var fPen = new agx.Vec3();
            var tPen = new agx.Vec3();
            t.getPenetrationForce(s, ref fPen, ref tPen);
            // 4つとも「排土板が土から受ける力」にそろえる。AGX の API はいずれもこの向きの値を返すので、符号を反転しない。
            // AGXUnity の ShowForces は Separation / Deformation / Contact だけを反転して表示しているが、そのまま使うと Penetration と向きが逆になる
            Vector3 pen = ToBaseFLU(fPen);
            Vector3 sep = ToBaseFLU(t.getSeparationContactForce(s));
            Vector3 def = ToBaseFLU(t.getDeformationContactForce(s));
            Vector3 con = ToBaseFLU(t.getContactForce(s));
            Add("F_total", pen + sep + def + con);
            Add("F_pen", pen);
            Add("F_sep", sep);
            Add("F_def", def);
            Add("F_con", con);
            Add("T_pen", ToBaseFLU(tPen));

            // ---- 刃先と排土板の姿勢 ----
            Vector3 edgeMid = SoilSimUtils.CuttingEdge(shovel, out var edgeStart, out var edgeEnd);
            Vector3 fwd = SoilSimUtils.Flat(referenceFrame.forward);
            Vector3 bladeVec = SoilSimUtils.TopEdgeMid(shovel) - edgeMid;
            Add("edge_x", edgeMid.z); Add("edge_y", -edgeMid.x); Add("edge_z", edgeMid.y);
            Add("blade_width", Vector3.Distance(edgeStart, edgeEnd));
            Add("blade_alpha_deg", Mathf.Atan2(Vector3.Dot(bladeVec, Vector3.up),
                                               -Vector3.Dot(bladeVec, referenceFrame.forward)) * Mathf.Rad2Deg);

            // ---- 地面の高さ ----
            Add("ground_below", SoilSimUtils.GroundHeight(terrain, edgeMid));
            Add("ground_behind", SoilSimUtils.GroundHeight(terrain, edgeMid - behindDistance * fwd));
            Add("ground_ahead", SoilSimUtils.GroundHeight(terrain, edgeMid + aheadDistance * fwd));
            for (int k = 1; k <= profileCount; k++)
            {
                float d = k * profileStep;
                Add($"ground_profile_{Mathf.RoundToInt(d * 100):000}cm", SoilSimUtils.GroundHeight(terrain, edgeMid + d * fwd));
            }

            // ---- 前方の土塊の質量・掘削体積 ----
            double frontMass = double.NaN, excavated = double.NaN;
#if AGX_EXTRA_TERRAIN_API
            // TODO(要確認): 2.37 での名前。2.40 では Shovel 側（shovel->getSoilParticleAggregate()）
            var agg = t.getSoilParticleAggregate(s);
            if (agg != null) frontMass = agg.getTotalAggregateMass();
            excavated = t.getLastExcavatedVolume();
#endif
            Add("front_soil_mass", frontMass);
            Add("excavated_volume_step", excavated);

            // ---- くさびの範囲の締固めの程度（時刻ごとに変わる状態ラベル）----
            double cMean = double.NaN, cStd = double.NaN, cN = 0;
#if AGX_GRID_CONTROL
            var grid = t.getTerrainGridControl();
            double sum = 0, sum2 = 0;
            int n = SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, 0f, wedgeLength,
                -0.5f * Vector3.Distance(edgeStart, edgeEnd), 0.5f * Vector3.Distance(edgeStart, edgeEnd),
                idx => { double c = grid.getSurfaceCompaction(idx); sum += c; sum2 += c * c; });
            if (n > 0)
            {
                cMean = sum / n;
                cStd = System.Math.Sqrt(System.Math.Max(0, sum2 / n - cMean * cMean));
                cN = n;
            }
#endif
            Add("state_compaction_mean", cMean);
            Add("state_compaction_std", cStd);
            Add("state_compaction_n", cN);

            // ---- 削っている層の締固めの程度（刃先の高さのボクセル）----
            double ccMean = double.NaN, ccMin = double.NaN, ccMax = double.NaN, ccN = 0;
#if AGX_GRID_CONTROL
            cutWatch.Restart();
            if (CutCompaction(grid, edgeMid, fwd, Vector3.Distance(edgeStart, edgeEnd),
                              out double m, out double mn, out double mx, out int nc))
            {
                ccMean = m; ccMin = mn; ccMax = mx; ccN = nc;
            }
            cutWatch.Stop();
            LogCutTiming(cutWatch.Elapsed.TotalMilliseconds);
#endif
            Add("state_cut_compaction_mean", ccMean);
            Add("state_cut_compaction_min", ccMin);
            Add("state_cut_compaction_max", ccMax);
            Add("state_cut_compaction_n", ccN);

            if (namesLabel == null) namesLabel = string.Join(",", names);
            var msg = new Float64MultiArrayMsg
            {
                layout = new MultiArrayLayoutMsg
                {
                    dim = new[] { new MultiArrayDimensionMsg { label = namesLabel, size = (uint)values.Count, stride = (uint)values.Count } },
                    data_offset = 0
                },
                data = values.ToArray()
            };
            ros.Publish(topicName, msg);
        }

#if AGX_GRID_CONTROL
        /// <summary>
        /// 刃先の前 0〜cutAheadDistance・排土板の幅のマスについて、刃先の高さ（edgeMid.y）に当たるボクセルの
        /// 締固めの程度を読み、平均・最小・最大と読んだマスの数を返す。読んだマスがなければ false。
        ///
        /// ボクセルの決め方：地表のボクセル zs（findSurfaceIndex）の中心の高さ y_c を getGridElementPositionWorld で読み、
        ///   刃先のボクセル = zs + floor((edge_z − y_c) / e + 0.5)   （e はボクセルの大きさ。ボクセルは中心 ± e/2）
        /// 刃先がちょうど境目にあるときは上のボクセルになる。
        /// 刃先がそのマスの地表（地面の高さ）より上にあるマス、刃先のボクセルが地表のボクセルより上になるマスは、
        /// 削っていないので数えない。
        /// </summary>
        bool CutCompaction(agxTerrain.TerrainGridControl grid, Vector3 edgeMid, Vector3 fwd, float width,
                           out double mean, out double min, out double max, out int n)
        {
            mean = min = max = double.NaN;
            n = 0;
            if (!cutApiResolved) ResolveCutApis(grid);
            if (miFindSurface == null || miGetCompaction == null || miElementPosWorld == null) return false;

            double e = terrain.Native.getElementSize();
            int res = terrain.TerrainDataResolution;
            double edgeY = edgeMid.y;
            double sum = 0, lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            int count = 0;
            SoilSimUtils.ForEachCellInRect(terrain, edgeMid, fwd, 0f, cutAheadDistance, -0.5f * width, 0.5f * width, idx =>
            {
                int ax = (int)idx.x, ay = (int)idx.y;
                float ground = SoilSimUtils.GroundHeight(terrain, SoilSimUtils.UnityIndexToWorld(terrain, res - 1 - ax, res - 1 - ay));
                if (edgeY >= ground) return;                                   // 刃先が地表より上（削っていない）
                int zs = Convert.ToInt32(miFindSurface.Invoke(grid, new object[] { idx }));
                if (!(miElementPosWorld.Invoke(grid, new object[] { new agx.Vec3i(ax, ay, zs) }) is agx.Vec3 pos)) return;
                double yc = pos.ToHandedVector3().y;
                int dz = (int)System.Math.Floor((edgeY - yc) / e + 0.5);
                if (dz > 0) return;                                            // 刃先が地表のボクセルより上
                double c = Convert.ToDouble(miGetCompaction.Invoke(grid, new object[] { new agx.Vec3i(ax, ay, zs + dz) }));
                if (!cutDiagLogged)
                {
                    cutDiagLogged = true;
                    Debug.Log($"BladeStatePublisher: 削っている層の最初のマス agx=({ax},{ay}) 地表 {ground:F4} m、地表のボクセル zs={zs}"
                              + $"（中心 {yc:F4} m、e={e:F4} m）、edge_z {edgeY:F4} m → dz={dz}、締固めの程度 {c:F4}");
                }
                if (double.IsNaN(c)) return;
                sum += c; lo = System.Math.Min(lo, c); hi = System.Math.Max(hi, c);
                count++;
            });
            if (count == 0) return false;
            mean = sum / count; min = lo; max = hi; n = count;
            return true;
        }

        void ResolveCutApis(agxTerrain.TerrainGridControl grid)
        {
            cutApiResolved = true;
            var methods = grid.GetType().GetMethods();
            miFindSurface = methods.FirstOrDefault(m => m.Name == "findSurfaceIndex"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(agx.Vec2i));
            miGetCompaction = methods.FirstOrDefault(m => m.Name == "getCompaction"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(agx.Vec3i));
            miElementPosWorld = methods.FirstOrDefault(m => m.Name == "getGridElementPositionWorld"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(agx.Vec3i)
                && m.ReturnType == typeof(agx.Vec3));
            if (miFindSurface != null && miGetCompaction != null && miElementPosWorld != null)
            {
                Debug.Log("BladeStatePublisher: 削っている層の締固めを読み出せます（findSurfaceIndex / getGridElementPositionWorld / getCompaction）");
                return;
            }
            var candidates = methods.Where(m => m.Name.Contains("ompaction") || m.Name.Contains("SurfaceIndex") || m.Name.Contains("Position"))
                                    .Select(m => $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(q => q.ParameterType.Name))})")
                                    .Distinct();
            Debug.LogWarning("BladeStatePublisher: 削っている層の締固めの関数が見つかりません。state_cut_compaction_* は NaN になります。"
                             + $"候補: {string.Join("; ", candidates)}");
        }

        void LogCutTiming(double ms)
        {
            if (cutTimingLogInterval <= 0) return;
            cutTimeSum += ms; cutTimeMax = System.Math.Max(cutTimeMax, ms); cutTimeCount++;
            if (cutTimeCount < cutTimingLogInterval) return;
            Debug.Log($"BladeStatePublisher: 削っている層の読み取り {cutTimeCount} 回、平均 {cutTimeSum / cutTimeCount:F3} ms、"
                      + $"最大 {cutTimeMax:F3} ms（1ステップ {Time.fixedDeltaTime * 1000:F1} ms）");
            cutTimeSum = 0; cutTimeMax = 0; cutTimeCount = 0;
        }
#endif

        // AGX ワールド座標のベクトル → Unity → base_link 基準 → ROS FLU
        Vector3 ToBaseFLU(agx.Vec3 agxWorld)
        {
            Vector3 world = agxWorld.ToHandedVector3();
            Vector3 local = referenceFrame.InverseTransformDirection(world);
            return new Vector3(local.z, -local.x, local.y);
        }
    }
}
