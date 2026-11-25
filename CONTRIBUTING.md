# 如何为 PCL-DreamPort 做出贡献

> **重要：** 提交 Issue 或 Pull Request 前，请确保改动符合本仓库的双许可要求（`PCL.Launcher/` 与 `PCL.Launcher.Core/` 适用 [PCL 自定义协议](PCL.Launcher.Core/LICENCE)，其余目录适用 [Apache License 2.0](LICENSE)），并如实说明生成式 AI 的辅助情况及本地验证结果。

## 开始之前

查看 [Issues](https://github.com/XiamengYaro/PCL-DreamPort/issues) 寻找可以参与的任务，或创建新 Issue 讨论你的想法。请勿将本分支的问题反馈到原版 PCL 或 PCL CE 仓库。

## 贡献流程

### 报告问题

1. 在提交 Issue 前，请先搜索是否已有相关 Issue
2. 使用提供的 Issue 模板
3. 包含以下信息：
   - 清晰的问题描述
   - 复现步骤（包括环境与版本信息）
   - 预期与实际行为对比
   - 相关日志/截图（如有）

### 提交代码

1. Fork 仓库并克隆到本地，切入 `dev` 分支：

   ```bash
   git clone https://github.com/XiamengYaro/PCL-DreamPort.git
   git checkout dev
   ```

2. 创建你的功能或修复分支：

   ```bash
   git checkout -b feat/your-feat-name
   # 或
   git checkout -b fix/issue-number-desc
   ```

3. 遵循双许可与无彩色 emoji 规范进行编写
4. 本地完成构建与测试验证后再提交：

   ```bash
   dotnet build "PCL Community Edition.slnx" -c Debug
   dotnet test DreamPort.Test/DreamPort.Test.csproj -c Debug
   dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Debug
   ```

5. 使用 Conventional Commits 提交信息格式 `<type>(scope?): <subject>`（如 `feat`、`fix`、`refactor`、`docs`、`test`、`chore`）
6. 创建指向 `XiamengYaro/PCL-DreamPort` 仓库 `dev` 分支的 Pull Request，附上修改说明与本地验证记录
