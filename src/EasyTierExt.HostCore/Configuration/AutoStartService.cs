using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace EasyTierExt.HostCore.Configuration;

/// <summary>
/// Manages Windows logon auto-start via Task Scheduler (HighestAvailable) so elevated
/// apps declaring requireAdministrator launch silently without UAC prompts.
/// </summary>
public static class AutoStartService
{
    public const string TaskName = "EasyTierWithExtension_AutoStart";
    public const string AutoStartArg = "--autostart";

    public static string BuildTaskXml(string exePath, string userId)
    {
        var workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
        var escapedExe = SecurityElement.Escape(exePath);
        var escapedDir = SecurityElement.Escape(workingDir);
        var escapedUser = SecurityElement.Escape(userId);

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedUser}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{escapedUser}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedExe}</Command>
      <Arguments>{AutoStartArg}</Arguments>
      <WorkingDirectory>{escapedDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }

    public static bool IsEnabled()
    {
        var (exitCode, _, _) = RunSchtasks(["/Query", "/TN", TaskName]);
        return exitCode == 0;
    }

    public static void SetEnabled(bool enabled, string? exePath = null)
    {
        if (enabled)
        {
            var resolvedExe = exePath
                ?? Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "EasyTierClient.exe");
            var userId = WindowsIdentity.GetCurrent().Name;
            var xml = BuildTaskXml(resolvedExe, userId);

            var tempXmlPath = Path.Combine(Path.GetTempPath(), $"easytier_autostart_{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(tempXmlPath, xml, Encoding.Unicode);
                var (exitCode, stdout, stderr) = RunSchtasks(["/Create", "/TN", TaskName, "/XML", tempXmlPath, "/F"]);
                if (exitCode != 0)
                {
                    var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    throw new InvalidOperationException($"创建开机自启计划任务失败 (Exit={exitCode}): {detail.Trim()}");
                }
            }
            finally
            {
                try { if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath); } catch { }
            }
        }
        else if (IsEnabled())
        {
            var (exitCode, stdout, stderr) = RunSchtasks(["/Delete", "/TN", TaskName, "/F"]);
            if (exitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"删除开机自启计划任务失败 (Exit={exitCode}): {detail.Trim()}");
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 schtasks.exe");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(5000);
        return (proc.ExitCode, stdout, stderr);
    }
}
