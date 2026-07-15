using UnityEngine;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
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
        private double max_chunk_duration = 1.0;

        // Receive Thread
        private Thread receiveThread;
        private volatile bool running = false;
        private const double RECEIVE_TIMEOUT = 0.5; // pull_sampleのブロッキング上限(秒)。runningを定期チェックして安全に終了するため
        private ConcurrentQueue<RawSample> sampleQueue = new ConcurrentQueue<RawSample>();

        // 受信生サンプル（最大8ch分をインライン保持する値型。ヒープ確保なし）
        private struct RawSample
        {
            public double timestamp;
            public float c0, c1, c2, c3, c4, c5, c6, c7;

            public float this[int ch]
            {
                get
                {
                    switch (ch)
                    {
                        case 0: return c0; case 1: return c1; case 2: return c2; case 3: return c3;
                        case 4: return c4; case 5: return c5; case 6: return c6; case 7: return c7;
                        default: return 0f;
                    }
                }
                set
                {
                    switch (ch)
                    {
                        case 0: c0 = value; break; case 1: c1 = value; break; case 2: c2 = value; break; case 3: c3 = value; break;
                        case 4: c4 = value; break; case 5: c5 = value; break; case 6: c6 = value; break; case 7: c7 = value; break;
                    }
                }
            }
        }

        // Latest Data
        private float[] latestValues = new float[8];  // 最大8チャンネル

        // 直近フレームでキューから取り出したサンプル数（1000Hz全サンプル処理用）
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

                channelCount = targetStream.channel_count();
                samplingRate = (float)targetStream.nominal_srate();
                inlet = new StreamInlet(targetStream);

                int buf_samples = (int)Mathf.Ceil((float)(samplingRate * max_chunk_duration));
                data_buffer = new float[buf_samples, channelCount];
                timestamp_buffer = new double[buf_samples];

                latestValues = new float[channelCount];

                isConnected = true;
                errorMessage = "";

                running = true;
                receiveThread = new Thread(ReceiveLoop);
                receiveThread.IsBackground = true;   // アプリ終了時にプロセスを道連れにしない
                receiveThread.Name = "LSL-EMG-Receive";
                receiveThread.Start();
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

        void ReceiveLoop()
        {
            float[] sample = new float[channelCount];
            while (running)
            {
                try
                {
                    double ts = inlet.pull_sample(sample, RECEIVE_TIMEOUT);
                    if (ts != 0.0)
                    {
                        RawSample rs = new RawSample { timestamp = ts };
                        int n = Mathf.Min(channelCount, 8);
                        for (int ch = 0; ch < n; ch++) rs[ch] = sample[ch];
                        sampleQueue.Enqueue(rs);
                    }
                    // ts==0.0 はタイムアウト(サンプル無し)。runningを再チェックしてループ継続。
                }
                catch (System.Exception)
                {
                    // inletがcloseされた/終了処理中など。ループを抜ける。
                    break;
                }
            }
        }

        void Update()
        {
            if (!isConnected)
            {
                LastChunkCount = 0;
                return;
            }

            int cap = data_buffer.GetLength(0);
            int count = 0;
            while (count < cap && sampleQueue.TryDequeue(out RawSample rs))
            {
                for (int ch = 0; ch < channelCount; ch++)
                {
                    data_buffer[count, ch] = rs[ch];
                }
                timestamp_buffer[count] = rs.timestamp;
                count++;
            }
            LastChunkCount = count;

            if (count > 0)// 受信できたかを判定
            {
                // 最新サンプルを取得
                int lastIndex = count - 1;

                for (int ch = 0; ch < channelCount; ch++)
                {
                    latestValues[ch] = data_buffer[lastIndex, ch];
                }

                receivedSamples += count;
            }
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
            StopReceiving();
        }

        void OnDisable()
        {
            StopReceiving();
        }

        void OnApplicationQuit()
        {
            StopReceiving();
        }

        void StopReceiving()
        {
            running = false;

            if (receiveThread != null)
            {
                receiveThread.Join(1000); // pull_sampleのtimeout(0.5s)より長く待つ
                receiveThread = null;
            }

            if (inlet != null)
            {
                inlet.close_stream();
                inlet = null;
            }

            if (resolver != null)
            {
                resolver.Dispose();
                resolver = null;
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
