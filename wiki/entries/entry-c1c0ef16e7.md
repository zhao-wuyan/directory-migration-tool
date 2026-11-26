---
title: 声明式内嵌 JSON 规则引擎与安全红线分层约定
mode: constraint
status: active
scope:
  - project
sources:
  - MigrationCore/Resources/Rules/cleanup-rules.json
  - MigrationCore/Services/Cleanup/CleanupRuleLoader.cs
  - MigrationCore/Services/Safety/ProtectedPathVocabulary.cs
---

项目内清理规则、应用迁移规则与常见目录说明统一维护在 MigrationCore/Resources/Rules/*.json（cleanup-rules.json、application-rules.json、directory-explanations.json）作为 EmbeddedResource 嵌入程序集，通过轻量强类型加载器（CleanupRuleLoader 等）展开家族模板并在加载期执行安全不变式校验（ID 唯一、禁止 .. 穿越、浏览器规则强制 IsBrowserCacheRule、Caution 项必填 CautionReason）。严禁引入动态脚本/表达式规则引擎（如 RulesEngine/NRules/Roslyn Scripting）扩大管理员权限攻击面或增加单文件体积。L0 物理硬禁区（MigrationSafetyPolicy）与清理永久排除区（CleanupExclusionPolicy）的判定逻辑保持强类型 C# 不可外置，重叠敏感路径词表统一复用 ProtectedPathVocabulary（FrozenSet<string> 单一事实源）。
