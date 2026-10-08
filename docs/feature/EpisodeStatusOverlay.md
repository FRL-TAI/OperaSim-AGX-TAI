# EpisodeStatusOverlay：データ収集の実行状況の表示

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/UI/EpisodeStatusOverlay.cs` |
| 依存するファイル | `Assets/Research/DataCollection/Scripts/ROS/SimEpisodeManager.cs` |
| 取り付け先 | シーンの `DataCollection` オブジェクト |


## 2. 目的
データ収集は数百エピソードを自動で繰り返すため、いま何番目のエピソードを、どの土質で、どの段階まで実行しているかを、Unity の画面を見るだけで分かるようにする。

あわせて、表示する土質パラメータを AGX 本体から読み戻した値にすることで、ROS2 側から送った設定が実際にシミュレーションに反映されているかを目で確認できるようにする。


## 3. 実装の内容
- Unity の IMGUI（`OnGUI`）で、Game ビューの右下に枠を描画する。画面の大きさが変わっても右下に表示される。
- 表示内容は `SimEpisodeManager.Status` から取得する。この値は `SimEpisodeManager` が AGX 本体から読み戻したもので、`/sim/episode_status` に送っている内容と同じ。
- エラーがあるときは、枠の最後に赤字で表示する。

### 表示される内容
| 表示 | 内容 |
| --- | --- |
| エピソード | 何番目のエピソードか（「4 / 60」のように1始まりで表示） |
| 名前 | エピソード名（`flat_constant` など） |
| 状態 | 進行状況（setup / lowering / running / raising / returning / done など） |
| 粘着力 c / 摩擦角 φ / 密度 ρ / 膨張率 | AGX 本体から読み戻した土質パラメータ |
| 地形の加工 | 要求された地形の加工の件数と、加工したマスの数。締固め操作（`AGX_GRID_CONTROL`）が有効か |
| 時刻 | シミュレーション時刻 [s] |
| エラー | `SimEpisodeManager` で発生したエラー（あるときのみ、赤字） |

表示は `SimEpisodeManager` が状態を更新する頻度（既定 5 Hz）で変わる。

## 4. 使い方

### 設定
`DataCollection` オブジェクトに追加し、Inspector で次を設定する。

| 欄 | 設定 | 既定値 |
| --- | --- | --- |
| Manager | `DataCollection` 自身（同じオブジェクトの `SimEpisodeManager`） | — |
| Font Size | 文字の大きさ | 16 |
| Size | 枠の幅と高さ [ピクセル] | 380 × 240 |
| Margin | 画面の端からの余白 [ピクセル] | 12 |

### 表示する
Play すると、**Game タブ**の右下に「データ収集」の枠が表示される。Scene タブには表示されない。

Play 直後、ROS2 側から指令が来る前は、エピソード「-」、名前「-」、状態「idle」と、Inspector の材料アセットの土質パラメータが表示される。