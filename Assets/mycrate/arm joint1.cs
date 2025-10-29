using UnityEngine;

public class JointAngleController : MonoBehaviour
{
    [SerializeField, Range(0f, 1f)]
    public float sliderValue = 0f; // Inspector

    void Update()
    {
       
        float angle = Mathf.Lerp(0f, 140f, sliderValue);

        // xrotate
        transform.localRotation = Quaternion.Euler(angle, 0f, 0f);

    }
}
