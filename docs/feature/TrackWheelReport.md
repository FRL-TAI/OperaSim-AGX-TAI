# TrackWheelReport：履帯の車輪と接地長の確認（検証用）

## 1. 該当ファイル
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Experiments/TrackWheelReport.cs` |
| 取り付け先 | シーンの `Experiments` オブジェクト（普段は無効にしておく） |


## 2. 目的
履帯の車輪の構成と、接地長の目安を確かめる。履帯による沈下・締固め（卒研のフェーズ0）や、接地圧の検討に使う。


## 3. 実装の内容
Play の開始から 1 秒後に、`Track` の各車輪について、種類（`TrackWheelModel`：Sprocket / Idler / Roller）、半径、位置を Console に出す。さらに、転輪（Roller）の位置を機体の前方向に投影し、前後の間隔（最も前の転輪と最も後ろの転輪の距離）を「接地長の目安」として出す。

```
…: model = Roller, radius = 0.xxx m, pos = (…)
Roller span (approx. contact length): x.xxx m
```

転輪の間隔は、転輪の中心の間の距離なので、実際の接地長より転輪の半径の分だけ短い目安である。


## 4. 使い方
1. `Experiments` の `TrackWheelReport` を有効にし、`Track` に `AGXUnity.Model.TrackL`（または `TrackR`）を設定する（空なら同じオブジェクトの `Track` を使う。どちらもなければ警告を出して終わる）。
2. Play を押し、1 秒後に Console を見る。
3. 確認が終わったら無効に戻す。
