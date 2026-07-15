using System.Collections.Generic;
using UnityEngine;



namespace LSL4Unity.Samples.SimpleInlet
{
    /// <summary>
    /// EMG信号処理:RMS、閾値カット、正規化、平滑化
    /// キャリブレーションと測定モード付き
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class EMGSignalProcessor : MonoBehaviour
    {

        [Header("モード変更")]
        public ProcessingMode mode = ProcessingMode.Measurement;

        [Header("RMS Window Settings")]
        [Tooltip("RMS計算用のサンプル数(例: 50サンプル = 0.05秒@1000Hz)")]
        public int rmsWindowSize = 50;

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

        [Header("Debug Display")]
        public bool showDebugGUI = true;

        [Header("References")]
        public EMGDebugDisplay emgSource;



        // 内部データ
        private Queue<float>[] dataBuffers = new Queue<float>[4];
        private Queue<float>[] smoothBuffers = new Queue<float>[4];

        // 直近フレームで処理した全サンプル（1000Hz記録用）
        public struct EMGProcessedSample
        {
            public double timestamp;
            public float raw1, raw2, raw3, raw4;
            public float filtered1, filtered2, filtered3, filtered4;
            public float normalized1, normalized2, normalized3, normalized4;
        }

        private List<EMGProcessedSample> frameSamples = new List<EMGProcessedSample>();
        public IReadOnlyList<EMGProcessedSample> FrameSamples => frameSamples;

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
        }



        void Update()
        {
            // 直近チャンクの全サンプルを1000Hzで処理する
            frameSamples.Clear();

            int count = emgSource.LastChunkCount;
            for (int s = 0; s < count; s++)
            {
                // 生データ取得(Ch1-4) - チャンク内サンプルsの値
                for (int ch = 1; ch <= 4; ch++)
                {
                    rawValues[ch - 1] = emgSource.GetChunkChannelValue(s, ch);
                }

                // 各チャンネルを処理（RMSスライディング窓・閾値・正規化・平滑化がサンプル単位で進む）
                for (int i = 0; i < 4; i++)
                {
                    ProcessChannel(i);
                }

                // このサンプルの処理結果を記録用に保持
                frameSamples.Add(new EMGProcessedSample
                {
                    timestamp = emgSource.GetChunkTimestamp(s),
                    raw1 = rawValues[0], raw2 = rawValues[1], raw3 = rawValues[2], raw4 = rawValues[3],
                    filtered1 = thresholdedRMS[0], filtered2 = thresholdedRMS[1], filtered3 = thresholdedRMS[2], filtered4 = thresholdedRMS[3],
                    normalized1 = smoothedValues[0], normalized2 = smoothedValues[1], normalized3 = smoothedValues[2], normalized4 = smoothedValues[3]
                });
            }

            // ループ後、公開配列は最後のサンプルの値を保持（制御・GUIは従来通り最新値を得る）
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
                    }
                    break;

                case ProcessingMode.ThresholdCalibration:
                    // チャンネル毎の閾値を更新
                    if (rms > thresholdRMS[channelIndex])
                    {
                        thresholdRMS[channelIndex] = rms;
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







        // キャリブレーション値をリセット
        [ContextMenu("Reset Calibration")]
        public void ResetCalibration()
        {
            for (int i = 0; i < 4; i++)
            {
                maxRMS[i] = 0f;
                thresholdRMS[i] = 0f;
            }
        }


        void OnGUI()
        {
            if (!showDebugGUI) return;

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
