# Project Architecture Rules (Non-Obvious Only)

- **两套迁移服务共享核心逻辑** - MigrationService和ReversibleMigrationService都使用CopyOperationExecutor和MigrationWorkflowHelper
- **进度映射采用非线性分配** - 复制阶段占10%-90%，其他5个阶段固定分配剩余百分比，确保用户体验
- **符号链接创建的双重保障机制** - P/Invoke优先，cmd mklink回退，确保在不同Windows环境下都能工作
- **文件复制使用Robocopy而非.NET原生方法** - 因为Robocopy提供更好的进度报告和错误恢复机制
- **一键迁移配置作为嵌入资源** - quick-migrate.json不是外部配置文件，而是编译到程序集中
- **版本号分离管理策略** - version.json是权威来源，.csproj通过脚本自动同步，避免手动不一致
- **目录扫描包含大文件阈值计算** - 统计信息中特别标记超过阈值的大文件，影响复制策略
- **符号链接检测依赖文件属性而非API** - 使用ReparsePoint属性，因为FileSystem.IsSymbolicLink在某些情况下不可靠
- **WPF单文件发布的技术权衡** - 牺牲启动速度换取部署便利性，适合工具类应用
- **两种发布模式的服务对象不同** - 自包含版本面向最终用户，框架依赖版本面向内部测试
- **调试输出的条件编译策略** - 使用#if DEBUG而不是运行时检查，确保生产版本完全移除调试代码
- **备份目录的时间戳命名约定** - 使用固定格式的时间戳，便于脚本和工具识别和清理
- **Robocopy参数动态构建逻辑** - 根据线程数和文件大小阈值动态调整参数，优化复制性能
- **UI与业务逻辑的分离原则** - MigrationCore完全不依赖WPF，便于未来支持其他UI框架
- **取消操作的进程树终止机制** - 确保Robocopy及其子进程被正确终止，避免资源泄漏

## MVVM 架构约定

- **ViewModel与View职责严格分离** - ViewModel负责业务逻辑、数据处理、状态管理和命令暴露，不能包含UI元素引用（Popup、VisualTreeHelper等）；View负责UI事件处理、可视化树操作和调用ViewModel命令，不能重复实现ViewModel的业务逻辑
- **禁止View层重复实现业务逻辑** - 如果ViewModel已有某个业务方法（如复制到剪贴板），View层必须调用ViewModel的方法，而不能重新实现一遍相同逻辑，这样确保单一数据源和用户反馈的一致性