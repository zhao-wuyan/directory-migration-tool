# 配置、官方能力与项目同步

## 官方指导的边界

原生机制优先读取当前安装的官方 Skill；以下链接用于核对，不将某个在线版本强加给旧部署：

- [OpenViking Agent Plugins](https://github.com/volcengine/OpenViking/blob/main/agent-plugins/README.md)
- [原生 Memory](https://github.com/volcengine/OpenViking/blob/main/agent-plugins/skills/openviking-memory/SKILL.md)
- [原生 Experience](https://github.com/volcengine/OpenViking/blob/main/agent-plugins/skills/ov-experience-memory/SKILL.md)
- [Resource 检索与资料摄入](https://github.com/volcengine/OpenViking/blob/main/agent-plugins/skills/ov-resources/SKILL.md)

原生 `/memories` 包括偏好、实体、事件，也可有经验；使用官方 `remember` / 会话采集机制，让服务分类，不手写 Memory 子目录。原生 `/memories/experiences` 的召回、读取和会话关联遵循官方 Experience 指导。Evolve 不接管官方自动提取、不关闭其 hooks、不改官方工具参数或凭据解析顺序。

教程、论文、URL、仓库等资料使用已注册的 `add_resource` 或当前部署的官方导入入口。先查询现有 Resource 布局、共享与私有范围及重复来源，再确定目的地。官方部署可能使用 `viking://resources/` 或 `viking://user/<user_id>/resources/`；不预设所有部署具有相同全局目录。工具缺失时说明缺少摄入能力，不能把资料塞进 `remember` 或 Evolve 经验候选来冒充资料导入成功。

## 实际路径与身份

| 对象 | 路径 / 配置 | 权威来源 |
| --- | --- | --- |
| Evolve 项目资产 | `viking://user/<user_id>/resources/evolve/projects/<project_id>` | `.evolve/openviking.json` 的 `root_uri` 与服务身份校验 |
| Evolve 跨项目个人经验 | `viking://user/<user_id>/resources/evolve/personal-experience` | 用户经验配置与连接身份 |
| 原生 Memory / Experience | 当前部署返回的 `/memories/` URI | 官方 Skill、真实工具结果 |
| 本机用户全局 Wiki | `~/.evolve/wiki/entries/` | `evolve kb ... --scope global` |
| 项目 Wiki / 工作记忆 | `<project>/wiki/`、`<project>/.evolve/memory.db` | `evolve kb` |

Evolve 连接使用绝对路径 `EVOLVE_OPENVIKING_CONFIG`，否则读取 `~/.openviking/ovcli.conf` 的 `url` 与 `api_key`。这个规则只描述 Evolve；原生插件可能有自己的配置优先级，不能推断二者一定连接同一个 endpoint / account / user。只核对必要的非敏感身份信息，不展示凭据或完整配置。

身份取自服务校验、项目配置和当前工具返回的 canonical URI，不硬编码历史用户 ID。`status` 成功连接不等于用户目标已确定，也不证明命名空间共享范围或写入权限。不同状态命令的字段只按实际含义解释。

## 项目初始化与运行

先确认当前安装具备命令：

```text
evolve openviking --help
evolve experience --help
evolve openviking status
evolve experience status
```

源码仓库内可将 `evolve` 替换为 `node dist/workflow-cli.js`。命令不可用说明安装版本或入口有差异，不能为了执行说明而修改安装包。

项目没有 `.evolve/openviking.json` 时使用本地知识，不自动读取 Evolve 连接或触网。只有用户要求开启项目资产同步时，运行当前版本支持的 `evolve openviking init`；不要把它和 `experience enable` 混为一谈。当前集成在缺失时准备 `.evolve/evolution.json` 的空策略；空 `targets` 只同步资产，不自动修改 Skill。需要策略时先查真实 Skill、runner 和 scenarios，再预览，按当前宿主审批入口应用，不猜测试脚本。

Session 开始同步并固定 release；用现有 Session/Run 流程操作，保留身份、manifest 和内容哈希校验。不得为同步擅自新建 Session，不能把别的 Run / release 的内容当成当前证据。Session 完成时的项目资产发布不等于本机 Wiki 的所有后续修改都已上传。

`evolve openviking memory share|unshare <本地记忆 ID>` 只选择项目 release 中的共享记忆，不是向账户公开，也不是个人跨项目经验发布。选择需要对应同步授权，且不代表已经完成上传。

## 本地 Wiki 发布后的远端同步

正式 Wiki 的提案、审阅与发布先走 `ew-knowledge`。本地 `kb apply` 成功后，检查返回的 `openviking_sync` 提示；本地成功与远端成功分开报告。

对已在 Session 收尾后发布或无 Session 的 Wiki，使用独立同步入口：

```text
evolve openviking knowledge preview [entries/<id> ...]
evolve openviking knowledge publish --approval <预览哈希>
```

`preview` 只读远端、不上传；检查精确正文、条目范围、目的地和远端版本。按用户已有的具体同步授权或本次固定预览确认发布，不因已授权本地 Wiki 发布就擅自扩大远端范围。发布保留远端 Skill、Memory 及未选条目；目的地、内容或远端版本变化时重新预览。不重建 Session，也不靠反复 `finish` 上传收尾后的新 Wiki。

完成后回读发布结果所指的 release 与条目、核对哈希；只读 status 不足以证明指定正文上传成功。具体返回 ID 与状态以工具结果为准。

## 宿主工具适配

先发现实际注册的能力，再按其 schema 调用；不要照抄其他宿主名称或追加不支持的 `target_uri` 等字段。

- 原生 OpenViking：可用时由官方 Skill 指导 `find/search/read/remember/add_resource`。只有明确存在的工具才可调用。
- Magpie MCP：当前 `magpie-plugin find(query)` 搜索可访问的 memories/resources/skills，`ls(uri)` 列目录，`read(uri)` 精读；`status()` 只报告连接与召回开关，不返回全局根。`remember(text)` 仅在用户要求记住时提交异步提取，不能指定精确存放路径或宣称立即持久化完成。缺少资料摄入能力时如实说明，不伪造 `add_resource`。
- Evolve OMP：`evolve_workflow` 的 argv 桥接按当前允许列表执行；个人经验只允许 `status/search/get`，项目 OpenViking 控制可能只允许 `policy`。写操作优先使用已注册的 `/evolve-experience`、`/evolve-openviking` 交互命令或具备授权的 CLI，不用外部插件绕过限制。项目知识精读使用现有 Evolve 工具才能记录 Evolve 回执；外部插件读取不自动构成 Evolve Run 的知识回执。

## 故障与恢复

- 未配置或关闭：保留本地工作，报告缺少哪一层配置；不要自动开启采集或同步。
- 网络不可用：遵循项目已有 `failure_policy`；本地 / cache 回退不能算远端写入成功，也不能代替精确 `ov:` ID 的远端正文。
- 身份、内容哈希、固定版本不匹配：停止本次远端操作，重新核对绑定与状态，不降低校验。
- 异步提取仅受理：报告“已提交提取”，待真实查询或任务状态证明完成再报告已保存。
- 发布结果未知：先读远端与现有回执，不能盲目重发或扩大条目范围。
