# 上位机TCP调用检测

显式启动Loopback Host，由版本化长度前缀TCP客户端加载、运行、轮询并取回真实结果。

一键运行仅执行离线检测图，不自动启动监听。独立模式：VisionPlatform.RuntimeHost.exe --project Examples/host-tcp/project.json --once。TCP模式：启动VisionPlatform.RuntimeHost.exe --port 0，记录输出地址与端口，再使用python tools/runtimehost_client.py --port 实际端口 --project 本工程绝对路径加载并运行。完整参数和消息见Host协议文档。

真实BlobCount=2、IsOk=True；Host结果必须含当前RunId、引擎RunId与节点状态/耗时；取消/断线后资源应释放。结果仅包含图像元数据，不传递原生指针。

全部图像为自制合成资源，文件相机通过通用相机接口工作；未验证真实硬件或工业精度。内置示例只读，修改先创建副本。
