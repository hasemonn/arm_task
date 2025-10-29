using UnityEngine;
using OculusSampleFramework;

[RequireComponent(typeof(Rigidbody))]
public class ReleaseWithPhysics : MonoBehaviour
{
    private OVRHand trackingHand;
    private Vector3 previousPosition;
    private Quaternion previousRotation;

    private Vector3 calculatedVelocity;
    private Vector3 calculatedAngularVelocity;

    private Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void SetTrackingHand(OVRHand hand)
    {
        trackingHand = hand;
        if (hand != null)
        {
            previousPosition = hand.transform.position;
            previousRotation = hand.transform.rotation;
        }
    }

    void Update()
    {
        if (trackingHand != null)
        {
            Vector3 currentPosition = trackingHand.transform.position;
            Quaternion currentRotation = trackingHand.transform.rotation;

            calculatedVelocity = (currentPosition - previousPosition) / Time.deltaTime;

            Quaternion deltaRotation = currentRotation * Quaternion.Inverse(previousRotation);
            deltaRotation.ToAngleAxis(out float angleInDegrees, out Vector3 rotationAxis);
            float angleInRadians = angleInDegrees * Mathf.Deg2Rad;
            calculatedAngularVelocity = (rotationAxis * angleInRadians) / Time.deltaTime;

            previousPosition = currentPosition;
            previousRotation = currentRotation;
        }
    }

    public void OnReleased()
    {
        if (rb == null) return;

        rb.velocity = calculatedVelocity;
        rb.angularVelocity = calculatedAngularVelocity;

        // トラッキング解除
        trackingHand = null;
    }
}