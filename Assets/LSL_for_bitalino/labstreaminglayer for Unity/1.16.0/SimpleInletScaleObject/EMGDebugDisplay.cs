using UnityEngine;
using System.Collections;
using LSL;

namespace LSL4Unity.Samples.SimpleInlet
{
    /// <summary>
    /// LSLからEMGデータを受信してConsoleとOnGUIで表示（3D環境対応）
    /// UI Textを使わないシンプル版
    /// </summary>
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

        void Start()
        {
            Debug.Log("=== EMGDebugDisplay Started ===");

            if (string.IsNullOrEmpty(StreamName))
            {
                errorMessage = "StreamName is empty!";
                Debug.LogError(errorMessage);
                enabled = false;
                return;
            }

            Debug.Log($"Searching for LSL stream: '{StreamName}'");
            isSearching = true;

            // 全ストリームを検索してデバッグ
            resolver = new ContinuousResolver();
            StartCoroutine(ResolveExpectedStream());
        }

        IEnumerator ResolveExpectedStream()
        {
            Debug.Log($"[LSL] Resolver created for stream: '{StreamName}'");

            var results = resolver.results();
            int waitCount = 0;

            while (results.Length == 0)
            {
                waitCount++;
                if (waitCount % 10 == 0)  // 1秒ごとにログ
                {
                    Debug.Log($"[LSL] Still searching... ({waitCount * 0.1f}s elapsed)");
                }

                yield return new WaitForSeconds(0.1f);
                results = resolver.results();

                // タイムアウト（30秒）
                if (waitCount > 300)
                {
                    errorMessage = "Timeout: Stream not found after 30 seconds";
                    Debug.LogError(errorMessage);
                    isSearching = false;
                    enabled = false;
                    yield break;
                }
            }

            isSearching = false;

            try
            {
                // 見つかった全ストリームをログ出力
                Debug.Log($"[LSL] Found {results.Length} stream(s):");
                for (int i = 0; i < results.Length; i++)
                {
                    Debug.Log($"  Stream {i}: Name='{results[i].name()}', Type='{results[i].type()}', Channels={results[i].channel_count()}");
                }

                // StreamNameと一致するものを探す（nameまたはtype）
                StreamInfo targetStream = null;
                foreach (var stream in results)
                {
                    if (stream.name() == StreamName || stream.type() == StreamName)//stream.type() == bitalimoのdivicename
                    {
                        targetStream = stream;
                        Debug.Log($"[LSL] Matched stream: Name='{stream.name()}', Type='{stream.type()}'");
                        break;
                    }
                }

                // 見つからなければ最初のストリームを使用
                if (targetStream == null)
                {
                    Debug.LogWarning($"[LSL] Stream '{StreamName}' not found by name or type. Using first available stream.");
                    targetStream = results[0];
                }

                Debug.Log($"[LSL] Creating inlet for stream: '{targetStream.name()}'");
                inlet = new StreamInlet(targetStream);

                channelCount = inlet.info().channel_count();
                samplingRate = (float)inlet.info().nominal_srate();

                Debug.Log($"[LSL] Inlet created successfully!");
                Debug.Log($"  Stream Name: {inlet.info().name()}");
                Debug.Log($"  Stream Type: {inlet.info().type()}");
                Debug.Log($"  Channel Count: {channelCount}");
                Debug.Log($"  Sampling Rate: {samplingRate} Hz");

                int buf_samples = (int)Mathf.Ceil((float)(samplingRate * max_chunk_duration));
                data_buffer = new float[buf_samples, channelCount];
                timestamp_buffer = new double[buf_samples];

                latestValues = new float[channelCount];

                isConnected = true;
                errorMessage = "";

                Debug.Log($"✅ [LSL] Connected successfully to '{StreamName}'");
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
                return;
            }

            try
            {
                int samples_returned = inlet.pull_chunk(data_buffer, timestamp_buffer);

                if (samples_returned > 0)// 受信できたかを判定
                {
                    // 最新サンプルを取得
                    int lastIndex = samples_returned - 1;

                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        latestValues[ch] = data_buffer[lastIndex, ch];
                    }

                    receivedSamples += samples_returned;

                    // デバッグ: 配列インデックスとチャンネル番号を明示
                    if (receivedSamples >= 100 && receivedSamples <= 110)
                    {
                        Debug.Log($"===== Sample #{receivedSamples} =====");
                        Debug.Log($"channelCount = {channelCount}");
                        Debug.Log($"samples_returned = {samples_returned}");
                        Debug.Log($"lastIndex = {lastIndex}");

                        for (int ch = 0; ch < channelCount; ch++)
                        {
                            Debug.Log($"  data_buffer[{lastIndex}, {ch}] = {data_buffer[lastIndex, ch]:F6} → latestValues[{ch}] = {latestValues[ch]:F6}");
                        }
                        Debug.Log("==================");
                    }

                    // 定期的にログ出力（1000Hzで表示）
                    if (showConsoleLog && receivedSamples % 1 == 0)
                    {
                        LogData();
                    }
                }
            }
            catch (System.Exception e)
            {
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
                Debug.Log("[LSL] Closing inlet...");
                inlet.close_stream();
                inlet = null;
            }
        }

        // Public methods for external access (channel: 1-4)
        public float GetChannelValue(int channel)
        {
            // BITalino: Ch1-4 → latestValues[1-4]にマッピング
            int index = channel;  // Ch1 → latestValues[1], Ch2 → latestValues[2], ...
            if (!isConnected)
            {
                Debug.LogWarning($"[GetChannelValue] Not connected! Returning 0 for Ch{channel}");
                return 0f;
            }

            if (index >= 1 && index <= 4 && index < channelCount)
            {
                Debug.Log($"[GetChannelValue] Ch{channel} → latestValues[{index}] = {latestValues[index]:F6}");
                return latestValues[index];
            }
            else
            {
                Debug.LogWarning($"[GetChannelValue] Invalid channel {channel} or index {index} (channelCount={channelCount})");
                return 0f;
            }
        }

        public float[] GetAllChannelValues()
        {
            return (float[])latestValues.Clone();
        }

        public bool IsConnected()
        {
            return isConnected;
        }
    }

}