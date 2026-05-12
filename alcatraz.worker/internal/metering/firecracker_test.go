package metering

import (
	"os"
	"path/filepath"
	"testing"
)

func writeFile(t *testing.T, content string) string {
	t.Helper()
	dir := t.TempDir()
	path := filepath.Join(dir, "metrics.fifo")
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("write fixture: %v", err)
	}
	return path
}

func TestReadLastMetrics_NoFile(t *testing.T) {
	m, err := ReadLastMetrics("/nonexistent/path")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m != nil {
		t.Fatalf("expected nil metrics, got %+v", m)
	}
}

func TestReadLastMetrics_EmptyFile(t *testing.T) {
	path := writeFile(t, "")
	m, err := ReadLastMetrics(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m != nil {
		t.Fatalf("expected nil metrics, got %+v", m)
	}
}

func TestReadLastMetrics_SingleObject(t *testing.T) {
	path := writeFile(t, `{"net":{"rx_bytes_count":100,"tx_bytes_count":200}}`+"\n")
	m, err := ReadLastMetrics(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m == nil {
		t.Fatalf("expected metrics, got nil")
	}
	if m.NetRxBytes != 100 || m.NetTxBytes != 200 {
		t.Fatalf("unexpected metrics: %+v", m)
	}
}

func TestReadLastMetrics_MultipleObjects_TakesLast(t *testing.T) {
	content := `{"net":{"rx_bytes_count":1,"tx_bytes_count":2}}` + "\n" +
		`{"net":{"rx_bytes_count":3,"tx_bytes_count":4}}` + "\n" +
		`{"net":{"rx_bytes_count":555,"tx_bytes_count":777}}` + "\n"
	path := writeFile(t, content)
	m, err := ReadLastMetrics(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m.NetRxBytes != 555 || m.NetTxBytes != 777 {
		t.Fatalf("unexpected metrics: %+v", m)
	}
}

func TestReadLastMetrics_TrailingPartialLine_FallsBackToLastComplete(t *testing.T) {
	content := `{"net":{"rx_bytes_count":1,"tx_bytes_count":2}}` + "\n" +
		`{"net":{"rx_bytes_count":9,"tx_bytes_count":` // mid-write, no newline
	path := writeFile(t, content)
	m, err := ReadLastMetrics(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m == nil {
		t.Fatalf("expected fallback metrics, got nil")
	}
	if m.NetRxBytes != 1 || m.NetTxBytes != 2 {
		t.Fatalf("unexpected metrics: %+v", m)
	}
}

func TestReadLastMetrics_OnlyPartialLine_ReturnsNil(t *testing.T) {
	path := writeFile(t, `{"net":{"rx_bytes_count":9,"tx_bytes_count":`)
	m, err := ReadLastMetrics(path)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if m != nil {
		t.Fatalf("expected nil, got %+v", m)
	}
}

func TestReadLastMetrics_MalformedJSON_ReturnsError(t *testing.T) {
	path := writeFile(t, `not-json` + "\n")
	_, err := ReadLastMetrics(path)
	if err == nil {
		t.Fatalf("expected error for malformed JSON")
	}
}
