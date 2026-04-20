# 🛡️ Aether-RT Security & Evasion Analysis

This report analyzes the **Anti-VM**, **Anti-Sandbox**, and **Windows Defender Neutralization (Anti-AV)** methods integrated into the Executor for maximum security and stealth operation.

---

## 1. Anti-Virtual Machine (VM) Detection
The Executor uses multiple detection layers to identify if it is running in a virtual environment:

*   **WMI ComputerSystem Check:** Queries the system for Manufacturer and Model. If they contain keywords like `VMware`, `VirtualBox`, `Virtual`, `QEMU`, `Hyper-V`, or `KVM`, the program terminates immediately.
*   **WMI BIOS Check:** Inspects the Serial Number and SMBIOS version for known VM signatures.
*   **MAC Address Filtering:** Checks the network interface's physical address against prefixes assigned to major VM vendors (VMware, VirtualBox, Hyper-V, Parallels).
*   **Driver Presence:** Scans for critical system files belonging exclusively to virtual machines (e.g., `VBoxGuest.sys`, `VBoxMouse.sys`, `vmmouse.sys`).
*   **Disk Size Guard:** Most VMs and analysis environments use small virtual disks. if the system drive is smaller than **60GB**, the Executor considers the environment unsafe.

---

## 2. Anti-Sandbox & Analysis Detection
To evade analysis by automated sandboxes and security researchers:

*   **20-Second Startup Stall:** Implements a proactive 20-second delay upon execution. This bypasses automated analysis systems that only monitor a file for a few seconds before labeling it "safe."
*   **Windows Sandbox Detection:** Checks the operating username. Windows Sandbox always uses the name `WDAGUtilityAccount`.
*   **Hardware Resource Check:** sandboxes often limit resources. If the system has **less than 2GB of RAM** or **less than 2 CPU cores**, the Executor kills itself.
*   **Recent Activity Check:** A real user system always has recent file activity. If the "Recent Files" folder is near empty (less than 5 shortcuts), it is flagged as a fresh sandbox.
*   **Process Blacklist:** Scans the active processes for known analysis tools:
    *   `Wireshark`, `Fiddler` (Network Analysis)
    *   `Procmon`, `Procexp` (System Monitoring)
    *   `x64dbg`, `x32dbg`, `OllyDbg`, `dnSpy` (Debuggers/Decompilers)
    *   `Process Hacker`, `Ida` (Memory/Static Analysis)
    *   `Sandboxie`, `VBoxService` (Environment Helpers)

---

## 3. Stealth UAC Bypass 2.0 (SilentCleanup)
The `/uacbypass` method has been upgraded to version 2.0 to evade advanced behavioral detections (like `UACBypassExp`) recently implemented in modern security stacks:

*   **Zero Registry Classes:** We no longer touch highly monitored keys like `ms-settings` or `fodhelper`.
*   **Environment Hijacking:** Version 2.0 exploits the Windows "SilentCleanup" scheduled task. It performs a hijack of the `%windir%` environment variable in the User hive (`HKCU\Environment`).
*   **Task Orchestration:** The Executor creates a temporary mock Windows directory, places a copy of itself named `cleanmgr.exe` inside, and triggers the task via `schtasks`.
*   **Privileged Execution:** Because the SilentCleanup task is configured to run with `Highest Privileges`, it executes our payload with administrative rights automatically, bypassing any UAC prompts.
*   **Total Cleanup:** Immediately after execution, the `%windir%` variable is restored to its original value, and all temporary files are purged.

---

## 4. Anti-AV & Evasion (Windows Defender Bypass)
These methods target runtime detection by modern antivirus solutions:

*   **UnDefend v2 (Database Locking):** Uses `ntdll.dll!NtOpenFile` to open primary Windows Defender database files (`mpasbase.vdm`, `mpasdlta.vdm`) with `0` share access. This prevents `MsMpEng.exe` from reading signatures, rendering the engine blind.
*   **Token Hijacking (GetSystem):** Achieves `SYSTEM` privileges by enabling `SeDebugPrivilege` and stealing a process token from `winlogon.exe`, avoiding noisy service creation.
*   **Binary Self-Locking:** The program opens its own executable file for reading without `FILE_SHARE_DELETE`, creating a kernel-level lock that prevents AV from moving or deleting the active binary.
*   **XOR String Obfuscation:** All sensitive identifiers (URLs, Token IDs, Registry paths, File names) are stored as XOR-encrypted byte arrays and only decrypted in memory during runtime.
*   **AMSI Runtime Patching (Memory):** Patches the `AmsiScanBuffer` function in `amsi.dll` in-memory. This "blinds" Windows Defender, preventing it from scanning scripts executed within the process.
*   **ETW Disabling:** Event Tracing for Windows (ETW) sends telemetry regarding system calls. By patching `EtwEventWrite` in `ntdll.dll`, we stop the flow of metadata to Windows Security logs.
*   **Test Mode Detection:** Uses `bcdedit` to check if Windows is in "Test Signing Mode," a common state for malware analysts running unsigned drivers.
*   **Junk Code Injection:** Large sections of harmless, randomized code are injected to alter the binary's hash, entropy, and signature, breaking automated pattern matching.

---

## 5. Failure Behavior
Unlike standard programs that throw errors, Aether-RT follows a **"Silent Exit"** strategy:

1.  If a VM/Sandbox is detected, `Main` returns immediately.
2.  No windows are created.
3.  No connection to the MQTT Broker is initiated.
4.  No network footprint is left (Network Zero Trace).

---
*Coded by konpep*
