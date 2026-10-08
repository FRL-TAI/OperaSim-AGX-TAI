using System;
using UnityEngine;
using AGXUnity.Model;

namespace PWRISimulator.ROS
{
    /// <summary>
    /// 土質データ収集用スクリプトで共通に使う処理。
    ///
    /// マス番号について：AGX の地形の番号は Unity の TerrainData の番号と反転している
    /// （agx = resolution - 1 - unity）。DeformableTerrain.cs の UpdateHeights と同じ対応。
    /// </summary>
    public static class SoilSimUtils
    {
        /// <summary>刃先（Cutting Edge）の両端と中点（ワールド座標）</summary>
        public static Vector3 CuttingEdge(DeformableTerrainShovel shovel, out Vector3 start, out Vector3 end)
        {
            // TODO(要確認): AGXUnity の Line / IFrame のプロパティ名
            start = shovel.CuttingEdge.Start.Position;
            end = shovel.CuttingEdge.End.Position;
            return 0.5f * (start + end);
        }

        public static Vector3 TopEdgeMid(DeformableTerrainShovel shovel)
        {
            return 0.5f * (shovel.TopEdge.Start.Position + shovel.TopEdge.End.Position);
        }

        /// <summary>水平面に投影した単位ベクトル</summary>
        public static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }

        /// <summary>ワールド座標 (x, z) の地表の高さ（ワールド y）</summary>
        public static float GroundHeight(DeformableTerrain terrain, Vector3 world)
        {
            var t = terrain.Terrain;
            return t.SampleHeight(world) + t.transform.position.y;
        }

        /// <summary>Unity の TerrainData のマス番号 (ix, iy) のワールド位置（y は 0）</summary>
        public static Vector3 UnityIndexToWorld(DeformableTerrain terrain, int ix, int iy)
        {
            var size = terrain.TerrainData.size;
            var pos = terrain.Terrain.transform.position;
            int res = terrain.TerrainDataResolution;
            return new Vector3(pos.x + size.x * ix / (res - 1), 0f, pos.z + size.z * iy / (res - 1));
        }

        public static Vector2Int WorldToUnityIndex(DeformableTerrain terrain, Vector3 world)
        {
            var size = terrain.TerrainData.size;
            var pos = terrain.Terrain.transform.position;
            int res = terrain.TerrainDataResolution;
            return new Vector2Int(Mathf.RoundToInt((world.x - pos.x) / size.x * (res - 1)),
                                  Mathf.RoundToInt((world.z - pos.z) / size.z * (res - 1)));
        }

        public static agx.Vec2i UnityToAgxIndex(DeformableTerrain terrain, int ix, int iy)
        {
            int res = terrain.TerrainDataResolution;
            return new agx.Vec2i(res - 1 - ix, res - 1 - iy);
        }

        /// <summary>
        /// 原点 origin・前方 forward を基準とした長方形（前方 x0〜x1、左方 y0〜y1 [m]）に入る
        /// 地形のマスについて action(AGXのマス番号) を呼ぶ。呼んだマスの数を返す。
        /// </summary>
        public static int ForEachCellInRect(DeformableTerrain terrain, Vector3 origin, Vector3 forward,
                                            float x0, float x1, float y0, float y1, Action<agx.Vec2i> action)
        {
            Vector3 fwd = Flat(forward);
            Vector3 left = -Vector3.Cross(Vector3.up, fwd);   // Unity は左手系
            int res = terrain.TerrainDataResolution;

            // 長方形の4隅からマス番号の探索範囲を決める
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var (a, b) in new[] { (x0, y0), (x0, y1), (x1, y0), (x1, y1) })
            {
                var idx = WorldToUnityIndex(terrain, origin + a * fwd + b * left);
                minX = Math.Min(minX, idx.x); maxX = Math.Max(maxX, idx.x);
                minY = Math.Min(minY, idx.y); maxY = Math.Max(maxY, idx.y);
            }
            minX = Mathf.Clamp(minX - 1, 0, res - 1); maxX = Mathf.Clamp(maxX + 1, 0, res - 1);
            minY = Mathf.Clamp(minY - 1, 0, res - 1); maxY = Mathf.Clamp(maxY + 1, 0, res - 1);

            int count = 0;
            for (int iy = minY; iy <= maxY; iy++)
            {
                for (int ix = minX; ix <= maxX; ix++)
                {
                    Vector3 d = UnityIndexToWorld(terrain, ix, iy) - origin;
                    d.y = 0f;
                    float lx = Vector3.Dot(d, fwd);
                    float ly = Vector3.Dot(d, left);
                    if (lx < x0 || lx > x1 || ly < y0 || ly > y1) continue;
                    action(UnityToAgxIndex(terrain, ix, iy));
                    count++;
                }
            }
            return count;
        }
    }
}
