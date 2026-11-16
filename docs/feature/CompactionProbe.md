# CompactionProbe：締固めの記録の配信

## 1. 該当ファイル
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Terrain/CompactionProbe.cs` |
| 依存するファイル | `Assets/Research/DataCollection/Scripts/Terrain/SoilSimUtils.cs`、`Assets/Research/DataCollection/Scripts/ROS/SimEpisodeManager.cs` |
| 取り付け先 | シーンの `Experiments` オブジェクト |
| 配信するトピック | `/sim/compaction_probe`（`std_msgs/String`、JSON） |
| 受け取る ROS2 側 | `ws-bulldozer` の `bull_ope`（`recording/compaction_recorder.py`）。`<時系列データの名前>_compaction.csv` に保存する |
| 必要な設定 | Scripting Define Symbols に `AGX_GRID_CONTROL`（`TerrainGridControl` を使うため） |


## 2. 目的
履帯で踏んだときの締固めと沈下を調べるため（卒研のフェーズ0）、走路に沿った地形のマスの高さと締固めの程度（地表と深さ方向）を、エピソードの間、一定の間隔で記録する。

- `BladeStatePublisher` の `state_compaction_*` は刃先の前の地表の平均だけなので、履帯の走路のマスごとの値と、深さ方向の値をこのコンポーネントで取る。
- ファイルには書き出さず、ROS2 側に送る（最終的なデータは Unity 側に残さない方針）。


## 3. 実装の内容
### 3.1 記録の開始と終了
`SimEpisodeManager.Status` を `PostStepForward` のたびに見て、次のように動く。

| 時点 | 動作 |
| --- | --- |
| 開始 | `phase` が `lowering` になり、直近の `setup` の `record_compaction` が true のとき。このときの `referenceFrame`（`body_link`）の位置を原点、水平に直した前方向を x、左方を y とする |
| 記録中 | シミュレーション時刻で `sampleInterval`（既定 1 秒）ごとに、全部の列のマスを1回分（スナップショット）として送る |
| 終了 | `phase` が `done` または `idle` になった時点で、最後のスナップショットを `final: true` で送り、以後は送らない |

シーンの読み込み直しでコンポーネントが作り直されるので、1つのシーン（1エピソード）で記録するのは1回だけ。

### 3.2 記録する列
| 列の名前（`lane`） | 場所 |
| --- | --- |
| `track_L±0.00`、`track_R±0.00` | 左右の履帯の走路。走路の中心線の横の位置は、`Track` の車輪の位置の平均から求める。履帯の幅（`trackWidth`）の中に中心が入るマスをすべて記録し、中心線からのマスの中心の横の位置 [m] を名前に付ける |
| `center` | 機体の中心線上のマス（1列） |
| `control` | 対照（走路の外、左方 `controlOffset` [m]）のマス（1列） |

前後方向は `xMin`〜`xMax`（既定 −1〜12 m）の範囲を、半マスずつ進めて調べ、同じマスは1回だけ記録する。

### 3.3 送るメッセージ
```json
{"request_id": 12, "episode_name": "ex_compaction_bands", "final": false,
 "header": "sim_time,phase,machine_x,lane,lane_y,x,unity_ix,unity_iy,height,c_surface,surface_z,c_z0,c_z1,c_z2,c_z3,c_z4,c_z5",
 "rows": "…\n…\n"}
```

- `request_id` は `setup` の番号。ROS2 側はこれが一致するメッセージだけを保存するので、前のエピソードの記録は混ざらない。
- `rows` は1回分の記録（CSV の行。改行は `\n` にそろえる）。`header` は毎回同じものを送る。

| 列 | 内容 |
| --- | --- |
| `sim_time` | 記録した時刻 [s]（AGX の `getTimeStamp()`） |
| `phase` | そのときの進行状況 |
| `machine_x` | 機体（`referenceFrame`）の前後の位置 [m]（上の座標） |
| `lane`、`lane_y`、`x` | 列の名前、マスの中心の横の位置 [m]、前後の位置 [m] |
| `unity_ix`、`unity_iy` | Unity の TerrainData のマスの番号 |
| `height` | 地表の高さ [m]（ワールド座標。`SoilSimUtils.GroundHeight`） |
| `c_surface` | 地表の締固めの程度（`TerrainGridControl.getSurfaceCompaction`） |
| `surface_z` | 地表のボクセルの深さ方向の番号（`findSurfaceIndex`） |
| `c_z0`〜`c_z5` | 地表のボクセル（`c_z0`）から下へ `depthLayers`（既定 6）個のボクセルの締固めの程度（`getCompaction`） |

### 3.4 実装上の注意点
- 深さ方向の関数（`findSurfaceIndex(Vec2i)`、`getCompaction(Vec3i)`）は、AGX の版によって名前が違う可能性があるので、リフレクションで探して呼んでいる（見つからなくてもコンパイルエラーにならない）。見つからなければ深さ方向の列は NaN になり、Console に候補の関数名を出す。2.37.3.0 では見つかる。
- AGX のマス番号は Unity のマス番号と反転している（`SoilSimUtils` と同じ対応）。


## 4. 使い方
### 設定
`Experiments` オブジェクトに追加し、Inspector で次を設定する。

| 欄 | 設定 | 既定値 |
| --- | --- | --- |
| Terrain | シーンの `Terrain` | — |
| Reference Frame | `d37pxi_24` の `body_link`（走行とともに動く剛体） | — |
| Episode Manager | `DataCollection` の `SimEpisodeManager` | — |
| Left Track / Right Track | `AGXUnity.Model.TrackL` / `TrackR` | — |
| X Min / X Max | 記録する前後の範囲 [m] | −1 / 12 |
| Track Width | 履帯の幅 [m]（`Track` の Width と同じ値） | 0.6 |
| Control Offset | 対照の列の位置（左方）[m] | 3.5 |
| Depth Layers | 深さ方向に読むボクセルの数（地表を含む） | 6 |
| Sample Interval | 記録の間隔 [s] | 1 |
| Probe Topic | 配信するトピック | `/sim/compaction_probe` |

### 記録する
ROS2 側のエピソードのクラスで `RECORD_COMPACTION = True` にする（例：`ex_compaction_bands`）。`bull_ope` が `setup` で `record_compaction: true` を送り、受け取った記録を保存する。Console に「CompactionProbe: 記録を開始しました」「記録を終了しました（…、N 回分を送信）」が出る。


## 5. 注意点
- 1回分のメッセージは、既定の設定で約 250 行（約 26 KB）になる（`ex_compaction_bands` の 1 エピソードで 31 回、7,812 行、約 0.8 MB）。間隔を短くしたり範囲を広げたりすると、ROS-TCP-Connector の通信が重くなるので注意する。
- 記録は `phase` が `done` になるまで続く。ROS2 側がエラーで止まり `done` を送らなかった場合は、次の読み込み直しで終わる（ROS2 側は待たずにファイルを閉じる）。
