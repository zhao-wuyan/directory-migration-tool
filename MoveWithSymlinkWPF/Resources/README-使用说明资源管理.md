# 使用说明文字资源管理方案

## 概述

为了方便统一管理和维护使用说明与主界面模式说明文案，所有相关文本均集中存放在 `Resources/UserGuideTexts.xaml` 资源字典中。

## 文件结构

```
MoveWithSymlinkWPF/
├── App.xaml                              # 应用程序入口，合并 FluentTheme / AppIcon / UserGuideTexts
├── Resources/
│   ├── FluentTheme.xaml                  # 统一轻量 Fluent 视觉令牌、Lucide 矢量图标与按钮样式
│   ├── AppIcon.xaml                      # 应用矢量图标资源
│   └── UserGuideTexts.xaml               # 使用说明与模式提示文字资源字典
├── Views/
│   ├── UserGuideWindow.xaml              # 使用说明弹框（左侧导览 + 右侧 Fluent 卡片布局）
│   └── UserGuideWindow.xaml.cs           # 章节切换、动态版本号与快捷键交互
└── MainWindow.xaml                       # 主窗口（步骤 1 模式说明复用本字典部分 Key）
```

## 核心分组说明（UserGuideTexts.xaml）

1. **主窗口共用模式说明**（`MainWindow.xaml` 直接引用，**请勿修改 Key**）：
   - `MigrationModeTitle` / `MigrationModeDescription` / `MigrationModeTip1–3`
   - `RestoreModeTitle` / `RestoreModeDescription` / `RestoreModeTip1–3`
   - `RepairModeTitle` / `RepairModeDescription` / `RepairScenario1–3`
2. **弹窗头部与导览**：`GuideWindowTitle`、`GuideHeaderTitle`、`GuideHeaderSubtitle`、`GuideSafetyPillText`
3. **第 1 节 · 软件概述**：`Overview*`、`WhatIsThisTool`、`ToolIntroduction`、`CoreAdvantage*`、`ModulesOverviewTitle`、`Module*`、`UseCase*`、`TechnicalPrinciple*`
4. **第 2 节 · 手动 / 还原 / 修复**：`ManualSection*`、`Step1–4*`、`RestoreModeGuide*`、`RestoreProcess*`、`Repair*`
5. **第 3 节 · 一键迁移**：`QuickMigrate*`、`QuickStart*`、`ConfigFile*`、`ConfigType*`、`AdvancedFeature*`
6. **第 4 节 · 磁盘扫描**：`DiskScanSection*`、`DiskScanEngine*`、`DiskScanViews*`、`DiskScanHardlink*`、`DiskScanQueue*`
7. **第 5 节 · 安全垃圾清理**：`CleanupSection*`、`CleanupTier*`、`CleanupChannel*`、`CleanupAiResidual*`
8. **第 6 节 · AI 助手**：`AiSection*`、`AiEntry*`、`AiModel*`、`AiPrivacy*`
9. **第 7 节 · 备份与安全机制**：`SafetySection*`、`SafetyPolicyTitle`、`SafetyL0–L3*`、`BackupCenter*`、`AclAndRollback*`
10. **第 8 节 · 注意事项与 FAQ**：`NoticesFaq*`、`Permission*`、`DiskSpace*`、`FileOccupation*`、`FAQ1–7Q/A`、`NeedHelp*`

## 维护注意事项

1. **保持共用 Key 稳定**：`MainWindow.xaml` 引用的模式说明与提示 Key 只修改文本内容，不随意改名。
2. **XML 转义规范**：字符串中如出现 `<`、`>`、`&` 须分别转义为 `&lt;`、`&gt;`、`&amp;`。
3. **测试宿主资源 URI**：窗口图标或独立拆出的 `ResourceDictionary` 使用 `pack://application:,,,/目录迁移工具;component/Resources/...` 绝对 pack URI，确保在主程序与单元测试宿主中均可解析。
