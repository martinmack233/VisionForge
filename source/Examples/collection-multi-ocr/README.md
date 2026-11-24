# 多区域英文OCR · 集合逐项

打开后先复制为用户工程，再运行。图中三个英文/数字区域使用正式RoiCollection参数，ForEach按0、1、2顺序调用原OCR节点。选择集合结果显示节点，在集合/逐项面板选择结果可高亮及查看局部；选择逐项节点可查看保留迭代端口。参数面板可编辑ROI集合和子流程，OCR文本布局可选择单行、多行块或自动。

输入图像由OpenCV绘制，为自制合成资源，不含用户图片、模型或数据库；不代表工业准确率认证。所有示例运行使用原Runtime，未触发训练、GPU、相机或通信。

逐项异常可选择停止或记录继续；空集合返回0项。调试图像仅保留显式预算，不长期保留每项整图。正角顺时针，原图像素坐标，索引从0开始。

可选YOLO衔接：在用户副本中显式配置已有授权模型及deployment.json，连接 YoloInference.Result → CollectionAdapter.Detections → CollectionFilter/Sort → CollectionToRois → ForEach.Collection，并连接同一源图到ForEach.Image；选择OCR子流程模板。该可选路径不携带权重、不自动下载，未用用户模型验收。
