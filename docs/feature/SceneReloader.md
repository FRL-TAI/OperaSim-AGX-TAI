# SceneReloader：シーンの読み込み直しによる初期化

## 1. 該当ファイル
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Scene/SceneReloader.cs` |
| 取り付け先 | シーンの `DataCollection` オブジェクト |
| 呼び出し元 | `SimEpisodeManager`（`/sim/episode_cmd` の `{"command":"reload"}`）、Inspector のコンテキストメニュー |


## 2. 目的
データ収集では、すべてのエピソードを同じ初期状態から始める必要がある。初期化の方法として、次を試した結果、**シーンを丸ごと読み込み直す**方法に決めた。

| 方法 | 結果 |
| --- | --- |
| 後退して開始位置に戻る | 向きのずれで開始位置を外れ、フィールドから落下することがあった |
| 機体を開始位置に瞬間移動させる | 位置は戻せたが、履帯のマージなどの内部状態が残り、走れなくなることがあった |
| **シーンを読み込み直す** | 機体・地形・粒子・AGX の内部状態がすべて初期状態に戻る。所要 約 1.1〜1.4 秒 |


## 3. 実装の内容
### 3.1 処理の流れ（`Reload()`）
1. 読み込み直しの途中なら、何もしない（二重の読み込み直しを防ぐ）。
2. 開始した時刻と回数を `static` の変数に記録し、Console に「シーン … を読み込み直します（N 回目）」と出す。
3. 今のシーンの `ROSConnection` の接続を閉じる（`Disconnect()`）。
4. ROS-TCP-Connector の HUD の見出しの一覧を空にする（`ClearHudHeaders()`）。
5. 今のシーンを `LoadSceneMode.Single` で読み込み直す。Build Settings に登録されていない場合も、エディタではシーンのパスで読み込む（`EditorSceneManager.LoadSceneInPlayMode`）。

新しいシーンで `Start()` が呼ばれた時点で、Console に「読み込み直しが完了しました（N 回目、○秒）」と出す。`static` の変数はシーンを読み込み直しても残るので、回数と所要時間を記録できる（Play を止めるとリセットされる）。

### 3.2 ROS の接続を閉じる理由（手順3）
`ROSConnectionPrefab(Clone)` はシーンの一部として作られ、読み込み直しで破棄される。しかし ROS-TCP-Connector 0.7.0 の `ROSConnection` には `OnDestroy` がなく、`Disconnect()` は `OnApplicationQuit` でしか呼ばれない。接続を管理するスレッドは GameObject とは別に動いているため、破棄されても止まらず、TCP 接続が開いたまま残る。

閉じないと、読み込み直しのたびに ROS-TCP-Endpoint に古い接続がたまり（10 回で 7 本、Play を止めても残る）、まれに ROS → Unity の指令が十数秒届かなくなる。そのため、読み込み直しの前に明示的に閉じる。詳細は [Tips](../tips/scene_reload_ros_connection.md)。

### 3.3 HUD の見出しを空にする理由（手順4）
`ROSConnection` は作られるたびに、画面左上の「ROS2 IP」の見出しを `HudPanel.RegisterHeader` で登録する。登録先は `HudPanel` の `static` の一覧（`s_HeaderContents`）で、外す方法がなく、Play の開始時にしか空にならない。

空にしないと、読み込み直しのたびに破棄済みの `ROSConnection` の見出しがたまって毎フレーム描かれ、画面の更新の速さが下がり続ける（84 回で約 108 → 約 37 フレーム/ゲーム秒）。古い `ROSConnection` も見出しから参照されて解放されない。新しいシーンの `ROSConnection` は `Start` で自分の見出しを登録し直すので、ここで空にしてよい。

一覧は `private static` のため、リフレクションで取り出して `Clear()` する。ROS-TCP-Connector の版が変わってフィールドが見つからない場合は、Console に警告を出す。


## 4. 使い方
### 設定
`DataCollection` オブジェクトに追加する。設定する欄はない。

### 手動で読み込み直す
Play 中に、Inspector の `SceneReloader` の「⋮」→「Reload Scene」を選ぶ。

### ROS2 から読み込み直す
`/sim/episode_cmd` に `{"command":"reload"}` を送る。`SimEpisodeManager` が `SceneReloader.Reload()` を呼ぶ。ROS2 側は、`/sim/episode_status` の `boot_id` が変わったことで、新しいシーンが立ち上がったと判断する。`bull_ope` は各エピソードの最初にこれを自動で行う。

```
ros2 topic pub --once /sim/episode_cmd std_msgs/msg/String '{data: "{\"command\":\"reload\"}"}'
```


## 5. 確認の方法
| 確認すること | 方法 | 正常な結果 |
| --- | --- | --- |
| 読み込み直しの時間 | Console の「読み込み直しが完了しました（N 回目、○秒）」 | 約 1.1〜1.4 秒。回数を重ねても増えない |
| ROS-TCP-Endpoint との接続の数 | コンテナ内で `awk '$2 ~ /:2710$/ && $4 == "01"' /proc/net/tcp /proc/net/tcp6 \| wc -l` | 常に 1（読み込み直しの瞬間だけ 0）。Play を止めると 0 |
| HUD の見出し | 画面左上の「ROS2 IP」 | 何回読み込み直しても1つだけ。Console に `HUDPanel already contains a header …` が出ない |
| `AGXUnity.Simulation` | Console の `Some objects were not cleaned up when closing the scene` | 出ない（[Tips](../tips/simulation_recreated_on_destroy.md)） |


## 6. 注意点
- 読み込み直すと、シーンのすべてのオブジェクトが作り直される。シーンをまたいで値を残したい場合は `static` の変数を使う（このスクリプトの回数・時刻のように）。
- 自作のスクリプトで AGX の毎ステップの処理を登録する場合は、`OnDestroy` で `Simulation.HasInstance` を確かめてから登録を外す。確かめずに `Simulation.Instance` を使うと、破棄の途中で `Simulation` が作り直される。
- 自作のスクリプトで ROS のトピックを購読する場合は、`OnDestroy` で `Unsubscribe` する（`SimEpisodeManager` を参照）。
- ROS-TCP-Connector を更新した場合は、`Disconnect()` が破棄のときに呼ばれるようになっていないか、`s_HeaderContents` の名前が変わっていないかを確認する。
