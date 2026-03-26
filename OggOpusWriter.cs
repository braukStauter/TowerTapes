using System.Text;

namespace TowerTapes;

public sealed class OggOpusWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly int _channels;
    private readonly int _inputSampleRate;
    private readonly int _serial;
    private int _pageSeq;
    private long _granule;
    private long _lastPageOffset;
    private int _lastPageLength;
    private bool _hasAudioPages;
    private bool _closed;

    private const int PRE_SKIP = 3840; // 80ms at 48kHz
    private const byte BOS = 0x02;
    private const byte EOS = 0x04;

    private static readonly uint[] CrcLut;

    static OggOpusWriter()
    {
        CrcLut = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
                r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : r << 1;
            CrcLut[i] = r;
        }
    }

    public OggOpusWriter(Stream output, int channels, int inputSampleRate)
    {
        _stream = output;
        _channels = channels;
        _inputSampleRate = inputSampleRate;
        _serial = Random.Shared.Next();
        WriteIdHeader();
        WriteCommentHeader();
    }

    private void WriteIdHeader()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("OpusHead"u8);
        w.Write((byte)1);              // version
        w.Write((byte)_channels);
        w.Write((ushort)PRE_SKIP);
        w.Write((uint)_inputSampleRate);
        w.Write((short)0);             // output gain
        w.Write((byte)0);              // channel mapping family
        WritePage(ms.ToArray(), BOS, 0);
    }

    private void WriteCommentHeader()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("OpusTags"u8);
        var vendor = Encoding.UTF8.GetBytes("TowerTapes");
        w.Write(vendor.Length);
        w.Write(vendor);
        w.Write(0); // no user comments
        WritePage(ms.ToArray(), 0, 0);
    }

    public void WritePacket(byte[] data, int length, int samplesPerChannel)
    {
        _granule += samplesPerChannel;
        _hasAudioPages = true;
        WritePage(data.AsSpan(0, length).ToArray(), 0, _granule);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;

        // Patch EOS flag onto the last audio page (avoids writing
        // a zero-length Opus packet which decoders would reject).
        if (_hasAudioPages && _stream.CanSeek && _lastPageLength > 0)
        {
            _stream.Seek(_lastPageOffset, SeekOrigin.Begin);
            var page = new byte[_lastPageLength];
            _stream.ReadExactly(page, 0, _lastPageLength);

            page[5] |= EOS;

            // Recompute CRC with the new flags byte
            page[22] = page[23] = page[24] = page[25] = 0;
            uint crc = 0;
            foreach (byte b in page)
                crc = (crc << 8) ^ CrcLut[((crc >> 24) ^ b) & 0xFF];
            BitConverter.GetBytes(crc).CopyTo(page, 22);

            _stream.Seek(_lastPageOffset, SeekOrigin.Begin);
            _stream.Write(page, 0, _lastPageLength);
        }

        _stream.Flush();
        _stream.Dispose();
    }

    private void WritePage(byte[] body, byte flags, long granule)
    {
        _lastPageOffset = _stream.Position;

        // Build segment table
        var segs = new List<byte>();
        int rem = body.Length;
        while (rem >= 255) { segs.Add(255); rem -= 255; }
        segs.Add((byte)rem);

        int hdrSize = 27 + segs.Count;
        var page = new byte[hdrSize + body.Length];

        // "OggS"
        page[0] = 0x4F; page[1] = 0x67; page[2] = 0x67; page[3] = 0x53;
        page[4] = 0;     // version
        page[5] = flags;
        BitConverter.GetBytes(granule).CopyTo(page, 6);
        BitConverter.GetBytes(_serial).CopyTo(page, 14);
        BitConverter.GetBytes(_pageSeq++).CopyTo(page, 18);
        // CRC at bytes 22-25 left as 0 for now
        page[26] = (byte)segs.Count;
        for (int i = 0; i < segs.Count; i++)
            page[27 + i] = segs[i];
        Buffer.BlockCopy(body, 0, page, hdrSize, body.Length);

        // Compute CRC over full page (with CRC field = 0)
        uint crc = 0;
        foreach (byte b in page)
            crc = (crc << 8) ^ CrcLut[((crc >> 24) ^ b) & 0xFF];
        BitConverter.GetBytes(crc).CopyTo(page, 22);

        _stream.Write(page, 0, page.Length);
        _lastPageLength = page.Length;
    }
}
