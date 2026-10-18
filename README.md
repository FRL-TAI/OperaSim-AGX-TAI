# OperaSim-AGX-TAI

## 概要
土木機械（油圧ショベル・クローラダンプ・ブルドーザ）と土砂の挙動を再現するシミュレータ [OperaSim-AGX](https://github.com/pwri-opera/OperaSim-AGX) を、自身の研究用に拡張・実装した機能等を保存するリポジトリ。

研究では、ブルドーザ（`d37pxi_24`）の排土板が土から受ける力などを ROS2 で記録し、土質パラメータ（粘着力 c、内部摩擦角 φ、密度 ρ など）を推定するニューラルネットワークの学習データを自動で集める。ROS2 側のデータ収集パッケージは `ws-bulldozer` リポジトリの `bull_ope`（`docs/bull_ope.md`）。


## ソフトウェア要件
- Unity：2022.3.16f1
- AGX：2.37.3.0（AGXUnity 5.0.1 に対応する版）
  - AGX (Core)
  - Particles
  - Granular
  - Terrain
  - Tracks

### Unityで使用するパッケージ
初回プロジェクト読み込み時に自動的に追加される.  
(もしも追加されなかった場合は手動で追加すること)

- [AGXUnity][AGXUnity page]: 5.0.1
- [ROS-TCP-Connector][ROS-TCP-Connector page]: 0.7.0
- [URDF-Importer][URDF-Importer page]: 0.5.2
- [UnitySensors, UnitySensorsROS][UnitySensors page]: 開発版

[AGXUnity page]: https://github.com/Algoryx/AGXUnity
[ROS-TCP-Connector page]: https://github.com/Unity-Technologies/ROS-TCP-Connector
[URDF-Importer page]: https://github.com/Unity-Technologies/URDF-Importer
[UnitySensors page]: https://github.com/Field-Robotics-Japan/UnitySensors


## 実行方法
1. Unity Hubより, 本プロジェクト(OperaSim-AGX-TAI)を追加
2. 追加されたプロジェクトのタイトルを押し, Unity Editorで開く
3. 使うシーンをプロジェクトウィンドウからダブルクリックしてロードする
   - 通常の操作：`Assets/Scenes/MainScene.unity`
   - データ収集：`Assets/Research/DataCollection/Scenes/DataCollectionScene.unity`（[データ収集用のシーン](docs/feature/DataCollectionScene.md)）
4. Assets/AGXUnity/Plugins/x86_64のフォルダへAGX Dynamicsのライセンスファイルをコピー&ペースト
5. RoboticsタブからROS Settingを開き、ROS TCP Endpointで接続するPCのIPアドレスおよびポート番号を入力する
6. Playボタンを押してシミュレーションを実行


## 自作部分に関して
自作のファイルは `Assets/Research/` 以下にまとめている。

```
Assets/Research/DataCollection/
├ Scenes/     DataCollectionScene.unity（データ収集用のシーン）
├ Scripts/
│  ├ ROS/      BladeStatePublisher.cs、SimEpisodeManager.cs
│  ├ Scene/    SceneReloader.cs
│  ├ Terrain/  SoilSimUtils.cs
│  └ UI/       EpisodeStatusOverlay.cs、ShowShovelForces.cs
└ Terrain/    FlatTerrainData.asset（平らな地形のデータ）
```

### 追加機能について
- [データ収集用のシーン](docs/feature/DataCollectionScene.md)
- [排土板まわりの状態の配信](docs/feature/BladeStatePublisher.md)
- [データ収集のエピソード管理](docs/feature/SimEpisodeManager.md)
- [シーンの読み込み直しによる初期化](docs/feature/SceneReloader.md)
- [データ収集の実行状況の表示](docs/feature/EpisodeStatusOverlay.md)
- [データ収集用の共通処理](docs/feature/SoilSimUtils.md)
- [ブルドーザのブレードに生じる抵抗力の表示](docs/feature/ShowShovelForces.md)

### OperaSim-AGX 本体への変更
| ファイル | 変更 | 理由 |
| --- | --- | --- |
| `Assets/3rdParty/AWSIM-IMU/ImuSensor.cs` | `OnDisable` で `Simulation.HasInstance` を確かめてから登録を外す | シーンの読み込み直しで `AGXUnity.Simulation` が作り直されるのを防ぐ（[Tips](docs/tips/simulation_recreated_on_destroy.md)） |
| `DataCollectionScene` の `d37pxi_24` | `Bulldozer Input` の `Movement Control Type` を `Twist Command` にする | `cmd_vel` で走らせるため（[Tips](docs/tips/cmd_vel.md)） |

### Tips
- [cmd_vel のTopicをpublishしても建機が動かない場合](docs/tips/cmd_vel.md)
- [シーンを閉じるときに AGXUnity.Simulation が作り直される（Some objects were not cleaned up）](docs/tips/simulation_recreated_on_destroy.md)
- [シーンを読み込み直すたびに ROS の接続と HUD 表示がたまる](docs/tips/scene_reload_ros_connection.md)
