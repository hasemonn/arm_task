using UnityEngine;

public class HandButton : MonoBehaviour
{
    public float pressThreshold = 0.02f;
    public float InitialPositionDeviation = 0f;
    public Transform button_Top;
    public Transform button_Base;

    private Vector3 startLocalPos;
    private bool isPressed = false;

    public reset[] cubesToReset;

    void Start()
    {
        startLocalPos = button_Top.localPosition;
        startLocalPos.y += + InitialPositionDeviation;
        Debug.Log("Start Local Position: " + startLocalPos);
    }

    void Update()
    {
        float pressDepth = button_Base.InverseTransformPoint(button_Top.position).y;
        float distance = startLocalPos.y - pressDepth;

        if (!isPressed && distance > pressThreshold)
        {
            isPressed = true;
            Debug.Log("Button Pressed!");
            OnPress();
        }

        if (isPressed && distance < pressThreshold * 0.5f)
        {
            isPressed = false;
        }
    }

    void OnPress()
    {
        foreach (var cube in cubesToReset)
        {
            cube.ResetPosition();
        }
    }
}
