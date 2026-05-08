package registry

import "testing"

func TestSetAndDelete(t *testing.T) {
	r := New()

	if !r.Set("a", "10.0.0.1", 22) {
		t.Fatal("first Set should report change")
	}
	if r.Set("a", "10.0.0.1", 22) {
		t.Fatal("second Set with same value should be no-change")
	}
	if !r.Set("a", "10.0.0.2", 22) {
		t.Fatal("Set with different host should report change")
	}

	if !r.Delete("a") {
		t.Fatal("Delete existing should report change")
	}
	if r.Delete("a") {
		t.Fatal("Delete missing should be no-op")
	}
}

func TestSnapshotSorted(t *testing.T) {
	r := New()
	r.Set("c", "10.0.0.3", 22)
	r.Set("a", "10.0.0.1", 22)
	r.Set("b", "10.0.0.2", 22)

	snap := r.Snapshot()
	if len(snap) != 3 {
		t.Fatalf("expected 3 entries, got %d", len(snap))
	}
	want := []string{"a", "b", "c"}
	for i, e := range snap {
		if e.ID != want[i] {
			t.Errorf("snapshot[%d].ID = %q, want %q", i, e.ID, want[i])
		}
	}
}
