# 使用说明文字资源管理方案

## 📋 概述

为了方便统一管理和维护使用说明文字，我们将所有使用说明相关的文本内容集中存放在资源字典文件中。

## 📁 文件结构

```
MoveWithSymlinkWPF/
├── App.xaml                          # 应用程序主文件,已注册资源字典
├── Resources/
│   └── UserGuideTexts.xaml          # 使用说明文字资源字典 ⭐核心文件
├── Views/
│   └── UserGuideWindow.xaml         # 使用说明窗口,引用资源
└── MainWindow.xaml                  # 主窗口,引用资源
```

## ✨ 核心文件说明

### UserGuideTexts.xaml
这是统一管理所有使用说明文字的资源字典文件,包含以下内容:

#### 1. 模式说明
- `MigrationModeTitle` - 迁移模式标题
- `MigrationModeDescription` - 迁移模式说明
- `RestoreModeTitle` - 还原模式标题
- `RestoreModeDescription` - 还原模式说明

#### 2. 快速提示
- `MigrationModeTip1/2/3` - 迁移模式提示
- `RestoreModeTip1/2/3` - 还原模式提示

#### 3. 软件概述
- `WhatIsThisTool` - 什么是目录迁移工具
- `ToolIntroduction` - 工具介绍
- `CoreAdvantage1-4` - 核心优势
- `UseCase1-5` - 适用场景
- `TechnicalPrinciple` - 技术原理

#### 4. 手动模式步骤
- `Step1/2/3/4Title` - 各步骤标题
- `Step1/2/3/4Action*` - 各步骤操作说明
- `Step3Warning*` - 注意事项

#### 5. 一键迁移
- `QuickMigrateTitle/Description` - 一键迁移说明
- `QuickStart1-4` - 快速上手步骤
- `ConfigFile*` - 配置文件说明
- `AdvancedFeature1-4` - 高级功能

#### 6. 特殊模式
- `RestoreModeGuide*` - 还原模式详细说明
- `RepairMode*` - 修复模式说明
- `SmartDetection*` - 智能检测说明

#### 7. 注意事项
- `PermissionWarning*` - 权限要求
- `DataSafety*` - 数据安全
- `DiskSpace*` - 磁盘空间
- `FileOccupation*` - 文件占用
- `SpecialCase*` - 特殊情况

#### 8. 常见问题
- `FAQ1Q/A` 到 `FAQ8Q/A` - 8个常见问题及答案
- `NeedHelp*` - 帮助信息

## 🔧 如何修改文字内容

### 方法一:直接编辑资源文件(推荐)

1. 打开 `Resources/UserGuideTexts.xaml`
2. 找到要修改的资源Key
3. 修改对应的文字内容
4. 保存文件

**示例:**

修改前:
```xml
<sys:String x:Key="MigrationModeTip1">• 确保目标磁盘有足够空间</sys:String>
```

修改后:
```xml
<sys:String x:Key="MigrationModeTip1">• 请确保目标磁盘有充足的可用空间</sys:String>
```

### 方法二:在Visual Studio中使用设计器

1. 在Solution Explorer中双击 `UserGuideTexts.xaml`
2. 使用查找功能(Ctrl+F)定位到要修改的文字
3. 直接修改文字内容
4. 保存即可

## 📍 如何添加新的文字资源

如果需要添加新的使用说明内容:

1. 在 `UserGuideTexts.xaml` 中添加新的资源:
```xml
<sys:String x:Key="YourNewKey">你的新文字内容</sys:String>
```

2. 在需要使用的XAML文件中引用:
```xml
<TextBlock Text="{StaticResource YourNewKey}"/>
```

## 🎯 优势与好处

### ✅ 统一管理
- 所有使用说明文字集中在一个文件中
- 修改一次,多处生效
- 避免重复维护

### ✅ 易于维护
- 文字内容与界面布局分离
- 方便批量修改和查找
- 降低出错几率

### ✅ 支持多语言
- 可以轻松扩展为多语言支持
- 只需创建不同语言的资源文件
- 运行时切换语言

### ✅ 版本控制友好
- 文字修改历史清晰可追溯
- 减少合并冲突
- 便于代码审查

## 🔍 使用位置说明

### MainWindow.xaml
- 步骤1的模式说明区域
- 使用模式标题、说明和快速提示

### UserGuideWindow.xaml
- 完整的使用说明窗口
- 包含所有标签页的内容
- 软件概述、手动模式、一键迁移、特殊模式、注意事项、常见问题

## ⚠️ 注意事项

1. **资源Key不要随意改动**
   - 如果改动Key,需要同步修改所有引用该Key的地方
   - 建议只修改Value(文字内容),不要修改Key

2. **保持资源文件格式正确**
   - 确保XML格式正确
   - 注意特殊字符需要转义(如 `<` 要写成 `&lt;`)

3. **测试修改结果**
   - 修改后运行程序检查显示效果
   - 确保所有引用的地方都正确显示

4. **备份重要修改**
   - 大批量修改前建议先备份文件
   - 使用版本控制系统(Git)管理修改历史

## 🚀 快速查找技巧

### 在Visual Studio中:
- 按 `Ctrl+Shift+F` 全局搜索
- 输入关键字快速定位到资源定义

### 在VSCode中:
- 按 `Ctrl+P` 快速打开文件
- 输入 `UserGuideTexts.xaml`
- 按 `Ctrl+F` 在文件内搜索

## 📝 示例:修改迁移模式说明

**需求:** 将迁移模式的说明文字改得更详细

**步骤:**

1. 打开 `Resources/UserGuideTexts.xaml`
2. 找到 `MigrationModeDescription`:
```xml
<sys:String x:Key="MigrationModeDescription">将源目录的数据迁移到目标位置，并在源位置创建符号链接指向新位置。程序可以透明访问，无需修改配置。</sys:String>
```

3. 修改为:
```xml
<sys:String x:Key="MigrationModeDescription">将源目录的数据完整迁移到目标位置，并在源位置创建符号链接指向新位置。迁移后,所有程序都可以透明访问新位置的文件,无需修改任何配置。整个过程安全可靠,支持自动备份和回滚。</sys:String>
```

4. 保存文件
5. 运行程序,查看MainWindow和UserGuideWindow中的显示效果

**结果:**
- MainWindow的步骤1模式说明区域会显示新文字
- UserGuideWindow中引用该资源的地方也会自动更新

## 🎨 最佳实践

1. **文字简洁明了**
   - 避免过长的文字
   - 使用简短、易懂的语句

2. **保持一致性**
   - 相同概念使用相同的术语
   - 风格保持统一

3. **合理分组**
   - 资源文件中已按功能分组
   - 新增资源时放在对应的分组中

4. **添加注释**
   - 必要时为资源添加注释说明用途
   ```xml
   <!-- 这是用于主界面步骤1的迁移模式说明 -->
   <sys:String x:Key="MigrationModeDescription">...</sys:String>
   ```

---

**作者:** 诏无言
**项目:** 玲珑星核 - Windows 符号链接迁移方案
**更新日期:** 2025-12
