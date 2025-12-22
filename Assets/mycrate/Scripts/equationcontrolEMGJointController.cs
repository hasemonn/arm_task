using UnityEngine;
using LSL4Unity.Samples.SimpleInlet;

public class IndependentEMGJointController : MonoBehaviour
{
    public enum ControlMode
    {
        EMG,        // EMG信号で制御
        Manual      // インスペクタで手動制御
    }

    [Header("Control Mode")]
    [SerializeField] private ControlMode controlMode = ControlMode.EMG;

    [Header("Manual Control (Manual Mode Only)")]
    [SerializeField, Range(-180f, 180f)] private float manualJoint1Angle = 0f;
    [SerializeField, Range(-180f, 180f)] private float manualJoint2Angle = 0f;

    [Header("EMG Data Source (EMG Mode Only)")]
    [SerializeField] private EMGSignalProcessor emgProcessor;

    [Header("Joint References")]
    [SerializeField] private Transform joint1;  // First joint (pivot1)
    [SerializeField] private Transform joint2;  // Second joint (pivot2)

    [Header("Link References (For Independent FK)")]
    [SerializeField] private Transform baseObject;     // Cylinder1 
    [SerializeField] private Transform link1;          // Cylinder2 
    [SerializeField] private Transform link2;          // Cylinder3 
    [SerializeField] private Transform endEffector;    // Ball

    // Link Lengths (Fixed values)
    private const float baseToJoint1Length = 0.2f;
    private const float joint1ToJoint2Length = 0.4f;
    private const float link2Length = 0.4f;

    // Public getters for link lengths
    public float BaseToJoint1Length => baseToJoint1Length;
    public float Joint1ToJoint2Length => joint1ToJoint2Length;
    public float Link2Length => link2Length;

    // Public getter properties for external access
    public Transform Joint1 => joint1;
    public Transform Joint2 => joint2;
    public Transform BaseObject => baseObject;
    public Transform EndEffector => endEffector;
    public Vector3 Joint1RotationAxis => joint1RotationAxis;
    public Vector3 Joint2RotationAxis => joint2RotationAxis;
    public EMGSignalProcessor EmgProcessor => emgProcessor;

    [Header("Joint Control Settings")]
    [SerializeField, Range(0.1f, 5f)] private float speedMultiplier = 1f;
    [SerializeField, Range(1f, 180f)] private float maxRotationSpeed = 90f; // degrees per second
    [SerializeField, Range(-180f, 180f)] private float joint1MinAngle = -30f;
    [SerializeField, Range(-180f, 180f)] private float joint1MaxAngle = 80f;
    [SerializeField, Range(-180f, 180f)] private float joint2MinAngle = 0f;
    [SerializeField, Range(-180f, 180f)] private float joint2MaxAngle = 160f;

    [Header("Joint Rotation Axes")]
    [SerializeField] private Vector3 joint1RotationAxis = Vector3.forward; // Z-axis by default
    [SerializeField] private Vector3 joint2RotationAxis = Vector3.right;   // X-axis by default

    [Header("EMG Channel Mapping (BiTalino Ch1-4)")]
    [SerializeField, Range(1, 4)] private int joint1BendChannel = 1;     // Ch1: Joint1 positive
    [SerializeField, Range(1, 4)] private int joint1ExtendChannel = 2;   // Ch2: Joint1 negative
    [SerializeField, Range(1, 4)] private int joint2BendChannel = 3;     // Ch3: Joint2 positive
    [SerializeField, Range(1, 4)] private int joint2ExtendChannel = 4;   // Ch4: Joint2 negative

    [Header("Control Thresholds")]
    [SerializeField, Range(0f, 20f)] private float activationThreshold = 5f;
    [SerializeField, Range(0f, 20f)] private float dominanceThreshold = 10f;

    [Header("Physics Parameters")]
    [SerializeField, Range(0f, 10f)] private float dampingCoefficient = 2f; // 粘性抵抗係数（大きいほど早く止まる）
    [SerializeField, Range(0f, 1f)] private float velocityThreshold = 0.1f; // この速度以下になったら完全停止

    [Header("Debug Display")]
    [SerializeField] private bool showDebugGUI = true;
    [SerializeField] private bool logControlValues = false;

    // Runtime data - BiTalino has 4 channels
    private float[] currentEMGRatios = new float[4];

    // Independent joint angles (not dependent on initial transform rotations)
    private float joint1TargetAngle = 0f;
    private float joint2TargetAngle = 0f;

    // Base reference for FK calculation
    private Vector3 basePosition;
    private Quaternion baseRotation;

    // For jerk and acceleration calculation
    private float joint1LastSpeed = 0f;
    private float joint1LastAccel = 0f;
    private float joint2LastSpeed = 0f;
    private float joint2LastAccel = 0f;

    public float Joint1Jerk { get; private set; }
    public float Joint2Jerk { get; private set; }
    public float Joint1Acceleration { get; private set; }
    public float Joint2Acceleration { get; private set; }

    // Debug info
    private int updateCount = 0;
    private bool isInitialized = false;

    // Freeze control
    private bool isFrozen = false;
    private float frozenJoint1Angle = 0f;
    private float frozenJoint2Angle = 0f;

    void Start()
    {
        InitializeController();
    }

    void InitializeController()
    {
        // EMG mode requires EMGSignalProcessor (simplified logic like EMGJointController)
        if (controlMode == ControlMode.EMG)
        {
            // Find EMGSignalProcessor if not assigned
            if (emgProcessor == null)
            {
                emgProcessor = FindObjectOfType<EMGSignalProcessor>();
                if (emgProcessor == null)
                {
                    Debug.LogError("IndependentEMGJointController: EMGSignalProcessor not found!");
                    enabled = false;
                    return;
                }
                else
                {
                    Debug.Log($"IndependentEMGJointController: EMGSignalProcessor found on '{emgProcessor.gameObject.name}'");
                }
            }
        }

        // Store base position and rotation as reference
        if (baseObject != null)
        {
            basePosition = baseObject.position;
            baseRotation = baseObject.rotation;
        }
        else
        {
            // If no base object, use world origin pointing up
            basePosition = Vector3.zero;
            baseRotation = Quaternion.identity;
        }

        // Reset target angles to 0
        joint1TargetAngle = 0f;
        joint2TargetAngle = 0f;

        isInitialized = true;
        Debug.Log($"IndependentEMGJointController: Initialized successfully ({controlMode} mode)");
    }

    void Update()
    {
        switch (controlMode)
        {
            case ControlMode.EMG:
                UpdateEMGControl();
                break;

            case ControlMode.Manual:
                UpdateManualControl();
                break;
        }

        ApplyIndependentJointRotations();
        updateCount++;
    }

    void UpdateEMGControl()
    {
        if (emgProcessor == null)
        {
            Debug.LogWarning("IndependentEMGJointController: EMG Processor is null in UpdateEMGControl");
            return;
        }

        if (emgProcessor.mode != LSL4Unity.Samples.SimpleInlet.EMGSignalProcessor.ProcessingMode.Measurement)
        {
            return;
        }

        // If frozen, maintain frozen angles
        if (isFrozen)
        {
            joint1TargetAngle = frozenJoint1Angle;
            joint2TargetAngle = frozenJoint2Angle;
            return;
        }

        UpdateEMGData();
        CalculateJointControl();

        if (logControlValues && updateCount % 30 == 0) // Log every 30 frames
        {
            LogControlValues();
        }
    }

    void UpdateManualControl()
    {
        // If frozen, maintain frozen angles
        if (isFrozen)
        {
            joint1TargetAngle = frozenJoint1Angle;
            joint2TargetAngle = frozenJoint2Angle;
            return;
        }

        // Directly use manual angles from inspector and clamp to limits
        joint1TargetAngle = Mathf.Clamp(manualJoint1Angle, joint1MinAngle, joint1MaxAngle);
        joint2TargetAngle = Mathf.Clamp(manualJoint2Angle, joint2MinAngle, joint2MaxAngle);
    }

    void UpdateEMGData()
    {
        // Get smoothed EMG ratios from BiTalino processor (0-100%)
        for (int i = 0; i < 4; i++)
        {
            currentEMGRatios[i] = emgProcessor.GetSmoothedValue(i + 1); // BiTalino uses 1-based indexing (Ch1-4)
        }
    }

    void CalculateJointControl()
    {
        float dt = Time.deltaTime;

        // リンクの長さと慣性モーメント
        float length1 = 0.2f;  // Link1 length
        float length2 = 0.2f;  // Link2 length
        float mass1 = 1.0f;    // Link1 mass 
        float mass2 = 1.0f;    // Link2 mass 

        // 慣性モーメント I = m * L^2 
        float inertia1 = mass1 * length1 * length1;
        float inertia2 = mass2 * length2 * length2;

        // Joint 1 Control: EMG信号からトルクを計算
        float joint1BendRatio = GetChannelRatio(joint1BendChannel);
        float joint1ExtendRatio = GetChannelRatio(joint1ExtendChannel);
        float joint1Torque = CalculateTorque(joint1BendRatio, joint1ExtendRatio);

        // Joint 2 Control: EMG信号からトルクを計算
        float joint2BendRatio = GetChannelRatio(joint2BendChannel);
        float joint2ExtendRatio = GetChannelRatio(joint2ExtendChannel);
        float joint2Torque = CalculateTorque(joint2BendRatio, joint2ExtendRatio);

        // Joint 1: 粘性抵抗を含む角加速度を計算
        // 運動方程式: τ - b*ω = I*α → α = (τ - b*ω) / I
        float dampingForce1 = dampingCoefficient * joint1LastSpeed;
        float j1Accel = (joint1Torque - dampingForce1) / inertia1;
        Joint1Jerk = (j1Accel - joint1LastAccel) / dt;
        Joint1Acceleration = j1Accel;

        // Joint 1: 角加速度を積分して角速度を更新
        float joint1Speed = joint1LastSpeed + j1Accel * dt;

        // 速度が閾値以下になったら完全停止（微小振動を防ぐ）
        if (Mathf.Abs(joint1Speed) < velocityThreshold && Mathf.Abs(joint1Torque) < 0.1f)
        {
            joint1Speed = 0f;
        }

        joint1LastSpeed = joint1Speed;
        joint1LastAccel = j1Accel;

        // Joint 2: 粘性抵抗を含む角加速度を計算
        float dampingForce2 = dampingCoefficient * joint2LastSpeed;
        float j2Accel = (joint2Torque - dampingForce2) / inertia2;
        Joint2Jerk = (j2Accel - joint2LastAccel) / dt;
        Joint2Acceleration = j2Accel;

        // Joint 2: 角加速度を積分して角速度を更新
        float joint2Speed = joint2LastSpeed + j2Accel * dt;

        // 速度が閾値以下になったら完全停止
        if (Mathf.Abs(joint2Speed) < velocityThreshold && Mathf.Abs(joint2Torque) < 0.1f)
        {
            joint2Speed = 0f;
        }

        joint2LastSpeed = joint2Speed;
        joint2LastAccel = j2Accel;

        // 角速度を積分して角度を更新
        joint1TargetAngle += joint1Speed * dt;
        joint2TargetAngle += joint2Speed * dt;

        // Clamp angles to limits
        joint1TargetAngle = Mathf.Clamp(joint1TargetAngle, joint1MinAngle, joint1MaxAngle);
        joint2TargetAngle = Mathf.Clamp(joint2TargetAngle, joint2MinAngle, joint2MaxAngle);
    }

    /// <summary>
    /// Calculate torque from EMG ratio difference
    /// </summary>
    float CalculateTorque(float bendRatio, float extendRatio)
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

        // Convert percentage difference to torque
        // 100% difference = maxRotationSpeed (使用を最大トルクとして流用)
        float torqueRatio = difference / 100f;
        float torque = torqueRatio * maxRotationSpeed * speedMultiplier;

        return torque;
    }

    /// <summary>
    /// Get EMG ratio for a specific channel (0-100%)
    /// </summary>
    float GetChannelRatio(int channel)
    {
        if (channel < 1 || channel > 4) return 0f;
        return currentEMGRatios[channel - 1]; // Convert to 0-based index
    }

    void ApplyIndependentJointRotations()
    {
        // Forward Kinematics without parent-child hierarchy
        // Calculate all positions from base using link lengths
        // 角度0°の時は一直線に整列

        if (!isInitialized || joint1 == null || joint2 == null)
            return;

        // ベースの向き（デフォルトはY軸上向き）
        Vector3 upDirection = baseRotation * Vector3.up;

        // === Joint1 (Pivot1) の位置と回転 ===
        // ベースからbaseToJoint1Length分上に配置
        Vector3 joint1Position = basePosition + upDirection * baseToJoint1Length;

        // Joint1の回転を適用
        Quaternion joint1Rotation = Quaternion.AngleAxis(joint1TargetAngle, joint1RotationAxis);
        Quaternion joint1WorldRotation = baseRotation * joint1Rotation;

        joint1.position = joint1Position;
        joint1.rotation = joint1WorldRotation;

        // === Link1 (Cylinder2) の位置と回転 ===
        // Joint1からjoint1ToJoint2Lengthの半分の位置（リンクの中心）
        if (link1 != null)
        {
            Vector3 link1Direction = joint1WorldRotation * Vector3.up;
            Vector3 link1Position = joint1Position + link1Direction * (joint1ToJoint2Length * 0.5f);

            link1.position = link1Position;
            link1.rotation = joint1WorldRotation;
        }

        // === Joint2 (Pivot2) の位置と回転 ===
        // Joint1からjoint1ToJoint2Length分進んだ位置
        Vector3 joint1ToJoint2Direction = joint1WorldRotation * Vector3.up;
        Vector3 joint2Position = joint1Position + joint1ToJoint2Direction * joint1ToJoint2Length;

        // Joint2の回転を適用（Joint1の回転に加えて）
        Quaternion joint2Rotation = Quaternion.AngleAxis(joint2TargetAngle, joint2RotationAxis);
        Quaternion joint2WorldRotation = joint1WorldRotation * joint2Rotation;

        joint2.position = joint2Position;
        joint2.rotation = joint2WorldRotation;

        // === Link2 (Cylinder3) の位置と回転 ===
        // Joint2からlink2Lengthの半分の位置（リンクの中心）
        if (link2 != null)
        {
            Vector3 link2Direction = joint2WorldRotation * Vector3.up;
            Vector3 link2Position = joint2Position + link2Direction * (link2Length * 0.5f);

            link2.position = link2Position;
            link2.rotation = joint2WorldRotation;
        }

        // === End Effector (Ball) の位置 ===
        // Joint2からlink2Length分進んだ位置（Cylinder3の上端）
        if (endEffector != null)
        {
            Vector3 joint2ToEndDirection = joint2WorldRotation * Vector3.up;
            // Ballの中心をCylinder3の上端に配置（くっついて見える）
            Vector3 endPosition = joint2Position + joint2ToEndDirection * link2Length;

            endEffector.position = endPosition;
        }
    }

    void LogControlValues()
    {
        float j1BendRatio = GetChannelRatio(joint1BendChannel);
        float j1ExtendRatio = GetChannelRatio(joint1ExtendChannel);
        float j1Torque = CalculateTorque(j1BendRatio, j1ExtendRatio);

        float j2BendRatio = GetChannelRatio(joint2BendChannel);
        float j2ExtendRatio = GetChannelRatio(joint2ExtendChannel);
        float j2Torque = CalculateTorque(j2BendRatio, j2ExtendRatio);

        Debug.Log($"EMG Independent Control - J1: {joint1TargetAngle:F1}° (torque:{j1Torque:F1}, speed:{joint1LastSpeed:F1}°/s, accel:{Joint1Acceleration:F1}°/s²) | " +
                  $"J2: {joint2TargetAngle:F1}° (torque:{j2Torque:F1}, speed:{joint2LastSpeed:F1}°/s, accel:{Joint2Acceleration:F1}°/s²) | " +
                  $"Ch{joint1BendChannel}/Ch{joint1ExtendChannel}: {j1BendRatio:F1}%/{j1ExtendRatio:F1}% | " +
                  $"Ch{joint2BendChannel}/Ch{joint2ExtendChannel}: {j2BendRatio:F1}%/{j2ExtendRatio:F1}%");
    }

    // Public methods for external access
    public void ResetJointPositions()
    {
        joint1TargetAngle = 0f;
        joint2TargetAngle = 0f;
        joint1LastSpeed = 0f;
        joint1LastAccel = 0f;
        joint2LastSpeed = 0f;
        joint2LastAccel = 0f;
        Joint1Jerk = 0f;
        Joint2Jerk = 0f;
        // Reset manual angles if in manual mode
        if (controlMode == ControlMode.Manual)
        {
            manualJoint1Angle = 0f;
            manualJoint2Angle = 0f;
        }

        // Apply FK to reset all positions
        ApplyIndependentJointRotations();

        Debug.Log("IndependentEMGJointController: Joint positions reset to initial state");
    }

    public void SetManualAngles(float joint1Angle, float joint2Angle)
    {
        if (controlMode == ControlMode.Manual)
        {
            manualJoint1Angle = Mathf.Clamp(joint1Angle, joint1MinAngle, joint1MaxAngle);
            manualJoint2Angle = Mathf.Clamp(joint2Angle, joint2MinAngle, joint2MaxAngle);
            Debug.Log($"Manual angles set: Joint1={manualJoint1Angle:F1}°, Joint2={manualJoint2Angle:F1}°");
        }
        else
        {
            Debug.LogWarning("SetManualAngles: Controller is not in Manual mode");
        }
    }

    public Vector2 GetJointAngles()
    {
        return new Vector2(joint1TargetAngle, joint2TargetAngle);
    }

    /// <summary>
    /// Freeze arm at current position (for marker task)
    /// </summary>
    public void FreezeArm()
    {
        isFrozen = true;
        frozenJoint1Angle = joint1TargetAngle;
        frozenJoint2Angle = joint2TargetAngle;
        Debug.Log($"Arm frozen at Joint1={frozenJoint1Angle:F1}°, Joint2={frozenJoint2Angle:F1}°");
    }

    /// <summary>
    /// Unfreeze arm and resume control
    /// </summary>
    public void UnfreezeArm()
    {
        isFrozen = false;
        Debug.Log("Arm unfrozen - control resumed");
    }

    /// <summary>
    /// Check if arm is currently frozen
    /// </summary>
    public bool IsFrozen()
    {
        return isFrozen;
    }

    public float GetCurrentEMGRatio(int channel)
    {
        return GetChannelRatio(channel);
    }

    /// <summary>
    /// Set the rotation axes for each joint
    /// </summary>
    public void SetJointRotationAxes(Vector3 joint1Axis, Vector3 joint2Axis)
    {
        joint1RotationAxis = joint1Axis.normalized;
        joint2RotationAxis = joint2Axis.normalized;
        Debug.Log($"Joint rotation axes updated - J1: {joint1RotationAxis}, J2: {joint2RotationAxis}");
    }

    /// <summary>
    /// Update base position and rotation for reference
    /// </summary>
    public void UpdateInitialRotations()
    {
        if (baseObject != null)
        {
            basePosition = baseObject.position;
            baseRotation = baseObject.rotation;
        }
        else
        {
            basePosition = Vector3.zero;
            baseRotation = Quaternion.identity;
        }

        Debug.Log("Base position and rotation updated");
    }

    void OnGUI()
    {
        if (!showDebugGUI || !Application.isPlaying) return;

        // Position debug window
        GUILayout.BeginArea(new Rect(420, 10, 450, 500));

        // Header
        GUILayout.Label($"Control Mode: {controlMode}");
        GUILayout.Label($"Updates: {updateCount}");

        GUILayout.Space(350);

        // Control buttons
        if (GUILayout.Button("Reset Joint Positions"))
        {
            ResetJointPositions();
        }

        if (GUILayout.Button("Update Initial Rotations"))
        {
            UpdateInitialRotations();
        }

        GUILayout.EndArea();
    }

    void OnValidate()
    {
        // Ensure channel indices are within valid range (BiTalino Ch1-4)
        joint1BendChannel = Mathf.Clamp(joint1BendChannel, 1, 4);
        joint1ExtendChannel = Mathf.Clamp(joint1ExtendChannel, 1, 4);
        joint2BendChannel = Mathf.Clamp(joint2BendChannel, 1, 4);
        joint2ExtendChannel = Mathf.Clamp(joint2ExtendChannel, 1, 4);

        // Normalize rotation axes
        joint1RotationAxis = joint1RotationAxis.normalized;
        joint2RotationAxis = joint2RotationAxis.normalized;
    }
}
