namespace Gonio.DeviceBridge;
public static class SelfTests
{
    public static void Run()
    {
        int tests=0;
        void Assert(bool condition,string name){if(!condition)throw new Exception("FAIL: "+name);tests++;Console.WriteLine("PASS: "+name);}
        bool Fails(Action action){try{action();return false;}catch{return true;}}
        Assert(DeviceProfiles.Require("AIO-160802GY-USB",new("AIO000",0,7)).RepeatTimes==1,"GY profile uses required single repeat");
        Assert(DeviceProfiles.All.All(p=>p.RepeatTimes==1 && p.Channels==8),"AY/GY profile limits");
        Assert(Fails(()=>DeviceProfiles.Require("UNKNOWN",new("AIO000"))),"unknown model rejected");
        long index=0;
        var config=new CaptureConfig("AIO000",0,1);
        var samples=SampleDecoder.Decode([1,2,3,4],config,ref index,1000);
        Assert(samples.Length==2&&samples[1].Ch1Voltage==3&&samples[1].Ch2Voltage==4,"interleaved two-channel decode");
        Assert(samples[1].Timestamp==1010&&samples[1].ElapsedMs==10&&index==2,"100 Hz index/time grid");
        var more=SampleDecoder.Decode([5,6],config,ref index,1000);
        Assert(more[0].SampleIndex==2&&more[0].Timestamp==1020,"continuous index across batches");
        index=0;var reversed=SampleDecoder.Decode([1,2,3],new("AIO000",2,0),ref index,0);
        Assert(reversed[0].Ch1Voltage==3&&reversed[0].Ch2Voltage==1,"physical channel mapping");
        Assert(Fails(()=>new CaptureConfig("AIO000",1,1).Validate()),"same channels rejected");
        Assert(Fails(()=>new CaptureConfig("AIO000",0,8).Validate()),"out of range channel rejected");
        Assert(Fails(()=>new CaptureConfig("../bad",0,1).Validate()),"invalid device name rejected");
        Assert(Fails(()=>SampleDecoder.Decode([1],config,ref index,0)),"incomplete scan rejected");
        Assert(Fails(()=>SampleDecoder.Decode([float.NaN,2],config,ref index,0)),"invalid voltage rejected");
        using var fake=new SimulatedDevice(); fake.Start(config);Thread.Sleep(45);Assert(fake.Read().Length>=4,"test acquisition produces buffered scans");
        Console.WriteLine($"{tests} tests passed.");
    }
}
