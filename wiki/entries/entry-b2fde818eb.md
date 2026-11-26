---
title: 超大类模块拆分边界、共享热点分析与 WPF 资源 URI 约定
mode: reference
status: active
scope:
  - project
sources:
  - MigrationCore/Services/Cleanup/CleanupService.cs
  - MigrationCore/Services/Scanning/ScanHotspotAnalyzer.cs
  - MoveWithSymlinkWPF/Views/QuickMigratePage.xaml
---

1）清理服务边界：CleanupService 保持薄门面，内部职责拆分为 CleanupRootPathResolver（策略表 + 单次扫描定位缓存）、CleanupRuleScanner（FileSystemName 零分配匹配 + 单次进程快照）、CleanupChannelAExecutor、CleanupOfficialApiHandlers 与 SystemToolRunner；2）共享热点分析：磁盘扫描 UI（DiskScanViewModel）与只读 AI 工具（ReadOnlyAiAnalysisToolset）的不重叠大目录热点采集统一委托 MigrationCore.Services.Scanning.ScanHotspotAnalyzer 单一事实源，禁止各自维护副本；3）超大 ViewModel 与 XAML 拆分：采用「独立子模型文件 + 主 ViewModel 按子视图/功能域 partial 拆分」保持 XAML 绑定路径零变更，超大 XAML 按子视图拆为独立 UserControl 或合并样式字典；4）WPF 测试宿主资源 URI：被拆出的独立 ResourceDictionary（如 Resources/QuickMigrateStyles.xaml）在页面中引用时必须使用 pack://application:,,,/目录迁移工具;component/Resources/... 绝对 pack URI，避免在 Phase1UnitTests（入口程序集非主应用）下相对路径解析失败。
