using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

namespace TowerTapes;

public sealed class AudioRecorder : IDisposable
{
    private readonly Config _config;
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private OggOpusWriter? _writer;
    private OpusEncoder? _encoder;

    private readonly object _lock = new();
    private readonly Queue<float> _micSamples = new();
    private readonly Queue<float> _sysSamples = new();
    private Thread? _thread;
    private volatile bool _recording;

    private const int RATE = 48000;
    private const int FRAME = 960; // 20ms at 48kHz

    public bool IsRecording => _recording;
    public bool ForceMicOpen { get; set; }
    public string? CurrentFile { get; private set; }
    public string? LastError { get; private set; }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public AudioRecorder(Config config) => _config = config;

    public void StartRecording(string outputPath)
    {
        if (_recording) return;
        LastError = null;
        CurrentFile = outputPath;

        try
        {
            var stream = File.Create(outputPath);
            _writer = new OggOpusWriter(stream, 2, RATE);
            _encoder = new OpusEncoder(RATE, 2, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = _config.OpusBitrateKbps * 1000;
        }
        catch (Exception ex)
        {
            LastError = $"Failed to create output file: {ex.Message}";
            return;
        }

        StartMicCapture();
        StartLoopbackCapture();

        if (_micCapture == null && _loopbackCapture == null)
        {
            LastError = "No audio devices available.";
            _writer?.Dispose();
            _writer = null;
            return;
        }

        _recording = true;
        _thread = new Thread(EncodingLoop)
        {
            IsBackground = true,
            Name = "TowerTapes-Encoder",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void StopRecording()
    {
        if (!_recording) return;
        _recording = false;

        _thread?.Join(5000);
        _thread = null;

        try { _micCapture?.StopRecording(); } catch { }
        try { _loopbackCapture?.StopRecording(); } catch { }
        _micCapture?.Dispose(); _micCapture = null;
        _loopbackCapture?.Dispose(); _loopbackCapture = null;

        lock (_lock) { _micSamples.Clear(); _sysSamples.Clear(); }

        _writer?.Dispose(); _writer = null;
        _encoder = null;
        CurrentFile = null;
    }

    public void Dispose() => StopRecording();

    // --- Capture setup ---

    private void StartMicCapture()
    {
        try
        {
            var device = GetMicDevice();
            if (device == null) return;
            _micCapture = new WasapiCapture(device);
            _micCapture.DataAvailable += OnMicData;
            _micCapture.RecordingStopped += OnCaptureStopped;
            _micCapture.StartRecording();
        }
        catch (Exception ex)
        {
            LastError = $"Mic: {ex.Message}";
            _micCapture?.Dispose();
            _micCapture = null;
        }
    }

    private void StartLoopbackCapture()
    {
        try
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            _loopbackCapture.DataAvailable += OnSysData;
            _loopbackCapture.RecordingStopped += OnCaptureStopped;
            _loopbackCapture.StartRecording();
        }
        catch (Exception ex)
        {
            LastError = $"Loopback: {ex.Message}";
            _loopbackCapture?.Dispose();
            _loopbackCapture = null;
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            LastError = $"Capture error: {e.Exception.Message}";
    }

    // --- Audio data callbacks ---

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var fmt = _micCapture!.WaveFormat;
        var mono = ToMono48k(e.Buffer, e.BytesRecorded, fmt);

        // Always enqueue real mic data — PTT gating is applied in the
        // encoding loop where GetAsyncKeyState is reliable (own thread).
        lock (_lock)
        {
            foreach (float s in mono)
                _micSamples.Enqueue(s);
        }
    }

    private void OnSysData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var fmt = _loopbackCapture!.WaveFormat;
        var mono = ToMono48k(e.Buffer, e.BytesRecorded, fmt);

        lock (_lock)
        {
            foreach (float s in mono)
                _sysSamples.Enqueue(s);
        }
    }

    // --- Format conversion ---

    private static float[] ToMono48k(byte[] buf, int len, WaveFormat fmt)
    {
        int channels = fmt.Channels;
        int sampleRate = fmt.SampleRate;

        // Decode to interleaved float
        float[] raw;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
            (fmt.BitsPerSample == 32 && fmt.Encoding != WaveFormatEncoding.Pcm))
        {
            int count = len / 4;
            raw = new float[count];
            Buffer.BlockCopy(buf, 0, raw, 0, count * 4);
        }
        else if (fmt.BitsPerSample == 32) // 32-bit integer PCM
        {
            int count = len / 4;
            raw = new float[count];
            for (int i = 0; i < count; i++)
                raw[i] = BitConverter.ToInt32(buf, i * 4) / 2147483648f;
        }
        else if (fmt.BitsPerSample == 16) // PCM 16
        {
            int count = len / 2;
            raw = new float[count];
            for (int i = 0; i < count; i++)
                raw[i] = BitConverter.ToInt16(buf, i * 2) / 32768f;
        }
        else // 24-bit or other — treat as silence
        {
            return [];
        }

        // Downmix to mono
        int frames = raw.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += raw[i * channels + ch];
            mono[i] = sum / channels;
        }

        // Resample to 48 kHz if needed
        if (sampleRate != RATE && sampleRate > 0)
        {
            double ratio = (double)RATE / sampleRate;
            int outLen = (int)(mono.Length * ratio);
            var res = new float[outLen];
            for (int i = 0; i < outLen; i++)
            {
                double src = i / ratio;
                int i0 = (int)src;
                int i1 = Math.Min(i0 + 1, mono.Length - 1);
                double f = src - i0;
                res[i] = (float)(mono[i0] * (1.0 - f) + mono[i1] * f);
            }
            return res;
        }

        return mono;
    }

    // --- Encoding loop ---

    private void EncodingLoop()
    {
        var outBuf = new byte[4000];

        while (_recording)
        {
            Thread.Sleep(15);
            EncodeFrames(outBuf);
        }

        // Final flush
        EncodeFrames(outBuf);
    }

    private void EncodeFrames(byte[] outBuf)
    {
        // Check PTT from the encoding thread where GetAsyncKeyState is reliable
        bool micMuted = !ForceMicOpen && _config.PttEnabled && !IsPttDown();

        lock (_lock)
        {
            // Use Max so encoding proceeds even if one capture stream lags or
            // fails entirely — the lagging channel is padded with silence.
            int available = Math.Max(_micSamples.Count, _sysSamples.Count);
            while (available >= FRAME)
            {
                var pcm = new short[FRAME * 2];
                for (int i = 0; i < FRAME; i++)
                {
                    float mic = _micSamples.Count > 0 ? _micSamples.Dequeue() : 0f;
                    float sys = _sysSamples.Count > 0 ? _sysSamples.Dequeue() : 0f;
                    pcm[i * 2] = Clamp16(micMuted ? 0f : mic);
                    pcm[i * 2 + 1] = Clamp16(sys);
                }

                try
                {
                    int encoded = _encoder!.Encode(pcm, 0, FRAME, outBuf, 0, outBuf.Length);
                    if (encoded > 0)
                        _writer!.WritePacket(outBuf, encoded, FRAME);
                }
                catch { }

                available = Math.Max(_micSamples.Count, _sysSamples.Count);
            }

            // Prevent unbounded queue growth if one stream is much faster —
            // trim the larger queue to at most 1 second of excess over the smaller.
            const int MAX_DRIFT = RATE; // 1 second
            if (_micSamples.Count > _sysSamples.Count + MAX_DRIFT)
                while (_micSamples.Count > _sysSamples.Count + MAX_DRIFT)
                    _micSamples.Dequeue();
            else if (_sysSamples.Count > _micSamples.Count + MAX_DRIFT)
                while (_sysSamples.Count > _micSamples.Count + MAX_DRIFT)
                    _sysSamples.Dequeue();
        }
    }

    private static short Clamp16(float v) =>
        (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);

    private bool IsPttDown()
    {
        // Handle mouse button names from the key detector
        int? vk = _config.PttKey switch
        {
            "RButton" => 0x02,   // VK_RBUTTON
            "MButton" => 0x04,   // VK_MBUTTON
            "XButton1" => 0x05,  // VK_XBUTTON1
            "XButton2" => 0x06,  // VK_XBUTTON2
            _ => null
        };

        if (vk.HasValue)
            return (GetAsyncKeyState(vk.Value) & 0x8000) != 0;

        if (Enum.TryParse<Keys>(_config.PttKey, true, out var k))
            return (GetAsyncKeyState((int)k) & 0x8000) != 0;

        return false;
    }

    private MMDevice? GetMicDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(_config.MicDeviceId))
        {
            try { return enumerator.GetDevice(_config.MicDeviceId); }
            catch { }
        }
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { }
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); }
        catch { }
        return null;
    }

    public static List<(string Id, string Name)> GetMicDevices()
    {
        var result = new List<(string, string)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                result.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return result;
    }
}
