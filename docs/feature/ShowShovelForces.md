# ShowShovelForces：排土板にかかる土の反力の表示

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| ファイル | `Assets/Research/DataCollection/Scripts/Debug/ShowShovelForces.cs` |
| 取り付け先 | シーンの `DataCollection` オブジェクト |


## 2. 目的
シミュレーション実行中に、排土板が土から受ける力を画面で確認できるようにする。
- 反力が取得できることを目視で確認する（OperaSim-AGX の既存のトピックには反力がない）。
- 空中・接地・押土などの状態で、どの種類の力がどの程度の大きさになるかを把握する。
- `BladeStatePublisher` が配信する合力と、この表示の値を比べ、取得処理や座標変換に誤りがないかを確認する。
- 土質パラメータを変えたとき、反力がどう変わるかをその場で確認する。


## 3. 実装の内容
AGXUnity の `DeformableTerrain` には、Shovel にかかる力を表示するデバッグ用ウィンドウが組み込まれている。ただし、これを開くプロパティ `TempDisplayShovelForces` は `[HideInInspector]` が付いていて Inspector から操作できない。本機能は、このプロパティを Inspector のコンテキストメニューから切り替える小さなコンポーネントである。AGXUnity 本体のコードは変更していない。

```csharp
using UnityEngine;
using AGXUnity.Model;

public class ShowShovelForces : MonoBehaviour
{
    [SerializeField] DeformableTerrain terrain;

    [ContextMenu("Toggle Shovel Forces")]
    void Toggle() { terrain.TempDisplayShovelForces = !terrain.TempDisplayShovelForces; }
}
```

### 表示される内容
Game ビューの右上に「Shovel forces」というウィンドウが開き、次の4種類の力が3成分ずつ kN 単位で表示される。

| 表示名 | 意味 | 主に現れる状況 |
| --- | --- | --- |
| Penetration force | 刃先がまだ削られていない地面に食い込むときの貫入抵抗 | 刃先を地面に入れて切削しているとき |
| Separation force | 地形から切り離され、排土板の前に溜まった土塊からの力 | 前進して土を押しているとき（押土時はほぼこれが支配的） |
| Deformer force | 排土板が切削方向以外（後ろ・横）に土を押しのけるときの力 | 排土板を下ろしての後退、アングルを付けた前進、旋回 |
| Contact force | 排土板の面が、まだ削られていない地面に直接押し付けられたときの力 | 排土板を地面に押し付けて静止したとき |

値は、AGXUnity の `DeformableTerrain.ShowForces()` 内で agxTerrain の API（`getPenetrationForce`、`getSeparationContactForce`、`getDeformationContactForce`、`getContactForce`）から取得されている。Separation・Deformer・Contact は AGXUnity 側で符号を反転して表示し、Penetration は反転していない。

### 仕様上の注意

- **座標系：** 表示は AGX のワールド座標系の値で、車体基準ではない。`BladeStatePublisher` の値（車体基準）とは成分の並びが異なるため、比べるときは合力の大きさ（各成分の2乗和の平方根）で比べる。
- **対象の Shovel：** 地形に登録された最初の Shovel の力だけを表示する。無効化された機体の Shovel は Play 開始時に取り除かれるため、MainScene で d37pxi_24 だけを有効にしている場合は、その排土板の力が表示される。
- **表示のみ：** 値は画面に表示されるだけで、記録・配信はされない。データとして使う場合は `BladeStatePublisher` を用いる。
- **粒子からの力：** 排土板の前でこぼれた土の粒子は、既定では排土板に力を返さない（AGX マニュアル 23.19）。そのため、表示される力に粒子からの力は含まれない。


## 4. 使い方

### 設定
`DataCollection` オブジェクトに追加し、Inspector の `Terrain` の欄にシーンの `Terrain` を割り当てる。

### 表示する方法
1. Play を押す。
2. 地形が初期化されるまで1〜2秒待つ。
3. Inspector の `ShowShovelForces` コンポーネント右上の「⋮」をクリックし、「Toggle Shovel Forces」を選ぶ。
4. **Game タブ**に切り替える。画面右上に「Shovel forces」ウィンドウが表示される。

もう一度「Toggle Shovel Forces」を選ぶとウィンドウが閉じる。

### 確認の手順例
1. 排土板を空中に上げて前進する：全成分がほぼ 0 になる。
2. 排土板を地面に軽く下ろして前進する：Penetration force が現れる。
3. 土を押して前進する：Separation force が支配的になり、水平成分が進行方向と逆向きになる。
