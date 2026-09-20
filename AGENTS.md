# Sidekick / TAO 中文版 —— 本项目纪律（派活方与实现/审计方的约定）

## 硬性

- **不许 `git add` / `commit` / `tag`**：提交、打标签、出包都由派活方做。
- **不许动 `poe2-work/gen-buildtarget-resx.py`**（resx 的唯一真源由派活方同步）。你要加资源键就**直接写两份 resx**，
  并在报告里列 `(键名, en, zh)` 表格。
- **`.cs` 文件行尾必须与原文件一致**（BOM / CRLF 敏感）。改完自查：
  `python -c "raw=open(p,'rb').read(); print(raw.count(b'\r\n'), raw.count(b'\n')-raw.count(b'\r\n'))"`，混行要归一。
- **不要碰 `%APPDATA%\sidekick`**（用户模板库）。真机测试一律用副本数据目录。
- 审计类任务**只读**：唯一落盘产物是报告，不改产品代码 / 测试 / 数据。

## 报告

- 写到**仓库根**，文件名按任务里给的；结论三档：**阻断 / 应该修 / 建议**，每条给「文件:行 + 该改成什么」。
- 「没找到就写没找到」，不许为凑数硬提；冷门事实标「推测」+ 验证方式。
- 结尾附：`git status --porcelain`、你实际跑了什么（`dotnet` 是否被沙箱拦）。

## 测试与验收

- **断言合同**：后置条件（数值 / 关系 / 方向），不要只断言标签（枚举、key、布尔）。
- 新增功能要造**触发档样本**；「默认路径逐位不变」**不等于**功能生效。
- 自跑：`dotnet build src/Sidekick.Avalonia -c Release`（0 错）+ `dotnet test tests/Sidekick.Modules.BuildTarget.Tests`
  （当前 **351** 项全过）。沙箱跑不了就写清楚并用反射 runner 兜底，派活方会在 Windows 侧复跑。

## 环境坑（都踩过）

- `dotnet` 走绝对路径：`export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"`。
- msys 路径（`/c/...`）不能传给原生 exe（`luajit.exe` 会 exit 127）：用 `C:/...` 或相对路径。
- Windows 侧 `dotnet build` 会把 `obj/project.assets.json` 的包目录改回 Windows nuget 路径 →
  WSL 里紧随其后的 `--no-restore` 构建报 `NETSDK1064`，重跑一次 restore 即可。
- **派活期间派活方不动这棵树**（撞车过一次，两边都白干）。

## 两条判定轴（不要合并）

- `VerdictDecider`：「按你的门槛 + 加法词缀」判。
- `ItemVerdictDecider`：「按引擎数值（DPS / EHP + 抗性上限守门）」判。
  界面上要能看出结论出自哪条轴。
