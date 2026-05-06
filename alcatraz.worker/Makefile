.PHONY: build test clean test-cni

build: bin/alcatraz-worker bin/spawn-client

bin/alcatraz-worker:
	go build -o bin/alcatraz-worker ./cmd/alcatraz-worker

bin/spawn-client:
	go build -o bin/spawn-client ./cmd/spawn-client

test:
	go test ./...

clean:
	rm -f bin/alcatraz-worker bin/spawn-client

test-cni: build
	@echo "=== Running CNI Cleanup Test ==="
	@sudo rm -f /tmp/worker.log
	@sudo touch /tmp/worker.log && sudo chmod 666 /tmp/worker.log
	@sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
	@sudo pkill -9 -f firecracker 2>/dev/null || true
	@sleep 2
	@cd /home/dev/Workspace/alcatraz/alcatraz.worker && sudo ./bin/alcatraz-worker > /tmp/worker.log 2>&1 &
	@sleep 3
	@echo "Worker started. Spawning VM..."
	@cd /home/dev/Workspace/alcatraz/alcatraz.worker && ./bin/spawn-client --id test-make --vcpus 1 --mem 512
	@echo "Waiting for VM to start..."
	@sleep 10
	@echo "=== Resources after spawn ==="
	@sudo ls -la /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null || echo "Cannot access CNI state"
	@ip link show alcatraz0 2>&1 | head -1 || true
	@echo ""
	@echo "=== Stopping VM ==="
	@sudo pkill -TERM -f "firecracker.*test-make" 2>/dev/null || true
	@echo "Waiting for cleanup..."
	@sleep 10
	@echo "=== Resources after stop (should be cleaned up) ==="
	@sudo ls -la /var/lib/cni/networks/alcatraz-bridge/ 2>/dev/null || echo "Cannot access CNI state"
	@ip link show | grep fc-tap || echo "TAP devices cleaned up"
	@echo ""
	@echo "=== Worker log ==="
	@sudo tail -20 /tmp/worker.log 2>/dev/null || echo "No log"
	@echo "=== Test Complete ==="