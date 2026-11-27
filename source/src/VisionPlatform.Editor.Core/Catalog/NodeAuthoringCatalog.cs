using System.Text.Json;
using VisionPlatform.Algorithms.Utilities;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Algorithms.Results;
using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;

namespace VisionPlatform.Editor.Core.Catalog;

public sealed record NodeAuthoringDefinition(string TypeId, string DisplayName, string Category,
    Type ParameterType, JsonElement DefaultParameters);

/// <summary>Editor-only metadata. Runtime registration and serialization contracts stay unchanged.</summary>
public sealed class NodeAuthoringCatalog
{
    private readonly Dictionary<string, NodeAuthoringDefinition> _items = new(StringComparer.Ordinal);
    public IReadOnlyList<NodeAuthoringDefinition> Items => _items.Values.ToArray();
    public NodeAuthoringCatalog()
    {
        Register<TcpCommunicationOptions>("TcpCommunication", "TCP通信（统一入口）", "通信");
        Register<ModbusCommunicationOptions>("ModbusCommunication", "Modbus通信", "通信");
        Register<ModbusDecodeOptions>("ModbusDecode", "Modbus寄存器解码", "通信");
        Register<RandomNumberOptions>("RandomNumber", "随机数（可复现）", "数据与文件");
        Register<FileReadOptions>("FileRead", "本地文件读取", "数据与文件");
        Register<FileValueOptions>("FileValue", "文件字段与单元格", "数据与文件");
        Register<BooleanOperationOptions>("BooleanOperation", "布尔运算", "逻辑控制");
        Register<ItemValueOptions>("ItemValue", "集合项字段值", "集合与子流程");
        Register<LinearContrastOptions>("LinearContrast", "线性亮度与对比度", "图像预处理");
        Register<RangeStretchOptions>("RangeStretch", "灰度范围拉伸", "图像预处理");
        Register<PercentileStretchOptions>("PercentileStretch", "百分位对比度拉伸", "图像预处理");
        Register<GammaCorrectionOptions>("GammaCorrection", "Gamma校正", "图像预处理");
        Register<EnhancementBudgetOptions>("Invert", "图像反相", "图像预处理");
        Register<SobelOptions>("Sobel", "Sobel真实浮点导数", "图像预处理");
        Register<ScharrOptions>("Scharr", "Scharr真实浮点导数", "图像预处理");
        Register<LaplacianOptions>("Laplacian", "Laplacian真实浮点导数", "图像预处理");
        Register<GradientMagnitudeDirectionOptions>("GradientMagnitudeDirection", "梯度幅值与方向", "图像预处理");
        Register<MatrixCreateOptions>("MatrixCreate", "构造数值矩阵", "矩阵与坐标");
        Register<MatrixMathOptions>("MatrixMath", "矩阵运算", "矩阵与坐标");
        Register<MatrixElementOptions>("MatrixElement", "矩阵元素查看", "矩阵与坐标");
        Register<TransformCreateOptions>("TransformCreate", "构造图像变换矩阵", "图像变换");
        Register<EstimateTransformOptions>("EstimateTransform", "对应点求仿射或透视", "图像变换");
        Register<WarpImageOptions>("WarpImage", "仿射与透视图像变换", "图像变换");
        Register<MapGeometryOptions>("MapGeometry", "映射点集与四边形", "矩阵与坐标");
        Register<ImageArithmeticOptions>("ImageArithmetic", "图像差分与算术", "图像预处理");
        Register<InspectionEmptyOptions>("ApplyMask", "应用掩膜", "图像预处理");
        Register<InspectionEmptyOptions>("RegionStatistics", "区域灰度统计", "目标检测");
        Register<ColorConversionOptions>("ColorSpaceConvert", "HSV/Lab工作颜色转换", "图像预处理");
        Register<ColorRangeOptions>("ColorInRange", "颜色多通道范围分割", "图像分割");
        Register<ColorChannelOptions>("ColorChannel", "工作颜色通道提取", "图像预处理");
        Register<FindContoursOptions>("FindContours", "提取全部轮廓", "目标检测");
        Register<ShapeAnalysisOptions>("ContourAnalysis", "全部轮廓形状分析", "目标检测");
        Register<ContourEnvelopeOptions>("ContourEnvelope", "轮廓包围几何", "几何测量");
        Register<ImageFilterOptions>("ImageFilter", "图像滤波", "图像预处理");
        Register<HoughLinesOptions>("HoughLinesP", "概率霍夫线段", "目标检测");
        Register<HoughCirclesOptions>("HoughCircles", "霍夫圆检测", "目标检测");
        Register<CannyOptions>("Canny", "Canny边缘", "图像预处理");
        Register<ItemGeometryOptions>("ItemGeometry", "当前几何转测量输入", "集合与子流程");

        Register<CollectionAdapterOptions>("CollectionAdapter", "结果转集合", "集合与子流程");
        Register<RoiCollectionOptions>("RoiCollection", "ROI集合", "集合与子流程");
        Register<CollectionCountOptions>("CollectionCount", "集合数量", "集合与子流程");
        Register<CollectionIndexOptions>("CollectionSelect", "集合按索引选择", "集合与子流程");
        Register<CollectionSliceOptions>("CollectionSlice", "集合范围选择", "集合与子流程");
        Register<CollectionFilterOptions>("CollectionFilter", "集合字段筛选", "集合与子流程");
        Register<CollectionSortOptions>("CollectionSort", "集合稳定排序", "集合与子流程");
        Register<ItemRoiOptions>("ItemToRoi", "当前项转ROI", "集合与子流程");
        Register<ItemRoiOptions>("CollectionToRois", "集合转ROI集合", "集合与子流程");
        Register<VisionPlatform.Contracts.Collections.ForEachOptions>("ForEach", "逐项子流程", "集合与子流程");
        _items["ForEach"] = _items["ForEach"] with { DefaultParameters = JsonDefaults.Element(new VisionPlatform.Contracts.Collections.ForEachOptions { Body = VisionPlatform.Plugins.Samples.CollectionSubflows.Measure() }) };
        Register<IterationInputOptions>("IterationInput", "子流程当前项", "集合与子流程");
        Register<IterationOutputOptions>("IterationOutput", "收集当前项结果", "集合与子流程");
        Register<VisionPlatform.Contracts.Collections.CollectionDisplayOptions>("CollectionDisplay", "集合结果显示", "集合与子流程");
        Register<VirtualCameraParameters>("VirtualCamera", "虚拟相机采集", "图像输入");
        Register<CameraInputParameters>("CameraInput", "工业相机采集", "图像输入");
        Register<ImageInputParameters>("ImageInput", "图像输入", "图像输入");
        Register<PixelFormatConvertParameters>("PixelFormatConvert", "像素格式转换", "图像预处理");
        Register<GaussianBlurParameters>("GaussianBlur", "高斯滤波", "图像预处理");
        Register<MedianBlurOptions>("MedianBlur", "中值滤波", "图像预处理");
        Register<ThresholdOptions>("Threshold", "固定阈值分割", "图像分割");
        Register<MorphologyOptions>("Morphology", "形态学处理", "图像预处理");
        Register<CropImageOptions>("CropImage", "图像裁切", "图像变换");
        Register<RotatedCropOptions>("RotatedCrop", "旋转裁切", "图像变换");
        Register<TemplateMatchingParameters>("TemplateMatching", "模板匹配", "模板匹配");
        Register<ROITransformParameters>("ROITransform", "ROI 跟随", "ROI 工具");
        Register<BlobOptions>("BlobDetection", "Blob 检测", "目标检测");
        Register<BlobFilterOptions>("BlobFilter", "Blob 筛选", "目标检测");
        Register<ResultDecisionParameters>("ResultDecision", "结果判定", "逻辑控制");
        Register<ImageDisplayParameters>("ImageDisplay", "图像显示", "结果显示");
        Register<TextDisplayParameters>("TextDisplay", "文本显示", "结果显示");
        Register<CompareOptions>("Compare", "数值比较", "逻辑控制");
        Register<LogicBinaryOptions>("LogicAnd", "布尔与", "逻辑控制");
        Register<LogicBinaryOptions>("LogicOr", "布尔或", "逻辑控制");
        Register<LogicNotOptions>("LogicNot", "布尔非", "逻辑控制");
        Register<IfElseParameters>("IfElse", "条件分支", "逻辑控制");
        Register<MergeParameters>("Merge", "分支合并", "逻辑控制");
        Register<TcpConnectParameters>("TcpConnect", "TCP 建立连接（旧入口）", "通信");
        Register<TcpSendParameters>("TcpSend", "TCP 发送（旧入口）", "通信");
        Register<TcpReceiveParameters>("TcpReceive", "TCP 接收（旧入口）", "通信");
        Register<TcpCloseParameters>("TcpDisconnect", "TCP 断开连接（旧入口）", "通信");
        Register<TcpServerStartParameters>("TcpServerStart", "TCP 启动监听（旧入口）", "通信");
        Register<TcpServerAcceptParameters>("TcpServerAccept", "TCP 接受客户端（旧入口）", "通信");
        Register<TcpReceiveParameters>("TcpServerReceive", "TCP 服务端接收（旧入口）", "通信");
        Register<TcpSendParameters>("TcpServerSend", "TCP 服务端响应（旧入口）", "通信");
        Register<TcpCloseParameters>("TcpServerStop", "TCP 停止监听（旧入口）", "通信");
        Register<SerialOpenOptions>("SerialOpen", "串口打开", "通信");
        Register<SerialSendOptions>("SerialSend", "串口发送", "通信");
        Register<SerialReceiveOptions>("SerialReceive", "串口接收", "通信");
        Register<SerialCloseOptions>("SerialClose", "串口关闭", "通信");
        Register<ResultDisplayParameters>("ResultDisplay", "检测结果显示", "结果显示");
        Register<ResultFormatOptions>("ResultFormatter", "检测结果格式化", "结果显示");
        Register<OtsuThresholdOptions>("OtsuThreshold", "Otsu 自动阈值", "图像分割");
        Register<AdaptiveThresholdOptions>("AdaptiveThreshold", "自适应阈值", "图像分割");
        Register<NormalizeOptions>("Normalize", "图像归一化", "图像预处理");
        Register<EqualizeHistOptions>("EqualizeHist", "灰度直方图均衡化", "图像预处理");
        Register<ClaheOptions>("CLAHE", "局部直方图均衡化", "图像预处理");
        Register<RotateImageOptions>("RotateImage", "图像旋转", "图像变换");
        Register<ResizeImageOptions>("ResizeImage", "图像缩放", "图像变换");
        Register<FlipImageOptions>("FlipImage", "图像翻转", "图像变换");
        Register<OcrOptions>("OCR", "OCR 字符识别", "字符与条码");
        Register<BarcodeReadOptions>("BarcodeReader", "一维条码识别", "字符与条码");
        Register<QrCodeReadOptions>("QRCodeReader", "二维码识别", "字符与条码");
        Register<YoloInferenceOptions>("YoloInference", "YOLO 目标检测", "AI 推理");
        Register<YoloDetectionRoiOptions>("YoloDetectionRoi", "AI目标转ROI", "ROI 工具");
        Register<CaliperOptions>("Caliper", "卡尺边缘测量", "几何测量");
        Register<CircleEdgePointsOptions>("CircleEdgePoints", "圆形边缘点提取", "几何测量");
        Register<LineFitOptions>("LineFit", "直线拟合", "几何测量");
        Register<CircleFitOptions>("CircleFit", "圆形拟合", "几何测量");
        Register<DistanceMeasureOptions>("DistanceMeasure", "距离测量", "几何测量");
        Register<AngleMeasureOptions>("AngleMeasure", "夹角测量", "几何测量");
    }
    public void Register<T>(string typeId, string name, string category) where T : new()
        => _items.Add(typeId, new(typeId, name, category, typeof(T), JsonDefaults.Element(new T())));
    public NodeAuthoringDefinition Get(string id) => _items.TryGetValue(id, out var value) ? value
        : throw new KeyNotFoundException("没有参数元数据: " + id);
}
