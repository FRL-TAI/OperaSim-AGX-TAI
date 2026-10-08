using UnityEngine;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// Play 中、Game ビューの右下にデータ収集の実行状況を表示する。
    /// 表示する土質パラメータは、AGX 本体から読み戻した値（SimEpisodeManager.Status）。
    /// </summary>
    public class EpisodeStatusOverlay : MonoBehaviour
    {
        [SerializeField] SimEpisodeManager manager;
        [SerializeField] int fontSize = 16;
        [SerializeField] Vector2 size = new Vector2(380, 240);
        [SerializeField] float margin = 12f;

        GUIStyle style;

        void OnGUI()
        {
            if (manager == null) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = fontSize,
                    richText = true,
                    padding = new RectOffset(12, 12, 10, 10)
                };
                style.normal.textColor = Color.white;
            }

            var s = manager.Status;
            string index = s.episode_index >= 0 ? $"{s.episode_index + 1} / {s.total_episodes}" : "-";
            string ops = SimEpisodeManager.GridControlEnabled ? "有効" : "無効";
            string text =
                $"<b>データ収集</b>\n" +
                $"エピソード  {index}\n" +
                $"名前        {s.episode_name}\n" +
                $"状態        <b>{s.phase}</b>\n" +
                $"粘着力 c    {s.cohesion:F0} Pa\n" +
                $"摩擦角 φ    {s.friction_angle_deg:F1} deg\n" +
                $"密度 ρ      {s.density:F0} kg/m³\n" +
                $"膨張率      {s.swell_factor:F2}\n" +
                $"地形の加工  {s.ops_requested} 件 / {s.ops_cells} マス（締固め操作: {ops}）\n" +
                $"時刻        {s.sim_time:F1} s";
            if (!string.IsNullOrEmpty(s.error))
                text += $"\n<color=#ff8080>エラー: {s.error}</color>";

            var rect = new Rect(Screen.width - size.x - margin, Screen.height - size.y - margin, size.x, size.y);
            GUI.Box(rect, text, style);
        }
    }
}
