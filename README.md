<div align="center">

<img src="image.png" alt="Aether-RT Header" width="100%">

# 🌐 Aether-RT
### Remote Terminal Control System

<br />

[![C#](https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![.NET 10](https://img.shields.io/badge/.NET%2010-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![MQTT](https://img.shields.io/badge/MQTT-660066?style=for-the-badge&logo=mqtt&logoColor=white)](https://mqtt.org/)
[![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://microsoft.com/)

<br />

**A high-performance, stealthy, and decentralized remote administration / reverse shell suite built in C#.**

---

### 🏛️ Developed & Coded By
## **konpep**

---

### 📽️ Feature Showcase
<video src="demo.mp4" width="100%" controls autoplay loop>
  Your browser does not support the video tag. <a href="demo.mp4">Download Demo Video</a>
</video>

<br />

---

### ✨ Key Features

**📡 Decentralized Communication**
Uses public MQTT brokers (e.g., `broker.hivemq.com`) to orchestrate machines without requiring a centralized C2 server or port forwarding.

**🛡️ UAC Bypass & Privilege Escalation**
Built-in `fodhelper.exe` bypass to seamlessly elevate to **ROOT/Administrator** silently in the background. Controller UI intelligently shows ROOT status.

**👻 Deep Stealth**
Invisible background execution (No console windows).
Copies itself to AppData under a deceptive name (`WindowsUpdater.exe`).
Native `System` and `Hidden` file attributes applied automatically.
Silent PowerShell execution (`$ProgressPreference = 'SilentlyContinue'`).

**📊 Real-time System Analytics**
Visual live-updating UI (using WinForms overlaid on the CLI) for tracking CPU, RAM, Upload, and Download speeds.

**📸 Discord Remote Screenshots**
Instantly capture and exfiltrate screen captures directly to a Discord Webhook.

**🔄 Auto-Persistence**
Patches the Windows Run Registry for automatic startup upon reboot. Seamless update mechanism with System Mutex handling to prevent duplicate instances.

<br />

---

### 🚀 Getting Started

#### 1. Configuration
Before compiling, you **must** set your unique communication channel.
Open both `Controller/Program.cs` and `Executor/Program.cs` and change the Secret ID to a random string.

```csharp
// Change this in BOTH Controller and Executor
static string secretId = "YOUR_UNIQUE_SECRET_ID_HERE";
```

#### 2. Build the Controller
```bash
cd Controller
dotnet build -c Release
```

#### 3. Build the Executor
```bash
cd Executor
dotnet publish -c Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true
```

<br />

---

### 💻 Usage & Commands

| Command | Description |
| :--- | :--- |
| `mls` | Interactive visual menu to switch between targets. |
| `/stats` | Opens live GUI charts for CPU, RAM, and Network. |
| `/uacbypass` | Silently elevates privileges to ROOT/Administrator. |
| `/screenshot` | Captures screen and sends to Discord Webhook. |
| `/info` | Retrieves detailed `systeminfo`. |
| `/list` | Retrieves running processes (`tasklist`). |
| `/clear` | Clears the terminal screen. |

<br />

---

### ⚠️ Disclaimer
**Educational Purposes Only**
This software is provided strictly for educational purposes and unauthorized testing is prohibited. The author assumes no liability for any misuse or damage.

<br />

---

*Console Built with ♥ using C# and MQTTnet*

</div>
