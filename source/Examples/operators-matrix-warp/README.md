# 矩阵与仿射/透视映射

复制后运行。仿射和透视各生成独立图像；mapped_roi把原ROI映射为真实四边形，来源键保留，不能当作旋转矩形。mapped_point给出(80,80)的正向映射，InverseTransform可还原。estimate使用明确配对点，不自动注册或标定。matrix/inverse/element展示真实数学矩阵；precision_matrix用于高精度表格编辑。

全部资源为自制合成/演示文本，不含用户数据、模型和环境，不代表产线准确率。普通核心Runtime即可运行，不需要Python、相机或网络。参数/旧结果失效沿用现有编辑器；工程包使用原导出/预检/导入到新目录。
