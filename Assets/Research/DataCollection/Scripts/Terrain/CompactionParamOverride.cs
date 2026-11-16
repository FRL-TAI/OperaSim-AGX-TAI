using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using AGXUnity;
using AGXUnity.Model;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// 締固めのパラメータ（Hardening Constant KE・NE）を、AGX 本体の材料だけ実行時に上書きする（実験B3 ③用）。
    /// 材料のアセットは書き換えない。コンポーネントを無効にすれば元の値のまま動く。
    /// シーンを読み込み直すたびに（エピソードごとに）最初のステップで適用し、読み戻した値を Console と記録ファイルに残す。
    /// </summary>
    public class CompactionParamOverride : MonoBehaviour
    {
        [SerializeField] DeformableTerrain terrain;
        [SerializeField] double hardeningKE = 1.0;        // dirt_1 の値：1
        [SerializeField] double hardeningNE = 0.0833;     // dirt_1 の値：0.0833
        [SerializeField] string conditionName = "baseline";
        [SerializeField] string logFile = "ResearchLogs/compaction/hardening_log.csv";

        bool applied = false;

        void Start()
        {
            Simulation.Instance.StepCallbacks.PostStepForward += OnPostStep;
        }

        void OnDestroy()
        {
            if (Simulation.HasInstance)
                Simulation.Instance.StepCallbacks.PostStepForward -= OnPostStep;
        }

        void OnPostStep()
        {
            if (applied || terrain == null || terrain.Native == null) return;
            var comp = terrain.Native.getTerrainMaterial().getCompactionProperties();
            double ke0 = comp.getHardeningConstantKE(), ne0 = comp.getHardeningConstantNE();
            comp.setHardeningConstantKE(hardeningKE);
            comp.setHardeningConstantNE(hardeningNE);
            double ke1 = comp.getHardeningConstantKE(), ne1 = comp.getHardeningConstantNE();
            applied = true;

            string msg = $"CompactionParamOverride（{conditionName}）: KE {ke0} → {ke1}、NE {ne0} → {ne1}";
            Debug.Log(msg);
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), logFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool header = !File.Exists(path);
                using (var w = new StreamWriter(path, true))
                {
                    if (header) w.WriteLine("datetime,condition,KE_before,NE_before,KE_after,NE_after");
                    w.WriteLine(string.Join(",", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), conditionName,
                        ke0.ToString(CultureInfo.InvariantCulture), ne0.ToString(CultureInfo.InvariantCulture),
                        ke1.ToString(CultureInfo.InvariantCulture), ne1.ToString(CultureInfo.InvariantCulture)));
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"CompactionParamOverride: 記録ファイルに書けません: {e.Message}");
            }
        }
    }
}