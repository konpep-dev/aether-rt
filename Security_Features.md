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

## 3. Stealth UAC Bypass (Privilege Escalation)
The `/uacbypass` method has been upgraded for maximum stealth and "FUD" (Fully Undetectable) status:

*   **Internal Execution:** The bypass logic has been moved from the Controller to the Executor. No suspicious PowerShell commands are sent over the network or logged in the system.
*   **ComputerDefaults Engine:** Instead of the commonly tracked `fodhelper.exe`, we utilize `computerdefaults.exe`, which is less targeted by modern scanners.
*   **Registry Obfuscation:** All sensitive registry keys (like `ms-settings\Shell\Open\command`) are XOR-encrypted in the source code.
*   **Direct Registry API:** Uses native .NET Registry APIs instead of shell commands, making the activity invisible to most behavior-based blockers.
*   **Rapid Cleanup:** Registry keys are automatically purged 3 seconds after execution, leaving the system clean.

---

## 4. Anti-AV & Evasion (Windows Defender Bypass)
These methods target runtime detection by modern antivirus solutions:

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
