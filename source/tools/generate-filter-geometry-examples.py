"""Generate the two original offline examples in a new directory. Requires Pillow only when regenerating assets."""

import argparse
import hashlib
import json
import math
from pathlib import Path
from random import Random

from PIL import Image, ImageDraw


def write_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def node(node_id, kind, name, **parameters):
    return dict(NodeId=node_id, NodeTypeId=kind, Name=name, Parameters=parameters, PluginVersion="1.0.0", IsEnabled=True)


def edge(source, output, target, port):
    return dict(SourceNodeId=source, SourcePort=output, TargetNodeId=target, TargetPort=port)


def check(node_id, port, expected, property_name=""):
    return dict(NodeId=node_id, Port=port, Expected=expected, Property=property_name, Tolerance=0)


def save(root, example_id, project_id, name, image, nodes, edges, flow, guide, try_it, verification, checks):
    folder = root / example_id
    folder.mkdir(parents=True, exist_ok=False)
    image.save(folder / "input.png")
    preview = image.copy()
    preview.thumbnail((480, 280))
    preview.save(folder / "preview.png")
    write_json(folder / "project.json", dict(SchemaVersion=3, ProjectId=project_id, ProjectName=name,
        Nodes=nodes, Connections=edges, ROI=[], TemplateResources=[], AlgorithmVersion={}, AiProject=None))
    (folder / "README.md").write_text(
        f"# {name}\n\n{flow}\n\n{guide}\n\n{try_it}\n\n预期：{verification}\n\n"
        "图片为本仓库自制合成资源，CC0 1.0。无需联网、模型或相机。请在示例副本中编辑。\n", encoding="utf-8")
    files = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(folder.iterdir())}
    write_json(folder / "manifest.json", dict(SchemaVersion=1, Id=example_id, Name=name, Category="传统视觉检测",
        Description=guide, Difficulty="基础", Tags=["自制合成", "滤波" if example_id == "image-filter-methods" else "轮廓几何"],
        Project="project.json", Preview="preview.png", Instructions=guide, Flow=flow, LearningGoals=guide,
        TryIt=try_it, ExpectedSummary=verification, Verification=verification, Expected=checks, Files=files))


def filtering(root):
    image = Image.new("L", (768, 448))
    rng = Random(7349)
    for y in range(image.height):
        for x in range(image.width):
            if y < 135 and x < 220:
                value = 110 if x % 2 == 0 else 128
            elif y < 135 and x >= 548:
                value = 110 if y % 2 == 0 else 128
            else:
                value = (102 if x < 384 else 150) + rng.randrange(-10, 11)
                if rng.randrange(170) == 0:
                    value = 5 if rng.randrange(2) == 0 else 245
            image.putpixel((x, y), value)

    nodes = [node("input", "ImageInput", "ImageInput", Path="input.png", PixelFormat="Mono8")]
    edges = []
    branches = [
        ("mean", "均值 / 方框", "Mean", dict(KernelWidth=5, KernelHeight=3)),
        ("gauss", "高斯", "Gaussian", dict(KernelSize=5, SigmaX=1.2)),
        ("median", "中值", "Median", dict(KernelSize=5)),
        ("bilateral", "双边", "Bilateral", dict(Diameter=7, SigmaColor=18, SigmaSpace=5)),
        ("horizontal", "方向平滑 · 横向", "Directional", dict(KernelSize=7, SamplingDirection="Horizontal")),
        ("vertical", "方向平滑 · 纵向", "Directional", dict(KernelSize=7, SamplingDirection="Vertical")),
    ]
    for key, label, mode, parameters in branches:
        nodes.append(node(key, "ImageFilter", label, Method=mode, **parameters))
        edges.append(edge("input", "Image", key, "Image"))
    nodes += [
        node("sum", "ImageFilter", "方框邻域和", Method="BoxSum", KernelWidth=5, KernelHeight=3),
        node("edges", "Canny", "Canny", Low=42, High=100, L2Gradient=True),
        node("display", "ImageDisplay", "ImageDisplay"),
    ]
    edges += [edge("input", "Image", "sum", "Image"), edge("bilateral", "ProcessedImage", "edges", "Image"),
        edge("edges", "Edges", "display", "Image")]
    save(root, "image-filter-methods", "2476c721-b2c8-4ead-96a0-fccd075f9284", "图像滤波 · 噪声与方向",
        image, nodes, edges, "噪声图 → 多种滤波分支；双边 → Canny → 图像显示",
        "一键运行后逐个选择图像滤波节点的 ProcessedImage。上方左侧为竖条纹，右侧为横条纹；中下部含低对比边缘、随机噪声和脉冲点。点选 NeighborhoodSum 检查浮点邻域和。",
        "把双边的颜色 Sigma 从 18 改为 45 后重跑，观察保边变化。对比横向和纵向取样消减哪组条纹；对比中值去除脉冲及平均带来的边缘软化。",
        "输出保留 768×448。左上竖条纹的已知偶数列像素，纵向取样后为 110，横向取样后为 120。",
        [check("horizontal", "ProcessedImage", 120, "Pixel[100,50]"),
         check("vertical", "ProcessedImage", 110, "Pixel[100,50]"), check("sum", "NeighborhoodSum", 768, "Width"),
         check("display", "Image", 448, "Height")])


def geometry(root):
    image = Image.new("L", (720, 400))
    draw = ImageDraw.Draw(image)
    draw.rectangle((36, 44, 174, 124), fill=255)
    angle = math.radians(32)
    draw.polygon([(round(272 + x * math.cos(angle) - y * math.sin(angle)),
                   round(91 + x * math.sin(angle) + y * math.cos(angle)))
                  for x, y in [(-62, -26), (62, -26), (62, 26), (-62, 26)]], fill=255)
    draw.ellipse((423, 36, 533, 146), fill=255)
    draw.polygon([(38, 211), (196, 211), (196, 241), (75, 241), (75, 324), (176, 324), (176, 356), (38, 356)], fill=255)
    draw.ellipse((280, 207, 418, 345), fill=255)
    draw.ellipse((313, 240, 385, 312), fill=0)
    draw.rectangle((640, 227, 719, 305), fill=255)  # clipped at the right edge

    nodes = [node("input", "ImageInput", "ImageInput", Path="input.png", PixelFormat="Mono8"),
        node("contours", "FindContours", "FindContours", Retrieval="Tree")]
    edges = [edge("input", "Image", "contours", "Image")]
    for key, label, mode in [("axis", "轴对齐外接矩形", "AxisAlignedRectangle"),
                             ("rotated", "最小面积旋转矩形", "MinimumAreaRectangle"),
                             ("outer", "最小外接圆", "MinimumEnclosingCircle"),
                             ("inner", "最大内接圆", "MaximumInscribedCircle")]:
        nodes.append(node(key, "ContourEnvelope", label, Mode=mode))
        edges.append(edge("contours", "Collection", key, "Collection"))
    edges.append(edge("input", "Image", "inner", "Mask"))
    nodes += [
        node("valid", "CollectionFilter", "CollectionFilter", Field="Valid", Comparison="Equal", Boolean=True),
        node("filter_radius", "CollectionFilter", "CollectionFilter", Field="Radius", Comparison="GreaterOrEqual", Number=12),
        node("sort", "CollectionSort", "CollectionSort", Field="Radius", Descending=True),
        node("select", "CollectionSelect", "CollectionSelect", Index=0),
        node("display", "CollectionDisplay", "CollectionDisplay", Fields="Index,Radius,Diameter", FillOpacity=0.14),
        node("each", "ForEach", "逐项读取直径", Body=dict(
            Version=1, InputNodeId="current", OutputNodeId="collect", NodeLocations={},
            Nodes=[node("current", "IterationInput", "IterationInput"), node("value", "ItemValue", "ItemValue", Field="Diameter"),
                   node("collect", "IterationOutput", "IterationOutput")],
            Connections=[edge("current", "Item", "value", "Item"), edge("value", "Number", "collect", "Number")])),
    ]
    edges += [edge("inner", "Collection", "valid", "Collection"), edge("valid", "Collection", "filter_radius", "Collection"),
        edge("filter_radius", "Collection", "sort", "Collection"), edge("sort", "Collection", "select", "Collection"),
        edge("sort", "Collection", "display", "Collection"), edge("input", "Image", "display", "Image"),
        edge("sort", "Collection", "each", "Collection")]
    save(root, "contour-envelope-geometry", "2b7bd612-a66b-4749-8765-c2bd11a12893", "轮廓包围几何 · 矩形与圆",
        image, nodes, edges, "二值图 → 树形轮廓 → 四种包围几何；内接圆 → 筛选与排序 → 集合显示",
        "运行后选择集合显示，在“集合 / 迭代”表点击一行高亮对应圆。查看各包围几何节点的 Collection 比较矩形、旋转矩形、外接圆和内接圆。内接圆的 Collection 含拒绝原因。",
        "查看右侧触边目标与环的孔洞项。用 Rectangles 接入已有 ROI 操作，用 Circles 或稳定排序接入取单项及逐项子流程。半径筛选低于 12 像素的目标不会进入显示表。",
        "输入有 6 个前景目标及 1 条孔洞边界。内接圆产出 5 项有效结果，孔洞边界和触边目标明确标为无效。",
        [check("contours", "Count", 7), check("axis", "ValidCount", 7), check("rotated", "ValidCount", 7),
         check("outer", "ValidCount", 7), check("inner", "ValidCount", 5), check("display", "Collection", 5, "Count"),
         check("each", "CompletedCount", 5)])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    destination = parser.parse_args().destination
    filtering(destination)
    geometry(destination)
