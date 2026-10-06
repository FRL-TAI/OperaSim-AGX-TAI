# ShowShovelForces：排土板にかかる土の反力の表示機能

## 1. 該当ファイル・コミット
| 項目 | 内容 |
| --- | --- |
| 追加ファイル | `Assets/Scripts/Research/ShowShovelForces.cs` |
| 利用している既存コード | `...(略).../DeformableTerrain.cs`（AGXUnity 同梱。変更なし） |
| コミットハッシュ | `acd929a220cf5bfa633d62a817a527ea192d5c5a` |


## 2. 実装した機能
シミュレーション実行中に、ブルドーザの排土板（AGX の Shovel）が土から受ける力を、Game ビューの右上にウィンドウとしてリアルタイム表示する機能である。

AGXUnity の `DeformableTerrain` コンポーネントには、Shovel にかかる力を表示するデバッグ用ウィンドウが組み込まれている。ただし、これを開くプロパティ `TempDisplayShovelForces` は `[HideInInspector]` が付いており、Inspector からは操作できない。本機能は、このプロパティを Inspector のコンテキストメニューから切り替えられるようにする小さなコンポーネントである。AGXUnity 本体のコードは変更していない。

### ソースコード

```csharp
// ShowShovelForces.cs
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

ウィンドウ（タイトル「Shovel forces」）には、次の4種類の力が3成分ずつ、kN 単位で表示される。

| 表示名 | 意味 | 主に現れる状況 |
| --- | --- | --- |
| Penetration force | 刃先がまだ削られていない地面に食い込むときの貫入抵抗 | 刃先を地面に入れて切削しているとき |
| Separation force | 地形から切り離され、排土板の前に溜まった土塊からの力 | 前進して土を押しているとき（押土時はほぼこれが支配的） |
| Deformer force | 排土板が切削方向以外（後ろ・横）に土を押しのけるときの力 | 排土板を下ろしての後退、アングルを付けた前進、旋回 |
| Contact force | 排土板の面が、まだ削られていない地面に直接押し付けられたときの力 | 排土板を地面に押し付けて静止したとき、硬い壁面への衝突 |

値は `DeformableTerrain.ShowForces()` 内で、agxTerrain の次の API から取得されている。Separation・Deformer・Contact は AGXUnity 側で符号を反転して表示し、Penetration は反転していない。

```csharp
Native.getPenetrationForce( shovel, ref penetrationForce, ref penetrationTorque );
var separationForce = -Native.getSeparationContactForce( shovel );
var deformerForce   = -Native.getDeformationContactForce( shovel );
var contactForce    = -Native.getContactForce( shovel );
```

### 仕様上の注意

- **座標系：** 表示は AGX のワールド座標系の値である。Unity の座標系とは x 軸の向きが異なり、車体基準の座標でもない。どの成分が車体の前後・上下に当たるかは、車体の向きと合わせて解釈する必要がある。
- **対象の Shovel：** 地形に登録された最初の Shovel の力だけを表示する。無効化された機体の Shovel は Play 開始時に地形から取り除かれるため、MainScene で d37pxi_24 だけを有効にしている場合は、その排土板の力が表示される。
- **表示のみ：** 値は画面に表示されるだけで、記録・配信はされない。データとして使う場合は `BladeStatePublisher.cs`（`/d37pxi_24/blade_state` に配信）を用いる。
- **粒子からの力：** 排土板の前でこぼれた土の粒子との接触は通常の剛体接触として扱われるため、表示される4つの力には含まれていない可能性がある。


## 3. 目的・使用用途
- **反力が取得できることの確認：** OperaSim-AGX の既存の ROS2 トピックには排土板の反力がなく、`main_fluid_pressure` も 0 のままである。本機能により、AGX の地形モジュールから排土板の反力を取得できることを目視で確認できる。
- **力の大きさと向きの把握：** 空中・接地・押土などの状態で、どの種類の力がどの程度の大きさになるかを確認し、反力のデータとしての妥当性を判断する。
- **配信スクリプトの検証：** `BladeStatePublisher.cs` が配信する合力と、本ウィンドウの値（各成分の2乗和の平方根）を比較し、取得処理や座標変換に誤りがないかを確認する。
- **土質パラメータの効果の確認：** 地形材料の粘着力・内部摩擦角・密度などを変えたとき、反力がどう変化するかをその場で確認する。
- **デバッグ：** 排土板の Shovel 設定（Cutting Edge、Top Edge、Cutting Direction）が正しいかの確認に使える。前進時に Separation force が支配的になれば、切削方向が正しく設定されていると判断できる。


## 4. 使用方法
### 4.1 初回の設定
1. Hierarchy の `Terrain` を選択する。
2. Inspector の「Add Component」から `ShowShovelForces` を追加する。
3. 追加したコンポーネントの `Terrain` 欄に、Hierarchy の `Terrain` をドラッグして割り当てる。
4. シーンを保存する。

### 4.2 表示する
1. Play を押す。
2. 地形が初期化されるまで1〜2秒待つ。
3. Inspector の `ShowShovelForces` コンポーネント右上の「⋮」をクリックし、「Toggle Shovel Forces」を選ぶ。
4. **Game タブ**に切り替える。画面右上に「Shovel forces」ウィンドウ（750 × 125 ピクセル）が表示される。

もう一度「Toggle Shovel Forces」を選ぶとウィンドウが閉じる。

### 4.3 うまく表示されないとき
| 症状 | 確認事項 |
| --- | --- |
| ウィンドウが見えない | Scene タブではなく Game タブを見ているか。Game ビューが小さいと右上が見切れるので、タブを最大化（ダブルクリックまたは Shift+Space）する |
| メニューに「Toggle Shovel Forces」がない | Play 中か。スクリプトのコンパイルエラーがないか（Console を確認） |
| 押しても反応しない、またはエラーが出る | `Terrain` 欄が割り当てられているか。Play 直後に操作していないか（初期化を待ってから操作する） |
| 力がすべて 0 のまま | 排土板が地面や土に接しているか。d37pxi_24 の `blade_link` に `Deformable Terrain Shovel` が付いており、地形の Shovels に登録されているか |
| コンパイルエラー「AGXUnity が見つからない」 | アセンブリ定義の参照の問題。既存の `BulldozerMainFluidPressurePublisher.cs` と同じフォルダに置く |

### 4.4 確認の手順例
反力の妥当性を確認するときは、次の3つの状態で値を比べる。

1. 排土板を空中に上げて前進する：全成分がほぼ 0 になる。
2. 排土板を地面に軽く下ろして前進する：Penetration force が現れる。
3. 土を押して前進する：Separation force が支配的になり、水平成分が進行方向と逆向きになる。


## 5. 関連ファイル
| ファイル | 関係 |
| --- | --- |
| `DeformableTerrain.cs`（AGXUnity） | 表示ウィンドウ本体（`ShowForces()`）と `TempDisplayShovelForces` プロパティ |
| `BladeStatePublisher.cs` | 同じ API で反力を取得し、車体基準の座標に変換して ROS2 に配信するスクリプト |
| `data_collection_guide.md` | 反力を含むデータセットの取得方法 |