using System.Collections;
using System.Linq;
using UnityEngine;
using AGXUnity.Model;

/// <summary>
/// 検証用（Experiments に付ける）。Play の開始から1秒後に、履帯の車輪の種類・半径・位置と、
/// 転輪（Roller）の前後の間隔（接地長の目安）を Console に出す。track が空なら、このオブジェクトの Track を使う。
/// </summary>
public class TrackWheelReport : MonoBehaviour
{
    [SerializeField] Track track;

    IEnumerator Start()
    {
        yield return new WaitForSeconds(1.0f);

        var tr = track != null ? track : GetComponent<Track>();
        if (tr == null)
        {
            Debug.LogWarning("TrackWheelReport: Track が設定されていません");
            yield break;
        }
        Vector3 forward = tr.transform.root.forward;   // 機体の前方向

        foreach (var w in tr.Wheels)
        {
            Debug.Log($"{w.name}: model = {w.Model}, radius = {w.Radius:F3} m, pos = {w.transform.position}");
        }

        var rollers = tr.Wheels.Where(w => w.Model == TrackWheelModel.Roller).ToList();
        if (rollers.Count >= 2)
        {
            var proj = rollers.Select(w => Vector3.Dot(w.transform.position, forward)).ToList();
            float L = proj.Max() - proj.Min();
            Debug.Log($"Roller span (approx. contact length): {L:F3} m");
        }
    }
}