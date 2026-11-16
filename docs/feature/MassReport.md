# MassReport：機体の質量の確認（検証用）

## 1. 該当ファイル
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Experiments/MassReport.cs` |
| 取り付け先 | シーンの `Experiments` オブジェクト（普段は無効にしておく） |


## 2. 目的
機体の質量を確かめる。履帯の駆動力のつり合いや沈下の検討で、機体全体の質量が必要になる。`body_link` の Inspector に入力された質量（9,300 kg）だけでは、腕・排土板などの剛体の質量が含まれないため、すべての剛体の質量を AGX 本体から読み出して合計する。


## 3. 実装の内容
Play の開始から 1 秒後（AGXUnity の初期化とシミュレーションの開始を待つ）に、`Machine Root` 以下のすべての `AGXUnity.RigidBody` について、AGX 本体の質量（`Native.getMassProperties().getMass()`）を Console に出し、最後に合計を出す。

```
body_link: 9300.0 kg
…
Total mass: 11000.0 kg (uninitialized bodies: 0)
```

初期化されていない剛体（`Native` が null）は警告を出して数え、合計には含めない。


## 4. 使い方
1. `Experiments` の `MassReport` を有効にし、`Machine Root` に `d37pxi_24` を設定する（空なら `Experiments` 以下を調べる）。
2. Play を押し、1 秒後に Console を見る。
3. 確認が終わったら無効に戻す（データ収集の読み込み直しのたびに出力されるのを避けるため）。

## 5. 確認した値
機体の質量は、剛体の質量の合計で約 11,000 kg だった（[DataCollectionScene](DataCollectionScene.md) の注意点）。
