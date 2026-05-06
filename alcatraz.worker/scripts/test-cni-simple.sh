#!/bin/bash
# Simple CNI cleanup test - focuses on testing cleanup after VM exit

set -e

echo "=== Simple CNI Cleanup Test ==="

# Step 0: Clean up
echo "Cleaning environment..."
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/172.16.0.* 2>/dev/null || true
sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
sudo pkill -9 -f firecracker 2>/dev/null || true
sleep 2

# Step 1: Check clean state
echo ""
echo "=== Step 1: Clean State ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null || echo "CNI dir not accessible"

# Step 2: Start worker
echo ""
echo "=== Step 2: Starting Worker ==="
cd /home/dev/Workspace/alcatraz/alcatraz.worker
sudo ./bin/alcatraz-worker > /tmp/worker.log 2>&1 &
sleep 3
echo "Worker started. Log (last 5 lines):"
sudo tail -5 /tmp/worker.log 2>/dev/null || echo "No log yet"

# Step 3: Spawn VM
echo ""
echo "=== Step 3: Spawning VM ==="
./bin/spawn-client --id test-simple --vcpus 1 --mem 512
echo "Waiting 10s for VM to start..."
sleep 10

# Step 4: Check resources
echo ""
echo "=== Step 4: Resources After Spawn ==="
echo "CNI state:"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null
echo ""
echo "Bridge:"
ip link show alcatraz0 2>&1 | head -1 || echo "No bridge"
echo ""
echo "TAP devices:"
ip link show | grep fc-tap || echo "No TAP devices"

# Step 5: Stop VM
echo ""
echo "=== Step 5: Stopping VM ==="
FIRECRACKER_PID=$(ps aux | grep "firecracker.*test-simple" | grep -v grep | awk '{print $2}')
if [ -n "$FIRECRACKER_PID" ]; then
    echo "Sending SIGTERM to Firecracker (PID: $FIRECRACKER_PID)..."
    sudo kill -TERM $FIRECRACKER_PID
else
    echo "Firecracker not found, trying pkill..."
    sudo pkill -TERM -f "firecracker.*test-simple" || true
fi

echo "Waiting 10s for cleanup..."
sleep 10

# Step 6: Verify cleanup
echo ""
echo "=== Step 6: Verify Cleanup ==="
echo "CNI state (should NOT have IP files):"
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null
echo ""
echo "TAP devices (should be cleaned up):"
ip link show | grep fc-tap || echo "TAP cleaned up ✓"
echo ""
echo "Network namespaces:"
sudo ls -la /var/run/netns/ 2>/dev/null | grep test-simple || echo "Netns cleaned up ✓"

# Check worker log
echo ""
echo "=== Worker Log (last 20 lines) ==="
sudo tail -20 /tmp/worker.log 2>/dev/null || echo "No log"

# Result
echo ""
echo "=== Result ==="
CNI_FILES=$(sudo ls /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null | grep -E "^172\." || true)
if [ -z "$CNI_FILES" ]; then
    echo "✓ PASS: CNI state cleaned up successfully!"
else
    echo "✗ FAIL: CNI state NOT cleaned up. Remaining:"
    echo "$CNI_FILES"
fi

# Cleanup
sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
