using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>
/// 2DOF腕の姿勢を背中の3x3振動子グリッドで連続的にフィードバックするシステム
/// 装着位置: 背中上半分
/// グリッド配置:
///   1(左上)  2     3(右上・肩)
///   4        5     6(joint1・肩関節)
///   7        8     9(base・体幹)
/// </summary>
public class ContinuousVibrationFeedback : MonoBehaviour
{
    [Header("Arm References")]
    [Tooltip("肩関節のTransform")]
    public Transform shoulderJoint;

    [Tooltip("肘関節のTransform")]
    public Transform elbowJoint;

    [Header("Arm Parameters")]
    [Tooltip("上腕の長さ (m)")]
    public float upperArmLength = 0.3f;

    [Tooltip("前腕の長さ (m)")]
    public float forearmLength = 0.3f;

    [Header("Joint Angle Settings")]
    [Tooltip("肩の基準角度（この角度で回転軸が振動子6の中心）")]
    public float shoulderPitchOffset = 25f;

    [Tooltip("オフセットからの変動範囲（±50°で振動子6-3または6-9の間）")]
    public float shoulderPitchRange = 50f;

    [Header("Vibration Intensity")]
    [Range(0, 255)]
    [Tooltip("最大振動強度 (0-255)")]
    public int maxIntensity = 255;

    [Range(0f, 1f)]
    [Tooltip("振動子5の最大強度比率（メイン振動子に対して）")]
    public float centerVibratorRatio = 0.7f;

    [Header("UDP Communication")]
    [Tooltip("マイコン(NUCLEO-F767ZI)のIPアドレス")]
    public string stm32IpAddress = "192.168.2.70";

    [Tooltip("マイコンのUDPポート番号")]
    public int stm32Port = 55555;

    [Tooltip("UDP送信を有効化")]
    public bool enableUdpSend = true;

    [Header("Debug")]
    [Tooltip("Sceneビューにデバッグギズモを表示")]
    public bool showDebugGizmos = true;

    [Tooltip("Consoleにデバッグログを出力")]
    public bool showDebugLog = false;

    #region Private Fields

    // UDP通信用
    private UdpClient udpClient;
    private IPEndPoint endPoint;

    // 振動強度配列（9個の振動子: インデックス0-8が振動子1-9に対応）
    private byte[] vibrationIntensities = new byte[9];

    // 送信カウント
    private int sendCount = 0;

    // 肘角度による基本振動子マッピング（30°刻み）
    private readonly int[] elbowAngleThresholds = { 0, 30, 60, 90, 120, 150, 160 };
    private readonly int[] elbowBaseVibrators = { 3, 2, 1, 4, 7, 8, 8 }; // 1-indexed (後で-1する)

    // デバッグ用
    private float currentShoulderPitch;
    private float currentElbowAngle;
    private Vector3 currentHandPosition;

    /// <summary>
    /// toMbed構造体 - マイコンに送信するデータ構造
    /// HapticUDPSender.csと同じ構造（mbedのdataStruct.hと一致）
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private struct ToMbed
    {
        public int vibration_intensity;        // offset: 0, size: 4

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public double[] Vibration;              // offset: 4, size: 80 (10×8)

        public float ch1;                       // offset: 84, size: 4
        public float ch2;                       // offset: 88, size: 4
        public float ch3;                       // offset: 92, size: 4
        public float ch4;                       // offset: 96, size: 4

        [MarshalAs(UnmanagedType.U1)]
        public byte servo1;                     // offset: 100, size: 1 (bool -> byte)

        [MarshalAs(UnmanagedType.U1)]
        public byte servo2;                     // offset: 101, size: 1 (bool -> byte)

        // パディングは自動的に挿入される（offset: 102-103, size: 2）

        public int checkCount;                  // offset: 104, size: 4
        public int returnCount;                 // offset: 108, size: 4
    }
    // 合計: 112 bytes (パディング込み)

    #endregion

    #region Unity Lifecycle

    void Start()
    {
        InitializeUDP();
        Debug.Log("ContinuousVibrationFeedback initialized");
    }

    void Update()
    {
        if (!ValidateReferences())
        {
            return;
        }

        // 関節角度を取得
        currentShoulderPitch = GetShoulderPitchAngle();
        currentElbowAngle = GetElbowAngle();

        // Forward Kinematicsで手先位置を計算（デバッグ用）
        currentHandPosition = CalculateForwardKinematics(currentShoulderPitch, currentElbowAngle);

        // 振動パターンを計算
        CalculateVibrationPattern(currentShoulderPitch, currentElbowAngle);

        // UDP送信
        if (enableUdpSend)
        {
            SendVibrationData();
        }

        // デバッグログ
        if (showDebugLog)
        {
            LogVibrationPattern();
        }
    }

    void OnDestroy()
    {
        CleanupUDP();
    }

    void OnDrawGizmos()
    {
        if (!showDebugGizmos || !Application.isPlaying)
            return;

        if (!ValidateReferences())
            return;

        // 腕の描画
        DrawArm();

        // 振動子グリッドの描画
        DrawVibrationGrid();
    }

    #endregion

    #region Core Methods

    /// <summary>
    /// UDP通信の初期化
    /// </summary>
    private void InitializeUDP()
    {
        try
        {
            udpClient = new UdpClient();
            endPoint = new IPEndPoint(IPAddress.Parse(stm32IpAddress), stm32Port);
            Debug.Log($"UDP initialized: {stm32IpAddress}:{stm32Port}");
        }
        catch (Exception e)
        {
            Debug.LogError($"UDP initialization failed: {e.Message}");
        }
    }

    /// <summary>
    /// UDP通信のクリーンアップ
    /// </summary>
    private void CleanupUDP()
    {
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
    }

    /// <summary>
    /// Transform参照の検証
    /// </summary>
    private bool ValidateReferences()
    {
        if (shoulderJoint == null)
        {
            if (showDebugLog)
                Debug.LogWarning("Shoulder joint is not assigned!");
            return false;
        }

        if (elbowJoint == null)
        {
            if (showDebugLog)
                Debug.LogWarning("Elbow joint is not assigned!");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 肩のPitch角度を取得
    /// </summary>
    private float GetShoulderPitchAngle()
    {
        if (shoulderJoint == null) return 0f;

        // localRotation.eulerAngles.xから取得
        float angle = shoulderJoint.localRotation.eulerAngles.x;

        // 0-360度を-180~180度に変換
        if (angle > 180f)
            angle -= 360f;

        return angle;
    }

    /// <summary>
    /// 肘の角度を取得
    /// </summary>
    private float GetElbowAngle()
    {
        if (elbowJoint == null) return 0f;

        // localRotation.eulerAngles.zから取得
        float angle = elbowJoint.localRotation.eulerAngles.z;

        // 0-360度を0-180度の範囲に調整（肘は0-160度の可動域）
        if (angle > 180f)
            angle = 360f - angle;

        return Mathf.Clamp(angle, 0f, 160f);
    }

    /// <summary>
    /// Forward Kinematics計算（2DOF）
    /// </summary>
    private Vector3 CalculateForwardKinematics(float shoulderPitch, float elbowAngle)
    {
        float shoulderPitchRad = shoulderPitch * Mathf.Deg2Rad;
        float elbowRad = elbowAngle * Mathf.Deg2Rad;

        // 肘の位置
        float elbowY = upperArmLength * Mathf.Sin(shoulderPitchRad);
        float elbowZ = upperArmLength * Mathf.Cos(shoulderPitchRad);

        // 手先の位置
        float totalAngle = shoulderPitch + elbowAngle;
        float totalRad = totalAngle * Mathf.Deg2Rad;

        float handY = elbowY + forearmLength * Mathf.Sin(totalRad);
        float handZ = elbowZ + forearmLength * Mathf.Cos(totalRad);

        return new Vector3(0, handY, handZ);
    }

    /// <summary>
    /// 振動パターンを計算
    /// </summary>
    private void CalculateVibrationPattern(float shoulderPitch, float elbowAngle)
    {
        // 配列を初期化
        Array.Clear(vibrationIntensities, 0, vibrationIntensities.Length);

        // === ステップ1: 縦方向オフセット計算 ===
        // shoulderPitch = shoulderPitchOffset (25°) → offset = 0 (振動子6が中心)
        // shoulderPitch = shoulderPitchOffset - 50° (-25°) → offset = -1 (下にシフト)
        // shoulderPitch = shoulderPitchOffset + 50° (75°) → offset = +1 (上にシフト)
        float verticalOffset = (shoulderPitch - shoulderPitchOffset) / shoulderPitchRange;
        verticalOffset = Mathf.Clamp(verticalOffset, -1f, 1f);

        // === ステップ2: 肘角度による基本振動子選択 ===
        int lowerIndex = 0;
        for (int i = 0; i < elbowAngleThresholds.Length - 1; i++)
        {
            if (elbowAngle >= elbowAngleThresholds[i] && elbowAngle < elbowAngleThresholds[i + 1])
            {
                lowerIndex = i;
                break;
            }
            else if (elbowAngle >= elbowAngleThresholds[elbowAngleThresholds.Length - 1])
            {
                lowerIndex = elbowAngleThresholds.Length - 2;
                break;
            }
        }

        int upperIndex = Mathf.Min(lowerIndex + 1, elbowAngleThresholds.Length - 1);

        // 補間重み
        float angleLower = elbowAngleThresholds[lowerIndex];
        float angleUpper = elbowAngleThresholds[upperIndex];
        float range = angleUpper - angleLower;
        float weight = range > 0 ? (elbowAngle - angleLower) / range : 0f;

        // === ステップ3: 縦シフトを適用した振動子選択 ===
        int baseMainVibrator = elbowBaseVibrators[lowerIndex];
        int baseNextVibrator = elbowBaseVibrators[upperIndex];

        int mainVibrator = GetShiftedVibrator(baseMainVibrator, verticalOffset);
        int nextVibrator = GetShiftedVibrator(baseNextVibrator, verticalOffset);

        // === ステップ4: メイン振動子の強度設定 ===
        vibrationIntensities[mainVibrator - 1] = (byte)((1f - weight) * maxIntensity);

        if (mainVibrator != nextVibrator)
        {
            vibrationIntensities[nextVibrator - 1] = (byte)(weight * maxIntensity);
        }

        // === ステップ5: 振動子5の重なり計算 ===
        // 90°で最大、そこから離れるほど減少
        float angleFrom90 = Mathf.Abs(elbowAngle - 90f);
        float overlapFactor = Mathf.Clamp01(1f - angleFrom90 / 90f);

        // 振動子5もverticalOffsetの影響を受ける
        int centerVibrator = GetShiftedVibrator(5, verticalOffset);

        // 中央振動子の強度を加算
        int centerIntensity = (int)(overlapFactor * maxIntensity * centerVibratorRatio);
        vibrationIntensities[centerVibrator - 1] = (byte)Mathf.Min(255,
            vibrationIntensities[centerVibrator - 1] + centerIntensity);
    }

    /// <summary>
    /// 縦方向のオフセットを適用して振動子番号をシフト
    /// </summary>
    /// <param name="baseVibrator">基本振動子番号 (1-9)</param>
    /// <param name="verticalOffset">縦方向オフセット (-1.0 ~ +1.0)</param>
    /// <returns>シフト後の振動子番号 (1-9)</returns>
    private int GetShiftedVibrator(int baseVibrator, float verticalOffset)
    {
        // 振動子番号を行・列に分解
        // 1,2,3 → row=0 (上)
        // 4,5,6 → row=1 (中)
        // 7,8,9 → row=2 (下)
        int row = (baseVibrator - 1) / 3;
        int col = (baseVibrator - 1) % 3;

        // 行をシフト (offset = +1で上にシフト、-1で下にシフト)
        float newRowFloat = row - verticalOffset; // 符号反転: offset+1で上(row小)へ
        int newRow = Mathf.RoundToInt(newRowFloat);
        newRow = Mathf.Clamp(newRow, 0, 2);

        // 新しい振動子番号
        int shiftedVibrator = newRow * 3 + col + 1;

        return shiftedVibrator;
    }

    /// <summary>
    /// UDP経由で振動データをマイコンに送信
    /// </summary>
    private void SendVibrationData()
    {
        if (udpClient == null || endPoint == null)
        {
            return;
        }

        try
        {
            // ToMbed構造体を作成
            ToMbed data = new ToMbed
            {
                vibration_intensity = 1,
                Vibration = new double[10],
                ch1 = 0f,
                ch2 = 0f,
                ch3 = 0f,
                ch4 = 0f,
                servo1 = 0,
                servo2 = 0,
                checkCount = sendCount,
                returnCount = 0
            };

            // byte[9]（0-255）をdouble[10]（0.0-1.0）に変換
            // 9チャンネル分のデータを設定（10チャンネル目は0）
            for (int i = 0; i < 9; i++)
            {
                data.Vibration[i] = vibrationIntensities[i] / 255.0;
            }
            data.Vibration[9] = 0.0; // 10チャンネル目は未使用

            // 構造体をバイト配列に変換
            byte[] bytes = StructToBytes(data);

            // UDP送信
            udpClient.Send(bytes, bytes.Length, endPoint);

            sendCount++;

            if (showDebugLog)
            {
                Debug.Log($"UDP Send #{sendCount}: Size={bytes.Length} bytes, Target={endPoint.Address}:{endPoint.Port}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"UDP send failed: {e.Message}");
        }
    }

    /// <summary>
    /// 構造体をバイト配列に変換するヘルパーメソッド
    /// </summary>
    private byte[] StructToBytes(ToMbed data)
    {
        int size = Marshal.SizeOf(data);
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(data, ptr, true);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return bytes;
    }

    /// <summary>
    /// デバッグログ出力
    /// </summary>
    private void LogVibrationPattern()
    {
        string pattern = "Vibration: [";
        for (int i = 0; i < vibrationIntensities.Length; i++)
        {
            pattern += vibrationIntensities[i];
            if (i < vibrationIntensities.Length - 1)
                pattern += ", ";
        }
        pattern += "]";

        Debug.Log($"Shoulder: {currentShoulderPitch:F1}°, Elbow: {currentElbowAngle:F1}° | {pattern}");
    }

    #endregion

    #region Debug Visualization

    /// <summary>
    /// 腕の描画
    /// </summary>
    private void DrawArm()
    {
        Vector3 shoulderPos = shoulderJoint.position;
        Vector3 elbowPos = elbowJoint != null ? elbowJoint.position : shoulderPos;
        Vector3 handPos = shoulderPos + shoulderJoint.rotation * currentHandPosition;

        // 上腕
        Gizmos.color = Color.white;
        Gizmos.DrawLine(shoulderPos, elbowPos);

        // 前腕
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(elbowPos, handPos);

        // 手先
        Gizmos.color = Color.green;
        Gizmos.DrawSphere(handPos, 0.02f);
    }

    /// <summary>
    /// 振動子グリッドの描画
    /// </summary>
    private void DrawVibrationGrid()
    {
        Vector3 shoulderPos = shoulderJoint.position;

        // 背中のグリッド位置（肩の後ろ）
        Vector3 gridCenter = shoulderPos + shoulderJoint.rotation * new Vector3(0, 0, -0.2f);

        float cellSize = 0.08f;

        // 3x3グリッドを描画
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                int vibratorIndex = row * 3 + col;
                int vibratorNumber = vibratorIndex + 1;

                Vector3 cellPos = gridCenter + new Vector3(
                    (col - 1) * cellSize,
                    -(row - 1) * cellSize,
                    0
                );

                cellPos = shoulderJoint.rotation * (cellPos - gridCenter) + gridCenter;

                // 振動強度に応じて色とサイズを変更
                float intensity = vibrationIntensities[vibratorIndex] / 255f;

                if (intensity > 0.01f)
                {
                    Gizmos.color = new Color(1f, 0f, 0f, intensity);
                    Gizmos.DrawSphere(cellPos, cellSize * 0.3f * intensity);
                }

                // グリッド枠
                Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                Gizmos.DrawWireCube(cellPos, Vector3.one * cellSize * 0.8f);

#if UNITY_EDITOR
                // 振動子番号を表示
                UnityEditor.Handles.Label(cellPos, vibratorNumber.ToString());
#endif
            }
        }
    }

    #endregion
}
