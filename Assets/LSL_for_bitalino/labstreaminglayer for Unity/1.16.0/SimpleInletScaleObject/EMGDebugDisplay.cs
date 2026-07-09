using UnityEngine;
using System.Collections;
using LSL;

namespace LSL4Unity.Samples.SimpleInlet
{
    /// <summary>
    /// LSLからEMGデータを受信してConsoleとOnGUIで表示（3D環境対応）
    /// UI Textを使わないシンプル版
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class EMGDebugDisplay : MonoBehaviour
    {
        [Header("LSL Stream Settings")]
        public string StreamName = "OpenSignals";
        [Header("Display Settings")]
        public bool showOnGUI = true;
        public bool showConsoleLog = true;
        public int logInterval = 100;  // 何サンプルごとにログ出力

        [Header("Status (Read Only)")]
        public bool isSearching = false;
        public bool isConnected = false;
        public int channelCount = 0;
        public float samplingRate = 0;
        public int receivedSamples = 0;
        public string errorMessage = "";

        // LSL Components
        private ContinuousResolver resolver;
        private StreamInlet inlet;
        private float[,] data_buffer;
        private double[] timestamp_buffer;
        private double max_chunk_duration = 0.2;

        // Latest Data
        private float[] latestValues = new float[8];  // 最大8チャンネル

        // 直近フレームで pull_chunk が返したサンプル数（1000Hz全サンプル処理用）
        public int LastChunkCount { get; private set; }

        void Start()
        {
            if (string.IsNullOrEmpty(StreamName))
            {
                errorMessage = "StreamName is empty!";
                Debug.LogError(errorMessage);
                enabled = false;
                return;
            }

            isSearching = true;

            // 全ストリームを検索してデバッグ
            resolver = new ContinuousResolver();
            StartCoroutine(ResolveExpectedStream());
        }

        IEnumerator ResolveExpectedStream()
        {
            var results = resolver.results();

            while (results.Length == 0)
            {
                yield return new WaitForSeconds(0.1f);
                results = resolver.results();

                // タイムアウトを無効化（フリーズ原因のため）
                // ストリームが見つかるまで無限に待機
                // if (waitCount > 300)
                // {
                //     errorMessage = "Timeout: Stream not found after 30 seconds";
                //     Debug.LogError(errorMessage);
                //     isSearching = false;
                //     enabled = false;
                //     yield break;
                // }
            }

            isSearching = false;

            try
            {
                // StreamNameと一致するものを探す（nameまたはtype）
                StreamInfo targetStream = null;
                foreach (var stream in results)
                {
                    if (stream.name() == StreamName || stream.type() == StreamName)//stream.type() == bitalimoのdivicename
                    {
                        targetStream = stream;
                        break;
                    }
                }

                // 見つからなければ最初のストリームを使用
                if (targetStream == null)
                {
                    Debug.LogWarning($"[LSL] Stream '{StreamName}' not found by name or type. Using first available stream.");
                    targetStream = results[0];
                }

                inlet = new StreamInlet(targetStream);
                channelCount = inlet.info().channel_count();
                samplingRate = (float)inlet.info().nominal_srate();

                int buf_samples = (int)Mathf.Ceil((float)(samplingRate * max_chunk_duration));
                data_buffer = new float[buf_samples, channelCount];
                timestamp_buffer = new double[buf_samples];

                latestValues = new float[channelCount];

                isConnected = true;
                errorMessage = "";
            }
            catch (System.Exception e)
            {
                errorMessage = $"Connection error: {e.Message}";
                Debug.LogError($"[LSL] Error creating inlet: {e.Message}");
                Debug.LogError($"[LSL] Stack trace: {e.StackTrace}");
                isConnected = false;
                enabled = false;
            }
        }

        void Update()
        {
            if (!isConnected || inlet == null)
            {
                LastChunkCount = 0;
                return;
            }

            try
            {
                int samples_returned = inlet.pull_chunk(data_buffer, timestamp_buffer);
                LastChunkCount = (samples_returned > 0) ? samples_returned : 0;

                if (samples_returned > 0)// 受信できたかを判定
                {
                    // 最新サンプルを取得
                    int lastIndex = samples_returned - 1;

                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        latestValues[ch] = data_buffer[lastIndex, ch];
                    }

                    receivedSamples += samples_returned;
                }
            }
            catch (System.Exception e)
            {
                LastChunkCount = 0;
                errorMessage = $"Data reception error: {e.Message}";
                Debug.LogError($"[LSL] Error receiving data: {e.Message}");
                isConnected = false;
            }
        }

        void LogData()
        {
            string logMessage = $"[Sample #{receivedSamples}] ";
            // BITalino: latestValues[1]～[4]がA1～A4（EMG Ch1～4）
            for (int i = 1; i <= 4 && i < channelCount; i++)
            {
                logMessage += $"Ch{i}={latestValues[i]:F4} | ";
            }
            Debug.Log(logMessage);
        }

        void OnGUI()
        {
            if (!showOnGUI) return;

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 18;
            labelStyle.normal.textColor = Color.white;

            GUIStyle dataStyle = new GUIStyle(GUI.skin.label);
            dataStyle.fontSize = 16;
            dataStyle.normal.textColor = Color.yellow;

            float yPos = 20;

            // サンプル数表示
            if (isConnected)
            {
                GUI.Label(new Rect(20, yPos, 500, 30),
                          $"Samples: {receivedSamples}", labelStyle);
                yPos += 35;

                // チャンネルデータ表示（latestValues[1]～[4]をCh1～4として表示）
                for (int i = 1; i <= 4 && i < channelCount; i++)
                {
                    GUI.Label(new Rect(20, yPos, 400, 30),
                              $"Ch{i}: {latestValues[i]:F4}", dataStyle);
                    yPos += 30;
                }
            }
        }

        void OnDestroy()
        {
            if (inlet != null)
            {
                inlet.close_stream();
                inlet = null;
            }
        }

        // Public methods for external access (channel: 1-4)
        public float GetChannelValue(int channel)
        {
            // BITalino: Ch1-4 → latestValues[1-4]にマッピング
            int index = channel;  // Ch1 → latestValues[1], Ch2 → latestValues[2], ...


            if (index >= 1 && index <= 4 && index < channelCount)
            {
                return latestValues[index];
            }
            else
            {
                return 0f;
            }
        }

        public float[] GetAllChannelValues()
        {
            return (float[])latestValues.Clone();
        }

        // 直近チャンク内の1サンプルの値を取得（channel: GetChannelValueと同じ1始まり規約、Ch1-4）
        public float GetChunkChannelValue(int sampleIndex, int channel)
        {
            if (data_buffer == null) return 0f;
            if (sampleIndex < 0 || sampleIndex >= LastChunkCount) return 0f;
            if (channel < 1 || channel > 4 || channel >= channelCount) return 0f;
            return data_buffer[sampleIndex, channel];
        }

        // 直近チャンク内の1サンプルのLSLタイムスタンプを取得
        public double GetChunkTimestamp(int sampleIndex)
        {
            if (timestamp_buffer == null) return 0.0;
            if (sampleIndex < 0 || sampleIndex >= LastChunkCount) return 0.0;
            return timestamp_buffer[sampleIndex];
        }

        public bool IsConnected()
        {
            return isConnected;
        }
    }

}