#!/bin/bash
# Automated CNI cleanup test - runs everything in one terminal
# Requires sudo privileges

set -e

echo "=== CNI Cleanup Test (Automated) ==="

# Clean up environment
echo "Step 0: Cleaning up environment..."
sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
sudo pkill -9 -f firecracker 2>/dev/null || true
sleep 2
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/172.16.0.* 2>/dev/null || true
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/last_reserved_ip.0 2>/dev/null || true

echo ""
echo "=== Step 1: Verify Clean State ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
ip link show alcatraz0 2>&1 || echo "No bridge (expected)"

echo ""
echo "=== Step 2: Start Worker in Background ==="
cd /home/dev/Workspace/alcatraz/alcatraz.worker
sudo rm -f /tmp/worker.log
sudo ./bin/alcatraz-worker > /tmp/worker.log 2>&1 &
WORKER_PID=$!
echo "Worker started (PID: $WORKER_PID)"
sleep 3
sudo tail -5 /tmp/worker.log

echo ""
echo "=== Step 3: Spawn VM ==="
./bin/spawn-client --id test-auto-cleanup --vcpus 1 --mem 512

echo "Waiting for VM to start..."
sleep 10

echo ""
echo "=== Step 4: Check Resources After Spawn ==="
echo "CNI state (should have IP file like 172.16.0.x):"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
echo ""
echo "Bridge (should be UP with CARRIER):"
ip link show alcatraz0
echo ""
echo "TAP devices (should exist, e.g., fc-tap0):"
ip link show | grep fc-tap || echo "No TAP devices found - ERROR"

echo ""
echo "=== Step 5: Stop VM (sends SIGTERM via StopVMM) ==="
FIRECRACKER_PID=$(ps aux | grep "firecracker.*test-auto-cleanup" | grep -v grep | awk '{print $2}')
if [ -n "$FIRECRACKER_PID" ]; then
    echo "Stopping Firecracker (PID: $FIRECRACKER_PID)..."
    sudo kill -TERM $FIRECRACKER_PID
else
    echo "Firecracker process not found - trying alternative..."
    sudo pkill -TERM -f "firecracker.*test-auto-cleanup" || true
fi

echo "Waiting for cleanup (8 seconds)..."
sleep 8

echo ""
echo "=== Step 6: Verify Cleanup ==="
echo "CNI state (should NOT have IP files like 172.16.0.x):"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
echo ""
echo "TAP devices (should be cleaned up):"
ip link show | grep fc-tap || echo "TAP devices cleaned up (GOOD)"
echo ""
echo "Network namespaces (should be cleaned up):"
sudo ls -la /var/run/netns/ 2>/dev/null | grep test-auto-cleanup || echo "Netns cleaned up (GOOD)"
echo ""
echo "Bridge state (should persist but may be DOWN):"
ip link show alcatraz0

echo ""
echo "=== Worker Log (last 30 lines) ==="
tail -30 /tmp/worker.log 2>/dev/null || echo "No worker log"

echo ""
echo "=== Test Complete ==="

# Stop worker
sudo pkill -9 -f "alcatraz-worker.*test-auto" 2>/dev/null || true

# Determine pass/fail
CNI_STATE=$(sudo ls /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null | grep -E "^172\." || true)
if [ -z "$CNI_STATE" ]; then
    echo ""
    echo "RESULT: PASS - CNI state cleaned up successfully!"
else
    echo ""
    echo "RESULT: FAIL - CNI state not cleaned up. Remaining files:"
    echo "$CNI_STATE"
fi
