using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Robotics.ROSTCPConnector;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace PWRISimulator.ROS
{
    /// <summary>
    /// Play 中に今のシーンを読み込み直して、機体・地形・AGX の状態をすべて初期状態に戻す。
    ///
    /// 使い方：
    ///   試作の確認：Inspector の「⋮」→「Reload Scene」
    ///   ROS から：SimEpisodeManager が {"command":"reload"} を受け取ると SceneReloader.Reload() を呼ぶ
    ///
    /// static の変数はシーンを読み込み直しても残るので、読み込み直しの回数と所要時間を記録できる。
    /// </summary>
    public class SceneReloader : MonoBehaviour
    {
        static float reloadStartedAt = -1f;   // 読み込み直しを始めた実時間 [s]
        static int reloadCount = 0;            // Play 開始からの読み込み直しの回数

        public static int ReloadCount => reloadCount;

        void Start()
        {
            // 新しいシーンで Start が呼ばれた = 読み込み直しが終わった
            if (reloadStartedAt >= 0f)
            {
                float elapsed = Time.realtimeSinceStartup - reloadStartedAt;
                Debug.Log($"SceneReloader: 読み込み直しが完了しました（{reloadCount} 回目、{elapsed:F2} 秒）");
                reloadStartedAt = -1f;
            }
        }

        [ContextMenu("Reload Scene")]
        void ReloadFromMenu()
        {
            Reload();
        }

        /// <summary>今のシーンを読み込み直す（実際の読み込みは次のフレームで行われる）</summary>
        public static void Reload()
        {
            if (reloadStartedAt >= 0f)
            {
                Debug.LogWarning("SceneReloader: 読み込み直しの途中なので無視します");
                return;
            }
            reloadStartedAt = Time.realtimeSinceStartup;
            reloadCount++;

            var scene = SceneManager.GetActiveScene();
            Debug.Log($"SceneReloader: シーン {scene.name} を読み込み直します（{reloadCount} 回目）");

            // ROSConnection（シーンの一部）は読み込み直しで破棄されるが、ROS-TCP-Connector は
            // 破棄のときに接続を閉じない（OnDestroy がなく、Disconnect は OnApplicationQuit でしか呼ばれない）。
            // 閉じないと古い接続が ROS-TCP-Endpoint に残り続け、ROS → Unity の指令が詰まることがあるので、
            // 読み込み直しの前に明示的に閉じる（関門1の段階Dで、10回の読み込み直しで接続が7本に増えることを確認）
            foreach (var ros in Object.FindObjectsOfType<ROSConnection>())
                ros.Disconnect();

            if (scene.buildIndex >= 0)
            {
                // Build Settings に登録されているシーン
                SceneManager.LoadScene(scene.buildIndex, LoadSceneMode.Single);
            }
            else
            {
#if UNITY_EDITOR
                // Build Settings に登録されていなくても、エディタではパスで読み込める
                EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
                Debug.LogError("SceneReloader: シーンを Build Settings に登録してください");
                reloadStartedAt = -1f;
#endif
            }
        }
    }
}