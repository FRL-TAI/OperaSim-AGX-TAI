# SoilSimUtils：データ収集用の共通処理

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Terrain/SoilSimUtils.cs` |
| 種類 | 静的クラス（コンポーネントではないので、オブジェクトには取り付けない） |


## 2. 目的
排土板の刃先の位置や地形の高さの取得、地形のマス番号の変換など、複数のスクリプトで使う処理を1か所にまとめ、計算方法を統一する。

特に、AGX の地形のマス番号は Unity の TerrainData のマス番号と向きが反転しているため、その変換を各スクリプトで別々に書くと間違いの原因になる。この変換をここに集約する。


## 3. 実装の内容
| メソッド | 内容 |
| --- | --- |
| `CuttingEdge(shovel, out start, out end)` | 刃先（Cutting Edge）の両端と中点のワールド座標を返す。AGXUnity の `DeformableTerrainShovel.CuttingEdge` の `Start` / `End` の位置を使う |
| `TopEdgeMid(shovel)` | 排土板の上端（Top Edge）の中点のワールド座標を返す |
| `Flat(v)` | ベクトルを水平面に投影し、長さ 1 にして返す（車体の前方向を水平に直すのに使う） |
| `GroundHeight(terrain, world)` | ワールド座標 (x, z) の地表の高さ（ワールド y）を返す。Unity の `Terrain.SampleHeight` に地形の位置を足したもの |
| `UnityIndexToWorld(terrain, ix, iy)` | Unity の TerrainData のマス番号から、ワールド座標（y = 0）を返す |
| `WorldToUnityIndex(terrain, world)` | ワールド座標から、最も近い Unity の TerrainData のマス番号を返す |
| `UnityToAgxIndex(terrain, ix, iy)` | Unity のマス番号を AGX のマス番号に変換する |
| `ForEachCellInRect(terrain, origin, forward, x0, x1, y0, y1, action)` | 原点と前方向を基準とした長方形（前方 x0〜x1、左方 y0〜y1 [m]）に入る地形のマスについて、AGX のマス番号を渡して `action` を呼ぶ。呼んだマスの数を返す |

### マス番号の対応
AGX の地形のマス番号は、Unity の TerrainData のマス番号と次の関係にある。AGXUnity の `DeformableTerrain.cs`（`UpdateHeights`）の対応と同じ。

```
AGX の番号 = 解像度 − 1 − Unity の番号   （x、y それぞれ）
```

Unity の TerrainData のマス番号 (ix, iy) は、ix がワールドの x 方向、iy がワールドの z 方向に対応する。

### 長方形の範囲の扱い（`ForEachCellInRect`）
1. 長方形の4隅のワールド座標をマス番号に変換し、探索する番号の範囲を決める（1マス分の余裕を持たせる）。
2. その範囲の各マスについて、ワールド座標を求め、原点からの前方・左方の距離を計算する。
3. 長方形の内側にあるマスだけを対象にする。

前方向は水平面に投影してから使い、左方向は Unity の左手系に合わせて `−Cross(上, 前)` で求める。


## 4. 使い方
コンポーネントではないので、他のスクリプトから次のように呼び出す。

```csharp
Vector3 edgeMid = SoilSimUtils.CuttingEdge(shovel, out var edgeStart, out var edgeEnd);
float ground = SoilSimUtils.GroundHeight(terrain, edgeMid);

int n = SoilSimUtils.ForEachCellInRect(terrain, edgeMid, referenceFrame.forward,
    0f, 1.0f, -1.5f, 1.5f,
    idx => { /* idx は AGX のマス番号（agx.Vec2i） */ });
```

名前空間は `PWRISimulator.ROS`。


## 5. 注意点
- `CuttingEdge` が返す位置が排土板の実際の刃先と合っているかは、Scene ビューでの Shovel の表示や、データの点検（刃先の高さと地面の高さの関係）で確認すること。
- 地形は Unity の Terrain の仕様上回転しない前提で、マス番号の変換を行っている。
- `GroundHeight` は Unity の TerrainData の高さを使う。AGX の地形の変化は AGXUnity が毎ステップ TerrainData に反映しているため、変形後の高さが得られる。
