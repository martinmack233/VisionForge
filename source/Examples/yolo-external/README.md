# YOLO 外部模型推理

调用既有 YOLOStudio 推理及 Tile。未配置外部依赖时明确失败，不生成演示框。

创建副本，配置 PythonExecutable、YoloStudioRoot、ModelPath（匹配的 deployment.json 必须存在），替换为自有授权图像后运行。可启用原系统 Tiled。默认不下载或加载模型。

依赖齐备后必须观察真实输出及模型 SHA256；发布包不含第三方模型。开发机真实 ONNX 和 Tile 记录见验收报告，不将该占位图视为缺陷精度测试。

图像为平台自制合成资源，非真实工业样本。OCR 仅随包英文模型。
