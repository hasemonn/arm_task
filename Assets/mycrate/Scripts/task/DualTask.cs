using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System;
using System.Threading;
using Cysharp.Threading.Tasks;

/// <summary>
/// デュアルタスク: 腕でターゲットAに到達 & 手でターゲットBに到達 → 両方成功後、腕と手を接触させる
/// </summary>
public class DualTask : MonoBehaviour
{
    [Header("Controller Reference")]
    public IndependentEMGJointController jointController;

    [Header("Arm References")]
    public Transform ball; // エンドエフェクタ（腕の先端）
    public Renderer ballRenderer; // エンドエフェクタのRenderer

    [Header("Hand Tracking Reference")]
    public Transform leftHandTransform; // 左手のトラッキング位置（例: OVRHandのIndexTip）
    public Transform rightHandTransform; // 右手のトラッキング位置（例: OVRHandのIndexTip）
    public Renderer leftHandRenderer; // 左手のRenderer（テクスチャ変更用）
    public Renderer rightHandRenderer; // 右手のRenderer（テクスチャ変更用）

    [Header("Target A (Arm) Patterns - FK計算")]
    public JointAnglePattern[] targetAPatterns = new JointAnglePattern[]
    {
        new JointAnglePattern { joint1Angle = 42f, joint2Angle = 137f, patternName = "ArmPattern1" },
        new JointAnglePattern { joint1Angle = -18f, joint2Angle = 83f, patternName = "ArmPattern2" },
        new JointAnglePattern { joint1Angle = 67f, joint2Angle = 12f, patternName = "ArmPattern3" },
        new JointAnglePattern { joint1Angle = 5f, joint2Angle = 88f, patternName = "ArmPattern4" },
        new JointAnglePattern { joint1Angle = -30f, joint2Angle = 154f, patternName = "ArmPattern5" }
    };

    [System.Serializable]
    public struct JointAnglePattern
    {
        public string patternName;
        public float joint1Angle;
        public float joint2Angle;
    }

    [Header("Target B (Hand) Positions")]
    [Tooltip("左手用ターゲットの位置（Trial 1-5）")]
    public Vector3[] targetBLeftPositions = new Vector3[]
    {
        new Vector3(0.3f, 1.2f, 0.3f),
        new Vector3(-0.3f, 1.2f, 0.2f),
        new Vector3(0.0f, 1.5f, 0.1f),
        new Vector3(0.3f, 0.9f, 0.2f),
        new Vector3(-0.3f, 0.9f, 0.3f)
    };

    [Tooltip("右手用ターゲットの位置（Trial 1-5）")]
    public Vector3[] targetBRightPositions = new Vector3[]
    {
        new Vector3(-0.3f, 1.2f, 0.3f),
        new Vector3(0.3f, 1.2f, 0.2f),
        new Vector3(0.0f, 1.5f, 0.1f),
        new Vector3(-0.3f, 0.9f, 0.2f),
        new Vector3(0.3f, 0.9f, 0.3f)
    };

    [Header("Hand Pattern (Trial 1-5)")]
    [Tooltip("試行ごとの手のパターン: Left, Right, Both")]
    public HandPattern[] trialHandPatterns = new HandPattern[]
    {
        HandPattern.Left,   // Trial 1: 左手
        HandPattern.Right,  // Trial 2: 右手
        HandPattern.Left,   // Trial 3: 左手
        HandPattern.Right,  // Trial 4: 右手
        HandPattern.Both    // Trial 5: 両手
    };

    public enum HandPattern
    {
        Left,
        Right,
        Both
    }

    [Header("UI References")]
    public TextMeshProUGUI countdownText;

    [Header("Task Settings")]
    public int totalTrials = 5;
    public float targetAScale = 2.5f; // ball（エンドエフェクタ）の2.5倍
    public Vector3 targetBSizeLeft = new Vector3(0.1f, 0.1f, 0.1f); // 左手用Cubeサイズ
    public Vector3 targetBSizeRight = new Vector3(0.1f, 0.1f, 0.1f); // 右手用Cubeサイズ
    public float dwellTime = 1.0f; // 滞在判定時間（秒）
    public float contactThreshold = 0.05f; // 接触判定の距離閾値（m）

    [Header("Visual Settings - Targets")]
    public Color targetAColor = Color.green; // ターゲットA（腕用）の色
    public Color targetBLeftColor = Color.red; // ターゲットB（左手用）の色
    public Color targetBRightColor = Color.blue; // ターゲットB（右手用）の色

    [Header("Visual Settings - Highlight (達成後)")]
    public Color highlightBallColor = new Color(1f, 0.5f, 0f); // オレンジ
    public Color highlightLeftHandColor = new Color(1f, 0.5f, 0f); // オレンジ
    public Color highlightRightHandColor = new Color(1f, 0.5f, 0f); // オレンジ

    [Header("Data Logging")]
    public bool enableLogging = true;
    public SaveDestination saveDestination = SaveDestination.KP;
    public string subFolderName = "DualTask";

    public enum SaveDestination
    {
        KP,
        KR
    }

    // 現在のターゲット
    private GameObject currentTargetA;
    private GameObject currentTargetBLeft;
    private GameObject currentTargetBRight;
    private Vector3 currentTargetAPosition;
    private Vector3 currentTargetBLeftPosition;
    private Vector3 currentTargetBRightPosition;
    private float targetAShoulderPitch;
    private float targetAElbowAngle;

    // タスク進行状態
    private int trialCount = 0;
    private bool taskRunning = false;
    private List<int> trialOrder;

    // サマリーデータ
    private List<TrialSummary> trialSummaries = new List<TrialSummary>();

    // UniTask用CancellationTokenSource
    private CancellationTokenSource taskCancellationTokenSource;

    // GUI表示用
    private string statusMessage = "Ready";

    // 色の保持
    private Color originalBallColor;
    private Color originalLeftHandColor;
    private Color originalRightHandColor;

    // 現在の試行パターン
    private HandPattern currentHandPattern;

    [System.Serializable]
    public struct TrialSummary
    {
        public int trialNumber;
        public string handPattern; // "Left", "Right", "Both"
        public string armPatternName;
        public float targetAJoint1;
        public float targetAJoint2;
        public Vector3 targetBLeftPosition;
        public Vector3 targetBRightPosition;
        public float timeToReachA; // ターゲットA到達までの時間
        public float timeToReachBLeft; // 左手ターゲットB到達までの時間
        public float timeToReachBRight; // 右手ターゲットB到達までの時間
        public float timeToContactLeft; // 左手とエンドエフェクタ接触までの時間
        public float timeToContactRight; // 右手とエンドエフェクタ接触までの時間
        public float totalTime; // 試行全体の時間
        public bool taskASuccess;
        public bool taskBLeftSuccess;
        public bool taskBRightSuccess;
        public bool contactLeftSuccess;
        public bool contactRightSuccess;
    }

    void Start()
    {
        if (jointController == null)
        {
            jointController = FindObjectOfType<IndependentEMGJointController>();
        }

        if (ballRenderer == null && ball != null)
        {
            ballRenderer = ball.GetComponent<Renderer>();
        }

        // オリジナルの色を保存
        if (ballRenderer != null)
        {
            originalBallColor = ballRenderer.material.color;
        }

        if (leftHandRenderer != null)
        {
            originalLeftHandColor = leftHandRenderer.material.color;
        }

        if (rightHandRenderer != null)
        {
            originalRightHandColor = rightHandRenderer.material.color;
        }

        if (countdownText != null)
        {
            countdownText.gameObject.SetActive(false);
        }
    }

    [ContextMenu("Start Task")]
    public void StartTask()
    {
        if (taskRunning) return;

        // 試行順序を1から5まで順番に
        trialOrder = new List<int>();
        for (int i = 0; i < totalTrials; i++)
        {
            trialOrder.Add(i);
        }

        Debug.Log($"DualTask: Trial order (1 to 5): {string.Join(", ", trialOrder)}");

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
            statusMessage = "All Trials Complete!";

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

        // 現在の試行の手パターンを取得
        int patternIndex = trialOrder[trialIndex];
        currentHandPattern = trialHandPatterns[patternIndex];

        // アームをリセット
        ResetArmPosition();
        ResetColors();
        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: cancellationToken);

        // ターゲットA（腕用）とターゲットB（手用）を生成
        GenerateTargets(patternIndex);

        if (currentTargetA == null)
        {
            Debug.LogError($"[Trial{trialCount}] Target generation failed! Skipping trial.");
            statusMessage = $"ERROR: Trial {trialCount} target creation failed";
            await UniTask.Delay(TimeSpan.FromSeconds(1f), cancellationToken: cancellationToken);
            return;
        }

        taskRunning = true;
        float trialStartTime = Time.time;
        float timeToReachA = 0f;
        float timeToReachBLeft = 0f;
        float timeToReachBRight = 0f;
        float timeToContactLeft = 0f;
        float timeToContactRight = 0f;
        bool taskASuccess = false;
        bool taskBLeftSuccess = false;
        bool taskBRightSuccess = false;
        bool contactLeftSuccess = false;
        bool contactRightSuccess = false;

        statusMessage = $"Trial {trialCount}/{totalTrials} - Reach targets ({currentHandPattern})";

        // 並行タスクリスト
        List<UniTask<(string taskName, bool success)>> tasks = new List<UniTask<(string, bool)>>();

        // ターゲットA（腕）の判定
        tasks.Add(WaitForTargetReachedNamed("TaskA", currentTargetA, ball, dwellTime, cancellationToken));

        // 手のパターンに応じてターゲットBの判定を追加
        if (currentHandPattern == HandPattern.Left || currentHandPattern == HandPattern.Both)
        {
            tasks.Add(WaitForTargetReachedNamed("TaskBLeft", currentTargetBLeft, leftHandTransform, dwellTime, cancellationToken));
        }

        if (currentHandPattern == HandPattern.Right || currentHandPattern == HandPattern.Both)
        {
            tasks.Add(WaitForTargetReachedNamed("TaskBRight", currentTargetBRight, rightHandTransform, dwellTime, cancellationToken));
        }

        // タスクを個別に監視して、達成したらすぐに色を変更
        var taskATask = MonitorAndChangeColorOnComplete("TaskA", tasks[0], cancellationToken);

        UniTask taskBLeftTask = UniTask.CompletedTask;
        UniTask taskBRightTask = UniTask.CompletedTask;

        int taskIndex = 1;
        if (currentHandPattern == HandPattern.Left || currentHandPattern == HandPattern.Both)
        {
            taskBLeftTask = MonitorAndChangeColorOnComplete("TaskBLeft", tasks[taskIndex], cancellationToken);
            taskIndex++;
        }

        if (currentHandPattern == HandPattern.Right || currentHandPattern == HandPattern.Both)
        {
            taskBRightTask = MonitorAndChangeColorOnComplete("TaskBRight", tasks[taskIndex], cancellationToken);
        }

        // すべてのタスクが完了するまで待機
        await UniTask.WhenAll(taskATask, taskBLeftTask, taskBRightTask);

        // 最終的な成功状態を記録
        var results = await UniTask.WhenAll(tasks);
        foreach (var result in results)
        {
            if (result.taskName == "TaskA" && result.success)
            {
                taskASuccess = true;
                timeToReachA = Time.time - trialStartTime;
            }
            else if (result.taskName == "TaskBLeft" && result.success)
            {
                taskBLeftSuccess = true;
                timeToReachBLeft = Time.time - trialStartTime;
            }
            else if (result.taskName == "TaskBRight" && result.success)
            {
                taskBRightSuccess = true;
                timeToReachBRight = Time.time - trialStartTime;
            }
        }

        // すべてのターゲットが達成されたら、接触フェーズへ
        bool allTargetsReached = taskASuccess;
        if (currentHandPattern == HandPattern.Left) allTargetsReached &= taskBLeftSuccess;
        if (currentHandPattern == HandPattern.Right) allTargetsReached &= taskBRightSuccess;
        if (currentHandPattern == HandPattern.Both) allTargetsReached &= (taskBLeftSuccess && taskBRightSuccess);

        if (allTargetsReached)
        {
            statusMessage = $"Trial {trialCount}/{totalTrials} - Touch hand(s) to ball";

            // 接触判定タスクリスト
            List<UniTask<(string contactName, bool success, float time)>> contactTasks = new List<UniTask<(string, bool, float)>>();

            if (currentHandPattern == HandPattern.Left || currentHandPattern == HandPattern.Both)
            {
                contactTasks.Add(WaitForContactNamed("ContactLeft", leftHandTransform, cancellationToken));
            }

            if (currentHandPattern == HandPattern.Right || currentHandPattern == HandPattern.Both)
            {
                contactTasks.Add(WaitForContactNamed("ContactRight", rightHandTransform, cancellationToken));
            }

            // すべての接触が完了するまで待機
            var contactResults = await UniTask.WhenAll(contactTasks);

            foreach (var result in contactResults)
            {
                if (result.contactName == "ContactLeft" && result.success)
                {
                    contactLeftSuccess = true;
                    timeToContactLeft = result.time;
                    Debug.Log($"[Trial{trialCount}] Left hand contact at {timeToContactLeft:F2}s");
                }
                else if (result.contactName == "ContactRight" && result.success)
                {
                    contactRightSuccess = true;
                    timeToContactRight = result.time;
                    Debug.Log($"[Trial{trialCount}] Right hand contact at {timeToContactRight:F2}s");
                }
            }
        }

        float totalTime = Time.time - trialStartTime;

        // サマリーを保存
        SaveTrialSummary(patternIndex, timeToReachA, timeToReachBLeft, timeToReachBRight,
                        timeToContactLeft, timeToContactRight, totalTime,
                        taskASuccess, taskBLeftSuccess, taskBRightSuccess,
                        contactLeftSuccess, contactRightSuccess);

        // クリーンアップ
        if (currentTargetA != null) Destroy(currentTargetA);
        if (currentTargetBLeft != null) Destroy(currentTargetBLeft);
        if (currentTargetBRight != null) Destroy(currentTargetBRight);
        currentTargetA = null;
        currentTargetBLeft = null;
        currentTargetBRight = null;

        taskRunning = false;

        await UniTask.Delay(TimeSpan.FromSeconds(0.5f), cancellationToken: cancellationToken);
    }

    void ResetArmPosition()
    {
        if (jointController != null)
        {
            jointController.ResetJointPositions();
        }
    }

    void ResetColors()
    {
        if (ballRenderer != null)
        {
            ballRenderer.material.color = originalBallColor;
        }

        if (leftHandRenderer != null)
        {
            leftHandRenderer.material.color = originalLeftHandColor;
        }

        if (rightHandRenderer != null)
        {
            rightHandRenderer.material.color = originalRightHandColor;
        }
    }

    void GenerateTargets(int patternIndex)
    {
        // ターゲットA（腕用）を生成
        if (patternIndex >= targetAPatterns.Length)
        {
            Debug.LogError($"Pattern index {patternIndex} out of range for Target A");
            return;
        }

        JointAnglePattern selectedPattern = targetAPatterns[patternIndex];
        targetAShoulderPitch = Mathf.Clamp(selectedPattern.joint1Angle, -30f, 80f);
        targetAElbowAngle = Mathf.Clamp(selectedPattern.joint2Angle, 0f, 160f);

        currentTargetAPosition = CalculateForwardKinematicsFromController(targetAShoulderPitch, targetAElbowAngle);

        if (currentTargetAPosition == Vector3.zero)
        {
            Debug.LogError($"FK calculation failed for Target A");
            return;
        }

        // ターゲットAを球体で生成（ballの2.5倍サイズ）
        currentTargetA = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        currentTargetA.name = $"TargetA_P{patternIndex}";
        currentTargetA.transform.position = currentTargetAPosition;
        float ballScale = ball != null ? ball.localScale.x : 0.05f;
        currentTargetA.transform.localScale = Vector3.one * ballScale * targetAScale;

        Renderer rendererA = currentTargetA.GetComponent<Renderer>();
        if (rendererA != null)
        {
            rendererA.material = new Material(Shader.Find("Standard"));
            rendererA.material.color = targetAColor;
        }

        Collider colliderA = currentTargetA.GetComponent<Collider>();
        if (colliderA != null)
        {
            colliderA.enabled = false;
        }

        Debug.Log($"[Trial{trialCount}] Target A created at {currentTargetAPosition}");

        // ターゲットB（手用）を生成 - パターンに応じて
        if (patternIndex >= targetBLeftPositions.Length || patternIndex >= targetBRightPositions.Length)
        {
            Debug.LogError($"Pattern index {patternIndex} out of range for Target B");
            return;
        }

        currentTargetBLeftPosition = targetBLeftPositions[patternIndex];
        currentTargetBRightPosition = targetBRightPositions[patternIndex];

        // 左手用ターゲット
        if (currentHandPattern == HandPattern.Left || currentHandPattern == HandPattern.Both)
        {
            currentTargetBLeft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            currentTargetBLeft.name = $"TargetBLeft_P{patternIndex}";
            currentTargetBLeft.transform.position = currentTargetBLeftPosition;
            currentTargetBLeft.transform.localScale = targetBSizeLeft;

            Renderer rendererBLeft = currentTargetBLeft.GetComponent<Renderer>();
            if (rendererBLeft != null)
            {
                rendererBLeft.material = new Material(Shader.Find("Standard"));
                rendererBLeft.material.color = targetBLeftColor;
            }

            Collider colliderBLeft = currentTargetBLeft.GetComponent<Collider>();
            if (colliderBLeft != null)
            {
                colliderBLeft.enabled = false;
            }

            Debug.Log($"[Trial{trialCount}] Target B (Left) created at {currentTargetBLeftPosition}");
        }

        // 右手用ターゲット
        if (currentHandPattern == HandPattern.Right || currentHandPattern == HandPattern.Both)
        {
            currentTargetBRight = GameObject.CreatePrimitive(PrimitiveType.Cube);
            currentTargetBRight.name = $"TargetBRight_P{patternIndex}";
            currentTargetBRight.transform.position = currentTargetBRightPosition;
            currentTargetBRight.transform.localScale = targetBSizeRight;

            Renderer rendererBRight = currentTargetBRight.GetComponent<Renderer>();
            if (rendererBRight != null)
            {
                rendererBRight.material = new Material(Shader.Find("Standard"));
                rendererBRight.material.color = targetBRightColor;
            }

            Collider colliderBRight = currentTargetBRight.GetComponent<Collider>();
            if (colliderBRight != null)
            {
                colliderBRight.enabled = false;
            }

            Debug.Log($"[Trial{trialCount}] Target B (Right) created at {currentTargetBRightPosition}");
        }
    }

    Vector3 CalculateForwardKinematicsFromController(float joint1Angle, float joint2Angle)
    {
        if (jointController == null) return Vector3.zero;

        float baseToJoint1Length = jointController.BaseToJoint1Length;
        float joint1ToJoint2Length = jointController.Joint1ToJoint2Length;
        float link2Length = jointController.Link2Length;

        Transform baseObject = jointController.BaseObject;
        if (baseObject == null) return Vector3.zero;

        Vector3 basePosition = baseObject.position;
        Quaternion baseRotation = baseObject.rotation;

        Vector3 upDirection = baseRotation * Vector3.up;

        // Joint1の位置と回転
        Vector3 joint1Position = basePosition + upDirection * baseToJoint1Length;
        Vector3 joint1RotationAxis = jointController.Joint1RotationAxis;
        Quaternion joint1Rotation = Quaternion.AngleAxis(joint1Angle, joint1RotationAxis);
        Quaternion joint1WorldRotation = baseRotation * joint1Rotation;

        // Joint2の位置と回転
        Vector3 joint1ToJoint2Direction = joint1WorldRotation * Vector3.up;
        Vector3 joint2Position = joint1Position + joint1ToJoint2Direction * joint1ToJoint2Length;

        Vector3 joint2RotationAxis = jointController.Joint2RotationAxis;
        Quaternion joint2Rotation = Quaternion.AngleAxis(joint2Angle, joint2RotationAxis);
        Quaternion joint2WorldRotation = joint1WorldRotation * joint2Rotation;

        // エンドエフェクタの位置
        Vector3 joint2ToEndDirection = joint2WorldRotation * Vector3.up;
        Vector3 endEffectorPosition = joint2Position + joint2ToEndDirection * link2Length;

        return endEffectorPosition;
    }

    /// <summary>
    /// タスク完了を監視して、完了したらすぐに色を変更
    /// </summary>
    async UniTask MonitorAndChangeColorOnComplete(string taskName, UniTask<(string, bool)> task, CancellationToken cancellationToken)
    {
        var result = await task;

        if (!result.Item2) return; // 失敗した場合は何もしない

        if (taskName == "TaskA")
        {
            Destroy(currentTargetA);
            currentTargetA = null;
            if (ballRenderer != null)
            {
                ballRenderer.material.color = highlightBallColor;
            }
            Debug.Log($"[Trial{trialCount}] Target A reached - Ball changed to orange");
        }
        else if (taskName == "TaskBLeft")
        {
            Destroy(currentTargetBLeft);
            currentTargetBLeft = null;
            if (leftHandRenderer != null)
            {
                leftHandRenderer.material.color = highlightLeftHandColor;
            }
            Debug.Log($"[Trial{trialCount}] Target B (Left) reached - Left hand changed to orange");
        }
        else if (taskName == "TaskBRight")
        {
            Destroy(currentTargetBRight);
            currentTargetBRight = null;
            if (rightHandRenderer != null)
            {
                rightHandRenderer.material.color = highlightRightHandColor;
            }
            Debug.Log($"[Trial{trialCount}] Target B (Right) reached - Right hand changed to orange");
        }
    }

    /// <summary>
    /// ターゲットに到達するまで待機（1秒間滞在判定）- 名前付き
    /// </summary>
    async UniTask<(string taskName, bool success)> WaitForTargetReachedNamed(string taskName, GameObject target, Transform tracker, float dwellTime, CancellationToken cancellationToken)
    {
        if (target == null || tracker == null)
        {
            return (taskName, false);
        }

        float targetRadius = target.transform.localScale.x / 2f; // 球体またはCubeの半径
        float dwellTimer = 0f;

        while (dwellTimer < dwellTime)
        {
            float distance = Vector3.Distance(tracker.position, target.transform.position);

            if (distance <= targetRadius)
            {
                dwellTimer += Time.deltaTime;
            }
            else
            {
                dwellTimer = 0f;
            }

            await UniTask.Yield(cancellationToken);
        }

        return (taskName, true);
    }

    /// <summary>
    /// 腕（ball）と手の接触を待機 - 名前付き、時間記録
    /// </summary>
    async UniTask<(string contactName, bool success, float time)> WaitForContactNamed(string contactName, Transform handTransform, CancellationToken cancellationToken)
    {
        if (ball == null || handTransform == null)
        {
            return (contactName, false, 0f);
        }

        float contactStartTime = Time.time;

        while (true)
        {
            float distance = Vector3.Distance(ball.position, handTransform.position);

            if (distance <= contactThreshold)
            {
                float contactTime = Time.time - contactStartTime;
                return (contactName, true, contactTime);
            }

            await UniTask.Yield(cancellationToken);
        }
    }

    void SaveTrialSummary(int patternIndex, float timeToReachA, float timeToReachBLeft, float timeToReachBRight,
                         float timeToContactLeft, float timeToContactRight, float totalTime,
                         bool taskASuccess, bool taskBLeftSuccess, bool taskBRightSuccess,
                         bool contactLeftSuccess, bool contactRightSuccess)
    {
        JointAnglePattern pattern = targetAPatterns[patternIndex];

        TrialSummary summary = new TrialSummary
        {
            trialNumber = trialCount,
            handPattern = currentHandPattern.ToString(),
            armPatternName = pattern.patternName,
            targetAJoint1 = pattern.joint1Angle,
            targetAJoint2 = pattern.joint2Angle,
            targetBLeftPosition = currentTargetBLeftPosition,
            targetBRightPosition = currentTargetBRightPosition,
            timeToReachA = timeToReachA,
            timeToReachBLeft = timeToReachBLeft,
            timeToReachBRight = timeToReachBRight,
            timeToContactLeft = timeToContactLeft,
            timeToContactRight = timeToContactRight,
            totalTime = totalTime,
            taskASuccess = taskASuccess,
            taskBLeftSuccess = taskBLeftSuccess,
            taskBRightSuccess = taskBRightSuccess,
            contactLeftSuccess = contactLeftSuccess,
            contactRightSuccess = contactRightSuccess
        };

        trialSummaries.Add(summary);
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

        string filename = $"DualTaskSummary_{timestamp}.csv";
        string filepath = Path.Combine(folderPath, filename);

        StringBuilder csv = new StringBuilder();
        csv.AppendLine("TrialNumber,HandPattern,ArmPatternName,TargetAJoint1,TargetAJoint2," +
                      "TargetBLeftPosX,TargetBLeftPosY,TargetBLeftPosZ," +
                      "TargetBRightPosX,TargetBRightPosY,TargetBRightPosZ," +
                      "TimeToReachA,TimeToReachBLeft,TimeToReachBRight," +
                      "TimeToContactLeft,TimeToContactRight,TotalTime," +
                      "TaskASuccess,TaskBLeftSuccess,TaskBRightSuccess," +
                      "ContactLeftSuccess,ContactRightSuccess");

        foreach (var summary in trialSummaries)
        {
            csv.AppendLine($"{summary.trialNumber},{summary.handPattern},{summary.armPatternName}," +
                          $"{summary.targetAJoint1},{summary.targetAJoint2}," +
                          $"{summary.targetBLeftPosition.x},{summary.targetBLeftPosition.y},{summary.targetBLeftPosition.z}," +
                          $"{summary.targetBRightPosition.x},{summary.targetBRightPosition.y},{summary.targetBRightPosition.z}," +
                          $"{summary.timeToReachA},{summary.timeToReachBLeft},{summary.timeToReachBRight}," +
                          $"{summary.timeToContactLeft},{summary.timeToContactRight},{summary.totalTime}," +
                          $"{summary.taskASuccess},{summary.taskBLeftSuccess},{summary.taskBRightSuccess}," +
                          $"{summary.contactLeftSuccess},{summary.contactRightSuccess}");
        }

        File.WriteAllText(filepath, csv.ToString());
        Debug.Log($"DualTask: Summary saved to {filepath}");
    }

    string GetDataFolderPath()
    {
        string basePath;

        switch (saveDestination)
        {
            case SaveDestination.KP:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\dual\KP");
                break;
            case SaveDestination.KR:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\dual\KR");
                break;
            default:
                basePath = Path.Combine(Application.dataPath, @"..\datafolder\dual\KP");
                break;
        }

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

            if (currentTargetA != null)
            {
                Destroy(currentTargetA);
                currentTargetA = null;
            }

            if (currentTargetBLeft != null)
            {
                Destroy(currentTargetBLeft);
                currentTargetBLeft = null;
            }

            if (currentTargetBRight != null)
            {
                Destroy(currentTargetBRight);
                currentTargetBRight = null;
            }

            ResetColors();
            statusMessage = "Task Stopped";
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 600, 350));

        GUILayout.Label($"Status: {statusMessage}", GUI.skin.box);

        if (taskRunning)
        {
            GUILayout.Label($"Trial: {trialCount}/{totalTrials} - Pattern: {currentHandPattern}", GUI.skin.box);
            GUILayout.Label($"Target A (Arm): {(currentTargetA != null ? "Active" : "Cleared")}", GUI.skin.box);
            GUILayout.Label($"Target B (Left): {(currentTargetBLeft != null ? "Active" : (currentHandPattern == HandPattern.Left || currentHandPattern == HandPattern.Both ? "Cleared" : "N/A"))}", GUI.skin.box);
            GUILayout.Label($"Target B (Right): {(currentTargetBRight != null ? "Active" : (currentHandPattern == HandPattern.Right || currentHandPattern == HandPattern.Both ? "Cleared" : "N/A"))}", GUI.skin.box);
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
