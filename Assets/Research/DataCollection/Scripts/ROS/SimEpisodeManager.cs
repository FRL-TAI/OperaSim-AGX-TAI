using System;
using UnityEngine;
using AGXUnity.Model;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Std;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// データ収集のエピソード管理。
    ///
    /// 受信 /sim/episode_cmd （std_msgs/String, JSON）
    ///   {"command":"setup", "request_id":…, "episode_name":…, "episode_index":…, "total_episodes":…,
    ///    "soil":{"cohesion":Pa, "friction_angle_deg":deg, "density":kg/m3, "swell_factor":…, "youngs_modulus":Pa},
    ///    "terrain_ops":[{"type":"compaction_rect", "x_start":…, "x_end":…, "y_min":…, "y_max":…, "value":…, "depth":…}]}
    ///     → 土質パラメータを設定 → 粒子を削除 → 地形の高さ（と締固め）をリセット → 地形を加工
    ///     soil の負の値は「変更しない」。terrain_ops の座標は開始時の刃先中点が原点、x:前方, y:左方 [m]
    ///   {"command":"phase", "phase":"running"}  → 表示用の進行状況だけ更新
    ///
    /// 送信 /sim/episode_status （std_msgs/String, JSON）, 既定 5 Hz と、指令の処理直後
    ///   AGX 本体から読み戻した土質パラメータ、地形の加工の結果、直近に処理した request_id など
    ///
    /// terrain_ops（締固めの操作）は Scripting Define Symbols に AGX_GRID_CONTROL を定義したときだけ有効。
    /// </summary>
    public class SimEpisodeManager : MonoBehaviour
    {
        [Serializable] public class SoilSpec
        {
            public double cohesion = -1, friction_angle_deg = -1, density = -1, swell_factor = -1, youngs_modulus = -1;
        }

        [Serializable] public class TerrainOp
        {
            public string type;
            public float x_start, x_end, y_min, y_max, value, depth;
        }

        [Serializable] public class EpisodeCommand
        {
            public string command;
            public int request_id;
            public string episode_name;
            public int episode_index;
            public int total_episodes;
            public string phase;
            public SoilSpec soil;
            public TerrainOp[] terrain_ops;
        }

        [Serializable] public class EpisodeStatus
        {
            public int request_id;
            public string episode_name;
            public int episode_index;
            public int total_episodes;
            public string phase;
            public double cohesion, friction_angle_deg, density, swell_factor, youngs_modulus;
            public int ops_requested;
            public bool ops_applied;
            public int ops_cells;
            public bool grid_control_enabled;
            public int reset_count;
            public double sim_time;
            public string error;
        }

        [SerializeField] DeformableTerrain terrain;
        [SerializeField] DeformableTerrainShovel shovel;   // 地形の加工の原点（刃先）に使う
        [SerializeField] Transform referenceFrame;         // base_link（前方向に使う）
        [SerializeField] string cmdTopic = "/sim/episode_cmd";
        [SerializeField] string statusTopic = "/sim/episode_status";
        [SerializeField] float statusRate = 5f;

        public EpisodeStatus Status { get; } = new EpisodeStatus { request_id = -1, episode_name = "-", episode_index = -1, phase = "idle", error = "" };

        public static bool GridControlEnabled
        {
#if AGX_GRID_CONTROL
            get { return true; }
#else
            get { return false; }
#endif
        }

        ROSConnection ros;
        float lastStatusTime = -999f;

        void Start()
        {
            ros = ROSConnection.GetOrCreateInstance();
            ros.Subscribe<StringMsg>(cmdTopic, OnEpisodeCmd);
            ros.RegisterPublisher<StringMsg>(statusTopic);
            Status.grid_control_enabled = GridControlEnabled;
        }

        void OnEpisodeCmd(StringMsg msg)
        {
            try
            {
                var cmd = JsonUtility.FromJson<EpisodeCommand>(msg.data);
                switch (cmd.command)
                {
                    case "setup":
                        if (cmd.request_id == Status.request_id) return;   // 再送された同じ指令は無視
                        Setup(cmd);
                        Status.request_id = cmd.request_id;
                        break;
                    case "phase":
                        Status.phase = cmd.phase;
                        break;
                    default:
                        Debug.LogWarning($"SimEpisodeManager: 不明なコマンド {cmd.command}");
                        break;
                }
            }
            catch (Exception e)
            {
                Status.error = e.Message;
                Debug.LogException(e);
            }
            PublishStatus();
        }

        void Setup(EpisodeCommand cmd)
        {
            if (terrain == null || terrain.Native == null)
                throw new InvalidOperationException("地形が初期化されていません");

            Status.error = "";
            Status.episode_name = cmd.episode_name;
            Status.episode_index = cmd.episode_index;
            Status.total_episodes = cmd.total_episodes;
            Status.phase = string.IsNullOrEmpty(cmd.phase) ? "setup" : cmd.phase;

            ApplySoil(cmd.soil);
            RemoveAllParticles();
            terrain.ResetHeights();   // 内部で setHeights を呼ぶ。締固めもリセットされる（AGX マニュアル FAQ）
            Status.reset_count++;

            var ops = cmd.terrain_ops ?? new TerrainOp[0];
            Status.ops_requested = ops.Length;
            Status.ops_cells = 0;
            Status.ops_applied = ops.Length == 0 || ApplyTerrainOps(ops);
        }

        void ApplySoil(SoilSpec s)
        {
            if (s == null) return;
            // Inspector の材料アセットは書き換えず、AGX 本体の材料だけを変更する（角度はラジアン）
            var bulk = terrain.Native.getTerrainMaterial().getBulkProperties();
            if (s.cohesion >= 0) bulk.setCohesion(s.cohesion);
            if (s.friction_angle_deg >= 0) bulk.setFrictionAngle(s.friction_angle_deg * Mathf.Deg2Rad);
            if (s.density > 0) bulk.setDensity(s.density);
            if (s.swell_factor > 0) bulk.setSwellFactor(s.swell_factor);
            if (s.youngs_modulus > 0) bulk.setYoungsModulus(s.youngs_modulus);
        }

        void RemoveAllParticles()
        {
            // AGX マニュアル 23.22（FAQ）の方法
            var soil = terrain.GetSoilSimulationInterface();
            var particles = terrain.GetParticles();
            if (soil == null || particles == null) return;

            // AGX の配列は size() で要素数、at(i) で i 番目の要素を取り出す
            // （AGXUnity の DeformableTerrainParticleRenderer.cs と同じ扱い）
            int n = (int)particles.size();
            for (int i = n - 1; i >= 0; i--)
                soil.removeSoilParticle(particles.at((uint)i));
        }

        bool ApplyTerrainOps(TerrainOp[] ops)
        {
#if AGX_GRID_CONTROL
            var grid = terrain.Native.getTerrainGridControl();
            Vector3 origin = SoilSimUtils.CuttingEdge(shovel, out _, out _);
            Vector3 fwd = referenceFrame.forward;
            float swell = (float)terrain.Native.getTerrainMaterial().getBulkProperties().getSwellFactor();
            foreach (var op in ops)
            {
                int n;
                switch (op.type)
                {
                    case "compaction_rect":
                        // TODO(要確認): 2.37 での名前と引数
                        n = SoilSimUtils.ForEachCellInRect(terrain, origin, fwd, op.x_start, op.x_end, op.y_min, op.y_max,
                            idx => grid.setSurfaceCompactionToDepth(idx, op.value, op.depth));
                        break;
                    case "loose_layer_rect":
                        n = SoilSimUtils.ForEachCellInRect(terrain, origin, fwd, op.x_start, op.x_end, op.y_min, op.y_max,
                            idx => grid.addSolidOccupancyLayerInColum(idx, op.value, 1.0f / swell, true));
                        break;
                    default:
                        Status.error = $"不明な地形の加工: {op.type}";
                        return false;
                }
                if (n == 0)
                {
                    Status.error = $"{op.type} の範囲に地形のマスがありません（地形の外？）";
                    return false;
                }
                Status.ops_cells += n;
            }
            return true;
#else
            Status.error = "AGX_GRID_CONTROL が無効なため地形の加工を適用できません";
            return false;
#endif
        }

        void Update()
        {
            if (Time.time - lastStatusTime < 1f / statusRate) return;
            PublishStatus();
        }

        void PublishStatus()
        {
            lastStatusTime = Time.time;
            ReadBack();
            ros.Publish(statusTopic, new StringMsg(JsonUtility.ToJson(Status)));
        }

        /// <summary>AGX 本体の現在の材料パラメータを Status に読み戻す</summary>
        void ReadBack()
        {
            Status.sim_time = Time.fixedTimeAsDouble;
            if (terrain == null || terrain.Native == null) return;
            var bulk = terrain.Native.getTerrainMaterial().getBulkProperties();
            Status.cohesion = bulk.getCohesion();
            Status.friction_angle_deg = bulk.getFrictionAngle() * Mathf.Rad2Deg;
            Status.density = bulk.getDensity();
            Status.swell_factor = bulk.getSwellFactor();
            Status.youngs_modulus = bulk.getYoungsModulus();
        }
    }
}
