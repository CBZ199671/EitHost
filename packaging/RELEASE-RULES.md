# EitHost 唯一发布目录规则

完成影响 EitHost 程序、运行配置或构建/打包流程的修改后，应构建对应版本并替换同一个日常运行目录。纯文档、注释或 agent 指令修改按其影响检查即可。该规则与根目录 `AGENTS.md` 一起执行。

唯一入口：`release/EitHost-Windows-x64/EitHost.App.exe`。
本工作区的绝对目录：`C:\Users\huolo\Desktop\EIT updata\EitHost\release\EitHost-Windows-x64`。

## 程序更新的完成要求

1. 完成当前任务中影响程序或打包的修改；应用版本统一维护在 `src/EitHost.App/EitHost.App.csproj`。程序行为更新至少递增补丁版本。
2. 运行与改动相关的测试和验证。
3. 在 Windows 项目根目录执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-eithost.ps1`。双击 `package.cmd` 或无参数调用 `scripts/publish-eithost.ps1` 等价。
4. 脚本从已保存的当前源码构建 Windows x64 自包含版本，执行 HDF5 包级检查，使用事务更新器替换唯一目录中的程序并验证 `Data` 保留，再核对安装后程序版本和运行库。
5. 核对 `VERSION.json`、`SHA256SUMS.txt`、`release/publish-manifest.json`。向用户只交付唯一目录中的 EXE 链接。若验证或替换失败，报告实际失败，不能把旧程序说成最新版本。

同一任务的多次编辑合并后发布一次；发布后再修改影响构建产物的内容，需要再次发布。无需每保存一个文件就构建。只有完成程序修改而没有生成对应版本，不算完成交付。

## 数据与历史

- `Data`、数据库、实验记录和用户配置必须保留。数据不混入分发包，不纳入 Git 版本管理，不通过清空整个目录来升级。
- 日常只维护一个可运行交付目录，不新建日期/版本后缀运行目录，不自动生成 ZIP、7z 或永久旧版本备份。构建中间目录位于系统临时目录并由脚本清理。
- 代码与程序历史使用 Git；不因本规则自动提交、推送或清理已有历史文件。
- `EitHost.App.exe` 本身不提交：自包含 EXE 约 141 MiB，超过 GitHub 单文件 100 MiB 硬上限，推送会被拒绝。该 EXE 通过 GitHub Release 附件分发，本地唯一目录仍由发布脚本正常更新。仓库继续跟踪 `VERSION.json`、`SHA256SUMS.txt`、`README.md`、随附 DLL 和脚本，接收方据此校验下载到的 EXE。
- `artifacts`、`dist` 和桌面诊断副本均不是最新上位机入口。只有用户明确要求对外归档时，才为 `package-eithost.ps1` 显式指定其他空的 `-OutputDirectory`；该模式只输出归档，不改变日常入口规则。
- 发布前正在运行的软件需要正常退出；不能为了替换 EXE 强行中断未保存的采集。

本规则属于用户对常规发布的持续授权，不需要每次重复请求发布许可。具体执行方法见 `开发与分发.md`。
