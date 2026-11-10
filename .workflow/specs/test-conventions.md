---
title: "Test Conventions"
readMode: required
priority: high
category: test
keywords:
  - test
  - coverage
  - mock
  - fixture
  - assertion
  - framework
---

# Test Conventions

## Framework

## Directory Structure

## Naming Conventions

## Patterns

## Entries



<spec-entry category="test" keywords="" date="2026-10-25" sid="S-20261025-318x" title="发布检查与文件安全验证边界" sourceRef="test/test-publish.ps1:14-39;README.md:117-124">

### 发布检查与文件安全验证边界

现有 test/test-publish.ps1 是交互式发布对比脚本，会实际执行两个发布流程，并非隔离单元测试。仅在需要验证发布时从仓库根目录运行该脚本。迁移、还原、修复与备份清理验证必须使用可丢弃目录，不得以用户真实目录作为烟测数据；初始化不运行迁移或发布。

</spec-entry>