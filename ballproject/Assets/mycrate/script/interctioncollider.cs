using System;
using OculusSampleFramework;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class InterctionCollider : MonoBehaviour
{
   [SerializeField] private ButtonController _resetButton;
   private Rigidbody  _rigidBody;
   private Vector3    _initPosition;
   private Quaternion _initRotation;
   private Vector3 lastHandPosition;
   private Vector3 handVelocity;
   private Transform trackingHand;
   private Vector3 newPosition;
   private float lastGrabTime = -999f;
   private const float grabCooldown = 0.3f; // ワープ後に再判定させない時間

   /// <summary>
   /// rigidbodyの取得で位置回転を記録、角速度と貫通解除速度を制限、初期速度のリセット(resetVelocity)
   /// </summary>
   void Start()
   {
       _rigidBody = GetComponent<Rigidbody>();
       _initPosition = this.transform.position;
       _initRotation = this.transform.rotation;
       _rigidBody.maxAngularVelocity = 0.5f;
       _rigidBody.maxDepenetrationVelocity = 0.5f;
       resetVelocity();


   }



    void Update()
    {
        if (trackingHand == null) return;
            handVelocity = (trackingHand.position - lastHandPosition) / Time.deltaTime;
            lastHandPosition = trackingHand.position;
    }


   /// <summary>
   /// 加速度初期化
   /// </summary>
   private void resetVelocity()
   {
       _rigidBody.velocity = Vector3.zero;
       _rigidBody.angularVelocity = Vector3.zero;
   }

/// <summary>
/// 5本の指先すべてがオブジェクトに接触しているかを判定し、
/// 接触していればその平均位置を返す
/// </summary>
private (bool isTouchingAllFingers, Vector3 averagePosition) getPinchPosition(OVRHand hand)
{
    OVRSkeleton skeleton = hand.GetComponent<OVRSkeleton>();
    if (skeleton == null || !skeleton.IsDataValid) return (false, Vector3.zero);

    var bones = skeleton.Bones;
    if (bones.Count == 0) return (false, Vector3.zero);

    Vector3 thumbTip    = bones[(int)OVRSkeleton.BoneId.Hand_ThumbTip].Transform.position;
    Vector3 indexTip    = bones[(int)OVRSkeleton.BoneId.Hand_IndexTip].Transform.position;
    Vector3 middleTip   = bones[(int)OVRSkeleton.BoneId.Hand_MiddleTip].Transform.position;
    Vector3 ringTip     = bones[(int)OVRSkeleton.BoneId.Hand_RingTip].Transform.position;
    Vector3 pinkyTip    = bones[(int)OVRSkeleton.BoneId.Hand_PinkyTip].Transform.position;
    Vector3 thumbDistal = bones[(int)OVRPlugin.BoneId.Hand_Thumb3].Transform.position;
    Vector3 pinkyDistal = bones[(int)OVRPlugin.BoneId.Hand_Pinky3].Transform.position;
    Vector3 wristroot   = bones[(int)OVRPlugin.BoneId.Hand_WristRoot].Transform.position;

    Collider col = GetComponent<Collider>();
    float threshold = 0.025f;

    bool isThumbTouching  = Vector3.Distance(thumbTip,  col.ClosestPoint(thumbTip))  < threshold;
    bool isIndexTouching  = Vector3.Distance(indexTip,  col.ClosestPoint(indexTip))  < threshold;
    bool isMiddleTouching = Vector3.Distance(middleTip, col.ClosestPoint(middleTip)) < threshold;
    bool isRingTouching   = Vector3.Distance(ringTip,   col.ClosestPoint(ringTip))   < threshold;
    bool isPinkyTouching  = Vector3.Distance(pinkyTip,  col.ClosestPoint(pinkyTip))  < threshold;

    bool allTouching = isThumbTouching && isIndexTouching && isMiddleTouching && isRingTouching && isPinkyTouching;

    if (allTouching)
    {
        Vector3 average = (thumbTip + indexTip + middleTip + ringTip + pinkyTip) / 5f;
        return (true, average);
    }

    return (false, Vector3.zero);
}



   /// <summary>
   /// 当たった方の手の取得。右手か左手か
   /// </summary>
   /// <param name="other"></param>
   /// <returns></returns>
   private (OVRHand hand , string handName) getCollisionHand(Collision other)
   {
       try
       {
           //親子関係 OVRHandPrefab/Capsules/Hand_Index1_***
           GameObject targetObject = other.transform.parent.parent.gameObject;
           OVRHand rightHand = HandsManager.Instance.RightHand;
           OVRHand leftHand  = HandsManager.Instance.LeftHand;
           if(targetObject.Equals(leftHand.gameObject))  return (leftHand, "LeftHand");
           if(targetObject.Equals(rightHand.gameObject)) return (rightHand,"RightHand");
           return (null,"None");
       }
       catch(Exception)
       {
           //parentが無かった時のエラーをキャッチ
           return (null, "None");
       }
   }

   /// <summary>
   /// 触れた時　ピンチ中にオブジェクトに触れると重力off+回転固定で手の位置にワープさせる
   /// </summary>
   /// <param name="other"></param>
private void OnCollisionEnter(Collision other)
{
    if (Time.time - lastGrabTime < grabCooldown) return;

    var collisionHand = getCollisionHand(other);
    if (collisionHand.hand == null) return;

    if (!PinchManager.IsHandFree(collisionHand.handName)) return;

    var result = getPinchPosition(collisionHand.hand);
    if (!result.isTouchingAllFingers) return;

    trackingHand = collisionHand.hand.transform;
    lastHandPosition = trackingHand.position;

    _rigidBody.useGravity = false;
    _rigidBody.isKinematic = false; 

    lastGrabTime = Time.time;

    PinchManager.GrabObject(collisionHand.handName, this.gameObject);
    GetComponent<ReleaseWithPhysics>()?.SetTrackingHand(collisionHand.hand);

    Debug.Log("catch");
}



   /// <summary>
   /// 触れている間　ピンチ中は速度リセットで手の位置を追従
   /// </summary>
   /// <param name="other"></param>
private void OnCollisionStay(Collision other)
{
    var collisionHand = getCollisionHand(other);
    if (collisionHand.hand == null) return;

    var result = getPinchPosition(collisionHand.hand);

    if (result.isTouchingAllFingers && PinchManager.IsHandFree(collisionHand.handName))
    {
        // 再登録
        PinchManager.GrabObject(collisionHand.handName, this.gameObject);

        // ↓ 滑らかに追従（velocity を使う方式）
        Vector3 targetPos = result.averagePosition;
        Vector3 currentPos = _rigidBody.position;
        Vector3 desiredVelocity = (targetPos - currentPos) / Time.fixedDeltaTime;


// 手のひらの回転を取得
Quaternion handRotation = collisionHand.hand.transform.rotation;

// 物体の回転を手のひらにスムーズに合わせる（補間）
Quaternion currentRotation = _rigidBody.rotation;
Quaternion targetRotation = handRotation;

// 補間係数（回転のスムーズさ）
float rotationLerpSpeed = 10f;

// Rigidbodyを直接回転させるには MoveRotation を使う
_rigidBody.MoveRotation(Quaternion.Slerp(currentRotation, targetRotation, Time.deltaTime * rotationLerpSpeed));



        _rigidBody.velocity = desiredVelocity;

        _rigidBody.useGravity = false;
    }
    else if (!result.isTouchingAllFingers)
    {
        _rigidBody.useGravity = true;
        _rigidBody.freezeRotation = false;

        // 登録解除
        PinchManager.ReleaseObject(collisionHand.handName, this.gameObject);
    }
}




   /// <summary>
   /// 離れた時　オブジェクトの重力をon+回転固定を解除
   /// </summary>
   /// <param name="other"></param>
   private void OnCollisionExit(Collision other)
   {
       var collisionHand = getCollisionHand(other);
       if (collisionHand.hand == null) return;

       //速度をオブジェクトに与える
       _rigidBody.velocity = handVelocity;

       _rigidBody.useGravity = true;
       _rigidBody.isKinematic = false;
       _rigidBody.freezeRotation = false;
       
       trackingHand = null;


       //オブジェクトを離したので解除
       PinchManager.ReleaseObject(collisionHand.handName, this.gameObject);
       GetComponent<ReleaseWithPhysics>()?.SetTrackingHand(collisionHand.hand);
   }
}

