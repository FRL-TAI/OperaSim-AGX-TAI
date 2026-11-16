using System.Collections;
using UnityEngine;

/// <summary>
/// 検証用（Experiments に付ける）。Play の開始から1秒後に、machineRoot 以下の剛体の質量と合計を Console に出す。
/// machineRoot が空なら、このオブジェクト以下を調べる。
/// </summary>
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