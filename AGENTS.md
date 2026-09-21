# EitHost 项目约定

- 唯一日常运行及交付入口：`release/EitHost-Windows-x64/EitHost.App.exe`。
- 完成影响程序、运行配置或构建/打包流程的修改后，运行相关验证，再在 Windows 项目根目录执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-eithost.ps1`，从当前源码更新该入口。常规本地更新已获持续授权；纯文档、注释或 agent 指令修改按其影响检查即可。
- 同一任务合并修改后更新一次；此后再改动构建产物相关内容，应重新更新。以 `src/EitHost.App/EitHost.App.csproj` 的 `Version` 为版本来源，程序行为变化至少递增补丁版本，确保 EXE、`VERSION.json` 和发布记录一致。
- 使用现有校验及事务更新器，保留 `Data`、实验数据库和用户配置；不清空安装目录、不把实验数据打入包、不强行中断未保存的采集。
- 日常只维护上述入口，不另建日期/版本运行目录或自动归档；对外分发仅使用用户指定目录。自包含 EXE 不纳入 Git，作为 Release 附件分发；随附清单和文件沿用仓库跟踪约定。
- 程序交付前确认版本、包校验、HDF5 自检及数据保留，运行链接指向唯一入口；失败时报告实际阻塞，不把旧程序称为最新版。本地打包授权不包含自动提交、推送或远端发布。
- 发布时按需查 `packaging/RELEASE-RULES.md` 与 `packaging/开发与分发.md`。
