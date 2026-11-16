using System.Collections;
using UnityEngine;

public class MassReport : MonoBehaviour
{
    [SerializeField] Transform machineRoot;

    IEnumerator Start()
    {
        // AGXUnity の初期化とシミュレーションの開始を待つ
        yield return new WaitForSeconds(1.0f);

        double total = 0.0;
        int missing = 0;
        
        var root = machineRoot != null ? machineRoot : transform;
        foreach (var rb in root.GetComponentsInChildren<AGXUnity.RigidBody>())
        {
            if (rb.Native == null)
            {
                Debug.LogWarning($"{rb.name}: Native is null (not initialized)");
                missing++;
                continue;
            }
            double m = rb.Native.getMassProperties().getMass();
            Debug.Log($"{rb.name}: {m:F1} kg");
            total += m;
        }
        Debug.Log($"Total mass: {total:F1} kg (uninitialized bodies: {missing})");
    }
}