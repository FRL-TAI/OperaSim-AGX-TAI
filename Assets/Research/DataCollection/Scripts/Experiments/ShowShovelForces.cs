using UnityEngine;
using AGXUnity.Model;

/// <summary>
/// 検証用（Experiments に付ける）。AGXUnity の Shovel forces ウィンドウ（Inspector に出ない TempDisplayShovelForces）を、
/// コンテキストメニュー「Toggle Shovel Forces」で開閉する。
/// </summary>
public class ShowShovelForces : MonoBehaviour
{
    [SerializeField] DeformableTerrain terrain;

    [ContextMenu("Toggle Shovel Forces")]
    void Toggle() { terrain.TempDisplayShovelForces = !terrain.TempDisplayShovelForces; }
}