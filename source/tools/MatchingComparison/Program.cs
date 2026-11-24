using System.Diagnostics;
using System.Text.Json;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using OpenCvSharp;

Cv2.SetNumThreads(1);
var root = Path.GetFullPath(args[0]);
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"cases.json")));
using var template = Cv2.ImRead(Path.Combine(root,"template.png"), ImreadModes.Grayscale);
var results = new List<object>();
foreach (var c in manifest.RootElement.EnumerateArray())
{
    using var image = Cv2.ImRead(Path.Combine(root,c.GetProperty("file").GetString()!),ImreadModes.Grayscale);
    var roi = c.GetProperty("roi").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    using var search = new Mat(image, new Rect((int)roi[0], (int)roi[1], (int)roi[2], (int)roi[3]));
    var opt = new CvPatternOpt { Trained=true, TrainedShape=CvTrainShape.Rect, TrainedOriginX=(template.Cols-1)/2.0, TrainedOriginY=(template.Rows-1)/2.0,
        UseAngleSearch=true, AngleZoneDeg=20, CoarseStepDeg=5, FineStepDeg=.25,
        UseScaleSearch=true, ScaleZonePct=20, ScaleStepPct=5, CoarseScale=.5,
        UseSearchRegion=false, AcceptScore=.7 };
    for(var i=0;i<2;i++) CvPatternFinder.Match(search,template,opt);
    var runs=new List<object>();
    for(var i=0;i<7;i++)
    {
        var before = Process.GetCurrentProcess().WorkingSet64;
        var timer=Stopwatch.StartNew();
        var p=CvPatternFinder.Match(search,template,opt);
        timer.Stop();
        runs.Add(new { milliseconds=timer.Elapsed.TotalMilliseconds, success=p.HasValue && p.Value.Score>=.7, score=p?.Score, x=p?.FoundX+roi[0],y=p?.FoundY+roi[1],angle=p?.ThetaDeg,scale=p?.Scale, working_set_before=before,working_set_after=Process.GetCurrentProcess().WorkingSet64,peak_working_set=Process.GetCurrentProcess().PeakWorkingSet64 });
    }
    results.Add(new { name=c.GetProperty("name").GetString(), runs });
    Console.WriteLine(c.GetProperty("name").GetString());
}
File.WriteAllText(Path.Combine(root,"cvinspect-results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
