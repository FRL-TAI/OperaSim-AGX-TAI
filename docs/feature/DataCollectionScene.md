# DataCollectionScene：データ収集用のシーン

## 1. 該当ファイル
| 項目 | 内容 |
| --- | --- |
| シーン | `Assets/Research/DataCollection/Scenes/DataCollectionScene.unity` |
| 地形のデータ | `Assets/Research/DataCollection/Terrain/FlatTerrainData.asset`（OperaSim の `FlatTerrainData` を複製したもの） |
| 対になる ROS2 側 | `ws-bulldozer` リポジトリの `bull_ope` パッケージ（`docs/bull_ope.md`） |


## 2. 目的
土質パラメータ推定用の学習データを集めるための専用のシーン。

- 切削深さを「元の地面の高さ − 刃先の高さ」で求めるため、ブルドーザの前方に平らな地面を用意する。
- データ収集に使う自作のコンポーネントを1つのオブジェクト（`DataCollection`）にまとめる。
- 毎エピソード、このシーンを読み込み直すことで、機体・地形・AGX の状態をすべて同じ初期状態に戻す（[SceneReloader](SceneReloader.md)）。


## 3. シーンの構成
### 3.1 DataCollection オブジェクト
| コンポーネント | 役割 | 主な設定 |
| --- | --- | --- |
| [BladeStatePublisher](BladeStatePublisher.md) | 排土板まわりの状態を `/d37pxi_24/blade_state` に 50 Hz で配信 | Terrain、Shovel（`blade_link`）、Reference Frame（`base_link`） |
| [SimEpisodeManager](SimEpisodeManager.md) | `/sim/episode_cmd` の指令（setup / phase / reload）を処理し、`/sim/episode_status` に状態を配信 | Terrain、Shovel、Reference Frame |
| [SceneReloader](SceneReloader.md) | シーンの読み込み直し | 設定する欄はない |
| [EpisodeStatusOverlay](EpisodeStatusOverlay.md) | Game ビューの右下に実行状況を表示 | Manager（同じオブジェクトの `SimEpisodeManager`） |
| [ShowShovelForces](ShowShovelForces.md) | AGXUnity の反力の確認用ウィンドウを開く（デバッグ用） | Terrain |

### 3.2 そのほかの設定
| 対象 | 設定 | 理由 |
| --- | --- | --- |
| `d37pxi_24` の `Bulldozer Input` | `Movement Control Type` = `Twist Command` | ROS2 側から `/d37pxi_24/cmd_vel` で走らせるため（[Tips](../tips/cmd_vel.md)） |
| `AGXUnity.Simulation`（シーンに保存） | Auto Stepping Mode = Fixed Update、Time Step = 0.02、Solver Settings = `SolverSettings` アセット | シミュレーションの時間刻み（50 Hz）とソルバーの設定をシーンで固定する |
| 地形 | `FlatTerrainData` を複製した平らな地形 | 切削深さの計算の前提 |
| 機体の初期位置 | 開始位置は ROS の座標でおよそ x = −10.0 m、y = 7.0 m、向き 0°（`global_pose`） | 前方に 10 m 以上の平らな地面を確保する。ROS 側の `SAFE_RADIUS`（20 m）はフィールドの端より内側に設定する |

`ROSConnectionPrefab(Clone)`（ROS-TCP-Connector の接続）は、Play の開始時に各スクリプトの `ROSConnection.GetOrCreateInstance()` が作る。シーンの一部として作られるので、読み込み直しのたびに作り直される（[Tips](../tips/scene_reload_ros_connection.md)）。


## 4. 使い方
1. プロジェクトウィンドウから `DataCollectionScene` を開く。
2. コンテナ内で ROS-TCP-Endpoint を起動する（`ros2 run ros_tcp_endpoint default_server_endpoint`）。
3. Play を押す。Game ビューの右下に「データ収集」の枠（状態 idle）が表示される。
4. ROS2 側で `bull_ope` の `test_episode` または `collect_data` を実行する。以降は ROS2 側からの指令で自動的に進む。

### 長時間の収集のときの注意
- Unity のウィンドウを最小化しない。PC がスリープしないようにする。
- Console の「Collapse」はオフにしておくと、新しいエラーが出たかを判断しやすい。
- Unity を再起動すると `Editor.log`（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）が上書きされる。収集の記録として残す場合は、再起動の前にコピーする。


## 5. 確認済みの動作（関門1）
`bull_ope` の `analysis/reports/gate1_stress.md` で、次を確認した。

| 項目 | 結果 |
| --- | --- |
| 読み込み直しの時間 | 1.1〜1.4 秒（84 回の読み込み直しで増加なし） |
| 開始位置のばらつき | 最大 0.17 cm、向き 0.08° 以内 |
| 土質の入れ替わり | 要求した値と AGX から読み戻した値が一致（10 回とも） |
| メモリ | 380〜470 MB を上下（増え続けない） |
| ROS-TCP-Endpoint との接続 | 常に 1 本（`SceneReloader` での `Disconnect()` の追加後） |


## 6. 注意点
- 読み込み直しの直後に、Console に毎回次のメッセージが出る。いずれも OperaSim 本体・AGXUnity・ROS-TCP-Connector から出るもので、データ収集には影響しない。
  - `NullReferenceException … AGXUnity.Constraint.Initialize`（2件）
  - `Script 'UrdfInertial' / 'UrdfJointRevolute' on GameObject bucket_end_link has possibly missing Required Components!`
  - `Removed 2 disabled shovels from terrain Terrain.`
  - `RealTimeTracker : Unity has skipped …` と `Critical Timing Problem: SkippedGameTime has decreased.`（シミュレーション時刻が 0 に戻るため）
  - `Exception … 'cfg/settings.cfg' does not exist`（Editor.log のみ。AGX のシミュレーションを作るたびに出る）
- Play を止めた後、`FlatTerrainData.asset` などの地形のアセットが「変更あり」になることがある（`DeformableTerrain` が破棄のときに地形の高さを書き戻すため）。シーンの変更をコミットするときは、地形のアセットを混ぜない（`git restore` で戻す）。
- 記録の最初の切削深さが約 13 cm になる（読み込み直し直後の排土板の位置が、ROS 側で測る基準の地面の高さより低い）。深さ 0 の基準は関門3で確かめる。
