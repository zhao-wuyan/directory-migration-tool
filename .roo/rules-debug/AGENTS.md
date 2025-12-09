# Project Debug Rules (Non-Obvious Only)

- **Debug模式需要管理员权限运行** - 符号链接创建需要管理员权限，否则会失败
- **发布Debug版本使用 -Mode debug -s 参数** - 这样不会增加版本号，适合调试测试
- **Robocopy进程可能需要手动终止** - 在调试中断时，Robocopy子进程可能仍在运行
- **符号链接创建失败检查开发者模式** - Windows需要开启开发者模式或以管理员权限运行
- **Debug模式下控制台输出只在Debug配置有效** - Release配置下即使有#if DEBUG也不会显示
- **文件复制进度在Debug模式下更详细** - 包含Robocopy原始输出解析过程
- **符号链接检测失败检查路径格式** - 需要使用完整路径，相对路径可能导致误判
- **目录扫描超时检查大文件阈值** - 大文件过多时扫描可能很慢，可调整LargeFileThresholdMB参数
- **还原模式失败检查标记文件** - .migrate-marker和.restore-marker文件可能影响状态检测
- **权限问题检查UAC虚拟化** - 某些系统目录需要特殊权限处理
- **一键迁移配置文件读取失败检查quick-migrate.json** - 嵌入资源可能未正确加载
- **进度计算异常检查指数移动平均算法** - CopyOperationExecutor中的速度计算可能在Debug模式下输出更多细节