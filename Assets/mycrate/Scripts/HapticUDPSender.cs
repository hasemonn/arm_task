using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Unity-UDP連携によるマルチチャンネル触覚フィードバックシステム
/// NUCLEO-F767ZIに接続された振動子を制御するUDP送信クラス
/// </summary>
public class HapticUDPSender : MonoBehaviour
{
    [Header("Network Settings")]
    [Tooltip("マイコンのIPアドレス")]
    [SerializeField] private string nucleoIP = "192.168.2.70";

    [Tooltip("マイコンのポート番号")]
    [SerializeField] private int nucleoPort = 55555;

    [Header("Channel Control")]
    [SerializeField] private PowerLevel channel0_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel1_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel2_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel3_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel4_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel5_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel6_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel7_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel8_level = PowerLevel.Off;
    [SerializeField] private PowerLevel channel9_level = PowerLevel.Off;

    [Header("Auto Send Settings")]
    [Tooltip("チャンネル値の変更を自動的にUDP送信する")]
    [SerializeField] private bool autoSendOnChange = true;

    [Tooltip("自動送信の間隔(秒) リアルタイム性を保つため小さい値推奨")]
    [SerializeField, Range(0.01f, 0.5f)] private float autoSendInterval = 0.5f;

    [Header("Debug")]
    [Tooltip("送信データをログに出力する")]
    [SerializeField] private bool logSentData = true;

    private enum PowerLevel
    {
        Off = 0,      // 0.0
        Low1 = 1,     // 0.1
        Low2 = 2,     // 0.2
        Low3 = 3,     // 0.3
        Mid1 = 4,     // 0.4
        Mid2 = 5,     // 0.5
        Mid3 = 6,     // 0.6
        High1 = 7,    // 0.7
        High2 = 8,    // 0.8
        High3 = 9,    // 0.9
        Max = 10      // 1.0
    }
    private float channel0 => (int)channel0_level / 10f;
    private float channel1 => (int)channel1_level / 10f;
    private float channel2 => (int)channel2_level / 10f;
    private float channel3 => (int)channel3_level / 10f;
    private float channel4 => (int)channel4_level / 10f;
    private float channel5 => (int)channel5_level / 10f;
    private float channel6 => (int)channel6_level / 10f;
    private float channel7 => (int)channel7_level / 10f;
    private float channel8 => (int)channel8_level / 10f;
    private float channel9 => (int)channel9_level / 10f;

    // 前回のチャンネル値（変更検出用）
    private float[] previousChannels = new float[10];
    private float lastAutoSendTime = 0f;

    // UDP通信用
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;

    // 送信カウント
    private int sendCount = 0;

    /// <summary>
    /// toMbed構造体 - マイコンに送信するデータ構造
    /// mbedのdataStruct.hと完全に同じ構造にする必要があります
    ///
    /// 重要: mbedのbool型は1バイトだが、後続のintとのアライメントに注意
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
    private struct ToMbed
    {
        public int vibration_intensity;        // offset: 0, size: 4

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public double[] Vibration;              // offset: 4, size: 80 (10×8)

        public float ch1;                       // offset: 84, size: 4
        public float ch2;                       // offset: 88, size: 4
        public float ch3;                       // offset: 92, size: 4
        public float ch4;                       // offset: 96, size: 4

        [MarshalAs(UnmanagedType.U1)]
        public byte servo1;                     // offset: 100, size: 1 (bool -> byte)

        [MarshalAs(UnmanagedType.U1)]
        public byte servo2;                     // offset: 101, size: 1 (bool -> byte)

        // パディングは自動的に挿入される（offset: 102-103, size: 2）

        public int checkCount;                  // offset: 104, size: 4
        public int returnCount;                 // offset: 108, size: 4
    }
    // 合計: 112 bytes (パディング込み)

    void Start()
    {
        InitializeUDP();
        InitializeChannelTracking();
    }

    void Update()
    {
        if (autoSendOnChange && Time.time - lastAutoSendTime >= autoSendInterval)
        {
            if (HasChannelChanged())
            {
                Debug.Log("HapticUDPSender: Channel changed detected in Update()");
                SendChannelValues();
                UpdatePreviousChannels();
                lastAutoSendTime = Time.time;
            }
        }
    }

    // Inspectorで値が変更されたときに呼ばれる
    void OnValidate()
    {
        // 実行中のみ送信（エディタモードでは送信しない）
        if (Application.isPlaying && autoSendOnChange && udpClient != null)
        {
            Debug.Log("HapticUDPSender: OnValidate() triggered - sending channel values");
            SendChannelValues();
            UpdatePreviousChannels();
        }
    }

    private void InitializeChannelTracking()
    {
        UpdatePreviousChannels();
    }

    private bool HasChannelChanged()
    {
        return previousChannels[0] != channel0 ||
               previousChannels[1] != channel1 ||
               previousChannels[2] != channel2 ||
               previousChannels[3] != channel3 ||
               previousChannels[4] != channel4 ||
               previousChannels[5] != channel5 ||
               previousChannels[6] != channel6 ||
               previousChannels[7] != channel7 ||
               previousChannels[8] != channel8 ||
               previousChannels[9] != channel9;
    }

    private void UpdatePreviousChannels()
    {
        previousChannels[0] = channel0;
        previousChannels[1] = channel1;
        previousChannels[2] = channel2;
        previousChannels[3] = channel3;
        previousChannels[4] = channel4;
        previousChannels[5] = channel5;
        previousChannels[6] = channel6;
        previousChannels[7] = channel7;
        previousChannels[8] = channel8;
        previousChannels[9] = channel9;
    }

    /// <summary>
    /// 10チャンネルの値をUDP送信（C++のPower[]配列に対応）
    /// </summary>
    public void SendChannelValues()
    {
        ToMbed data = new ToMbed
        {
            vibration_intensity = 1,
            Vibration = new double[10],
            ch1 = 0f,
            ch2 = 0f,
            ch3 = 0f,
            ch4 = 0f,
            servo1 = 0,
            servo2 = 0,
            checkCount = sendCount,
            returnCount = 0
        };

        // 10チャンネルの値を設定（C++のPower[0]～Power[9]に対応）
        data.Vibration[0] = channel0;
        data.Vibration[1] = channel1;
        data.Vibration[2] = channel2;
        data.Vibration[3] = channel3;
        data.Vibration[4] = channel4;
        data.Vibration[5] = channel5;
        data.Vibration[6] = channel6;
        data.Vibration[7] = channel7;
        data.Vibration[8] = channel8;
        data.Vibration[9] = channel9;

        SendData(data);
    }

    void InitializeUDP()
    {
        try
        {
            udpClient = new UdpClient();
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(nucleoIP), nucleoPort);
            Debug.Log($"HapticUDPSender: UDP initialized - Target: {nucleoIP}:{nucleoPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"HapticUDPSender: Failed to initialize UDP - {e.Message}");
        }
    }


    /// <summary>
    /// 構造体をバイト配列に変換してUDP送信
    /// </summary>
    private void SendData(ToMbed data)
    {
        if (udpClient == null)
        {
            Debug.LogError("HapticUDPSender: UDP client not initialized");
            return;
        }

        try
        {
            // 構造体をバイト配列に変換
            byte[] bytes = StructToBytes(data);

            // UDP送信
            udpClient.Send(bytes, bytes.Length, remoteEndPoint);

            sendCount++;

            if (logSentData)
            {
                Debug.Log($"=== UDP Send #{sendCount} ===");
                Debug.Log($"Size: {bytes.Length} bytes (expected: 112)");
                Debug.Log($"Target: {remoteEndPoint.Address}:{remoteEndPoint.Port}");

                // 全チャンネルの値を表示（0でも表示）
                string channelInfo = "Channels: [";
                for (int i = 0; i < data.Vibration.Length; i++)
                {
                    channelInfo += $"{data.Vibration[i]:F1}";
                    if (i < data.Vibration.Length - 1) channelInfo += ", ";
                }
                channelInfo += "]";
                Debug.Log(channelInfo);

                // バイト配列の全てを16進数で表示（16バイトごとに改行）
                string hex = "Bytes (hex):\n";
                for (int i = 0; i < bytes.Length; i++)
                {
                    hex += bytes[i].ToString("X2") + " ";
                    if ((i + 1) % 16 == 0) hex += "\n";
                }
                Debug.Log(hex);
                Debug.Log("===================");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"HapticUDPSender: Failed to send data - {e.Message}");
        }
    }

    /// <summary>
    /// 構造体をバイト配列に変換するヘルパーメソッド
    /// </summary>
    private byte[] StructToBytes(ToMbed data)
    {
        int size = Marshal.SizeOf(data);
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(data, ptr, true);
            Marshal.Copy(ptr, bytes, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return bytes;
    }


    void OnDestroy()
    {
        // クリーンアップ
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
            Debug.Log("HapticUDPSender: UDP client closed");
        }
    }
}
