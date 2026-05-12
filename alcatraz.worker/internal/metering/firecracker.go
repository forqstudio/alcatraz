package metering

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"os"
)

// FcMetrics holds the subset of Firecracker metrics we read for billing.
// All counters are cumulative since VM boot.
type FcMetrics struct {
	NetRxBytes int64
	NetTxBytes int64
}

type fcRawMetrics struct {
	Net struct {
		RxBytesCount int64 `json:"rx_bytes_count"`
		TxBytesCount int64 `json:"tx_bytes_count"`
	} `json:"net"`
}

// ReadLastMetrics opens path and parses the last complete JSON object from it.
// Firecracker appends one JSON object per flush (default every 60s) when
// configured with --metrics-path. Returns (nil, nil) if the file is empty
// (e.g. before the first flush has happened).
func ReadLastMetrics(path string) (*FcMetrics, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, nil
		}
		return nil, fmt.Errorf("read %s: %w", path, err)
	}

	line, err := lastCompleteJSONLine(data)
	if err != nil {
		return nil, err
	}
	if line == nil {
		return nil, nil
	}

	var raw fcRawMetrics
	if err := json.Unmarshal(line, &raw); err != nil {
		return nil, fmt.Errorf("parse fc metrics: %w", err)
	}

	return &FcMetrics{
		NetRxBytes: raw.Net.RxBytesCount,
		NetTxBytes: raw.Net.TxBytesCount,
	}, nil
}

// lastCompleteJSONLine returns the bytes of the last newline-terminated
// segment in data. Firecracker writes each metrics flush as one JSON object
// followed by a newline. A trailing partial write (no closing newline) is
// skipped so we never parse a half-written record.
func lastCompleteJSONLine(data []byte) ([]byte, error) {
	if len(data) == 0 {
		return nil, nil
	}

	// Trim trailing newline if present so we work with the line before it.
	end := len(data)
	if data[end-1] == '\n' {
		end--
	} else {
		// Last record was not terminated — skip back to the prior newline.
		idx := bytes.LastIndexByte(data[:end], '\n')
		if idx < 0 {
			return nil, nil
		}
		end = idx
	}

	if end == 0 {
		return nil, nil
	}

	start := bytes.LastIndexByte(data[:end], '\n')
	if start < 0 {
		start = 0
	} else {
		start++
	}

	line := bytes.TrimSpace(data[start:end])
	if len(line) == 0 {
		return nil, nil
	}
	return line, nil
}

