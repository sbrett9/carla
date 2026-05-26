"""Bypass the carlanet.Client wrapper — use wheel-installed DLLs directly to
isolate whether the wrapper's WorldObserver background thread is the blocker."""
import sys, os
sys.path = [p for p in sys.path if p and p != os.path.dirname(os.path.abspath(__file__))]

import carlanet  # triggers DLL loads
print(f"carlanet loaded from: {carlanet.__file__}")

from CarlaNet.Transport import CarlaClient
from System import TimeSpan

def rpc(t): return t.GetAwaiter().GetResult()

c = CarlaClient("localhost", 2000, TimeSpan.FromSeconds(10))
print(f"Server version: {rpc(c.GetServerVersionAsync())}")
info = rpc(c.GetMapInfoAsync())
print(f"Map name: {info.Name}")
print(f"Spawn points: {info.RecommendedSpawnPoints.Count}")
rpc(c.DisposeAsync().AsTask())
print("=== direct test passed ===")
