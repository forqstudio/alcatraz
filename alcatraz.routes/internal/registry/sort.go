package registry

import "sort"

func sortEntries(entries []Entry) {
	sort.Slice(entries, func(i, j int) bool { return entries[i].ID < entries[j].ID })
}
