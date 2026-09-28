using System.Diagnostics;
using System.Text;
using System.Globalization;
using static Gonio.DeviceBridge.ContecNative;

namespace Gonio.DeviceBridge;

public record DeviceInfo(string Name, string Model)
{
    public DeviceProfile? Profile => DeviceProfiles.Find(Model);
    public bool Supported => Profile != null;
}
public record CaptureConfig(string DeviceName, int Channel1 = 0, int Channel2 = 1)
{
    public int ScanChannels => Math.Max(Channel1, Channel2) + 1;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > 64 || !DeviceName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new ArgumentException("デバイス名が不正です。Device Utilityの名前を指定してください。");
        if (Channel1 < 0 || Channel1 > 7 || Channel2 < 0 || Channel2 > 7 || Channel1 == Channel2) throw new ArgumentException("物理チャンネルは0〜7の異なる2つを指定してください。");
    }
}
public record Sample(long SampleIndex, double ElapsedMs, long Timestamp, double Ch1Voltage, double Ch2Voltage);
public interface IAnalogDevice : IDisposable
{
    void Start(CaptureConfig config);
    float[] Read();
    void Stop();
}
public static class SampleDecoder
{
    public static Sample[] Decode(float[] values, CaptureConfig config, ref long index, long epoch)
    {
        config.Validate();
        if (values.Length % config.ScanChannels != 0) throw new InvalidDataException("チャンネル配列長が不正です。");
        var result = new Sample[values.Length / config.ScanChannels];
        for (int i = 0; i < result.Length; i++)
        {
            var a = values[i * config.ScanChannels + config.Channel1];
            var b = values[i * config.ScanChannels + config.Channel2];
            if (!float.IsFinite(a) || !float.IsFinite(b)) throw new InvalidDataException("センサ値が有限数ではありません。");
            result[i] = new Sample(index, index * 10, epoch + index * 10, a, b);
            index++;
        }
        return result;
    }
}
public sealed class ContecDevice : IAnalogDevice
{
    private short id;
    private bool initialized, started;
    private int channels;
    public string? Model { get; private set; }
    public static void Check(int code, string operation)
    {
        if (code == 0) return;
        var text = new StringBuilder(256);
        AioGetErrorString(code, text);
        throw new IOException($"{operation}: CONTECエラー {code} {text}");
    }
    public static List<DeviceInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CONTEC接続はWindows版で利用できます。");
        var list = new List<DeviceInfo>();
        for (short i = 0; i < 64; i++)
        {
            var name = new StringBuilder(256); var model = new StringBuilder(256);
            int code = AioQueryDeviceName(i, name, model);
            if (code == 10006) break; // no device at this index, per SDK
            Check(code, "AioQueryDeviceName");
            list.Add(new DeviceInfo(name.ToString(), model.ToString()));
        }
        return list;
    }
    public void Start(CaptureConfig config)
    {
        config.Validate();
        var device = Enumerate().SingleOrDefault(d => d.Name.Equals(config.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (device == null) throw new IOException("登録された機器が見つかりません。CONTEC Device Utilityを確認してください。");
        var profile = DeviceProfiles.Require(device.Model, config);
        Model = profile.Model;
        Check(AioInit(config.DeviceName, ref id), "AioInit"); initialized = true;
        try
        {
            // No reset-device call: do not disturb another process's ongoing acquisition.
            short maximum = 0;
            Check(AioGetAiMaxChannels(id, ref maximum), "AioGetAiMaxChannels");
            if (config.ScanChannels > maximum) throw new IOException("ドライバのチャンネル数と設定が一致しません。");
            channels = config.ScanChannels;
            Check(AioSetAiChannels(id, (short)channels), "AioSetAiChannels");
            Check(AioSetAiRangeAll(id, 0), "AioSetAiRangeAll ±10V");
            Check(AioSetAiTransferMode(id, 0), "AioSetAiTransferMode device buffer");
            Check(AioSetAiMemoryType(id, 0), "AioSetAiMemoryType FIFO");
            Check(AioSetAiClockType(id, 0), "AioSetAiClockType internal");
            Check(AioSetAiSamplingClock(id, 10000f), "AioSetAiSamplingClock 100Hz");
            float actual = 0;
            Check(AioGetAiSamplingClock(id, ref actual), "AioGetAiSamplingClock");
            if (Math.Abs(actual - 10000f) > 0.1f) throw new IOException($"100Hz設定を確認できません。読戻し={actual}µs");
            Check(AioSetAiStartTrigger(id, 0), "AioSetAiStartTrigger software");
            Check(AioSetAiStopTrigger(id, 4), "AioSetAiStopTrigger command");
            Check(AioSetAiRepeatTimes(id, profile.RepeatTimes), "AioSetAiRepeatTimes");
            Check(AioResetAiStatus(id), "AioResetAiStatus");
            Check(AioResetAiMemory(id), "AioResetAiMemory");
            Check(AioStartAi(id), "AioStartAi"); started = true;
        }
        catch { Dispose(); throw; }
    }
    public float[] Read()
    {
        int status = 0, count = 0;
        Check(AioGetAiStatus(id, ref status), "AioGetAiStatus");
        if ((status & 0x000F0000) != 0) throw new IOException($"機器エラー: 0x{status:X}（オーバーフロー・クロック・AD変換・ドライバ）");
        if ((status & 1) == 0) throw new IOException("機器のサンプリングが停止しました。");
        Check(AioGetAiSamplingCount(id, ref count), "AioGetAiSamplingCount");
        if (count <= 0) return [];
        int requested = Math.Min(count, 1000), actual = requested;
        var values = new float[requested * channels];
        Check(AioGetAiSamplingDataEx(id, ref actual, values), "AioGetAiSamplingDataEx");
        if (actual < 0 || actual > requested) throw new IOException("CONTEC取得数が不正です。");
        return values.AsSpan(0, actual * channels).ToArray();
    }
    public void Stop()
    {
        if (started) { started = false; Check(AioStopAi(id), "AioStopAi"); }
    }
    public void Dispose()
    {
        try { Stop(); } finally { if (initialized) { initialized = false; Check(AioExit(id), "AioExit"); } }
    }
}

// Explicit test-only implementation, never selected as fallback for native failure.
public sealed class SimulatedDevice : IAnalogDevice
{
    private readonly Stopwatch clock = new();
    private int count, channels;
    public void Start(CaptureConfig config) { config.Validate(); channels = config.ScanChannels; count = 0; clock.Restart(); }
    public float[] Read()
    {
        int due = Math.Min((int)(clock.ElapsedMilliseconds / 10) - count, 1000);
        var data = new float[Math.Max(0, due) * channels];
        for (int i = 0; i < due; i++) for (int c = 0; c < channels; c++) data[i * channels + c] = 2 + c + (float)Math.Sin((count + i) / 50.0) * .25f;
        count += Math.Max(0, due); return data;
    }
    public void Stop() => clock.Stop();
    public void Dispose() => Stop();
}
