#!/bin/bash
set -e

echo "=== CNI Cleanup Test ==="

# Clean up environment
echo "Cleaning up environment..."
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/172.16.0.* 2>/dev/null || true
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/last_reserved_ip.0 2>/dev/null || true
sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
sudo pkill -9 -f firecracker 2>/dev/null || true
sleep 2

echo ""
echo "=== Step 1: Verify Clean State ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
ip link show alcatraz0 2>&1 || echo "No bridge (expected)"

echo ""
echo "=== Step 2: Start Worker (run this in another terminal) ==="
echo "  cd /home/dev/Workspace/alcatraz/alcatraz.worker"
echo "  sudo ./bin/alcatraz-worker"
echo ""
read -p "Press Enter after starting worker..."

echo ""
echo "=== Step 3: Spawn VM ==="
cd /home/dev/Workspace/alcatraz/alcatraz.worker
./bin/spawn-client --id test-cni-cleanup --vcpus 1 --mem 512

echo "Waiting for VM to start..."
sleep 8

echo ""
echo "=== Step 4: Check Resources After Spawn ==="
echo "CNI state (should have IP file like 172.16.0.x):"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
echo ""
echo "Bridge (should be UP):"
ip link show alcatraz0
echo ""
echo "TAP devices (should exist, e.g., fc-tap0):"
ip link show | grep fc-tap || echo "No TAP devices found"

echo ""
echo "=== Step 5: Stop VM (sends SIGTERM via StopVMM) ==="
FIRECRACKER_PID=$(ps aux | grep "firecracker.*test-cni-cleanup" | grep -v grep | awk '{print $2}')
if [ -n "$FIRECRACKER_PID" ]; then
    echo "Stopping Firecracker (PID: $FIRECRACKER_PID)..."
    sudo kill -TERM $FIRECRACKER_PID
else
    echo "Firecracker process not found"
fi

echo "Waiting for cleanup (5 seconds)..."
sleep 5

echo ""
echo "=== Step 6: Verify Cleanup ==="
echo "CNI state (should NOT have IP files like 172.16.0.x):"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/

echo ""
echo "TAP devices (should be cleaned up):"
ip link show | grep fc-tap || echo "TAP devices cleaned up (GOOD)"

echo ""
echo "Network namespaces (should be cleaned up):"
sudo ls -la /var/run/netns/ | grep test-cni-cleanup || echo "Netns cleaned up (GOOD)"

echo ""
echo "Bridge state (should persist but may be DOWN):"
ip link show alcatraz0

echo ""
echo "=== Worker Log (last 30 lines) ==="
tail -30 /tmp/worker.log 2>/dev/null || echo "No worker log at /tmp/worker.log"

echo ""
echo "=== Test Complete ==="
echo ""
echo "Expected results:"
echo "  - No IP files (172.16.0.x) in /var/lib/cni/networks/alcatraz-bridge/"
echo "  - No TAP devices (fc-tapX)"
echo "  - No network namespace for the VM"
echo "  - Bridge alcatraz0 still exists but may be DOWN"
