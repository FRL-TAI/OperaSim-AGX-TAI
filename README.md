# OperaSim-AGX-TAI

## 概要
土木機械（油圧ショベル・クローラダンプ・ブルドーザ）と土砂の挙動を再現するシミュレータ [OperaSim-AGX](https://github.com/pwri-opera/OperaSim-AGX) を、自身の研究用に拡張・実装した機能等を保存するリポジトリ


## ソフトウェア要件
- Unity：2022.3.62f1
- AGX：2.38.0.1(X64 VS2022)
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
3. 建設機械のモデルが表示されていない場合には, プロジェクトウィンドウからAssets/Scenes/MainScene.unityをダブルクリックしてロードする
4. Assets/AGXUnity/Plugins/x86_64のフォルダへAGX Dynamicsのライセンスファイルをコピー&ペースト
5. RoboticsタブからROS Settingを開き、ROS TCP Endpointで接続するPCのIPアドレスおよびポート番号を入力する
6. Playボタンを押してシミュレーションを実行


## 自作部分に関して
### 追加機能について
- [ブルドーザのブレードに生じる抵抗力の表示](docs/feature/show_shovelforces.md)

### Tips
- [cmd_vel のTopicをpublishしても建機が動かない場合](docs/tips/cmd_vel.md)