using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;
using System.Threading;
using Cysharp.Threading.Tasks;

public class MarkerTask : MonoBehaviour
{
    [Header("Controller Reference")]
    public IndependentEMGJointController jointController; // Independent EMG Joint Controller

    [Header("Arm References")]
    public Transform shoulderJoint; // 肩関節
    public Transform elbowJoint; // 肘関節
    public Transform ball; // 腕の先端のボール

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
        public float joint1Angle; // Joint1の角度（度）
        public float joint2Angle; // Joint2の角度（度）
    }
    
    [Header("UI References")]
    public TextMeshProUGUI countdownText; // World Space Canvasのカウントダウンテキスト（VR対応）

    [Header("Task Settings")]
    public int totalTrials = 10;
    public float initialFreezeDuration = 1.0f; // 初期位置固定時間
    public float movementDuration = 3.0f;
    public float freezeDuration = 1.0f; // ターゲット位置固定時間
    public float markerSize = 0.05f;
    
    [Header("Data Logging")]
    public bool enableLogging = true;
    public SaveDestination saveDestination = SaveDestination.FB無し;
    public string subFolderName = "";

    public enum SaveDestination
    {
        FB無し,
        KP,
        KR
    }
    
    // 現在のターゲット情報 - 2DOF
    private Vector3 currentTargetPosition;
    private float targetShoulderPitch;
    private float targetElbowAngle;
    private int currentPatternIndex; // 現在使用中のパターンインデックス

    // マーカーオブジェクト
    private GameObject currentMarker;
    public GameObject CurrentMarker => currentMarker; // KR_VibrationFeedbackから参照できるように

    // タスク進行状態
    private int trialCount = 0;
    private bool taskRunning = false;
    private bool isInMovementPhase = false; // 緑色の期間（動作中）かどうか
    private List<int> trialOrder;

    // KR Feedback用フラグ（KR_VibrationFeedbackがなくてもエラーにならない）
    private bool shouldProvideKRFeedback = false;
    public bool ShouldProvideKRFeedback => shouldProvideKRFeedback;

    // 試行ごとのサマリーデータ
    private List<TrialSummary> trialSummaries = new List<TrialSummary>();

    // UniTask用のCancellationTokenSource
    private CancellationTokenSource taskCancellationTokenSource;

    // GUI表示用
    private string statusMessage = "Ready";
    
    // データロギング
    private List<MotionData> dataBuffer = new List<MotionData>();
    private Vector3 lastPosition;
    private float[] lastJointAngles;
    private float actualPathLength = 0f;
    private Vector3 trialStartPosition;
    private float trialStartTime;
    private float movementPhaseStartTime; // 緑色期間の開始時刻
    private float lastLogTime = 0f;

    // 微分計算の最小時間間隔（数値的破綻を防ぐ）
    private const float MIN_DELTA_TIME = 0.0001f;

    // データサンプリング周波数: 1000Hz
    private const float LOG_INTERVAL = 0.001f; // 1ms = 1000Hz
    
    [System.Serializable]
    public struct TrialSummary
    {
        public int trialNumber;
        public string patternName;
        public float targetJoint1;
        public float targetJoint2;
        public float movementTime; // 試行開始から終了までの時間
        public float pathLength;
        public float pathEfficiency;
        public float averagePositionError;
        public float averageJointSpaceError;
        public float peakJerk;
        public bool taskSuccess; // 3D距離0.05以内ならtrue
        public float finalDistance; // 最終的な3D距離
        public float averageFBIntensity; // KR条件時の平均FB強度
        public int finalFBVibrator; // KR条件時の最終振動子番号
    }

    [System.Serializable]
    public struct MotionData
    {
        public float timestamp;
        public int trialNumber;

        // ターゲット情報(FK計算済み) - 2DOF
        public float targetShoulderPitch;
        public float targetElbowAngle;
        public Vector3 targetPosition;

        // 実際の状態 - 2DOF
        public Vector3 actualPosition;
        public float actualShoulderPitch;
        public float actualElbowAngle;

        // 誤差
        public float positionError; // デカルト空間での誤差
        public float jointSpaceError; // 関節空間での誤差

        // 速度とジャーク - 2DOF
        public float shoulderPitchVelocity;
        public float elbowVelocity;
        public float shoulderPitchJerk;
        public float elbowJerk;

        // 軌道情報
        public float pathLength; // 累積移動距離
        public float optimalPathLength; // 最短距離
        public float pathEfficiency; // 効率 (0-1)

        // KR Feedback情報（Summary集計用、Detailedには含めない）
        public int fbVibrator; // 振動子番号 (0=なし, 1-9)
        public int fbIntensity; // 振動強度 (0,2,4,6,8,10)
    }
    
    // KR Feedback参照（存在する場合のみ）
    private UDPSender krFeedback;
    private bool shouldLogKRFeedback = false;

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

        // KR Feedbackの有無をチェック
        krFeedback = FindObjectOfType<UDPSender>();

        // KRがアタッチされている、またはKRフォルダに保存する場合にFB強度を記録
        shouldLogKRFeedback = (krFeedback != null) || (saveDestination == SaveDestination.KR);

        if (shouldLogKRFeedback)
        {
            Debug.Log("MarkerTask: KR Feedback logging enabled");
        }

        // バックグラウンドでも実行を継続（勝手にPauseにならない）
        Application.runInBackground = true;
    }

    void Update()
    {
        if (!taskRunning) return;

        // 緑色の期間（動作中）のみデータをログ
        if (enableLogging && ball != null && isInMovementPhase)
        {
            // 1000Hz (1ms間隔) でデータをサンプリング
            float currentTime = Time.time;
            if (currentTime - lastLogTime >= LOG_INTERVAL)
            {
                LogData();
                lastLogTime = currentTime;
            }
        }
    }

    [ContextMenu("Start Task")]
    public void StartTask()
    {
        if (taskRunning) return;

        // パターンをランダムに提示
        trialOrder = new List<int>();
        for (int i = 0; i < totalTrials; i++)
        {
            trialOrder.Add(i);
        }

        // Fisher-Yatesシャッフル
        System.Random rng = new System.Random();
        int n = trialOrder.Count;
        while (n > 1)
        {
            n--;
            int k = rng.Next(n + 1);
            int temp = trialOrder[k];
            trialOrder[k] = trialOrder[n];
            trialOrder[n] = temp;
        }

        Debug.Log($"Trial order (randomized): {string.Join(", ", trialOrder)}");

        trialCount = 0;
        trialSummaries.Clear();

        taskCancellationTokenSource?.Cancel();
        taskCancellationTokenSource?.Dispose();
        taskCancellationTokenSource = new CancellationTokenSource();

        RunTaskFlowAsync(taskCancellationTokenSource.Token).Forget();
    }

    async UniTask RunTaskFlowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ShowCountdownAsync(cancellationToken);

            for (int i = 0; i < totalTrials; i++)
            {
                await RunSingleTrialAsync(i, cancellationToken);
            }

            taskRunning = false;
            statusMessage = "Task Complete!";

            if (enableLogging)
            {
                SaveAllData();
            }
        }
        catch (OperationCanceledException)
        {
            statusMessage = "Task Cancelled";
        }
    }

    async UniTask ShowCountdownAsync(CancellationToken cancellationToken)
    {
        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(true);
        }

        for (int i = 3; i > 0; i--)
        {
            if (countdownText != null)
            {
                countdownText.text = i.ToString();
            }
            await UniTask.Delay(TimeSpan.FromSeconds(1f), cancellationToken: cancellationToken);
        }

        if (countdownText != null)
        {
            countdownText.text = "GO!";
        }
        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: cancellationToken);

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }
    }

    async UniTask RunSingleTrialAsync(int trialIndex, CancellationToken cancellationToken)
    {
        trialCount = trialIndex + 1;
        statusMessage = $"Trial {trialCount}/{totalTrials}";

        // アームをリセット
        ResetArmPosition();
        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: cancellationToken);

        // ターゲットマーカーを生成（黄色）
        GenerateTargetForTrial(trialOrder[trialIndex]);

        Debug.Log($"[Trial{trialCount}] After GenerateTargetForTrial - Marker status: {(currentMarker != null ? "VALID" : "NULL")}");

        if (currentMarker == null)
        {
            Debug.LogError($"[Trial{trialCount}] Marker creation failed! Skipping trial.");
            statusMessage = $"ERROR: Trial {trialCount} marker creation failed";
            await UniTask.Delay(TimeSpan.FromSeconds(1f), cancellationToken: cancellationToken);
            return;
        }

        // タスク開始フラグを立てる（KR FBが動作するように）
        taskRunning = true;

        // 初期位置で固定（黄色マーカー）
        if (jointController != null)
        {
            jointController.FreezeArm();
        }
        statusMessage = $"Trial {trialCount}/{totalTrials} - Initial position (Ready)";
        await UniTask.Delay(TimeSpan.FromSeconds(initialFreezeDuration), cancellationToken: cancellationToken);

        // 固定解除 - 動作開始
        if (jointController != null)
        {
            jointController.UnfreezeArm();
        }

        // マーカーを緑に変更（動作時）& データロギング開始
        ChangeMarkerColor(Color.green);
        isInMovementPhase = true; // 緑色期間開始
        movementPhaseStartTime = Time.time; // 緑色期間の開始時刻
        lastLogTime = Time.time; // サンプリングタイマーリセット
        lastPosition = ball != null ? ball.position : Vector3.zero; // 初期位置リセット
        actualPathLength = 0f; // パス長リセット
        float movementStartTime = Time.time; // movementTime計算用

        statusMessage = $"Trial {trialCount}/{totalTrials} - Move to target";
        await UniTask.Delay(TimeSpan.FromSeconds(movementDuration), cancellationToken: cancellationToken);

        // マーカーを赤に変更（ターゲット位置固定時）& データロギング停止
        ChangeMarkerColor(Color.red);
        isInMovementPhase = false; // 緑色期間終了
        float movementEndTime = Time.time; // 緑色期間終了時刻

        if (jointController != null)
        {
            jointController.FreezeArm();
        }

        // KR Feedbackフラグを立てる（振動開始）
        shouldProvideKRFeedback = true;
        Debug.Log($"[Trial{trialCount}] KR Feedback Flag = TRUE (Freeze started)");

        statusMessage = $"Trial {trialCount}/{totalTrials} - Hold position";
        await UniTask.Delay(TimeSpan.FromSeconds(freezeDuration), cancellationToken: cancellationToken);

        // KR Feedbackフラグを下げる（振動停止）
        shouldProvideKRFeedback = false;
        Debug.Log($"[Trial{trialCount}] KR Feedback Flag = FALSE (Freeze ended)");

        if (jointController != null)
        {
            jointController.UnfreezeArm();
        }

        // movementTime: 緑色の期間のみ（動作時間）
        float movementTime = movementEndTime - movementStartTime;

        // 最終的な3D距離を計算してタスク成功判定
        Vector3 finalBallPos = ball != null ? ball.position : Vector3.zero;
        float finalDistance = Vector3.Distance(finalBallPos, currentTargetPosition);
        bool taskSuccess = finalDistance <= 0.05f;

        SaveTrialSummary(currentPatternIndex, movementTime, finalDistance, taskSuccess);

        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }

        taskRunning = false;

        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: cancellationToken);
    }

    void ChangeMarkerColor(Color color)
    {
        Debug.Log($"[Trial{trialCount}] ChangeMarkerColor called - Marker is {(currentMarker != null ? "VALID" : "NULL")}");

        if (currentMarker != null)
        {
            Renderer markerRenderer = currentMarker.GetComponent<Renderer>();
            if (markerRenderer != null)
            {
                markerRenderer.material.color = color;
                markerRenderer.material.SetColor("_EmissionColor", color * 0.5f);
                Debug.Log($"[Trial{trialCount}] Marker color changed to {color}");
            }
            else
            {
                Debug.LogWarning($"[Trial{trialCount}] Marker renderer is NULL when changing color");
            }
        }
        else
        {
            Debug.LogError($"[Trial{trialCount}] Cannot change color - currentMarker is NULL!");
        }
    }

    void ResetArmPosition()
    {
        if (jointController != null)
        {
            jointController.ResetJointPositions();
        }
    }

    Vector3 CalculateForwardKinematicsFromController(float joint1Angle, float joint2Angle)
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
        Quaternion joint1Rotation = Quaternion.AngleAxis(joint1Angle, joint1RotationAxis);
        Quaternion joint1WorldRotation = baseRotation * joint1Rotation;

        // === Joint2の位置と回転 ===
        Vector3 joint1ToJoint2Direction = joint1WorldRotation * Vector3.up;
        Vector3 joint2Position = joint1Position + joint1ToJoint2Direction * joint1ToJoint2Length;

        Vector3 joint2RotationAxis = jointController.Joint2RotationAxis;
        Quaternion joint2Rotation = Quaternion.AngleAxis(joint2Angle, joint2RotationAxis);
        Quaternion joint2WorldRotation = joint1WorldRotation * joint2Rotation;

        // === エンドエフェクタの位置 ===
        Vector3 joint2ToEndDirection = joint2WorldRotation * Vector3.up;
        Vector3 endEffectorPosition = joint2Position + joint2ToEndDirection * link2Length;

        return endEffectorPosition;
    }
    
    void GenerateTargetForTrial(int patternIndex)
    {
        if (jointController == null || targetPatterns == null || patternIndex >= targetPatterns.Length)
        {
            statusMessage = "ERROR: Controller or patterns missing";
            return;
        }

        // 現在のパターンインデックスを保存
        currentPatternIndex = patternIndex;

        JointAnglePattern selectedPattern = targetPatterns[patternIndex];

        // IndependentEMGJointControllerの稼働域に合わせてクランプ
        // joint1: -30〜80, joint2: 0〜160
        targetShoulderPitch = Mathf.Clamp(selectedPattern.joint1Angle, -30f, 80f);
        targetElbowAngle = Mathf.Clamp(selectedPattern.joint2Angle, 0f, 160f);

        currentTargetPosition = CalculateForwardKinematicsFromController(
            targetShoulderPitch,
            targetElbowAngle
        );

        // FK計算が失敗した場合
        if (currentTargetPosition == Vector3.zero)
        {
            statusMessage = $"ERROR: FK=zero P{patternIndex}";
            return;
        }

        // FK計算結果の妥当性チェック
        float magnitude = currentTargetPosition.magnitude;
        if (magnitude > 10f)
        {
            statusMessage = $"WARNING: Too far ({magnitude:F2}m) P{patternIndex}";
        }
        else if (magnitude < 0.01f)
        {
            statusMessage = $"ERROR: Too close ({magnitude:F4}m) P{patternIndex}";
            return;
        }

        if (currentMarker != null)
        {
            Destroy(currentMarker);
            currentMarker = null;
        }

        Debug.Log($"[P{patternIndex}] Creating marker at {currentTargetPosition} (J1={targetShoulderPitch:F1}°, J2={targetElbowAngle:F1}°)");

        try
        {
            currentMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Debug.Log($"[P{patternIndex}] CreatePrimitive result: {(currentMarker != null ? "SUCCESS" : "NULL!")}");

            if (currentMarker == null)
            {
                statusMessage = $"ERROR: CreatePrimitive returned NULL for P{patternIndex}";
                Debug.LogError($"CreatePrimitive returned NULL for pattern {patternIndex}");
                return;
            }

            currentMarker.name = $"TargetMarker_P{patternIndex}";
            currentMarker.transform.position = currentTargetPosition;
            currentMarker.transform.localScale = Vector3.one * markerSize;
            Debug.Log($"[P{patternIndex}] Marker configured: name={currentMarker.name}, pos={currentMarker.transform.position}, scale={currentMarker.transform.localScale}");
        }
        catch (System.Exception e)
        {
            statusMessage = $"ERROR: Marker creation failed - {e.Message}";
            Debug.LogError($"[P{patternIndex}] Exception during marker creation: {e.Message}\n{e.StackTrace}");
            return;
        }

        Renderer markerRenderer = currentMarker.GetComponent<Renderer>();
        if (markerRenderer != null)
        {
            markerRenderer.material = new Material(Shader.Find("Standard"));
            markerRenderer.material.color = Color.yellow;
            markerRenderer.material.EnableKeyword("_EMISSION");
            markerRenderer.material.SetColor("_EmissionColor", Color.yellow * 0.5f);
            Debug.Log($"[P{patternIndex}] Marker material set to yellow (initial)");
        }
        else
        {
            Debug.LogWarning($"[P{patternIndex}] Marker renderer is NULL");
        }

        Collider markerCollider = currentMarker.GetComponent<Collider>();
        if (markerCollider != null)
        {
            markerCollider.enabled = false;
        }

        Debug.Log($"[P{patternIndex}] Marker creation complete. Final check: {(currentMarker != null ? "OK" : "NULL!")}");

        trialStartTime = Time.time;
        lastLogTime = Time.time; // 1000Hzサンプリング用タイマーをリセット
        trialStartPosition = ball != null ? ball.position : Vector3.zero;
        actualPathLength = 0f;
        dataBuffer.Clear();
    }

    void SaveTrialSummary(int patternIndex, float movementTime, float finalDistance, bool taskSuccess)
    {
        if (dataBuffer.Count == 0) return;

        // 平均誤差とピークジャークを計算
        float totalPositionError = 0f;
        float totalJointSpaceError = 0f;
        float peakJerk = 0f;
        float totalFBIntensity = 0f;
        int fbSampleCount = 0;
        int finalFBVibrator = 0;

        foreach (var data in dataBuffer)
        {
            totalPositionError += data.positionError;
            totalJointSpaceError += data.jointSpaceError;

            float maxJerkThisFrame = Mathf.Max(
                Mathf.Abs(data.shoulderPitchJerk),
                Mathf.Abs(data.elbowJerk)
            );
            peakJerk = Mathf.Max(peakJerk, maxJerkThisFrame);

            // KR条件時のFB強度を集計（振動があるデータのみ）
            if (shouldLogKRFeedback && data.fbVibrator > 0)
            {
                totalFBIntensity += data.fbIntensity;
                fbSampleCount++;
                // 最後のFB振動子を更新（FB期間中の最後）
                finalFBVibrator = data.fbVibrator;
            }
        }

        float avgPositionError = totalPositionError / dataBuffer.Count;
        float avgJointSpaceError = totalJointSpaceError / dataBuffer.Count;
        float avgFBIntensity = fbSampleCount > 0 ? totalFBIntensity / fbSampleCount : 0f;

        // 最適経路長(直線距離)
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
            finalFBVibrator = finalFBVibrator
        };

        trialSummaries.Add(summary);
        SaveTrialDetailedMotionCSV(trialCount, patternIndex);
    }

    void SaveTrialDetailedMotionCSV(int trialNumber, int patternIndex)
    {
        if (dataBuffer.Count == 0) return;

        string folderPath = GetDataFolderPath();

        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string filename = $"Trial{trialNumber:D2}_Pattern{(patternIndex + 1):D2}_{timestamp}.csv";
        string filepath = Path.Combine(folderPath, filename);

        StringBuilder csv = new StringBuilder();

        // DetailedにはFB情報を含めない（Summaryのみ）
        csv.AppendLine("Timestamp," +
                      "TargetShoulderPitch,TargetElbowAngle," +
                      "TargetPosX,TargetPosY,TargetPosZ," +
                      "ActualPosX,ActualPosY,ActualPosZ," +
                      "ActualShoulderPitch,ActualElbowAngle," +
                      "PositionError,JointSpaceError," +
                      "ShoulderPitchVel,ElbowVel," +
                      "ShoulderPitchJerk,ElbowJerk," +
                      "PathLength,OptimalPathLength,PathEfficiency");

        foreach (var data in dataBuffer)
        {
            csv.AppendLine($"{data.timestamp}," +
                          $"{data.targetShoulderPitch},{data.targetElbowAngle}," +
                          $"{data.targetPosition.x},{data.targetPosition.y},{data.targetPosition.z}," +
                          $"{data.actualPosition.x},{data.actualPosition.y},{data.actualPosition.z}," +
                          $"{data.actualShoulderPitch},{data.actualElbowAngle}," +
                          $"{data.positionError},{data.jointSpaceError}," +
                          $"{data.shoulderPitchVelocity},{data.elbowVelocity}," +
                          $"{data.shoulderPitchJerk},{data.elbowJerk}," +
                          $"{data.pathLength},{data.optimalPathLength},{data.pathEfficiency}");
        }

        File.WriteAllText(filepath, csv.ToString());
    }
    
    
    /// <summary>
    /// データをログに記録 (2DOF版)
    /// </summary>
    void LogData()
    {
        Vector3 currentPosition = ball.position;

        // 移動距離を累積
        if (lastPosition != Vector3.zero)
        {
            actualPathLength += Vector3.Distance(currentPosition, lastPosition);
        }

        // 現在の関節角度を取得
        float[] currentJointAngles = GetCurrentJointAngles();
        float[] currentJointVelocities = CalculateJointVelocities(currentJointAngles);

        // ジャークはIndependentEMGJointControllerから取得
        float shoulderJerk = jointController != null ? jointController.Joint1Jerk : 0f;
        float elbowJerk = jointController != null ? jointController.Joint2Jerk : 0f;

        // 誤差計算
        float positionError = Vector3.Distance(currentPosition, currentTargetPosition);
        float jointSpaceError = CalculateJointSpaceError(currentJointAngles);

        // 最適経路長(直線距離)
        float optimalPathLength = Vector3.Distance(trialStartPosition, currentTargetPosition);
        float pathEfficiency = optimalPathLength > 0 ? optimalPathLength / Mathf.Max(actualPathLength, 0.001f) : 0f;

        // 緑色期間の開始からの経過時間（0～約3秒）
        float trialElapsedTime = Time.time - movementPhaseStartTime;

        // KR Feedback情報を取得（KR FBフラグが立っている期間のみ）
        int fbVibrator = 0;
        int fbIntensity = 0;
        if (shouldLogKRFeedback && shouldProvideKRFeedback && krFeedback != null)
        {
            fbVibrator = krFeedback.CurrentVibrator;
            fbIntensity = krFeedback.CurrentIntensity;
        }

        // データ構造体を作成
        MotionData data = new MotionData
        {
            timestamp = trialElapsedTime, // 試行開始からの相対時間
            trialNumber = trialCount,

            targetShoulderPitch = targetShoulderPitch,
            targetElbowAngle = targetElbowAngle,
            targetPosition = currentTargetPosition,

            actualPosition = currentPosition,
            actualShoulderPitch = currentJointAngles[0],
            actualElbowAngle = currentJointAngles[1],

            positionError = positionError,
            jointSpaceError = jointSpaceError,

            shoulderPitchVelocity = currentJointVelocities[0],
            elbowVelocity = currentJointVelocities[1],
            shoulderPitchJerk = shoulderJerk,
            elbowJerk = elbowJerk,

            pathLength = actualPathLength,
            optimalPathLength = optimalPathLength,
            pathEfficiency = pathEfficiency,

            fbVibrator = fbVibrator,
            fbIntensity = fbIntensity
        };

        dataBuffer.Add(data);

        lastPosition = currentPosition;
        lastJointAngles = currentJointAngles;
    }
    
    /// <summary>
    /// 現在の関節角度を取得 (2DOF版)
    /// </summary>
    float[] GetCurrentJointAngles()
    {
        float[] angles = new float[2]; // 2DOF: joint1, joint2

        if (jointController != null)
        {
            Vector2 jointAngles = jointController.GetJointAngles();
            angles[0] = jointAngles.x; // joint1
            angles[1] = jointAngles.y; // joint2
        }

        return angles;
    }
    
    /// <summary>
    /// 関節速度を計算 (2DOF版)
    /// </summary>
    float[] CalculateJointVelocities(float[] currentAngles)
    {
        float[] velocities = new float[2]; // 2DOF
        float dt = Mathf.Max(Time.deltaTime, MIN_DELTA_TIME);

        if (lastJointAngles != null && lastJointAngles.Length >= 2)
        {
            for (int i = 0; i < 2; i++)
            {
                float velocity = (currentAngles[i] - lastJointAngles[i]) / dt;

                // 異常値チェック
                if (float.IsNaN(velocity) || float.IsInfinity(velocity))
                {
                    velocity = 0f;
                }

                velocities[i] = velocity;
            }
        }

        return velocities;
    }
    
    /// <summary>
    /// 関節空間での誤差を計算 (2DOF版)
    /// </summary>
    float CalculateJointSpaceError(float[] currentAngles)
    {
        float error = 0f;
        error += Mathf.Abs(Mathf.DeltaAngle(currentAngles[0], targetShoulderPitch));
        error += Mathf.Abs(Mathf.DeltaAngle(currentAngles[1], targetElbowAngle));
        return error / 2f; // 平均誤差 (2DOF)
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

        // KR条件の場合はFB情報列を追加
        if (shouldLogKRFeedback)
        {
            csv.AppendLine("TrialNumber,PatternName,TargetJoint1,TargetJoint2," +
                          "MovementTime,PathLength,PathEfficiency," +
                          "AveragePositionError,AverageJointSpaceError,PeakJerk,TaskSuccess,FinalDistance," +
                          "AverageFBIntensity,FinalFBVibrator");

            foreach (var summary in trialSummaries)
            {
                csv.AppendLine($"{summary.trialNumber},{summary.patternName}," +
                              $"{summary.targetJoint1},{summary.targetJoint2}," +
                              $"{summary.movementTime},{summary.pathLength},{summary.pathEfficiency}," +
                              $"{summary.averagePositionError},{summary.averageJointSpaceError}," +
                              $"{summary.peakJerk},{summary.taskSuccess},{summary.finalDistance}," +
                              $"{summary.averageFBIntensity},{summary.finalFBVibrator}");
            }
        }
        else
        {
            csv.AppendLine("TrialNumber,PatternName,TargetJoint1,TargetJoint2," +
                          "MovementTime,PathLength,PathEfficiency," +
                          "AveragePositionError,AverageJointSpaceError,PeakJerk,TaskSuccess,FinalDistance");

            foreach (var summary in trialSummaries)
            {
                csv.AppendLine($"{summary.trialNumber},{summary.patternName}," +
                              $"{summary.targetJoint1},{summary.targetJoint2}," +
                              $"{summary.movementTime},{summary.pathLength},{summary.pathEfficiency}," +
                              $"{summary.averagePositionError},{summary.averageJointSpaceError}," +
                              $"{summary.peakJerk},{summary.taskSuccess},{summary.finalDistance}");
            }
        }

        File.WriteAllText(filepath, csv.ToString());
    }

    
    /// <summary>
    /// データフォルダのパスを取得
    /// </summary>
    string GetDataFolderPath()
    {
        string basePath;

        switch (saveDestination)
        {
            case SaveDestination.FB無し:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\nonFB");
                break;
            case SaveDestination.KP:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\KP");
                break;
            case SaveDestination.KR:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\KR");
                break;
            default:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\nonFB");
                break;
        }

        // サブフォルダ名が指定されている場合は結合
        if (!string.IsNullOrEmpty(subFolderName))
        {
            basePath = Path.Combine(basePath, subFolderName);
        }

        return basePath;
    }
    
    public int GetTrialCount() => trialCount;
    public bool IsTaskRunning() => taskRunning;

    [ContextMenu("Stop Task")]
    public void StopTask()
    {
        if (taskRunning)
        {
            taskCancellationTokenSource?.Cancel();
            taskRunning = false;

            if (currentMarker != null)
            {
                Destroy(currentMarker);
                currentMarker = null;
            }

            if (jointController != null && jointController.IsFrozen())
            {
                jointController.UnfreezeArm();
            }

            statusMessage = "Task Stopped";
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 400, 200));

        GUILayout.Label($"Status: {statusMessage}", GUI.skin.box);

        // デバッグ情報
        if (taskRunning)
        {
            GUILayout.Label($"Trial: {trialCount}/{totalTrials} (Pattern{trialCount})", GUI.skin.box);
            GUILayout.Label($"Angles: J1={targetShoulderPitch:F1}° J2={targetElbowAngle:F1}°", GUI.skin.box);
            GUILayout.Label($"FK Result: {currentTargetPosition.ToString("F3")}", GUI.skin.box);

            float dist = currentTargetPosition.magnitude;
            GUILayout.Label($"Distance: {dist:F3}m", GUI.skin.box);

            if (currentMarker != null)
            {
                GUILayout.Label($"Marker: OK (active={currentMarker.activeSelf})", GUI.skin.box);
            }
            else
            {
                GUILayout.Label("Marker: NULL ", GUI.skin.box);
            }
        }

        // Controller状態
        if (jointController != null)
        {
            bool hasBase = jointController.BaseObject != null;
            GUILayout.Label($"Controller OK, BaseObject: {hasBase}", GUI.skin.box);
        }
        else
        {
            GUILayout.Label("Controller: NULL", GUI.skin.box);
        }

        GUILayout.Space(10);

        if (!taskRunning)
        {
            if (GUILayout.Button("Start Task", GUILayout.Height(40)))
            {
                StartTask();
            }
        }
        else
        {
            if (GUILayout.Button("Stop Task", GUILayout.Height(40)))
            {
                StopTask();
            }
        }

        GUILayout.EndArea();
    }

    void OnDestroy()
    {
        taskCancellationTokenSource?.Cancel();
        taskCancellationTokenSource?.Dispose();
    }
}
