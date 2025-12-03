# JointController - セットアップガイド

## 概要
JointControllerは、**Position Constraintで連動している構造**に対応したジョイント制御スクリプトです。
親子関係なしでJoint/Pivotオブジェクトを制御し、VRでのスケール問題を解決します。

## 主な機能
- **Position Constraint対応**: 位置連動はConstraintに任せ、回転のみを制御
- **複数Joint対応**: 配列で複数のJointを一括管理
- **柔軟な回転軸設定**: X, Y, Z軸を各Jointで個別に指定可能
- **マニュアルモード**: UDP/EMG入力なしでインスペクターから直接角度を操作
- **EMGモード**: BiTalinoからのEMG信号で制御

---

## EMGJointControllerとの違い

| 項目 | EMGJointController | JointController (新) |
|------|-------------------|---------------------|
| 位置連動 | スクリプトで計算 | Position Constraintで自動 |
| Joint数 | 2個固定 | 配列で複数対応 |
| 回転軸 | 固定（Joint1:Z, Joint2:X） | 各Joint個別に設定可能 |
| アタッチ先 | 空のGameObject | 空のGameObjectまたは各Joint |

---

## セットアップ手順

### 1. Position Constraintの設定

各Joint/Pivotオブジェクトに**Position Constraint**コンポーネントを設定してください。

**例: 2つのJointがある場合**
```
Hierarchy:
- Joint1_Cylinder (Position Constraint: なし、または親オブジェクトを参照)
- Joint2_Cylinder (Position Constraint: Joint1_Cylinderを参照)
```

**Position Constraintの設定方法:**
1. `Joint2_Cylinder`を選択
2. Inspector > Add Component > Position Constraint
3. Sources に `Joint1_Cylinder` を追加
4. At Rest を調整して適切な位置に配置
5. Is Active にチェック ✓

---

### 2. Hierarchy構造

**推奨構造:**
```
- Joint_Controller (空のGameObject) ← スクリプトをここにアタッチ
- Joint1_Cylinder (Position Constraint: 任意)
- Joint2_Cylinder (Position Constraint: Joint1を参照)
- Joint3_Cylinder (Position Constraint: Joint2を参照)
...
```

**または各Jointにアタッチ:**
```
- Joint1_Cylinder ← スクリプトをアタッチ
- Joint2_Cylinder ← スクリプトをアタッチ
...
```

---

### 3. スクリプトのアタッチ

**方法1: 一括制御（推奨）**
1. 空のGameObjectを作成（例: "Joint_Controller"）
2. `JointController.cs`をドラッグ＆ドロップしてアタッチ
3. すべてのJointをInspectorで設定

**方法2: 個別制御**
1. 各Joint/Pivotオブジェクトにそれぞれアタッチ
2. 制御したいJointだけをInspectorで設定

---

### 4. Inspector設定

#### 制御対象のJoint/Pivot
1. **Joints**配列のサイズを設定（例: 2）
2. Element 0: `Joint1_Cylinder`をドラッグ＆ドロップ
3. Element 1: `Joint2_Cylinder`をドラッグ＆ドロップ

#### 回転軸の設定
Jointsの数に応じて自動的にサイズが調整されます。
- **Rotation Axes**で各Jointの回転軸を指定（X, Y, Z）
  - 例: Joint1 → Z軸、Joint2 → X軸

#### Test Mode（マニュアル制御）
- **Use Manual Control**: ✓チェックでマニュアルモード
- **Manual Angles**: 各Jointの角度を配列で設定
  - 配列のサイズは自動調整されます

#### EMG Data Source（EMGモード時のみ）
- **Emg Processor**: `EMGSignalProcessor`をドラッグ＆ドロップ
  - マニュアルモード時は不要

#### EMG Channel Mapping
- **Bend Channels**: 各Jointの正方向チャンネル（例: 1, 3）
- **Extend Channels**: 各Jointの負方向チャンネル（例: 2, 4）

---

## 使用方法

### マニュアルモード（テスト用）
1. **Use Manual Control** にチェック ✓
2. **Manual Angles**配列の各要素をスライダーで調整
   - Element 0 → Joint1の角度
   - Element 1 → Joint2の角度
3. Game Viewでリアルタイムに動作を確認

**メリット:**
- UDP/EMG入力なしで動作確認可能
- Position Constraintの設定テストに最適
- VRでのスケール問題確認に使える

### EMGモード（実運用）
1. **Use Manual Control** のチェックを外す
2. **Emg Processor** を設定
3. **Bend Channels**と**Extend Channels**を設定
   - デフォルト: Joint0 → Ch1/Ch2, Joint1 → Ch3/Ch4
4. BiTalinoからEMG信号を受信して制御

---

## Position Constraintとの連動

### 仕組み
1. **Position Constraint**: Joint間の位置関係を自動管理
   - Joint2はJoint1に追従
   - Joint3はJoint2に追従（多段階連動も可能）

2. **JointController**: 各Jointの**回転のみ**を制御
   - Position Constraintが位置を自動調整
   - スクリプトは回転角度のみを更新

### メリット
- **VRスケール問題を解決**: 親子関係なしでも連動
- **Unityエディタで調整しやすい**: Position Constraintで視覚的に設定
- **柔軟性**: Constraintのパラメータで細かく調整可能

---

## 設定例

### 例1: 2つのJoint（指の第1・第2関節）
```
Joints: [Joint1_Cylinder, Joint2_Cylinder]
Rotation Axes: [Z, X]
Bend Channels: [1, 3]
Extend Channels: [2, 4]

Position Constraint設定:
- Joint1: なし（ルート）
- Joint2: Joint1を参照
```

### 例2: 3つのJoint（指の3関節）
```
Joints: [Joint1, Joint2, Joint3]
Rotation Axes: [Z, X, X]
Bend Channels: [1, 3, 1]  ※ 4チャンネルしかないため共有
Extend Channels: [2, 4, 2]

Position Constraint設定:
- Joint1: なし（ルート）
- Joint2: Joint1を参照
- Joint3: Joint2を参照
```

---

## トラブルシューティング

### Jointが動かない
1. **Position Constraint**が正しく設定されているか確認
   - Is Active にチェックが入っているか
   - Sourcesに正しいオブジェクトが設定されているか
2. **Joints配列**に正しくオブジェクトが割り当てられているか
3. **Rotation Axes**が正しい軸に設定されているか

### 位置がずれる
- **Position Constraintの設定**を確認
  - At Rest / Translation Offsetを調整
  - Weightを確認（通常は1.0）

### マニュアルモードで動かない
- **Use Manual Control**にチェックが入っているか確認
- Game Viewでプレイモードになっているか確認

### EMGモードで動かない
- **Use Manual Control**のチェックを外す
- **Emg Processor**が正しく設定されているか確認
- BiTalinoが正しく接続されているか確認

---

## アタッチメント先まとめ

### 推奨方法: 一括制御
| オブジェクト | スクリプト | 説明 |
|------------|----------|------|
| Joint_Controller（空のGameObject） | JointController.cs | このスクリプトをアタッチ |
| Joint1_Cylinder | Position Constraint | Inspectorで参照、位置連動 |
| Joint2_Cylinder | Position Constraint | Inspectorで参照、位置連動 |

### 代替方法: 個別制御
| オブジェクト | スクリプト | 説明 |
|------------|----------|------|
| Joint1_Cylinder | JointController.cs | Joint1のみを制御 |
| Joint2_Cylinder | JointController.cs | Joint2のみを制御 |

---

## 開発者向け情報

### 主要メソッド
- `ApplyJointRotations()`: 各Jointに回転を適用
- `GetAngleFromAxis()` / `SetAngleToAxis()`: 指定した軸の角度を取得/設定
- `OnValidate()`: Inspector変更時に配列サイズを自動調整

### 座標系
- **回転**: 各Jointで個別に指定可能（X, Y, Z）
- **位置**: Position Constraintが自動管理

### 配列の自動調整
`OnValidate()`メソッドにより、Joints配列のサイズを変更すると、以下の配列が自動的にサイズ調整されます：
- `manualAngles`
- `rotationAxes`
- `bendChannels`
- `extendChannels`

---

## バージョン履歴
- **v1.0**: 初版（Position Constraint対応、複数Joint対応、マニュアルモード）
