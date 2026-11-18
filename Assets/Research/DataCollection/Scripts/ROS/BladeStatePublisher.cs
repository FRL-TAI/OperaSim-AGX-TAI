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
    ///
    /// 排土板の前の土の質量・体積（front_*）と、そのステップの掘削体積（excavated_volume_step）は、
    /// 2.37 の Terrain の API（引数にショベルを取るもの）で常に取得する。2.40 では同じ関数が Shovel 側に移り、Terrain 側は非推奨。
    ///
    /// 土の山の体積（pile_*）は PileVolume で求める（T15。定義は PileVolume.cs の説明）。
    ///
    /// 前方の地面の高さ（T23。notes decisions/2026-11-17_粒子を含む高さの列とground_profileの扱い.md）：
    ///   ground_profile_*  : Unity の TerrainData の高さ（地形の高さ場。粒子を含まず、1ステップ遅れる）。これまでの記録との比較のため残す
    ///   ground_terrain_*  : 同じ点の、AGX の地形の高さ場（Terrain.getHeight）を直接読んだ高さ。1ステップの遅れがない
    ///   ground_vis_*      : 同じ点の、粒子を含む高さ（Terrain.getSoilParticleBoundedHeightField）と ground_terrain_* の大きいほう
    ///   ground_ptop_*     : 確かめ用。最寄りの頂点の地形の高さと、その頂点に割り当てた粒子の上端（中心 ＋ 半径）の最大の大きいほう
    ///   いずれもワールドの高さ [m]。ground_terrain_*・ground_vis_* は4つの頂点の双一次補間。
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
        [SerializeField] int profileTimingLogInterval = 500; // この回数の配信ごとに、粒子を含む高さと配信全体の処理時間を Console に出す（0 で出さない）

        [Header("状態ラベル（AGX_GRID_CONTROL 有効時）")]
        [SerializeField] float wedgeLength = 1.0f;        // 刃先から前方の、平均を取る範囲 [m]

        [Header("削っている層の締固め（AGX_GRID_CONTROL 有効時）")]
        [SerializeField] float cutAheadDistance = 0.5f;   // 刃先から前方の、読む範囲 [m]（横は排土板の幅）
        [SerializeField] int cutTimingLogInterval = 500;  // この回数の配信ごとに、読み取りの処理時間を Console に出す（0 で出さない）

        [Header("土の山の体積（PileVolume）")]
        [SerializeField] float pileFrontLength = 2.0f;    // 刃先から前方の範囲 [m]
        [SerializeField] float pileHalfWidth = 1.48f;     // 横の範囲（排土板の中心から）[m]

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

        PileVolume pile;

        // 前方の高さ（T23）
        Vector3[] profilePts;
        int visDiagCount = 0;                        // 粒子を含む高さ場の確認を出した回数（最初と、粒子が 100 個を超えた最初の回の2回）
        readonly System.Diagnostics.Stopwatch visWatch = new System.Diagnostics.Stopwatch();
        readonly System.Diagnostics.Stopwatch totalWatch = new System.Diagnostics.Stopwatch();
        double visTimeSum = 0, visTimeMax = 0, totalTimeSum = 0, totalTimeMax = 0;
        int profTimeCount = 0;

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
            totalWatch.Restart();

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
            if (profilePts == null || profilePts.Length != profileCount) profilePts = new Vector3[profileCount];
            for (int k = 1; k <= profileCount; k++)
            {
                float d = k * profileStep;
                profilePts[k - 1] = edgeMid + d * fwd;
                Add($"ground_profile_{Mathf.RoundToInt(d * 100):000}cm", SoilSimUtils.GroundHeight(terrain, profilePts[k - 1]));
            }

            // ---- 排土板の前の土の質量・体積、掘削体積（AGX 2.37 の Terrain の API）----
            // front_soil_mass        : 土塊（soil aggregate）の質量 [kg]、PRIMARY（刃の向きに削る）。AGX の説明では
            //                          「破壊領域で押しのけようとしている土の質量。PRIMARY では inner shape の分を含む」
            // front_soil_mass_deform : DEFORM_BACK・RIGHT・LEFT の土塊の質量の和 [kg]（押しのけ・横への変形）
            // front_dynamic_mass     : ショベルの inner shape の中の動く土（粒子と fluid mass）の質量 [kg]
            // front_inner_volume     : inner shape の体積 [m3]（dead load の計算に使う。排土板の形で決まる）
            // front_dead_load_fraction : inner shape のうち動く土で埋まっている割合 [-]（0〜1）
            // excavated_volume_step  : 地形全体で、直前のステップに掘削された体積 [m3]
            Add("front_soil_mass", t.getSoilAggregateMass(s, agxTerrain.Shovel.ExcavationMode.PRIMARY));
            Add("front_soil_mass_deform", t.getSoilAggregateMass(s, agxTerrain.Shovel.ExcavationMode.DEFORM_BACK)
                                        + t.getSoilAggregateMass(s, agxTerrain.Shovel.ExcavationMode.DEFORM_RIGHT)
                                        + t.getSoilAggregateMass(s, agxTerrain.Shovel.ExcavationMode.DEFORM_LEFT));
            Add("front_dynamic_mass", t.getDynamicMass(s));
            Add("front_inner_volume", t.getInnerVolume(s));
            Add("front_dead_load_fraction", t.getLastDeadLoadFraction(s));
            Add("excavated_volume_step", t.getLastExcavatedVolume());

            // ---- 土の山の体積（地形の高さ ＋ 動く土 ÷ (ρ/s)）と、動く土の合計（getDynamicMass の範囲の確認用）----
            // pile_volume          : V_pile [m3]（後ろは排土板の上端、前は刃先の前 pileFrontLength、横は ±pileHalfWidth）
            // pile_vol_terrain     : そのうち地形の分 Σ max(h − h0, 0)·A [m3]（h0 はシーンを読み込んだ直後の高さ）
            // pile_vol_dynamic     : そのうち動く土の分 粒子の質量 ÷ (ρ/s) [m3]（fluid mass は範囲で数えられない。dyn_mass_fluid_total が上限）
            // pile_mass_particle   : 範囲内の粒子の質量 [kg]
            // pile_volume_front150 / front250 / wide / ridge : 感度用（前 1.5 m・2.5 m、横に1マス広げた幅、横の畝＝外側1マス）[m3]
            // pile_vol_terrain_setup : 基本の範囲で Σ max(h − h_setup, 0)·A [m3]（h_setup は setup の地形の加工の後。山を含む）
            // pile_back_offset     : 刃先から排土板の上端までの水平距離 [m]（範囲の後ろの端）
            // pile_bulk_density    : 換算に使った ρ/s [kg/m3]
            // dyn_mass_particle_near : 上端〜刃先の前 0.5 m・±pileHalfWidth の粒子の質量 [kg]（参考）
            // dyn_mass_particle_total / dyn_mass_fluid_total / dyn_mass_total : 地形全体の粒子・fluid mass・calculateTotalDynamicMass [kg]
            // dyn_num_particles    : 地形全体の粒子の数
            // bal_vol_pos / bal_vol_neg : 刃先の後ろ 16 m〜前 3 m・±3 m の窓での Σ max(h − h0, 0)·A と Σ min(h − h0, 0)·A [m3]（収支の確認用）
            //   bal_* は 25 回（0.5 s）ごとに求め、間は前の値を出す
            if (pile == null) pile = new PileVolume(terrain);
            pile.FrontLength = pileFrontLength;
            pile.HalfWidth = pileHalfWidth;
            // 前方の断面の点の最寄りの頂点で、粒子の上端の最大を求めてもらう（ground_ptop_* 用）
            if (pile.QueryCells == null || pile.QueryCells.Length != profileCount) pile.QueryCells = new Vector2Int[profileCount];
            for (int k = 0; k < profileCount; k++) pile.QueryCells[k] = SoilSimUtils.WorldToUnityIndex(terrain, profilePts[k]);
            pile.Update(edgeMid, SoilSimUtils.TopEdgeMid(shovel), referenceFrame.forward);
            bool pv = pile.Valid;
            Add("pile_volume", pv ? pile.Volume(PileVolume.Bin.Base) : double.NaN);
            Add("pile_vol_terrain", pv ? pile.TerrainVol[(int)PileVolume.Bin.Base] : double.NaN);
            Add("pile_vol_dynamic", pv ? pile.ParticleMass[(int)PileVolume.Bin.Base] / pile.BulkDensity : double.NaN);
            Add("pile_mass_particle", pv ? pile.ParticleMass[(int)PileVolume.Bin.Base] : double.NaN);
            Add("pile_volume_front150", pv ? pile.Volume(PileVolume.Bin.Front150) : double.NaN);
            Add("pile_volume_front250", pv ? pile.Volume(PileVolume.Bin.Front250) : double.NaN);
            Add("pile_volume_wide", pv ? pile.Volume(PileVolume.Bin.Wide) : double.NaN);
            Add("pile_volume_ridge", pv ? pile.Volume(PileVolume.Bin.Ridge) : double.NaN);
            Add("pile_vol_terrain_setup", pv ? pile.TerrainVolSetup : double.NaN);
            Add("pile_back_offset", pv ? pile.BackOffset : double.NaN);
            Add("pile_bulk_density", pv ? pile.BulkDensity : double.NaN);
            Add("dyn_mass_particle_near", pv ? pile.ParticleMassNear : double.NaN);
            Add("dyn_mass_particle_total", pv ? pile.TotalParticleMass : double.NaN);
            Add("dyn_mass_fluid_total", pv ? pile.TotalFluidMass : double.NaN);
            Add("dyn_mass_total", pv ? pile.TotalDynamicMass : double.NaN);
            Add("dyn_num_particles", pv ? pile.NumParticles : double.NaN);
            Add("bal_vol_pos", pv ? pile.WindowVolPos : double.NaN);
            Add("bal_vol_neg", pv ? pile.WindowVolNeg : double.NaN);
            // pile_volume_vis      : 粒子を含む高さ場から求める山の体積 [m3]（V_pile と同じ範囲。T24。定義は PileVolume.cs の説明）
            // pile_volume_vis_wide : 同じ計算を、刃先の後ろ 1 m〜前 3 m・±2 m で [m3]
            Add("pile_volume_vis", pv ? pile.VisVol : double.NaN);
            Add("pile_volume_vis_wide", pv ? pile.VisVolWide : double.NaN);
            // vis_net_pos / vis_net_neg / vis_net : 収支の窓（bal_* と同じ）での、粒子を含む高さ − 開始時の粒子を含む高さ の
            //   正の部分・負の部分（正の値）・差 [m3]（T25）。1 秒ごと（raising の間と進行状況が変わった回は毎回）に求め、間は前の値
            Add("vis_net_pos", pv ? pile.VisNetPos : double.NaN);
            Add("vis_net_neg", pv ? pile.VisNetNeg : double.NaN);
            Add("vis_net", pv ? pile.VisNetPos - pile.VisNetNeg : double.NaN);

            // ---- 前方の地面の高さ（AGX から直接読む。粒子を含む高さ。T23）----
            AddForwardHeights(t, pv);

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
            totalWatch.Stop();
            LogProfileTiming();
        }

        /// <summary>
        /// ground_profile_* と同じ点で、ground_terrain_* / ground_vis_* / ground_ptop_* を足す（T23。定義はクラスの説明）。
        /// 粒子を含む高さ場は毎回 getSoilParticleBoundedHeightField で取り直す。解像度が地形と違うときは NaN にする。
        /// </summary>
        void AddForwardHeights(agxTerrain.Terrain t, bool pileValid)
        {
            int res = terrain.TerrainDataResolution;
            float baseY = terrain.Terrain.transform.position.y;

            visWatch.Restart();
            agxCollide.HeightField hfB = null;
            try { hfB = t.getSoilParticleBoundedHeightField(); }
            catch (System.Exception e) { if (visDiagCount == 0) Debug.LogWarning($"BladeStatePublisher: getSoilParticleBoundedHeightField が失敗しました: {e.Message}"); }
            bool hfOk = hfB != null && hfB.getResolutionX() == res && hfB.getResolutionY() == res;
            var vis = new double[profileCount];
            var ter = new double[profileCount];
            for (int k = 0; k < profileCount; k++)
            {
                ter[k] = Bilinear(profilePts[k], (ax, ay) => t.getHeight(new agx.Vec2i(ax, ay)));
                double hb = hfOk ? Bilinear(profilePts[k], (ax, ay) => hfB.getHeight((uint)ax, (uint)ay)) : double.NaN;
                vis[k] = double.IsNaN(hb) ? double.NaN : System.Math.Max(hb, ter[k]);
            }
            visWatch.Stop();
            if (visDiagCount == 0 || (visDiagCount == 1 && pileValid && pile.NumParticles > 100)) LogVisDiag(t, hfB, hfOk);

            for (int k = 0; k < profileCount; k++)
                Add($"ground_terrain_{Mathf.RoundToInt((k + 1) * profileStep * 100):000}cm", ter[k]);
            for (int k = 0; k < profileCount; k++)
                Add($"ground_vis_{Mathf.RoundToInt((k + 1) * profileStep * 100):000}cm", vis[k]);
            for (int k = 0; k < profileCount; k++)
            {
                double v = double.NaN;
                if (pileValid && pile.QueryParticleTop != null)
                {
                    var c = pile.QueryCells[k];
                    if (c.x >= 0 && c.y >= 0 && c.x < res && c.y < res)
                    {
                        var a = SoilSimUtils.UnityToAgxIndex(terrain, c.x, c.y);
                        double hT = baseY + t.getHeight(a);
                        v = System.Math.Max(hT, pile.QueryParticleTop[k]);
                    }
                }
                Add($"ground_ptop_{Mathf.RoundToInt((k + 1) * profileStep * 100):000}cm", v);
            }
        }

        /// <summary>
        /// ワールドの点 (x, z) の高さを、4つの頂点の高さ（AGX のマス番号で読む関数 h）から双一次補間で求める [ワールド y]。
        /// 再生中は Terrain の transform が MaximumDepth だけ下げてあり、AGX の高さ ＋ transform.y がワールドの高さになる
        /// （DeformableTerrain.UpdateHeights が TerrainData に AGX の高さをそのまま書くのと同じ対応）。範囲外は NaN。
        /// </summary>
        double Bilinear(Vector3 world, System.Func<int, int, double> h)
        {
            int res = terrain.TerrainDataResolution;
            var size = terrain.TerrainData.size;
            var pos = terrain.Terrain.transform.position;
            double fx = (world.x - pos.x) / size.x * (res - 1), fz = (world.z - pos.z) / size.z * (res - 1);
            if (fx < 0 || fz < 0 || fx > res - 1 || fz > res - 1) return double.NaN;
            int ix = System.Math.Min((int)System.Math.Floor(fx), res - 2), iz = System.Math.Min((int)System.Math.Floor(fz), res - 2);
            double ux = fx - ix, uz = fz - iz;
            // Unity の番号 (ix, iz) → AGX の番号 (res − 1 − ix, res − 1 − iz)
            double h00 = h(res - 1 - ix, res - 1 - iz), h10 = h(res - 2 - ix, res - 1 - iz);
            double h01 = h(res - 1 - ix, res - 2 - iz), h11 = h(res - 2 - ix, res - 2 - iz);
            return pos.y + (1 - ux) * (1 - uz) * h00 + ux * (1 - uz) * h10 + (1 - ux) * uz * h01 + ux * uz * h11;
        }

        /// <summary>
        /// 粒子を含む高さ場の対応を確かめるため、最初と、粒子が 100 個を超えた最初の回に、全頂点で
        /// 「粒子を含む高さ − 地形の高さ」の最小・最大と、1 mm より高い頂点の数を Console に出す。
        /// 地形の衝突用の高さ場（getHeightField）と getHeight の差も出す（座標の基準が同じかの確認）。
        /// </summary>
        void LogVisDiag(agxTerrain.Terrain t, agxCollide.HeightField hfB, bool hfOk)
        {
            visDiagCount++;
            int res = terrain.TerrainDataResolution;
            if (hfB == null) { Debug.LogWarning("BladeStatePublisher: getSoilParticleBoundedHeightField が null を返しました。ground_vis_* は NaN になります"); return; }
            if (!hfOk)
            {
                Debug.LogWarning($"BladeStatePublisher: 粒子を含む高さ場の解像度 {hfB.getResolutionX()}×{hfB.getResolutionY()} が地形 {res} と違います。ground_vis_* は NaN になります");
                return;
            }
            var hfC = t.getHeightField();
            double dMin = double.PositiveInfinity, dMax = double.NegativeInfinity, cMaxAbs = 0;
            int nAbove = 0;
            for (int ay = 0; ay < res; ay++)
                for (int ax = 0; ax < res; ax++)
                {
                    double hT = t.getHeight(new agx.Vec2i(ax, ay));
                    double d = hfB.getHeight((uint)ax, (uint)ay) - hT;
                    dMin = System.Math.Min(dMin, d); dMax = System.Math.Max(dMax, d);
                    if (d > 1e-3) nAbove++;
                    if (hfC != null) cMaxAbs = System.Math.Max(cMaxAbs, System.Math.Abs(hfC.getHeight((uint)ax, (uint)ay) - hT));
                }
            // TerrainData（ground_profile_* の元）と AGX の高さの対応の確認（刃先の前 2.0 m の点）
            Vector3 p = profilePts[profileCount - 1];
            double viaUnity = SoilSimUtils.GroundHeight(terrain, p);
            double viaAgx = Bilinear(p, (ax, ay) => t.getHeight(new agx.Vec2i(ax, ay)));
            Debug.Log($"BladeStatePublisher: 粒子を含む高さ場（粒子 {(pile != null ? pile.NumParticles : 0)} 個）解像度 {res}、粒子を含む高さ − 地形の高さ 最小 {dMin * 1000:F2} mm・最大 {dMax * 1000:F1} mm、"
                      + $"1 mm より高い頂点 {nAbove}/{res * res}、|衝突用の高さ場 − getHeight| の最大 {cMaxAbs * 1000:F3} mm、"
                      + $"前 {profileCount * profileStep:F2} m の点で TerrainData {viaUnity:F4} m・AGX {viaAgx:F4} m");
        }

        void LogProfileTiming()
        {
            if (profileTimingLogInterval <= 0) return;
            double v = visWatch.Elapsed.TotalMilliseconds, a = totalWatch.Elapsed.TotalMilliseconds;
            visTimeSum += v; visTimeMax = System.Math.Max(visTimeMax, v);
            totalTimeSum += a; totalTimeMax = System.Math.Max(totalTimeMax, a);
            if (++profTimeCount < profileTimingLogInterval) return;
            Debug.Log($"BladeStatePublisher: {profTimeCount} 回の平均 粒子を含む高さ {visTimeSum / profTimeCount:F3} ms（最大 {visTimeMax:F3} ms）、"
                      + $"配信全体 {totalTimeSum / profTimeCount:F3} ms（最大 {totalTimeMax:F3} ms）（1ステップ {Time.fixedDeltaTime * 1000:F1} ms）");
            visTimeSum = visTimeMax = totalTimeSum = totalTimeMax = 0; profTimeCount = 0;
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
