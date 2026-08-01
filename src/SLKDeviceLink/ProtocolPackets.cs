namespace SLKDeviceLink;

public static class ProtocolPackets
{
    public static byte[] YcyV1Fixed(bool channelA, bool channelB, int intensity, int mode)
    {
        intensity = Math.Clamp(intensity, 0, 180);
        mode = Math.Clamp(mode, 1, 16);
        var channel = channelA && channelB ? 0x03 : channelA ? 0x01 : channelB ? 0x02 : 0x03;
        var bytes = new byte[] { 0x35, 0x11, (byte)channel, intensity > 0 ? (byte)1 : (byte)0, (byte)(intensity >> 8), (byte)intensity, (byte)mode, 0, 0, 0 };
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    public static byte[] YcyV1Realtime(bool channelA, bool channelB, int intensity, int frequency, int pulseWidth)
    {
        intensity = Math.Clamp(intensity, 0, 180);
        frequency = Math.Clamp(frequency, 1, 100);
        pulseWidth = Math.Clamp(pulseWidth, 0, 100);
        var channel = channelA && channelB ? 0x03 : channelA ? 0x01 : channelB ? 0x02 : 0x03;
        var bytes = new byte[] { 0x35, 0x11, (byte)channel, intensity > 0 ? (byte)1 : (byte)0, (byte)(intensity >> 8), (byte)intensity, 0x11, (byte)frequency, (byte)pulseWidth, 0 };
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    public static byte[] YcyV2Fixed(int intensityA, int modeA, int intensityB, int modeB)
    {
        intensityA = Math.Clamp(intensityA, 0, 180);
        intensityB = Math.Clamp(intensityB, 0, 180);
        var bytes = new byte[] { 0x35, 0x11, 0x01, (byte)(intensityA >> 8), (byte)intensityA, (byte)Math.Clamp(modeA, 1, 16), (byte)(intensityB >> 8), (byte)intensityB, (byte)Math.Clamp(modeB, 1, 16), 0 };
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    public static byte[] YcyV2Realtime(int intensityA, int frequencyA, int pulseA, int intensityB, int frequencyB, int pulseB)
    {
        intensityA = Math.Clamp(intensityA, 0, 180);
        intensityB = Math.Clamp(intensityB, 0, 180);
        var bytes = new byte[]
        {
            0x35, 0x11, 0x02,
            (byte)(intensityA >> 8), (byte)intensityA, (byte)Math.Clamp(frequencyA, 1, 100), (byte)Math.Clamp(pulseA, 0, 100),
            (byte)(intensityB >> 8), (byte)intensityB, (byte)Math.Clamp(frequencyB, 1, 100), (byte)Math.Clamp(pulseB, 0, 100), 0
        };
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    public static byte[] CoyoteSoftLimit(DeviceProfile profile) => new byte[]
    {
        0xBF,
        (byte)Math.Clamp(profile.CoyoteSoftMaxA, 0, 200),
        (byte)Math.Clamp(profile.CoyoteSoftMaxB, 0, 200),
        (byte)Math.Clamp(profile.CoyoteFrequencyBalanceA, 0, 255),
        (byte)Math.Clamp(profile.CoyoteFrequencyBalanceB, 0, 255),
        (byte)Math.Clamp(profile.CoyoteStrengthBalanceA, 0, 255),
        (byte)Math.Clamp(profile.CoyoteStrengthBalanceB, 0, 255)
    };

    public static byte[] CoyoteFrame(int sequence, int intensityA, int intensityB, ReadOnlySpan<byte> waveformA, ReadOnlySpan<byte> waveformB, bool setIntensityA = true, bool setIntensityB = true)
    {
        if (waveformA.Length != 8 || waveformB.Length != 8)
            throw new ArgumentException("Coyote channel waveform must contain exactly 8 bytes.");

        var bytes = new byte[20];
        bytes[0] = 0xB0;
        var parsingMethod = (setIntensityA ? 0x0C : 0) | (setIntensityB ? 0x03 : 0);
        bytes[1] = (byte)(((sequence & 0x0F) << 4) | parsingMethod);
        bytes[2] = (byte)Math.Clamp(intensityA, 0, 200);
        bytes[3] = (byte)Math.Clamp(intensityB, 0, 200);
        waveformA.CopyTo(bytes.AsSpan(4, 8));
        waveformB.CopyTo(bytes.AsSpan(12, 8));
        return bytes;
    }

    public static byte[] ToyQuery()
    {
        var bytes = new byte[] { 0x35, 0x10, 0 };
        bytes[^1] = Checksum(bytes.AsSpan(0, 2));
        return bytes;
    }

    public static byte[] ToyRate(int motorA, int motorB, int motorC)
    {
        var bytes = new byte[] { 0x35, 0x12, (byte)Math.Clamp(motorA, 0, 20), (byte)Math.Clamp(motorB, 0, 20), (byte)Math.Clamp(motorC, 0, 20), 0 };
        bytes[^1] = Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var value in bytes)
            unchecked { sum += value; }
        return sum;
    }
}
