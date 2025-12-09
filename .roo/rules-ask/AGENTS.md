# Project Documentation Rules (Non-Obvious Only)

- **MigrationCore和MoveWithSymlinkWPF项目分工不明确** - MigrationCore包含业务逻辑，但也有一些UI相关的辅助类
- **quick-migrate.json配置作为嵌入资源** - 这个文件在根目录但实际上是作为资源嵌入到EXE中的
- **docs目录包含版本特定文档** - v1.0和v1.1子目录包含不同版本的详细说明，但不会随版本发布
- **PowerShell脚本使用UTF-8编码** - 所有.ps1文件都使用UTF-8编码，不是系统默认编码
- **发布配置在Properties/PublishProfiles目录** - 不是在标准的项目文件中，而是使用独立的pubxml文件
- **版本管理使用独立的JSON文件** - version.json是版本号的权威来源，.csproj文件会自动同步
- **调试信息只在Debug配置下输出** - 即使使用#if DEBUG，Release配置下也不会显示控制台输出
- **一键迁移配置文件路径不直观** - quick-migrate.json虽然在根目录，但实际作为资源嵌入到程序集中
- **符号链接检测逻辑复杂** - 使用ReparsePoint属性而不是标准API，因为标准API在某些情况下不可靠
- **文件复制进度映射非线性** - 复制阶段只占10%-90%，其他阶段固定分配剩余百分比
- **robocopy参数动态生成** - 不是固定的命令行，而是根据配置动态构建参数
- **应用名称包含中文** - 程序集名称是"目录迁移工具"，不是英文
- **WPF项目使用单文件发布** - 这是较新的.NET功能，传统WPF项目通常不使用单文件发布