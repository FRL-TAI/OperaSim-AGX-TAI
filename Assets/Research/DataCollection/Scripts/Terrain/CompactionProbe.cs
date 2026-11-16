using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using AGXUnity;
using AGXUnity.Model;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;


namespace PWRISimulator.ROS
{
    /// <summary>
    /// 締固めの記録用（実験A・B など）。
    /// ROS 側が setup 指令で record_compaction を true にしたエピソードの間、
    /// 履帯の走路・機体の中心・対照（走路の外）の列に沿った地形のマスについて、
    /// 地表の高さと締固めの程度（地表と深さ方向）を一定間隔で /sim/compaction_probe に送る。
    /// ファイルには書き出さない（保存は ROS 側の CompactionRecorder が行う）。
    ///
    /// 送るメッセージ（std_msgs/String, JSON）:
    ///   {"request_id": …, "episode_name": …, "final": false, "header": "sim_time,phase,…", "rows": "…\n…\n"}
    ///   request_id は setup 指令の番号。ROS 側はこれが一致する記録だけを保存する。
    ///   rows は1回分の記録（CSV の行、改行は \n）。列はこれまでのファイル出力と同じ。
    ///   final が true のメッセージが、そのエピソードの最後の記録。
    ///
    /// 履帯の走路は、履帯の幅（trackWidth）の中に中心が入るマスをすべて記録する。
    /// 列の名前は「track_L-0.16」のように、履帯の中心線からのマスの中心の横の位置（左方が正）[m] を付ける。
    /// 機体の中心（center）と対照（control）は1列だけ記録する。
    ///
    /// 記録の開始：SimEpisodeManager の phase が "lowering" になり、record_compaction が true のとき。
    ///             このときの referenceFrame の位置と向きを、列の座標の原点と前方向にする。
    ///             referenceFrame には、走行とともに動く剛体のオブジェクト（body_link など）を設定する。
    /// 記録の終了：phase が "done"（または "idle"）になった時点で、最後の記録を final として送る。
    ///
    /// 深さ方向の値は TerrainGridControl の findSurfaceIndex / getCompaction(Vec3i) を使う。
    /// この名前の関数がなければ、深さ方向の列は NaN になり、Console に候補の関数名を出す
    /// （コンパイルエラーにならないよう、リフレクションで呼んでいる）。
    /// </summary>
    public class CompactionProbe : MonoBehaviour
    {
        [Header("参照")]
        [SerializeField] DeformableTerrain terrain;
        [SerializeField] Transform referenceFrame;           // 走行とともに動く剛体（d37pxi_24 の body_link など）
        [SerializeField] SimEpisodeManager episodeManager;
        [SerializeField] Track leftTrack;                    // AGXUnity.Model.TrackL
        [SerializeField] Track rightTrack;                   // AGXUnity.Model.TrackR

        [Header("記録の条件")]
        [SerializeField] float xMin = -1.0f;                       // 開始位置からの前後の範囲 [m]（前方が正）
        [SerializeField] float xMax = 12.0f;
        [SerializeField] float trackWidth = 0.6f;                  // 履帯の幅 [m]（Track の Width と同じ値にする）
        [SerializeField] float controlOffset = 3.5f;              // 対照の列の位置（左方）[m]
        [SerializeField] int depthLayers = 6;                     // 深さ方向に読むボクセルの数（地表のボクセルを含む）
        [SerializeField] float sampleInterval = 1.0f;             // 記録の間隔 [s]（シミュレーション時刻）

        [Header("ROS")]
        [SerializeField] string probeTopic = "/sim/compaction_probe";

        /// <summary>記録する列。halfWidth が 0 なら中心線上の1列、正なら中心線から ±halfWidth に中心が入るマスすべて</summary>
        struct Lane
        {
            public string name;
            public float y;          // 中心線の横の位置（左方）[m]
            public float halfWidth;  // [m]
        }

        /// <summary>ROS に送るメッセージ（JsonUtility で JSON にする）</summary>
        [Serializable]
        class ProbeMessage
        {
            public int request_id;
            public string episode_name;
            public bool final;
            public string header;
            public string rows;
        }

        bool recording = false;
        bool finished = false;
        double lastSample = double.NegativeInfinity;
        Vector3 origin, fwd, left;
        readonly List<Lane> lanes = new List<Lane>();
        agxTerrain.TerrainGridControl grid;
        MethodInfo miFindSurface, miGetCompaction;

        ROSConnection ros;
        string header;
        int requestId;
        string episodeName;
        int snapshots;

        void Start()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.RegisterPublisher<StringMsg>(probeTopic);
            Simulation.Instance.StepCallbacks.PostStepForward += OnPostStep;
        }

        void OnDestroy()
        {
            if (Simulation.HasInstance)
                Simulation.Instance.StepCallbacks.PostStepForward -= OnPostStep;
        }

        void OnPostStep()
        {
            if (finished || episodeManager == null || terrain == null || referenceFrame == null) return;
            var st = episodeManager.Status;

            if (!recording)
            {
                if (st.phase == "lowering" && st.record_compaction)
                    Begin(st.episode_name, st.request_id);
                return;
            }

            double t = Simulation.Instance.Native.getTimeStamp();
            bool done = st.phase == "done" || st.phase == "idle";
            if (t - lastSample >= sampleInterval || done)
            {
                Snapshot(t, st.phase, done);
                lastSample = t;
            }
            if (done)
            {
                recording = false;
                finished = true;
                Debug.Log($"CompactionProbe: 記録を終了しました（{episodeName}、{snapshots} 回分を送信）");
            }
        }

        void Begin(string name, int rid)
        {
            grid = terrain.Native.getTerrainGridControl();
            origin = referenceFrame.position;
            fwd = SoilSimUtils.Flat(referenceFrame.forward);
            left = -Vector3.Cross(Vector3.up, fwd);   // SoilSimUtils と同じ（Unity は左手系）

            lanes.Clear();
            if (leftTrack != null)
                lanes.Add(new Lane { name = "track_L", y = LaneOffset(leftTrack), halfWidth = 0.5f * trackWidth });
            if (rightTrack != null)
                lanes.Add(new Lane { name = "track_R", y = LaneOffset(rightTrack), halfWidth = 0.5f * trackWidth });
            lanes.Add(new Lane { name = "center", y = 0f, halfWidth = 0f });
            lanes.Add(new Lane { name = "control", y = controlOffset, halfWidth = 0f });

            FindDepthApis();

            var cols = new List<string> { "sim_time", "phase", "machine_x", "lane", "lane_y", "x",
                                          "unity_ix", "unity_iy", "height", "c_surface", "surface_z" };
            for (int k = 0; k < depthLayers; k++) cols.Add($"c_z{k}");
            header = string.Join(",", cols);

            episodeName = name;
            requestId = rid;
            snapshots = 0;
            recording = true;
            string laneInfo = string.Join(", ", lanes.Select(l =>
                l.halfWidth > 0 ? $"{l.name}={l.y:F2}m±{l.halfWidth:F2}m" : $"{l.name}={l.y:F2}m"));
            Debug.Log($"CompactionProbe: 記録を開始しました（{episodeName}、request_id {requestId}）。"
                      + $"列: {laneInfo}。送信先: {probeTopic}");
        }

        /// <summary>履帯の車輪の位置の平均から、走路の中心線の横の位置（左方）[m] を求める</summary>
        float LaneOffset(Track track)
        {
            var wheels = track.Wheels.ToList();
            if (wheels.Count == 0) return 0f;
            return wheels.Average(w => Vector3.Dot(w.transform.position - origin, left));
        }

        void FindDepthApis()
        {
            var methods = grid.GetType().GetMethods();
            miFindSurface = methods.FirstOrDefault(m => m.Name == "findSurfaceIndex"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(agx.Vec2i));
            miGetCompaction = methods.FirstOrDefault(m => m.Name == "getCompaction"
                && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(agx.Vec3i));

            if (miFindSurface != null && miGetCompaction != null)
            {
                Debug.Log("CompactionProbe: 深さ方向の締固めを読み出せます（findSurfaceIndex / getCompaction）");
                return;
            }
            var candidates = methods.Select(m => m.Name)
                                    .Where(n => n.Contains("ompaction") || n.Contains("SurfaceIndex"))
                                    .Distinct();
            Debug.LogWarning("CompactionProbe: 深さ方向の関数が見つかりません。深さ方向の列は NaN になります。"
                             + $"候補: {string.Join(", ", candidates)}");
        }

        void Snapshot(double t, string phase, bool final)
        {
            int res = terrain.TerrainDataResolution;
            float step = (float)terrain.Native.getElementSize();
            float machineX = Vector3.Dot(referenceFrame.position - origin, fwd);
            var sb = new StringBuilder();

            foreach (var lane in lanes)
            {
                var visited = new HashSet<Vector2Int>();
                // 横方向：中心線から ±halfWidth を半マスずつ。halfWidth が 0 なら中心線だけ
                int nLat = lane.halfWidth > 0 ? Mathf.CeilToInt(2f * lane.halfWidth / (0.5f * step)) + 1 : 1;

                for (float x = xMin; x <= xMax + 1e-4f; x += 0.5f * step)   // 前後方向も半マスずつ進める
                {
                    for (int j = 0; j < nLat; j++)
                    {
                        float dy = nLat == 1 ? 0f : -lane.halfWidth + 2f * lane.halfWidth * j / (nLat - 1);
                        Vector3 p = origin + x * fwd + (lane.y + dy) * left;
                        Vector2Int ui = SoilSimUtils.WorldToUnityIndex(terrain, p);
                        if (ui.x < 0 || ui.y < 0 || ui.x >= res || ui.y >= res || visited.Contains(ui)) continue;

                        Vector3 center = SoilSimUtils.UnityIndexToWorld(terrain, ui.x, ui.y);
                        float cellY = Vector3.Dot(center - origin, left);
                        float rel = cellY - lane.y;            // 中心線からのマスの中心の横の位置
                        if (lane.halfWidth > 0 && Mathf.Abs(rel) > lane.halfWidth) continue;   // 中心が履帯の幅の外
                        visited.Add(ui);

                        string label = lane.halfWidth > 0
                            ? lane.name + rel.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)
                            : lane.name;
                        AppendCell(sb, t, phase, machineX, label, cellY, center, ui, res);
                    }
                }
            }

            var msg = new ProbeMessage
            {
                request_id = requestId,
                episode_name = episodeName,
                final = final,
                header = header,
                rows = sb.ToString()
            };
            ros.Publish(probeTopic, new StringMsg(JsonUtility.ToJson(msg)));
            snapshots++;
        }

        void AppendCell(StringBuilder sb, double t, string phase, float machineX, string label, float cellY,
                        Vector3 center, Vector2Int ui, int res)
        {
            int ax = res - 1 - ui.x, ay = res - 1 - ui.y;          // AGX のマス番号（SoilSimUtils と同じ対応）
            var a2 = new agx.Vec2i(ax, ay);
            float cellX = Vector3.Dot(center - origin, fwd);
            float height = SoilSimUtils.GroundHeight(terrain, center);
            double cSurf = grid.getSurfaceCompaction(a2);

            var fields = new List<string>
            {
                F(t), phase, F(machineX), label, F(cellY), F(cellX),
                ui.x.ToString(CultureInfo.InvariantCulture), ui.y.ToString(CultureInfo.InvariantCulture),
                F(height), F(cSurf)
            };

            if (miFindSurface != null && miGetCompaction != null)
            {
                int zs = Convert.ToInt32(miFindSurface.Invoke(grid, new object[] { a2 }));
                fields.Add(zs.ToString(CultureInfo.InvariantCulture));
                for (int k = 0; k < depthLayers; k++)
                {
                    object v = miGetCompaction.Invoke(grid, new object[] { new agx.Vec3i(ax, ay, zs - k) });
                    fields.Add(F(Convert.ToDouble(v)));
                }
            }
            else
            {
                fields.Add("NaN");
                for (int k = 0; k < depthLayers; k++) fields.Add("NaN");
            }
            // AppendLine は Windows では \r\n になるので、改行は \n に揃える
            sb.Append(string.Join(",", fields)).Append('\n');
        }

        static string F(double v) => v.ToString("G7", CultureInfo.InvariantCulture);
    }
}