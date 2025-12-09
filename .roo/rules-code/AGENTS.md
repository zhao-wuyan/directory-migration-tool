# Project Coding Rules (Non-Obvious Only)

- **符号链接创建必须使用 SymbolicLinkHelper 类** - 优先使用P/Invoke方法，失败时自动回退到cmd mklink命令
- **文件复制必须通过 CopyOperationExecutor 类** - 不直接使用Robocopy，确保进度监控和取消功能正常工作
- **所有符号链接检测必须使用 ReparsePoint 属性** - 不要依赖文件系统API的IsSymbolicLink方法，它在某些情况下不可靠
- **版本号修改必须同时更新 version.json 和 .csproj 文件** - publish.ps1脚本会自动同步两者，手动修改时需注意保持一致
- **Debug模式下的控制台输出必须使用 #if DEBUG 条件编译** - 生产发布版本会禁用调试输出以减少体积
- **WPF视图模型必须使用 CommunityToolkit.Mvvm 的 [ObservableProperty] 和 [RelayCommand]** - 不要手动实现INotifyPropertyChanged
- **目录扫描必须使用 MigrationWorkflowHelper.ScanDirectoryAsync** - 确保统计格式与大文件阈值计算一致
- **进度映射必须遵循 6 阶段流程** - 复制阶段占10%-90%，其他阶段按固定比例分配（0-5%-10%-93%-96%-100%）
- **备份目录必须使用时间戳命名** - MigrationWorkflowHelper.CreateSymbolicLinkWithBackupAsync会自动处理
- **Robocopy线程数默认为8** - 但可通过MigrationConfig.RobocopyThreads配置，大文件复制时可能需要调整
- **发布配置必须使用项目中的pubxml文件** - 不要直接使用dotnet publish命令，确保正确的压缩和优化设置
- **中文字符串在代码中保持原样** - UI文本使用中文，不要硬编码英文