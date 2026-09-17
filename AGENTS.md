# 本项目上位机发布规则（用户指定，必须遵守）

- 唯一日常运行及交付目录：`C:\Users\huolo\Desktop\EIT updata\EitHost\release\EitHost-Windows-x64`；仓库内相对路径为 `release/EitHost-Windows-x64`。
- 每次完成 EitHost 的修改或更新后，结束任务前必须运行与改动相关的验证，再执行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-eithost.ps1`，从当前已保存的源码重新构建并更新上述目录。不能只改源码、只运行 build，或只给出临时包路径便声称完成。
- 同一任务的修改合并后发布一次；发布后若又修改项目文件，必须再次发布。纯文档/规则更新也遵守这一完成要求。
- 应用版本以 `src/EitHost.App/EitHost.App.csproj` 中的 `Version` 为准；程序行为变更至少递增补丁版本，并使 EXE、`VERSION.json` 和发布记录一致。不要在发布脚本中硬编码旧版本号。
- 更新替换程序文件，保留现有 `Data`、实验数据库和用户配置；使用现有校验及事务更新器，禁止直接清空整个安装目录或把实验数据混入构建包。
- 日常禁止另建 `artifacts/.../EitHost.App*`、`dist/<版本或日期>`、桌面副本等上位机交付入口。中间构建只能放可清理的临时目录；历史版本交给 Git 管理，不自动生成归档包或持久备份目录。只有用户明确要求对外分发或另存时，才使用显式指定的归档输出目录。
- `package.cmd`、无参数 `scripts/package-eithost.ps1`、无参数 `scripts/publish-eithost.ps1` 都必须更新唯一日常目录。若无法构建、校验或替换，明确报告实际阻塞，不能把旧程序称为最新版。
- 交付前确认当前 EXE 版本、包文件校验、HDF5 包级自检和数据保留结果；最终给用户的运行链接只指向该目录内的 `EitHost.App.exe`。
- 详细流程见 `packaging/RELEASE-RULES.md` 和 `packaging/开发与分发.md`。本规则是用户已经授权的常规发布要求，无需在每次发布前重复请求许可。

