# Upstream reference tree

Everything in this directory is a verbatim copy of
[**xberg-io/xberg**](https://github.com/xberg-io/xberg), the project this repository was
forked from. `X-Ray.Content` under `../dotnet/` is a native C# port of its extraction engine;
this tree is kept so that port can be re-derived against the Rust it came from.

**Nothing here is built, published, or edited.** It is not part of the `X-Ray.Content`
package. Keeping it byte-identical to upstream is what lets upstream commits be replayed onto
it — see [`.claude/skills/sync-upstream-reference`](../.claude/skills/sync-upstream-reference/SKILL.md).

## Sync state

| | |
|---|---|
| Upstream | `https://github.com/xberg-io/xberg` |
| Last replayed upstream commit | `5717407b` |
| How that was recorded | `dotnet/TODO.md`, "Upstream `xberg-io/xberg` was merged at `5717407b`" |

That commit predates this tree's move into `.reference/`: it was a plain `git merge` of
upstream, back when these files sat at the repository root. Every sync from here on is a
replay onto `.reference/`, and **this table is the record** — update it in the same commit as
the replay, or the next sync cannot tell where to start.

## Known local deltas

The replay depends on this tree matching upstream, so deviations are listed rather than left
to be discovered:

- **`.gitignore`** — upstream's file verbatim, but it lives here rather than at the repository
  root, where this fork keeps its own. Upstream patches touching `.gitignore` land here
  correctly; the fork's rules are never merged into it.
- Nothing else. If you must edit a file here, record it in this list.
