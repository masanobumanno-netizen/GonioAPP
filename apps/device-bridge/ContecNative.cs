using System.Runtime.InteropServices;
using System.Text;

namespace Gonio.DeviceBridge;

// Signatures checked against API-AIO(WDM) 9.31 CaioCs source in Develop.cab.
// Install the official x64 driver on Windows before native acquisition.
internal static class ContecNative
{
    [DllImport("caio.dll")] internal static extern int AioGetAiMaxChannels(short id, ref short channels);
    [DllImport("caio.dll")] internal static extern int AioQueryDeviceName(short index, StringBuilder name, StringBuilder model);
    [DllImport("caio.dll")] internal static extern int AioSetAiTransferMode(short id, short mode);
    [DllImport("caio.dll")] internal static extern int AioSetAiRepeatTimes(short id, int times);
    [DllImport("caio.dll")] internal static extern int AioResetAiStatus(short id);
    [DllImport("caio.dll")] internal static extern int AioInit(string deviceName, ref short id);
    [DllImport("caio.dll")] internal static extern int AioExit(short id);
    [DllImport("caio.dll")] internal static extern int AioGetErrorString(int errorCode, StringBuilder errorString);
    [DllImport("caio.dll")] internal static extern int AioSetAiChannels(short id, short channels);
    [DllImport("caio.dll")] internal static extern int AioSetAiRangeAll(short id, short range);
    [DllImport("caio.dll")] internal static extern int AioSetAiClockType(short id, short clockType);
    [DllImport("caio.dll")] internal static extern int AioSetAiSamplingClock(short id, float samplingClock);
    [DllImport("caio.dll")] internal static extern int AioGetAiSamplingClock(short id, ref float samplingClock);
    [DllImport("caio.dll")] internal static extern int AioSetAiStartTrigger(short id, short trigger);
    [DllImport("caio.dll")] internal static extern int AioSetAiStopTrigger(short id, short trigger);
    [DllImport("caio.dll")] internal static extern int AioSetAiMemoryType(short id, short memoryType);
    [DllImport("caio.dll")] internal static extern int AioResetAiMemory(short id);
    [DllImport("caio.dll")] internal static extern int AioStartAi(short id);
    [DllImport("caio.dll")] internal static extern int AioStopAi(short id);
    [DllImport("caio.dll")] internal static extern int AioGetAiStatus(short id, ref int status);
    [DllImport("caio.dll")] internal static extern int AioGetAiSamplingCount(short id, ref int count);
    [DllImport("caio.dll")] internal static extern int AioGetAiSamplingDataEx(short id, ref int times, [Out, MarshalAs(UnmanagedType.LPArray)] float[] data);
}
