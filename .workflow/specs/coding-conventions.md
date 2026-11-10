---
title: "Coding Conventions"
readMode: required
priority: high
category: coding
keywords:
  - style
  - naming
  - import
  - pattern
  - convention
  - formatting
---

# Coding Conventions

## Formatting

## Naming

## Imports

## Patterns

## Entries



<spec-entry category="coding" keywords="" date="2026-10-25" sid="S-20261025-dxz1" title="C# 与 MVVM 编码约定" sourceRef="MigrationCore/Services/MigrationService.cs;MigrationCore/Services/ReversibleMigrationService.cs;MoveWithSymlinkWPF/ViewModels/QuickMigrateViewModel.cs">

### C# 与 MVVM 编码约定

沿用现有四空格缩进、文件范围 namespace、PascalCase 类型与公开成员、_camelCase 私有字段；Nullable 与 ImplicitUsings 已启用。ViewModel 使用 CommunityToolkit.Mvvm 的 ObservableObject、ObservableProperty 与 RelayCommand；异步服务沿用 Task、IProgress 与 CancellationToken；注释和 UI 文本以中文为主，调试控制台输出使用 DEBUG 条件编译。

</spec-entry>