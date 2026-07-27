# codex-check-stock

PDA 盘点 Web/API 项目，直接连接默认实例上的 `hh2j1332` 数据库。

## 运行 Web 端

```powershell
dotnet run --urls http://0.0.0.0:5188
```

默认连接串在 `appsettings.json`：

```text
Server=.;Database=hh2j1332;Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True;
```

管理员测试账号来自 ERP：`00000 / 123456`。

## 数据库对象

脚本：`sql/001_codex_pda_check_stock.sql`

新增对象使用 `CodexPda` 前缀：

- `CodexPdaCheckHeader`
- `CodexPdaCheckSubmit`
- `CodexPdaCheckSubmitDetail`
- `CodexPdaCheckedCountMap`
- `CodexPda_GetWarehouseCheck`

提交时会追加写入 ERP 原表 `CheckedCount`，不会修改 ERP 原表结构。

## APK 套壳

Android WebView 工程在 `android-shell`。APK 首次启动会进入服务器配置页，支持手动输入或扫码配置 IIS 地址。

服务器地址会保存在设备本地，后续启动直接进入软件，不需要因为 IIS 地址变化重新打包 APK。
