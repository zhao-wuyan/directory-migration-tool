# 个人跨项目经验

## 已确认的设计

用户选择的是“让 AI 自动积累各项目中验证过的经验”，而非自动抓取教程建立资料库。全局指同一用户跨项目复用；Java、Python、Go、前端等是领域分类，不分别建立平行系统。

流程为：授权仓库采集 → 当前任务 AI 提炼真实验证结论 → 完成 Session 后列入本地待审 → 定期审阅并确认固定批次 → 发布 → 其他项目按需检索。不开额外模型来给成功结果编造原因，不以单次测试通过证明未经验证的因果。

## 存放与配置

```text
viking://user/<实际 user_id>/resources/evolve/personal-experience/
  <domain>/<SHA256(stack)>/<经验 ID>.md
```

这是 Evolve 自定义私有 Resource，不是官方原生 `/memories/experiences`，也不是项目 release。用户与账户由现有连接身份确认，代码生成 URI、技术栈目录哈希和经验 ID；不要手工拼造实际目的地或写入文件。

采集名单保存在 `~/.evolve/experience.json`（测试可用绝对路径 `EVOLVE_EXPERIENCE_CONFIG` 覆盖），连接复用 `~/.openviking/ovcli.conf` 或 `EVOLVE_OPENVIKING_CONFIG`。不要求用户手写 JSON，不复制 API key。

用户授权开启或关闭当前仓库后，在真实 Git 工作树根运行：

```text
evolve experience enable
evolve experience status
evolve experience disable
```

默认未配置；`enable` 只修改本机采集名单，不发布内容。`disable` 不删除远端经验。`status` 的 `configured` / `collecting` 只说明配置和采集状态，不能证明远端库是否有内容。未加入采集名单的仓库可查询已有经验，但当前实现仍需已有个人经验配置；未配置时先报告状态，不为只读查询擅自开启采集。

## 什么能成为候选

由执行验证或测试的 AI 根据实际通过的检查填写经验字段；候选必须来自已完成 Session 的封存验证产物，且仓库在采集名单中。保留 `domain`、带版本的 `stack`、标题、适用条件、方法或教训、验证摘要及可追溯检查证据。

- 可以：Python 某版本的异步服务中，同步 SDK 阻塞事件循环；实际复现并用并发检查验证替代方案。
- 可以：某版本 Java 框架在特定调用条件下事务未生效；明确验证过的调用边界和修复办法。
- 留在项目：仓库专属目录约定、未解决猜测、临时错误、部署地址、内部业务规则。
- 进入原生 Memory：用户明确要求记住的偏好、长期事实或决定；若官方提取经验，遵循官方机制，不强行搬迁。
- 进入资料库：教程、官方文档、论文等原文，保留来源；没有项目验证就不能包装成 Evolve 已验证经验。

当前格式要求版本化 `stack`，如 `python@3.12`；纯粹不依赖技术栈的通用原则不能伪造 `engineering@1` 等版本来过校验，可留在已有 Wiki / Resource 中。提炼前去除项目专属与敏感信息，不能上传原始聊天、内部代码、凭据或完整日志。

## 查询与复用

先确认当前项目技术栈与版本，再按任务需要调用：

```text
evolve experience search python python@3.12 "异步请求并发延迟"
evolve experience get python python@3.12 <搜索返回的经验 ID>
```

精读正文，核对适用条件、验证摘要和冲突关系，保留 URI / 正文哈希作为引用。查询只返回目标 domain 与 stack 的条目；不要把结果扩展成跨版本通则。结果是参考，不替代项目 Knowledge Gate 或项目约束，不全量注入所有领域经验。

## 审阅、发布与核实

```text
evolve experience pending
evolve experience review <候选 ID>
evolve experience review <候选 ID> --relation duplicate --related <已有经验 ID>
evolve experience review <候选 ID> --relation conflict --related <冲突经验 ID>
```

`pending` 读取允许采集的仓库及已完成证据，不上传。`review` 展示精确正文、目的地账户、建议关系、同栈相关项和审批哈希；相似项不是完整的重复或冲突证明。相同经验补验证来源，冲突保留双方，不自动覆盖。

复用用户对已展示的固定候选、关系与目标的授权；未有授权时定期批量呈现并确认，不在每个中间 Run 打断任务。审阅和拒绝不阻塞项目完成。批准后执行与预览完全一致的操作：

```text
evolve experience publish <候选 ID> --relation <new|duplicate|conflict> --approval <预览哈希>
```

预览带 `--related` 时发布也带同一 ID。内容、目的地或相关条目版本变化必须重新预览；`-y` 不产生授权。发布后用 `get` 回读返回的正式经验 ID 并核对内容，保留回执。失败或结果未知时先核对远端，不盲目重发。

宿主可能限制 Agent 只能调用 `status/search/get`；若当前宿主提供原生经验审阅和批准入口，应使用该入口。不得通过原生 Memory 写入或直接修改 URI 绕过受限制的 Evolve 发布流程。
