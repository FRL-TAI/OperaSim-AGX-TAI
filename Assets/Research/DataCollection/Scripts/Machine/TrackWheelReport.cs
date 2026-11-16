using System.Collections;
using System.Linq;
using UnityEngine;
using AGXUnity.Model;

public class TrackWheelReport : MonoBehaviour
{
    [SerializeField] Track track;
    
    IEnumerator Start()
    {
        yield return new WaitForSeconds(1.0f);

        var tr = track != null ? track : GetComponent<Track>();
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