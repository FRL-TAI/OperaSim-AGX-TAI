# BladeStatePublisher：排土板まわりの状態の配信

## 1. 該当ファイル
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
- AGX の1ステップの計算が終わった直後（`Simulation.Instance.StepCallbacks.PostStepForward`）に値を読み出し、`publishRate`（既定 50 Hz）の間隔で配信する。時間刻み（0.02 s）は浮動小数点の誤差でわずかに短くなることがあるため、半ステップ分の余裕を持たせて間隔を判定している（余裕がないと1ステップおきに飛ばされて 25 Hz になる）。
- 破棄されるとき（シーンの読み込み直し・Play の停止）は、`OnDestroy` で `Simulation.HasInstance` を確かめてから毎ステップの処理の登録を外す（`Simulation.Instance` と書くと、破棄済みの `Simulation` を作り直してしまうため）。
- すべての値を1つの `Float64MultiArray` にまとめ、**各値の名前を `layout.dim[0].label` にカンマ区切りで入れる**。受信側は名前で値を取り出せるので、項目を追加しても受信側の修正は不要。
- 排土板の反力は、AGXUnity の `DeformableTerrain.ShowForces()` と同じ API で取得する。**4つの力はすべて「排土板が土から受ける力」の向きにそろえ、符号を反転しない。** AGXUnity の `ShowForces()` は Separation・Deformation・Contact の3つだけを反転して表示しているが、そのまま使うと Penetration と向きが逆になるため（関門1で、履帯の駆動力との相関から確認して修正）。

### 座標系
| 対象 | 座標系 |
| --- | --- |
| 力・トルク | 車体（`referenceFrame` = `base_link`）に固定した座標。ROS の慣例（FLU）に合わせ、**x：前方、y：左方、z：上方** |
| 位置 | ワールド座標を ROS の並びにしたもの（x = Unity の z、y = −Unity の x、z = Unity の y）。高さ（z）は Unity のワールド y と同じ値 |

### 配信する値
| 名前 | 単位 | 内容 |
| --- | --- | --- |
| `sim_time` | s | シミュレーション時刻 |
| `F_total_x/y/z` | N | 下の4つの力の合計（排土板が土から受ける力の合力）。土を押して前進中は `F_total_x` が負 |
| `F_pen_x/y/z` | N | 貫入抵抗。刃先が削られていない地面に食い込むときの抵抗 |
| `F_sep_x/y/z` | N | 分離抵抗。前方へ切削・押土するとき、排土板の前の土塊から受ける力。前進時に支配的 |
| `F_def_x/y/z` | N | 変形抵抗。後退や横方向など、切削方向以外に土を押しのけるときの力（このシーンでは常に 0。下の「注意点」を参照） |
| `F_con_x/y/z` | N | 通常の接触力。掘削モードが働いていないときに、排土板の面が地面に触れる力（このシーンでは常に 0。下の「注意点」を参照） |
| `T_pen_x/y/z` | N·m | 貫入抵抗のトルク（参考値。座標変換を力と同じ方法で行っている） |
| `edge_x/y/z` | m | 刃先（Cutting Edge）の中点の位置。`edge_z` が刃先の高さ |
| `blade_width` | m | 刃先の両端の距離（排土板の幅） |
| `blade_alpha_deg` | deg | 排土板の角度 α。刃先から上端（Top Edge）へのベクトルが、水平（車体の後ろ向き）となす角。90° で垂直 |
| `ground_below` | m | 刃先の真下の地面の高さ。削っている間は、まだ切り取られていない土と、排土板の前にたまった土の山の表面の高さになる（削った後の地面ではない） |
| `ground_behind` | m | 刃先から `behindDistance`（既定 0.3 m）後ろの地面の高さ。削っている間は、削った後の地面の高さ（刃先の高さとほぼ同じ）になる |
| `ground_ahead` | m | 刃先から `aheadDistance`（既定 0.5 m）前方の地面の高さ。切削深さの基準に使う |
| `ground_profile_025cm` 〜 `ground_profile_200cm` | m | 刃先から `profileStep`（既定 0.25 m）ごとに `profileCount`（既定 8）点の、前方の地面の高さ |
| `front_soil_mass` | kg | 排土板の前の土塊の質量（`AGX_EXTRA_TERRAIN_API` 有効時のみ。無効時は NaN） |
| `excavated_volume_step` | m³ | そのステップで掘り起こされた体積（同上） |
| `state_compaction_mean` | 無次元 | 刃先から前方 `wedgeLength`（既定 1 m）、排土板の幅の範囲の、地表の締固めの程度の平均（`AGX_GRID_CONTROL` 有効時のみ） |
| `state_compaction_std` | 無次元 | 同じ範囲の締固めの程度の標準偏差（同上） |
| `state_compaction_n` | 個 | 平均に使った地形のマスの数（同上。無効時は 0） |

### オプション機能（Scripting Define Symbols）
AGX Dynamics 2.37.3.0（このプロジェクトで使っている版）で API が使えるか確かめながら導入したものは、Player Settings の Scripting Define Symbols に次のシンボルを定義したときだけコンパイルされる。定義していない場合、該当する値は NaN（`state_compaction_n` は 0）になる。

| シンボル | このプロジェクトでの状態 |
| --- | --- |
| `AGX_GRID_CONTROL` | 有効（`state_compaction_*` に値が入る。`SimEpisodeManager` の地形の加工も使える） |
| `AGX_EXTRA_TERRAIN_API` | 無効（`front_soil_mass`、`excavated_volume_step` は NaN） |


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
| Behind Distance | `ground_behind` を測る距離（刃先の後ろ）[m] | 0.3 |
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
- AGX Dynamics 2.37.3.0 には、締固めの程度に応じたダイレイタンシー角の計算に不具合がある可能性がある（2.38.0.0 で修正、AGX の変更履歴 `61b41b0f3a`）。削る・押すうちに締固めの程度が変わるので、反力に影響している可能性がある
- **`ground_below` と `ground_behind` の違い：** AGX の地形は 0.3125 m のマスで高さを持つ。刃先の真下のマスには、まだ切り取られていない土と、排土板の前にたまった土の山が乗っているので、`ground_below` は削っている間、刃先より 5〜7 cm（最大 40 cm）高く、刃先が1マス進むごとに上がっては落ちる「のこぎり波」になる。削った後の地面の高さが必要な場合は `ground_behind` を使う（関門3で、`ground_behind` − `edge_z` の中央値が +0.1〜+0.3 cm になることを確認）。`behindDistance` を 0.3 m より小さくすると、削っている途中のマスを測ることがある
- **`F_def_*` と `F_con_*` は常に 0：** 直進・旋回・後退のどの動きでも 0 だった。排土板を地面に 2 cm 当てて 5 m 以上後退しても、通った跡の地面は削られず、力の大きさの中央値は 0 N（接触点の表示でも、排土板にはまれにしか接触点が出ない）。前に削る動き（掘削のモデル）以外では、排土板が地形に入っても土はほとんど動かず、力もほとんど生じない。前に削る動きの力は `F_pen` と `F_sep` で表される。排土板の `DeformableTerrainShovel` の Settings は None（AGX の既定値）で、排土板と地形の衝突は Collision Groups で切られていない（`bull_ope` の関門3）
- `ShowShovelForces` のウィンドウとは、Separation・Deformer・Contact の符号が逆になる（上記）。比べるときは大きさで比べる。
- このスクリプトの配信はシーンの読み込み直しのたびに作り直される。読み込み直しの直後、AGX の初期化が終わるまでの短い間は配信されない（`bull_ope` は新しいシーンのトピックがそろうまで待つ）。
