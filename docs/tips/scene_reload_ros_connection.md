# シーンを読み込み直すたびに ROS の接続と HUD 表示がたまる

## 環境
| 項目 | 内容 |
|---|---|
| シミュレータ | OperaSim-AGX（Unity + AGX Dynamics for Unity） |
| Unity ⇔ ROS 2 通信 | ROS-TCP-Connector 0.7.0（`ros_tcp_endpoint` 経由） |
| シーン | `DataCollectionScene`（シーンを読み込み直してエピソードを初期化する） |
| 関係するスクリプト | `Assets/Research/DataCollection/Scripts/Scene/SceneReloader.cs` |


## 1. 現象
シーンの読み込み直し（`SceneManager.LoadScene`）を何十回も繰り返すと、次のことが起きる。

**ROS の接続がたまる**
- ROS-TCP-Endpoint の接続が、読み込み直しのたびに1本ずつ増える（10 回で 7 本）。Play を止めても残る。
- ROS-TCP-Endpoint のログで、`Connection from 127.0.0.1` の数に対して `Disconnected from 127.0.0.1` が半分ほどしか出ない。
- まれに、ROS → Unity の方向だけ、指令が十数秒届かなくなる（Unity → ROS のトピックは届いている）。`bull_ope` では、`Unity から設定完了の応答がありません` の時間切れとして現れた（50 回中 1 回）。Unity 側・ROS-TCP-Endpoint 側のどちらにもエラーは出ない。

**HUD 表示がたまる**
- 画面左上の「ROS2 IP」の表示が、読み込み直しのたびに1つずつ重なっていく。
- Console に `HUDPanel already contains a header registered at index 0! Registering at index N instead.` が出て、N が1ずつ増える。
- 画面の更新の速さが下がり続ける（84 回で約 108 → 約 37 フレーム/ゲーム秒）。50 回/秒を下回ると、`/clock` の周波数も 50 Hz を下回る。


## 2. 原因
`ROSConnectionPrefab(Clone)` は、Play の開始時に各スクリプトの `ROSConnection.GetOrCreateInstance()` が作る。作られる先が今のシーンなので、**シーンを読み込み直すたびに破棄され、新しいシーンで作り直される**（Editor.log で `ROSConnection:Start ()` が読み込み直しと同じ回数出る）。

**接続がたまる理由：** ROS-TCP-Connector の `ROSConnection` には `OnDestroy` がなく、`Disconnect()` は `OnApplicationQuit` でしか呼ばれない。接続を管理するスレッドは GameObject とは別に動いているため、GameObject が破棄されても止まらず、TCP 接続も開いたまま残る。残った接続は、次に Unity がスクリプトを読み込み直したとき（次の Play の開始など）にまとめて閉じられる（ROS-TCP-Endpoint のログに `Exception: No more data available` が数行まとめて出る）。

**HUD がたまる理由：** `ROSConnection` は作られるたびに `HudPanel.RegisterHeader(DrawHeaderGUI)` で見出しを登録する（`ShowHud` の設定に関係なく）。登録先は `HudPanel` の `static` の一覧（`s_HeaderContents`）で、外す関数がなく、Play の開始時にしか空にならない。そのため、破棄済みの `ROSConnection` の見出しがたまり、毎フレーム描かれる。古い `ROSConnection` も見出しから参照されて解放されない。


## 3. 対処法
`SceneReloader.Reload()` で、シーンを読み込み直す**直前に**、次の2つを行う。

```csharp
// 古い接続を閉じる（接続を管理するスレッドに止まるよう知らせ、スレッドが止まるときにソケットが閉じられる）
foreach (var ros in Object.FindObjectsOfType<ROSConnection>())
    ros.Disconnect();

// HUD の見出しの一覧を空にする（新しいシーンの ROSConnection が Start で登録し直す）
var field = typeof(HudPanel).GetField("s_HeaderContents", BindingFlags.NonPublic | BindingFlags.Static);
if (field?.GetValue(null) is System.Collections.IDictionary headers)
    headers.Clear();
```

- `ROSConnection.GetOrCreateInstance()` は、無ければ新しく作ってしまうので使わず、`FindObjectsOfType` で探す。
- `s_HeaderContents` は `private static` なので、リフレクションで取り出す。ROS-TCP-Connector の版が変わって見つからない場合に気づけるよう、`SceneReloader` では警告を出すようにしている。
- HUD の描画だけを止めたい場合は、Robotics → ROS Settings の Show HUD を外す方法もある。ただし、見出しの登録（と古い `ROSConnection` が解放されないこと）は止まらない。

シーンごとに `ROSConnection` を作り直す仕組み自体は変えていない。`ROSConnection` を `DontDestroyOnLoad` で残す方法も考えられるが、OperaSim 本体の購読者がすべて破棄のときに購読を解除しているかを確かめる必要があり、変更の範囲が大きくなるため採用していない。


## 4. 確認
**接続の数**（ROS-TCP-Endpoint を動かしているコンテナ内で実行。`ss` が無い環境でも使える）

```
while true; do echo "$(date +%T)  接続数: $(awk '$2 ~ /:2710$/ && $4 == "01"' /proc/net/tcp /proc/net/tcp6 | wc -l)"; sleep 5; done
```

`2710` はポート 10000 を 16 進数で書いたもの、`01` は接続中の意味。読み込み直しを繰り返しても常に 1（読み込み直しの瞬間だけ 0）、Play を止めると 0 になれば正常。

**HUD 表示**
- 画面左上の「ROS2 IP」が、何回読み込み直しても1つだけ。
- Console に `HUDPanel already contains a header registered at index 0!` が出ない。
- Game ビューの Stats の FPS が、読み込み直しを重ねても下がらない。

**画面の更新の速さの推移**（Editor.log から）

`RealTimeTracker` が読み込み直しのたびに出す `Unity has skipped - ○s game time (at frame N)` の、フレーム番号の差 ÷ ゲーム時間の差で、読み込み直しの間の「ゲーム1秒あたりのフレーム数」が分かる。読み込み直しの回数に対して横ばいなら正常。
