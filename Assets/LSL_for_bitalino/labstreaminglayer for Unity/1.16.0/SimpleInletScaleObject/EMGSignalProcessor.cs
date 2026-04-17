using System.Collections.Generic;
using UnityEngine;



namespace LSL4Unity.Samples.SimpleInlet
{
    /// <summary>
    /// EMG信号処理:iEMG（積分EMG）、閾値カット、正規化、平滑化
    /// キャリブレーションと測定モード付き
    /// </summary>
    public class EMGSignalProcessor : MonoBehaviour
    {

        [Header("モード変更")]
        public ProcessingMode mode = ProcessingMode.Measurement;

        [Header("iEMG Window Settings")]
        [Tooltip("iEMG計算用のサンプル数(例: 100サンプル = 0.1秒@1000Hz)")]
        public int iemgWindowSize = 100;

        [Header("iEMG Scaling")]
        [Tooltip("積分値のスケーリングファクター（デフォルト: 0.001）")]
        public float iemgScalingFactor = 0.001f;

        [Header("Calibration Values")]
        [Tooltip("Ch1-4の最大iEMG値（MVC: 最大随意収縮時）\nMaxCalibrationモードで自動取得 or インスペクターで手動設定")]
        public float[] maxIEMG = new float[4];

        [Tooltip("Ch1-4の閾値iEMG値（安静時ベースライン）\nThresholdCalibrationモードで自動取得 or インスペクターで手動設定")]
        public float[] thresholdIEMG = new float[4];

        [Header("Smoothing Settings")]
        [Tooltip("平滑化フィルタのウィンドウサイズ")]
        public int smoothWindowSize = 10;

        [Header("Output (Read Only)")]
        [Tooltip("Ch1-4の生データ")]
        public float[] rawValues = new float[4];

        [Tooltip("Ch1-4のiEMG値")]
        public float[] iemgValues = new float[4];

        [Tooltip("Ch1-4の閾値カット後iEMG")]
        public float[] thresholdedIEMG = new float[4];

        [Tooltip("Ch1-4の正規化値(0-100%)")]
        public float[] normalizedValues = new float[4];

        [Tooltip("Ch1-4の平滑化後の値(0-100%)")]
        public float[] smoothedValues = new float[4];

        [Header("Accumulated iEMG (Read Only)")]
        [Tooltip("Ch1-4の蓄積されたiEMG値")]
        public float[] accumulatedIEMG = new float[4];

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
        }



        void Update()
        {

            // 生データ取得(Ch1-4)
            for (int ch = 1; ch <= 4; ch++)
            {
                rawValues[ch - 1] = emgSource.GetChannelValue(ch);
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
            if (dataBuffers[channelIndex].Count > iemgWindowSize)
            {
                dataBuffers[channelIndex].Dequeue();
            }

            // 2. iEMG計算（積分EMG）
            float iemg = CalculateIEMG(dataBuffers[channelIndex]);
            iemgValues[channelIndex] = iemg;

            // 3. スケーリング適用（0.001倍で小さくする）
            float scaledIEMG = iemg * iemgScalingFactor;

            // 4. 蓄積（積分...で蓄積）
            accumulatedIEMG[channelIndex] += scaledIEMG;

            // モード別処理
            // 注意: キャリブレーション値(maxIEMG, thresholdIEMG)は各モードでのみ更新される
            // Measurementモードではキャリブレーション値は固定され、インスペクターで手動変更可能
            switch (mode)
            {
                case ProcessingMode.MaxCalibration:
                    // 最大随意収縮(MVC)時のiEMG値を記録
                    // maxIEMGのみ更新、thresholdIEMGは更新しない
                    if (iemg > maxIEMG[channelIndex])
                    {
                        maxIEMG[channelIndex] = iemg;
                    }
                    break;

                case ProcessingMode.ThresholdCalibration:
                    // 安静時（平常状態）のiEMG値を記録
                    // thresholdIEMGのみ更新、maxIEMGは更新しない
                    if (iemg > thresholdIEMG[channelIndex])
                    {
                        thresholdIEMG[channelIndex] = iemg;
                    }
                    break;

                case ProcessingMode.Measurement:
                    // 測定モード: maxIEMGとthresholdIEMGは固定値として使用
                    // インスペクターで手動変更した値もここで反映される

                    // 5. 閾値カット(チャンネル毎)
                    float thresholded = (iemg <= thresholdIEMG[channelIndex]) ? 0f : iemg;
                    thresholdedIEMG[channelIndex] = thresholded;

                    // 6. 正規化(0-100%)チャンネル毎
                    float normalized = 0f;
                    if (maxIEMG[channelIndex] > thresholdIEMG[channelIndex] && thresholded > 0f)
                    {
                        normalized = Mathf.Clamp01((thresholded - thresholdIEMG[channelIndex]) / (maxIEMG[channelIndex] - thresholdIEMG[channelIndex])) * 100f;
                    }
                    normalizedValues[channelIndex] = normalized;

                    // 7. 平滑化
                    smoothBuffers[channelIndex].Enqueue(normalized);
                    if (smoothBuffers[channelIndex].Count > smoothWindowSize)
                    {
                        smoothBuffers[channelIndex].Dequeue();
                    }
                    smoothedValues[channelIndex] = CalculateAverage(smoothBuffers[channelIndex]);
                    break;
            }
        }



        float CalculateIEMG(Queue<float> buffer)
        {
            if (buffer.Count == 0)
                return 0f;

            float sum = 0f;

            // 絶対値の積分（平均）
            foreach (float value in buffer)
            {
                sum += Mathf.Abs(value);
            }
            return sum / buffer.Count;
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


        public float GetIEMGValue(int channel)
        {
            if (channel >= 1 && channel <= 4)
                return iemgValues[channel - 1];
            return 0f;
        }

        public float GetAccumulatedIEMG(int channel)
        {
            if (channel >= 1 && channel <= 4)
                return accumulatedIEMG[channel - 1];
            return 0f;
        }

        public void ResetAccumulatedIEMG()
        {
            for (int i = 0; i < 4; i++)
            {
                accumulatedIEMG[i] = 0f;
            }
        }



        // キャリブレーション値をリセット
        [ContextMenu("Reset Calibration")]
        public void ResetCalibration()
        {
            for (int i = 0; i < 4; i++)
            {
                maxIEMG[i] = 0f;
                thresholdIEMG[i] = 0f;
            }
        }

        // 蓄積値をリセット
        [ContextMenu("Reset Accumulated iEMG")]
        public void ResetAccumulated()
        {
            ResetAccumulatedIEMG();
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
            GUIStyle maxStyle = new GUIStyle(labelStyle);
            GUIStyle thresholdStyle = new GUIStyle(labelStyle);

            // 現在更新中の値をハイライト
            if (mode == ProcessingMode.MaxCalibration)
                maxStyle.normal.textColor = Color.yellow;
            if (mode == ProcessingMode.ThresholdCalibration)
                thresholdStyle.normal.textColor = Color.yellow;

            GUI.Label(new Rect(xPos, yPos, 280, 25), "Max iEMG (MVC):", maxStyle);
            yPos += 25;

            for (int i = 0; i < 4; i++)
            {
                GUI.Label(new Rect(xPos + 10, yPos, 270, 20), $"Ch{i + 1}: {maxIEMG[i]:F4}", maxStyle);
                yPos += 20;
            }
            yPos += 10;

            GUI.Label(new Rect(xPos, yPos, 280, 25), "Threshold iEMG (Rest):", thresholdStyle);
            yPos += 25;

            for (int i = 0; i < 4; i++)
            {
                GUI.Label(new Rect(xPos + 10, yPos, 270, 20), $"Ch{i + 1}: {thresholdIEMG[i]:F4}", thresholdStyle);
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
                yPos += 10;

                // 蓄積iEMG値を表示
                GUI.Label(new Rect(xPos, yPos, 280, 25), "Accumulated iEMG:", labelStyle);
                yPos += 25;
                for (int i = 0; i < 4; i++)
                {
                    GUI.Label(new Rect(xPos + 10, yPos, 270, 20), $"Ch{i + 1}: {accumulatedIEMG[i]:F6}", labelStyle);
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
