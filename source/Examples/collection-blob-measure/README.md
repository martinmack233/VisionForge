# 多Blob · 筛选排序与逐项测量

复制并运行。BlobDetection真实输出四个连通域；集合按Area>=100筛除小点，按X稳定排序，再逐项把当前Blob转ROI，用原DistanceMeasure计算ROI中心到右边中点距离，并以>=12像素判定。结果应为10/15/25像素，对应NG/OK/OK。参数面板可改变筛选和判定，旧结果会失效。

输入图像由OpenCV绘制，为自制合成资源，不含用户图片、模型或数据库；不代表工业准确率认证。所有示例运行使用原Runtime，未触发训练、GPU、相机或通信。

逐项异常可选择停止或记录继续；空集合返回0项。调试图像仅保留显式预算，不长期保留每项整图。正角顺时针，原图像素坐标，索引从0开始。

可选YOLO衔接：在用户副本中显式配置已有授权模型及deployment.json，连接 YoloInference.Result → CollectionAdapter.Detections → CollectionFilter/Sort → CollectionToRois → ForEach.Collection，并连接同一源图到ForEach.Image；选择OCR子流程模板。该可选路径不携带权重、不自动下载，未用用户模型验收。
