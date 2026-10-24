namespace MagSid.Editor.Avalonia;

/// <summary>
/// A .wav as samples, mixed to one channel, -1 to 1 - what Digi to SID-osc analyses. The Windows
/// editor reads it through NAudio, which is Windows only; this reads the RIFF file itself: PCM at
/// 8, 16, 24 or 32 bits, and IEEE float at 32 or 64, in a plain or an extensible format chunk.
/// </summary>
internal static class WavFile
{
    private const ushort Pcm = 1;
    private const ushort IeeeFloat = 3;
    private const ushort Extensible = 0xFFFE;

    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Not a .wav file: it does not start with RIFF.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Not a .wav file: it is a RIFF file, but not WAVE.");

        ushort format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string id = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            long next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                // The extensible form names its real format in the first two bytes of a GUID.
                if (format == Extensible && size >= 26)
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    reader.ReadUInt32();
                    format = reader.ReadUInt16();
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes((int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position));
            }

            if (next > reader.BaseStream.Length)
                break;
            reader.BaseStream.Position = next;
        }

        if (data is null || channels == 0 || sampleRate <= 0)
            throw new InvalidDataException("The .wav has no sound in it, or no format chunk saying what the sound is.");

        Func<byte[], int, float> sample = (format, bits) switch
        {
            (Pcm, 8) => (b, i) => (b[i] - 128) / 128f,
            (Pcm, 16) => (b, i) => BitConverter.ToInt16(b, i) / 32768f,
            (Pcm, 24) => (b, i) => ((b[i] | (b[i + 1] << 8) | (b[i + 2] << 16)) << 8 >> 8) / 8388608f,
            (Pcm, 32) => (b, i) => BitConverter.ToInt32(b, i) / 2147483648f,
            (IeeeFloat, 32) => (b, i) => BitConverter.ToSingle(b, i),
            (IeeeFloat, 64) => (b, i) => (float)BitConverter.ToDouble(b, i),
            _ => throw new InvalidDataException($"A .wav of format {format} at {bits} bits cannot be read - save it as 16-bit PCM."),
        };

        int bytesPerSample = bits / 8;
        int frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
                sum += sample(data, (f * channels + c) * bytesPerSample);
            mono[f] = (float)(sum / channels);
        }

        return (mono, sampleRate);
    }
}
