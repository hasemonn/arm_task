using UnityEngine;
using TMPro;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using Cysharp.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

/// <summary>
/// MarkerTaskとKR Feedbackを統合したスクリプト
/// freeze期間中に直接UDP送信を実行
/// </summary>
public class MarkerTask_KR_Integrated : MonoBehaviour
{
    [Header("Arm References")]
    public Transform shoulderJoint;
    public Transform elbowJoint;
    public Transform cylinder3;
    public Transform ball;

    [Header("Target Patterns")]
    public JointAnglePattern[] targetPatterns = new JointAnglePattern[]
    {
        new JointAnglePattern { joint1Angle = 42f, joint2Angle = 137f, patternName = "Pattern1" },
        new JointAnglePattern { joint1Angle = -18f, joint2Angle = 83f, patternName = "Pattern2" },
        new JointAnglePattern { joint1Angle = 67f, joint2Angle = 12f, patternName = "Pattern3" },
        new JointAnglePattern { joint1Angle = 5f, joint2Angle = 88f, patternName = "Pattern4" },
        new JointAnglePattern { joint1Angle = -30f, joint2Angle = 154f, patternName = "Pattern5" },
        new JointAnglePattern { joint1Angle = 28f, joint2Angle = 45f, patternName = "Pattern6" },
        new JointAnglePattern { joint1Angle = 74f, joint2Angle = 101f, patternName = "Pattern7" },
        new JointAnglePattern { joint1Angle = -5f, joint2Angle = 160f, patternName = "Pattern8" },
        new JointAnglePattern { joint1Angle = 60f, joint2Angle = 7f, patternName = "Pattern9" },
        new JointAnglePattern { joint1Angle = 15f, joint2Angle = 129f, patternName = "Pattern10" }
    };

    [System.Serializable]
    public struct JointAnglePattern
    {
        public string patternName;
        public float joint1Angle;
        public float joint2Angle;
    }

    [Header("UI References")]
    public TextMeshProUGUI countdownText;

    [Header("Task Settings")]
    public int totalTrials = 10;
    public float initialFreezeDuration = 1.0f;
    public float movementDuration = 3.0f;
    public float freezeDuration = 1.0f;
    public float markerSize = 0.05f;

    [Header("KR Feedback Settings")]
    public bool enableKRFeedback = true;
    public string nucleoIP = "192.168.2.70";
    public int nucleoPort = 55555;
    public float maxDistance = 0.5f;
    public float krUpdateInterval = 0.02f;

    [Tooltip("振動フィードバックの時間（秒）")]
    public float vibrationDuration = 1.0f;

    [Tooltip("視覚フィードバック表示時間（秒）")]
    public float visualFeedbackDuration = 1.0f;

    [Header("Data Logging")]
    public bool enableLogging = true;
    public SaveDestination saveDestination = SaveDestination.KR;
    public string subFolderName = "";

    public enum SaveDestination
    {
        FB無し,
        KP,
        KR
    }

    [Header("References")]
    public IndependentEMGJointController jointController;

    // 現在のターゲット情報
    private Vector3 currentTargetPosition;
    private float targetShoulderPitch;
    private float targetElbowAngle;

    // 現在のマーカー
    private GameObject currentMarker;
    public GameObject CurrentMarker => currentMarker;

    // タスク状態
    private bool taskRunning = false;
    public bool IsTaskRunning() => taskRunning;

    private int trialCount = 0;
    public int GetTrialCount() => trialCount;

    private CancellationTokenSource cancellationTokenSource;
    private string statusMessage = "";

    // データ記録
    private List<MotionData> dataBuffer = new List<MotionData>();
    private List<TrialSummary> trialSummaries = new List<TrialSummary>();
    private bool shouldLogKRFeedback = false;

    // パス長計算用
    private Vector3 trialStartPosition;
    private float actualPathLength = 0f;
    private Vector3 lastPosition = Vector3.zero;

    // データサンプリング
    private const float LOG_INTERVAL = 0.001f;
    private float lastLogTime = 0f;
    private float movementPhaseStartTime = 0f;

    // 関節角度の前回値
    private float[] lastJointAngles = new float[2];

    // UDP通信
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    private int sendCount = 0;

    // KR Feedback状態
    private int currentVibrator = 0;
    private int currentIntensity = 0;
    public int CurrentVibrator => currentVibrator;
    public int CurrentIntensity => currentIntensity;

    // 腕とマーカーの表示/非表示用
    private List<Renderer> armRenderers = new List<Renderer>();

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private struct ToMbed
    {
        public int vibration_intensity;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public double[] Vibration;
        public float ch1;
        public float ch2;
        public float ch3;
        public float ch4;
        [MarshalAs(UnmanagedType.U1)]
        public byte servo1;
        [MarshalAs(UnmanagedType.U1)]
        public byte servo2;
        public int checkCount;
        public int returnCount;
    }

    [System.Serializable]
    public struct TrialSummary
    {
        public int trialNumber;
        public string patternName;
        public float targetJoint1;
        public float targetJoint2;
        public float movementTime;
        public float pathLength;
        public float pathEfficiency;
        public float averagePositionError;
        public float averageJointSpaceError;
        public float peakJerk;
        public bool taskSuccess;
        public float finalDistance;
        public float averageFBIntensity;
        public int finalFBVibrator;
        public float movementSmoothness;
        public int numberOfSubmovements;
        public float fbActivationRate;
        public float movementDuringFB;
    }

    [System.Serializable]
    public struct MotionData
    {
        public float timestamp;
        public int trialNumber;
        public float targetShoulderPitch;
        public float targetElbowAngle;
        public Vector3 targetPosition;
        public Vector3 actualPosition;
        public float actualShoulderPitch;
        public float actualElbowAngle;
        public float positionError;
        public float jointSpaceError;
        public float shoulderPitchVelocity;
        public float elbowVelocity;
        public float shoulderPitchJerk;
        public float elbowJerk;
        public float pathLength;
        public float optimalPathLength;
        public float pathEfficiency;
        public int fbVibrator;
        public int fbIntensity;
    }

    void Start()
    {
        lastJointAngles = new float[2];

        if (jointController == null)
        {
            jointController = FindObjectOfType<IndependentEMGJointController>();
        }

        if (ball != null)
        {
            markerSize = ball.localScale.x;
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }

        shouldLogKRFeedback = (saveDestination == SaveDestination.KR);

        // UDP初期化
        if (enableKRFeedback)
        {
            InitializeUDP();
            SendAllOff();
        }

        // バックグラウンドでも実行を継続（VR対応）
        Application.runInBackground = true;

        // 腕のRendererを収集
        CollectArmRenderers();
    }

    void CollectArmRenderers()
    {
        armRenderers.Clear();

        AddRenderersToList(shoulderJoint);
        AddRenderersToList(elbowJoint);
        AddRenderersToList(cylinder3);
        AddRenderersToList(ball);

        Debug.Log($"[MarkerTask] Collected {armRenderers.Count} renderers for arm parts.");
    }


    void AddRenderersToList(Transform target)
    {
        if (target != null)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                armRenderers.Add(r);
            }
        }
    }

    void HideArmAndMarker()
    {
        // 腕の全パーツ（cylinder, ball含む）を非表示
        foreach (var renderer in armRenderers)
        {
            if (renderer != null) renderer.enabled = false;
        }

        // マーカー（ターゲット）を非表示
        if (currentMarker != null)
        {
            currentMarker.SetActive(false);
        }
    }

    void ShowArmAndMarker()
    {
        // 腕の全パーツを表示
        foreach (var renderer in armRenderers)
        {
            if (renderer != null) renderer.enabled = true;
        }

        // マーカー（ターゲット）を表示
        if (currentMarker != null)
        {
            currentMarker.SetActive(true);
        }
    }

    void InitializeUDP()
    {
        try
        {
            udpClient = new UdpClient();
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(nucleoIP), nucleoPort);
            Debug.Log($"[KR Integrated] UDP initialized: {nucleoIP}:{nucleoPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR Integrated] UDP Init Error: {e.Message}");
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space) && !taskRunning)
        {
            StartTask();
        }

        if (Input.GetKeyDown(KeyCode.Escape) && taskRunning)
        {
            StopTask();
        }
    }

    public void StartTask()
    {
        if (taskRunning) return;

        cancellationTokenSource = new CancellationTokenSource();
        taskRunning = true;
        trialCount = 0;
        trialSummaries.Clear();

        RunTaskAsync(cancellationTokenSource.Token).Forget();
    }

    public void StopTask()
    {
        if (!taskRunning) return;

        cancellationTokenSource?.Cancel();
        taskRunning = false;

        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }

        if (enableKRFeedback)
        {
            SendAllOff();
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }
    }

    async UniTask RunTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < totalTrials; i++)
            {
                int patternIndex = i % targetPatterns.Length;
                await RunSingleTrialAsync(patternIndex, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (enableLogging)
            {
                SaveAllData();
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Task cancelled by user");
        }
        finally
        {
            taskRunning = false;
            if (currentMarker != null)
            {
                Destroy(currentMarker);
                currentMarker = null;
            }
            if (countdownText != null)
            {
                countdownText.gameObject.SetActive(false);
            }
        }
    }

    async UniTask RunSingleTrialAsync(int patternIndex, CancellationToken cancellationToken)
    {
        trialCount++;
        JointAnglePattern pattern = targetPatterns[patternIndex];

        targetShoulderPitch = pattern.joint1Angle;
        targetElbowAngle = pattern.joint2Angle;
        currentTargetPosition = CalculateForwardKinematics(targetShoulderPitch, targetElbowAngle);

        // マーカー作成
        CreateMarker(currentTargetPosition);

        // 初期位置固定
        if (jointController != null)
        {
            jointController.FreezeArm();
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(true);
            countdownText.text = "Ready...";
        }

        await UniTask.Delay(TimeSpan.FromSeconds(initialFreezeDuration), DelayType.Realtime, cancellationToken: cancellationToken);

        // 動作開始（緑色期間）
        if (currentMarker != null)
        {
            Renderer markerRenderer = currentMarker.GetComponent<Renderer>();
            if (markerRenderer != null)
            {
                markerRenderer.material.color = Color.green;
            }
        }

        if (jointController != null)
        {
            jointController.UnfreezeArm();
        }

        movementPhaseStartTime = Time.time;
        lastLogTime = Time.time;
        trialStartPosition = ball.position;
        actualPathLength = 0f;
        lastPosition = Vector3.zero;
        dataBuffer.Clear();

        // 緑色期間：腕とマーカーを非表示
        HideArmAndMarker();

        statusMessage = $"Trial {trialCount}/{totalTrials} - Move to target";
        float movementStartTime = Time.time;

        // 動作期間中のデータロギング
        while (Time.time - movementStartTime < movementDuration)
        {
            if (Time.time - lastLogTime >= LOG_INTERVAL)
            {
                LogData();
                lastLogTime = Time.time;
            }

            if (countdownText != null)
            {
                float remaining = movementDuration - (Time.time - movementStartTime);
                countdownText.text = $"{remaining:F1}s";
            }

            await UniTask.Yield(cancellationToken);
        }

        float movementEndTime = Time.time;

        // Freeze期間（赤色期間）- KR Feedback統合
        if (currentMarker != null)
        {
            Renderer markerRenderer = currentMarker.GetComponent<Renderer>();
            if (markerRenderer != null)
            {
                markerRenderer.material.color = Color.red;
            }
        }

        if (jointController != null)
        {
            jointController.FreezeArm();
        }

        // Phase 1: 振動フィードバックのみ（腕・マーカー非表示）
        await ProvideFreezeKRFeedbackAsync(cancellationToken);

        // Phase 2: 視覚フィードバック（腕とマーカーを表示）
        ShowArmAndMarker();
        await UniTask.Delay(TimeSpan.FromSeconds(visualFeedbackDuration), cancellationToken: cancellationToken);

        if (jointController != null)
        {
            jointController.UnfreezeArm();
        }

        // 結果計算
        float movementTime = movementEndTime - movementStartTime;
        Vector3 finalBallPos = ball.position;
        float finalDistance = Vector3.Distance(finalBallPos, currentTargetPosition);
        bool taskSuccess = finalDistance <= 0.05f;

        SaveTrialSummary(patternIndex, movementTime, finalDistance, taskSuccess);
        SaveTrialDetailedMotionCSV(trialCount, patternIndex);

        // マーカー削除
        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }

        await UniTask.Delay(500, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Freeze期間中のKR Feedback（統合版）- 振動フィードバックのみ
    /// </summary>
    async UniTask ProvideFreezeKRFeedbackAsync(CancellationToken cancellationToken)
    {
        if (!enableKRFeedback || ball == null || currentMarker == null)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(vibrationDuration), cancellationToken: cancellationToken);
            return;
        }

        // ★ freeze開始時のボール位置を取得
        Vector3 finalBallPos = ball.position;
        Vector3 targetPos = currentMarker.transform.position;

        // 距離と方向を計算
        float dx = targetPos.x - finalBallPos.x;
        float dz = targetPos.z - finalBallPos.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);

        // 振動子と強度を決定
        int vibrator = 0;
        int intensity = 0;

        float perfectThreshold = 0.001f;

        if (distance < perfectThreshold)
        {
            currentVibrator = 0;
            currentIntensity = 0;
        }
        else
        {
            if (distance >= maxDistance)
            {
                intensity = 10;
            }
            else
            {
                int level = Mathf.Clamp((int)Mathf.Ceil(distance / maxDistance * 5), 1, 5);
                intensity = level * 2;
            }

            float angle = Mathf.Atan2(dz, dx) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360;

            vibrator = GetVibratorFromAngle(angle);

            currentVibrator = vibrator;
            currentIntensity = intensity;
        }

        Debug.Log($"[KR Integrated] Vibrator={vibrator}, Intensity={intensity}, FinalDistance={distance:F4}m");

        // 振動フィードバックを送信（vibrationDuration秒間固定）
        float startTime = Time.time;
        while (Time.time - startTime < vibrationDuration)
        {
            if (vibrator > 0 && intensity > 0)
            {
                SendVibrationCommand(vibrator, intensity);
            }
            else
            {
                SendAllOff();
            }

            // krUpdateInterval（0.02s = 50Hz）ごとに送信
            await UniTask.Delay(TimeSpan.FromSeconds(krUpdateInterval), cancellationToken: cancellationToken);
        }
        // 振動終了
        currentVibrator = 0;
        currentIntensity = 0;
        SendAllOff();
    }

    int GetVibratorFromAngle(float angle)
    {
        if (angle >= 337.5f || angle < 22.5f) return 6;
        else if (angle >= 22.5f && angle < 67.5f) return 3;
        else if (angle >= 67.5f && angle < 112.5f) return 2;
        else if (angle >= 112.5f && angle < 157.5f) return 1;
        else if (angle >= 157.5f && angle < 202.5f) return 4;
        else if (angle >= 202.5f && angle < 247.5f) return 7;
        else if (angle >= 247.5f && angle < 292.5f) return 8;
        else return 9;
    }

    void SendVibrationCommand(int vibratorNumber, int intensity)
    {
        if (udpClient == null || vibratorNumber < 1 || vibratorNumber > 9) return;

        try
        {
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

            for (int i = 0; i < 10; i++)
            {
                data.Vibration[i] = 0.0;
            }

            int arrayIndex = vibratorNumber - 1;
            data.Vibration[arrayIndex] = intensity / 10.0;

            byte[] bytes = StructToBytes(data);
            udpClient.Send(bytes, bytes.Length, remoteEndPoint);
            sendCount++;
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR Integrated] Send Error: {e.Message}");
        }
    }

    void SendAllOff()
    {
        if (udpClient == null) return;

        try
        {
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

            for (int i = 0; i < 10; i++)
            {
                data.Vibration[i] = 0.0;
            }

            byte[] bytes = StructToBytes(data);
            udpClient.Send(bytes, bytes.Length, remoteEndPoint);
            sendCount++;
        }
        catch (Exception e)
        {
            Debug.LogError($"[KR Integrated] All-Off Error: {e.Message}");
        }
    }

    byte[] StructToBytes(ToMbed data)
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
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        return bytes;
    }

    void CreateMarker(Vector3 position)
    {
        currentMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        currentMarker.name = "TargetMarker";
        currentMarker.transform.position = position;
        currentMarker.transform.localScale = Vector3.one * markerSize;

        Renderer renderer = currentMarker.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Color.yellow;
        }

        Collider collider = currentMarker.GetComponent<Collider>();
        if (collider != null)
        {
            Destroy(collider);
        }
    }

    Vector3 CalculateForwardKinematics(float shoulderPitch, float elbowAngle)
    {
        if (jointController == null)
        {
            statusMessage = "ERROR: jointController is null";
            return Vector3.zero;
        }

        float baseToJoint1Length = jointController.BaseToJoint1Length;
        float joint1ToJoint2Length = jointController.Joint1ToJoint2Length;
        float link2Length = jointController.Link2Length;

        Transform baseObject = jointController.BaseObject;
        if (baseObject == null)
        {
            statusMessage = "ERROR: BaseObject is null";
            return Vector3.zero;
        }

        Vector3 basePosition = baseObject.position;
        Quaternion baseRotation = baseObject.rotation;

        Vector3 upDirection = baseRotation * Vector3.up;

        // === Joint1の位置と回転 ===
        Vector3 joint1Position = basePosition + upDirection * baseToJoint1Length;

        Vector3 joint1RotationAxis = jointController.Joint1RotationAxis;
        Quaternion joint1Rotation = Quaternion.AngleAxis(shoulderPitch, joint1RotationAxis);
        Quaternion joint1WorldRotation = baseRotation * joint1Rotation;

        // === Joint2の位置と回転 ===
        Vector3 joint1ToJoint2Direction = joint1WorldRotation * Vector3.up;
        Vector3 joint2Position = joint1Position + joint1ToJoint2Direction * joint1ToJoint2Length;

        Vector3 joint2RotationAxis = jointController.Joint2RotationAxis;
        Quaternion joint2Rotation = Quaternion.AngleAxis(elbowAngle, joint2RotationAxis);
        Quaternion joint2WorldRotation = joint1WorldRotation * joint2Rotation;

        // === エンドエフェクタの位置 ===
        Vector3 joint2ToEndDirection = joint2WorldRotation * Vector3.up;
        Vector3 endEffectorPosition = joint2Position + joint2ToEndDirection * link2Length;

        return endEffectorPosition;
    }

    void LogData()
    {
        Vector3 currentPosition = ball.position;

        if (lastPosition != Vector3.zero)
        {
            actualPathLength += Vector3.Distance(currentPosition, lastPosition);
        }

        lastPosition = currentPosition;

        Vector2 currentAngles = jointController != null ? jointController.GetJointAngles() : Vector2.zero;
        float currentShoulderPitch = currentAngles.x;
        float currentElbowAngle = currentAngles.y;

        float positionError = Vector3.Distance(currentPosition, currentTargetPosition);
        float jointSpaceError = CalculateJointSpaceError(currentAngles, targetShoulderPitch, targetElbowAngle);

        float deltaTime = LOG_INTERVAL;
        float shoulderVel = (currentShoulderPitch - lastJointAngles[0]) / deltaTime;
        float elbowVel = (currentElbowAngle - lastJointAngles[1]) / deltaTime;

        float shoulderJerk = 0f;
        float elbowJerk = 0f;

        float optimalPathLength = Vector3.Distance(trialStartPosition, currentTargetPosition);
        float pathEfficiency = optimalPathLength > 0 ? optimalPathLength / Mathf.Max(actualPathLength, 0.001f) : 0f;

        float trialElapsedTime = Time.time - movementPhaseStartTime;

        int fbVibrator = 0;
        int fbIntensity = 0;

        MotionData data = new MotionData
        {
            timestamp = trialElapsedTime,
            trialNumber = trialCount,
            targetShoulderPitch = targetShoulderPitch,
            targetElbowAngle = targetElbowAngle,
            targetPosition = currentTargetPosition,
            actualPosition = currentPosition,
            actualShoulderPitch = currentShoulderPitch,
            actualElbowAngle = currentElbowAngle,
            positionError = positionError,
            jointSpaceError = jointSpaceError,
            shoulderPitchVelocity = shoulderVel,
            elbowVelocity = elbowVel,
            shoulderPitchJerk = shoulderJerk,
            elbowJerk = elbowJerk,
            pathLength = actualPathLength,
            optimalPathLength = optimalPathLength,
            pathEfficiency = pathEfficiency,
            fbVibrator = fbVibrator,
            fbIntensity = fbIntensity
        };

        dataBuffer.Add(data);

        lastJointAngles[0] = currentShoulderPitch;
        lastJointAngles[1] = currentElbowAngle;
    }

    float CalculateJointSpaceError(Vector2 currentAngles, float targetShoulder, float targetElbow)
    {
        float error = 0f;
        error += Mathf.Abs(Mathf.DeltaAngle(currentAngles[0], targetShoulder));
        error += Mathf.Abs(Mathf.DeltaAngle(currentAngles[1], targetElbow));
        return error / 2f;
    }

    void SaveTrialSummary(int patternIndex, float movementTime, float finalDistance, bool taskSuccess)
    {
        if (dataBuffer.Count == 0) return;

        float totalPositionError = 0f;
        float totalJointSpaceError = 0f;
        float peakJerk = 0f;
        float totalFBIntensity = 0f;
        int fbSampleCount = 0;
        int finalFBVibrator = 0;
        float sumSquaredElbowJerk = 0f;

        foreach (var data in dataBuffer)
        {
            totalPositionError += data.positionError;
            totalJointSpaceError += data.jointSpaceError;

            float maxJerkThisFrame = Mathf.Max(Mathf.Abs(data.shoulderPitchJerk), Mathf.Abs(data.elbowJerk));
            peakJerk = Mathf.Max(peakJerk, maxJerkThisFrame);

            sumSquaredElbowJerk += data.elbowJerk * data.elbowJerk;
        }

        float avgPositionError = totalPositionError / dataBuffer.Count;
        float avgJointSpaceError = totalJointSpaceError / dataBuffer.Count;
        float avgFBIntensity = fbSampleCount > 0 ? totalFBIntensity / fbSampleCount : 0f;
        float movementSmoothness = Mathf.Sqrt(sumSquaredElbowJerk / dataBuffer.Count);

        float optimalPathLength = Vector3.Distance(trialStartPosition, currentTargetPosition);
        float pathEfficiency = optimalPathLength > 0 ? optimalPathLength / Mathf.Max(actualPathLength, 0.001f) : 0f;

        JointAnglePattern pattern = targetPatterns[patternIndex];

        TrialSummary summary = new TrialSummary
        {
            trialNumber = trialCount,
            patternName = pattern.patternName,
            targetJoint1 = pattern.joint1Angle,
            targetJoint2 = pattern.joint2Angle,
            movementTime = movementTime,
            pathLength = actualPathLength,
            pathEfficiency = pathEfficiency,
            averagePositionError = avgPositionError,
            averageJointSpaceError = avgJointSpaceError,
            peakJerk = peakJerk,
            taskSuccess = taskSuccess,
            finalDistance = finalDistance,
            averageFBIntensity = avgFBIntensity,
            finalFBVibrator = finalFBVibrator,
            movementSmoothness = movementSmoothness,
            numberOfSubmovements = 0,
            fbActivationRate = 0f,
            movementDuringFB = 0f
        };

        trialSummaries.Add(summary);
        SaveTrialDetailedMotionCSV(trialCount, patternIndex);
    }

    void SaveTrialDetailedMotionCSV(int trialNumber, int patternIndex)
    {
        if (!enableLogging || dataBuffer.Count == 0) return;

        string folderPath = GetDataFolderPath();
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filename = $"Trial{trialNumber:D2}_Pattern{(patternIndex + 1):D2}_{timestamp}.csv";
        string filepath = Path.Combine(folderPath, filename);

        StringBuilder csv = new StringBuilder();
        csv.AppendLine("Timestamp,TargetElbowAngle,TargetPosX,TargetPosZ,ActualPosX,ActualPosZ,ActualElbowAngle,PositionError,JointSpaceError,ElbowVel,ElbowJerk,PathLength,OptimalPathLength,PathEfficiency");

        foreach (var data in dataBuffer)
        {
            csv.AppendLine($"{data.timestamp},{data.targetElbowAngle}," +
                          $"{data.targetPosition.x},{data.targetPosition.z}," +
                          $"{data.actualPosition.x},{data.actualPosition.z}," +
                          $"{data.actualElbowAngle},{data.positionError},{data.jointSpaceError}," +
                          $"{data.elbowVelocity},{data.elbowJerk}," +
                          $"{data.pathLength},{data.optimalPathLength},{data.pathEfficiency}");
        }

        File.WriteAllText(filepath, csv.ToString());
    }

    void SaveAllData()
    {
        string folderPath = GetDataFolderPath();
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        SaveTrialSummaryCSV(folderPath, timestamp);
    }

    void SaveTrialSummaryCSV(string folderPath, string timestamp)
    {
        if (trialSummaries.Count == 0) return;

        string filename = $"TrialSummary_{timestamp}.csv";
        string filepath = Path.Combine(folderPath, filename);

        StringBuilder csv = new StringBuilder();

        if (shouldLogKRFeedback)
        {
            csv.AppendLine("TrialNumber,PatternName,TargetJoint2,PathLength,PathEfficiency,AveragePositionError,AverageJointSpaceError,PeakJerk,TaskSuccess,FinalDistance,MovementSmoothness,NumberOfSubmovements,FBActivationRate,MovementDuringFB,AverageFBIntensity,FinalFBVibrator");

            foreach (var summary in trialSummaries)
            {
                csv.AppendLine($"{summary.trialNumber},{summary.patternName},{summary.targetJoint2}," +
                              $"{summary.pathLength},{summary.pathEfficiency}," +
                              $"{summary.averagePositionError},{summary.averageJointSpaceError}," +
                              $"{summary.peakJerk},{summary.taskSuccess},{summary.finalDistance}," +
                              $"{summary.movementSmoothness},{summary.numberOfSubmovements}," +
                              $"{summary.fbActivationRate},{summary.movementDuringFB}," +
                              $"{summary.averageFBIntensity},{summary.finalFBVibrator}");
            }
        }
        else
        {
            csv.AppendLine("TrialNumber,PatternName,TargetJoint2,PathLength,PathEfficiency,AveragePositionError,AverageJointSpaceError,PeakJerk,TaskSuccess,FinalDistance,MovementSmoothness,NumberOfSubmovements");

            foreach (var summary in trialSummaries)
            {
                csv.AppendLine($"{summary.trialNumber},{summary.patternName},{summary.targetJoint2}," +
                              $"{summary.pathLength},{summary.pathEfficiency}," +
                              $"{summary.averagePositionError},{summary.averageJointSpaceError}," +
                              $"{summary.peakJerk},{summary.taskSuccess},{summary.finalDistance}," +
                              $"{summary.movementSmoothness},{summary.numberOfSubmovements}");
            }
        }

        File.WriteAllText(filepath, csv.ToString());
    }

    string GetDataFolderPath()
    {
        string basePath;

#if UNITY_EDITOR
        basePath = Path.Combine(Application.dataPath, "..", "Data");
#else
        basePath = Path.Combine(Application.persistentDataPath, "Data");
#endif

        string destinationFolder = saveDestination.ToString();
        string finalPath = string.IsNullOrEmpty(subFolderName)
            ? Path.Combine(basePath, destinationFolder)
            : Path.Combine(basePath, destinationFolder, subFolderName);

        return Path.GetFullPath(finalPath);
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
                Debug.LogError($"[KR Integrated] OnDestroy Error: {e.Message}");
            }
        }
    }

    void OnGUI()
    {
        GUIStyle style = new GUIStyle(GUI.skin.label);
        style.fontSize = 16;
        style.normal.textColor = Color.white;

        GUI.Label(new Rect(10, 10, 400, 30), statusMessage, style);

        if (!taskRunning)
        {
            GUI.Label(new Rect(10, 40, 400, 30), "Press SPACE to start task", style);
        }
        else
        {
            GUI.Label(new Rect(10, 40, 400, 30), "Press ESC to stop task", style);
        }
    }
}
