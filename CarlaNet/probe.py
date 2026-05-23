"""
Targeted probe: confirm msgid=0 works on every fresh connection and
decode the exact response format for UnpackResult implementation.
"""
import socket, msgpack, sys, time

HOST = sys.argv[1] if len(sys.argv) > 1 else 'localhost'
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2000

def call(method, *args, msgid=0, label=""):
    packed_args = [[False]] + list(args)
    request = msgpack.packb([0, msgid, method, packed_args], use_bin_type=True)
    tag = label or f"{method}(msgid={msgid})"
    print(f"\n[{tag}]")
    print(f"  TX: {request.hex()}  decoded={msgpack.unpackb(request, raw=False)}")

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(5.0)
    try:
        sock.connect((HOST, PORT))
        sock.sendall(request)
        try:
            data = sock.recv(65536)
            if data:
                print(f"  RX: {data.hex()}  decoded={msgpack.unpackb(data, raw=False)}")
            else:
                print("  RX: connection closed (0 bytes)")
        except socket.timeout:
            print("  RX: TIMEOUT")
    except Exception as e:
        print(f"  Error: {e}")
    finally:
        sock.close()

print(f"Target: {HOST}:{PORT}")

# Each call is a fresh TCP connection
call("version",            msgid=0, label="version  msgid=0 (fresh conn A)")
time.sleep(0.5)
call("version",            msgid=0, label="version  msgid=0 (fresh conn B)")
time.sleep(0.5)
call("version",            msgid=1, label="version  msgid=1 (fresh conn C)")
time.sleep(0.5)
call("get_episode_info",   msgid=0, label="get_episode_info msgid=0")
