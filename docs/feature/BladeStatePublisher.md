# BladeStatePublisher：排土板まわりの状態の配信

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/ROS/BladeStatePublisher.cs` |
| 依存するファイル | `Assets/Research/DataCollection/Scripts/Terrain/SoilSimUtils.cs` |
| 取り付け先 | シーンの `DataCollection` オブジェクト |
| 配信するトピック | `/d37pxi_24/blade_state`（`std_msgs/Float64MultiArray`、50 Hz） |


## 2. 目的
土質パラメータを推定するニューラルネットワークの学習データとして、ブルドーザの排土板が土から受ける力と、その時点の排土板・地形の状態を、ROS2 から取得できるようにする。

OperaSim-AGX の既存のトピックには排土板の反力が含まれておらず、油圧のトピック（`main_fluid_pressure`）も常に 0 であるため、AGX Dynamics の地形モジュールから直接値を取り出して配信する。


## 3. 実装の内容

### 仕組み
- AGX の1ステップの計算が終わった直後（`Simulation.Instance.StepCallbacks.PostStepForward`）に値を読み出し、`publishRate`（既定 50 Hz）の間隔で配信する。
- すべての値を1つの `Float64MultiArray` にまとめ、**各値の名前を `layout.dim[0].label` にカンマ区切りで入れる**。受信側は名前で値を取り出せるので、項目を追加しても受信側の修正は不要。
- 排土板の反力は、AGXUnity の `DeformableTerrain.ShowForces()` と同じ API・同じ符号の扱いで取得する。

### 座標系
| 対象 | 座標系 |
| --- | --- |
| 力・トルク | 車体（`referenceFrame` = `base_link`）に固定した座標。ROS の慣例（FLU）に合わせ、**x：前方、y：左方、z：上方** |
| 位置 | ワールド座標を ROS の並びにしたもの（x = Unity の z、y = −Unity の x、z = Unity の y）。高さ（z）は Unity のワールド y と同じ値 |

### 配信する値
| 名前 | 単位 | 内容 |
| --- | --- | --- |
| `sim_time` | s | シミュレーション時刻 |
| `F_total_x/y/z` | N | 下の4つの力の合計（排土板にかかる力の合力） |
| `F_pen_x/y/z` | N | 貫入抵抗。刃先が削られていない地面に食い込むときの抵抗 |
| `F_sep_x/y/z` | N | 分離抵抗。前方へ切削・押土するとき、排土板の前の土塊から受ける力。前進時に支配的 |
| `F_def_x/y/z` | N | 変形抵抗。後退や横方向など、切削方向以外に土を押しのけるときの力 |
| `F_con_x/y/z` | N | 通常の接触力。掘削モードが働いていないときに、排土板の面が地面に触れる力 |
| `T_pen_x/y/z` | N·m | 貫入抵抗のトルク（参考値。座標変換を力と同じ方法で行っている） |
| `edge_x/y/z` | m | 刃先（Cutting Edge）の中点の位置。`edge_z` が刃先の高さ |
| `blade_width` | m | 刃先の両端の距離（排土板の幅） |
| `blade_alpha_deg` | deg | 排土板の角度 α。刃先から上端（Top Edge）へのベクトルが、水平（車体の後ろ向き）となす角。90° で垂直 |
| `ground_below` | m | 刃先の真下の地面の高さ |
| `ground_ahead` | m | 刃先から `aheadDistance`（既定 0.5 m）前方の地面の高さ。切削深さの基準に使う |
| `ground_profile_025cm` 〜 `ground_profile_200cm` | m | 刃先から `profileStep`（既定 0.25 m）ごとに `profileCount`（既定 8）点の、前方の地面の高さ |
| `front_soil_mass` | kg | 排土板の前の土塊の質量（`AGX_EXTRA_TERRAIN_API` 有効時のみ。無効時は NaN） |
| `excavated_volume_step` | m³ | そのステップで掘り起こされた体積（同上） |
| `state_compaction_mean` | 無次元 | 刃先から前方 `wedgeLength`（既定 1 m）、排土板の幅の範囲の、地表の締固めの程度の平均（`AGX_GRID_CONTROL` 有効時のみ） |
| `state_compaction_std` | 無次元 | 同じ範囲の締固めの程度の標準偏差（同上） |
| `state_compaction_n` | 個 | 平均に使った地形のマスの数（同上。無効時は 0） |

### オプション機能（Scripting Define Symbols）
AGX Dynamics 2.37.3.0 で API が使えるか未確認のものは、Player Settings の Scripting Define Symbols に次のシンボルを定義したときだけコンパイルされる。既定では無効で、該当する値は NaN（`state_compaction_n` は 0）になる。

| シンボル | 有効になる値 | 使う API |
| --- | --- | --- |
| `AGX_EXTRA_TERRAIN_API` | `front_soil_mass`, `excavated_volume_step` | `Terrain.getSoilParticleAggregate(shovel)`, `Terrain.getLastExcavatedVolume()` |
| `AGX_GRID_CONTROL` | `state_compaction_*` | `Terrain.getTerrainGridControl()`, `TerrainGridControl.getSurfaceCompaction()` |


## 4. 使い方

### 設定
`DataCollection` オブジェクトに追加し、Inspector で次を設定する。

| 欄 | 設定 | 既定値 |
| --- | --- | --- |
| Terrain | シーンの `Terrain` | — |
| Shovel | `d37pxi_24` > `base_link` > `body_link` > `blade_arm_link` > `blade_link` | — |
| Reference Frame | `d37pxi_24` > `base_link` | — |
| Ahead Distance | `ground_ahead` を測る距離 [m] | 0.5 |
| Profile Step / Profile Count | 前方の地面の高さを測る間隔 [m] と点数 | 0.25 / 8 |
| Wedge Length | 締固めの平均を取る前方の範囲 [m] | 1.0 |
| Topic Name | 配信するトピック | `/d37pxi_24/blade_state` |
| Publish Rate | 配信の頻度 [Hz] | 50 |

### 確認
Play 中に次を実行する。

```
ros2 topic echo /d37pxi_24/blade_state --once
ros2 topic hz /d37pxi_24/blade_state
```

`layout.dim[0].label` に値の名前の一覧が、`data` に同じ数の値が入っていれば正常。

### 受信側での取り出し方（Python）
```python
def on_blade_state(msg):
    names = msg.layout.dim[0].label.split(",")
    values = dict(zip(names, msg.data))
    fx = values["F_total_x"]
```

データ収集パッケージ `bull_ope` の `sim_interface.py` がこの方法で受信している。


## 5. 注意点

- 刃先の位置は AGXUnity の `DeformableTerrainShovel` の `CuttingEdge` / `TopEdge` から取得している（`SoilSimUtils.cs`）。値が排土板の実際の形と合っているかは、データの点検で確認する。
- 排土板の前でこぼれた土の粒子は、既定では排土板に力を返さない（AGX マニュアル 23.19）。そのため、反力に粒子からの力は含まれない。
- `ground_ahead` などの地面の高さは Unity の `Terrain.SampleHeight` で求めている。排土板の前の土の山が地形の高さに含まれるかどうかは、その土が地形に戻っているかによる。
- 地表の締固めの平均（`state_compaction_*`）は、こちらで定義した近似であり、AGX が内部で反力の計算に使う値の求め方とは一致しない。
