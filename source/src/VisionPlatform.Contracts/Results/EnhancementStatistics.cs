using VisionPlatform.Contracts.Geometry;
namespace VisionPlatform.Contracts.Results;
public sealed record EnhancementChannelStatistics(int Channel,double InputMinimum,double InputMaximum,double InputMean,double InputStandardDeviation,
    double OutputMinimum,double OutputMaximum,double OutputMean,double OutputStandardDeviation);
public sealed record EnhancementStatistics(Guid FrameId,ImageCoordinateSpace CoordinateSpace,int Width,int Height,string Rule,
    IReadOnlyList<EnhancementChannelStatistics> Channels,double? SelectedInputMinimum=null,double? SelectedInputMaximum=null,bool ConstantRange=false);

