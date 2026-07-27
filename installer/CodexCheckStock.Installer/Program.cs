using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.SqlClient;

[assembly: SupportedOSPlatform("windows")]

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    if (!OperatingSystem.IsWindows())
    {
        Fail("该安装器只能在 Windows 服务器上运行。");
        return;
    }

    if (!IsAdministrator())
    {
        Fail("请右键以管理员身份运行安装器，否则无法配置 IIS。");
        return;
    }

    Console.WriteLine("Codex PDA 盘点 IIS 安装器");
    Console.WriteLine("----------------------------------------");

    var mode = args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        ? "2"
        : Prompt("请选择操作：1=安装/更新，2=一键卸载", "1");

    if (mode == "2")
    {
        var uninstallDir = Prompt("安装目录", @"C:\inetpub\codex-check-stock");
        var uninstallSite = Prompt("IIS 站点名称", "CodexCheckStock");
        var uninstallPool = Prompt("IIS 应用池名称", uninstallSite);
        var confirmed = PromptYesNo($"确认卸载站点 {uninstallSite} 并删除目录 {uninstallDir}", false);
        if (!confirmed)
        {
            Console.WriteLine("已取消卸载。");
            return;
        }

        Console.WriteLine("正在卸载 IIS 站点和应用池...");
        UninstallIis(uninstallSite, uninstallPool, uninstallDir, deleteInstallDir: true);
        Console.WriteLine("卸载完成。数据库中的 PDA 表和历史数据未删除。");
        return;
    }

    var sqlServer = Prompt("SQL Server 实例", ".");
    var database = Prompt("数据库名", "hh2j1332");
    var useWindowsAuth = PromptYesNo("使用 Windows 身份验证连接数据库", true);
    string connectionString;
    if (useWindowsAuth)
    {
        connectionString = $"Server={sqlServer};Database={database};Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True;";
    }
    else
    {
        var sqlUser = Prompt("SQL 用户名", "sa");
        var sqlPassword = PromptSecret("SQL 密码");
        connectionString = $"Server={sqlServer};Database={database};User ID={sqlUser};Password={sqlPassword};Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True;";
    }

    var installDir = Prompt("安装目录", @"C:\inetpub\codex-check-stock");
    var port = PromptInt("IIS 端口", 5188);
    var siteName = Prompt("IIS 站点名称", "CodexCheckStock");
    var appPoolName = Prompt("IIS 应用池名称", siteName);

    Console.WriteLine();
    Console.WriteLine("正在测试数据库连接...");
    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
    }

    Console.WriteLine("正在解压站点文件...");
    Directory.CreateDirectory(installDir);
    var tempDir = Path.Combine(Path.GetTempPath(), "codex-check-stock-install-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        ExtractPayload(tempDir);
        CopyDirectory(tempDir, installDir);
    }
    finally
    {
        TryDeleteDirectory(tempDir);
    }

    Console.WriteLine("正在写入数据库连接配置...");
    WriteAppSettings(installDir, connectionString);

    Console.WriteLine("正在初始化 PDA 数据库对象...");
    await RunSqlScripts(installDir, connectionString);

    Console.WriteLine("正在配置 IIS 站点...");
    ConfigureIis(siteName, appPoolName, installDir, port);

    Console.WriteLine("正在生成一键卸载脚本...");
    WriteUninstaller(installDir, siteName, appPoolName);

    Console.WriteLine();
    Console.WriteLine("安装完成。");
    Console.WriteLine($"访问地址：http://localhost:{port}/");
    Console.WriteLine($"一键卸载：{Path.Combine(installDir, "一键卸载.bat")}");
    Console.WriteLine("如需 PDA 访问，请使用服务器局域网 IP 加端口。");
}
catch (Exception ex)
{
    Console.WriteLine();
    Fail(ex.Message);
}

static string Prompt(string label, string defaultValue)
{
    Console.Write($"{label} [{defaultValue}]: ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}

static int PromptInt(string label, int defaultValue)
{
    while (true)
    {
        var value = Prompt(label, defaultValue.ToString());
        if (int.TryParse(value, out var result) && result > 0 && result <= 65535) return result;
        Console.WriteLine("请输入 1-65535 之间的端口。");
    }
}

static bool PromptYesNo(string label, bool defaultValue)
{
    var suffix = defaultValue ? "Y/n" : "y/N";
    Console.Write($"{label} [{suffix}]: ");
    var value = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
    if (value.Length == 0) return defaultValue;
    return value is "y" or "yes" or "是";
}

static string PromptSecret(string label)
{
    Console.Write($"{label}: ");
    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return buffer.ToString();
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length <= 0) continue;
            buffer.Length--;
            Console.Write("\b \b");
            continue;
        }
        buffer.Append(key.KeyChar);
        Console.Write("*");
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void ExtractPayload(string targetDir)
{
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream("payload.zip")
        ?? throw new InvalidOperationException("安装包缺少站点资源 payload.zip，请重新打包。");
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    archive.ExtractToDirectory(targetDir, overwriteFiles: true);
}

static void CopyDirectory(string sourceDir, string targetDir)
{
    foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(dir.Replace(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase));
    }

    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        var target = file.Replace(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void WriteAppSettings(string installDir, string connectionString)
{
    var json = """
        {
          "ConnectionStrings": {
            "Default": "__CONNECTION__"
          },
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning"
            }
          },
          "AllowedHosts": "*"
        }
        """.Replace("__CONNECTION__", EscapeJson(connectionString));

    File.WriteAllText(Path.Combine(installDir, "appsettings.json"), json, new UTF8Encoding(false));
}

static void WriteUninstaller(string installDir, string siteName, string appPoolName)
{
    var script = string.Join(Environment.NewLine, new[]
    {
        "@echo off",
        "chcp 65001 >nul",
        "net session >nul 2>&1",
        "if %errorlevel% neq 0 (",
        "  powershell -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process -FilePath '%~f0' -Verb RunAs\"",
        "  exit /b",
        ")",
        "",
        "echo 正在卸载 Codex PDA 盘点...",
        "set \"APPCMD=%windir%\\System32\\inetsrv\\appcmd.exe\"",
        "if exist \"%APPCMD%\" (",
        $"  \"%APPCMD%\" stop site /site.name:\"{EscapeCmd(siteName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" delete site \"{EscapeCmd(siteName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" stop apppool /apppool.name:\"{EscapeCmd(appPoolName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" delete apppool \"{EscapeCmd(appPoolName)}\" >nul 2>&1",
        ")",
        "",
        "echo IIS 站点和应用池已删除。",
        "echo 数据库中的 PDA 表和历史数据未删除。",
        "echo 正在删除安装目录...",
        "cd /d \"%TEMP%\"",
        $"start \"\" cmd /c \"timeout /t 3 /nobreak >nul & rmdir /s /q \"\"{EscapeCmd(installDir)}\"\"\"",
        "exit /b",
        "",
    });

    File.WriteAllText(Path.Combine(installDir, "一键卸载.bat"), script, Encoding.Default);
}

static string EscapeJson(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

static string EscapeCmd(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);

static async Task RunSqlScripts(string installDir, string connectionString)
{
    var sqlDir = Path.Combine(installDir, "sql");
    if (!Directory.Exists(sqlDir)) return;

    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    foreach (var file in Directory.GetFiles(sqlDir, "*.sql").OrderBy(Path.GetFileName))
    {
        var script = await File.ReadAllTextAsync(file, Encoding.UTF8);
        foreach (var batch in SplitSqlBatches(script))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandTimeout = 120;
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

static IEnumerable<string> SplitSqlBatches(string script)
{
    var batch = new StringBuilder();
    using var reader = new StringReader(script);
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            yield return batch.ToString();
            batch.Clear();
        }
        else
        {
            batch.AppendLine(line);
        }
    }
    if (batch.Length > 0) yield return batch.ToString();
}

static void ConfigureIis(string siteName, string appPoolName, string installDir, int port)
{
    var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var appcmd = Path.Combine(systemRoot, "System32", "inetsrv", "appcmd.exe");
    if (!File.Exists(appcmd))
    {
        throw new InvalidOperationException("未检测到 IIS。请先安装 IIS 和 ASP.NET Core Hosting Bundle。");
    }

    var moduleCheck = RunProcess(appcmd, "list module /name:AspNetCoreModuleV2", throwOnError: false);
    if (moduleCheck.ExitCode != 0)
    {
        throw new InvalidOperationException("IIS 未安装 ASP.NET Core Module V2。请先安装 .NET 8 Hosting Bundle 后再运行安装器。");
    }

    if (RunProcess(appcmd, $"list apppool /name:\"{appPoolName}\"", throwOnError: false).ExitCode != 0)
    {
        RunProcess(appcmd, $"add apppool /name:\"{appPoolName}\"");
    }
    RunProcess(appcmd, $"set apppool /apppool.name:\"{appPoolName}\" /managedRuntimeVersion:\"\"");

    if (RunProcess(appcmd, $"list site /name:\"{siteName}\"", throwOnError: false).ExitCode != 0)
    {
        RunProcess(appcmd, $"add site /name:\"{siteName}\" /bindings:http/*:{port}: /physicalPath:\"{installDir}\"");
    }
    else
    {
        RunProcess(appcmd, $"set site /site.name:\"{siteName}\" /bindings:http/*:{port}:");
        RunProcess(appcmd, $"set vdir \"{siteName}/\" /physicalPath:\"{installDir}\"");
    }

    RunProcess(appcmd, $"set app \"{siteName}/\" /applicationPool:\"{appPoolName}\"");
    RunProcess("icacls", $"\"{installDir}\" /grant \"IIS AppPool\\{appPoolName}\":(OI)(CI)RX /T", throwOnError: false);
    RunProcess(appcmd, $"start apppool /apppool.name:\"{appPoolName}\"", throwOnError: false);
    RunProcess(appcmd, $"start site /site.name:\"{siteName}\"", throwOnError: false);
}

static void UninstallIis(string siteName, string appPoolName, string installDir, bool deleteInstallDir)
{
    var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var appcmd = Path.Combine(systemRoot, "System32", "inetsrv", "appcmd.exe");
    if (File.Exists(appcmd))
    {
        RunProcess(appcmd, $"stop site /site.name:\"{siteName}\"", throwOnError: false);
        RunProcess(appcmd, $"delete site \"{siteName}\"", throwOnError: false);
        RunProcess(appcmd, $"stop apppool /apppool.name:\"{appPoolName}\"", throwOnError: false);
        RunProcess(appcmd, $"delete apppool \"{appPoolName}\"", throwOnError: false);
    }

    if (!deleteInstallDir || !Directory.Exists(installDir)) return;

    var normalizedInstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var executableDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (executableDir.StartsWith(normalizedInstallDir, StringComparison.OrdinalIgnoreCase))
    {
        ScheduleDirectoryDelete(normalizedInstallDir);
        Console.WriteLine("安装目录将在安装器退出后自动删除。");
        return;
    }

    Directory.Delete(normalizedInstallDir, recursive: true);
}

static void ScheduleDirectoryDelete(string installDir)
{
    var cmd = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{installDir}\"";
    var process = new Process();
    process.StartInfo = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = cmd,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    process.Start();
}

static (int ExitCode, string Output) RunProcess(string fileName, string arguments, bool throwOnError = true)
{
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    process.Start();
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (throwOnError && process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{Path.GetFileName(fileName)} 执行失败：{output}");
    }
    return (process.ExitCode, output);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
    catch
    {
        // 临时目录清理失败不影响安装结果。
    }
}

static void Fail(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("安装失败：" + message);
    Console.ResetColor();
    Console.WriteLine("按回车键退出...");
    Console.ReadLine();
}
