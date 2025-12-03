using UnityEngine;
using LSL4Unity.Samples.SimpleInlet;

/// <summary>
/// JointController - Position Constraintと連動するジョイント制御
///
/// アタッチメント先:
/// - 各Joint/PivotオブジェクトにそれぞれアタッチしてOK
/// - または空のGameObjectにアタッチして複数のJointを一括制御することも可能
///
/// 前提条件:
/// - Joint/Pivotオブジェクトは親子関係なし
/// - Position Constraintコンポーネントで位置連動が設定済み
/// - このスクリプトは回転のみを制御
///
/// 設定方法:
/// 1. 制御したいJoint/PivotオブジェクトをInspectorで割り当て
/// 2. 回転軸（X, Y, Z）を指定
/// 3. マニュアルモードまたはEMGモードを選択
/// </summary>
public class JointController : MonoBehaviour
{
    [Header("EMG Data Source (EMGモード時のみ必要)")]
    [SerializeField] private EMGSignalProcessor emgProcessor;

    [Header("制御対象のJoint/Pivot (Position Constraint設定済み)")]
    [Tooltip("制御するJoint/Pivotオブジェクト")]
    [SerializeField] private Transform[] joints;

    [Header("回転軸の設定")]
    [Tooltip("各Jointの回転軸を指定")]
    [SerializeField] private RotationAxis[] rotationAxes;

    [Header("Test Mode (UDP不要のマニュアル制御)")]
    [Tooltip("テストモードを有効にするとEMG入力を無視してマニュアル制御")]
    [SerializeField] private bool useManualControl = false;

    [Tooltip("マニュアル制御: 各Jointの角度")]
    [SerializeField] private float[] manualAngles;

    [Header("Joint Control Settings (EMGモード)")]
    [SerializeField, Range(0.1f, 5f)] private float speedMultiplier = 1f;
    [SerializeField, Range(1f, 180f)] private float maxRotationSpeed = 90f;
    [SerializeField, Range(-180f, 180f)] private float minAngle = -90f;
    [SerializeField, Range(-180f, 180f)] private float maxAngle = 90f;

    [Header("EMG Channel Mapping (BiTalino Ch1-4)")]
    [Tooltip("各Jointの正方向チャンネル（Bend）")]
    [SerializeField] private int[] bendChannels;
    [Tooltip("各Jointの負方向チャンネル（Extend）")]
    [SerializeField] private int[] extendChannels;

    [Header("Control Thresholds")]
    [SerializeField, Range(0f, 20f)] private float activationThreshold = 5f;
    [SerializeField, Range(0f, 20f)] private float dominanceThreshold = 10f;

    [Header("Debug Display")]
    [SerializeField] private bool showDebugGUI = true;
    [SerializeField] private bool logControlValues = false;

    // Runtime data
    private float[] currentEMGRatios = new float[4];
    private float[] currentAngles;
    private string debugStatus = "";
    private int updateCount = 0;

    [System.Serializable]
    public enum RotationAxis
    {
        X,
        Y,
        Z
    }

    void Start()
    {
        InitializeController();
    }

    void InitializeController()
    {
        // Jointの数に合わせて配列を初期化
        if (joints != null && joints.Length > 0)
        {
            currentAngles = new float[joints.Length];

            // 配列のサイズを調整
            if (manualAngles == null || manualAngles.Length != joints.Length)
            {
                manualAngles = new float[joints.Length];
            }
            if (rotationAxes == null || rotationAxes.Length != joints.Length)
            {
                rotationAxes = new RotationAxis[joints.Length];
                for (int i = 0; i < joints.Length; i++)
                {
                    rotationAxes[i] = RotationAxis.Z; // デフォルトはZ軸
                }
            }
            if (bendChannels == null || bendChannels.Length != joints.Length)
            {
                bendChannels = new int[joints.Length];
                for (int i = 0; i < joints.Length; i++)
                {
                    bendChannels[i] = (i * 2 + 1); // デフォルト: 1, 3, ...
                }
            }
            if (extendChannels == null || extendChannels.Length != joints.Length)
            {
                extendChannels = new int[joints.Length];
                for (int i = 0; i < joints.Length; i++)
                {
                    extendChannels[i] = (i * 2 + 2); // デフォルト: 2, 4, ...
                }
            }

            // 初期角度を取得
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] != null)
                {
                    currentAngles[i] = GetAngleFromAxis(joints[i], rotationAxes[i]);
                }
            }
        }

        // EMGプロセッサーの検索（マニュアルモードでない場合）
        if (!useManualControl)
        {
            if (emgProcessor == null)
            {
                emgProcessor = FindObjectOfType<EMGSignalProcessor>();
                if (emgProcessor == null)
                {
                    Debug.LogWarning("JointController: EMGSignalProcessor not found! Switching to manual control mode.");
                    useManualControl = true;
                }
            }
        }

        if (useManualControl)
        {
            debugStatus = "Manual Control Mode";
            Debug.Log("JointController: Initialized in Manual Control Mode (UDP/EMG disabled)");
        }
        else
        {
            debugStatus = "EMG Control Mode";
            Debug.Log("JointController: Initialized in EMG Control Mode");
        }
    }

    void Update()
    {
        if (joints == null || joints.Length == 0) return;

        // Manual Control Mode - インスペクターで直接角度を設定
        if (useManualControl)
        {
            for (int i = 0; i < joints.Length; i++)
            {
                if (i < manualAngles.Length)
                {
                    currentAngles[i] = manualAngles[i];
                }
            }
            debugStatus = "Manual Control Active";
        }
        else
        {
            // EMG Control Mode
            if (emgProcessor == null)
            {
                debugStatus = "EMG Processor not found";
                return;
            }

            if (emgProcessor.mode != EMGSignalProcessor.ProcessingMode.Measurement)
            {
                debugStatus = $"Waiting for Measurement mode (current: {emgProcessor.mode})";
                return;
            }

            UpdateEMGData();
            CalculateJointControl();

            if (logControlValues && updateCount % 30 == 0)
            {
                LogControlValues();
            }
        }

        // 回転を適用（Position Constraintは自動的に位置を連動）
        ApplyJointRotations();

        updateCount++;
    }

    void UpdateEMGData()
    {
        for (int i = 0; i < 4; i++)
        {
            currentEMGRatios[i] = emgProcessor.GetSmoothedValue(i + 1);
        }
        debugStatus = "Receiving EMG Data";
    }

    void CalculateJointControl()
    {
        for (int i = 0; i < joints.Length; i++)
        {
            if (i >= bendChannels.Length || i >= extendChannels.Length) continue;

            float bendRatio = GetChannelRatio(bendChannels[i]);
            float extendRatio = GetChannelRatio(extendChannels[i]);
            float speed = CalculateJoyConSpeed(bendRatio, extendRatio);

            currentAngles[i] += speed * Time.deltaTime;
            currentAngles[i] = Mathf.Clamp(currentAngles[i], minAngle, maxAngle);
        }
    }

    float CalculateJoyConSpeed(float bendRatio, float extendRatio)
    {
        if (bendRatio < activationThreshold) bendRatio = 0f;
        if (extendRatio < activationThreshold) extendRatio = 0f;

        float difference = bendRatio - extendRatio;

        if (Mathf.Abs(difference) < dominanceThreshold)
        {
            return 0f;
        }

        float speedRatio = difference / 100f;
        float speed = speedRatio * maxRotationSpeed * speedMultiplier;

        return speed;
    }

    float GetChannelRatio(int channel)
    {
        if (channel < 1 || channel > 4) return 0f;
        return currentEMGRatios[channel - 1];
    }

    /// <summary>
    /// 回転を適用（Position Constraintは位置を自動制御）
    /// </summary>
    void ApplyJointRotations()
    {
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] == null) continue;

            SetAngleToAxis(joints[i], rotationAxes[i], currentAngles[i]);
        }
    }

    float GetAngleFromAxis(Transform target, RotationAxis axis)
    {
        Vector3 euler = target.eulerAngles;
        switch (axis)
        {
            case RotationAxis.X: return euler.x;
            case RotationAxis.Y: return euler.y;
            case RotationAxis.Z: return euler.z;
            default: return 0f;
        }
    }

    void SetAngleToAxis(Transform target, RotationAxis axis, float angle)
    {
        Vector3 euler = target.eulerAngles;
        switch (axis)
        {
            case RotationAxis.X:
                euler.x = angle;
                break;
            case RotationAxis.Y:
                euler.y = angle;
                break;
            case RotationAxis.Z:
                euler.z = angle;
                break;
        }
        target.eulerAngles = euler;
    }

    void LogControlValues()
    {
        string log = "JointController - ";
        for (int i = 0; i < joints.Length; i++)
        {
            if (i < bendChannels.Length && i < extendChannels.Length)
            {
                float bendRatio = GetChannelRatio(bendChannels[i]);
                float extendRatio = GetChannelRatio(extendChannels[i]);
                float speed = CalculateJoyConSpeed(bendRatio, extendRatio);
                log += $"J{i}: {currentAngles[i]:F1}° (Ch{bendChannels[i]}/{extendChannels[i]}: {bendRatio:F1}%/{extendRatio:F1}%) ";
            }
        }
        Debug.Log(log);
    }

    // Public methods
    public void ResetJointPositions()
    {
        for (int i = 0; i < currentAngles.Length; i++)
        {
            currentAngles[i] = 0f;
        }
        Debug.Log("JointController: Joint positions reset");
    }

    public float[] GetJointAngles()
    {
        return currentAngles;
    }

    void OnGUI()
    {
        if (!showDebugGUI || !Application.isPlaying) return;

        GUILayout.BeginArea(new Rect(10, 10, 400, 600));

        string mode = useManualControl ? "Manual Control" : "EMG Control";
        GUILayout.Label($"=== Joint Controller ({mode}) ===", GUI.skin.box);
        GUILayout.Label($"Status: {debugStatus}");
        GUILayout.Label($"Updates: {updateCount}");
        GUILayout.Label($"Position Constraint: Enabled");

        GUILayout.Space(10);

        if (useManualControl)
        {
            GUILayout.Label("*** MANUAL CONTROL MODE ***", GUI.skin.box);
            GUILayout.Label("インスペクターでJoint角度を調整できます");
            GUILayout.Label("UDP/EMG入力は無効化されています");
            GUILayout.Space(10);
        }

        // Joint Status
        GUILayout.Label("Joint Status:");
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i] != null)
            {
                string axisName = rotationAxes[i].ToString();
                if (useManualControl)
                {
                    GUILayout.Label($"Joint{i} ({axisName}-axis): {currentAngles[i]:F1}°");
                }
                else if (i < bendChannels.Length && i < extendChannels.Length)
                {
                    float bendRatio = GetChannelRatio(bendChannels[i]);
                    float extendRatio = GetChannelRatio(extendChannels[i]);
                    float speed = CalculateJoyConSpeed(bendRatio, extendRatio);
                    GUILayout.Label($"Joint{i} ({axisName}): {currentAngles[i]:F1}° (Ch{bendChannels[i]}/{extendChannels[i]}: {bendRatio:F1}%/{extendRatio:F1}%, speed: {speed:F1}°/s)");
                }
            }
        }

        GUILayout.Space(10);

        if (!useManualControl)
        {
            GUILayout.Label("EMG Channels (Smoothed %):");
            for (int i = 1; i <= 4; i++)
            {
                float ratio = GetChannelRatio(i);
                GUILayout.Label($"Ch{i}: {ratio:F1}%");
            }

            GUILayout.Space(10);

            GUILayout.Label("Control Settings:");
            GUILayout.Label($"Activation Threshold: {activationThreshold:F1}%");
            GUILayout.Label($"Dominance Threshold: {dominanceThreshold:F1}%");
            GUILayout.Label($"Speed Multiplier: {speedMultiplier:F1}x");
            GUILayout.Space(10);
        }

        if (GUILayout.Button("Reset Joint Positions"))
        {
            ResetJointPositions();
        }

        if (useManualControl)
        {
            GUILayout.Label("※ インスペクターで角度を直接変更可能");
        }

        GUILayout.EndArea();
    }

    void OnValidate()
    {
        // Jointの数に合わせて配列のサイズを調整
        if (joints != null && joints.Length > 0)
        {
            // manualAnglesのサイズ調整
            if (manualAngles == null || manualAngles.Length != joints.Length)
            {
                float[] newManualAngles = new float[joints.Length];
                if (manualAngles != null)
                {
                    for (int i = 0; i < Mathf.Min(manualAngles.Length, newManualAngles.Length); i++)
                    {
                        newManualAngles[i] = manualAngles[i];
                    }
                }
                manualAngles = newManualAngles;
            }

            // rotationAxesのサイズ調整
            if (rotationAxes == null || rotationAxes.Length != joints.Length)
            {
                RotationAxis[] newAxes = new RotationAxis[joints.Length];
                if (rotationAxes != null)
                {
                    for (int i = 0; i < Mathf.Min(rotationAxes.Length, newAxes.Length); i++)
                    {
                        newAxes[i] = rotationAxes[i];
                    }
                }
                rotationAxes = newAxes;
            }

            // bendChannelsのサイズ調整
            if (bendChannels == null || bendChannels.Length != joints.Length)
            {
                int[] newBendChannels = new int[joints.Length];
                if (bendChannels != null)
                {
                    for (int i = 0; i < Mathf.Min(bendChannels.Length, newBendChannels.Length); i++)
                    {
                        newBendChannels[i] = bendChannels[i];
                    }
                }
                else
                {
                    for (int i = 0; i < newBendChannels.Length; i++)
                    {
                        newBendChannels[i] = i * 2 + 1; // デフォルト: 1, 3, ...
                    }
                }
                bendChannels = newBendChannels;
            }

            // extendChannelsのサイズ調整
            if (extendChannels == null || extendChannels.Length != joints.Length)
            {
                int[] newExtendChannels = new int[joints.Length];
                if (extendChannels != null)
                {
                    for (int i = 0; i < Mathf.Min(extendChannels.Length, newExtendChannels.Length); i++)
                    {
                        newExtendChannels[i] = extendChannels[i];
                    }
                }
                else
                {
                    for (int i = 0; i < newExtendChannels.Length; i++)
                    {
                        newExtendChannels[i] = i * 2 + 2; // デフォルト: 2, 4, ...
                    }
                }
                extendChannels = newExtendChannels;
            }
        }

        // チャンネル値のクランプ (1-4)
        if (bendChannels != null)
        {
            for (int i = 0; i < bendChannels.Length; i++)
            {
                bendChannels[i] = Mathf.Clamp(bendChannels[i], 1, 4);
            }
        }
        if (extendChannels != null)
        {
            for (int i = 0; i < extendChannels.Length; i++)
            {
                extendChannels[i] = Mathf.Clamp(extendChannels[i], 1, 4);
            }
        }
    }
}
