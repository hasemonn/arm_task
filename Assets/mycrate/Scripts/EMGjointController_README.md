# EMGJointController - セットアップガイド

## 概要
EMGJointControllerは、**親子関係なし**でCylinderを連動制御できるスクリプトです。
VRでの表示時にスケールが変わる問題を解決するため、親子関係を解除した状態で動作します。

## 主な機能
- **親子関係不要**: Joint1とJoint2を親子関係なしで連動制御
- **マニュアルモード**: UDP/EMG入力なしでインスペクターから直接角度を操作可能
- **EMGモード**: BiTalinoからのEMG信号で制御（従来の機能）

---

## セットアップ手順

### 1. Hierarchy構造の設定

#### 現在の構造（親子関係あり）
```
- Joint1_Cylinder
  └─ Joint2_Cylinder  ← 親子関係
```

#### 新しい構造（親子関係なし）
```
- EMG_Controller (空のGameObject)
- Joint1_Cylinder
- Joint2_Cylinder
```

**変更手順:**
1. Hierarchyで`Joint2_Cylinder`を選択
2. `Joint1_Cylinder`の外にドラッグ＆ドロップして親子関係を解除
3. Hierarchyのルートレベルに空のGameObjectを作成し、名前を「EMG_Controller」に変更

---

### 2. スクリプトのアタッチ

1. `EMG_Controller`オブジェクトを選択
2. `EMGJointController.cs`をドラッグ＆ドロップしてアタッチ

---

### 3. Inspector設定

#### Joint References (親子関係なし)
- **Joint1**: `Joint1_Cylinder`をドラッグ＆ドロップ
- **Joint2**: `Joint2_Cylinder`をドラッグ＆ドロップ
- **Joint1 Length**: Joint1の長さを設定（デフォルト: 1.0）
  - Cylinderのスケールや実際の長さに合わせて調整してください

#### Test Mode（UDP不要のマニュアル制御）
- **Use Manual Control**: ✓チェックを入れるとマニュアルモードに切り替わります
- **Manual Joint1 Angle**: Joint1の角度（-180～180度）
- **Manual Joint2 Angle**: Joint2の角度（-180～180度）

#### EMG Data Source（EMGモード時のみ必要）
- **Emg Processor**: `EMGSignalProcessor`をドラッグ＆ドロップ
  - マニュアルモード時は不要です

---

## 使用方法

### マニュアルモード（テスト用）
1. Inspector で **Use Manual Control** にチェック
2. **Manual Joint1 Angle** と **Manual Joint2 Angle** のスライダーを動かす
3. Game Viewでリアルタイムに動作を確認

**メリット:**
- UDP/EMG入力なしで動作確認可能
- VRでのスケール問題のテストに最適

### EMGモード（実運用）
1. Inspector で **Use Manual Control** のチェックを外す
2. **Emg Processor** を設定
3. BiTalinoからEMG信号を受信して制御

---

## VRスケール問題について

### 問題の原因
親子関係を設定すると、VRで近距離から見た際にスケールが不自然に変わる問題が発生します。

### 解決方法
このスクリプトでは、親子関係を使わずに以下の方法で連動制御します：

1. **Joint1**: Z軸で回転
2. **Joint2の位置**: Joint1の先端位置を計算して配置
   ```
   Joint2の位置 = Joint1の位置 + (Joint1の向き × Joint1の長さ)
   ```
3. **Joint2の回転**: Joint1の回転を継承してからX軸回転を追加

これにより、親子関係なしでも自然な連動動作を実現します。

---

## トラブルシューティング

### Joint2がJoint1の先端に配置されない
- **Joint1 Length**の値を調整してください
- Cylinderのスケールや実際の長さに合わせる必要があります

### マニュアルモードで動かない
- **Use Manual Control**にチェックが入っているか確認
- Game Viewでプレイモードになっているか確認

### EMGモードで動かない
- **Use Manual Control**のチェックを外す
- **Emg Processor**が正しく設定されているか確認
- BiTalinoが正しく接続されているか確認

---

## アタッチメント先まとめ

| オブジェクト | スクリプト | 説明 |
|------------|----------|------|
| EMG_Controller（空のGameObject） | EMGJointController.cs | このスクリプトをアタッチ |
| Joint1_Cylinder | - | Inspectorで参照するのみ |
| Joint2_Cylinder | - | Inspectorで参照するのみ |

---

## 開発者向け情報

### 主要メソッド

- `ApplyJointRotationsWithoutHierarchy()`: 親子関係なしで回転を適用
- `Update()`: マニュアルモードとEMGモードを切り替え
- `InitializeController()`: 初期化（EMGプロセッサーの検索など）

### 座標系

- **Joint1**: Z軸回転（ワールド座標）
- **Joint2**: X軸回転（Joint1の回転を継承）
- Joint2の位置: Joint1の先端に動的に配置

---

## バージョン履歴

- **v2.0**: 親子関係なしで動作、マニュアルモード追加
- **v1.0**: 初版（親子関係前提）
