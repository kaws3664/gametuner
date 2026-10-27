# AGENTS.md — GameTuner (FrameSmith)

面向 007 First Light 的 Windows 个性化画质调优工具（WPF, .NET 9）。

## 构建与测试（交差前必须全绿）

```powershell
dotnet restore GameTuner.sln --configfile NuGet.Config
dotnet build GameTuner.sln --no-restore
dotnet test GameTuner.sln --no-build --no-restore
```

当前基线：构建 0 警告 0 错误，测试 45 通过 0 失败。

## 交差格式（prove-it）

```
做了什么：一句话
测试：<命令> → 通过 N，失败 0
改动文件：<列表>
没验证到的：哪些、为什么；没有就写「无」
```

## 规则表

| 规则 | 由什么拦住 |
|---|---|
| 交差前跑通测试 | `dotnet test GameTuner.sln --no-build` |
| UI 文案用简体中文，代码标识符用英文 | 人工review |
| 密钥只进 Windows 凭据管理器，不写进配置文件/仓库 | code review |
| 测试不得访问真实网络（用本地 HttpListener 假服务器） | code review |

## 外部事实（已实测，勿重踩）

- 用户在用的 LLM 网关：https://agentrouter.org （备用 https://ps.air-outer.com），OpenAI 兼容。
- 模型 `gpt-6.1-sol` **只支持 OpenAI Responses API**（POST /v1/responses）；调 /v1/chat/completions 会 400。
- 该网关做 UA 指纹检测：请求头 User-Agent 不是受支持的编程客户端（如 codex_cli_rs/、claude-cli/）时返回 401 `unauthorized client detected`。普通 HTTP 客户端默认 UA 会被拒。
- 推理模型默认 effort=max，会烧光 max_output_tokens 只输出推理；请求体需带 `"reasoning":{"effort":"low"}` 和足够的 `max_output_tokens`（≥300）。
- Prompt 含字面「连接成功」会被网关内容审查 400 content-blocked（只拦输入，模型输出没事）。
- 测试一律打本地假服务器，不许真调网关；真实连通性验证由人工会话单独执行。
- 网关只接受 CN/EN/FR/DE/RU 的正文：日文/韩文/西语/葡语等文本、base64 与二进制块会 400 content-blocked。
  发给模型的 prompt 里不要贴这类内容（含日文注释的代码先把注释译成英文）；发前跑 `python tools/agentrouter-preflight.py <路径>`。


## 语言与风格

- UI 文案：简体中文（与现有一致）。
- 代码、标识符、注释：英文为主，与现状一致。
- 不改动与本任务无关的文件；不提交 commit（由人工会话 review 后提交）。
