using UnityEngine;
using LSL4Unity.Samples.SimpleInlet;

public class EMGJointController : MonoBehaviour
{
    [Header("EMG Data Source")]
    [SerializeField] private EMGSignalProcessor emgProcessor;

    [Header("Joint References")]
    [SerializeField] private Transform joint1;  // First joint transform
    [SerializeField] private Transform joint2;  // Second joint transform

    // Public getter properties for external access
    public Transform Joint1 => joint1;
    public Transform Joint2 => joint2;
    public EMGSignalProcessor EmgProcessor => emgProcessor;

    [Header("Joint Control Settings (Joy-Con Style)")]
    [SerializeField, Range(0.1f, 5f)] private float speedMultiplier = 1f; // 速度の倍率
    [SerializeField, Range(1f, 180f)] private float maxRotationSpeed = 90f; // degrees per second
    [SerializeField, Range(-180f, 180f)] private float joint1MinAngle = -90f;
    [SerializeField, Range(-180f, 180f)] private float joint1MaxAngle = 90f;
    [SerializeField, Range(-180f, 180f)] private float joint2MinAngle = -90f;
    [SerializeField, Range(-180f, 180f)] private float joint2MaxAngle = 90f;

    [Header("EMG Channel Mapping (BiTalino Ch1-4)")]
    [SerializeField, Range(1, 4)] private int joint1BendChannel = 1;     // Ch1: Joint1 positive
    [SerializeField, Range(1, 4)] private int joint1ExtendChannel = 2;   // Ch2: Joint1 negative
    [SerializeField, Range(1, 4)] private int joint2BendChannel = 3;     // Ch3: Joint2 positive
    [SerializeField, Range(1, 4)] private int joint2ExtendChannel = 4;   // Ch4: Joint2 negative

    [Header("Control Thresholds")]
    [SerializeField, Range(0f, 20f)] private float activationThreshold = 5f; // 筋電割合の最小閾値(%)
    [SerializeField, Range(0f, 20f)] private float dominanceThreshold = 10f; // 拮抗筋の差分閾値(%)

    [Header("Debug Display")]
    [SerializeField] private bool showDebugGUI = true;
    [SerializeField] private bool logControlValues = false;

    // Runtime data - BiTalino has 4 channels
    private float[] currentEMGRatios = new float[4];  // 筋電割合 (0-100%)

    // Joint control state
    private float joint1CurrentAngle = 0f;
    private float joint2CurrentAngle = 0f;

    // Debug info
    private string debugStatus = "";
    private int updateCount = 0;

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
                Debug.LogError("EMGJointController: EMGSignalProcessor not found!");
                enabled = false;
                return;
            }
        }

        // Store initial joint rotations
        if (joint1 != null)
            joint1CurrentAngle = joint1.localEulerAngles.x;
        if (joint2 != null)
            joint2CurrentAngle = joint2.localEulerAngles.z;

        debugStatus = "Initialized";
        Debug.Log("EMGJointController: Initialized successfully (BiTalino mode)");
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
        ApplyJointRotations();

        updateCount++;

        if (logControlValues && updateCount % 30 == 0) // Log every 30 frames
        {
            LogControlValues();
        }
    }

    void UpdateEMGData()
    {
        // Get smoothed EMG ratios from BiTalino processor (0-100%)
        for (int i = 0; i < 4; i++)
        {
            currentEMGRatios[i] = emgProcessor.GetSmoothedValue(i + 1); // BiTalino uses 1-based indexing (Ch1-4)
        }

        debugStatus = "Receiving EMG Data";
    }

    void CalculateJointControl()
    {
        // Joint 1 Control (Joy-Con style: ratio determines speed)
        float joint1BendRatio = GetChannelRatio(joint1BendChannel);
        float joint1ExtendRatio = GetChannelRatio(joint1ExtendChannel);
        float joint1Speed = CalculateJoyConSpeed(joint1BendRatio, joint1ExtendRatio);

        // Joint 2 Control
        float joint2BendRatio = GetChannelRatio(joint2BendChannel);
        float joint2ExtendRatio = GetChannelRatio(joint2ExtendChannel);
        float joint2Speed = CalculateJoyConSpeed(joint2BendRatio, joint2ExtendRatio);

        // Update joint angles directly based on speed (Joy-Con style direct control)
        joint1CurrentAngle += joint1Speed * Time.deltaTime;
        joint2CurrentAngle += joint2Speed * Time.deltaTime;

        // Clamp angles to limits
        joint1CurrentAngle = Mathf.Clamp(joint1CurrentAngle, joint1MinAngle, joint1MaxAngle);
        joint2CurrentAngle = Mathf.Clamp(joint2CurrentAngle, joint2MinAngle, joint2MaxAngle);
    }

    /// <summary>
    /// ジョイコン風の速度計算: 筋電割合の差分で方向と速度を決定
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
            return 0f; // 拮抗している状態 - 動かさない
        }

        // Convert percentage difference to rotation speed
        // 100% difference = maxRotationSpeed
        float speedRatio = difference / 100f;
        float speed = speedRatio * maxRotationSpeed * speedMultiplier;

        return speed;
    }

    /// <summary>
    /// Get EMG ratio for a specific channel (0-100%)
    /// </summary>
    float GetChannelRatio(int channel)
    {
        if (channel < 1 || channel > 4) return 0f;
        return currentEMGRatios[channel - 1]; // Convert to 0-based index
    }

    void ApplyJointRotations()
    {
        if (joint1 != null)
        {
            Vector3 euler1 = joint1.localEulerAngles;
            euler1.z = joint1CurrentAngle;  // Joint1 uses Z-axis rotation
            joint1.localEulerAngles = euler1;
        }

        if (joint2 != null)
        {
            Vector3 euler2 = joint2.localEulerAngles;
            euler2.x = joint2CurrentAngle;  // Joint2 uses X-axis rotation
            joint2.localEulerAngles = euler2;
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

        Debug.Log($"EMG Control - J1: {joint1CurrentAngle:F1}° (speed:{j1Speed:F1}°/s) | " +
                  $"J2: {joint2CurrentAngle:F1}° (speed:{j2Speed:F1}°/s) | " +
                  $"Ch{joint1BendChannel}/Ch{joint1ExtendChannel}: {j1BendRatio:F1}%/{j1ExtendRatio:F1}% | " +
                  $"Ch{joint2BendChannel}/Ch{joint2ExtendChannel}: {j2BendRatio:F1}%/{j2ExtendRatio:F1}%");
    }

    // Public methods for external access
    public void ResetJointPositions()
    {
        joint1CurrentAngle = 0f;
        joint2CurrentAngle = 0f;
        Debug.Log("EMGJointController: Joint positions reset");
    }

    public Vector2 GetJointAngles()
    {
        return new Vector2(joint1CurrentAngle, joint2CurrentAngle);
    }

    public float GetCurrentEMGRatio(int channel)
    {
        return GetChannelRatio(channel);
    }

    void OnGUI()
    {
        if (!showDebugGUI || !Application.isPlaying) return;

        // Position debug window
        GUILayout.BeginArea(new Rect(420, 10, 400, 450));

        // Header
        GUILayout.Label("=== EMG Joint Controller (BiTalino) ===", GUI.skin.box);
        GUILayout.Label($"Status: {debugStatus}");
        GUILayout.Label($"Updates: {updateCount}");

        GUILayout.Space(10);

        // EMG Data Display (BiTalino Ch1-4)
        GUILayout.Label("BiTalino EMG Channels (Smoothed %):");
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

        // Joint Status
        GUILayout.Label("Joint Status:");
        float j1Speed = CalculateJoyConSpeed(GetChannelRatio(joint1BendChannel), GetChannelRatio(joint1ExtendChannel));
        float j2Speed = CalculateJoyConSpeed(GetChannelRatio(joint2BendChannel), GetChannelRatio(joint2ExtendChannel));
        GUILayout.Label($"Joint1: {joint1CurrentAngle:F1}° (speed: {j1Speed:F1}°/s)");
        GUILayout.Label($"Joint2: {joint2CurrentAngle:F1}° (speed: {j2Speed:F1}°/s)");

        GUILayout.Space(10);

        // Control Settings
        GUILayout.Label($"Activation Threshold: {activationThreshold:F1}%");
        GUILayout.Label($"Dominance Threshold: {dominanceThreshold:F1}%");
        GUILayout.Label($"Speed Multiplier: {speedMultiplier:F1}x");

        GUILayout.Space(10);

        // Control buttons
        if (GUILayout.Button("Reset Joint Positions"))
        {
            ResetJointPositions();
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
    }
}