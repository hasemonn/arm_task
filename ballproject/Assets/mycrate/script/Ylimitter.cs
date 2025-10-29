using UnityEngine;

public class YAxisLimiter : MonoBehaviour
{
    // Y軸の最小値と最大値を設定
    public float minY = 0f;
    public float maxY = 1f;

    void Update()
    {
        // 現在の位置を取得
        Vector3 position = transform.position;

        // Y軸を制限
        position.y = Mathf.Clamp(position.y, minY, maxY);

        // 新しい位置を設定
        transform.position = position;
    }
}
