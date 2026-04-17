using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System;
using System.Collections;
using System.Runtime.InteropServices;

/// <summary>
/// KR Feedback: MarkerTask終了時（freeze期間）に誤差方向と距離を振動で提示
/// スクリプト単体でUDP送信を実行（UDPSender参照不要）
/// </summary>
public class KR_VibrationFeedback : MonoBehaviour
{
    [Header("Network Settings")]
    [Tooltip("Microcontroller IP address")]
    public string nucleoIP = "192.168.2.70";

    [Tooltip("Microcontroller port number")]
    public int nucleoPort = 55555;

    [Header("KR Feedback Settings")]
    [Tooltip("Enable KR feedback after freeze period")]
    public bool enableKRFeedback = true;

    [Tooltip("Maximum distance for intensity calculation")]
    public float maxDistance = 0.5f;

    [Tooltip("Vibration update interval during freeze (seconds)")]
    public float updateInterval = 0.02f; // 50Hz = 20ms間隔

    [Header("References")]
    [Tooltip("MarkerTask reference (auto-found if null)")]
    public MarkerTask markerTask;

    [Header("Debug")]
    [Tooltip("Log sent data to console")]
    public bool logSentData = true;

    [Tooltip("Log detailed debug information")]
    public bool debugMode = true;

    // UDP communication
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;

    // Send counter
    private int sendCount = 0;

    // State tracking
    private bool wasFeedbackActive = false;
    private Coroutine feedbackCoroutine = null;

    // Current feedback state (for logging)
    private int currentVibrator = 0;
    private int currentIntensity = 0;
    public int CurrentVibrator => currentVibrator;
    public int CurrentIntensity => currentIntensity;

    /// <summary>
    /// toMbed構造体 - マイコンに送信するデータ構造
    /// ContinuousVibrationFeedback.csと同じ構造（mbedのdataStruct.hと一致）
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

    void Start()
    {
        InitializeUDP();
        SendAllOff();

        // Find MarkerTask if not assigned
        if (markerTask == null)
        {
            markerTask = FindObjectOfType<MarkerTask>();
        }

        // Disable ContinuousVibrationFeedback if KR feedback is enabled
        if (enableKRFeedback)
        {
            ContinuousVibrationFeedback kvFeedback = FindObjectOfType<ContinuousVibrationFeedback>();
            if (kvFeedback != null)
            {
                kvFeedback.enabled = false;
            }
        }
    }

    void Update()
    {
        if (!enableKRFeedback || markerTask == null) return;

        // Only check during task running
        if (!markerTask.IsTaskRunning())
        {
            if (feedbackCoroutine != null)
            {
                StopCoroutine(feedbackCoroutine);
                feedbackCoroutine = null;
            }
            SendAllOff();
            wasFeedbackActive = false;
            return;
        }

        // MarkerTaskのKR Feedbackフラグを監視
        bool shouldFeedback = markerTask.ShouldProvideKRFeedback;

        // Feedback開始: フラグがfalse→trueに変化
        if (!wasFeedbackActive && shouldFeedback)
        {
            if (feedbackCoroutine != null)
            {
                StopCoroutine(feedbackCoroutine);
            }
            feedbackCoroutine = StartCoroutine(ProvideKRFeedbackDuringFreeze());
        }
        // Feedback終了: フラグがtrue→falseに変化
        else if (wasFeedbackActive && !shouldFeedback)
        {
            if (feedbackCoroutine != null)
            {
                StopCoroutine(feedbackCoroutine);
                feedbackCoroutine = null;
            }
            SendAllOff();
        }

        wasFeedbackActive = shouldFeedback;
    }

    /// <summary>
    /// Initialize UDP communication
    /// </summary>
    private void InitializeUDP()
    {
        try
        {
            udpClient = new UdpClient();
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(nucleoIP), nucleoPort);
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR FB] UDP Init Error: {e.Message}");
        }
    }

    /// <summary>
    /// Provide KR feedback continuously during freeze period
    /// </summary>
    IEnumerator ProvideKRFeedbackDuringFreeze()
    {
        if (markerTask == null || markerTask.ball == null)
        {
            Debug.LogError("[KR FB] MarkerTask or Ball is NULL");
            yield break;
        }

        // Get current marker directly from MarkerTask
        GameObject currentMarker = markerTask.CurrentMarker;

        if (currentMarker == null)
        {
            Debug.LogError($"[KR FB] Marker is NULL (Trial {markerTask.GetTrialCount()})");
            SendAllOff();
            yield break;
        }

        // ★ freeze期間の長さをローカルにコピー（UniTaskとの競合を避ける）
        float freezeDuration = markerTask.freezeDuration;

        // ★ freeze期間開始時（緑→赤に変わった瞬間）のボールの最終位置を取得
        Vector3 finalBallPos = markerTask.ball.position;
        Vector3 targetPos = currentMarker.transform.position;

        // ボールからターゲットへの方向 = ボールを動かすべき方向
        // 例: ターゲットが前（+Z）、ボールが後ろ → dz=正 → 北（2）を振動 = 「前に動かせ」
        float dx = targetPos.x - finalBallPos.x;
        float dz = targetPos.z - finalBallPos.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);

        // Check if perfectly aligned
        float perfectThreshold = 0.001f; // 1mm以内は完璧とみなす（FBなし）

        int vibrator = 0;
        int intensity = 0;

        if (distance < perfectThreshold)
        {
            // Perfect alignment - no vibration
            currentVibrator = 0;
            currentIntensity = 0;
        }
        else
        {
            // Calculate intensity (2,4,6,8,10) based on distance
            if (distance >= maxDistance)
            {
                intensity = 10; // Maximum intensity for out-of-range
            }
            else
            {
                // Map distance to intensity: 2,4,6,8,10
                // distance: 0.001~maxDistance → intensity: 2~10
                int level = Mathf.Clamp((int)Mathf.Ceil(distance / maxDistance * 5), 1, 5);
                intensity = level * 2;
            }

            // Calculate angle for direction
            float angle = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360;

            // Determine vibrator (1-9 grid)
            vibrator = GetVibratorFromAngle(angle);

            // Store current state for logging
            currentVibrator = vibrator;
            currentIntensity = intensity;
        }

        // 必要最小限のログ: 振動場所、強度、距離
        Debug.Log($"[KR FB] Vibrator={vibrator}, Intensity={intensity}, FinalDistance={distance:F4}m");

        // 1秒固定のパケットを1回送信
        if (vibrator > 0 && intensity > 0)
        {
            SendVibrationCommand(vibrator, intensity, freezeDuration); // 1.0秒
        }
        else
        {
            SendAllOff();
        }

        // freeze期間待機
        yield return new WaitForSeconds(freezeDuration);

        // Freeze duration ended - stop vibration
        currentVibrator = 0;
        currentIntensity = 0;
        SendAllOff();
    }

    /// <summary>
    /// Map angle to vibrator number (3x3 grid, center=5 is NOT used)
    /// 振動子配置:
    ///   1(NW)  2(N)   3(NE)
    ///   4(W)   5(中心：未使用)  6(E)
    ///   7(SW)  8(S)   9(SE)
    ///
    /// 8-direction mapping (5は使用しない):
    ///   6(0°/East), 3(45°/NE), 2(90°/North), 1(135°/NW),
    ///   4(180°/West), 7(225°/SW), 8(270°/South), 9(315°/SE)
    /// </summary>
    int GetVibratorFromAngle(float angle)
    {
        // 振動子1-4, 6-9の8方向のみ使用（5は中心なので使わない）
        if (angle >= 337.5f || angle < 22.5f) return 6;      // 0° East
        else if (angle >= 22.5f && angle < 67.5f) return 3;  // 45° NE
        else if (angle >= 67.5f && angle < 112.5f) return 2; // 90° North
        else if (angle >= 112.5f && angle < 157.5f) return 1; // 135° NW
        else if (angle >= 157.5f && angle < 202.5f) return 4; // 180° West
        else if (angle >= 202.5f && angle < 247.5f) return 7; // 225° SW
        else if (angle >= 247.5f && angle < 292.5f) return 8; // 270° South
        else return 9; // 315° SE
    }

    /// <summary>
    /// Send vibration command to a specific vibrator
    /// </summary>
    /// <param name="vibratorNumber">Vibrator number (1-9 for 3x3 grid)</param>
    /// <param name="intensity">Vibration intensity (0, 2, 4, 6, 8, 10)</param>
    /// <param name="duration">Duration in seconds</param>
    public void SendVibrationCommand(int vibratorNumber, int intensity, float duration)
    {
        if (udpClient == null)
        {
            Debug.LogError("[KR FB] UDP client is NULL - cannot send");
            return;
        }

        if (vibratorNumber < 1 || vibratorNumber > 9)
        {
            Debug.LogWarning($"[KR FB] Invalid vibrator number: {vibratorNumber} (must be 1-9)");
            return;
        }

        if (intensity < 0 || intensity > 10 || intensity % 2 != 0)
        {
            Debug.LogWarning($"[KR FB] Invalid intensity: {intensity} (must be 0,2,4,6,8,10)");
            return;
        }

        try
        {
            // Create ToMbed structure
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

            // Clear all channels
            for (int i = 0; i < 10; i++)
            {
                data.Vibration[i] = 0.0;
            }

            // Set intensity for specified vibrator
            // Convert intensity (0-10) to double (0.0-1.0)
            int arrayIndex = vibratorNumber - 1;
            data.Vibration[arrayIndex] = intensity / 10.0;

            // Convert structure to bytes and send
            byte[] bytes = StructToBytes(data);

            if (bytes.Length != 112)
            {
                Debug.LogError($"[KR FB] Invalid packet size: {bytes.Length} bytes (expected 112)");
                return;
            }

            udpClient.Send(bytes, bytes.Length, remoteEndPoint);
            sendCount++;
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR FB] Send Error: {e.Message}");
        }
    }

    /// <summary>
    /// Send all vibrators off command
    /// </summary>
    public void SendAllOff()
    {
        if (udpClient == null) return;

        try
        {
            // Create ToMbed structure with all channels off
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

            // All channels off
            for (int i = 0; i < 10; i++)
            {
                data.Vibration[i] = 0.0;
            }

            // Convert structure to bytes and send
            byte[] bytes = StructToBytes(data);
            udpClient.Send(bytes, bytes.Length, remoteEndPoint);
            sendCount++;
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR FB] All-Off Error: {e.Message}");
        }
    }

    /// <summary>
    /// 構造体を配列に変換
    /// </summary>
    private byte[] StructToBytes(ToMbed data)
    {
        int size = Marshal.SizeOf(data);
        byte[] bytes = new byte[size];
        IntPtr ptr = IntPtr.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(data, ptr, true);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR FB] StructToBytes Error: {e.Message}");
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        return bytes;
    }

    void OnDestroy()
    {
        if (udpClient != null)
        {
            try
            {
                SendAllOff();
                udpClient.Close();
                udpClient = null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[KR FB] OnDestroy Error: {e.Message}");
            }
        }
    }
}
