using UnityEngine;
using AGXUnity.Model;

public class ShowShovelForces : MonoBehaviour
{
    [SerializeField] DeformableTerrain terrain;

    [ContextMenu("Toggle Shovel Forces")]
    void Toggle() { terrain.TempDisplayShovelForces = !terrain.TempDisplayShovelForces; }
}