using System.Collections.Generic;
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
    /// Player Settings の Scripting Define Symbols で次を定義すると、追加の値も取得する
    /// （2.37 で API があるか未確認のため既定では無効。無効なら NaN）。
    ///   AGX_GRID_CONTROL      : くさびの範囲の締固めの程度（state_compaction_*）
    ///   AGX_EXTRA_TERRAIN_API : 前方の土塊の質量、そのステップの掘削体積
    /// </summary>
    public class BladeStatePublisher : MonoBehaviour
    {
        [Header("AGX")]
        [SerializeField] DeformableTerrain terrain;
        [SerializeField] DeformableTerrainShovel shovel;
        [SerializeField] Transform referenceFrame;   // base_link

        [Header("地面の高さ")]
        [SerializeField] float aheadDistance = 0.5f;      // ground_ahead を測る距離 [m]
        [SerializeField] float profileStep = 0.25f;       // 前方の断面を測る間隔 [m]
        [SerializeField] int profileCount = 8;            // 前方の断面の点数

        [Header("状態ラベル（AGX_GRID_CONTROL 有効時）")]
        [SerializeField] float wedgeLength = 1.0f;        // 刃先から前方の、平均を取る範囲 [m]

        [Header("ROS")]
        [SerializeField] string topicName = "/d37pxi_24/blade_state";
        [SerializeField] float publishRate = 50f;

        ROSConnection ros;
        double lastPublishTime = double.NegativeInfinity;
        readonly List<string> names = new List<string>();
        readonly List<double> values = new List<double>();
        string namesLabel = null;

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
            if (time - lastPublishTime < 1.0 / publishRate) return;
            lastPublishTime = time;
            values.Clear();

            Add("sim_time", time);

            // ---- 排土板にかかる力（DeformableTerrain.ShowForces と同じ API・符号）----
            var fPen = new agx.Vec3();
            var tPen = new agx.Vec3();
            t.getPenetrationForce(s, ref fPen, ref tPen);
            Vector3 pen = ToBaseFLU(fPen);
            Vector3 sep = ToBaseFLU(-t.getSeparationContactForce(s));
            Vector3 def = ToBaseFLU(-t.getDeformationContactForce(s));
            Vector3 con = ToBaseFLU(-t.getContactForce(s));
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

        // AGX ワールド座標のベクトル → Unity → base_link 基準 → ROS FLU
        Vector3 ToBaseFLU(agx.Vec3 agxWorld)
        {
            Vector3 world = agxWorld.ToHandedVector3();
            Vector3 local = referenceFrame.InverseTransformDirection(world);
            return new Vector3(local.z, -local.x, local.y);
        }
    }
}
