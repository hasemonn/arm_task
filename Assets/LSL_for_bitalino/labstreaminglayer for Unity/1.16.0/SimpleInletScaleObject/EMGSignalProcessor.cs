using System.Collections.Generic;
using UnityEngine;



namespace LSL4Unity.Samples.SimpleInlet
{
    /// <summary>
    /// EMG信号処理:RMS、閾値カット、正規化、平滑化
    /// キャリブレーションと測定モード付き
    /// </summary>
    public class EMGSignalProcessor : MonoBehaviour
    {

        [Header("モード変更")]
        public ProcessingMode mode = ProcessingMode.Measurement;

        [Header("RMS Window Settings")]
        [Tooltip("RMS計算用のサンプル数(例: 100サンプル = 0.1秒@1000Hz)")]
        public int rmsWindowSize = 100;

        [Header("Calibration Values (Read Only)")]
        [Tooltip("Ch1-4の最大RMS値")]
        public float[] maxRMS = new float[4];

        [Tooltip("Ch1-4のカット閾値")]
        public float[] thresholdRMS = new float[4];

        [Header("Smoothing Settings")]
        [Tooltip("平滑化フィルタのウィンドウサイズ")]
        public int smoothWindowSize = 10;

        [Header("Output (Read Only)")]
        [Tooltip("Ch1-4の生データ")]
        public float[] rawValues = new float[4];

        [Tooltip("Ch1-4のRMS値")]
        public float[] rmsValues = new float[4];

        [Tooltip("Ch1-4の閾値カット後RMS")]
        public float[] thresholdedRMS = new float[4];

        [Tooltip("Ch1-4の正規化値(0-100%)")]
        public float[] normalizedValues = new float[4];

        [Tooltip("Ch1-4の平滑化後の値(0-100%)")]
        public float[] smoothedValues = new float[4];

        [Header("References")]
        public EMGDebugDisplay emgSource;



        // 内部データ
        private Queue<float>[] dataBuffers = new Queue<float>[4];
        private Queue<float>[] smoothBuffers = new Queue<float>[4];

        public enum ProcessingMode
        {
            MaxCalibration,      // 最大値キャリブレーション
            ThresholdCalibration, // 閾値キャリブレーション
            Measurement          // 測定モード
        }

        void Start()
        {
            // バッファ初期化
            for (int i = 0; i < 4; i++)
            {
                dataBuffers[i] = new Queue<float>();
                smoothBuffers[i] = new Queue<float>();
            }

            // EMGDebugDisplayを自動検索
            if (emgSource == null)
            {
                emgSource = FindObjectOfType<EMGDebugDisplay>();
                if (emgSource == null)
                {
                    Debug.LogError("[EMGSignalProcessor] EMGDebugDisplay not found!");
                    enabled = false;
                }
            }
            Debug.Log($"[EMGSignalProcessor] Started in {mode} mode");
        }



        void Update()
        {
            if (emgSource == null)
            {
                Debug.LogWarning("[EMGSignalProcessor] emgSource is null!");
                return;
            }

            if (!emgSource.IsConnected())
            {
                Debug.LogWarning("[EMGSignalProcessor] emgSource is not connected!");
                return;
            }

            // 生データ取得(Ch1-4)
            for (int ch = 1; ch <= 4; ch++)
            {
                rawValues[ch - 1] = emgSource.GetChannelValue(ch);
            }

            // デバッグ: 最初の100フレームでrawValuesをログ出力
            if (Time.frameCount <= 100 && Time.frameCount % 10 == 0)
            {
                Debug.Log($"[EMGSignalProcessor Frame {Time.frameCount}] rawValues: [{rawValues[0]:F6}, {rawValues[1]:F6}, {rawValues[2]:F6}, {rawValues[3]:F6}]");
            }

            // 各チャンネルを処理
            for (int i = 0; i < 4; i++)
            {
                ProcessChannel(i);
            }
        }

        void ProcessChannel(int channelIndex)
        {
            float rawValue = rawValues[channelIndex];

            // 1. スライディングウィンドウに追加
            dataBuffers[channelIndex].Enqueue(rawValue);
            if (dataBuffers[channelIndex].Count > rmsWindowSize)
            {
                dataBuffers[channelIndex].Dequeue();

            }

            // 2. RMS計算
            float rms = CalculateRMS(dataBuffers[channelIndex]);
            rmsValues[channelIndex] = rms;

            // モード別処理
            switch (mode)
            {
                case ProcessingMode.MaxCalibration:
                    // チャンネル毎の最大値を更新
                    if (rms > maxRMS[channelIndex])
                    {
                        maxRMS[channelIndex] = rms;
                        Debug.Log($"[MaxCalibration] Ch{channelIndex + 1}: New maxRMS = {maxRMS[channelIndex]:F4}");
                    }
                    break;

                case ProcessingMode.ThresholdCalibration:
                    // チャンネル毎の閾値を更新
                    if (rms > thresholdRMS[channelIndex])
                    {
                        thresholdRMS[channelIndex] = rms;
                        Debug.Log($"[ThresholdCalibration] Ch{channelIndex + 1}: New thresholdRMS = {thresholdRMS[channelIndex]:F4}");
                    }
                    break;


                case ProcessingMode.Measurement:
                    // 3. 閾値カット(チャンネル毎)
                    float thresholded = (rms <= thresholdRMS[channelIndex]) ? 0f : rms;
                    thresholdedRMS[channelIndex] = thresholded;


                    // 4. 正規化(0-100%)チャンネル毎
                    float normalized = 0f;
                    if (maxRMS[channelIndex] > thresholdRMS[channelIndex] && thresholded > 0f)
                    {
                        normalized = Mathf.Clamp01((thresholded - thresholdRMS[channelIndex]) / (maxRMS[channelIndex] - thresholdRMS[channelIndex])) * 100f;
                    }
                    normalizedValues[channelIndex] = normalized;


                    // 5. 平滑化
                    smoothBuffers[channelIndex].Enqueue(normalized);
                    if (smoothBuffers[channelIndex].Count > smoothWindowSize)
                    {
                        smoothBuffers[channelIndex].Dequeue();
                    }
                    smoothedValues[channelIndex] = CalculateAverage(smoothBuffers[channelIndex]);
                    break;

            }

        }



        float CalculateRMS(Queue<float> buffer)
        {
            if (buffer.Count == 0)
                return 0f;

            float sum = 0f;

            foreach (float value in buffer)
            {
                sum += value * value;
            }
            return Mathf.Sqrt(sum / buffer.Count);
        }



        float CalculateAverage(Queue<float> buffer)
        {
            if (buffer.Count == 0)
                return 0f;


            float sum = 0f;
            foreach (float value in buffer)
            {
                sum += value;
            }
            return sum / buffer.Count;
        }



        // Public API
        public float GetSmoothedValue(int channel)
        {
            if (channel >= 1 && channel <= 4)
                return smoothedValues[channel - 1];
            return 0f;
        }


        public float GetNormalizedValue(int channel)
        {
            if (channel >= 1 && channel <= 4)
                return normalizedValues[channel - 1];
            return 0f;
        }


        public float GetRMSValue(int channel)
        {
            if (channel >= 1 && channel <= 4)
                return rmsValues[channel - 1];
            return 0f;
        }



        // キャリブレーション値をリセット
        [ContextMenu("Reset Calibration")]
        public void ResetCalibration()
        {
            for (int i = 0; i < 4; i++)
            {
                maxRMS[i] = 0f;
                thresholdRMS[i] = 0f;
            }
            Debug.Log("[EMGSignalProcessor] Calibration values reset");
        }


        void OnGUI()
        {
            GUIStyle modeStyle = new GUIStyle(GUI.skin.label);
            modeStyle.fontSize = 20;
            modeStyle.fontStyle = FontStyle.Bold;
            modeStyle.normal.textColor = GetModeColor();

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 14;
            labelStyle.normal.textColor = Color.white;

            float xPos = Screen.width - 300;
            float yPos = 20;

            // モード表示
            GUI.Label(new Rect(xPos, yPos, 280, 30), $"Mode: {mode}", modeStyle);
            yPos += 35;

            // キャリブレーション値表示(Ch1-4)
            GUI.Label(new Rect(xPos, yPos, 280, 25), "Max RMS:", labelStyle);
            yPos += 25;

            for (int i = 0; i < 4; i++)
            {
                GUI.Label(new Rect(xPos + 10, yPos, 270, 20), $"Ch{i + 1}: {maxRMS[i]:F4}", labelStyle);
                yPos += 20;
            }
            yPos += 10;


            GUI.Label(new Rect(xPos, yPos, 280, 25), "Threshold RMS:", labelStyle);
            yPos += 25;

            for (int i = 0; i < 4; i++)
            {
                GUI.Label(new Rect(xPos + 10, yPos, 270, 20), $"Ch{i + 1}: {thresholdRMS[i]:F4}", labelStyle);
                yPos += 20;
            }
            yPos += 10;


            // 測定モードの場合、処理済みデータを表示
            if (mode == ProcessingMode.Measurement)
            {
                GUI.Label(new Rect(xPos, yPos, 280, 25), "Smoothed (%):", labelStyle);
                yPos += 25;
                for (int i = 0; i < 4; i++)
                {
                    string channelInfo = $"Ch{i + 1}: {smoothedValues[i]:F1}%";
                    GUI.Label(new Rect(xPos + 10, yPos, 270, 20), channelInfo, labelStyle);
                    yPos += 20;
                }
            }
        }


        Color GetModeColor()
        {
            switch (mode)
            {
                case ProcessingMode.MaxCalibration:
                    return Color.red;
                case ProcessingMode.ThresholdCalibration:
                    return Color.yellow;
                case ProcessingMode.Measurement:
                    return Color.green;
                default:
                    return Color.white;
            }
        }
    }
}
