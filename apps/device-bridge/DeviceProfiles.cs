namespace Gonio.DeviceBridge;

// Only profiles checked against API-AIO(WDM)'s device-specific function tables.
public record DeviceProfile(string Model, int Channels, int RepeatTimes = 1, string InputMethod = "single-ended", string Range = "±10V", bool HardwareVerified = false);
public static class DeviceProfiles
{
    public static readonly DeviceProfile[] All = [
        new("AI-1608AY-USB", 8), new("AIO-160802AY-USB", 8),
        new("AI-1608GY-USB", 8), new("AIO-160802GY-USB", 8)
    ];
    public static DeviceProfile? Find(string model) => All.SingleOrDefault(p => p.Model.Equals(model.Trim(), StringComparison.OrdinalIgnoreCase));
    public static DeviceProfile Require(string model, CaptureConfig config)
    {
        var profile = Find(model) ?? throw new IOException("未対応の型番です: " + model + "。対応機種: " + string.Join(", ", All.Select(p => p.Model)));
        if (config.ScanChannels > profile.Channels) throw new ArgumentException("選択した物理チャンネルが機器の範囲を超えています。");
        return profile;
    }
}
