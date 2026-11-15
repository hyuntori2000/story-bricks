using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BrailleSoundDemo
{
    /// <summary>Bounded PCM16 WAV conversion. No codec plugin or platform-specific decoder is required.</summary>
    public static class WavCodec
    {
        public const int MaximumBytes = 8 * 1024 * 1024;

        public static byte[] Encode(float[] samples, int channels, int sampleRate)
        {
            if (samples == null || samples.Length == 0 || channels < 1 || channels > 2 ||
                sampleRate < 8000 || sampleRate > 48000 || samples.Length % channels != 0 ||
                (long)samples.Length * 2 + 44 > MaximumBytes)
                throw new ArgumentException("Expected nonempty mono/stereo audio at 8–48 kHz within the WAV size limit.");
            using (var stream = new MemoryStream(44 + samples.Length * 2))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)channels); writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2);
                foreach (float sample in samples)
                {
                    float value = float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : Mathf.Clamp(sample, -1, 1);
                    writer.Write((short)Mathf.RoundToInt(value < 0 ? value * 32768 : value * 32767));
                }
                return stream.ToArray();
            }
        }

        public static float[] Decode(byte[] bytes, out int channels, out int sampleRate)
        {
            channels = 0; sampleRate = 0;
            if (bytes == null || bytes.Length < 44 || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Generated audio is empty or exceeds the WAV size limit.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (Tag(reader) != "RIFF") throw new InvalidDataException("Expected a RIFF WAV file.");
                uint riffSize = reader.ReadUInt32();
                if (riffSize + 8L != bytes.Length || Tag(reader) != "WAVE") throw new InvalidDataException("Invalid WAV container length.");
                long dataStart = -1; int dataSize = 0; bool format = false;
                while (stream.Position + 8 <= stream.Length)
                {
                    string chunk = Tag(reader); uint length = reader.ReadUInt32(); long start = stream.Position;
                    long end = start + length;
                    if (end > stream.Length) throw new InvalidDataException("Truncated WAV chunk.");
                    if (chunk == "fmt ")
                    {
                        if (length < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("Generated audio must use PCM16 WAV.");
                        channels = reader.ReadUInt16(); sampleRate = reader.ReadInt32();
                        int byteRate = reader.ReadInt32(); int blockAlign = reader.ReadUInt16(); int bits = reader.ReadUInt16();
                        if (channels < 1 || channels > 2 || sampleRate < 8000 || sampleRate > 48000 || bits != 16 ||
                            blockAlign != channels * 2 || byteRate != sampleRate * blockAlign)
                            throw new InvalidDataException("Unsupported WAV channels, rate, or sample format.");
                        format = true;
                    }
                    else if (chunk == "data")
                    {
                        if (dataStart >= 0) throw new InvalidDataException("Multiple WAV data chunks are not supported.");
                        dataStart = start; dataSize = checked((int)length);
                    }
                    stream.Position = end + (length & 1);
                    if (stream.Position > stream.Length) throw new InvalidDataException("Missing WAV padding.");
                }
                if (!format || dataStart < 0 || dataSize <= 0 || dataSize % (channels * 2) != 0 ||
                    (double)dataSize / (channels * 2 * sampleRate) > 120)
                    throw new InvalidDataException("Expected nonempty speech audio up to 120 seconds.");
                stream.Position = dataStart;
                var samples = new float[dataSize / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
                return samples;
            }
        }

        public static AudioClip CreateClip(byte[] bytes, string name)
        {
            var samples = Decode(bytes, out int channels, out int rate);
            var clip = AudioClip.Create(name, samples.Length / channels, channels, rate, false);
            if (!clip.SetData(samples, 0)) { UnityEngine.Object.Destroy(clip); throw new InvalidDataException("Could not load decoded WAV samples."); }
            return clip;
        }
        static string Tag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    }
}
