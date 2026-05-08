.PHONY: build test clean

build: bin/alcatraz-routes

bin/alcatraz-routes:
	go build -o bin/alcatraz-routes ./cmd/alcatraz-routes

test:
	go test ./...

clean:
	rm -f bin/alcatraz-routes
