using UnityEngine;
using LSL4Unity.Samples.SimpleInlet;

/// <summary>
/// 角度変更のみでロボットアームを制御するEMGコントローラー
/// RoboticArmLinkageスタイルの制御方式を使用
/// </summary>
public class AngleBasedEMGJointController : MonoBehaviour
{
    [Header("EMG Data Source")]
    [SerializeField] private EMGSignalProcessor emgProcessor;

    [Header("Joint Control Objects")]
    [SerializeField] private Transform joint1Object;  // 第1関節オブジェクト
    [SerializeField] private Transform joint2Object;  // 第2関節オブジェクト

    // Public getter properties for external access
    public Transform Joint1Object => joint1Object;
    public Transform Joint2Object => joint2Object;
    public EMGSignalProcessor EmgProcessor => emgProcessor;

    [Header("Joint Angle Control")]
    [SerializeField, Range(0.1f, 5f)] private float speedMultiplier = 1f;
    [SerializeField, Range(1f, 180f)] private float maxRotationSpeed = 90f; // degrees per second
    [SerializeField, Range(-180f, 180f)] private float joint1MinAngle = -90f;
    [SerializeField, Range(-180f, 180f)] private float joint1MaxAngle = 90f;
    [SerializeField, Range(-180f, 180f)] private float joint2MinAngle = -90f;
    [SerializeField, Range(-180f, 180f)] private float joint2MaxAngle = 90f;

    [Header("Rotation Axis Settings")]
    [SerializeField] private RotationAxis joint1RotationAxis = RotationAxis.Z;
    [SerializeField] private RotationAxis joint2RotationAxis = RotationAxis.X;

    [Header("EMG Channel Mapping (BiTalino Ch1-4)")]
    [SerializeField, Range(1, 4)] private int joint1BendChannel = 1;     // Ch1: Joint1 positive
    [SerializeField, Range(1, 4)] private int joint1ExtendChannel = 2;   // Ch2: Joint1 negative
    [SerializeField, Range(1, 4)] private int joint2BendChannel = 3;     // Ch3: Joint2 positive
    [SerializeField, Range(1, 4)] private int joint2ExtendChannel = 4;   // Ch4: Joint2 negative

    [Header("Control Thresholds")]
    [SerializeField, Range(0f, 20f)] private float activationThreshold = 5f;
    [SerializeField, Range(0f, 20f)] private float dominanceThreshold = 10f;

    [Header("Debug Display")]
    [SerializeField] private bool showDebugGUI = true;
    [SerializeField] private bool logControlValues = false;

    // Runtime data - BiTalino has 4 channels
    private float[] currentEMGRatios = new float[4];

    // Target angles (0-180 degrees range like RoboticArmLinkage)
    private float joint1TargetAngle = 0f;
    private float joint2TargetAngle = 0f;

    // Initial rotations for reference
    private Quaternion joint1InitialRotation;
    private Quaternion joint2InitialRotation;

    // Debug info
    private string debugStatus = "";
    private int updateCount = 0;

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
        // Find EMGSignalProcessor if not assigned
        if (emgProcessor == null)
        {
            emgProcessor = FindObjectOfType<EMGSignalProcessor>();
            if (emgProcessor == null)
            {
                Debug.LogError("AngleBasedEMGJointController: EMGSignalProcessor not found!");
                enabled = false;
                return;
            }
        }

        // Store initial rotations as reference points
        if (joint1Object != null)
            joint1InitialRotation = joint1Object.rotation;
        if (joint2Object != null)
            joint2InitialRotation = joint2Object.rotation;

        // Initialize target angles to center of range
        joint1TargetAngle = (joint1MinAngle + joint1MaxAngle) / 2f;
        joint2TargetAngle = (joint2MinAngle + joint2MaxAngle) / 2f;

        debugStatus = "Initialized";
        Debug.Log("AngleBasedEMGJointController: Initialized successfully (Angle-based mode)");
    }

    void Update()
    {
        if (emgProcessor == null)
        {
            debugStatus = "EMG Processor not found";
            return;
        }

        if (emgProcessor.mode != LSL4Unity.Samples.SimpleInlet.EMGSignalProcessor.ProcessingMode.Measurement)
        {
            debugStatus = $"Waiting for Measurement mode (current: {emgProcessor.mode})";
            return;
        }

        UpdateEMGData();
        CalculateJointControl();
        ApplyAngleBasedJointRotations();

        updateCount++;

        if (logControlValues && updateCount % 30 == 0) // Log every 30 frames
        {
            LogControlValues();
        }
    }

    void UpdateEMGData()
    {
        // Get smoothed EMG ratios from BiTalino processor (0-100%)
        // UDPから送られるデータをそのまま使用
        for (int i = 0; i < 4; i++)
        {
            currentEMGRatios[i] = emgProcessor.GetSmoothedValue(i + 1); // BiTalino uses 1-based indexing (Ch1-4)
        }

        debugStatus = "Receiving EMG Data";
    }

    void CalculateJointControl()
    {
        // Joint 1 Control
        float joint1BendRatio = GetChannelRatio(joint1BendChannel);
        float joint1ExtendRatio = GetChannelRatio(joint1ExtendChannel);
        float joint1Speed = CalculateJoyConSpeed(joint1BendRatio, joint1ExtendRatio);

        // Joint 2 Control
        float joint2BendRatio = GetChannelRatio(joint2BendChannel);
        float joint2ExtendRatio = GetChannelRatio(joint2ExtendChannel);
        float joint2Speed = CalculateJoyConSpeed(joint2BendRatio, joint2ExtendRatio);

        // Update target angles based on speed
        joint1TargetAngle += joint1Speed * Time.deltaTime;
        joint2TargetAngle += joint2Speed * Time.deltaTime;

        // Clamp angles to limits
        joint1TargetAngle = Mathf.Clamp(joint1TargetAngle, joint1MinAngle, joint1MaxAngle);
        joint2TargetAngle = Mathf.Clamp(joint2TargetAngle, joint2MinAngle, joint2MaxAngle);
    }

    /// <summary>
    /// Joy-Con style speed calculation: determine direction and speed from EMG ratio difference
    /// UDPデータをそのまま使用
    /// </summary>
    float CalculateJoyConSpeed(float bendRatio, float extendRatio)
    {
        // Apply activation threshold
        if (bendRatio < activationThreshold) bendRatio = 0f;
        if (extendRatio < activationThreshold) extendRatio = 0f;

        // Calculate difference (positive = bend, negative = extend)
        float difference = bendRatio - extendRatio;

        // Only move if difference exceeds dominance threshold
        if (Mathf.Abs(difference) < dominanceThreshold)
        {
            return 0f; // Antagonist state - no movement
        }

        // Convert percentage difference to rotation speed
        // 100% difference = maxRotationSpeed
        float speedRatio = difference / 100f;
        float speed = speedRatio * maxRotationSpeed * speedMultiplier;

        return speed;
    }

    /// <summary>
    /// Get EMG ratio for a specific channel (0-100%)
    /// UDPから送られるデータをそのまま使用
    /// </summary>
    float GetChannelRatio(int channel)
    {
        if (channel < 1 || channel > 4) return 0f;
        return currentEMGRatios[channel - 1]; // Convert to 0-based index
    }

    /// <summary>
    /// RoboticArmLinkageスタイルの角度ベース回転適用
    /// 角度変更のみで制御
    /// </summary>
    void ApplyAngleBasedJointRotations()
    {
        // RoboticArmLinkageのGetRotationFromAngleと同様の方式
        if (joint1Object != null)
        {
            Quaternion joint1Rotation = GetRotationFromAngle(joint1TargetAngle, joint1RotationAxis);
            joint1Object.rotation = joint1InitialRotation * joint1Rotation;
        }

        if (joint2Object != null)
        {
            Quaternion joint2Rotation = GetRotationFromAngle(joint2TargetAngle, joint2RotationAxis);
            joint2Object.rotation = joint2InitialRotation * joint2Rotation;
        }
    }

    /// <summary>
    /// 角度から回転を生成（RoboticArmLinkageと同じ方式）
    /// </summary>
    Quaternion GetRotationFromAngle(float angle, RotationAxis axis)
    {
        switch (axis)
        {
            case RotationAxis.X:
                return Quaternion.Euler(angle, 0, 0);
            case RotationAxis.Y:
                return Quaternion.Euler(0, angle, 0);
            case RotationAxis.Z:
                return Quaternion.Euler(0, 0, angle);
            default:
                return Quaternion.identity;
        }
    }

    void LogControlValues()
    {
        float j1BendRatio = GetChannelRatio(joint1BendChannel);
        float j1ExtendRatio = GetChannelRatio(joint1ExtendChannel);
        float j1Speed = CalculateJoyConSpeed(j1BendRatio, j1ExtendRatio);

        float j2BendRatio = GetChannelRatio(joint2BendChannel);
        float j2ExtendRatio = GetChannelRatio(joint2ExtendChannel);
        float j2Speed = CalculateJoyConSpeed(j2BendRatio, j2ExtendRatio);

        Debug.Log($"EMG Angle Control - J1: {joint1TargetAngle:F1}° (speed:{j1Speed:F1}°/s) | " +
                  $"J2: {joint2TargetAngle:F1}° (speed:{j2Speed:F1}°/s) | " +
                  $"Ch{joint1BendChannel}/Ch{joint1ExtendChannel}: {j1BendRatio:F1}%/{j1ExtendRatio:F1}% | " +
                  $"Ch{joint2BendChannel}/Ch{joint2ExtendChannel}: {j2BendRatio:F1}%/{j2ExtendRatio:F1}%");
    }

    // Public methods for external access
    public void ResetJointPositions()
    {
        // Reset to center of angle range
        joint1TargetAngle = (joint1MinAngle + joint1MaxAngle) / 2f;
        joint2TargetAngle = (joint2MinAngle + joint2MaxAngle) / 2f;
        
        // Apply reset rotations
        ApplyAngleBasedJointRotations();
            
        Debug.Log("AngleBasedEMGJointController: Joint angles reset to center position");
    }

    public Vector2 GetJointAngles()
    {
        return new Vector2(joint1TargetAngle, joint2TargetAngle);
    }

    public float GetCurrentEMGRatio(int channel)
    {
        return GetChannelRatio(channel);
    }

    /// <summary>
    /// Set target angles directly (for external control)
    /// </summary>
    public void SetJointAngles(float joint1Angle, float joint2Angle)
    {
        joint1TargetAngle = Mathf.Clamp(joint1Angle, joint1MinAngle, joint1MaxAngle);
        joint2TargetAngle = Mathf.Clamp(joint2Angle, joint2MinAngle, joint2MaxAngle);
    }

    /// <summary>
    /// Set the rotation axes for each joint
    /// </summary>
    public void SetJointRotationAxes(RotationAxis joint1Axis, RotationAxis joint2Axis)
    {
        joint1RotationAxis = joint1Axis;
        joint2RotationAxis = joint2Axis;
        Debug.Log($"Joint rotation axes updated - J1: {joint1RotationAxis}, J2: {joint2RotationAxis}");
    }

    /// <summary>
    /// Update initial rotations for reference
    /// </summary>
    public void UpdateInitialRotations()
    {
        if (joint1Object != null)
            joint1InitialRotation = joint1Object.rotation;
        if (joint2Object != null)
            joint2InitialRotation = joint2Object.rotation;
        
        Debug.Log("Initial rotations updated");
    }

    void OnGUI()
    {
        if (!showDebugGUI || !Application.isPlaying) return;

        // Position debug window
        GUILayout.BeginArea(new Rect(420, 10, 450, 550));

        // Header
        GUILayout.Label("=== Angle-Based EMG Joint Controller ===", GUI.skin.box);
        GUILayout.Label($"Status: {debugStatus}");
        GUILayout.Label($"Updates: {updateCount}");

        GUILayout.Space(10);

        // EMG Data Display (BiTalino Ch1-4)
        GUILayout.Label("BiTalino EMG Channels (UDP Data):");
        for (int i = 1; i <= 4; i++)
        {
            string channelName = GetChannelName(i);
            float ratio = GetChannelRatio(i);
            GUILayout.Label($"{channelName}: {ratio:F1}%");
        }

        GUILayout.Space(10);

        // Control Mapping
        GUILayout.Label("Control Mapping:");
        GUILayout.Label($"Joint1 Bend: Ch{joint1BendChannel} ({GetChannelRatio(joint1BendChannel):F1}%)");
        GUILayout.Label($"Joint1 Extend: Ch{joint1ExtendChannel} ({GetChannelRatio(joint1ExtendChannel):F1}%)");
        GUILayout.Label($"Joint2 Bend: Ch{joint2BendChannel} ({GetChannelRatio(joint2BendChannel):F1}%)");
        GUILayout.Label($"Joint2 Extend: Ch{joint2ExtendChannel} ({GetChannelRatio(joint2ExtendChannel):F1}%)");

        GUILayout.Space(10);

        // Joint Status (Angle-based)
        GUILayout.Label("Angle-Based Joint Status:");
        float j1Speed = CalculateJoyConSpeed(GetChannelRatio(joint1BendChannel), GetChannelRatio(joint1ExtendChannel));
        float j2Speed = CalculateJoyConSpeed(GetChannelRatio(joint2BendChannel), GetChannelRatio(joint2ExtendChannel));
        GUILayout.Label($"Joint1: {joint1TargetAngle:F1}° (speed: {j1Speed:F1}°/s)");
        GUILayout.Label($"Joint2: {joint2TargetAngle:F1}° (speed: {j2Speed:F1}°/s)");
        
        GUILayout.Space(5);
        GUILayout.Label("Rotation Axes:");
        GUILayout.Label($"Joint1 Axis: {joint1RotationAxis}");
        GUILayout.Label($"Joint2 Axis: {joint2RotationAxis}");
        
        GUILayout.Space(5);
        GUILayout.Label("Angle Ranges:");
        GUILayout.Label($"Joint1: {joint1MinAngle:F0}° to {joint1MaxAngle:F0}°");
        GUILayout.Label($"Joint2: {joint2MinAngle:F0}° to {joint2MaxAngle:F0}°");

        GUILayout.Space(10);

        // Control Settings
        GUILayout.Label($"Activation Threshold: {activationThreshold:F1}%");
        GUILayout.Label($"Dominance Threshold: {dominanceThreshold:F1}%");
        GUILayout.Label($"Speed Multiplier: {speedMultiplier:F1}x");

        GUILayout.Space(10);

        // Control buttons
        if (GUILayout.Button("Reset Joint Angles"))
        {
            ResetJointPositions();
        }
        
        if (GUILayout.Button("Update Initial Rotations"))
        {
            UpdateInitialRotations();
        }

        GUILayout.EndArea();
    }

    string GetChannelName(int channel)
    {
        return $"Ch{channel}";
    }

    void OnValidate()
    {
        // Ensure channel indices are within valid range (BiTalino Ch1-4)
        joint1BendChannel = Mathf.Clamp(joint1BendChannel, 1, 4);
        joint1ExtendChannel = Mathf.Clamp(joint1ExtendChannel, 1, 4);
        joint2BendChannel = Mathf.Clamp(joint2BendChannel, 1, 4);
        joint2ExtendChannel = Mathf.Clamp(joint2ExtendChannel, 1, 4);

        // Ensure angle ranges are valid
        joint1MinAngle = Mathf.Clamp(joint1MinAngle, -180f, 180f);
        joint1MaxAngle = Mathf.Clamp(joint1MaxAngle, -180f, 180f);
        joint2MinAngle = Mathf.Clamp(joint2MinAngle, -180f, 180f);
        joint2MaxAngle = Mathf.Clamp(joint2MaxAngle, -180f, 180f);
    }
}
