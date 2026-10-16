# シーンを閉じるときに AGXUnity.Simulation が作り直される（Some objects were not cleaned up）

## 環境
| 項目 | 内容 |
|---|---|
| シミュレータ | OperaSim-AGX（Unity + AGX Dynamics for Unity） |
| シーン | `DataCollectionScene`（シーンを読み込み直してエピソードを初期化する） |
| 関係するスクリプト | `Assets/3rdParty/AWSIM-IMU/ImuSensor.cs`（`d37pxi_24` の `body_link` に付いている） |


## 1. 現象
シーンを読み込み直したとき（`SceneReloader`）や Play を止めたときに、ときどき Unity の Console に次のエラーが出る。

```
Some objects were not cleaned up when closing the scene. (Did you spawn new GameObjects from OnDestroy?)
The following scene GameObjects were found:
AGXUnity.Simulation
```

出たり出なかったりする（関門1では読み込み直し 8 回中 3 回と、Play の停止時）。Editor.log では、このエラーの直前に `IOError: File in path: 'cfg/settings.cfg' does not exist.`（AGX のシミュレーションを作るときに出るメッセージ）が出ており、`Unable to remove contact material - contact material not in Material Manager.` が続くこともある。


## 2. 原因
シーンを閉じるとき、各オブジェクトの `OnDisable` / `OnDestroy` が呼ばれる。その順番は Unity では決まっていない。

`ImuSensor.cs` は、`OnDisable` で次のように AGX の毎ステップの処理の登録を外していた。

```csharp
public void OnDisable()
{
    Simulation.Instance.StepCallbacks.SimulationPost -= DoFixedUpdate;
}
```

AGXUnity の `Simulation.Instance` は、`Simulation` がまだ無ければ自動で作る仕組みになっている。`AGXUnity.Simulation` が `ImuSensor` より先に破棄されていると、この行が**新しい `Simulation` を作ってしまう**。破棄の順番によってだけ起きるので、出たり出なかったりする。

作り直された `Simulation` は、シーンに保存されている `Simulation` の設定（`SolverSettings` アセットなど）を持たない。Editor.log を見ると、このエラーが出た回は、次のシーンの立ち上がりで `Simulation` が作られていなかった。つまり、**次のエピソードが、シーンの設定と違う計算条件で実行されていた可能性がある**。


## 3. 対処法
`ImuSensor.cs` の `OnDisable` を、ほかのスクリプト（`BladeStatePublisher`、`ConstructionMachine` など）と同じく、`Simulation.HasInstance` で確かめてから登録を外す書き方に変える。

```csharp
public void OnDisable()
{
    // シーンを閉じるときは Simulation が先に破棄されていることがある。
    // Simulation.Instance と書くと新しく作り直してしまうので、残っているときだけ登録を外す
    if (Simulation.HasInstance)
        Simulation.Instance.StepCallbacks.SimulationPost -= DoFixedUpdate;
}
```

`DoFixedUpdate` の中の `Simulation.Instance` は、シミュレーションが動いている間にしか呼ばれないので、そのままでよい。


## 4. 確認
1. Play し、読み込み直しを 10〜20 回繰り返す（`bull_ope` の `collect_data --episodes stress_short --num 20` など）。
2. Console の検索欄に `cleaned up` と入れ、0 件であることを確かめる。
3. Play 中に Hierarchy の検索欄に `AGXUnity.Simulation` と入れ、1つだけであること、Inspector の Solver Settings に `SolverSettings` が入っていることを確かめる。
4. Play を止めたときも、`cleaned up` が出ないことを確かめる。

関門1では、修正後の 84 回の読み込み直しで 0 件だった。


## 5. 同じ問題を探す方法
`OnDestroy` / `OnDisable` の中で `Simulation.Instance` を確かめずに使っているスクリプトは、同じ問題を起こしうる。Unity のプロジェクトのフォルダで、PowerShell から次を実行して探せる（AGXUnity 自身のフォルダは除く）。

```
Get-ChildItem -Path Assets -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch "\\AGXUnity\\" } | Select-String -Pattern "void OnDestroy|void OnDisable" -Context 0,15 | Where-Object { ($_.Context.PostContext -join "`n") -match "Simulation\.Instance|GetSimulation\(\)" } | ForEach-Object { "==== $($_.Path):$($_.LineNumber)"; $_.Line; $_.Context.PostContext; "" }
```

2026/10/16 時点では、`Assets/Scripts/ParticleEmitter.cs` の `OnDestroy` にも同じ書き方（`Simulation.Instance?.Native`。`?.` では作り直しを防げない）がある。`DataCollectionScene` では使われていないので直していないが、粒子の放出を使うシーンで読み込み直しをする場合は、同じように直す。
