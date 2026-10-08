# SimEpisodeManager：データ収集のエピソード管理

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/ROS/SimEpisodeManager.cs` |
| 依存するファイル | `Assets/Research/DataCollection/Scripts/Terrain/SoilSimUtils.cs` |
| 取り付け先 | シーンの `DataCollection` オブジェクト |
| 受信するトピック | `/sim/episode_cmd`（`std_msgs/String`、JSON） |
| 配信するトピック | `/sim/episode_status`（`std_msgs/String`、JSON、5 Hz と指令の処理直後） |


## 2. 目的
学習データを大量に集めるため、「土質パラメータを設定し、地形を初期状態に戻して走行する」という1回分の試行（エピソード）を、ROS2 側から自動で繰り返せるようにする。

具体的には、ROS2 側のデータ収集パッケージ（`bull_ope`）からの指令を受けて、次を行う。

- AGX Dynamics の地形材料に、土質パラメータ（粘着力、内部摩擦角、密度など）を設定する。
- 前のエピソードで削った地形と、こぼれた土の粒子を元に戻す。
- 設定が実際に反映された値（AGX から読み戻した値）を ROS2 側へ返し、学習データのラベルとして使えるようにする。

## 3. 実装の内容

### 処理の流れ（setup 指令）
1. 受信した JSON を `JsonUtility` で読み込む。
2. 同じ `request_id` の指令がすでに処理済みなら無視する（ROS2 側は取りこぼし対策で同じ指令を1秒ごとに再送するため）。
3. 土質パラメータを AGX 本体の地形材料に設定する（`terrain.Native.getTerrainMaterial().getBulkProperties()` の各 set メソッド。角度はラジアンに変換）。Inspector の材料アセットは書き換えない。
4. こぼれた土の粒子をすべて削除する（AGX マニュアル 23.22 FAQ の方法）。
5. `DeformableTerrain.ResetHeights()` で地形の高さを初期状態に戻す。内部で AGX の `setHeights` が呼ばれ、締固めの程度もリセットされる。
6. 地形の加工（`terrain_ops`）があれば適用する（`AGX_GRID_CONTROL` 有効時のみ）。
7. 状態を AGX から読み戻し、`/sim/episode_status` に送る。

### 受信する指令（`/sim/episode_cmd`）
**setup：エピソードの設定**
```json
{
  "command": "setup",
  "request_id": 12,
  "episode_name": "flat_constant",
  "episode_index": 3,
  "total_episodes": 60,
  "phase": "setup",
  "soil": {"cohesion": 10000, "friction_angle_deg": 30, "density": 1600,
           "swell_factor": -1, "youngs_modulus": -1},
  "terrain_ops": []
}
```

| 項目 | 内容 |
| --- | --- |
| `soil.cohesion` | 粘着力 [Pa] |
| `soil.friction_angle_deg` | 内部摩擦角 [deg] |
| `soil.density` | 密度 [kg/m³] |
| `soil.swell_factor` | 膨張率 [−] |
| `soil.youngs_modulus` | ヤング率 [Pa] |
| `terrain_ops` | 地形の加工のリスト（下記） |

`soil` の各値は、負の値なら「変更しない」と解釈する。

**phase：進行状況の更新**
```json
{"command": "phase", "phase": "running"}
```

画面右下の表示（`EpisodeStatusOverlay`）に出す進行状況だけを更新する。地形や土質は変えない。

**terrain_ops：地形の加工（`AGX_GRID_CONTROL` 有効時のみ）**

座標は「設定時の刃先の中点を原点とし、x が車体の前方、y が左方」の長方形で指定する [m]。

| `type` | 内容 | `value` | `depth` |
| --- | --- | --- | --- |
| `compaction_rect` | 範囲内の地表から指定の深さまでの締固めの程度を設定する | 締固めの程度 | 設定する深さ [m] |
| `loose_layer_rect` | 範囲内に緩い土（締固め 1/膨張率）の層を積む | 層の厚さ [m] | 使わない |

`AGX_GRID_CONTROL` が無効のときに `terrain_ops` を送ると、加工は行わず、状態の `ops_applied` を false、`error` にその旨を入れて返す。

### 送信する状態（`/sim/episode_status`）

```json
{"request_id": 12, "episode_name": "flat_constant", "episode_index": 3, "total_episodes": 60,
 "phase": "running", "cohesion": 10000.0, "friction_angle_deg": 30.0, "density": 1600.0,
 "swell_factor": 1.28, "youngs_modulus": 6500000.0, "ops_requested": 0, "ops_applied": true,
 "ops_cells": 0, "grid_control_enabled": false, "reset_count": 4, "sim_time": 123.4, "error": ""}
```

| 項目 | 内容 |
| --- | --- |
| `request_id` | 直近に処理した setup 指令の番号。ROS2 側は、これが自分の送った番号になるまで待つ |
| `episode_name` / `episode_index` / `total_episodes` | 現在のエピソード名、番号（0 始まり）、総数 |
| `phase` | 進行状況（setup / lowering / running / raising / returning / done など） |
| `cohesion` 〜 `youngs_modulus` | **AGX 本体から読み戻した**土質パラメータ。学習データのラベルとして使う |
| `ops_requested` / `ops_applied` / `ops_cells` | 地形の加工の要求件数、成否、加工したマスの数 |
| `grid_control_enabled` | `AGX_GRID_CONTROL` が有効か |
| `reset_count` | 地形をリセットした回数 |
| `sim_time` | シミュレーション時刻 [s] |
| `error` | エラーの内容（なければ空） |

### 実装上の注意点
- 粒子の削除では、AGX の配列型 `agx.GranularBodyPtrArray` を `size()` と `at(i)` で扱い、後ろから順に削除している（AGXUnity の `DeformableTerrainParticleRenderer.cs` と同じ扱い。C# の `Count` や `[i]` は使えない）。
- 指令の処理中に例外が起きた場合は、`error` に内容を入れて状態を送り、Unity の Console にも出力する。


## 4. 使い方

### 設定
`DataCollection` オブジェクトに追加し、Inspector で次を設定する。

| 欄 | 設定 | 既定値 |
| --- | --- | --- |
| Terrain | シーンの `Terrain` | — |
| Shovel | `blade_link`（地形の加工の原点に使う） | — |
| Reference Frame | `base_link`（地形の加工の前方向に使う） | — |
| Cmd Topic | 受信するトピック | `/sim/episode_cmd` |
| Status Topic | 送信するトピック | `/sim/episode_status` |
| Status Rate | 状態を送る頻度 [Hz] | 5 |

### 通常の使い方
ROS2 パッケージ `bull_ope` の `collect_data` / `test_episode` が自動で指令を送るので、手動で操作する必要はない。

### 手動での確認
Play 中に、状態を確認する。

```
ros2 topic echo /sim/episode_status
```

手動で setup 指令を送る。

```
ros2 topic pub --once /sim/episode_cmd std_msgs/msg/String '{data: "{\"command\":\"setup\",\"request_id\":1,\"episode_name\":\"manual\",\"episode_index\":0,\"total_episodes\":1,\"soil\":{\"cohesion\":10000,\"friction_angle_deg\":30,\"density\":1600,\"swell_factor\":-1,\"youngs_modulus\":-1},\"terrain_ops\":[]}"}'
```

画面右下の表示と `/sim/episode_status` の値が、名前「manual」、粘着力 10000 Pa、摩擦角 30 deg、密度 1600 kg/m³ に変わり、地形と粒子が初期状態に戻れば正常。同じ `request_id` で2回送っても、2回目は無視される。


## 5. 注意点
- 土質パラメータの変更は AGX 本体の材料にだけ行うため、Play を止めると Inspector の材料アセットの値（dirt_1 の設定）に戻る。
- 地形のリセットは高さと締固めを戻すが、`terrain_ops` 以外の方法で追加した材料の割り当ては失われることがある（AGX マニュアル 23.22 FAQ）。
- AGX Dynamics 2.37.3.0 には、締固めに応じたダイレイタンシー角の計算のバグが残っている可能性がある（2.38.0.0 で修正、AGX 変更履歴 `61b41b0f3a`）。締固めを操作する `terrain_ops` を使う場合は留意する。
- `terrain_ops` の処理（`AGX_GRID_CONTROL`）は、2.37.3.0 で API が使えるか未確認。