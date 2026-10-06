# ブルドーザ（d37pxi_24）が cmd_vel トピックで動かない

## 環境
| 項目 | 内容 |
|---|---|
| シミュレータ | OperaSim-AGX（Unity + AGX Dynamics for Unity） |
| 対象機種 | ブルドーザ `d37pxi_24` |
| ROS 2 側 | Docker コンテナ上の ROS 2 |
| Unity ⇔ ROS 2 通信 | ROS-TCP-Connector（`ros_tcp_endpoint` 経由） |
| 送信トピック | `/d37pxi_24/cmd_vel`（`geometry_msgs/msg/Twist`） |


## 1. 現象

ROS 2 側から `/d37pxi_24/cmd_vel` に速度指令を送っても、シミュレータ上のブルドーザがまったく動かない。

送信したコマンドは以下のとおり。

```bash
ros2 topic pub -r 10 /d37pxi_24/cmd_vel geometry_msgs/msg/Twist \
"{linear: {x: 0.5, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}"
```

このとき、次のように **ROS 2 の通信自体は正常に見える** のが特徴である。

- `ros2 topic pub` は `publishing #1: ...` と出力し、送信は継続している。
- `ros2 topic info /d37pxi_24/cmd_vel` で `Subscription count: 1` と表示され、購読者が存在する。
- `/d37pxi_24/odom_pose` や `/d37pxi_24/joint_states` など、シミュレータが出すトピックはコンテナ側で正常に受信できる。
- Unity の Console にエラーは出ていない。

つまり「ROS 2 の通信には問題がないのに、車両だけが反応しない」状態になる。


## 2. 考えられる要因

### 2.1 今回の原因：走行入力モードが `cmd_vel` を使わない設定になっていた

`d37pxi_24` の GameObject にアタッチされている **`Bulldozer Input (Script)`** の
**`Movement Control Type`** が **`Actuator Command`** になっていた。

OperaSim-AGX のブルドーザでは、走行指令の受信と、その指令を実際に使うかどうかが別のクラスに分かれている。

| クラス | 役割 |
|---|---|
| `TrackMessageSubscriber` | `/track_cmd`・`/track_volume_cmd`・`/cmd_vel` の3トピックを購読し、受信値を保持するだけ |
| `TrackTwistCommandConvertor` | Twist（並進速度・角速度）を左右履帯の速度に変換する |
| `BulldozerInput` | `Movement Control Type` の設定に応じて、どの入力を走行に使うかを選ぶ |

`Movement Control Type` が `Actuator Command` の場合、走行には `track_cmd`（左右履帯への直接指令）が使われる。
このとき `cmd_vel` は **受信はされているが、走行制御には一切使われない**。

そのため、ROS 2 から見ると購読者が存在して通信も成立しているのに、車両は動かないという現象になる。


## 3. 対処法

### 3.1 Movement Control Type を `Twist Command` に変更する

1. Unity で Play を **停止した状態（編集モード）** にする。
2. Hierarchy で `MainScene` > `d37pxi_24` を選択する。
3. Inspector で `Bulldozer Input (Script)` を展開する。
4. **`Movement Control Type`** を `Actuator Command` から **`Twist Command`** に変更する。
5. **Ctrl+S** でシーンを保存する。

> **注意**
> Play 中に Inspector の値を変更しても、Play を停止すると元に戻る。
> Play 中は動作確認だけにして、確定した設定は必ず編集モードで変更・保存すること。

### 3.2 動作確認

Play した状態で、ROS 2 側から指令を送る。

**前進**
```bash
ros2 topic pub -r 10 /d37pxi_24/cmd_vel geometry_msgs/msg/Twist \
"{linear: {x: 0.5}, angular: {z: 0.0}}"
```

**その場で左旋回**
```bash
ros2 topic pub -r 10 /d37pxi_24/cmd_vel geometry_msgs/msg/Twist \
"{linear: {x: 0.0}, angular: {z: 0.05}}"
```

**停止**
```bash
ros2 topic pub --once /d37pxi_24/cmd_vel geometry_msgs/msg/Twist \
"{linear: {x: 0.0}, angular: {z: 0.0}}"
```

別ターミナルで以下を実行すると、実際に車両が動いているかを数値で確認できる。

```bash
ros2 topic echo /d37pxi_24/odom_pose
```

### 3.3 入力モードと使用トピックの対応

| Movement Control Type | 走行に使われるトピック |
|---|---|
| `Actuator Command` | `/d37pxi_24/track_cmd`（左右履帯への直接指令） |
| `Twist Command` | `/d37pxi_24/cmd_vel`（並進速度・角速度） |

`track_cmd` で動かしたいときは `Actuator Command` に戻す必要がある。
制御方法を切り替えるときは、このモード設定も合わせて変更すること。

選択肢の正確な意味や、他にどのようなモードがあるかは、以下でソースコードを確認できる。

```bash
grep -rn "MovementControlType" Assets/ --include=*.cs
```